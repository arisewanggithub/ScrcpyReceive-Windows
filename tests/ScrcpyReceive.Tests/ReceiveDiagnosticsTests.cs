using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class ReceiveDiagnosticsTests
{
    private static Packet Media(uint track, long pts, int bytes = 4) =>
        new(track == 1 ? PacketType.VideoFrame : PacketType.AudioPacket, 1, track, 1, pts, 0, new byte[bytes]);

    [Fact]
    public void EmptyTracksAreNotReportedAsRecentTraffic()
    {
        var diagnostics = new ReceiveDiagnostics(() => 1234);
        Assert.Contains("track=1 received=0 bytes=0 lastPts=0 ageMs=-1", diagnostics.Diagnostics);
        Assert.Contains("track=2 received=0 bytes=0 lastPts=0 ageMs=-1", diagnostics.Diagnostics);
    }

    [Fact]
    public void ArrivalGapsAndReadWaitsAreIndependentFromSourcePts()
    {
        long at = 1000;
        var diagnostics = new ReceiveDiagnostics(() => at);
        diagnostics.Received(Media(2, 900_000), 20);
        at += 1200;
        diagnostics.Received(Media(1, 920_000, 100), 1100);
        at += 1;
        diagnostics.Received(Media(2, 910_000), 1);
        at += 29;
        Assert.Contains("track=1 received=1 bytes=100 lastPts=920000 ageMs=30 gapMaxMs=0 readMaxMs=1100",
            diagnostics.Diagnostics);
        Assert.Contains("track=2 received=2 bytes=8 lastPts=910000 ageMs=29 gapMaxMs=1201 readMaxMs=20",
            diagnostics.Diagnostics);
    }

    [Fact]
    public void ControlTrafficDoesNotHideAnAudioArrivalGap()
    {
        long at = 0;
        var diagnostics = new ReceiveDiagnostics(() => at);
        diagnostics.Received(Media(2, 50), 0);
        at = 1000;
        diagnostics.Received(Packet.Control(PacketType.Ping, 1, new { nonce = 1 }), 1000);
        Assert.Contains("track=2 received=1 bytes=4 lastPts=50 ageMs=1000 gapMaxMs=0 readMaxMs=0",
            diagnostics.Diagnostics);
    }

    [Fact]
    public void OpusPtsReversalsDoNotChangeArrivalOrder()
    {
        long at = 0;
        var diagnostics = new ReceiveDiagnostics(() => at);
        diagnostics.Received(Media(2, 40_000), 0);
        at = 20;
        diagnostics.Received(Media(2, 20_000), 20);
        diagnostics.Handled(150);
        diagnostics.Handled(1);
        Assert.Contains("handlingMaxMs=150", diagnostics.Diagnostics);
        Assert.Contains("lastPts=20000 ageMs=0 gapMaxMs=20", diagnostics.Diagnostics);
    }

    [Fact]
    public void NegativeDurationsFailExplicitly()
    {
        var diagnostics = new ReceiveDiagnostics();
        Assert.Throws<ArgumentOutOfRangeException>(() => diagnostics.Received(Media(1, 0), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => diagnostics.Handled(-1));
    }
}
