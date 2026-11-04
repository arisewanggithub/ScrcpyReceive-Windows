using System.Buffers.Binary;
using System.Net.Sockets;

namespace ScrcpyReceive.Core;

public static class TcpSocketDiagnostics
{
    public static string Read(Socket? socket)
    {
        if (socket is null) return "tcpInfo=unavailable(closed)";
        if (!OperatingSystem.IsWindows()) return "tcpInfo=unavailable(platform)";
        try
        {
            var data = new byte[88];
            // SIO_TCP_INFO, version 0: a read-only query on this owned socket.
            var count = socket.IOControl(unchecked((int)0xD8000027), new byte[4], data);
            if (count < 85) return $"tcpInfo=unavailable(responseBytes={count})";
            return $"availableBytes={socket.Available}, " + Parse(data.AsSpan(0, count));
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            return error is SocketException socketError
                ? $"tcpInfo=unavailable({socketError.SocketErrorCode})"
                : "tcpInfo=unavailable(disposed)";
        }
    }

    public static string Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 85) throw new InvalidDataException("TCP_INFO_v0 response was truncated.");
        return $"tcpState={BinaryPrimitives.ReadUInt32LittleEndian(data)}, " +
            $"rttUs={BinaryPrimitives.ReadUInt32LittleEndian(data[20..])}, " +
            $"receiveWindow={BinaryPrimitives.ReadUInt32LittleEndian(data[40..])}, " +
            $"receiveBuffer={BinaryPrimitives.ReadUInt32LittleEndian(data[44..])}, " +
            $"kernelBytesIn={BinaryPrimitives.ReadUInt64LittleEndian(data[56..])}, " +
            $"reorderedBytes={BinaryPrimitives.ReadUInt32LittleEndian(data[64..])}, " +
            $"localRetransmittedBytes={BinaryPrimitives.ReadUInt32LittleEndian(data[68..])}, " +
            $"localRetransmitTimeouts={BinaryPrimitives.ReadUInt32LittleEndian(data[80..])}";
    }
}
