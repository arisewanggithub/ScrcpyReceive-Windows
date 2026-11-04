using System.Runtime.InteropServices;
using System.Diagnostics;
using ScrcpyReceive.Core;

namespace ScrcpyReceive.Media;

public sealed unsafe partial class WasapiOutput : IDisposable
{
    [LibraryImport("ole32.dll")]
    private static partial int CoIncrementMTAUsage(out nint cookie);
    [LibraryImport("ole32.dll")]
    private static partial int CoDecrementMTAUsage(nint cookie);
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);
    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint AvSetMmThreadCharacteristics(string task, ref uint index);
    [LibraryImport("avrt.dll", SetLastError = true)]
    private static partial int AvRevertMmThreadCharacteristics(nint handle);
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort Format, Channels;
        public uint SampleRate, BytesPerSecond;
        public ushort BlockAlign, BitsPerSample, Extra;
    }

    private readonly object gate = new();
    private nint apartment, enumerator, client, render, clock;
    private string endpoint = "";
    private ulong frequency, origin;
    private long written, lastDefaultCheck;
    private ulong lastPosition;
    private long progressAt;
    private long consumedAt, lastConsumed, transitFrames;
    private string clockSource = "warmup";
    private long lastPlayed;
    private uint lastPadding;
    private long startedAt, lastWriteAt, maximumWriteGapMs, emptyEpisodes, maximumEmptyMs;
    private long? emptyAt;
    private readonly AutoResetEvent ready = new(false);
    private bool started, draining, disposed, initialized;
    public uint Capacity { get; private set; }
    public static IDisposable AudioThread()
    {
        uint index = 0;
        var handle = AvSetMmThreadCharacteristics("Audio", ref index);
        if (handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Audio scheduling is unavailable.");
        return new AudioThreadLease(handle);
    }
    private sealed class AudioThreadLease(nint handle) : IDisposable
    {
        public void Dispose()
        {
            if (AvRevertMmThreadCharacteristics(handle) == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Audio scheduling cleanup failed.");
        }
    }
    public void WaitForBuffer(CancellationToken token)
    {
        if (WaitHandle.WaitAny([ready, token.WaitHandle], 250) == 1) token.ThrowIfCancellationRequested();
    }
    public bool Started { get { lock (gate) return started; } }
    public long Written { get { lock (gate) return written; } }
    public string Diagnostics { get { lock (gate) return $"deviceHz={frequency}, capacity={Capacity}, written={written}, played={lastPlayed}, padding={lastPadding}, clockSource={clockSource}, runMs={(started ? Environment.TickCount64 - startedAt : 0)}, writeGapMaxMs={maximumWriteGapMs}, emptyEpisodes={emptyEpisodes}, emptyMaxMs={maximumEmptyMs}"; } }
    private static nint Method(nint value, int slot) => (*(nint**)value)[slot];

    public WasapiOutput()
    {
        Marshal.ThrowExceptionForHR(CoIncrementMTAUsage(out apartment));
        try
        {
            var clsid = new Guid("bcde0395-e52f-467c-8e3d-c4579291692e");
            var iid = new Guid("a95664d2-9614-4f35-a746-de8db63617e6");
            Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, 23, in iid, out enumerator));
            var device = DefaultDevice();
            try
            {
                endpoint = DeviceId(device);
                var audioId = new Guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
                nint audioClient = 0;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Method(device, 3))(
                    device, &audioId, 23, 0, &audioClient));
                client = audioClient;
            }
            finally { Marshal.Release(device); }
            var format = new WaveFormat { Format = 1, Channels = 2, SampleRate = 48000, BytesPerSecond = 192000, BlockAlign = 4, BitsPerSample = 16 };
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, WaveFormat*, nint, int>)Method(client, 3))(
                client, 0, 0x88040000, 200_000, 0, &format, 0));
            initialized = true;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(client, 13))(
                client, ready.SafeWaitHandle.DangerousGetHandle()));
            uint capacity;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Method(client, 4))(client, &capacity));
            if (capacity is 0 or > AudioFormat.Rate / 20) throw new NotSupportedException("WASAPI output buffer exceeds the 50 ms low-latency budget.");
            Capacity = capacity;
            render = Service(new("f294acfc-3146-4483-a7bf-addca7c260e2"));
            clock = Service(new("cd63314f-3fba-4a1b-812c-ef96358728e7"));
            ulong rate;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, ulong*, int>)Method(clock, 3))(clock, &rate));
            if (rate == 0) throw new IOException("WASAPI returned a zero audio-clock frequency.");
            frequency = rate;
            CheckDefault();
        }
        catch { Dispose(); throw; }
    }

    private nint DefaultDevice()
    {
        nint device;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Method(enumerator, 4))(
            enumerator, 0, 1, &device));
        return device;
    }
    private static string DeviceId(nint device)
    {
        nint text;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(device, 5))(device, &text));
        try { return Marshal.PtrToStringUni(text) ?? throw new IOException("Playback endpoint ID is missing."); }
        finally { Marshal.FreeCoTaskMem(text); }
    }
    private nint Service(Guid id)
    {
        nint result;
        var status = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(client, 14))(client, &id, &result);
        if (status < 0) throw new IOException($"WASAPI service {id} failed (0x{status:X8}).", Marshal.GetExceptionForHR(status));
        return result;
    }
    private uint Padding()
    {
        uint count;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Method(client, 6))(client, &count));
        if (count > Capacity) throw new IOException("WASAPI padding exceeds its output buffer.");
        lastPadding = count;
        if (started && !draining)
        {
            var at = Environment.TickCount64;
            if (count == 0)
            {
                if (emptyAt is null) { emptyAt = at; emptyEpisodes++; }
                maximumEmptyMs = Math.Max(maximumEmptyMs, at - emptyAt.Value);
            }
            else if (emptyAt is { } empty)
            {
                maximumEmptyMs = Math.Max(maximumEmptyMs, at - empty);
                emptyAt = null;
            }
        }
        return count;
    }
    private ulong Position(out ulong qpc)
    {
        ulong position, time;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, ulong*, ulong*, int>)Method(clock, 4))(clock, &position, &time));
        qpc = time;
        return position;
    }
    public long PlayedFrames()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!started) return 0;
            var position = Position(out var timestamp);
            if (position < origin || position < lastPosition) throw new IOException("WASAPI audio-clock position moved backwards.");
            if (position != lastPosition) { lastPosition = position; progressAt = Environment.TickCount64; }
            var frames = (long)((position - origin) / frequency * 48000 + (position - origin) % frequency * 48000 / frequency);
            var ticks = (ulong)Stopwatch.GetTimestamp();
            var qpcRate = (ulong)Stopwatch.Frequency;
            var now = ticks / qpcRate * 10_000_000 + ticks % qpcRate * 10_000_000 / qpcRate;
            var consumed = written - Padding();
            if (consumed < lastConsumed) throw new IOException("WASAPI client consumption moved backwards.");
            if (consumed > lastConsumed) { lastConsumed = consumed; consumedAt = Environment.TickCount64; }
            if (!draining && Environment.TickCount64 - consumedAt >= 1000 && lastPadding > 0)
                throw new IOException("WASAPI output stopped consuming queued audio.");
            var qualified = timestamp > 0 && timestamp <= now && now - timestamp <= 1_000_000 &&
                Environment.TickCount64 - progressAt <= 100;
            if (qualified)
            {
                lastPlayed = AudioPosition.Presented(frames, timestamp, now, consumed, lastPlayed);
                transitFrames = Math.Max(0, consumed - lastPlayed);
                clockSource = "hardware-timestamp";
            }
            else
            {
                lastPlayed = AudioPosition.RetainedTransit(consumed, transitFrames, lastPlayed);
                clockSource = "consumption-retained-transit";
            }
            return lastPlayed;
        }
    }
    public uint BufferedFrames { get { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); return Padding(); } } }

    public int Write(byte[] data, int offsetFrames, int frames)
    {
        if (offsetFrames < 0 || frames < 0 || offsetFrames > data.Length / 4 || frames > data.Length / 4 - offsetFrames)
            throw new ArgumentOutOfRangeException(nameof(frames), "PCM write exceeds the source buffer.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var count = Math.Min(frames, checked((int)(Capacity - Padding())));
            if (count == 0) return 0;
            nint destination;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(render, 3))(render, (uint)count, &destination));
            var copied = false;
            try
            {
                fixed (byte* source = data) Buffer.MemoryCopy(source + offsetFrames * 4, (void*)destination, count * 4, count * 4);
                copied = true;
            }
            finally
            {
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Method(render, 4))(
                    render, copied ? (uint)count : 0, 0));
            }
            written += count;
            if (started)
            {
                var at = Environment.TickCount64;
                maximumWriteGapMs = Math.Max(maximumWriteGapMs, at - lastWriteAt);
                lastWriteAt = at;
            }
            return count;
        }
    }
    public void Start()
    {
        lock (gate)
        {
            origin = Position(out _);
            lastPosition = origin;
            progressAt = Environment.TickCount64;
            consumedAt = progressAt;
            lastConsumed = 0;
            transitFrames = Math.Min(Capacity, 4800);
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)Method(client, 10))(client));
            started = true;
            startedAt = Environment.TickCount64;
            lastWriteAt = startedAt;
        }
    }
    private void CheckDefault()
    {
        var device = DefaultDevice();
        try { if (DeviceId(device) != endpoint) throw new IOException("Default playback device changed; reconnect to use the new device."); }
        finally { Marshal.Release(device); }
    }
    public void CheckHealth()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            _ = PlayedFrames();
            if (Environment.TickCount64 - lastDefaultCheck >= 500)
            {
                CheckDefault();
                lastDefaultCheck = Environment.TickCount64;
            }
        }
    }
    public void BeginDrain() { lock (gate) draining = true; }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (client != 0 && initialized)
                {
                    try { if (started) Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)Method(client, 11))(client)); }
                    finally { Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)Method(client, 12))(client)); }
                }
            }
            finally
            {
                foreach (var pointer in new[] { render, clock, client, enumerator }) if (pointer != 0) Marshal.Release(pointer);
                render = clock = client = enumerator = 0;
                ready.Dispose();
                if (apartment != 0) { var owned = apartment; apartment = 0; Marshal.ThrowExceptionForHR(CoDecrementMTAUsage(owned)); }
            }
        }
    }
}
