using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class AudioBufferTests
{
    private static byte[] Data(int frames, byte value = 1) => Enumerable.Repeat(value, frames * 4).ToArray();

    [Fact]
    public void StartupKeepsOnlyTargetPlusTenMilliseconds()
    {
        var buffer = new AudioBuffer();
        buffer.Push(Data(AudioFormat.MaximumFrames), 1_000_000, AudioFormat.MaximumFrames);
        Assert.Equal(2880, buffer.Frames);
        Assert.Equal(9120, buffer.Snapshot.Dropped);
        var block = buffer.Pull(480);
        Assert.Equal(1_190_000, block.Spans[0].FirstPtsUs);
    }

    [Fact]
    public void UnderrunProvidesSilenceWithoutDiscardingLateContent()
    {
        var buffer = new AudioBuffer();
        buffer.Start();
        var silence = buffer.Pull(960);
        Assert.Empty(silence.Spans);
        Assert.All(silence.Data, value => Assert.Equal(0, value));
        buffer.Push(Data(960, 7), 0, 960);
        var recovered = buffer.Pull(960);
        Assert.Single(recovered.Spans);
        Assert.All(recovered.Data, value => Assert.Equal(7, value));
        Assert.Equal(960, buffer.Snapshot.Silence);
        Assert.Equal(0, buffer.Snapshot.Dropped);
    }

    [Fact]
    public void OverflowShedsOnlyOldDecodedOutput()
    {
        var buffer = new AudioBuffer();
        buffer.Start();
        buffer.Push(Data(4800, 1), 1_000_000, 4800);
        buffer.Push(Data(4800, 2), 1_100_000, 4800);
        Assert.Equal(5520, buffer.Frames);
        Assert.Equal(4080, buffer.Snapshot.Dropped);
        var block = buffer.Pull(960);
        Assert.Equal(1_085_000, block.Spans[0].FirstPtsUs);
        Assert.All(block.Data.Take(720 * 4), value => Assert.Equal(1, value));
        Assert.All(block.Data.Skip(720 * 4), value => Assert.Equal(2, value));
    }

    [Fact]
    public void SourceAudioProgressExcludesRecoverySilence()
    {
        var buffer = new AudioBuffer();
        var presentation = new AudioPresentation();
        buffer.Push(Data(480), 1_000_000, 480);
        presentation.Submit(0, buffer.Pull(960));
        Assert.Equal((240L, (long?)1_005_000), presentation.Current(240));
        Assert.Equal((480L, (long?)1_010_000), presentation.Current(960));
        presentation.Submit(960, buffer.Pull(960));
        Assert.Equal((480L, (long?)1_010_000), presentation.Current(1920));
        Assert.Equal((480L, (long?)1_010_000), presentation.Current(999999));
    }

    [Fact]
    public void OpusSourcePtsReversalsStayInFifoOrder()
    {
        var buffer = new AudioBuffer();
        var presentation = new AudioPresentation();
        buffer.Push(Data(960, 1), 1_000_000, 960);
        buffer.Push(Data(960, 2), 700_000, 960);
        presentation.Submit(0, buffer.Pull(960));
        Assert.Equal((960L, (long?)1_020_000), presentation.Current(960));
        presentation.Submit(960, buffer.Pull(960));
        Assert.Equal((1920L, (long?)720_000), presentation.Current(1920));
    }

    [Fact]
    public void DrainDoesNotKeepGeneratingSilence()
    {
        var buffer = new AudioBuffer();
        buffer.Push(Data(100), 0, 100);
        buffer.BeginDrain();
        Assert.Equal(100, buffer.Pull(960).Frames);
        Assert.Equal(0, buffer.Pull(960).Frames);
        Assert.Equal(0, buffer.Snapshot.Silence);
        Assert.Throws<ProtocolException>(() => buffer.Push(Data(1), 0, 1));
    }

    [Fact]
    public void RecurrentQuarterSecondGapsAndBurstsStayBounded()
    {
        var buffer = new AudioBuffer();
        var presentation = new AudioPresentation();
        buffer.Start();
        long position = 0;
        for (var cycle = 0; cycle < 100; cycle++)
        {
            for (var gap = 0; gap < 25; gap++)
            {
                presentation.Submit(position, buffer.Pull(480));
                position += 480;
                _ = presentation.Current(position);
            }
            for (var burst = 0; burst < 25; burst++)
                buffer.Push(Data(480), (cycle * 50L + burst) * 10_000, 480);
            Assert.InRange(buffer.Frames, 0, AudioBuffer.MaximumBufferedFrames);
            for (var output = 0; output < 25; output++)
            {
                presentation.Submit(position, buffer.Pull(480));
                position += 480;
                _ = presentation.Current(position);
            }
        }
        Assert.True(buffer.Snapshot.Silence > 0);
        Assert.True(buffer.Snapshot.Dropped > 0);
        Assert.True(presentation.Current(position).ContentFrames > 0);
        Assert.Equal(0, buffer.Frames);
    }

    [Fact]
    public void ResamplingUsesOutputPositionsWithoutRewritingSourceTime()
    {
        var buffer = new AudioBuffer();
        var presentation = new AudioPresentation();
        buffer.Push(Data(980), 0, 960);
        presentation.Submit(0, buffer.Pull(980));
        Assert.Equal((980L, (long?)20_000), presentation.Current(980));
    }

    [Fact]
    public void CompensationHasDeadbandAndFourSecondTwoPercentLimit()
    {
        var control = new AudioRateControl();
        AudioCompensation? result = null;
        for (var index = 0; index < 50; index++)
            result = control.Observe(2400, 960, 960, 0, 0);
        Assert.Equal(new AudioCompensation(0, 0), result);
        for (var index = 0; index < 50; index++)
            result = control.Observe(5520, 960, 960, 48000, 0);
        Assert.NotNull(result);
        Assert.InRange(result.Samples, -3840, -1);
        Assert.Equal(192000, result.Distance);
    }

    [Fact]
    public void CompensationNeverAcceleratesAnAlreadyLowBuffer()
    {
        var control = new AudioRateControl();
        AudioCompensation? result = null;
        for (var index = 0; index < 50; index++)
            result = control.Observe(0, 960, 960, 48000, 0);
        Assert.NotNull(result);
        Assert.True(result.Samples >= 0);
    }

    [Fact]
    public void InvalidBlocksAndPresentationPositionsFailExplicitly()
    {
        var buffer = new AudioBuffer();
        Assert.Throws<ProtocolException>(() => buffer.Push(new byte[3], 0, 1));
        Assert.Throws<ProtocolException>(() => buffer.Push(Data(1), -1, 1));
        var presentation = new AudioPresentation();
        Assert.Throws<ProtocolException>(() => presentation.Submit(0, new(new byte[3], [])));
        Assert.Throws<ProtocolException>(() => presentation.Submit(1, new(Data(1), [])));
    }

    [Fact]
    public void AdaptiveTargetStartsLowAndGrowsOnlyOnActualUnderrun()
    {
        var buffer = new AudioBuffer();
        Assert.Equal(2400, buffer.Target);
        buffer.Start();
        buffer.Push(Data(960), 0, 960);
        _ = buffer.Pull(960);
        Assert.Equal(2400, buffer.Target);
        _ = buffer.Pull(960);
        Assert.Equal(3360, buffer.Target);
        Assert.Equal(960, buffer.Snapshot.Silence);
    }

    [Fact]
    public void AdaptiveTargetAndFifoHaveExactUpperBounds()
    {
        var buffer = new AudioBuffer();
        buffer.Start();
        for (var count = 0; count < 50; count++) _ = buffer.Pull(960);
        Assert.Equal(7200, buffer.Target);
        buffer.Push(Data(12000), 0, 12000);
        Assert.Equal(10800, buffer.Frames);
        Assert.Equal(1200, buffer.Snapshot.Dropped);
    }

    [Fact]
    public void AdaptiveTargetShrinksGraduallyAfterTenSecondsWithoutUnderrun()
    {
        var buffer = new AudioBuffer();
        buffer.Start();
        _ = buffer.Pull(12000);
        Assert.Equal(7200, buffer.Target);
        for (var index = 0; index < 499; index++)
        {
            buffer.Push(Data(960), index * 20000L, 960);
            _ = buffer.Pull(960);
        }
        Assert.Equal(7200, buffer.Target);
        buffer.Push(Data(960), 499 * 20000L, 960);
        _ = buffer.Pull(960);
        Assert.Equal(6720, buffer.Target);
        for (var index = 0; index < 5000; index++)
        {
            buffer.Push(Data(960), (500 + index) * 20000L, 960);
            _ = buffer.Pull(960);
        }
        Assert.Equal(2400, buffer.Target);
    }

    [Fact]
    public void CompensationRegulatesTheAdaptiveTargetNotTheOriginalMinimum()
    {
        var control = new AudioRateControl();
        AudioCompensation? result = null;
        for (var index = 0; index < 50; index++)
            result = control.Observe(7200, 960, 960, 0, 0, 7200);
        Assert.NotNull(result);
        Assert.InRange(result.Samples, 1, 3840);
        Assert.Equal(192000, result.Distance);
        Assert.Throws<ProtocolException>(() => control.Observe(7200, 960, 960, 0, 0, 7201));
    }
}
