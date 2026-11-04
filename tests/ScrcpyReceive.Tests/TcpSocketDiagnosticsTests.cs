using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class TcpSocketDiagnosticsTests
{
    [Fact]
    public void NativeLayoutDistinguishesReceiverWindowAndKernelBytesFromLocalRetransmissions()
    {
        var data = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 5);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 1234);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 65536);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), 131072);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(56), 9_000_000_000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(64), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(68), 2000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(80), 3);
        var result = TcpSocketDiagnostics.Parse(data);
        Assert.Contains("receiveWindow=65536", result);
        Assert.Contains("receiveBuffer=131072", result);
        Assert.Contains("kernelBytesIn=9000000000", result);
        Assert.Contains("reorderedBytes=1000", result);
        Assert.Contains("localRetransmittedBytes=2000", result);
        Assert.Contains("localRetransmitTimeouts=3", result);
    }

    [Fact]
    public void InvalidNativeResponseCannotLookLikeSuccessfulCounters() =>
        Assert.Throws<InvalidDataException>(() => TcpSocketDiagnostics.Parse(new byte[84]));

    [Fact]
    public async Task OwnedWindowsLoopbackSocketReturnsActualKernelStatistics()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();
        await client.GetStream().WriteAsync(new byte[4096]);
        await accepted.GetStream().ReadExactlyAsync(new byte[4096]);
        var result = TcpSocketDiagnostics.Read(accepted.Client);
        Assert.Contains("tcpState=4", result);
        Assert.Contains("receiveWindow=", result);
        Assert.Contains("kernelBytesIn=", result);
        Assert.DoesNotContain("unavailable", result);
    }
}
