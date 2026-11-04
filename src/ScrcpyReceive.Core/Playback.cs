namespace ScrcpyReceive.Core;

public interface IPlaybackFactory
{
    string Description { get; }
    bool SupportsSrt => false;
    ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken cancellationToken);
}

public interface IPlayback : IAsyncDisposable
{
    void CheckHealth() { }
    string Diagnostics => "";
    IDisposable? EnterTrack(uint track) => null;
    void Observe(Packet packet) { }
    // Completion means the real resources exist, not merely that configuration was queued.
    ValueTask ConfigureAsync(Packet configuration, CancellationToken cancellationToken);
    ValueTask PresentAsync(Packet media, CancellationToken cancellationToken);
}

public interface IVideoPlaybackProgress
{
    bool HasPresentedVideo { get; }
    // Override when current-generation presentation resets on reconfiguration.
    bool HasEverPresentedVideo => HasPresentedVideo;
}

public interface IAudioPlaybackProgress
{
    long PlayedAudioFrames { get; }
}

public interface ISrtPlayback : IPlayback, IVideoPlaybackProgress, IAudioPlaybackProgress
{
    Task ReceiveSrtAsync(Func<Packet, CancellationToken, ValueTask> emit, CancellationToken cancellationToken);
}

public sealed class UnavailablePlaybackFactory : IPlaybackFactory
{
    public string Description => "Playback unavailable in this foundation build. Hardware-only decoding, GPU presentation, audio and SRT are not yet connected.";

    public ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromException<IPlayback>(new ProtocolException(
            $"Requested {request.VideoCodec}/{request.AudioCodec ?? "audio off"} over {(request.Srt is null ? "TCP" : "SRT/fMP4")}. " +
            Description + " No capture was accepted.", "UNSUPPORTED_CONFIG"));
}
