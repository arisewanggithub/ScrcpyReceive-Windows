using System.Text.Json;
using System.Diagnostics;
using ScrcpyReceive.Core;

namespace ScrcpyReceive.Media;

public sealed class VideoPlayback : IPlayback, IVideoPlaybackProgress
{
    private readonly GpuPresenter presenter;
    private readonly HardwareDecoder decoder;
    private readonly Func<ValueTask> detach;
    private readonly Func<string, ValueTask> status;
    private readonly AudioPlayback? audio;
    private readonly string codecName;
    private int rendered;
    private readonly object nativeGate = new();
    private HardwareFrame? lastFrame;
    private HardwareFrame? pendingFrame;
    private readonly AutoResetEvent renderWake = new(false);
    private readonly CancellationTokenSource renderCancellation = new();
    private readonly Task renderTask;
    private readonly PlaybackAwake awake = new(() => new PlaybackPowerRequest());
    private int resizeRequested;
    private Exception? renderFailure;
    private Exception? resizeFailure;
    private bool disposed;
    private long maximumAvSkewUs;
    private int avMeasurements;
    private long maximumSteadySkewUs;
    private long? firstVideoPresentationPts;
    private long receivedVideoPackets, decodedVideoPackets, decodedFrames, skippedFrames;
    private long maximumDecodeUs, maximumGateWaitUs, totalDecodeUs;
    public long DecodedFrames => Interlocked.Read(ref decodedFrames);
    public long SkippedFrames => Interlocked.Read(ref skippedFrames);
    public long LastPresentedPtsUs => Interlocked.Read(ref lastVideoPts);
    public string DecoderDiagnostics => decoder.Diagnostics;
    public int HardwareSurfaceCount => decoder.HardwareSurfaceCount;
    public int MinimumHardwareSurfaces => decoder.MinimumHardwareSurfaces;
    public (long Silence, long Dropped) AudioRecovery => audio?.Recovery ?? (0, 0);
    public int AudioTargetFrames => audio?.TargetFrames ?? 0;
    public bool KeepsDisplayAwake => awake.IsActive;
    public bool HasPresentedVideo => Volatile.Read(ref rendered) != 0;
    public bool HasEverPresentedVideo => presenter.PresentedFrames > 0;
    public long MaximumAvSkewUs => Interlocked.Read(ref maximumAvSkewUs);
    public int AvMeasurements => Volatile.Read(ref avMeasurements);
    public long MaximumSteadySkewUs => Interlocked.Read(ref maximumSteadySkewUs);
    public void Observe(Packet packet)
    {
        if (packet.Track == 1) Interlocked.Increment(ref receivedVideoPackets);
    }
    private long lastVideoPts;
    public string Diagnostics => $"video received={Interlocked.Read(ref receivedVideoPackets)}, decoded={DecodedFrames}, rendered={presenter.PresentedFrames}, skipped={SkippedFrames}, lastPts={Interlocked.Read(ref lastVideoPts)}, maxSkewUs={MaximumAvSkewUs}, decodeMaxUs={Interlocked.Read(ref maximumDecodeUs)}, decodeAvgUs={Interlocked.Read(ref totalDecodeUs) / Math.Max(1, Interlocked.Read(ref decodedVideoPackets))}, gateWaitMaxUs={Interlocked.Read(ref maximumGateWaitUs)}; {decoder.Diagnostics}; {presenter.Diagnostics}; {audio?.Diagnostics}";

    public long PlayedAudioFrames => audio?.PlayedFrames ?? 0;
    public IDisposable? EnterTrack(uint track) => track == 2 && audio is not null ? WasapiOutput.AudioThread() : null;
    public VideoPlayback(GpuPresenter presenter, HardwareDecoder decoder, Func<ValueTask> detach, Func<string, ValueTask> status,
        AudioPlayback? audio, string codecName = "h264")
    {
        this.presenter = presenter;
        this.decoder = decoder;
        this.detach = detach;
        this.status = status;
        this.audio = audio;
        this.codecName = codecName;
        renderTask = Task.Factory.StartNew(RenderLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public static void Validate(CastRequest request)
    {
        if (request.VideoCodec is not ("h264" or "h265" or "av1") ||
            request.ReceiverOrientation != "auto" || request.FramesPerSecond > 60)
            throw new ProtocolException("Supports custom TCP 8-bit H.264, HEVC Main and AV1 Profile 0, PCM/AAC-LC/Opus audio, auto presentation, at most 60 FPS. No substitution.", "UNSUPPORTED_CONFIG");
    }

    public void CheckHealth()
    {
        CheckVideoHealth();
        audio?.CheckHealth();
    }
    private void CheckVideoHealth()
    {
        if (Volatile.Read(ref resizeFailure) is { } failure)
            throw new IOException($"GPU resize/presentation failed: {failure.Message}", failure);
        if (Volatile.Read(ref renderFailure) is { } renderError)
            throw new IOException($"GPU renderer failed: {renderError.Message}", renderError);
    }

    public void Resize(int width, int height, float scaleX, float scaleY)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
        try
        {
            if (presenter.SetViewport(width, height, scaleX, scaleY))
            {
                Interlocked.Exchange(ref resizeRequested, 1);
                renderWake.Set();
            }
        }
        catch (Exception error) { Interlocked.CompareExchange(ref resizeFailure, error, null); }
    }

    public async ValueTask ConfigureAsync(Packet configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.Track == 2)
        {
            if (audio is null) throw new ProtocolException("Audio was not requested.");
            await audio.ConfigureAsync(configuration, cancellationToken);
            await status("Audio decoder and WASAPI output initialized.");
            return;
        }
        var json = configuration.Object();
        var buffers = JsonObject.Member(json, "csd", JsonValueKind.Array).EnumerateArray()
            .Select(item => Convert.FromBase64String(item.GetString()!)).ToArray();
        CheckVideoHealth();
        lock (nativeGate)
        {
            Volatile.Write(ref rendered, 0);
            if (pendingFrame is not null) { pendingFrame.Dispose(); pendingFrame = null; Interlocked.Increment(ref skippedFrames); }
            lastFrame?.Dispose(); lastFrame = null;
            Interlocked.Exchange(ref resizeRequested, 0);
            decoder.Configure(CodecConfiguration.VideoData(codecName, buffers),
                JsonObject.Number(json, "width"), JsonObject.Number(json, "height"));
            presenter.Prepare(JsonObject.Number(json, "width"), JsonObject.Number(json, "height"));
        }
        await status($"Hardware {codecName} decoder configured; waiting for a real random-access picture.");
    }

