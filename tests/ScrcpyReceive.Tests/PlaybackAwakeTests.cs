using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class PlaybackAwakeTests
{
    private sealed class Lease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    [Fact]
    public void IdleDoesNotAcquireAndRepeatedPlaybackDoesNotDuplicateRequests()
    {
        var acquired = 0;
        var released = 0;
        using var awake = new PlaybackAwake(() => { acquired++; return new Lease(() => released++); });
        awake.SetPlaying(false);
        Assert.Equal(0, acquired);
        awake.SetPlaying(true);
        awake.SetPlaying(true);
        Assert.True(awake.IsActive);
        Assert.Equal(1, acquired);
        awake.SetPlaying(false);
        awake.SetPlaying(false);
        Assert.False(awake.IsActive);
        Assert.Equal(1, released);
    }

    [Fact]
    public void StopThenRecastGetsAFreshLeaseAndCloseReleasesIt()
    {
        var acquired = 0;
        var released = 0;
        var awake = new PlaybackAwake(() => { acquired++; return new Lease(() => released++); });
        awake.SetPlaying(true);
        awake.SetPlaying(false);
        awake.SetPlaying(true);
        awake.Dispose();
        awake.Dispose();
        Assert.Equal(2, acquired);
        Assert.Equal(2, released);
        Assert.False(awake.IsActive);
        Assert.Throws<ObjectDisposedException>(() => awake.SetPlaying(true));
    }

    [Fact]
    public void AcquisitionFailureIsExplicitAndDoesNotPretendToBeActive()
    {
        using var awake = new PlaybackAwake(() => throw new InvalidOperationException("request failed"));
        Assert.Throws<InvalidOperationException>(() => awake.SetPlaying(true));
        Assert.False(awake.IsActive);
    }

    [Fact]
    public void ReleaseFailureIsExplicitAndDoesNotRetainTheLease()
    {
        var awake = new PlaybackAwake(() => new Lease(() => throw new InvalidOperationException("clear failed")));
        awake.SetPlaying(true);
        Assert.Throws<InvalidOperationException>(() => awake.SetPlaying(false));
        Assert.False(awake.IsActive);
        awake.Dispose();
    }
}
