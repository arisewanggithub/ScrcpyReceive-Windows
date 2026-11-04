using System.Net;
using System.Net.Sockets;
using ScrcpyReceive.Core;
using ScrcpySend.Core;
using Xunit;

namespace ScrcpyReceive.SenderInterop;

public sealed class DiscoveryInteropTests
{
    [Fact]
    public async Task ActualSenderDiscoveryReceivesReceiverV3PacketAndUsesUdpSourceAddress()
    {
        var name = $"Receiver loopback test {Guid.NewGuid():N}";
        var found = new TaskCompletionSource<ReceiverEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var discovery = new ReceiverDiscovery().RunAsync(entries =>
        {
            var receiver = entries.FirstOrDefault(entry => entry.Name == name);
            if (receiver is not null) found.TrySetResult(receiver);
        }, detail => found.TrySetException(new IOException(detail)), cancellation.Token);
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await udp.SendAsync(Discovery.Advertisement(name, 27184, false), new IPEndPoint(IPAddress.Loopback, 27183), cancellation.Token);
            var result = await found.Task.WaitAsync(cancellation.Token);
            Assert.Equal(IPAddress.Loopback, result.Address);
            Assert.Equal(27184, result.Port);
            Assert.False(result.Busy);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
        }
    }
}
