using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class ReceiverActivityTests
{
    [Fact]
    public void ReadProgressDistinguishesPartialInputFromAProcessingStall()
    {
        long time = 0;
        var activity = new ReceiverActivity(() => time);
        activity.Begin(ReceiverOperation.Read, "header");
        time = 100;
        activity.Progress(ReceiverOperation.Read, "VideoFrame payload=100/1000");
        time = 600;
        Assert.Contains("Read=VideoFrame payload=100/1000 ageMs=600 noProgressMs=500", activity.Diagnostics);
        Assert.Contains("Video=idle ageMs=0", activity.Diagnostics);
    }

    [Fact]
    public void CompletedSlowWorkRemainsVisibleAfterWorkersResume()
    {
        long time = 0;
        var activity = new ReceiverActivity(() => time);
        activity.Begin(ReceiverOperation.Video, "StreamConfig generation=3");
        time = 1500;
        activity.Complete(ReceiverOperation.Video);
        activity.Begin(ReceiverOperation.Video, "VideoFrame generation=3");
        time += 1;
        activity.Complete(ReceiverOperation.Video);
        Assert.Contains("Video=idle ageMs=0 noProgressMs=0 completed=2 maxMs=1500 maxDetail=StreamConfig generation=3", activity.Diagnostics);
        Assert.Contains("Video 1500ms StreamConfig generation=3", activity.Diagnostics);
    }

    [Fact]
    public void RecentSlowHistoryHasAnEightEntryBound()
    {
        long time = 0;
        var activity = new ReceiverActivity(() => time);
        for (var index = 0; index < 12; index++)
        {
            activity.Begin(ReceiverOperation.Admission, $"item{index}");
            time += 100;
            activity.Complete(ReceiverOperation.Admission);
        }
        var history = activity.Diagnostics.Split("recentSlow=[")[1];
        Assert.DoesNotContain("item3", history);
        Assert.Contains("item4", history);
        Assert.Contains("item11", history);
        Assert.Equal(8, history.Split(" | ").Length);
    }

    [Fact]
    public void TransportSamplesOnlyKeepTheLastEightMetadataSnapshots()
    {
        long time = 0;
        var activity = new ReceiverActivity(() => time);
        activity.Begin(ReceiverOperation.Read, "header");
        for (var index = 0; index < 12; index++)
        {
            time += 250;
            activity.RecordTransport($"sample{index}");
        }
        var history = activity.Diagnostics.Split("transportStalls=[")[1];
        Assert.DoesNotContain("sample3", history);
        Assert.Contains("sample4", history);
        Assert.Contains("sample11", history);
        Assert.Equal(8, history.Split(" | ").Length);
    }

    [Fact]
    public void IndependentWorkersKeepSeparateActiveAndMaximumTimings()
    {
        long time = 0;
        var activity = new ReceiverActivity(() => time);
        activity.Begin(ReceiverOperation.Video, "StreamConfig");
        activity.Begin(ReceiverOperation.Audio, "AudioPacket");
        time = 2;
        activity.Complete(ReceiverOperation.Audio);
        time = 1400;
        Assert.Contains("Video=StreamConfig ageMs=1400 noProgressMs=1400", activity.Diagnostics);
        Assert.Contains("Audio=idle ageMs=0 noProgressMs=0 completed=1 maxMs=2", activity.Diagnostics);
    }
}
