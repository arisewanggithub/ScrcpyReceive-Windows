namespace ScrcpyReceive.Core;

public sealed class ReceiveDiagnostics(Func<long>? nowMs = null)
{
    private sealed class Track
    {
        public long Count, Bytes, Pts, MaximumGapMs, MaximumReadMs;
        public long? ArrivedAt;
    }

    private readonly object gate = new();
    private readonly Func<long> now = nowMs ?? (() => Environment.TickCount64);
    private readonly Track[] tracks = [new(), new()];
    private long maximumHandlingMs;

    public void Received(Packet packet, long readMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(readMs);
        if (!Wire.IsMedia(packet.Type)) return;
        JsonObject.Require(packet.Track is 1 or 2, "Received media requires a valid track.");
        lock (gate)
        {
            var at = now();
            var track = tracks[packet.Track - 1];
            if (track.ArrivedAt is { } previous)
                track.MaximumGapMs = Math.Max(track.MaximumGapMs, at - previous);
            track.ArrivedAt = at;
            track.Count++;
            track.Bytes = checked(track.Bytes + packet.Payload.Length);
            track.Pts = packet.PtsUs;
            track.MaximumReadMs = Math.Max(track.MaximumReadMs, readMs);
        }
    }

    public void Handled(long elapsedMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMs);
        lock (gate) maximumHandlingMs = Math.Max(maximumHandlingMs, elapsedMs);
    }

    public string Diagnostics
    {
        get
        {
            lock (gate)
            {
                var at = now();
                return $"receive handlingMaxMs={maximumHandlingMs}; " +
                    string.Join("; ", tracks.Select((track, index) =>
                        $"track={index + 1} received={track.Count} bytes={track.Bytes} lastPts={track.Pts} " +
                        $"ageMs={(track.ArrivedAt is { } arrived ? at - arrived : -1)} " +
                        $"gapMaxMs={track.MaximumGapMs} readMaxMs={track.MaximumReadMs}"));
            }
        }
    }
}
