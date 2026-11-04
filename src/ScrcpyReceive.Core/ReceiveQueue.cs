namespace ScrcpyReceive.Core;

public sealed class ReceiveQueue : IDisposable
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public const int MaximumEntries = 512;
    public const long MaximumSpanUs = 500_000;
    private sealed record Entry(Packet Packet, long AdmittedAt);
    private readonly object gate = new();
    private readonly Queue<Entry>[] packets = [new(), new()];
    private readonly SemaphoreSlim[] signals = [new(0), new(0)];
    private readonly Packet?[] inFlight = new Packet?[2];
    private int mediaBytes;
    private int controlBytes;
    private int controls;
    private readonly int maximumMediaBytes;
    private TaskCompletionSource space = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long[] waitTotalUs = new long[2], waitMaximumUs = new long[2], mediaTaken = new long[2];
    public string Diagnostics
    {
        get
        {
            lock (gate)
                return $"queue videoWaitAvgUs={waitTotalUs[0] / Math.Max(1, mediaTaken[0])}, videoWaitMaxUs={waitMaximumUs[0]}, " +
                    $"audioWaitAvgUs={waitTotalUs[1] / Math.Max(1, mediaTaken[1])}, audioWaitMaxUs={waitMaximumUs[1]}, " +
                    $"mediaBytes={mediaBytes}/{maximumMediaBytes}, videoQueued={packets[0].Count}, audioQueued={packets[1].Count}, " +
                    $"videoInFlight={inFlight[0]?.Type.ToString() ?? "none"}, audioInFlight={inFlight[1]?.Type.ToString() ?? "none"}";
        }
    }

    public ReceiveQueue(int maximumMediaBytes = MaximumBytes)
    {
        if (maximumMediaBytes is <= 0 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumMediaBytes));
        this.maximumMediaBytes = maximumMediaBytes;
    }

    public void Add(Packet packet)
    {
        lock (gate)
        {
            var media = Wire.IsMedia(packet.Type);
            JsonObject.Require(packet.Track is 1 or 2, "Queued packets require an enabled track.");
            JsonObject.Require(packets.Sum(items => items.Count) + inFlight.Count(item => item is not null) < MaximumEntries,
                "Receiver queue entry limit reached.", "CONGESTION");
            if (media)
            {
                JsonObject.Require(packet.Payload.Length <= maximumMediaBytes - mediaBytes,
                    $"Receiver media exceeds its {maximumMediaBytes}-byte queue budget.", "CONGESTION");
                var minimum = packet.PtsUs;
                var maximum = packet.PtsUs;
                foreach (var queued in packets.SelectMany(items => items).Select(entry => entry.Packet))
                    if (Wire.IsMedia(queued.Type) && queued.Track == packet.Track)
                    {
                        minimum = Math.Min(minimum, queued.PtsUs);
                        maximum = Math.Max(maximum, queued.PtsUs);
                    }
                JsonObject.Require(maximum - minimum <= MaximumSpanUs,
                    $"Receiver track {packet.Track} queue exceeds 500 ms: min={minimum}, max={maximum}, bytes={mediaBytes}, entries={packets.Sum(items => items.Count)}.", "CONGESTION");
                mediaBytes += packet.Payload.Length;
            }
            else
            {
                JsonObject.Require(controls < 64 && packet.Payload.Length <= 256 * 1024 - controlBytes,
                    "Receiver configuration queue limit reached.", "CONGESTION");
                controls++;
                controlBytes += packet.Payload.Length;
            }
            packets[packet.Track - 1].Enqueue(new(packet, System.Diagnostics.Stopwatch.GetTimestamp()));
            signals[packet.Track - 1].Release();
        }
    }

    public async ValueTask AddAsync(Packet packet, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Wire.IsMedia(packet.Type) && packet.Payload.Length > maximumMediaBytes) { Add(packet); return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
        var until = Environment.TickCount64 + 500;
        ProtocolException? lastCongestion = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (lastCongestion is not null && Environment.TickCount64 >= until) throw lastCongestion;
            Task changed;
            ProtocolException congestion;
            lock (gate)
            {
                changed = space.Task;
                try { Add(packet); return; }
                catch (ProtocolException error) when (error.Code == "CONGESTION") { congestion = error; lastCongestion = error; }
            }
            try { await changed.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw congestion; }
        }
    }

    private void SpaceChanged()
    {
        var previous = space;
        space = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    public async ValueTask<Packet> TakeAsync(uint track, CancellationToken cancellationToken)
    {
        if (track is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(track));
        await signals[track - 1].WaitAsync(cancellationToken);
        lock (gate)
        {
            if (inFlight[track - 1] is not null) throw new InvalidOperationException("Each track has one serial consumer.");
            var entry = packets[track - 1].Dequeue();
            var packet = entry.Packet;
            if (Wire.IsMedia(packet.Type))
            {
                var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(entry.AdmittedAt).TotalMicroseconds;
                mediaTaken[track - 1]++;
                waitTotalUs[track - 1] += elapsed;
                waitMaximumUs[track - 1] = Math.Max(waitMaximumUs[track - 1], elapsed);
            }
            inFlight[track - 1] = packet;
            SpaceChanged();
            return packet;
        }
    }

    public void Complete(Packet packet)
    {
        lock (gate)
        {
            if (packet.Track is not (1 or 2) || !ReferenceEquals(inFlight[packet.Track - 1], packet))
                throw new InvalidOperationException("Completing an unowned track packet.");
            inFlight[packet.Track - 1] = null;
            if (Wire.IsMedia(packet.Type)) mediaBytes -= packet.Payload.Length;
            else { controls--; controlBytes -= packet.Payload.Length; }
            SpaceChanged();
        }
    }

    public void Dispose()
    {
        foreach (var signal in signals) signal.Dispose();
        foreach (var queue in packets) queue.Clear();
        Array.Clear(inFlight);
    }
}
