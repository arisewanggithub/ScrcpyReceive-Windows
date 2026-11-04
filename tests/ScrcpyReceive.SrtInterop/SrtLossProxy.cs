using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ScrcpyReceive.SrtInterop;

// Loopback-only, bounded loss injection: retain later data while one sequence awaits retransmission.
internal sealed class SrtLossProxy : IAsyncDisposable
{
    private readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource lifetime = new();
    private readonly IPEndPoint sender;
    private readonly Task running;
    private readonly bool permanent;
    private IPEndPoint? receiver;
    private int armed;
    private uint? sequence;
    private long blockedAt;
    private long recoveredAt;
    public long LossStartedAt => Interlocked.Read(ref blockedAt);
    public long LossRecoveredAt => Interlocked.Read(ref recoveredAt);
    public int DroppedPackets { get; private set; }
    public string Url => $"srt://127.0.0.1:{((IPEndPoint)socket.Client.LocalEndPoint!).Port}";

    public SrtLossProxy(string upstream, bool permanent = false)
    {
        this.permanent = permanent;
        sender = IPEndPoint.Parse(upstream["srt://".Length..]);
        if (!IPAddress.IsLoopback(sender.Address)) throw new ArgumentException("Only loopback SRT is allowed.");
        running = RunAsync();
    }

    public void Arm() => Volatile.Write(ref armed, 1);

    private async Task RunAsync()
    {
        var token = lifetime.Token;
        try
        {
            while (true)
            {
                var packet = await socket.ReceiveAsync(token);
                var fromSender = packet.RemoteEndPoint.Equals(sender);
                if (!fromSender)
                {
                    if (receiver is null && IPAddress.IsLoopback(packet.RemoteEndPoint.Address))
                        receiver = packet.RemoteEndPoint;
                    if (!packet.RemoteEndPoint.Equals(receiver)) throw new IOException("Unexpected SRT proxy peer.");
                }
                var destination = fromSender ? receiver ?? throw new IOException("SRT reply preceded caller.") : sender;
                var releasesGap = false;
                if (fromSender && Volatile.Read(ref armed) != 0 && packet.Buffer.Length >= 16)
                {
                    var number = BinaryPrimitives.ReadUInt32BigEndian(packet.Buffer);
                    if ((number & 0x80000000u) == 0)
                    {
                        if (sequence is null) { sequence = number; Interlocked.Exchange(ref blockedAt, Stopwatch.GetTimestamp()); }
                        if (sequence == number && (permanent || Stopwatch.GetElapsedTime(blockedAt) < TimeSpan.FromMilliseconds(400)))
                        {
                            DroppedPackets++;
                            continue;
                        }
                        releasesGap = sequence == number;
                    }
                }
                await socket.SendAsync(packet.Buffer, destination, token);
                if (releasesGap) Interlocked.CompareExchange(ref recoveredAt, Stopwatch.GetTimestamp(), 0);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try { await running; }
        finally { socket.Dispose(); lifetime.Dispose(); }
    }
}
