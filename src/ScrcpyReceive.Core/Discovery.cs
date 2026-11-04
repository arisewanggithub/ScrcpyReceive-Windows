using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace ScrcpyReceive.Core;

public sealed record ListenerAddress(IPAddress Address, IPAddress Broadcast);

public static class Discovery
{
    public const int Port = 27183;
    public static string DisplayEndpoint(IPAddress address, int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return $"{host} : {port}";
    }

    public static IReadOnlyList<ListenerAddress> Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up &&
                item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(item.Address))
            .Select(item =>
            {
                var address = item.Address.GetAddressBytes();
                var mask = item.IPv4Mask.GetAddressBytes();
                return new ListenerAddress(item.Address,
                    new IPAddress(address.Select((value, index) => (byte)(value | ~mask[index])).ToArray()));
            }).DistinctBy(item => item.Address).OrderBy(item => item.Address.ToString(), StringComparer.Ordinal).ToArray();

    public static byte[] Advertisement(string name, int port, bool busy) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol = "screencopy", version = Wire.Version, name, port, state = busy ? "busy" : "available"
        });
}
