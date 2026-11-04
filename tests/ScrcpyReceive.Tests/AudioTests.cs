using System.Text.Json;
using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class AudioTests
{
    [Theory]
    [InlineData("raw", 0)]
    [InlineData("aac", 0)]
    [InlineData("opus", 312)]
    public void PairedAudioConfigurationsValidate(string codec, int skip)
    {
        var format = AudioFormat.Parse(QueueAndTrackTests.Configuration(2, codec: codec).Object(), codec);
        Assert.Equal(codec, format.Codec);
        Assert.Equal(skip, format.PreSkip);
        Assert.Equal(2, format.Channels);
    }

    [Theory]
    [InlineData("aac", "EnA=")]
    [InlineData("aac", "EZA=AA")]
    [InlineData("opus", "")]
    [InlineData("opus", "T3B1c0hlYWQBAjgBgLsAAAAAAAE=")]
    public void InvalidCompressedAudioConfigurationFailsBeforeReadiness(string codec, string csd)
    {
        var body = JsonObject.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            codec, sampleRate = 48000, channels = 2, csd = new[] { csd },
            codecDelayNs = 6500000, seekPrerollNs = 80000000
        }));
        Assert.Throws<ProtocolException>(() => AudioFormat.Parse(body, codec));
    }

    [Fact]
    public void QualifiedHardwareTimestampInterpolatesButNeverPassesClientConsumption()
    {
        Assert.Equal(480, AudioPosition.Presented(0, 10_000_000, 10_100_000, 960, 0));
        Assert.Equal(240, AudioPosition.Presented(0, 10_000_000, 10_100_000, 240, 0));
        Assert.Equal(480, AudioPosition.Presented(0, 0, 10_100_000, 960, 480));
        Assert.Equal(480, AudioPosition.Presented(0, 1, 20_000_000, 960, 480));
    }

    [Fact]
    public async Task AudioTrackRetainsItsDedicatedThreadAcrossAsyncWaits()
    {
        await TrackThread.Run(async () =>
        {
            var thread = Environment.CurrentManagedThreadId;
            await Task.Delay(10);
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            await Task.Yield();
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
        });
    }

    [Fact]
    public void StaleHardwareTimestampKeepsLearnedTransitAndFollowsRealConsumption()
    {
        Assert.Equal(4800, AudioPosition.RetainedTransit(5280, 480, 4600));
        Assert.Equal(5280, AudioPosition.RetainedTransit(5760, 480, 4800));
        Assert.Equal(5280, AudioPosition.RetainedTransit(5760, 480, 5280));
        Assert.Equal(0, AudioPosition.RetainedTransit(0, 480, 0));
    }
}
