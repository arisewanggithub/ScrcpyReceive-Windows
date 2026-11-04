using System.Buffers.Binary;
using System.Net;
using ScrcpyReceive.Core;
using ScrcpyReceive.Media;
using Sender = ScrcpySend.Core;

namespace ScrcpyReceive.SrtInterop;

internal static class OpusDemuxRegression
{
    internal static async Task<int> RunAsync(string directory)
    {
        foreach (var (mono, gain, mismatch) in new[] { (false, false, false), (true, true, false), (false, false, true) })
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var video = new SyntheticVideo(Path.Combine(directory, "synthetic-hevc.json"), "h265");
            await using var sender = new SyntheticOpusSender(Path.Combine(directory, "synthetic-av-opus.json"), mono, gain);
            var descriptor = sender.Prepare(IPAddress.Loopback, IPAddress.Loopback, 1);
            var request = new CastRequest("h265", mismatch ? "aac" : "opus", 30, "auto",
                new(descriptor.Url, descriptor.LatencyMs, descriptor.StreamId));
            await using var input = new SrtInput(request);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            AudioDecoder? decoder = null, louder = null;
            var packets = 0;
            var samples = 0;
            double power = 0, amplified = 0;
            var sent = sender.RunAsync(video, null, new Sender.CaptureSettings(VideoCodec: "h265", EnableAudio: true, MediaTransport: "srt"), lifetime.Token);
            var received = input.RunAsync((packet, _) =>
            {
                if (packet.Track != 2 || completed.Task.IsCompleted) return ValueTask.CompletedTask;
                if (packet.Type == PacketType.StreamConfig)
                {
                    var format = AudioFormat.Parse(packet.Object(), "opus");
                    SyntheticVideo.Require(format.PreSkip == 312 && format.Channels == (mono ? 1 : 2) &&
                        format.GainQ8 == (gain ? 1536 : 0), "MP4 dOps did not preserve Opus pre-skip/channels/gain.");
                    decoder = new(format);
                    var head = format.Configuration.ToArray();
                    BinaryPrimitives.WriteInt16LittleEndian(head.AsSpan(16), checked((short)(format.GainQ8 + 1536)));
                    louder = new(format with { Configuration = head, GainQ8 = format.GainQ8 + 1536 });
                }
                else
                {
                    var normal = decoder!.Decode(packet);
                    var high = louder!.Decode(packet);
                    if (packets == 0)
                        SyntheticVideo.Require(normal.PtsUs == packet.PtsUs + 6500, "MP4 Opus pre-skip timestamp was applied incorrectly.");
                    samples += normal.Data.Length / 4;
                    for (var index = 0; index < normal.Data.Length; index += 2)
                    {
                        var a = BinaryPrimitives.ReadInt16LittleEndian(normal.Data.AsSpan(index));
                        var b = BinaryPrimitives.ReadInt16LittleEndian(high.Data.AsSpan(index));
                        power += (double)a * a;
                        amplified += (double)b * b;
                    }
                    if (++packets == 6)
                    {
                        var ratio = Math.Sqrt(amplified / power);
                        SyntheticVideo.Require(samples == 6 * 960 - 312 && double.IsFinite(ratio) && ratio is > 1.9 and < 2.1,
                            "MP4 Opus did not apply pre-skip and gain exactly once.");
                        completed.TrySetResult();
                    }
                }
                return ValueTask.CompletedTask;
            }, lifetime.Token);
            try
            {
                var first = await Task.WhenAny(completed.Task, received, sent).WaitAsync(lifetime.Token);
                if (mismatch)
                {
                    try { await first; throw new InvalidOperationException("MP4 Opus was accepted as AAC."); }
                    catch (ProtocolException error) when (error.Code == "UNSUPPORTED_CONFIG") { }
                    Console.WriteLine("OPUS DEMUX PASS: requested AAC rejects actual Opus; no substitution.");
                }
                else
                {
                    await first;
                    SyntheticVideo.Require(first == completed.Task, "Opus stream ended before native decoding verification.");
                    Console.WriteLine($"OPUS DEMUX PASS: channels={(mono ? 1 : 2)}, gain={(gain ? 1536 : 0)}, " +
                        $"decodedFrames={samples}, preSkip=312, gain applied once.");
                }
            }
            finally
            {
                lifetime.Cancel();
                sender.Abort();
                try { await sent; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                try { await received; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (ProtocolException) when (mismatch) { }
                decoder?.Dispose();
                louder?.Dispose();
            }
        }
        return 0;
    }
}
