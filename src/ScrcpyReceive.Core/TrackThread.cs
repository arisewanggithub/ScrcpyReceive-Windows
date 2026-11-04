using System.Collections.Concurrent;

namespace ScrcpyReceive.Core;

public static class TrackThread
{
    public static Task Run(Func<Task> action) => Task.Factory.StartNew(() =>
    {
        using var context = new SerialContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = action();
            while (!task.IsCompleted) context.Step();
            task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(null); }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private sealed class SerialContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => callbacks.Add(() => callback(state));
        public void Step()
        {
            if (callbacks.TryTake(out var action, 20)) action();
        }
        public void Dispose() => callbacks.Dispose();
    }
}
