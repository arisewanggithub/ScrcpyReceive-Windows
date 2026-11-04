namespace ScrcpyReceive.Core;

public sealed class PlaybackAwake(Func<IDisposable> acquire) : IDisposable
{
    private readonly object gate = new();
    private IDisposable? request;
    private bool disposed;
    public bool IsActive { get { lock (gate) return request is not null; } }

    public void SetPlaying(bool playing)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (playing) request ??= acquire();
            else
            {
                var owned = request;
                request = null;
                owned?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            try { SetPlaying(false); }
            finally { disposed = true; }
        }
    }
}
