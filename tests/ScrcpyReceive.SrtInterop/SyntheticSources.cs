using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ScrcpyReceive.Core;
using Sender = ScrcpySend.Core;

namespace ScrcpyReceive.SrtInterop;

internal sealed class SyntheticVideo : Sender.IVideoSource
{
    private sealed record Frame(byte[] Data, byte[] Configuration);
    private readonly Frame[][] generations;
    private readonly bool rotate, partial;
    private readonly string codec;
    private long produced;
    private int generationsProduced;

    internal SyntheticVideo(string path, string codec, bool rotate = false, bool partial = false)
    {
        this.codec = codec;
        this.rotate = rotate;
        this.partial = partial;
        var file = new FileInfo(path);
        Require(file.Exists && file.Length is > 0 and <= 32 * 1024 * 1024, "Missing or oversized synthetic fixture.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new() { MaxDepth = 16 });
        var root = document.RootElement;
        Require(root.GetProperty("syntheticOnly").ValueKind == JsonValueKind.True,
            "Only explicitly labeled syntheticOnly fixtures are permitted.");
        if (root.TryGetProperty("videoCodec", out var declaredCodec))
            Require(declaredCodec.GetString() == codec, "Fixture codec differs from the requested case.");
        var sourceGenerations = root.GetProperty("generations");
        Require(sourceGenerations.ValueKind == JsonValueKind.Array && sourceGenerations.GetArrayLength() > 0, "Missing synthetic generations.");
        Require(!rotate || sourceGenerations.GetArrayLength() == 3, "Rotation requires three labeled fixture generations.");
        var generation = sourceGenerations[0];
        Width = generation.GetProperty("width").GetInt32();
        Height = generation.GetProperty("height").GetInt32();
        OriginUs = generation.GetProperty("frames")[0].GetProperty("ptsUs").GetInt64();
        generations = sourceGenerations.EnumerateArray().Take(rotate ? 3 : 1)
            .Select(item => LoadGeneration(item, codec)).ToArray();
        Require(!rotate || generations.All(frames => frames.Length <= 32 && 32 % frames.Length == 0),
            "Rotation GOP lengths must divide the bounded 32-frame generation interval.");
        if (rotate)
            Require(sourceGenerations[1].GetProperty("width").GetInt32() == Height &&
                sourceGenerations[1].GetProperty("height").GetInt32() == Width &&
                sourceGenerations[2].GetProperty("width").GetInt32() == Width &&
                sourceGenerations[2].GetProperty("height").GetInt32() == Height,
                "Rotation fixture must describe landscape/portrait/landscape dimensions.");
    }

    private static Frame[] LoadGeneration(JsonElement generation, string codec)
    {
        var Width = generation.GetProperty("width").GetInt32();
        var Height = generation.GetProperty("height").GetInt32();
        Require(Width is >= 64 and <= 3840 && Height is >= 64 and <= 3840, "Unsupported synthetic dimensions.");
        var entries = generation.GetProperty("frames");
        Require(entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() is > 0 and <= 512,
            "Synthetic GOP must contain 1..512 frames.");
        var frames = new Frame[entries.GetArrayLength()];
        byte[]? configuration = null;
        long previous = -1;
        for (var index = 0; index < frames.Length; index++)
        {
            var entry = entries[index];
            var csd = Decode(entry, "configuration", 45000);
            var data = Decode(entry, "data", 4 * 1024 * 1024);
            var pts = entry.GetProperty("ptsUs").GetInt64();
            Require(pts >= 0 && pts >= previous, "Invalid synthetic source timestamp.");
            previous = pts;
            if (index == 0)
            {
                CodecConfiguration.ValidateVideo(codec, csd, Width, Height);
                configuration = csd;
            }
            Require(configuration!.AsSpan().SequenceEqual(csd), "A repeated GOP cannot change its declared configuration.");
            var key = CodecConfiguration.ValidateVideoFrame(codec, data, csd);
            Require(entry.GetProperty("key").GetBoolean() == key && (index != 0 || key),
                "The repeated GOP must start with an authentic random-access picture and preserve declared key flags.");
            frames[index] = new(data, csd);
        }
        return frames;
    }

    public string EncoderName => "TEST-ONLY-SYNTHETIC-GOP-NO-CAPTURE";
    public int Width { get; }
    public int Height { get; }
    internal long OriginUs { get; }
    internal long FramesProduced => Interlocked.Read(ref produced);
    internal int GenerationsProduced => Volatile.Read(ref generationsProduced);

    public async IAsyncEnumerable<Sender.EncodedVideo> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        for (long index = 0; ; index++)
        {
            await PaceAsync(start, index * 1_000_000 / 30, cancellationToken);
            var generation = rotate ? (int)Math.Min(index / 32, generations.Length - 1) : 0;
            var frames = generations[generation];
            var frame = frames[(int)((rotate ? index % 32 : index) % frames.Length)];
            var data = frame.Data;
            if (CodecConfiguration.ValidateVideoFrame(codec, data, frame.Configuration))
                data = InBandKey(frame, generation);
            Volatile.Write(ref generationsProduced, generation + 1);
            Interlocked.Increment(ref produced);
            yield return new(data, checked(OriginUs + index * 1_000_000 / 30), frame.Configuration);
        }
    }

