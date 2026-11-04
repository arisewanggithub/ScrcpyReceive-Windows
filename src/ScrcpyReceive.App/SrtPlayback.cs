using ScrcpyReceive.Core;
using ScrcpyReceive.Media;

namespace ScrcpyReceive.App;

internal sealed class SrtPlayback(VideoPlayback playback, SrtInput input) : ISrtPlayback
{
    public bool HasPresentedVideo => playback.HasPresentedVideo;
    public bool HasEverPresentedVideo => playback.HasEverPresentedVideo;
    public long PlayedAudioFrames => playback.PlayedAudioFrames;
    public string Diagnostics => playback.Diagnostics;
    public IDisposable? EnterTrack(uint track) => playback.EnterTrack(track);
    public void Observe(Packet packet) => playback.Observe(packet);
    public void CheckHealth() => playback.CheckHealth();
    public ValueTask ConfigureAsync(Packet packet, CancellationToken token) => playback.ConfigureAsync(packet, token);
    public ValueTask PresentAsync(Packet packet, CancellationToken token) => playback.PresentAsync(packet, token);
    public Task ReceiveSrtAsync(Func<Packet, CancellationToken, ValueTask> emit, CancellationToken token) =>
        input.RunAsync(emit, token);
    public async ValueTask DisposeAsync()
    {
        try { await input.DisposeAsync(); }
        finally { await playback.DisposeAsync(); }
    }
}
