using System.Net;
using ScrcpyReceive.Core;
using ScrcpyReceive.Media;
using ScrcpySend.Media;
using Sender = ScrcpySend.Core;
using static ScrcpyReceive.SrtInterop.SyntheticVideo;

namespace ScrcpyReceive.SrtInterop;

// Fast transport/demux regression only; deliberately separate from actual native UI acceptance tests.
internal static class DemuxRegression
{
    internal static async Task<int> RunAsync(string directory)
    {
        try
        {
            foreach (var (codec, fixture, partial) in new[]
            {
                ("h264", "synthetic-h264-generations.json", false),
                ("h265", "synthetic-hevc.json", false),
                ("av1", "synthetic-av1.json", false),
                ("h264", "synthetic-h264-generations.json", true),
                ("h265", "synthetic-hevc.json", true)
            })
            {
                using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using var video = new SyntheticVideo(Path.Combine(directory, fixture), codec, true, partial);
                await using var audio = new SyntheticSilence(video.OriginUs);
                await using var sender = new SrtMedia();
                var descriptor = sender.Prepare(IPAddress.Loopback, IPAddress.Loopback, 1);
                var request = new CastRequest(codec, "aac", 30, "auto",
                    new(descriptor.Url, descriptor.LatencyMs, descriptor.StreamId));
                await using var input = new SrtInput(request);
                var state = new TrackState(request);
                var dimensions = new List<(int Width, int Height)>();
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var videoFrames = new int[3];
                var audioPackets = 0;
                var sent = sender.RunAsync(video, audio,
                    new Sender.CaptureSettings(EnableAudio: true, VideoCodec: codec, MaxFps: 30, MediaTransport: "srt"), lifetime.Token);
                var received = input.RunAsync((packet, _) =>
                {
                    packet = packet with { Session = 1 };
                    if (packet.Type == PacketType.StreamConfig)
                    {
                        state.Configure(packet);
                        state.MarkReady(packet.Track);
                        if (packet.Track == 1)
                        {
                            var json = packet.Object();
                            dimensions.Add((JsonObject.Number(json, "width"), JsonObject.Number(json, "height")));
                            Require(dimensions.Count <= 3, "Duplicate or stale configuration produced an extra generation.");
                        }
                    }
                    else
                    {
                        state.Media(packet);
                        if (packet.Track == 1)
                        {
                            Require(packet.Generation <= 3, "Unexpected video generation.");
                            videoFrames[packet.Generation - 1]++;
                        }
                        else audioPackets++;
                        if (videoFrames[2] >= 8) completed.TrySetResult();
                    }
                    return ValueTask.CompletedTask;
                }, lifetime.Token);
                try
                {
                    var first = await Task.WhenAny(completed.Task, sent, received).WaitAsync(lifetime.Token);
                    await first;
                    Require(first == completed.Task, "SRT loopback ended before rotation completed.");
                    Require(dimensions.SequenceEqual(new[]
                    {
                        (video.Width, video.Height), (video.Height, video.Width), (video.Width, video.Height)
                    }) && videoFrames.All(count => count >= 8) && audioPackets >= 50,
                        "Rotation dimensions, continuous pictures, or independent AAC packets were lost.");
                    Console.WriteLine($"DEMUX ONLY PASS: {codec}, partial={partial}, generations=3, " +
                        $"video={string.Join(',', videoFrames)}, AAC={audioPackets}. Native UI is tested separately.");
                }
                finally
                {
                    lifetime.Cancel();
                    sender.Abort();
                    try { await sent; } catch (OperationCanceledException) { }
                    try { await received; } catch (OperationCanceledException) { }
                }
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");
            return 1;
        }
    }
}
