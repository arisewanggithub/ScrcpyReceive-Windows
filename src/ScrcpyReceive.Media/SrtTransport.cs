using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ScrcpyReceive.Core;

namespace ScrcpyReceive.Media;

internal sealed class SrtTransport : IDisposable
{
    private const string Library = "ScrcpyReceive.Srt";
    private static readonly object StartupGate = new();
    private static bool initialized;
    private static bool resolverInstalled;
    private readonly SrtDescriptor descriptor;
    private readonly byte[] message = new byte[65536];
    private int socket = -1;
    private int offset, remaining;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_startup();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_cleanup();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_create_socket();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_close(int socket);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_setsockflag(int socket, int option, byte[] value, int length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_connect(int socket, byte[] address, int length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_getsockstate(int socket);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_recvmsg(int socket, byte[] buffer, int length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_getlasterror(nint error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern uint srt_getversion();

    internal static void Validate(SrtDescriptor descriptor)
    {
        var endpoint = Endpoint(descriptor.Url);
        JsonObject.Require(descriptor.StreamId.StartsWith("sc-", StringComparison.Ordinal) &&
            long.TryParse(descriptor.StreamId.AsSpan(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _),
            "Invalid SRT stream ID.", "UNSUPPORTED_CONFIG");
        var session = long.Parse(descriptor.StreamId.AsSpan(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        _ = SrtDescriptor.Parse(JsonSerializer.SerializeToElement(new
        {
            kind = "srt", container = "fmp4", url = descriptor.Url,
            latencyMs = descriptor.LatencyMs, streamId = descriptor.StreamId
        }), session, endpoint.Address);
    }

    private static IPEndPoint Endpoint(string url)
    {
        JsonObject.Require(url.StartsWith("srt://", StringComparison.Ordinal) &&
            IPEndPoint.TryParse(url[6..], out _), "SRT requires a numeric endpoint.", "UNSUPPORTED_CONFIG");
        return IPEndPoint.Parse(url[6..]);
    }

    internal static void Preflight()
    {
        lock (StartupGate)
        {
            if (initialized) return;
            if (!resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(typeof(SrtTransport).Assembly, (name, _, _) =>
                    name == Library ? NativeLibrary.Load(Path.Combine(NativeRuntime.RootPath, "srt.dll")) : 0);
                resolverInstalled = true;
            }
            var version = srt_getversion();
            if (version != 0x010507) throw new NotSupportedException("Pinned libsrt 1.5.7 is required.");
            Check(srt_startup(), "Initialize libsrt");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => srt_cleanup();
            initialized = true;
        }
    }

    internal SrtTransport(SrtDescriptor descriptor) => this.descriptor = descriptor;

    internal void Connect(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        socket = srt_create_socket();
        Check(socket, "Create SRT caller");
        Set(50, 0); // SRTO_TRANSTYPE: live (before dependent socket options).
        Set(1, 0); Set(2, 0); // Nonblocking send/receive, including connect.
        Set(23, descriptor.LatencyMs);
        Set(43, descriptor.LatencyMs); Set(44, descriptor.LatencyMs);
        Set(31, 0); Set(49, 1316);
        Set(36, 10000);
        Check(srt_setsockflag(socket, 7, new byte[4], 4), "Disable SRT close linger");
        var id = Encoding.UTF8.GetBytes(descriptor.StreamId);
        Check(srt_setsockflag(socket, 46, id, id.Length), "Set pinned SRT stream ID");
        var address = Endpoint(descriptor.Url).Serialize();
        var bytes = new byte[address.Size];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = address[index];
        var result = srt_connect(socket, bytes, bytes.Length);
        if (result < 0 && srt_getlasterror(0) is not (6001 or 6002))
            Check(result, "Connect pinned SRT caller");
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var state = srt_getsockstate(socket);
            if (state == 5) return;
            if (state is not (1 or 2 or 4)) throw new IOException("SRT connection failed.");
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(10))
                throw new TimeoutException("SRT connection timed out.");
            token.WaitHandle.WaitOne(5);
        }
    }

    private void Set(int option, int value) =>
        Check(srt_setsockflag(socket, option, BitConverter.GetBytes(value), 4), $"Set SRT option {option}");

    internal int Read(Span<byte> destination, CancellationToken token)
    {
        while (remaining == 0)
        {
            token.ThrowIfCancellationRequested();
            var count = srt_recvmsg(socket, message, message.Length);
            if (count > 0) { offset = 0; remaining = count; break; }
            if (count == 0) throw new EndOfStreamException("Live SRT input ended without control STOP.");
            var error = srt_getlasterror(0);
            if (error == 2001) throw new EndOfStreamException("Live SRT peer disconnected without control STOP.");
            if (error != 6002) Check(count, "Read SRT media");
            token.WaitHandle.WaitOne(5);
        }
        token.ThrowIfCancellationRequested();
        var copied = Math.Min(remaining, destination.Length);
        message.AsSpan(offset, copied).CopyTo(destination);
        offset += copied;
        remaining -= copied;
        return copied;
    }

    private static void Check(int result, string operation)
    {
        if (result < 0) throw new IOException($"{operation} failed (SRT {srt_getlasterror(0)}).");
    }

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref socket, -1);
        if (owned >= 0) srt_close(owned);
    }
}
