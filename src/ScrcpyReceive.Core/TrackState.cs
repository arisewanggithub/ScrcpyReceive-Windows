using System.Text.Json;

namespace ScrcpyReceive.Core;

public sealed class TrackState(CastRequest request)
{
    private readonly Dictionary<uint, uint> generations = [];
    private readonly Dictionary<uint, long> timestamps = [];
    private readonly HashSet<uint> ready = [];
    private bool initialReady;
    private readonly object gate = new();

    public void Configure(Packet packet)
    {
        lock (gate) ConfigureCore(packet);
    }

    private void ConfigureCore(Packet packet)
    {
        JsonObject.Require(request.Tracks.Contains(packet.Track), "Track was not requested.");
        var expected = generations.GetValueOrDefault(packet.Track) + 1;
        JsonObject.Require(packet.Generation == expected, "Nonsequential generation.");
        var json = packet.Object();
        JsonObject.Require(JsonObject.Text(json, "codec") ==
            (packet.Track == 1 ? request.VideoCodec : request.AudioCodec), "Codec substitution is forbidden.", "UNSUPPORTED_CONFIG");
        if (packet.Track == 1)
        {
            var buffers = CodecConfiguration.Buffers(json);
            JsonObject.Require(JsonObject.Number(json, "width") is >= 1 and <= 16384 &&
                JsonObject.Number(json, "height") is >= 1 and <= 16384, "Invalid video dimensions.");
            JsonObject.Require(buffers.Length > 0 && buffers.All(b => b.Length > 0), "Missing video CSD.");
            if (request.VideoCodec != "av1")
                JsonObject.Require(buffers.All(b => b.AsSpan().StartsWith(new byte[] { 0, 0, 1 }) ||
                    b.AsSpan().StartsWith(new byte[] { 0, 0, 0, 1 })), "Annex B CSD required.");
        }
        else
        {
            _ = AudioFormat.Parse(json, request.AudioCodec!);
        }
        generations[packet.Track] = packet.Generation;
        timestamps.Remove(packet.Track);
        ready.Remove(packet.Track);
    }

    public void MarkReady(uint track)
    {
        lock (gate)
        {
        JsonObject.Require(generations.ContainsKey(track), "Cannot ready an unconfigured track.");
        ready.Add(track);
        initialReady |= request.Tracks.All(ready.Contains);
        }
    }

    public void Media(Packet packet)
    {
        lock (gate) MediaCore(packet);
    }

    private void MediaCore(Packet packet)
    {
        JsonObject.Require(initialReady && ready.Contains(packet.Track), "Media arrived before all initial tracks were ready.");
        JsonObject.Require(generations.GetValueOrDefault(packet.Track) == packet.Generation, "Stale media generation.");
        var opus = packet.Track == 2 && request.AudioCodec == "opus";
        JsonObject.Require(packet.PtsUs >= 0 && (opus || !timestamps.TryGetValue(packet.Track, out var previous) ||
            packet.PtsUs >= previous), "Media PTS moved backwards.");
        if (packet.Track == 2 && request.AudioCodec == "raw")
            JsonObject.Require(packet.Payload.Length % 4 == 0, "PCM packet is not sample aligned.");
        timestamps[packet.Track] = packet.PtsUs;
    }
}
