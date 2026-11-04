using ScrcpyReceive.Core;
using System.Text.Json;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class H264HighTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void GeneratedHighCompatibleFixtureConfigurationValidates(int generation)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-h264-high.json")));
        var segment = document.RootElement.GetProperty("generations")[generation];
        var configuration = Convert.FromBase64String(segment.GetProperty("frames")[0].GetProperty("configuration").GetString()!);
        Assert.Contains(H264Stream.Units(configuration), unit => (unit.Span[0] & 31) == 7 && unit.Span[1] == 100);
        H264Stream.ValidateConfiguration(configuration, segment.GetProperty("width").GetInt32(), segment.GetProperty("height").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void HighProfileDimensionsFollowExtendedFieldsAndScalingLists(int scaling)
    {
        var configuration = Configuration(1, 0, 0, scaling);
        H264Stream.ValidateConfiguration(configuration, 320, 192);
        Assert.Throws<ProtocolException>(() => H264Stream.ValidateConfiguration(configuration, 192, 320));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(2, 0, 0)]
    [InlineData(3, 0, 0)]
    [InlineData(1, 2, 0)]
    [InlineData(1, 0, 2)]
    public void Non420OrNon8BitHighCannotSilentlyUseNv12(int chroma, int lumaDepth, int chromaDepth)
    {
        var error = Assert.Throws<ProtocolException>(() =>
            H264Stream.ValidateConfiguration(Configuration(chroma, lumaDepth, chromaDepth, 0), 320, 192));
        Assert.Equal("UNSUPPORTED_CONFIG", error.Code);
    }

    [Fact]
    public void High10IsRejectedWithTheActualProfileId()
    {
        var configuration = Configuration(1, 0, 0, 0);
        configuration[5] = 110;
        var error = Assert.Throws<ProtocolException>(() => H264Stream.ValidateConfiguration(configuration, 320, 192));
        Assert.Equal("UNSUPPORTED_CONFIG", error.Code);
        Assert.Contains("110", error.Message);
    }

    [Fact]
    public void TruncatedExtendedHighHeaderFailsBeforeDimensions()
    {
        Assert.Throws<ProtocolException>(() =>
            H264Stream.ValidateConfiguration([0, 0, 0, 1, 0x67, 100, 0, 31, 0x80, 0, 0, 0, 1, 0x68, 0x80], 320, 192));
    }

    [Fact]
    public void InvalidScalingListDeltaIsRejected() =>
        Assert.Throws<ProtocolException>(() => H264Stream.ValidateConfiguration(Configuration(1, 0, 0, 4), 320, 192));

    private static byte[] Configuration(int chroma, int lumaDepth, int chromaDepth, int scaling)
    {
        var bits = new List<int>();
        void Write(uint value, int count) { for (var shift = count - 1; shift >= 0; shift--) bits.Add((int)(value >> shift) & 1); }
        void Ue(int value)
        {
            var code = (uint)value + 1;
            var count = 0;
            for (var remaining = code; remaining > 0; remaining >>= 1) count++;
            Write(0, count - 1);
            Write(code, count);
        }
        Write(100, 8); Write(0, 8); Write(31, 8); Ue(0);
        Ue(chroma); Ue(lumaDepth); Ue(chromaDepth); Write(0, 1);
        Write(scaling == 0 ? 0u : 1u, 1);
        if (scaling != 0)
            for (var list = 0; list < 8; list++)
            {
                Write(1, 1);
                if (scaling == 2) Ue(16); // se(-8) selects the default scaling list.
                else for (var item = 0; item < (list < 6 ? 16 : 64); item++)
                    Ue(scaling == 4 ? 258 : scaling == 3 ? (item % 2 == 0 ? 1 : 2) : 0);
            }
        Ue(0); Ue(0); Ue(0); Ue(1); Write(0, 1);
        Ue(19); Ue(11); Write(1, 1); Write(1, 1); Write(0, 1); Write(0, 1); Write(1, 1);
        while (bits.Count % 8 != 0) bits.Add(0);
        var bytes = new List<byte> { 0, 0, 0, 1, 0x67 };
        var zeros = 0;
        for (var offset = 0; offset < bits.Count; offset += 8)
        {
            var value = (byte)bits.Skip(offset).Take(8).Aggregate(0, (current, bit) => (current << 1) | bit);
            if (zeros >= 2 && value <= 3) { bytes.Add(3); zeros = 0; }
            bytes.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        bytes.AddRange([0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80]);
        return bytes.ToArray();
    }
}
