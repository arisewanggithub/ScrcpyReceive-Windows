using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace ScrcpyReceive.Core;

public enum PacketType : ushort
{
    Hello = 1, SessionRequest = 3, SessionAccept = 4, Reject = 5,
    StreamConfig = 6, StreamReady = 7, VideoFrame = 8, AudioPacket = 9,
    Status = 10, Error = 11, Ping = 12, Pong = 13, Stop = 14, SessionEnd = 15
}

public sealed class ProtocolException(string message, string code = "PROTOCOL") : IOException(message)
{
    public string Code { get; } = code;
}

public sealed record Packet(PacketType Type, long Session, uint Track, uint Generation,
    long PtsUs, uint Flags, byte[] Payload)
{
    public static Packet Control(PacketType type, long session, object body) =>
        new(type, session, 0, 0, -1, 0, JsonSerializer.SerializeToUtf8Bytes(body));

    public JsonElement Object() => JsonObject.Parse(Payload);
}

public static class JsonObject
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static JsonElement Parse(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(Utf8.GetString(payload),
                new JsonDocumentOptions { MaxDepth = 32 });
            Require(document.RootElement.ValueKind == JsonValueKind.Object, "Expected JSON object.");
            Validate(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            throw new ProtocolException($"Invalid JSON: {error.Message}");
        }
    }

    private static void Validate(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    Require(names.Add(property.Name), "Duplicate JSON property.");
                    Validate(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Validate(item);
                break;
            case JsonValueKind.Number:
                Require(element.TryGetInt32(out _), "JSON numbers must be signed 32-bit integers.");
                break;
            case JsonValueKind.String:
                _ = element.GetString();
                break;
        }
    }

    public static void Require(bool condition, string message, string code = "PROTOCOL")
    {
        if (!condition) throw new ProtocolException(message, code);
    }

    public static JsonElement Member(JsonElement element, string name, JsonValueKind kind)
    {
        Require(element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out _), $"Missing {name}.");
        var value = element.GetProperty(name);
        Require(value.ValueKind == kind, $"Invalid {name}.");
        return value;
    }

    public static string Text(JsonElement element, string name, int maximum = 80)
    {
        var text = Member(element, name, JsonValueKind.String).GetString()!;
        Require(text.Length <= maximum, $"{name} exceeds {maximum} characters.");
        return text;
    }

    public static int Number(JsonElement element, string name)
    {
        var value = Member(element, name, JsonValueKind.Number);
        Require(value.TryGetInt32(out _), $"Invalid integer {name}.");
        return value.GetInt32();
    }

    public static bool Boolean(JsonElement element, string name)
    {
        Require(element.TryGetProperty(name, out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False, $"Invalid {name}.");
        return value.GetBoolean();
    }
}

public static class Wire
{
    public const int Version = 3;
    public const int HeaderSize = 40;

    public static async ValueTask<Packet> ReadAsync(Stream input, CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderSize];
        await input.ReadExactlyAsync(header.AsMemory(0, 4), cancellationToken);
        JsonObject.Require(header.AsSpan(0, 4).SequenceEqual("SCPY"u8), "Invalid magic.");
        await input.ReadExactlyAsync(header.AsMemory(4, 2), cancellationToken);
        JsonObject.Require(BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) == Version,
            "Protocol version 3 required.", "VERSION");
        await input.ReadExactlyAsync(header.AsMemory(6), cancellationToken);
        var packet = new Packet((PacketType)BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6)),
            BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(16)),
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24)),
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28)),
            BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(32)),
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12)), []);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        Validate(packet, length);
        packet = packet with { Payload = new byte[checked((int)length)] };
        await input.ReadExactlyAsync(packet.Payload, cancellationToken);
        if (!IsMedia(packet.Type)) _ = packet.Object();
        return packet;
    }

    public static async ValueTask WriteAsync(Stream output, Packet packet, CancellationToken cancellationToken = default)
    {
        Validate(packet, (uint)packet.Payload.Length);
        if (!IsMedia(packet.Type)) _ = packet.Object();
        var header = new byte[HeaderSize];
        "SCPY"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)packet.Type);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)packet.Payload.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), packet.Flags);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16), packet.Session);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), packet.Track);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), packet.Generation);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(32), packet.PtsUs);
        await output.WriteAsync(header, cancellationToken);
        await output.WriteAsync(packet.Payload, cancellationToken);
    }

    public static bool IsMedia(PacketType type) => type is PacketType.VideoFrame or PacketType.AudioPacket;

    private static void Validate(Packet packet, uint length)
    {
        JsonObject.Require(Enum.IsDefined(packet.Type), "Unknown or reserved packet type.");
        var media = IsMedia(packet.Type);
        var tracked = media || packet.Type is PacketType.StreamConfig or PacketType.StreamReady;
        var maximum = packet.Type switch
        {
            PacketType.VideoFrame => 4 * 1024 * 1024,
            PacketType.AudioPacket => 256 * 1024,
            _ => 65536
        };
        JsonObject.Require(length <= maximum && (!media || length > 0), "Invalid payload size.");
        JsonObject.Require(packet.Session >= 0 && (!tracked || packet.Session > 0), "Invalid session.");
        JsonObject.Require(tracked
            ? packet.Track is 1 or 2 && packet.Generation is > 0 and <= int.MaxValue
            : packet.Track == 0 && packet.Generation == 0, "Invalid track/generation.");
        JsonObject.Require(packet.Type != PacketType.VideoFrame || packet.Track == 1, "Video requires track 1.");
        JsonObject.Require(packet.Type != PacketType.AudioPacket || packet.Track == 2, "Audio requires track 2.");
        JsonObject.Require(media ? packet.PtsUs >= 0 : packet.PtsUs == -1, "Invalid PTS.");
        JsonObject.Require(packet.Flags == 0 || packet.Type == PacketType.VideoFrame && packet.Flags == 1,
            "Invalid flags.");
    }
}
