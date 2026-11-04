namespace ScrcpyReceive.Core;

public enum ReceiverOperation { Read, Admission, Video, Audio, Write }

public sealed class ReceiverActivity(Func<long>? nowMs = null)
{
    private sealed class Operation
    {
        internal bool Active;
        internal long StartedAt, ProgressAt, MaximumMs, Completed;
        internal string Detail = "idle", MaximumDetail = "none";
    }

    private readonly object gate = new();
    private readonly Func<long> now = nowMs ?? (() => Environment.TickCount64);
    private readonly Operation[] operations = Enumerable.Range(0, 5).Select(_ => new Operation()).ToArray();
    private readonly Queue<string> recentSlowOperations = new();
    private readonly Queue<string> transportProbes = new();

    public long ReadNoProgressMs
    {
        get { lock (gate) return operations[(int)ReceiverOperation.Read].Active
            ? now() - operations[(int)ReceiverOperation.Read].ProgressAt : 0; }
    }

    public void RecordTransport(string snapshot)
    {
        lock (gate)
        {
            transportProbes.Enqueue($"atMs={now()} readNoProgressMs={ReadNoProgressMs} {snapshot}");
            while (transportProbes.Count > 8) transportProbes.Dequeue();
        }
    }

    public void Begin(ReceiverOperation kind, string detail)
    {
        lock (gate)
        {
            var operation = operations[(int)kind];
            if (operation.Active) throw new InvalidOperationException($"Receiver {kind} operation is already active.");
            operation.Active = true;
            operation.StartedAt = operation.ProgressAt = now();
            operation.Detail = detail;
        }
    }

    public void Progress(ReceiverOperation kind, string detail)
    {
        lock (gate)
        {
            var operation = operations[(int)kind];
            if (!operation.Active) throw new InvalidOperationException($"Receiver {kind} operation is not active.");
            operation.ProgressAt = now();
            operation.Detail = detail;
        }
    }

    public void Complete(ReceiverOperation kind)
    {
        lock (gate)
        {
            var operation = operations[(int)kind];
            if (!operation.Active) throw new InvalidOperationException($"Receiver {kind} operation is not active.");
            var elapsed = now() - operation.StartedAt;
            if (elapsed > operation.MaximumMs)
            {
                operation.MaximumMs = elapsed;
                operation.MaximumDetail = operation.Detail;
            }
            if (elapsed >= 100)
            {
                recentSlowOperations.Enqueue($"{kind} {elapsed}ms {operation.Detail}");
                while (recentSlowOperations.Count > 8) recentSlowOperations.Dequeue();
            }
            operation.Active = false;
            operation.Completed++;
        }
    }

    public string Diagnostics
    {
        get
        {
            lock (gate)
            {
                var at = now();
                return "activity " + string.Join("; ", operations.Select((operation, index) =>
                    $"{(ReceiverOperation)index}={(operation.Active ? operation.Detail : "idle")} " +
                    $"ageMs={(operation.Active ? at - operation.StartedAt : 0)} " +
                    $"noProgressMs={(operation.Active ? at - operation.ProgressAt : 0)} " +
                    $"completed={operation.Completed} maxMs={operation.MaximumMs} maxDetail={operation.MaximumDetail}")) +
                    "; recentSlow=[" + string.Join(" | ", recentSlowOperations) + "]" +
                    "; transportStalls=[" + string.Join(" | ", transportProbes) + "]";
            }
        }
    }
}
