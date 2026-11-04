using ScrcpyReceive.Core;

namespace ScrcpyReceive.Media;

/// <summary>One explicitly selected, control-peer-pinned live SRT/fMP4 input.</summary>
public sealed class SrtInput : IAsyncDisposable
{
    private readonly CastRequest request;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private Task? running;
    private Task? disposing;

    public SrtInput(CastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        JsonObject.Require(request.Srt is not null && request.VideoCodec is "h264" or "h265" or "av1" &&
            request.AudioCodec is null or "aac" or "opus", "SRT requires explicit supported video and optional AAC-LC/Opus.", "UNSUPPORTED_CONFIG");
        SrtTransport.Validate(request.Srt!);
        SrtDemuxer.Preflight(request.VideoCodec, request.AudioCodec);
        SrtTransport.Preflight();
        this.request = request;
    }

    /// <summary>Configuration callbacks must complete only after output/decoder readiness.</summary>
    public Task RunAsync(Func<Packet, CancellationToken, ValueTask> emit, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(emit);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposing is not null, this);
            if (running is not null) throw new InvalidOperationException("SRT input can run only once.");
            running = Task.Run(() =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                linked.Token.ThrowIfCancellationRequested();
                using var transport = new SrtTransport(request.Srt!);
                transport.Connect(linked.Token);
                using var demuxer = new SrtDemuxer(request, transport, emit, linked.Token);
                demuxer.Run();
            }, CancellationToken.None);
            return running;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposing is not null) return new(disposing);
            lifetime.Cancel();
            disposing = FinishDisposalAsync(running);
            return new(disposing);
        }
    }

    private async Task FinishDisposalAsync(Task? task)
    {
        try
        {
            if (task is not null)
            {
                try { await task.ConfigureAwait(false); }
                catch { /* RunAsync owns the playback failure; disposal still releases everything. */ }
            }
        }
        finally { lifetime.Dispose(); }
    }
}
