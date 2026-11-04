using System.Net;
using System.Net.Sockets;

namespace ScrcpyReceive.Core;

public sealed class DiscoveryBroadcaster(Action<SocketException> onFailure) : IDisposable
{
    private readonly Dictionary<IPAddress, UdpClient> sockets = [];
    private bool disposed;

    public async ValueTask PublishAsync(IReadOnlyList<ListenerAddress> addresses, byte[] payload, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (payload.Length > 1024) throw new ArgumentException("Discovery payload exceeds 1024 bytes.", nameof(payload));
        var current = addresses.Select(item => item.Address).ToHashSet();
        foreach (var address in sockets.Keys.Where(address => !current.Contains(address)).ToArray())
        {
            sockets[address].Dispose();
            sockets.Remove(address);
        }
        foreach (var address in addresses)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!sockets.TryGetValue(address.Address, out var socket))
                {
                    socket = new UdpClient(AddressFamily.InterNetwork);
                    try
                    {
                        socket.EnableBroadcast = true;
                        socket.Client.Bind(new IPEndPoint(address.Address, 0));
                    }
                    catch { socket.Dispose(); throw; }
                    sockets.Add(address.Address, socket);
                }
                await socket.SendAsync(payload, new IPEndPoint(address.Broadcast, Discovery.Port), token);
            }
            catch (SocketException error)
            {
                if (sockets.Remove(address.Address, out var broken)) broken.Dispose();
                onFailure(error);
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var socket in sockets.Values) socket.Dispose();
        sockets.Clear();
    }
}
