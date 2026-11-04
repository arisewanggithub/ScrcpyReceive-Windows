using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class AdditionalCodecTests
{
    private sealed class Bits
    {
        private readonly List<int> data = [];
        public Bits Put(uint value, int count) { for (var bit = count - 1; bit >= 0; bit--) data.Add((int)(value >> bit & 1)); return this; }
        public Bits Ue(uint value)
        {
            var code = value + 1;
            var length = 32 - System.Numerics.BitOperations.LeadingZeroCount(code);
            Put(0, length - 1); Put(code, length); return this;
        }
        public byte[] Finish()
        {
            Put(1, 1);
            while (data.Count % 8 != 0) data.Add(0);
            return Enumerable.Range(0, data.Count / 8).Select(index =>
                (byte)data.Skip(index * 8).Take(8).Aggregate(0, (value, bit) => value * 2 + bit)).ToArray();
        }
    }
    private static byte[] Nal(int type, byte[] payload) => new byte[] { 0, 0, 0, 1, (byte)(type << 1), 1 }.Concat(Escape(payload)).ToArray();
    private static byte[] Escape(byte[] input)
    {
        var result = new List<byte>(); var zeros = 0;
        foreach (var value in input)
        {
            if (zeros >= 2 && value <= 3) { result.Add(3); zeros = 0; }
            result.Add(value); zeros = value == 0 ? zeros + 1 : 0;
        }
        return result.ToArray();
    }
    private static byte[] Hevc(uint profile = 1, uint depth = 0, uint chroma = 1)
    {
        var vps = Nal(32, new Bits().Put(0, 4).Finish());
        var sps = new Bits().Put(0, 4).Put(0, 3).Put(1, 1).Put(0, 3).Put(profile, 5)
            .Put(0, 32).Put(0, 32).Put(0, 16).Put(120, 8)
            .Ue(0).Ue(chroma).Ue(16).Ue(16).Put(0, 1).Ue(depth).Ue(depth).Finish();
        var pps = Nal(34, new Bits().Ue(0).Ue(0).Put(0, 5).Finish());
        return vps.Concat(Nal(33, sps)).Concat(pps).ToArray();
    }
    private static byte[] HevcFrame(int type, uint slice)
    {
        var bits = new Bits().Put(1, 1);
        if (type is >= 16 and <= 21) bits.Put(0, 1);
        return Nal(type, bits.Ue(0).Ue(slice).Finish());
    }
    private static byte[] Obu(int type, byte[] payload) => new[] { (byte)((type << 3) | 2), (byte)payload.Length }.Concat(payload).ToArray();
    private static byte[] Av1(bool highDepth = false, bool reduced = false)
    {
        var bits = new Bits().Put(0, 3).Put(reduced ? 1u : 0u, 1).Put(reduced ? 1u : 0u, 1);
        if (reduced) bits.Put(0, 5);
        else bits.Put(0, 1).Put(0, 1).Put(0, 5).Put(0, 12).Put(0, 5);
        bits.Put(3, 4).Put(3, 4).Put(15, 4).Put(15, 4);
        if (!reduced) bits.Put(0, 1);
        bits.Put(0, 3);
        if (!reduced) bits.Put(0, 4).Put(0, 1).Put(0, 1).Put(0, 1);
        bits.Put(0, 3).Put(highDepth ? 1u : 0u, 1).Put(0, 1).Put(0, 1).Put(0, 1).Put(0, 2).Put(0, 1).Put(0, 1);
        return Obu(1, bits.Finish());
    }

    [Fact]
    public void HevcMainConfigurationAndRandomAccessValidate()
    {
        var configuration = Hevc();
        HevcStream.ValidateConfiguration(configuration, 16, 16);
        Assert.Equal((16, 16), HevcStream.Dimensions(configuration));
        Assert.True(HevcStream.ValidateFrame(HevcFrame(19, 2), configuration));
        Assert.False(HevcStream.ValidateFrame(HevcFrame(1, 1), configuration));
    }
    [Theory]
    [InlineData(2, 0, 1)]
    [InlineData(1, 2, 1)]
    [InlineData(1, 0, 2)]
    public void UnsupportedHevcProfileDepthAndChromaFail(uint profile, uint depth, uint chroma)
    {
        Assert.Equal("UNSUPPORTED_CONFIG", Assert.Throws<ProtocolException>(() =>
            HevcStream.ValidateConfiguration(Hevc(profile, depth, chroma), 16, 16)).Code);
    }
    [Fact]
    public void HevcRejectsBSlicesLeadingPicturesAndMultiplePictures()
    {
        var configuration = Hevc();
        Assert.Throws<ProtocolException>(() => HevcStream.ValidateFrame(HevcFrame(1, 0), configuration));
        Assert.Throws<ProtocolException>(() => HevcStream.ValidateFrame(HevcFrame(8, 1), configuration));
        Assert.Throws<ProtocolException>(() => HevcStream.ValidateFrame(HevcFrame(1, 1).Concat(HevcFrame(1, 1)).ToArray(), configuration));
        Assert.Throws<ProtocolException>(() => HevcStream.ValidateConfiguration(configuration, 32, 16));
        Assert.Throws<ProtocolException>(() => HevcStream.ValidateFrame(Nal(32, new Bits().Put(1, 4).Finish()).Concat(HevcFrame(19, 2)).ToArray(), configuration));
    }
    [Fact]
    public void Av1ProfileZeroConfigurationAndFrameFlagsValidate()
    {
        var configuration = Av1();
        Av1Stream.ValidateConfiguration(configuration, 16, 16);
        Assert.Equal((16, 16), Av1Stream.Dimensions(configuration));
        Assert.True(Av1Stream.ValidateFrame(Obu(6, [0x10]), configuration));
        Assert.False(Av1Stream.ValidateFrame(Obu(6, [0x30]), configuration));
        Av1Stream.ValidateConfiguration(Av1(reduced: true), 16, 16);
        Assert.True(Av1Stream.ValidateFrame(Obu(6, [0]), Av1(reduced: true)));
    }
    [Fact]
    public void Av1RejectsHighDepthShowExistingHiddenAndMultipleFrames()
    {
        var configuration = Av1();
        Assert.Equal("UNSUPPORTED_CONFIG", Assert.Throws<ProtocolException>(() => Av1Stream.ValidateConfiguration(Av1(true), 16, 16)).Code);
        Assert.Throws<ProtocolException>(() => Av1Stream.ValidateFrame(Obu(6, [0x80]), configuration));
        Assert.Throws<ProtocolException>(() => Av1Stream.ValidateFrame(Obu(6, [0]), configuration));
        Assert.Throws<ProtocolException>(() => Av1Stream.ValidateFrame(Obu(6, [0x10]).Concat(Obu(6, [0x10])).ToArray(), configuration));
        Assert.Throws<ProtocolException>(() => Av1Stream.ValidateConfiguration(configuration, 32, 16));
    }
    [Fact]
    public void Av1RejectsMalformedObusAndUndeclaredSequenceChanges()
    {
        Assert.Throws<ProtocolException>(() => Av1Stream.Configuration([0x08, 0]));
        Assert.Throws<ProtocolException>(() => Av1Stream.Configuration([0x0a, 0x80]));
        Assert.Throws<ProtocolException>(() => Av1Stream.Configuration([0x0a, 30, 0]));
        Assert.Throws<ProtocolException>(() => Av1Stream.Configuration(Av1().Concat(Av1()).ToArray()));
        Assert.Throws<ProtocolException>(() => Av1Stream.ValidateFrame(Av1(reduced: true).Concat(Obu(6, [0x10])).ToArray(), Av1()));
    }
}
