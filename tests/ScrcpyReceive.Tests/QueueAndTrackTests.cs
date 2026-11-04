using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class QueueAndTrackTests
{
    internal static Packet Media(long pts, uint track = 1, uint generation = 1, int size = 4) =>
        new(track == 1 ? PacketType.VideoFrame : PacketType.AudioPacket, 1, track, generation, pts, 0, new byte[size]);

    internal static Packet Configuration(uint track = 1, uint generation = 1, string codec = "raw")
    {
        var json = track == 1
            ? Packet.Control(PacketType.StreamConfig, 1, new { codec = "h264", width = 1280, height = 720, csd = new[] { "AAAAAQ==" } })
            : Packet.Control(PacketType.StreamConfig, 1, new
            {
                codec, sampleRate = 48000, channels = 2,
                csd = codec switch { "aac" => new[] { "EZA=" }, "opus" => ["T3B1c0hlYWQBAjgBgLsAAAAAAA=="], _ => [] },
                sampleFormat = "s16le", codecDelayNs = 6500000, seekPrerollNs = 80000000
            });
        return json with { Track = track, Generation = generation };
    }

    [Fact]
    public async Task QueueIsFifoAndRestoresBudgetOnRemoval()
    {
        using var queue = new ReceiveQueue();
        queue.Add(Media(1, size: ReceiveQueue.MaximumBytes));
        Assert.Equal("CONGESTION", Assert.Throws<ProtocolException>(() => queue.Add(Media(1))).Code);
        var first = await queue.TakeAsync(1, default);
        Assert.Equal(ReceiveQueue.MaximumBytes, first.Payload.Length);
        Assert.Throws<ProtocolException>(() => queue.Add(Media(1)));
        queue.Complete(first);
        queue.Add(Media(2, size: ReceiveQueue.MaximumBytes));
        var second = await queue.TakeAsync(1, default);
        Assert.Equal(2, second.PtsUs);
        queue.Complete(second);
    }

    [Fact]
    public void SpanUsesMaximumMinusMinimumNotArrivalEndpoints()
    {
        using var queue = new ReceiveQueue();
        queue.Add(Media(500_000, 2));
        queue.Add(Media(0, 2));
        queue.Add(Media(250_000, 2));
        Assert.Equal("CONGESTION", Assert.Throws<ProtocolException>(() => queue.Add(Media(500_001, 2))).Code);
    }

    [Fact]
    public void SpanIsSeparatePerTrackAndExactBoundaryIsAccepted()
    {
        using var queue = new ReceiveQueue();
        queue.Add(Media(0));
        queue.Add(Media(500_000));
        queue.Add(Media(9_000_000, 2));
        queue.Add(Media(9_500_000, 2));
    }

    [Fact]
    public void ConfigurationsAreBoundedAndDoNotContributePtsSpan()
    {
        using var queue = new ReceiveQueue();
        for (var index = 0; index < 64; index++) queue.Add(Configuration());
        queue.Add(Media(9_000_000));
        Assert.Throws<ProtocolException>(() => queue.Add(Configuration()));
    }

    [Fact]
    public void EntryCountIsExplicitlyBounded()
    {
        using var queue = new ReceiveQueue();
        for (var index = 0; index < ReceiveQueue.MaximumEntries; index++) queue.Add(Media(0, size: 1));
        Assert.Throws<ProtocolException>(() => queue.Add(Media(0, size: 1)));
    }

    [Fact]
    public async Task WaitingQueueHonorsCancellation()
    {
        using var queue = new ReceiveQueue();
        using var cancellation = new CancellationTokenSource();
        var wait = queue.TakeAsync(1, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
    }

    [Fact]
    public async Task AudioCanProgressWhileVideoConsumerIsWaiting()
    {
        using var queue = new ReceiveQueue();
        queue.Add(Media(0));
        queue.Add(Media(0, 2));
        var video = await queue.TakeAsync(1, default);
        var audio = await queue.TakeAsync(2, default);
        Assert.Equal(2u, audio.Track);
        queue.Complete(audio);
        queue.Add(Media(20_000, 2));
        var next = await queue.TakeAsync(2, default);
        Assert.Equal(20_000, next.PtsUs);
        queue.Complete(next);
        queue.Complete(video);
        Assert.Throws<InvalidOperationException>(() => queue.Complete(video));
    }

    [Fact]
    public void ProductionQueueReservesSpaceForNativePacketCopy()
    {
        using var queue = new ReceiveQueue(4 * 1024 * 1024);
        queue.Add(Media(0, size: 4 * 1024 * 1024));
        var error = Assert.Throws<ProtocolException>(() => queue.Add(Media(0)));
        Assert.Equal("CONGESTION", error.Code);
        Assert.Contains("4194304", error.Message);
    }

    [Fact]
    public void AllInitialTracksMustBeReadyBeforeMedia()
    {
        var state = new TrackState(RequestTests.Request());
        state.Configure(Configuration());
        state.MarkReady(1);
        Assert.Throws<ProtocolException>(() => state.Media(Media(0)));
        state.Configure(Configuration(2));
        state.MarkReady(2);
        state.Media(Media(0));
    }

    [Theory]
    [InlineData("raw", false)]
    [InlineData("aac", false)]
    [InlineData("opus", true)]
    public void OnlyExactOpusCodecPermitsNonnegativeTimestampReversals(string codec, bool allows)
    {
        var state = new TrackState(RequestTests.Request(codec: codec));
        state.Configure(Configuration());
        state.MarkReady(1);
        state.Configure(Configuration(2, codec: codec));
        state.MarkReady(2);
        state.Media(Media(100, 2));
        if (allows) state.Media(Media(0, 2));
        else Assert.Throws<ProtocolException>(() => state.Media(Media(0, 2)));
    }

    [Fact]
    public void GenerationChangesAreSequentialAndDoNotResetOtherTrack()
    {
        var state = new TrackState(RequestTests.Request());
        state.Configure(Configuration());
        state.MarkReady(1);
        state.Configure(Configuration(2));
        state.MarkReady(2);
        Assert.Throws<ProtocolException>(() => state.Configure(Configuration(generation: 3)));
        state.Configure(Configuration(generation: 2));
        state.Media(Media(0, 2));
        Assert.Throws<ProtocolException>(() => state.Media(Media(0)));
        state.MarkReady(1);
        state.Media(Media(0, generation: 2));
        Assert.Throws<ProtocolException>(() => state.Media(Media(0)));
    }

    [Fact]
    public void PcmMustBeStereoSampleAligned()
    {
        var state = new TrackState(RequestTests.Request());
        state.Configure(Configuration());
        state.MarkReady(1);
        state.Configure(Configuration(2));
        state.MarkReady(2);
        Assert.Throws<ProtocolException>(() => state.Media(Media(0, 2, size: 3)));
    }
}
