using System.Text.Json;
using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class H264Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ActualHardwareEncodedSyntheticGenerationsValidate(int generation)
    {
        using var document = Fixture();
        var segment = document.RootElement.GetProperty("generations")[generation];
        var frames = segment.GetProperty("frames").EnumerateArray().ToArray();
        var configuration = Convert.FromBase64String(frames[0].GetProperty("configuration").GetString()!);
        H264Stream.ValidateConfiguration(configuration, segment.GetProperty("width").GetInt32(), segment.GetProperty("height").GetInt32());
        Assert.Equal((segment.GetProperty("width").GetInt32(), segment.GetProperty("height").GetInt32()),
            H264Stream.Dimensions(configuration));
        Assert.True(frames[0].GetProperty("key").GetBoolean());
        foreach (var frame in frames)
            Assert.Equal(frame.GetProperty("key").GetBoolean(),
                H264Stream.ValidateFrame(Convert.FromBase64String(frame.GetProperty("data").GetString()!)));
        Assert.Throws<ProtocolException>(() => H264Stream.ValidateConfiguration(configuration, 1, 1));
    }

    [Theory]
    [InlineData(0xA0)] // B slice.
    [InlineData(0x90)] // SP slice.
    [InlineData(0x94)] // SI slice.
    public void ReorderedOrUnsupportedSlicesFail(int header) =>
        Assert.Throws<ProtocolException>(() => H264Stream.ValidateFrame([0, 0, 0, 1, 0x41, (byte)header]));

    [Theory]
    [InlineData(0x41, 0xC0, false)]
    [InlineData(0x65, 0xB0, true)]
    public void KeyDetectionUsesActualNalAndSlice(int type, int header, bool key) =>
        Assert.Equal(key, H264Stream.ValidateFrame([0, 0, 0, 1, (byte)type, (byte)header]));

    [Fact]
    public void NalViewsDoNotDuplicateLargeEncodedFrames()
    {
        var data = new byte[4 * 1024 * 1024];
        data[3] = 1;
        data[4] = 0x41;
        data[5] = 0xC0;
        data[^1] = 0x80;
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(H264Stream.ValidateFrame(data));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 65536);
    }

    [Theory]
    [InlineData("0000000165")]
    [InlineData("0000000178C0")]
    [InlineData("00000001E5B0")]
    [InlineData("000000016742001E")]
    [InlineData("000000010000000165B0")]
    public void MalformedFramesAreRejected(string hex) =>
        Assert.Throws<ProtocolException>(() => H264Stream.ValidateFrame(Convert.FromHexString(hex)));

    [Fact]
    public void ConfigRequiresSpsAndPps()
    {
        Assert.Throws<ProtocolException>(() => H264Stream.ValidateConfiguration([0, 0, 0, 1, 0x68, 0x80], 320, 192));
        Assert.Throws<ProtocolException>(() => H264Stream.ValidateConfiguration([0, 0, 0, 1, 0x67, 0x42, 0x80], 320, 192));
    }

    private static JsonDocument Fixture() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-h264.json")));
}