    public async ValueTask PresentAsync(Packet media, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (media.Track == 2)
        {
            if (audio is null) throw new ProtocolException("Audio was not requested.");
            await audio.PresentAsync(media, cancellationToken);
            return;
        }
        CheckVideoHealth();
        IReadOnlyList<HardwareFrame> frames;
        var waitingAt = Stopwatch.GetTimestamp();
        lock (nativeGate)
        {
            Interlocked.Exchange(ref maximumGateWaitUs, Math.Max(Interlocked.Read(ref maximumGateWaitUs), (long)Stopwatch.GetElapsedTime(waitingAt).TotalMicroseconds));
            var begin = Stopwatch.GetTimestamp();
            frames = decoder.Decode(media);
            var elapsed = (long)Stopwatch.GetElapsedTime(begin).TotalMicroseconds;
            Interlocked.Exchange(ref maximumDecodeUs, Math.Max(Interlocked.Read(ref maximumDecodeUs), elapsed));
            Interlocked.Add(ref totalDecodeUs, elapsed);
        }
        Interlocked.Increment(ref decodedVideoPackets);
        Interlocked.Add(ref decodedFrames, frames.Count);
        try
        {
            foreach (var frame in frames)
            {
                if (frame.PtsUs < 0) throw new ProtocolException("Hardware decoder lost the source timestamp.");
                cancellationToken.ThrowIfCancellationRequested();
                lock (nativeGate)
                {
                    CheckVideoHealth();
                    if (pendingFrame is not null) { pendingFrame.Dispose(); Interlocked.Increment(ref skippedFrames); }
                    pendingFrame = frame.Clone();
                    renderWake.Set();
                }
            }
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private void RenderLoop()
    {
        var token = renderCancellation.Token;
        try
        {
            while (true)
            {
                var delay = Volatile.Read(ref pendingFrame) is not null || Volatile.Read(ref resizeRequested) != 0 ? 5 : 250;
                if (WaitHandle.WaitAny([renderWake, token.WaitHandle], delay) == 1) token.ThrowIfCancellationRequested();
                token.ThrowIfCancellationRequested();
                if (!Monitor.TryEnter(nativeGate)) continue;
                var first = false;
                try
                {
                    if (pendingFrame is { } frame)
                    {
                        if (!presenter.Present(frame)) continue;
                        awake.SetPlaying(true);
                        pendingFrame = null;
                        lastFrame?.Dispose();
                        lastFrame = frame;
                        Interlocked.Exchange(ref resizeRequested, 0);
                        Interlocked.Exchange(ref lastVideoPts, frame.PtsUs);
                        if (audio?.Position is { ContentFrames: > 0, PtsUs: { } audioPts })
                        {
                            var skew = Math.Abs(frame.PtsUs - audioPts);
                            maximumAvSkewUs = Math.Max(maximumAvSkewUs, skew);
                            firstVideoPresentationPts ??= frame.PtsUs;
                            if (frame.PtsUs - firstVideoPresentationPts.Value >= 1_000_000)
                                maximumSteadySkewUs = Math.Max(maximumSteadySkewUs, skew);
                            Interlocked.Increment(ref avMeasurements);
                        }
                        first = Interlocked.Exchange(ref rendered, 1) == 0;
                    }
                    else if (Volatile.Read(ref resizeRequested) != 0 && lastFrame is not null && presenter.Redraw(lastFrame))
                        Interlocked.Exchange(ref resizeRequested, 0);
                }
                finally { Monitor.Exit(nativeGate); }
                if (first) status("Playing: first hardware-decoded D3D11 surface presented" +
                    (audio is null ? " (video only)." : " with low-latency audio recovery.")).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Interlocked.CompareExchange(ref renderFailure, error, null); throw; }
        finally { awake.SetPlaying(false); }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            renderCancellation.Cancel();
            try { await renderTask; }
            finally { if (audio is not null) await audio.DisposeAsync(); }
        }
        finally
        {
            try { await detach(); }
            finally
            {
                lock (nativeGate)
                {
                    disposed = true;
                    pendingFrame?.Dispose();
                    pendingFrame = null;
                    lastFrame?.Dispose();
                    lastFrame = null;
                    try { decoder.Dispose(); }
                    finally
                    {
                        try { presenter.Dispose(); }
                        finally
                        {
                            try { awake.Dispose(); }
                            finally { renderWake.Dispose(); renderCancellation.Dispose(); }
                        }
                    }
                }
            }
        }
    }
}
