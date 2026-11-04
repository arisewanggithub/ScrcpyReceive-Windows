using ScrcpyReceive.Core;

namespace ScrcpyReceive.Media;

public sealed class AudioPlayback : IAsyncDisposable
{
    private readonly string codec;
    private readonly object gate = new();
    private WasapiOutput? output;
    private AudioDecoder? decoder;
    private AudioRegulator? regulator;
    private AudioPresentation? presentation;
    private CancellationTokenSource? outputCancellation;
    private AutoResetEvent? dataReady;
    private Task? outputTask;
    private bool configuring;
    private long? firstInputAt;
    private long lastPts, receivedFrames;

    public (long ContentFrames, long? PtsUs) Position
    {
        get
        {
            lock (gate)
                return configuring || output is null || presentation is null ? (0, null) : presentation.Current(output.PlayedFrames());
        }
    }
    public long PlayedFrames => Position.ContentFrames;
    public int TargetFrames { get { lock (gate) return regulator?.Buffer.Target ?? AudioBuffer.TargetFrames; } }
    public (long Silence, long Dropped) Recovery
    {
        get { lock (gate) { var snapshot = regulator?.Buffer.Snapshot; return (snapshot?.Silence ?? 0, snapshot?.Dropped ?? 0); } }
    }
    public string Diagnostics
    {
        get { lock (gate) return $"audio lastPts={lastPts}, decodedFrames={receivedFrames}; {regulator?.Diagnostics}; {output?.Diagnostics}"; }
    }

    public AudioPlayback(string codec)
    {
        this.codec = codec;
        AudioDecoder.ValidateCodec(codec);
        using (WasapiOutput.AudioThread()) { }
        output = new WasapiOutput();
    }

    public async ValueTask ConfigureAsync(Packet packet, CancellationToken token)
    {
        var format = AudioFormat.Parse(packet.Object(), codec);
        lock (gate) configuring = true;
        try
        {
            if (decoder is not null) await RetireAsync(true, token);
            AudioDecoder? nextDecoder = null;
            AudioRegulator? nextRegulator = null;
            WasapiOutput? nextOutput = null;
            try
            {
                nextDecoder = new AudioDecoder(format);
                nextRegulator = new AudioRegulator();
                nextOutput = output ?? new WasapiOutput();
                var stop = new CancellationTokenSource();
                var signal = new AutoResetEvent(false);
                var position = new AudioPresentation();
                var ownedOutput = nextOutput;
                var ownedRegulator = nextRegulator;
                lock (gate)
                {
                    decoder = nextDecoder;
                    regulator = nextRegulator;
                    output = nextOutput;
                    presentation = position;
                    outputCancellation = stop;
                    dataReady = signal;
                    firstInputAt = null;
                    outputTask = Task.Factory.StartNew(() => RunOutput(ownedOutput, ownedRegulator.Buffer, position, signal, stop.Token),
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                nextDecoder = null;
                nextRegulator = null;
                nextOutput = null;
            }
            finally
            {
                nextDecoder?.Dispose();
                nextRegulator?.Dispose();
                if (nextOutput is not null && nextOutput != output) nextOutput.Dispose();
            }
        }
        finally { lock (gate) configuring = false; }
    }

    public ValueTask PresentAsync(Packet packet, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            try
            {
                CheckWorker();
                var currentDecoder = decoder ?? throw new InvalidOperationException("Audio is not configured.");
                var currentRegulator = regulator ?? throw new InvalidOperationException("Audio regulation is not initialized.");
                firstInputAt ??= Environment.TickCount64;
                var decoded = currentDecoder.Decode(packet);
                lastPts = packet.PtsUs;
                receivedFrames += decoded.Data.Length / 4;
                currentRegulator.Push(decoded);
                dataReady!.Set();
                return ValueTask.CompletedTask;
            }
            catch (ProtocolException) { throw; }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new ProtocolException($"Audio playback failed: {error.Message}", "AUDIO_FAILED");
            }
        }
    }

    private static void RunOutput(WasapiOutput output, AudioBuffer buffer, AudioPresentation position,
        AutoResetEvent dataReady, CancellationToken token)
    {
        try
        {
            using var scheduling = WasapiOutput.AudioThread();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                _ = position.Current(output.PlayedFrames());
                if (!output.Started && buffer.Frames < buffer.Target && !buffer.Draining)
                {
                    if (WaitHandle.WaitAny([dataReady, token.WaitHandle], 250) == 1) token.ThrowIfCancellationRequested();
                    continue;
                }
                var padding = output.BufferedFrames;
                if (buffer.Draining && buffer.Frames == 0 && padding == 0) return;
                var available = checked((int)(output.Capacity - padding));
                if (available > 0)
                {
                    var block = buffer.Pull(available);
                    if (block.Frames > 0)
                    {
                        var start = output.Written;
                        var written = output.Write(block.Data, 0, block.Frames);
                        if (written != block.Frames) throw new IOException("WASAPI did not accept the reserved output block.");
                        position.Submit(start, block);
                        if (!output.Started) { buffer.Start(); output.Start(); }
                    }
                }
                output.WaitForBuffer(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void CheckWorker()
    {
        if (outputTask is { IsFaulted: true } failed)
            throw new ProtocolException($"Audio output worker failed: {failed.Exception!.GetBaseException().Message}", "AUDIO_FAILED");
    }

    public void CheckHealth()
    {
        lock (gate)
        {
            if (configuring) return;
            try
            {
                CheckWorker();
                output?.CheckHealth();
                if (firstInputAt is { } start && Environment.TickCount64 - start > 3000 && PlayedFrames == 0)
                    throw new ProtocolException("Real source audio did not start playing within three seconds.", "AUDIO_FAILED");
            }
            catch (ProtocolException) { throw; }
            catch (Exception error) { throw new ProtocolException($"Audio output failed: {error.Message}", "AUDIO_FAILED"); }
        }
    }

    private async Task RetireAsync(bool drain, CancellationToken token)
    {
        WasapiOutput? ownedOutput;
        AudioDecoder? ownedDecoder;
        AudioRegulator? ownedRegulator;
        CancellationTokenSource? stop;
        AutoResetEvent? signal;
        Task? worker;
        lock (gate)
        {
            ownedOutput = output; output = null;
            ownedDecoder = decoder; decoder = null;
            ownedRegulator = regulator; regulator = null;
            stop = outputCancellation; outputCancellation = null;
            signal = dataReady; dataReady = null;
            worker = outputTask; outputTask = null;
            presentation = null;
            firstInputAt = null;
        }
        try
        {
            if (drain) { ownedOutput?.BeginDrain(); ownedRegulator?.BeginDrain(); signal?.Set(); }
            else stop?.Cancel();
            if (worker is not null) await worker.WaitAsync(TimeSpan.FromMilliseconds(500), token);
        }
        finally
        {
            stop?.Cancel();
            signal?.Set();
            try { if (worker is not null) await worker; }
            finally
            {
                try { ownedOutput?.Dispose(); }
                finally
                {
                    ownedDecoder?.Dispose();
                    ownedRegulator?.Dispose();
                    signal?.Dispose();
                    stop?.Dispose();
                }
            }
        }
    }

    public async ValueTask DisposeAsync() => await RetireAsync(false, CancellationToken.None);
}
