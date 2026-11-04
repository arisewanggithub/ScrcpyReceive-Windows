using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScrcpyReceive.Core;
using ScrcpyReceive.Media;

namespace ScrcpyReceive.App;

internal sealed class VideoHost : IPlaybackFactory, IDisposable
{
    private readonly SwapChainPanel panel;
    private readonly DispatcherQueue dispatcher;
    private readonly Action<string> status;
    private GpuPresenter? active;
    private VideoPlayback? playback;
    private XamlRoot? root;
    public long PresentedFrames => active?.PresentedFrames ?? 0;
    public long RedrawnFrames => active?.RedrawnFrames ?? 0;
    public long PlayedAudioFrames => playback?.PlayedAudioFrames ?? 0;
    public long MaximumAvSkewUs => playback?.MaximumAvSkewUs ?? 0;
    public int AvMeasurements => playback?.AvMeasurements ?? 0;
    public long MaximumSteadySkewUs => playback?.MaximumSteadySkewUs ?? 0;
    public long DecodedFrames => playback?.DecodedFrames ?? 0;
    public int ConfiguredVideoGenerations => playback?.ConfiguredVideoGenerations ?? 0;
    public long SkippedFrames => playback?.SkippedFrames ?? 0;
    public long LastPresentedPtsUs => playback?.LastPresentedPtsUs ?? 0;
    public string DecoderDiagnostics => playback?.DecoderDiagnostics ?? "";
    public int HardwareSurfaceCount => playback?.HardwareSurfaceCount ?? 0;
    public int MinimumHardwareSurfaces => playback?.MinimumHardwareSurfaces ?? 0;
    public (long Silence, long Dropped) AudioRecovery => playback?.AudioRecovery ?? (0, 0);
    public int AudioTargetFrames => playback?.AudioTargetFrames ?? 0;
    public bool KeepsDisplayAwake => playback?.KeepsDisplayAwake ?? false;
    public bool IsAttached => active is not null;
    public bool IsPlaying => PresentedFrames > 0;
    public bool SupportsSrt => true;

    public string Description => "Hardware-only 8-bit H.264, HEVC Main and AV1 Profile 0. Custom TCP supports PCM/AAC-LC/Opus audio; explicit SRT/fMP4 supports AAC-LC/Opus. Immediate video and adaptive 50-150 ms audio buffering. Auto orientation, up to 60 FPS; brief audio gaps remain possible.";

    public VideoHost(SwapChainPanel panel, Action<string> status)
    {
        this.panel = panel;
        this.status = status;
        dispatcher = panel.DispatcherQueue;
        panel.SizeChanged += SizeChanged;
        panel.Loaded += Loaded;
        panel.CompositionScaleChanged += CompositionScaleChanged;
    }

    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (root is not null) root.Changed -= RootChanged;
        root = panel.XamlRoot;
        root.Changed += RootChanged;
        Resize();
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Resize();
    private void SizeChanged(object sender, SizeChangedEventArgs args) => Resize();
    private void CompositionScaleChanged(SwapChainPanel sender, object args) => Resize();
    private void Resize()
    {
        var width = (int)Math.Ceiling(panel.ActualWidth * panel.CompositionScaleX);
        var height = (int)Math.Ceiling(panel.ActualHeight * panel.CompositionScaleY);
        if (playback is not null) playback.Resize(width, height, panel.CompositionScaleX, panel.CompositionScaleY);
        else active?.SetViewport(width, height, panel.CompositionScaleX, panel.CompositionScaleY);
    }

    public async ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken cancellationToken)
    {
        VideoPlayback.Validate(request);
        GpuPresenter? presenter = null;
        HardwareDecoder? decoder = null;
        AudioPlayback? audio = null;
        SrtInput? srtInput = null;
        VideoPlayback? preparedPlayback = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Srt is not null) srtInput = new SrtInput(request);
            presenter = new GpuPresenter();
            decoder = new HardwareDecoder(presenter.Device, request.VideoCodec);
            if (request.AudioCodec is not null) audio = new AudioPlayback(request.AudioCodec);
            var ownedPresenter = presenter;
            await OnUiAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (active is not null) throw new InvalidOperationException("A video surface is already attached.");
                Attach(panel, ownedPresenter.SwapChain.NativePointer);
                active = ownedPresenter;
                Resize();
            });
            cancellationToken.ThrowIfCancellationRequested();
            var ownedPlayback = new VideoPlayback(presenter, decoder, () => new ValueTask(OnUiAsync(() =>
            {
                if (active != ownedPresenter) throw new InvalidOperationException("Video surface ownership changed.");
                try { Attach(panel, 0); }
                finally
                {
                    playback = null;
                    active = null;
                    status("Playback ended.");
                }
            })), detail => new ValueTask(OnUiAsync(() =>
            {
                if (active == ownedPresenter) status(detail);
            })), audio, request.VideoCodec);
            preparedPlayback = ownedPlayback;
            await OnUiAsync(() =>
            {
                playback = ownedPlayback;
                Resize();
            });
            ownedPlayback.CheckHealth();
            return srtInput is null ? ownedPlayback : new SrtPlayback(ownedPlayback, srtInput);
        }
        catch (Exception error)
        {
            try
            {
                try { if (srtInput is not null) await srtInput.DisposeAsync(); }
                finally
                {
                    if (preparedPlayback is not null)
                    {
                        presenter = null; decoder = null; audio = null;
                        await preparedPlayback.DisposeAsync();
                    }
                    else if (presenter is not null)
                    {
                        var failedPresenter = presenter;
                        await OnUiAsync(() =>
                        {
                            if (active != failedPresenter) return;
                            Attach(panel, 0);
                            playback = null;
                            active = null;
                        });
                    }
                }
            }
            catch (Exception cleanupError) { throw new AggregateException("Hardware preflight and detach failed.", error, cleanupError); }
            finally
            {
                try { if (audio is not null) await audio.DisposeAsync(); }
                finally { decoder?.Dispose(); presenter?.Dispose(); }
            }
            if (error is OperationCanceledException) throw;
            throw new ProtocolException($"Playback preflight failed: {error.Message}", "UNSUPPORTED_CONFIG");
        }
    }

    private Task OnUiAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        })) completion.SetException(new InvalidOperationException("Video UI dispatcher is unavailable."));
        return completion.Task;
    }

    private static unsafe void Attach(SwapChainPanel panel, nint swapChain)
    {
        var inspectable = WinRT.MarshalInspectable<object>.FromManaged(panel);
        nint native = 0;
        try
        {
            var id = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in id, out native));
            var set = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)native)[3];
            Marshal.ThrowExceptionForHR(set(native, swapChain));
        }
        finally
        {
            if (native != 0) Marshal.Release(native);
            Marshal.Release(inspectable);
        }
    }

    public void Dispose()
    {
        panel.SizeChanged -= SizeChanged;
        panel.Loaded -= Loaded;
        panel.CompositionScaleChanged -= CompositionScaleChanged;
        if (root is not null) root.Changed -= RootChanged;
    }
}
