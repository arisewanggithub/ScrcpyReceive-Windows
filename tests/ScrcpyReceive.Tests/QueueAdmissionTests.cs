using System.Diagnostics;
using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class QueueAdmissionTests
{
    private static Packet Video(long pts, int bytes = 4) => new(PacketType.VideoFrame, 1, 1, 1, pts, 0, new byte[bytes]);

    [Fact]
    public async Task BurstAdmissionWaitsForSpaceWithoutIncreasingTheSpanLimit()
    {
        using var queue = new ReceiveQueue();
        var first = Video(0);
        var next = Video(500_001);
        queue.Add(first);
        var admission = queue.AddAsync(next, CancellationToken.None).AsTask();
        Assert.False(admission.IsCompleted);
        Assert.Equal("CONGESTION", Assert.Throws<ProtocolException>(() => queue.Add(next)).Code);
        var taken = await queue.TakeAsync(1, CancellationToken.None);
        await admission.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Complete(taken);
        var admitted = await queue.TakeAsync(1, CancellationToken.None);
        Assert.Same(next, admitted);
        queue.Complete(admitted);
    }

    [Fact]
    public async Task BytesRemainChargedUntilTheInflightPacketCompletes()
    {
        using var queue = new ReceiveQueue(8);
        queue.Add(Video(0, 8));
        var taken = await queue.TakeAsync(1, CancellationToken.None);
        var next = Video(1, 4);
        var admission = queue.AddAsync(next, CancellationToken.None).AsTask();
        Assert.False(admission.IsCompleted);
        queue.Complete(taken);
        await admission.WaitAsync(TimeSpan.FromSeconds(2));
        var admitted = await queue.TakeAsync(1, CancellationToken.None);
        queue.Complete(admitted);
    }

    [Fact]
    public async Task IrreduciblyOversizedPacketFailsImmediately()
    {
        using var queue = new ReceiveQueue(4);
        var admission = queue.AddAsync(Video(0, 8), CancellationToken.None).AsTask();
        Assert.True(admission.IsCompleted);
        Assert.Equal("CONGESTION", (await Assert.ThrowsAsync<ProtocolException>(() => admission)).Code);
    }

    [Fact]
    public async Task SustainedCongestionStillFailsAfterTheBoundedAdmissionDeadline()
    {
        using var queue = new ReceiveQueue();
        queue.Add(Video(0));
        var timer = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<ProtocolException>(() => queue.AddAsync(Video(500_001), CancellationToken.None).AsTask());
        Assert.Equal("CONGESTION", failure.Code);
        Assert.InRange(timer.ElapsedMilliseconds, 450, 1500);
    }

    [Fact]
    public async Task CancellationDoesNotAdmitAPacketOrBecomeCongestion()
    {
        using var queue = new ReceiveQueue();
        var first = Video(0);
        queue.Add(first);
        using var cancellation = new CancellationTokenSource();
        var admission = queue.AddAsync(Video(500_001), cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admission);
        var taken = await queue.TakeAsync(1, CancellationToken.None);
        Assert.Same(first, taken);
        queue.Complete(taken);
    }

    [Fact]
    public async Task AlreadyCanceledAdmissionDoesNotInsertEvenWhenSpaceExists()
    {
        using var queue = new ReceiveQueue();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.AddAsync(Video(0), cancellation.Token).AsTask());
        var first = Video(500_001);
        queue.Add(first);
        var taken = await queue.TakeAsync(1, CancellationToken.None);
        Assert.Same(first, taken);
        queue.Complete(taken);
    }

    [Fact]
    public async Task QueueDwellIsMeasuredSeparatelyFromWaitingForInput()
    {
        using var queue = new ReceiveQueue();
        var pending = queue.TakeAsync(1, CancellationToken.None).AsTask();
        await Task.Delay(30);
        var afterAdmission = Stopwatch.StartNew();
        queue.Add(Video(0));
        var packet = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        var first = System.Text.RegularExpressions.Regex.Match(queue.Diagnostics, @"videoWaitMaxUs=(\d+)");
        Assert.True(first.Success);
        Assert.InRange(long.Parse(first.Groups[1].Value), 0, (long)afterAdmission.Elapsed.TotalMicroseconds);
        queue.Complete(packet);
        queue.Add(Video(1));
        await Task.Delay(30);
        packet = await queue.TakeAsync(1, CancellationToken.None);
        var match = System.Text.RegularExpressions.Regex.Match(queue.Diagnostics, @"videoWaitMaxUs=(\d+)");
        Assert.True(match.Success);
        Assert.True(long.Parse(match.Groups[1].Value) >= 20_000);
        Assert.Contains("audioWaitMaxUs=0", queue.Diagnostics);
        queue.Complete(packet);
    }
}
