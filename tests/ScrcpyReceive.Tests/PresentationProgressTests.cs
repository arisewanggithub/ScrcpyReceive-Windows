using System.Text.Json;
using ScrcpyReceive.SrtInterop;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class PresentationProgressTests
{
    private static readonly PlaybackWindow Window = new(100, 3100);
    private static JsonElement Samples(Func<int, long>? count = null, Func<int, long>? pts = null, int length = 31) =>
        JsonSerializer.SerializeToElement(Enumerable.Range(0, length).Select(index => new
        {
            timestampTicks = 100L + index * 100,
            presentedFrames = count?.Invoke(index) ?? 2 + index,
            sourcePtsUs = pts?.Invoke(index) ?? 1_000_000L + index * 100_000
        }));

    [Fact]
    public void ContinuingPresentationPassesAllPostReadinessIntervals() =>
        PresentationProgress.Validate(Samples(), Window, 1000, 3);

    [Fact]
    public void TwoStartupPicturesCannotHideFrozenPostReadinessVideo() =>
        Assert.Throws<InvalidOperationException>(() =>
            PresentationProgress.Validate(Samples(_ => 2, _ => 1_000_000), Window, 1000, 3));

    [Fact]
    public void EarlyProgressCannotHideAFreezeNearTheEnd() =>
        Assert.Throws<InvalidOperationException>(() =>
            PresentationProgress.Validate(Samples(index => 2 + Math.Min(index, 20),
                index => 1_000_000L + Math.Min(index, 20) * 100_000), Window, 1000, 3));

    [Fact]
    public void ChangingCountsWithoutNewSourcePicturesDoesNotPass() =>
        Assert.Throws<InvalidOperationException>(() =>
            PresentationProgress.Validate(Samples(pts: _ => 1_000_000), Window, 1000, 3));

    [Fact]
    public void AdvancingSourcePtsWithoutPresentationDoesNotPass() =>
        Assert.Throws<InvalidOperationException>(() =>
            PresentationProgress.Validate(Samples(count: _ => 2), Window, 1000, 3));

    [Fact]
    public void MissingEndOfIntervalSamplesCannotHideAFreeze() =>
        Assert.Throws<InvalidOperationException>(() =>
            PresentationProgress.Validate(Samples(length: 20), Window, 1000, 3));

    [Fact]
    public void LossRecoveryRequiresContinuingPresentationAfterRelease() =>
        PresentationProgress.Validate(Samples(), Window with { LossStartedAtTicks = 200, LossRecoveredAtTicks = 700 }, 1000, 3);

    [Fact]
    public void LossWithoutRecoveryCannotPassAsContinuingPlayback() =>
        Assert.Throws<InvalidOperationException>(() =>
            PresentationProgress.Validate(Samples(), Window with { LossStartedAtTicks = 200 }, 1000, 3));

    [Fact]
    public void RecoveryJustBeforeStopDoesNotProvePlaybackRecovered() =>
        Assert.Throws<InvalidOperationException>(() =>
            PresentationProgress.Validate(Samples(), Window with { LossStartedAtTicks = 2800, LossRecoveredAtTicks = 3000 }, 1000, 3));

    [Fact]
    public void DeliberatelyUnrecoveredGapMayOnlyPassTheStopCleanupCase() =>
        PresentationProgress.Validate(Samples(_ => 2, _ => 1_000_000),
            Window with { LossStartedAtTicks = 200, PendingLossStop = true }, 1000, 3);
}
