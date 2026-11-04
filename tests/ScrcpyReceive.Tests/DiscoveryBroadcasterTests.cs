using System.Net;
using System.Net.Sockets;
using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class DiscoveryBroadcasterTests
{
    [Fact]
    public async Task ReusesBoundSocketAcrossAdvertisementsAndClosesItOnShutdown()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, Discovery.Port));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var broadcaster = new DiscoveryBroadcaster(error => throw new IOException("Unexpected discovery failure.", error));
        var addresses = new[] { new ListenerAddress(IPAddress.Loopback, IPAddress.Loopback) };
        var payload = Discovery.Advertisement("Socket lifetime test", 27184, false);
        await broadcaster.PublishAsync(addresses, payload, timeout.Token);
        var first = await receiver.ReceiveAsync(timeout.Token);
        await broadcaster.PublishAsync(addresses, payload, timeout.Token);
        var second = await receiver.ReceiveAsync(timeout.Token);
        Assert.Equal(first.RemoteEndPoint, second.RemoteEndPoint);
        Assert.Equal(payload, first.Buffer);
        Assert.Equal(payload, second.Buffer);
        broadcaster.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await broadcaster.PublishAsync(addresses, payload, timeout.Token));
    }

    [Fact]
    public async Task OversizedAdvertisementsFailBeforeSocketCreation()
    {
        using var broadcaster = new DiscoveryBroadcaster(error => throw new IOException("Unexpected socket creation.", error));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await broadcaster.PublishAsync([], new byte[1025], default));
    }
}
