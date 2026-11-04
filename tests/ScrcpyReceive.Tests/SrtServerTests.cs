using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Threading.Channels;
using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class SrtServerTests
{
    private sealed class Playback : ISrtPlayback
    {
        public bool HasPresentedVideo { get; set; }
        public long PlayedAudioFrames { get; set; }
        public bool Disposed { get; private set; }
        public string AudioCodec { get; set; } = "aac";
        public bool ReconfigureBeforeReady { get; set; }
        public bool BlockAudioReconfiguration { get; set; }
        public Channel<Packet> ConfigurationUpdates { get; } = Channel.CreateBounded<Packet>(1);
        public TaskCompletionSource AudioReconfiguring { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAudioReconfiguration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AudioReconfigured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? InputFailure { get; set; }
        public TaskCompletionSource Configured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InputCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask ConfigureAsync(Packet packet, CancellationToken token)
        {
            if (packet.Track != 2 || packet.Generation == 1) return;
            AudioReconfiguring.TrySetResult();
            if (BlockAudioReconfiguration) await ReleaseAudioReconfiguration.Task.WaitAsync(token);
            PlayedAudioFrames = 0;
            AudioReconfigured.TrySetResult();
        }
        public ValueTask PresentAsync(Packet packet, CancellationToken token) => ValueTask.CompletedTask;
        public async Task ReceiveSrtAsync(Func<Packet, CancellationToken, ValueTask> emit, CancellationToken token)
        {
            try
            {
                await emit(QueueAndTrackTests.Configuration(), token);
                await emit(QueueAndTrackTests.Configuration(2, codec: AudioCodec), token);
                Configured.TrySetResult();
                if (InputFailure is { } failure) throw failure;
                if (ReconfigureBeforeReady)
                {
                    for (uint generation = 2; ; generation++)
                    {
                        await Task.Delay(1000, token);
                        await emit(QueueAndTrackTests.Configuration(generation: generation), token);
                    }
                }
                await foreach (var packet in ConfigurationUpdates.Reader.ReadAllAsync(token))
                    await emit(packet, token);
            }
            finally { InputCanceled.TrySetResult(); }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Factory : IPlaybackFactory
    {
        public bool SupportsSrt => true;
        public string Description => "TEST ONLY SRT playback.";
        public Playback Player { get; } = new();
        public ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken token)
        {
            Player.AudioCodec = request.AudioCodec!;
            return ValueTask.FromResult<IPlayback>(Player);
        }
    }

    private static async Task Send(TcpClient client, Packet packet) => await Wire.WriteAsync(client.GetStream(), packet);
    private static async Task<Packet> Read(TcpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        return await Wire.ReadAsync(client.GetStream(), timeout.Token);
    }
    private static async Task<TcpClient> Connect(ReceiverServer server, string audioCodec = "aac")
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await Send(client, Packet.Control(PacketType.Hello, 0, new { role = "sender", versions = new[] { 3 }, name = "Test" }));
        Assert.Equal(PacketType.Hello, (await Read(client)).Type);
        await Send(client, Packet.Control(PacketType.SessionRequest, 1, RequestTests.Settings(true, audioCodec, new
        {
            kind = "srt", container = "fmp4", url = "srt://127.0.0.1:1234", latencyMs = 100, streamId = "sc-0000000000000001"
        })));
        var accepted = await Read(client);
        Assert.Equal(PacketType.SessionAccept, accepted.Type);
        Assert.Equal("srt", JsonObject.Text(accepted.Object().GetProperty("mediaTransport"), "kind"));
        return client;
    }

    [Theory]
    [InlineData("aac")]
    [InlineData("opus")]
    public async Task SrtReadinessRequiresPresentedVideoAndAdvancingSourceAudio(string audioCodec)
    {
        var factory = new Factory();
        await using var server = new ReceiverServer("Test", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        using var client = await Connect(server, audioCodec);
        await factory.Player.Configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.Player.PlayedAudioFrames = 480;
        Assert.False(client.Client.Poll(150_000, SelectMode.SelectRead));
        factory.Player.HasPresentedVideo = true;
        Assert.False(client.Client.Poll(200_000, SelectMode.SelectRead));
        factory.Player.PlayedAudioFrames = 960;
        var playing = await Read(client);
        Assert.Equal(PacketType.Status, playing.Type);
        Assert.Equal("srt-playing", JsonObject.Text(playing.Object(), "state"));
        await Send(client, Packet.Control(PacketType.Stop, 1, new { reason = "user" }));
        Assert.Equal(PacketType.SessionEnd, (await Read(client)).Type);
        Assert.True(factory.Player.InputCanceled.Task.IsCompleted);
        Assert.True(factory.Player.Disposed);
    }

    [Theory]
    [InlineData("aac")]
    [InlineData("opus")]
    public async Task SrtAudioGenerationResetUsesItsOwnReadinessBaseline(string codec)
    {
        var factory = new Factory();
        factory.Player.BlockAudioReconfiguration = true;
        await using var server = new ReceiverServer("Test", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        using var client = await Connect(server, codec);
        await factory.Player.Configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.Player.PlayedAudioFrames = 480_000;
        factory.Player.HasPresentedVideo = true;
        await Task.Delay(250);
        Assert.False(client.Client.Poll(0, SelectMode.SelectRead));
        await factory.Player.ConfigurationUpdates.Writer.WriteAsync(QueueAndTrackTests.Configuration(2, 2, codec));
        await factory.Player.AudioReconfiguring.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            factory.Player.PlayedAudioFrames = 480_960;
            Assert.False(client.Client.Poll(200_000, SelectMode.SelectRead));
        }
        finally { factory.Player.ReleaseAudioReconfiguration.TrySetResult(); }
        await factory.Player.AudioReconfigured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(200);
        Assert.False(client.Client.Poll(0, SelectMode.SelectRead));
        factory.Player.PlayedAudioFrames = 480;
        var playing = await Read(client);
        Assert.Equal(PacketType.Status, playing.Type);
        Assert.Equal("srt-playing", JsonObject.Text(playing.Object(), "state"));
        await Send(client, Packet.Control(PacketType.Stop, 1, new { reason = "audio-generation-ready" }));
        Assert.Equal(PacketType.SessionEnd, (await Read(client)).Type);
        Assert.True(factory.Player.Disposed);
    }

    [Fact]
    public async Task SrtStaticPlaybackRemainsAliveAfterInitialReadiness()
    {
        var factory = new Factory();
        await using var server = new ReceiverServer("Test", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        using var client = await Connect(server);
        await factory.Player.Configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.Player.PlayedAudioFrames = 480;
        factory.Player.HasPresentedVideo = true;
        await Task.Delay(250);
        factory.Player.PlayedAudioFrames = 960;
        var playing = await Read(client);
        Assert.Equal(PacketType.Status, playing.Type);
        Assert.Equal("srt-playing", JsonObject.Text(playing.Object(), "state"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(10_500))
        {
            var packet = await Wire.ReadAsync(client.GetStream(), deadline.Token);
            Assert.Equal(PacketType.Ping, packet.Type);
            await Send(client, Packet.Control(PacketType.Pong, 1, new { nonce = JsonObject.Number(packet.Object(), "nonce") }));
        }
        Assert.True(server.IsBusy);
        await Send(client, Packet.Control(PacketType.Stop, 1, new { reason = "static-playback-complete" }));
        Assert.Equal(PacketType.SessionEnd, (await Read(client)).Type);
        Assert.True(factory.Player.Disposed);
    }

    [Theory]
    [InlineData("aac", 0, false)]
    [InlineData("opus", 480, true)]
    public async Task SrtInitialReadinessTimesOutWithoutAdvancingAudio(string codec, long position, bool reconfigure)
    {
        var factory = new Factory();
        factory.Player.ReconfigureBeforeReady = reconfigure;
        await using var server = new ReceiverServer("Test", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        using var client = await Connect(server, codec);
        await factory.Player.Configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.Player.PlayedAudioFrames = position;
        factory.Player.HasPresentedVideo = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(13));
        var elapsed = Stopwatch.StartNew();
        var pings = 0;
        while (true)
        {
            var packet = await Wire.ReadAsync(client.GetStream(), deadline.Token);
            if (packet.Type == PacketType.Ping)
            {
                pings++;
                await Send(client, Packet.Control(PacketType.Pong, 1, new { nonce = JsonObject.Number(packet.Object(), "nonce") }));
                continue;
            }
            Assert.Equal(PacketType.Error, packet.Type);
            Assert.Equal("TIMEOUT", JsonObject.Text(packet.Object(), "code"));
            Assert.True(JsonObject.Boolean(packet.Object(), "fatal"));
            break;
        }
        Assert.True(pings >= 3, "Control traffic must remain healthy while initial audio readiness expires.");
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 9, 12);
        await factory.Player.InputCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cleanup = Stopwatch.StartNew();
        while (server.IsBusy && cleanup.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
        Assert.False(server.IsBusy);
        Assert.True(factory.Player.Disposed);
    }

    [Fact]
    public async Task SrtSessionsRejectTcpMediaInsteadOfFallingBack()
    {
        var factory = new Factory();
        await using var server = new ReceiverServer("Test", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        using var client = await Connect(server);
        await Send(client, QueueAndTrackTests.Configuration());
        var error = await Read(client);
        Assert.Equal(PacketType.Error, error.Type);
        Assert.Equal("PROTOCOL", JsonObject.Text(error.Object(), "code"));
    }

    [Fact]
    public async Task SrtTransportFailureIsFatalAndReleasesPlayback()
    {
        var factory = new Factory();
        factory.Player.InputFailure = new IOException("Synthetic SRT receive failure");
        await using var server = new ReceiverServer("Test", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        using var client = await Connect(server);
        var error = await Read(client);
        Assert.Equal(PacketType.Error, error.Type);
        Assert.True(JsonObject.Boolean(error.Object(), "fatal"));
        Assert.Equal("DECODER_FAILED", JsonObject.Text(error.Object(), "code"));
        var until = DateTime.UtcNow.AddSeconds(2);
        while (server.IsBusy && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.False(server.IsBusy);
        Assert.True(factory.Player.Disposed);
    }

    [Fact]
    public async Task SrtEofJustBeforeControlStopDoesNotInventAFatalError()
    {
        var factory = new Factory();
        factory.Player.InputFailure = new EndOfStreamException("Synthetic peer closed for STOP");
        await using var server = new ReceiverServer("Test", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        using var client = await Connect(server);
        await factory.Player.Configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Send(client, Packet.Control(PacketType.Stop, 1, new { reason = "user" }));
        Assert.Equal(PacketType.SessionEnd, (await Read(client)).Type);
        Assert.True(factory.Player.Disposed);
    }
}
