using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class ListenerTests
{
    private static ReceiverServer Server(IPlaybackFactory? factory = null) =>
        new("Test receiver", factory ?? new UnavailablePlaybackFactory(), _ => { }, 0, IPAddress.Loopback);

    private static async Task<TcpClient> ConnectAsync(ReceiverServer server)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        return client;
    }

    private static async Task<Packet> ReadAsync(TcpClient client)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await Wire.ReadAsync(client.GetStream(), cancellation.Token);
    }

    private static Task SendAsync(TcpClient client, Packet packet) =>
        Wire.WriteAsync(client.GetStream(), packet).AsTask();

    private static async Task HelloAsync(TcpClient client)
    {
        await SendAsync(client, Packet.Control(PacketType.Hello, 0,
            new { role = "sender", versions = new[] { 3 }, name = "Test sender", sessionCode = "ignored" }));
        Assert.Equal(PacketType.Hello, (await ReadAsync(client)).Type);
    }

    private static async Task RequestAsync(TcpClient client, long session = 1, bool audio = false)
    {
        await SendAsync(client, Packet.Control(PacketType.SessionRequest, session, RequestTests.Settings(audio)));
    }

    [Fact]
    public async Task FoundationRejectsBeforeCaptureWithoutPretendReadiness()
    {
        await using var server = Server();
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        var rejected = await ReadAsync(client);
        Assert.Equal(PacketType.Reject, rejected.Type);
        Assert.Equal(1, rejected.Session);
        Assert.Equal("UNSUPPORTED_CONFIG", JsonObject.Text(rejected.Object(), "code"));
        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task SrtNeverFallsBackToCustomTcp()
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await SendAsync(client, Packet.Control(PacketType.SessionRequest, 1, RequestTests.Settings(false, "aac", new
        {
            kind = "srt",
            container = "fmp4",
            url = "srt://127.0.0.1:1234",
            latencyMs = 100,
            streamId = "sc-0000000000000001"
        })));
        var rejected = await ReadAsync(client);
        Assert.Equal("UNSUPPORTED_CONFIG", JsonObject.Text(rejected.Object(), "code"));
        Assert.Equal(0, factory.Opens);
    }

    [Fact]
    public async Task BusyDoesNotEvictAndStopAcknowledgesMatchingSession()
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var first = await ConnectAsync(server);
        await HelloAsync(first);
        await RequestAsync(first);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(first)).Type);
        Assert.True(server.IsBusy);
        using var other = await ConnectAsync(server);
        await SendAsync(other, Packet.Control(PacketType.Hello, 0,
            new { role = "sender", versions = new[] { 3 }, name = "Other sender" }));
        Assert.Equal("BUSY", JsonObject.Text((await ReadAsync(other)).Object(), "code"));
        await SendAsync(first, Packet.Control(PacketType.Stop, 1, new { reason = "user" }));
        var ended = await ReadAsync(first);
        Assert.Equal(PacketType.SessionEnd, ended.Type);
        Assert.Equal(1, ended.Session);
        Assert.True(factory.Playback.Disposed);
    }

    [Fact]
    public async Task ConcurrentHelloDoesNotAllowTwoActiveSessions()
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var first = await ConnectAsync(server);
        using var second = await ConnectAsync(server);
        await HelloAsync(first);
        await HelloAsync(second);
        await RequestAsync(first);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(first)).Type);
        await RequestAsync(second, 2);
        var rejected = await ReadAsync(second);
        Assert.Equal(2, rejected.Session);
        Assert.Equal("BUSY", JsonObject.Text(rejected.Object(), "code"));
        Assert.Equal(1, factory.Opens);
        await SendAsync(first, Packet.Control(PacketType.Stop, 1, new { reason = "test" }));
        Assert.Equal(PacketType.SessionEnd, (await ReadAsync(first)).Type);
    }

    [Fact]
    public async Task ReadyFollowsActualConfigurationAndMediaRemainsOrdered()
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client, audio: true);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, QueueAndTrackTests.Configuration());
        var ready = await ReadAsync(client);
        Assert.Equal(PacketType.StreamReady, ready.Type);
        Assert.Contains(1u, factory.Playback.Configured);
        await SendAsync(client, QueueAndTrackTests.Configuration(2));
        Assert.Equal(2u, (await ReadAsync(client)).Track);
        await SendAsync(client, QueueAndTrackTests.Media(100));
        await SendAsync(client, QueueAndTrackTests.Media(200));
        await factory.Playback.TwoPresented.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 100L, 200L }, factory.Playback.Presented.ToArray());
        await SendAsync(client, Packet.Control(PacketType.Stop, 1, new { reason = "done" }));
        Assert.Equal(PacketType.SessionEnd, (await ReadAsync(client)).Type);
    }

    [Fact]
    public async Task PingsEchoSignedNonceAndReceiverStopIsAcknowledged()
    {
        await using var server = Server(new TestFactory());
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, Packet.Control(PacketType.Ping, 1, new { nonce = int.MinValue }));
        var pong = await ReadAsync(client);
        Assert.Equal(PacketType.Pong, pong.Type);
        Assert.Equal(int.MinValue, JsonObject.Number(pong.Object(), "nonce"));
        var disconnect = server.DisconnectAsync();
        Assert.Equal(PacketType.Stop, (await ReadAsync(client)).Type);
        await SendAsync(client, Packet.Control(PacketType.SessionEnd, 1, new { reason = "user" }));
        await disconnect.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task MediaBeforeConfigurationFailsExplicitly()
    {
        await using var server = Server(new TestFactory());
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, QueueAndTrackTests.Media(0));
        var error = await ReadAsync(client);
        Assert.Equal(PacketType.Error, error.Type);
        Assert.Equal("PROTOCOL", JsonObject.Text(error.Object(), "code"));
    }

    [Fact]
    public async Task ShutdownCancelsTrickledSetupAndReleasesPort()
    {
        var server = Server();
        server.Start(false);
        var port = server.Port;
        using var client = await ConnectAsync(server);
        await client.GetStream().WriteAsync("SC"u8.ToArray());
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        using var replacement = new TcpListener(IPAddress.Loopback, port);
        replacement.Start();
    }

    [Fact]
    public async Task SetupSupportsPingAndStopBeforeRequest()
    {
        await using var server = Server();
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await SendAsync(client, Packet.Control(PacketType.Ping, 0, new { nonce = -7 }));
        var pong = await ReadAsync(client);
        Assert.Equal(PacketType.Pong, pong.Type);
        Assert.Equal(-7, JsonObject.Number(pong.Object(), "nonce"));
        await SendAsync(client, Packet.Control(PacketType.Stop, 0, new { reason = "cancel setup" }));
        Assert.Equal(PacketType.SessionEnd, (await ReadAsync(client)).Type);
    }

    [Fact]
    public async Task UnacknowledgedReceiverStopClosesWithinTwoSecondBudget()
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        var elapsed = Stopwatch.StartNew();
        var disconnect = server.DisconnectAsync();
        Assert.Equal(PacketType.Stop, (await ReadAsync(client)).Type);
        await disconnect.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 1.8, 3.5);
        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task TrickledSetupDoesNotExtendOverallTenSecondDeadline()
    {
        await using var server = Server();
        server.Start(false);
        using var client = await ConnectAsync(server);
        var elapsed = Stopwatch.StartNew();
        await client.GetStream().WriteAsync("S"u8.ToArray());
        await Task.Delay(TimeSpan.FromSeconds(7));
        await client.GetStream().WriteAsync("C"u8.ToArray());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var rejected = await Wire.ReadAsync(client.GetStream(), timeout.Token);
        Assert.Equal("TIMEOUT", JsonObject.Text(rejected.Object(), "code"));
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 9.0, 13.0);
    }

    [Fact]
    public async Task UnsolicitedSessionEndIsAProtocolError()
    {
        await using var server = Server(new TestFactory());
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, Packet.Control(PacketType.SessionEnd, 1, new { reason = "not a STOP acknowledgement" }));
        Assert.Equal("PROTOCOL", JsonObject.Text((await ReadAsync(client)).Object(), "code"));
    }

    [Fact]
    public async Task SimultaneousShutdownCallersAwaitTheSameCleanup()
    {
        var server = Server();
        server.Start(false);
        using var client = await ConnectAsync(server);
        await client.GetStream().WriteAsync("SC"u8.ToArray());
        await Task.WhenAll(server.DisposeAsync().AsTask(), server.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ReadyTracksAndLivePongsDoNotMasqueradeAsRenderedVideo()
    {
        await using var server = Server(new TestFactory());
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        var elapsed = Stopwatch.StartNew();
        await SendAsync(client, QueueAndTrackTests.Configuration());
        Assert.Equal(PacketType.StreamReady, (await ReadAsync(client)).Type);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(13));
        while (true)
        {
            var packet = await Wire.ReadAsync(client.GetStream(), deadline.Token);
            if (packet.Type == PacketType.Ping)
            {
                await SendAsync(client, Packet.Control(PacketType.Pong, 1, new { nonce = JsonObject.Number(packet.Object(), "nonce") }));
                continue;
            }
            Assert.Equal(PacketType.Error, packet.Type);
            Assert.Equal("TIMEOUT", JsonObject.Text(packet.Object(), "code"));
            break;
        }
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 9.0, 12.0);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public async Task TcpReconfigurationCannotExtendInitialVideoDeadline(uint track)
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client, audio: track == 2);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        var elapsed = Stopwatch.StartNew();
        await SendAsync(client, QueueAndTrackTests.Configuration());
        Assert.Equal(PacketType.StreamReady, (await ReadAsync(client)).Type);
        if (track == 2)
        {
            await SendAsync(client, QueueAndTrackTests.Configuration(2));
            Assert.Equal(PacketType.StreamReady, (await ReadAsync(client)).Type);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(13));
        uint generation = 1;
        var pings = 0;
        while (true)
        {
            var packet = await Wire.ReadAsync(client.GetStream(), deadline.Token);
            if (packet.Type == PacketType.StreamReady)
            {
                Assert.Equal(track, packet.Track);
                Assert.Equal(generation, packet.Generation);
                continue;
            }
            if (packet.Type == PacketType.Ping)
            {
                pings++;
                await SendAsync(client, Packet.Control(PacketType.Pong, 1, new { nonce = JsonObject.Number(packet.Object(), "nonce") }));
                await SendAsync(client, QueueAndTrackTests.Configuration(track, ++generation));
                continue;
            }
            Assert.Equal(PacketType.Error, packet.Type);
            Assert.Equal("TIMEOUT", JsonObject.Text(packet.Object(), "code"));
            Assert.True(JsonObject.Boolean(packet.Object(), "fatal"));
            break;
        }
        Assert.True(pings >= 3);
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 9, 12);
        var cleanup = Stopwatch.StartNew();
        while (server.IsBusy && cleanup.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
        Assert.False(server.IsBusy);
        Assert.True(factory.Playback.Disposed);
    }

    [Fact]
    public async Task TcpPresentationHistorySurvivesSubsequentReconfiguration()
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, QueueAndTrackTests.Configuration());
        Assert.Equal(PacketType.StreamReady, (await ReadAsync(client)).Type);
        await SendAsync(client, QueueAndTrackTests.Media(0));
        await SendAsync(client, QueueAndTrackTests.Media(33_333));
        await factory.Playback.TwoPresented.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.Playback.SuppressCurrentPresentation = true;
        Assert.False(factory.Playback.HasPresentedVideo);
        Assert.True(factory.Playback.HasEverPresentedVideo);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var elapsed = Stopwatch.StartNew();
        uint generation = 2;
        await SendAsync(client, QueueAndTrackTests.Configuration(generation: generation));
        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(10_500))
        {
            var packet = await Wire.ReadAsync(client.GetStream(), deadline.Token);
            if (packet.Type == PacketType.StreamReady) continue;
            Assert.Equal(PacketType.Ping, packet.Type);
            await SendAsync(client, Packet.Control(PacketType.Pong, 1, new { nonce = JsonObject.Number(packet.Object(), "nonce") }));
            await SendAsync(client, QueueAndTrackTests.Configuration(generation: ++generation));
        }
        Assert.True(server.IsBusy);
        await SendAsync(client, Packet.Control(PacketType.Stop, 1, new { reason = "post-start-reconfiguration" }));
        while (true)
        {
            var packet = await ReadAsync(client);
            if (packet.Type == PacketType.StreamReady) continue;
            Assert.Equal(PacketType.SessionEnd, packet.Type);
            break;
        }
        Assert.True(factory.Playback.Disposed);
    }

    [Fact]
    public async Task PlaybackHealthFailureIsTerminalEvenWithoutNewMedia()
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, QueueAndTrackTests.Configuration());
        Assert.Equal(PacketType.StreamReady, (await ReadAsync(client)).Type);
        factory.Playback.HealthFailure = new IOException("GPU resize failed");
        var error = await ReadAsync(client);
        Assert.Equal(PacketType.Error, error.Type);
        Assert.Equal("DECODER_FAILED", JsonObject.Text(error.Object(), "code"));
        Assert.Contains("GPU resize failed", JsonObject.Text(error.Object(), "message", 2048));
    }

    [Fact]
    public void DiscoveryMatchesPairedContractWithoutHostOrCodes()
    {
        var json = JsonObject.Parse(Discovery.Advertisement("Test", 27184, true));
        Assert.Equal("screencopy", JsonObject.Text(json, "protocol"));
        Assert.Equal(3, JsonObject.Number(json, "version"));
        Assert.Equal("busy", JsonObject.Text(json, "state"));
        Assert.Equal(5, json.EnumerateObject().Count());
    }

    [Fact]
    public async Task RepeatedConnectionsAndListenerRestartsReleaseOwnership()
    {
        for (var index = 0; index < 10; index++)
        {
            await using var server = Server();
            server.Start(false);
            using var client = await ConnectAsync(server);
            await HelloAsync(client);
            await RequestAsync(client);
            Assert.Equal(PacketType.Reject, (await ReadAsync(client)).Type);
        }
    }

    private sealed class TestFactory : IPlaybackFactory
    {
        public string Description => "TEST ONLY - no real rendering.";
        public TestPlayback Playback { get; } = new();
        public int Opens { get; private set; }
        public ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken cancellationToken)
        {
            Opens++;
            return ValueTask.FromResult<IPlayback>(Playback);
        }
    }

    [Fact]
    public async Task RemoteStopSuppressesHealthChecksWhilePlaybackRetires()
    {
        var factory = new TestFactory();
        var retiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Playback.OnDispose = () =>
        {
            factory.Playback.HealthFailure = new ProtocolException("Retired output must not be health-checked.", "AUDIO_FAILED");
            retiring.TrySetResult();
            return new ValueTask(release.Task);
        };
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, Packet.Control(PacketType.Stop, 1, new { reason = "user" }));
        await retiring.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try { await Task.Delay(250); }
        finally { release.TrySetResult(); }
        Assert.Equal(PacketType.SessionEnd, (await ReadAsync(client)).Type);
    }

    private sealed class TestPlayback : IPlayback, IVideoPlaybackProgress
    {
        public bool Disposed { get; private set; }
        public bool SuppressCurrentPresentation { get; set; }
        public bool HasPresentedVideo => !SuppressCurrentPresentation && !Presented.IsEmpty;
        public bool HasEverPresentedVideo => !Presented.IsEmpty;
        public Exception? HealthFailure { get; set; }
        public Func<ValueTask>? OnDispose { get; set; }
        public void CheckHealth()
        {
            if (HealthFailure is { } failure) throw failure;
        }
        public ConcurrentBag<uint> Configured { get; } = [];
        public ConcurrentQueue<long> Presented { get; } = [];
        public TaskCompletionSource TwoPresented { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask ConfigureAsync(Packet configuration, CancellationToken cancellationToken)
        {
            Configured.Add(configuration.Track);
            return ValueTask.CompletedTask;
        }
        public ValueTask PresentAsync(Packet media, CancellationToken cancellationToken)
        {
            Presented.Enqueue(media.PtsUs);
            if (Presented.Count == 2) TwoPresented.TrySetResult();
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return OnDispose?.Invoke() ?? ValueTask.CompletedTask;
        }
    }
}
