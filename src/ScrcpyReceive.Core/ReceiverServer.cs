using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Collections.Concurrent;

namespace ScrcpyReceive.Core;

public sealed record ReceiverNotice(string State, string Detail, IReadOnlyList<string>? ListeningEndpoints = null);

public sealed class ReceiverServer : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly string name;
    private readonly IPlaybackFactory factory;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Connection> connections = [];
    private readonly Action<ReceiverNotice> notify;
    private Connection? active;
    private Task? acceptTask;
    private Task? discoveryTask;
    private Task? shutdownTask;
    private bool started;
    private bool stopped;

    public ReceiverServer(string name, IPlaybackFactory factory, Action<ReceiverNotice> notify,
        int port = 27184, IPAddress? bindAddress = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
            throw new ArgumentException("Receiver name requires 1..80 characters without controls.", nameof(name));
        this.name = name;
        this.factory = factory;
        this.notify = notify;
        listener = new TcpListener(bindAddress ?? IPAddress.Any, port);
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public bool IsBusy { get { lock (gate) return active is not null; } }

    public void Start(bool advertise = true)
    {
        lock (gate)
        {
            if (started || stopped) throw new InvalidOperationException("Create a fresh listener for each start.");
            listener.Start(32);
            started = true;
            acceptTask = Task.Run(AcceptAsync);
            discoveryTask = advertise ? Task.Run(AdvertiseAsync) : Task.CompletedTask;
        }
        notify(new("Listening", $"TCP {Port}. {factory.Description}"));
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                client.NoDelay = true;
                Connection connection;
                lock (gate)
                {
                    if (connections.Count >= 32)
                    {
                        client.Dispose();
                        notify(new("Connection limit", "At most 32 control connections are allowed."));
                        continue;
                    }
                    connection = new Connection(this, client);
                    connections.Add(connection);
                }
                connection.Completion = RunConnectionAsync(connection);
            }
        }
        catch (Exception error) when (lifetime.IsCancellationRequested &&
            error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (SocketException error)
        {
            notify(new("Listener failed", error.Message));
            lifetime.Cancel();
            listener.Stop();
        }
    }

    private async Task RunConnectionAsync(Connection connection)
    {
        try { await connection.RunAsync(); }
        finally
        {
            bool released;
            lock (gate)
            {
                released = active == connection;
                if (released) active = null;
                connections.Remove(connection);
            }
            connection.Dispose();
            if (released && !lifetime.IsCancellationRequested)
                notify(new("Listening", "Cast resources released; ready for a new sender."));
        }
    }

    private bool Claim(Connection connection)
    {
        lock (gate)
        {
            if (active is not null) return false;
            active = connection;
            return true;
        }
    }

    private async Task AdvertiseAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        using var broadcaster = new DiscoveryBroadcaster(error =>
            notify(new("Discovery warning", $"UDP advertisement failed: {error.SocketErrorCode}. Manual TCP connection remains available.")));
        string? network = null;
        try
        {
            do
            {
                var addresses = Discovery.Addresses();
                var signature = string.Join(",", addresses.Select(item => item.Address));
                var changed = network != signature;
                if (network is not null && signature != network)
                {
                    notify(new("Network changed", "Current addresses changed; disconnecting the active cast."));
                    await DisconnectAsync("Receiver network changed");
                }
                network = signature;
                if (changed)
                {
                    notify(new("Listening", "Current listener addresses refreshed.",
                        addresses.Count == 0 ? [$"TCP {Port}; no active non-loopback IPv4 address."]
                            : addresses.Select(item => Discovery.DisplayEndpoint(item.Address, Port)).ToArray()));
                    notify(new("Listening", "UDP discovery targets: " +
                        string.Join(", ", addresses.Select(item => $"{item.Address} -> {Discovery.DisplayEndpoint(item.Broadcast, Discovery.Port)}"))));
                }
                var bytes = Discovery.Advertisement(name, Port, IsBusy);
                await broadcaster.PublishAsync(addresses, bytes, lifetime.Token);
            } while (await timer.WaitForNextTickAsync(lifetime.Token));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (NetworkInformationException error)
        {
            notify(new("Discovery failed", $"Network enumeration failed: {error.Message}. Stop and restart the listener."));
        }
    }

    public async Task DisconnectAsync(string reason = "user")
    {
        Connection? connection;
        lock (gate) connection = active;
        if (connection is not null) await connection.StopAsync(reason);
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (shutdownTask is not null) return new(shutdownTask);
            stopped = true;
            shutdownTask = ShutdownAsync();
            return new(shutdownTask);
        }
    }

    private async Task ShutdownAsync()
    {
        try { await DisconnectAsync(); }
        finally
        {
            lifetime.Cancel();
            listener.Stop();
            if (acceptTask is not null) await acceptTask;
            Connection[] remaining;
            lock (gate) remaining = connections.ToArray();
            foreach (var connection in remaining) connection.Abort();
            await Task.WhenAll(remaining.Select(item => item.Completion));
            if (discoveryTask is not null) await discoveryTask;
            lifetime.Dispose();
        }
        notify(new("Stopped", "Listener and owned connections closed."));
    }

    private sealed class Connection : IDisposable
    {
        private readonly ReceiverServer owner;
        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly CancellationTokenSource cancellation;
        private readonly CancellationTokenSource playbackCancellation;
        private readonly SemaphoreSlim writer = new(1, 1);
        private readonly ReceiveDiagnostics receive = new();
        private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Reserve the remaining encoded budget for the managed/native in-progress packet.
        private ReceiveQueue queue = new(4 * 1024 * 1024);
        private long session;
        private long lastInput = Environment.TickCount64;
        private long startup = Environment.TickCount64;
        private int tracksReady;
        private int expectedTracks;
        private uint readyAudioGeneration;
        private bool accepted;
        private bool disposed;
        private int stopping;
        private bool srtMode, srtAudioExpected;
        private readonly ConcurrentDictionary<(uint Track, uint Generation), TaskCompletionSource> srtConfigurations = new();
        public Task Completion { get; set; } = Task.CompletedTask;

        public Connection(ReceiverServer owner, TcpClient client)
        {
            this.owner = owner;
            this.client = client;
            stream = client.GetStream();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.lifetime.Token);
            playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        }

        private async Task SendAsync(Packet packet, CancellationToken extraCancellation = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, extraCancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await writer.WaitAsync(timeout.Token);
            try { await Wire.WriteAsync(stream, packet, timeout.Token); }
            finally { writer.Release(); }
        }

        private async Task<Packet> ReadAsync(CancellationToken token)
        {
            var begin = Environment.TickCount64;
            var packet = await Wire.ReadAsync(stream, token);
            receive.Received(packet, Environment.TickCount64 - begin);
            Interlocked.Exchange(ref lastInput, Environment.TickCount64);
            return packet;
        }

        public async Task RunAsync()
        {
            IPlayback? playback = null;
            Task? pump = null;
            Task? watchdog = null;
            Task? mediaInput = null;
            try
            {
                using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                setup.CancelAfter(TimeSpan.FromSeconds(10));
                var hello = await ReadAsync(setup.Token);
                JsonObject.Require(hello.Type == PacketType.Hello && hello.Session == 0, "HELLO required first.");
                var json = hello.Object();
                JsonObject.Require(JsonObject.Text(json, "role") == "sender", "Sender role required.");
                var peerName = JsonObject.Text(json, "name");
                JsonObject.Require(peerName.Length > 0 && !peerName.Any(char.IsControl), "Invalid sender name.");
                var versions = JsonObject.Member(json, "versions", JsonValueKind.Array);
                JsonObject.Require(versions.GetArrayLength() is >= 1 and <= 16 &&
                    versions.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Number &&
                        item.TryGetInt32(out _)), "Invalid version list.");
                JsonObject.Require(versions.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Number &&
                    item.TryGetInt32(out var value) && value == Wire.Version), "Version 3 required.", "VERSION");
                if (owner.IsBusy)
                {
                    await SendAsync(Packet.Control(PacketType.Reject, 0, new { code = "BUSY", message = "Receiver already in use" }));
                    return;
                }
                await SendAsync(Packet.Control(PacketType.Hello, 0, new { role = "receiver", versions = new[] { 3 }, name = owner.name }), setup.Token);
                var requestPacket = await ReadAsync(setup.Token);
                while (requestPacket.Type != PacketType.SessionRequest)
                {
                    JsonObject.Require(requestPacket.Session == 0, "Setup control requires session zero.");
                    if (requestPacket.Type == PacketType.Ping)
                        await SendAsync(Packet.Control(PacketType.Pong, 0,
                            new { nonce = JsonObject.Number(requestPacket.Object(), "nonce") }), setup.Token);
                    else if (requestPacket.Type == PacketType.Pong)
                        _ = JsonObject.Number(requestPacket.Object(), "nonce");
                    else if (requestPacket.Type == PacketType.Stop)
                    {
                        await SendAsync(Packet.Control(PacketType.SessionEnd, 0,
                            new { reason = JsonObject.Text(requestPacket.Object(), "reason", 1024) }), setup.Token);
                        return;
                    }
                    else throw new ProtocolException("Expected SESSION_REQUEST or setup liveness/STOP.");
                    requestPacket = await ReadAsync(setup.Token);
                }
                JsonObject.Require(requestPacket.Type == PacketType.SessionRequest && requestPacket.Session > 0,
                    "Positive SESSION_REQUEST required.");
                session = requestPacket.Session;
                if (!owner.Claim(this))
                {
                    await SendAsync(Packet.Control(PacketType.Reject, session, new { code = "BUSY", message = "Receiver already in use" }));
                    return;
                }
                CastRequest request;
                try
                {
                    request = CastRequest.Parse(requestPacket.Object(), session,
                        ((IPEndPoint)client.Client.RemoteEndPoint!).Address);
                }
                catch (ProtocolException error)
                {
                    throw new ProtocolException(error.Message, "UNSUPPORTED_CONFIG");
                }
                srtMode = request.Srt is not null;
                srtAudioExpected = request.AudioCodec is not null;
                if (srtMode && !owner.factory.SupportsSrt)
                    throw new ProtocolException("This playback backend does not support SRT/fMP4. No fallback.", "UNSUPPORTED_CONFIG");
                if (request.AudioCodec is not null)
                {
                    queue.Dispose();
                    queue = new ReceiveQueue(4 * 1024 * 1024 - 256 * 1024);
                }
                playback = await owner.factory.OpenAsync(request, setup.Token);
                if (srtMode && playback is not ISrtPlayback)
                    throw new ProtocolException("SRT playback input is unavailable.", "UNSUPPORTED_CONFIG");
                expectedTracks = request.Tracks.Length;
                await SendAsync(Packet.Control(PacketType.SessionAccept, session, srtMode
                    ? (object)new { tracks = request.Tracks, mediaTransport = new { kind = "srt" } }
                    : new { tracks = request.Tracks }), setup.Token);
                accepted = true;
                Interlocked.Exchange(ref startup, Environment.TickCount64);
                owner.notify(new("Initializing", $"{request.VideoCodec}, {request.AudioCodec ?? "audio off"}"));
                var trackState = new TrackState(request);
                pump = Task.WhenAll(request.Tracks.Select(track =>
                    TrackThread.Run(() => PumpAsync(playback, trackState, track))));
                watchdog = WatchdogAsync(playback);
                if (playback is ISrtPlayback input && srtMode) mediaInput = ReceiveSrtAsync(input);
                while (!cancellation.IsCancellationRequested)
                {
                    var packet = await ReadAsync(cancellation.Token);
                    JsonObject.Require(packet.Session == session, "Session mismatch.");
                    if (Volatile.Read(ref stopping) != 0 && packet.Type is not (PacketType.SessionEnd or PacketType.Stop))
                        continue;
                    switch (packet.Type)
                    {
                        case PacketType.StreamConfig:
                        case PacketType.VideoFrame:
                        case PacketType.AudioPacket:
                            JsonObject.Require(!srtMode, "SRT sessions cannot substitute TCP media/configuration.");
                            var handlingAt = Environment.TickCount64;
                            try
                            {
                                await queue.AddAsync(packet, cancellation.Token);
                                if (Wire.IsMedia(packet.Type)) playback.Observe(packet);
                            }
                            finally { receive.Handled(Environment.TickCount64 - handlingAt); }
                            break;
                        case PacketType.Ping:
                            await SendAsync(Packet.Control(PacketType.Pong, session, new { nonce = JsonObject.Number(packet.Object(), "nonce") }));
                            break;
                        case PacketType.Pong:
                            _ = JsonObject.Number(packet.Object(), "nonce");
                            break;
                        case PacketType.Stop:
                            var reason = JsonObject.Text(packet.Object(), "reason", 1024);
                            Interlocked.Exchange(ref stopping, 1);
                            playbackCancellation.Cancel();
                            if (mediaInput is not null) await mediaInput;
                            if (pump is not null) await pump;
                            var retiring = playback;
                            playback = null;
                            await retiring.DisposeAsync();
                            await SendAsync(Packet.Control(PacketType.SessionEnd, session, new { reason }));
                            owner.notify(new("Disconnected", "Sender stopped the cast."));
                            return;
                        case PacketType.SessionEnd:
                            JsonObject.Require(Volatile.Read(ref stopping) != 0, "Unexpected SESSION_END without local STOP.");
                            _ = JsonObject.Text(packet.Object(), "reason", 1024);
                            owner.notify(new("Disconnected", "Sender ended the session."));
                            return;
                        case PacketType.Error:
                            var body = packet.Object();
                            var code = JsonObject.Text(body, "code");
                            JsonObject.Require(code is "VERSION" or "BUSY" or "UNSUPPORTED_CONFIG" or "PROTOCOL" or
                                "CAPTURE_FAILED" or "AUDIO_FAILED" or "DECODER_FAILED" or "TIMEOUT" or "CONGESTION" or "INTERNAL",
                                "Unknown sender error code.");
                            var message = JsonObject.Text(body, "message", 2048);
                            var fatal = JsonObject.Boolean(body, "fatal");
                            owner.notify(new("Sender error", $"{code}: {message}"));
                            if (fatal) return;
                            break;
                        case PacketType.Status:
                            _ = JsonObject.Text(packet.Object(), "state");
                            _ = JsonObject.Text(packet.Object(), "message", 2048);
                            break;
                        default:
                            throw new ProtocolException("Unexpected sender packet.");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
            {
                await FailAsync(error, playback);
            }
            catch (Exception error)
            {
                // Backend failures must be visible and terminal, never converted into readiness.
                await FailAsync(new ProtocolException($"Unexpected receiver failure: {error.GetType().Name}: {error.Message}", "INTERNAL"), playback);
            }
            finally
            {
                cancellation.Cancel();
                client.Dispose();
                if (mediaInput is not null) await mediaInput;
                if (pump is not null) await pump;
                if (watchdog is not null) await watchdog;
                try { if (playback is not null) await playback.DisposeAsync(); }
                catch (Exception error) { owner.notify(new("Cleanup failed", $"{error.GetType().Name}: {error.Message}")); }
                finally { ended.TrySetResult(); }
            }
        }

        private async Task ReceiveSrtAsync(ISrtPlayback playback)
        {
            var token = playbackCancellation.Token;
            try
            {
                await playback.ReceiveSrtAsync(async (incoming, cancellationToken) =>
                {
                    var packet = incoming with { Session = session };
                    JsonObject.Require(packet.Type == PacketType.StreamConfig || Wire.IsMedia(packet.Type), "Unexpected SRT demux output.");
                    if (packet.Type == PacketType.StreamConfig)
                    {
                        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var key = (packet.Track, packet.Generation);
                        JsonObject.Require(srtConfigurations.TryAdd(key, ready), "Duplicate pending SRT configuration.");
                        try
                        {
                            await queue.AddAsync(packet, cancellationToken);
                            await ready.Task.WaitAsync(cancellationToken);
                        }
                        finally { srtConfigurations.TryRemove(key, out _); }
                    }
                    else
                    {
                        receive.Received(packet, 0);
                        await queue.AddAsync(packet, cancellationToken);
                        playback.Observe(packet);
                    }
                }, token);
                token.ThrowIfCancellationRequested();
                throw new EndOfStreamException("SRT live media ended without control STOP.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (token.IsCancellationRequested) return;
                if (error is EndOfStreamException)
                {
                    try { await Task.Delay(150, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                }
                if (Volatile.Read(ref stopping) != 0) return;
                await FailAsync(error is ProtocolException ? error :
                    new ProtocolException($"SRT input failed: {error.Message}", "DECODER_FAILED"), playback);
                cancellation.Cancel();
                client.Dispose();
            }
        }

        private async Task FailAsync(Exception error, IPlayback? playback = null)
        {
            var code = error is ProtocolException protocol ? protocol.Code :
                error is OperationCanceledException ? "TIMEOUT" : "PROTOCOL";
            owner.notify(new(code, error.Message + $" {receive.Diagnostics}; {queue.Diagnostics}" +
                (playback is null ? "" : $" {playback.Diagnostics}")));
            try
            {
                await SendAsync(accepted
                    ? Packet.Control(PacketType.Error, session, new { code, message = error.Message, fatal = true })
                    : Packet.Control(PacketType.Reject, session, new { code, message = error.Message }));
            }
            catch (Exception sendError) when (sendError is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                owner.notify(new("Control closed", $"Could not deliver {code}: {sendError.GetType().Name}"));
            }
        }

        private async Task PumpAsync(IPlayback playback, TrackState state, uint track)
        {
            var ready = false;
            try
            {
                using var scheduling = playback.EnterTrack(track);
                while (true)
                {
                    var packet = await queue.TakeAsync(track, playbackCancellation.Token);
                    try
                    {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(playbackCancellation.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    if (packet.Type == PacketType.StreamConfig)
                    {
                        if (ready)
                        {
                            Interlocked.Exchange(ref startup, Environment.TickCount64);
                            Interlocked.Decrement(ref tracksReady);
                            ready = false;
                        }
                        state.Configure(packet);
                        if (packet.Track == 2) Volatile.Write(ref readyAudioGeneration, 0);
                        await playback.ConfigureAsync(packet, timeout.Token);
                        state.MarkReady(packet.Track);
                        if (packet.Track == 2) Volatile.Write(ref readyAudioGeneration, packet.Generation);
                        if (srtMode)
                        {
                            if (!srtConfigurations.TryGetValue((packet.Track, packet.Generation), out var configured))
                                throw new ProtocolException("SRT configuration acknowledgment is missing.", "INTERNAL");
                            configured.TrySetResult();
                        }
                        else await SendAsync(new(PacketType.StreamReady, session, packet.Track, packet.Generation, -1, 0,
                            JsonSerializer.SerializeToUtf8Bytes(new { ok = true })), playbackCancellation.Token);
                        ready = true;
                        Interlocked.Increment(ref tracksReady);
                    }
                    else
                    {
                        state.Media(packet);
                        await playback.PresentAsync(packet, timeout.Token);
                    }
                    }
                    finally { queue.Complete(packet); }
                }
            }
            catch (OperationCanceledException) when (playbackCancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                await FailAsync(error is ProtocolException ? error :
                    new ProtocolException($"Playback failed: {error.Message}", track == 2 ? "AUDIO_FAILED" : "DECODER_FAILED"), playback);
                cancellation.Cancel();
                client.Dispose();
            }
        }

        private async Task WatchdogAsync(IPlayback playback)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            var nextPing = Environment.TickCount64 + 2000;
            var initialStartup = Interlocked.Read(ref startup);
            var srtPlaying = false;
            var videoStarted = false;
            uint firstAudioGeneration = 0;
            long firstAudioPosition = -1;
            try
            {
                while (await timer.WaitForNextTickAsync(cancellation.Token))
                {
                    if (Volatile.Read(ref stopping) != 0) continue;
                    try { playback.CheckHealth(); }
                    catch (ProtocolException) { throw; }
                    catch (Exception error) { throw new ProtocolException($"Playback health failed: {error.Message}", "DECODER_FAILED"); }
                    var now = Environment.TickCount64;
                    videoStarted |= playback is IVideoPlaybackProgress { HasEverPresentedVideo: true };
                    if (srtMode && !srtPlaying && playback is ISrtPlayback progress && progress.HasPresentedVideo)
                    {
                        var generation = Volatile.Read(ref readyAudioGeneration);
                        var audio = progress.PlayedAudioFrames;
                        var stableAudio = generation != 0 && generation == Volatile.Read(ref readyAudioGeneration);
                        if (stableAudio && generation != firstAudioGeneration)
                        {
                            firstAudioGeneration = generation;
                            firstAudioPosition = audio;
                        }
                        if (!srtAudioExpected || stableAudio && audio > 0 && audio > firstAudioPosition)
                        {
                            await SendAsync(Packet.Control(PacketType.Status, session, new
                            {
                                state = "srt-playing", message = "Hardware video presented and enabled source audio is advancing."
                            }));
                            srtPlaying = true;
                        }
                    }
                    if (now - Interlocked.Read(ref lastInput) >= 10_000 ||
                        srtMode && !srtPlaying && now - initialStartup >= 10_000 ||
                        !srtMode && playback is IVideoPlaybackProgress && !videoStarted && now - initialStartup >= 10_000 ||
                        (Volatile.Read(ref tracksReady) < expectedTracks ||
                            playback is IVideoPlaybackProgress { HasPresentedVideo: false }) &&
                        now - Interlocked.Read(ref startup) >= 10_000)
                        throw new ProtocolException("Session startup or incoming traffic timed out.", "TIMEOUT");
                    if (now >= nextPing)
                    {
                        await SendAsync(Packet.Control(PacketType.Ping, session, new { nonce = unchecked((int)now) }));
                        nextPing = now + 2000;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
            {
                await FailAsync(error, playback);
                cancellation.Cancel();
                client.Dispose();
            }
        }

        public async Task StopAsync(string reason)
        {
            lock (client)
            {
                if (disposed || ended.Task.IsCompleted) return;
                Interlocked.Exchange(ref stopping, 1);
                playbackCancellation.Cancel();
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                if (accepted)
                {
                    await SendAsync(Packet.Control(PacketType.Stop, session, new { reason }), deadline.Token);
                    await ended.Task.WaitAsync(deadline.Token);
                }
            }
            catch (Exception error) when (error is TimeoutException or IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                owner.notify(new("Disconnect", $"Closing control connection: {error.GetType().Name}"));
            }
            finally { Abort(); }
        }

        public void Abort()
        {
            lock (client)
            {
                if (disposed) return;
                cancellation.Cancel();
                client.Dispose();
            }
        }

        public void Dispose()
        {
            lock (client)
            {
                disposed = true;
                client.Dispose();
                cancellation.Dispose();
                playbackCancellation.Dispose();
                writer.Dispose();
                queue.Dispose();
            }
        }
    }
}