    private byte[] InBandKey(Frame frame, int generation)
    {
        if (codec == "av1")
        {
            var sequence = Av1Stream.Configuration(frame.Configuration);
            return frame.Data.AsSpan().IndexOf(sequence) >= 0 ? frame.Data : sequence.Concat(frame.Data).ToArray();
        }
        bool Parameter(ReadOnlyMemory<byte> unit) =>
            codec == "h264" ? (unit.Span[0] & 31) is 7 or 8 : (unit.Span[0] >> 1 & 63) is 32 or 33 or 34;
        var parameters = H264Stream.Units(frame.Configuration).Where(Parameter);
        if (partial && generation > 0)
        {
            var previous = H264Stream.Units(generations[generation - 1][0].Configuration).Where(Parameter).ToArray();
            var complete = parameters.ToArray();
            var updates = complete.Where(unit => !previous.Any(old => old.Span.SequenceEqual(unit.Span))).ToArray();
            Require(updates.Length > 0 && updates.Length < complete.Length,
                "Partial-update fixtures must actually omit unchanged parameter sets while changing others.");
            parameters = updates;
        }
        var units = parameters.Concat(H264Stream.Units(frame.Data).Where(unit => !Parameter(unit))).ToArray();
        Require(units.Sum(unit => (long)unit.Length + 4) <= 4 * 1024 * 1024, "Generated in-band key exceeds sample bounds.");
        return units.SelectMany(unit => new byte[] { 0, 0, 0, 1 }.Concat(unit.ToArray())).ToArray();
    }

    internal static async Task PaceAsync(long start, long dueUs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var remaining = TimeSpan.FromMicroseconds(dueUs) - Stopwatch.GetElapsedTime(start);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
        token.ThrowIfCancellationRequested();
    }

    private static byte[] Decode(JsonElement entry, string name, int maximum)
    {
        var text = entry.GetProperty(name).GetString();
        Require(text is not null && text.Length <= ((maximum + 2) / 3) * 4, "Synthetic sample exceeds its bound.");
        var bytes = Convert.FromBase64String(text!);
        Require(bytes.Length is > 0 && bytes.Length <= maximum, "Invalid synthetic sample size.");
        return bytes;
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class SyntheticSilence(long originUs) : Sender.IAudioSource
{
    private readonly byte[] silence = new byte[1024 * 2 * sizeof(short)];
    private long packets;
    internal long FramesProduced => Interlocked.Read(ref packets) * 1024;
    public object Configuration => new
    {
        codec = "raw", sampleRate = 48000, channels = 2, sampleFormat = "s16le", csd = Array.Empty<string>()
    };

    // This source never captures, mutes, or accesses an actual audio endpoint.
    public void BeginTransmission() { }

    public async IAsyncEnumerable<Sender.AudioPacket> ReadPacketsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        for (long index = 0; ; index++)
        {
            var offsetUs = index * 1024 * 1_000_000 / 48000;
            await SyntheticVideo.PaceAsync(start, offsetUs, cancellationToken);
            Interlocked.Increment(ref packets);
            yield return new(silence, checked(originUs + offsetUs));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
