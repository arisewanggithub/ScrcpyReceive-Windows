using System.Buffers.Binary;
using System.Text;
using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task HeaderIsExactlyBigEndianV3()
    {
        var packet = new Packet(PacketType.VideoFrame, 0x0102030405060708, 1, 2, 0x1112131415161718, 1, [0xAA]);
        using var output = new MemoryStream();
        await Wire.WriteAsync(output, packet);
        Assert.Equal("53435059000300080000000100000001010203040506070800000001000000021112131415161718AA",
            Convert.ToHexString(output.ToArray()));
        output.Position = 0;
        var result = await Wire.ReadAsync(output);
        Assert.Equal(packet with { Payload = result.Payload }, result);
        Assert.Equal(packet.Payload, result.Payload);
    }

    [Fact]
    public async Task FragmentationAndCoalescingPreservePackets()
    {
        using var bytes = new MemoryStream();
        await Wire.WriteAsync(bytes, Packet.Control(PacketType.Ping, 5, new { nonce = -123 }));
        await Wire.WriteAsync(bytes, Packet.Control(PacketType.Pong, 5, new { nonce = -123 }));
        using var input = new FragmentedStream(bytes.ToArray());
        Assert.Equal(PacketType.Ping, (await Wire.ReadAsync(input)).Type);
        Assert.Equal(PacketType.Pong, (await Wire.ReadAsync(input)).Type);
        Assert.Equal(bytes.Length, input.Position);
    }

    [Theory]
    [InlineData("16030100", "PROTOCOL")]
    [InlineData("534350590002", "VERSION")]
    [InlineData("534350590001", "VERSION")]
    public async Task MagicAndVersionFailWithoutWaitingForRemainingHeader(string hex, string code)
    {
        using var input = new MemoryStream(Convert.FromHexString(hex));
        var error = await Assert.ThrowsAsync<ProtocolException>(async () => await Wire.ReadAsync(input));
        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData(2, 0, 0, 0, 0, -1, 0)]
    [InlineData(99, 0, 0, 0, 0, -1, 0)]
    [InlineData(8, 4194305, 0, 1, 1, 0, 0)]
    [InlineData(9, 262145, 0, 2, 1, 0, 0)]
    [InlineData(1, 65537, 0, 0, 0, -1, 0)]
    [InlineData(8, 0, 0, 1, 1, 0, 0)]
    [InlineData(8, 1, 0, 2, 1, 0, 0)]
    [InlineData(9, 1, 0, 1, 1, 0, 0)]
    [InlineData(8, 1, 2, 1, 1, 0, 0)]
    [InlineData(8, 1, 0, 1, 0, 0, 0)]
    [InlineData(8, 1, 0, 1, 1, -1, 0)]
    [InlineData(1, 0, 0, 1, 0, -1, 0)]
    [InlineData(8, 1, 0, 1, 1, 0, -1)]
    public async Task InvalidHeaderFailsBeforePayloadRead(int type, int length, int flags, int track,
        int generation, long pts, long session)
    {
        var header = new byte[40];
        "SCPY"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)type);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), length);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), flags);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16), session == 0 ? 1 : session);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24), track);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(28), generation);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(32), pts);
        using var input = new MemoryStream(header);
        await Assert.ThrowsAsync<ProtocolException>(async () => await Wire.ReadAsync(input));
    }

    [Theory]
    [InlineData("{}{}")]
    [InlineData("[]")]
    [InlineData("{\"x\":1,\"x\":2}")]
    [InlineData("{\"x\":{\"y\":1,\"y\":2}}")]
    [InlineData("{\"x\":2147483648}")]
    [InlineData("{\"x\":1.5}")]
    [InlineData("{\"x\":NaN}")]
    [InlineData("{\"x\":\"\\uD800\"}")]
    [InlineData("{\"\\uD800\":1}")]
    public void StrictJsonRejectsInvalidObjects(string json) =>
        Assert.Throws<ProtocolException>(() => JsonObject.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void InvalidUtf8IsNotReplaced() =>
        Assert.Throws<ProtocolException>(() => JsonObject.Parse([123, 34, 120, 34, 58, 34, 255, 34, 125]));

    [Fact]
    public void UnknownFieldsAreIgnoredButStillStructurallyValidated()
    {
        var json = JsonObject.Parse("""{"role":"sender","sessionCode":"ignored","extra":[{"n":1}]}"""u8);
        Assert.Equal("sender", JsonObject.Text(json, "role"));
        Assert.Throws<ProtocolException>(() => JsonObject.Parse("""{"extra":{"duplicate":1,"duplicate":2}}"""u8));
    }

    [Fact]
    public async Task TruncatedPayloadIsAnError()
    {
        using var bytes = new MemoryStream();
        await Wire.WriteAsync(bytes, Packet.Control(PacketType.Hello, 0, new { role = "sender" }));
        using var input = new MemoryStream(bytes.ToArray()[..^1]);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await Wire.ReadAsync(input));
    }

    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}
