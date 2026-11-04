using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScrcpyReceive.Core;
using ScrcpyReceive.Media;

namespace ScrcpyReceive.App;

public sealed partial class MainWindow : Window
{
    private sealed record VideoPresentationSample(long TimestampTicks, long PresentedFrames, long SourcePtsUs);
    private readonly ObservableCollection<string> notices = [];
    private ReceiverServer? server;
    private bool closing;
    private readonly bool isolatedTest;
    private Task? startup;
    private bool showingPlayer;
    private int listenerGeneration;
    private readonly WindowLayoutController layout;
    private readonly VideoHost video;

    public MainWindow(bool isolatedTest = false)
    {
        this.isolatedTest = isolatedTest;
        InitializeComponent();
        Title = "ScrcpyReceive-Windows";
        Diagnostics.ItemsSource = notices;
        video = new(VideoSurface, detail => Notice(new("Video", detail)));
        layout = new(this, LayoutRoot, error => Notice(new("Window layout failed", error.Message)));
        MilestoneText.Text = "Low-latency H.264 / HEVC / AV1 - TCP and SRT/fMP4 preview";
        CapabilityInfo.Title = "Hardware video and audio";
        CapabilityInfo.Message = video.Description + " Unsupported requests fail explicitly; no software video fallback.";
        AppWindow.Closing += OnClosing;
        WindowRoot.Loaded += (_, _) => PageScroll.Focus(FocusState.Programmatic);
        Closed += (_, _) => { video.Dispose(); layout.Dispose(); };
    }

    private void Notice(ReceiverNotice notice, int? generation = null)
    {
        void Apply()
        {
            if (generation.HasValue && generation.Value != listenerGeneration) return;
            if (notice.ListeningEndpoints is not null)
                AddressesText.Text = string.Join("   |   ", notice.ListeningEndpoints);
            if (notice.State == "Initializing") Problem.IsOpen = false;
            StatusText.Text = notice.State;
            notices.Add($"{DateTime.Now:T} {notice.State}: {notice.Detail}");
            while (notices.Count > 100) notices.RemoveAt(0);
            UpdatePresentation();
            if (notice.State is "Start failed" or "Listener failed" or "Discovery warning" or "Discovery failed" or "Sender error" or "Cleanup failed" or
                "UNSUPPORTED_CONFIG" or "DECODER_FAILED" or "AUDIO_FAILED" or "TIMEOUT" or "CONGESTION" or "PROTOCOL" or "INTERNAL")
            {
                Problem.Title = notice.State;
                Problem.Message = notice.Detail;
                Problem.IsOpen = true;
            }
        }
        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    internal Task InitializeAsync() => startup ??= StartAsync();

    private async Task StartAsync()
    {
        if (server is not null || closing) return;
        ReceiverServer? candidate = null;
        try
        {
            var generation = ++listenerGeneration;
            candidate = new("ScrcpyReceive-Windows", video,
                notice => Notice(notice, generation), isolatedTest ? 0 : 27184,
                isolatedTest ? IPAddress.Loopback : IPAddress.Any);
            candidate.Start(advertise: !isolatedTest);
            server = candidate;
            AddressesText.Text = isolatedTest ? $"{Discovery.DisplayEndpoint(IPAddress.Loopback, server.Port)} (isolated self-test)"
                : $"TCP {server.Port}; refreshing current interface addresses...";
        }
        catch (Exception error) when (error is SocketException or ArgumentException or System.Net.NetworkInformation.NetworkInformationException)
        {
            if (candidate is not null) await candidate.DisposeAsync();
            server = null;
            AddressesText.Text = "Not listening";
            Notice(new("Start failed", error.Message +
                " Close any other receiver using TCP 27184, then close and reopen this app. Allow this app on trusted Private networks in Windows Firewall."));
        }
    }

    private async Task StopAsync()
    {
        listenerGeneration++;
        try
        {
            if (server is not null) await server.DisposeAsync();
        }
        finally
        {
            server = null;
            AddressesText.Text = "Not listening";
            StatusText.Text = "Stopped";
            if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) SetFullscreen(false);
        }
    }

    private void SetFullscreen(bool fullscreen)
    {
        if (fullscreen == (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)) return;
        AppWindow.SetPresenter(fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        if (showingPlayer) PlayerInput.Focus(FocusState.Programmatic);
    }

    private void UpdatePresentation()
    {
        var playing = video.IsPlaying;
        if (!playing && AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) SetFullscreen(false);
        if (playing == showingPlayer) return;
        showingPlayer = playing;
        PageScroll.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PlayerInput.IsTabStop = playing;
        if (playing) PlayerInput.Focus(FocusState.Programmatic);
        else PageScroll.Focus(FocusState.Programmatic);
    }

    private void Root_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        bool Down(Windows.System.VirtualKey key) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        var modified = Down(Windows.System.VirtualKey.Control) || Down(Windows.System.VirtualKey.Menu) ||
            Down(Windows.System.VirtualKey.LeftWindows) || Down(Windows.System.VirtualKey.RightWindows);
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(WindowRoot.XamlRoot);
        var editing = focused is TextBox or PasswordBox or RichEditBox;
        if (args.KeyStatus.WasKeyDown && video.IsPlaying && !editing && !modified &&
            args.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.F)
        {
            args.Handled = true;
            return;
        }
        args.Handled = HandleShortcut(args.Key, editing, modified);
    }

    private bool HandleShortcut(Windows.System.VirtualKey key, bool editingText = false, bool modified = false)
    {
        var translated = key switch
        {
            Windows.System.VirtualKey.Enter => ConsoleKey.Enter,
            Windows.System.VirtualKey.F => ConsoleKey.F,
            Windows.System.VirtualKey.Escape => ConsoleKey.Escape,
            _ => (ConsoleKey?)null
        };
        var action = PlaybackShortcuts.Evaluate(translated, video.IsPlaying,
            AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen, editingText, modified);
        if (action == FullscreenAction.None) return false;
        SetFullscreen(action == FullscreenAction.Enter);
        return true;
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closing) return;
        args.Cancel = true;
        closing = true;
        if (startup is not null) await startup;
        await StopAsync();
        Close();
    }

    internal async Task RunSelfTestAsync(string reportFile)
    {
        var checks = new List<string>();
        string? failure = null;
        try
        {
            if (server is null) throw new InvalidOperationException("App startup did not automatically start listening.");
            if (PageScroll.Visibility != Visibility.Visible || showingPlayer)
                throw new InvalidOperationException("Idle receiver must show the waiting page.");
            var port = server.Port;
            await InitializeAsync();
            if (server.Port != port) throw new InvalidOperationException("Startup created a duplicate listener.");
            if (HandleShortcut(Windows.System.VirtualKey.F) || HandleShortcut(Windows.System.VirtualKey.Enter))
                throw new InvalidOperationException("Idle receiver must not enter fullscreen.");
            checks.Add("Automatic startup is idempotent; idle Enter/F do not enter fullscreen");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
            await Wire.WriteAsync(client.GetStream(), Packet.Control(PacketType.Hello, 0,
                new { role = "sender", versions = new[] { 3 }, name = "UI self-test" }), timeout.Token);
            var hello = await Wire.ReadAsync(client.GetStream(), timeout.Token);
            if (hello.Type != PacketType.Hello)
                throw new InvalidOperationException("Receiver HELLO missing.");
            // Deliberately malformed request must never reach capture acceptance.
            await Wire.WriteAsync(client.GetStream(), Packet.Control(PacketType.SessionRequest, 1, new { }), timeout.Token);
            var rejection = await Wire.ReadAsync(client.GetStream(), timeout.Token);
            if (rejection.Type != PacketType.Reject || JsonObject.Text(rejection.Object(), "code") != "UNSUPPORTED_CONFIG")
                throw new InvalidOperationException("Unsupported request was not rejected.");
            checks.Add("Real loopback HELLO and explicit rejection");
            if (!AddressesText.Text.Contains($" : {server.Port}", StringComparison.Ordinal) &&
                !AddressesText.Text.StartsWith($"TCP {server.Port}; no active", StringComparison.Ordinal))
                throw new InvalidOperationException("Actual listener address display was not refreshed.");
            var originalSize = AppWindow.Size;
            var scale = LayoutRoot.XamlRoot.RasterizationScale;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Ceiling(560 * scale) + 32, (int)Math.Ceiling(420 * scale) + 80));
            await Task.Delay(100);
            AddressesText.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            await Task.Delay(100);
            var bounds = AddressesText.TransformToVisual(PageScroll).TransformBounds(new Windows.Foundation.Rect(0, 0,
                AddressesText.ActualWidth, AddressesText.ActualHeight));
            if (bounds.Left < -1 || bounds.Right > PageScroll.ViewportWidth + 1 ||
                bounds.Top < -1 || bounds.Bottom > PageScroll.ViewportHeight + 1)
                throw new InvalidOperationException("Receiver address is clipped in a small window.");
            checks.Add("Small high-DPI window keeps receiver addresses reachable without command buttons");
            AppWindow.Resize(originalSize);
            PageScroll.ChangeView(0, 0, null, true);
            await StopAsync();
            if (server is not null) throw new InvalidOperationException("Listener did not close.");
            checks.Add("Owned listener shutdown and UI reset");
        }
        catch (Exception error)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
            Notice(new("Self-test failed", failure));
        }
        finally
        {
            if (server is not null) await StopAsync();
        }
        await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
        {
            passed = failure is null, checks, failure, physicalPlaybackTested = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal async Task RunSrtReceiverSelfTestAsync(string reportFile)
    {
        string? failure = null;
        long presented = 0, decoded = 0, audioFrames = 0;
        var configuredVideoGenerations = 0;
        var presentationSamples = new List<VideoPresentationSample>();
        var awake = false;
        try
        {
            await InitializeAsync();
            if (server is null) throw new InvalidOperationException("Isolated SRT listener did not start.");
            await File.WriteAllTextAsync(reportFile + ".ready.json", JsonSerializer.Serialize(new { port = server.Port }));
            var until = Stopwatch.StartNew();
            var accepted = false;
            while (until.Elapsed < TimeSpan.FromSeconds(30))
            {
                accepted |= server.IsBusy;
                presented = Math.Max(presented, video.PresentedFrames);
                decoded = Math.Max(decoded, video.DecodedFrames);
                configuredVideoGenerations = Math.Max(configuredVideoGenerations, video.ConfiguredVideoGenerations);
                audioFrames = Math.Max(audioFrames, video.PlayedAudioFrames);
                awake |= video.KeepsDisplayAwake;
                if (video.IsAttached)
                    presentationSamples.Add(new(Stopwatch.GetTimestamp(), video.PresentedFrames, video.LastPresentedPtsUs));
                if (accepted && !server.IsBusy) break;
                await Task.Delay(20);
            }
            if (server.IsBusy || !accepted) throw new TimeoutException("SRT test did not complete control STOP within thirty seconds.");
            if (presented == 0 || decoded == 0 || !awake)
                throw new InvalidOperationException("SRT did not actually present hardware video and acquire its display request. " +
                    string.Join(" | ", notices.TakeLast(6)));
            if (video.IsAttached || video.KeepsDisplayAwake)
                throw new InvalidOperationException("SRT STOP did not detach video/release display power.");
            await StopAsync();
        }
        catch (Exception error) { failure = $"{error.GetType().Name}: {error.Message}"; }
        finally { if (server is not null) await StopAsync(); }
        await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
        {
            passed = failure is null, failure, hardwareDecodedFrames = decoded, presentedFrames = presented,
            playedSourceAudioFrames = audioFrames, displayAwakeDuringPlayback = awake,
            configuredVideoGenerations,
            presentationClockFrequency = Stopwatch.Frequency, presentationSamples,
            detachedAfterStop = !video.IsAttached, displayReleasedAfterStop = !video.KeepsDisplayAwake,
            syntheticOnly = true, physicalSenderPlaybackTested = false
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    internal async Task RunVideoBacklogSelfTestAsync(string fixtureFile, string reportFile)
    {
        string? failure = null;
        var observed = 0;
        var checkpoints = 0;
        long presented = 0, decoded = 0;
        try
        {
            if (new FileInfo(fixtureFile).Length > 16 * 1024 * 1024)
                throw new ArgumentException("Synthetic fixture exceeds 16 MiB.");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(fixtureFile));
            var fixture = document.RootElement;
            if (!fixture.GetProperty("syntheticOnly").GetBoolean())
                throw new ArgumentException("Only a labeled synthetic fixture is allowed.");
            var codec = fixture.TryGetProperty("videoCodec", out var value) ? value.GetString()! : "h264";
            var generation = fixture.GetProperty("generations")[0];
            var frames = generation.GetProperty("frames").EnumerateArray().ToArray();
            if (frames.Length is < 1 or > 512) throw new ArgumentException("Invalid synthetic GOP.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using (var player = await video.OpenAsync(new(codec, null, 30, "auto", null), timeout.Token))
            {
                await player.ConfigureAsync(new(PacketType.StreamConfig, 1, 1, 1, -1, 0,
                    JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        codec, width = generation.GetProperty("width").GetInt32(), height = generation.GetProperty("height").GetInt32(),
                        csd = new[] { frames[0].GetProperty("configuration").GetString()! }
                    })), timeout.Token);
                var current = Frame(0);
                player.Observe(current);
                observed++;
                for (var index = 0; index < 32; index++)
                {
                    var next = Frame(index + 1);
                    player.Observe(next);
                    observed++;
                    await player.PresentAsync(current, timeout.Token);
                    await Task.Delay(34, timeout.Token);
                    if (index % 8 == 7)
                    {
                        var wait = Stopwatch.StartNew();
                        while (video.LastPresentedPtsUs != current.PtsUs && wait.Elapsed < TimeSpan.FromSeconds(2))
                        {
                            player.CheckHealth();
                            await Task.Delay(10, timeout.Token);
                        }
                        if (video.LastPresentedPtsUs != current.PtsUs || observed <= video.DecodedFrames)
                            throw new InvalidOperationException("Video did not advance while an observed encoded picture remained pending.");
                        checkpoints++;
                    }
                    current = next;
                }
                presented = video.PresentedFrames;
                decoded = video.DecodedFrames;
                if (decoded != 32 || checkpoints != 4 || presented < 4)
                    throw new InvalidOperationException("Backlogged video did not keep presenting decoded pictures.");

                Packet Frame(int index)
                {
                    var frame = frames[index % frames.Length];
                    return new(PacketType.VideoFrame, 1, 1, 1,
                        checked(frames[0].GetProperty("ptsUs").GetInt64() + index * 1_000_000L / 30),
                        frame.GetProperty("key").GetBoolean() ? 1u : 0u,
                        Convert.FromBase64String(frame.GetProperty("data").GetString()!));
                }
            }
            if (video.IsAttached || video.KeepsDisplayAwake)
                throw new InvalidOperationException("Backlog test did not detach output and release display power.");
        }
        catch (Exception error) { failure = $"{error.GetType().Name}: {error.Message}"; }
        finally { if (server is not null) await StopAsync(); }
        await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
        {
            passed = failure is null, failure, observedVideoPackets = observed, hardwareDecodedFrames = decoded,
            presentedFrames = presented, advancingCheckpointsWithPendingEncodedPicture = checkpoints,
            detachedAfterStop = !video.IsAttached, displayReleasedAfterStop = !video.KeepsDisplayAwake,
            syntheticOnly = true, physicalSenderPlaybackTested = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal async Task RunVideoSelfTestAsync(string fixtureFile, string reportFile)
    {
        string? failure = null;
        long presented = 0;
        var detached = false;
        var configuredGenerations = 0;
        long playedAudioFrames = 0;
        string? testedAudioCodec = null;
        double? opusGainRatio = null;
        int? opusPreSkipValidated = null;
        var audioGenerations = 0;
        long maximumAvSkewUs = 0;
        long decodedFrames = 0, skippedFrames = 0, recoverySilence = 0, droppedAudio = 0;
        var jitterEvents = 0;
        var jitterRecoveredDuringStream = false;
        long decodedWhileHidden = 0;
        var occlusionInjected = false;
        string? decoderDiagnostics = null;
        var hardwareSurfaces = 0;
        var audioTargetMs = 0;
        var displayAwakeDuringPlayback = false;
        var displayRequestReleasedAfterStop = false;
        var testedVideoCodec = "h264";
        try
        {
            if (new FileInfo(fixtureFile).Length > 16 * 1024 * 1024)
                throw new ArgumentException("Synthetic test fixture exceeds 16 MiB.");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(fixtureFile));
            var fixture = document.RootElement;
            testedVideoCodec = fixture.TryGetProperty("videoCodec", out var videoCodecValue) ? videoCodecValue.GetString()! : "h264";
            if (!fixture.GetProperty("syntheticOnly").GetBoolean())
                throw new ArgumentException("Only a labeled synthetic, non-captured fixture is allowed.");
            var generations = fixture.TryGetProperty("generations", out var segments) ? segments.EnumerateArray().ToArray() : [fixture];
            var withAudio = fixture.TryGetProperty("audio", out var audioFixture) && audioFixture.ValueKind == JsonValueKind.Object;
            var testOcclusion = fixture.TryGetProperty("testOcclusion", out var occlusionValue) && occlusionValue.GetBoolean();
            testedAudioCodec = withAudio ? audioFixture.GetProperty("codec").GetString() : null;
            if (testedAudioCodec == "opus")
            {
                var format = AudioFormat.Parse(audioFixture.GetProperty("configuration"), "opus");
                var packets = audioFixture.GetProperty("packets").EnumerateArray().Take(3).ToArray();
                using var normal = new AudioDecoder(format);
                var head = format.Configuration.ToArray();
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(head.AsSpan(16), checked((short)(format.GainQ8 + 1536)));
                using var louder = new AudioDecoder(format with { Configuration = head, GainQ8 = format.GainQ8 + 1536 });
                var samples = 0;
                double power = 0, gainPower = 0;
                for (var index = 0; index < packets.Length; index++)
                {
                    var pts = index == 1 ? packets[0].GetProperty("ptsUs").GetInt64() - 449 : packets[index].GetProperty("ptsUs").GetInt64();
                    var packet = new Packet(PacketType.AudioPacket, 1, 2, 1, pts, 0, Convert.FromBase64String(packets[index].GetProperty("data").GetString()!));
                    var output = normal.Decode(packet);
                    var amplified = louder.Decode(packet);
                    if (index == 1 && output.PtsUs != pts) throw new InvalidOperationException("Opus source PTS reversal was rewritten.");
                    samples += output.Data.Length / 4;
                    for (var offset = 0; offset < output.Data.Length; offset += 2)
                    {
                        var sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(output.Data.AsSpan(offset));
                        var high = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(amplified.Data.AsSpan(offset));
                        power += (double)sample * sample;
                        gainPower += (double)high * high;
                    }
                }
                var expected = packets.Sum(packet => packet.GetProperty("durationFrames").GetInt32()) - format.PreSkip;
                if (samples != expected) throw new InvalidOperationException($"Opus pre-skip mismatch: {samples} vs {expected}.");
                opusPreSkipValidated = format.PreSkip;
                opusGainRatio = Math.Sqrt(gainPower / power);
                if (opusGainRatio is < 1.9 or > 2.1 || !double.IsFinite(opusGainRatio.Value))
                    throw new InvalidOperationException("OpusHead gain was not applied exactly once.");
            }
            if (withAudio) generations = [generations[0]];
            var frames = generations.SelectMany(segment => segment.GetProperty("frames").EnumerateArray()).ToArray();
            if (frames.Length is < 3 or > 120) throw new ArgumentException("Expected 3..120 synthetic frames.");
            var longRun = fixture.TryGetProperty("testDurationSeconds", out var longDuration) && longDuration.GetInt32() >= 10;
            var expectedVideoFrames = longRun ? longDuration.GetInt32() * 60 : frames.Length;
            await InitializeAsync();
            if (server is null) throw new InvalidOperationException("Listener failed.");
            if (video.KeepsDisplayAwake) throw new InvalidOperationException("Idle listening acquired a display power request.");
            var testDuration = fixture.TryGetProperty("testDurationSeconds", out var durationValue) ? durationValue.GetInt32() : 2;
            if (testDuration is < 1 or > 60) throw new ArgumentException("Synthetic test duration is outside 1..60 seconds.");
            var jitterMs = fixture.TryGetProperty("networkJitterMs", out var jitterValue) ? jitterValue.GetInt32() : 0;
            if (jitterMs != 0 && (!withAudio || jitterMs is < 200 or > 400))
                throw new ArgumentException("Synthetic jitter requires audio and a 200..400 ms delivery gap.");
            var networkTimer = Stopwatch.StartNew();
            long nextJitterAtMs = 700;
            long lastSentVideoPts = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(testDuration + 15));
            using var client = new System.Net.Sockets.TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
            var stream = client.GetStream();
            using var sendGate = new SemaphoreSlim(1);
            await Send(Packet.Control(PacketType.Hello, 0,
                new { role = "sender", versions = new[] { 3 }, name = "Synthetic hardware video test" }));
            await Expect(PacketType.Hello);
            await Send(Packet.Control(PacketType.SessionRequest, 1, new
            {
                video = new { codec = testedVideoCodec, encoder = "Synthetic fixture", bitrate = 1000000, maxSize = 1920,
                    maxFps = longRun ? 60 : 30, displayId = 0, orientation = "0", crop = (object?)null, codecOptions = "" },
                receiverOrientation = "auto",
                audio = new { enabled = withAudio, codec = testedAudioCodec ?? "raw", encoder = "", bitrate = 192000, source = "output", codecOptions = "" }
            }));
            await Expect(PacketType.SessionAccept);
            if (withAudio)
            {
                await Send(new(PacketType.StreamConfig, 1, 2, 1, -1, 0,
                    System.Text.Encoding.UTF8.GetBytes(audioFixture.GetProperty("configuration").GetRawText())));
                var audioReady = await Expect(PacketType.StreamReady);
                if (audioReady.Track != 2) throw new InvalidOperationException("Audio readiness missing.");
                audioGenerations = 1;
            }
            Task? audioFeed = null;
            foreach (var segment in generations)
            {
                var segmentFrames = segment.GetProperty("frames").EnumerateArray().ToArray();
                if (segmentFrames.Length == 0) throw new ArgumentException("Empty synthetic generation.");
                var generation = (uint)++configuredGenerations;
                if (generations.Length >= 3 && generation is 2 or 3)
                {
                    if (!HandleShortcut(generation == 2 ? Windows.System.VirtualKey.Enter : Windows.System.VirtualKey.F))
                        throw new InvalidOperationException("Playing Enter/F did not toggle fullscreen.");
                }
                await Send(new(PacketType.StreamConfig, 1, 1, generation, -1, 0,
                    JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        codec = testedVideoCodec, width = segment.GetProperty("width").GetInt32(), height = segment.GetProperty("height").GetInt32(),
                        csd = new[] { segmentFrames[0].GetProperty("configuration").GetString()! }
                    })));
                var ready = await Expect(PacketType.StreamReady);
                if (ready.Generation != generation) throw new InvalidOperationException("Ready generation mismatch.");
                if (withAudio) audioFeed = FeedAudio();
                var videoTimer = Stopwatch.StartNew();
                var videoCount = longRun ? expectedVideoFrames : segmentFrames.Length;
                for (var index = 0; index < videoCount; index++)
                {
                    var frame = segmentFrames[index % segmentFrames.Length];
                    var pts = longRun ? segmentFrames[0].GetProperty("ptsUs").GetInt64() + index * 1_000_000L / 60 : frame.GetProperty("ptsUs").GetInt64();
                    if (longRun)
                    {
                        var delay = index * 1_000_000L / 60 - videoTimer.Elapsed.TotalMicroseconds;
                        if (delay > 0) await Task.Delay(TimeSpan.FromMicroseconds(delay), timeout.Token);
                    }
                    await Send(new(PacketType.VideoFrame, 1, 1, generation, pts,
                        frame.GetProperty("key").GetBoolean() ? 1u : 0u,
                        Convert.FromBase64String(frame.GetProperty("data").GetString()!)));
                    lastSentVideoPts = pts;
                    if (testOcclusion && !occlusionInjected && index == 3)
                    {
                        occlusionInjected = true;
                        var before = video.DecodedFrames;
                        AppWindow.Hide();
                        var restore = DispatcherQueue.CreateTimer();
                        restore.Interval = TimeSpan.FromSeconds(1);
                        restore.Tick += (_, _) =>
                        {
                            restore.Stop();
                            decodedWhileHidden = video.DecodedFrames - before;
                            AppWindow.Show();
                            Activate();
                        };
                        restore.Start();
                    }
                    if (!longRun) await Task.Delay(TimeSpan.FromMilliseconds(34), timeout.Token);
                }
            }
            var wait = Stopwatch.StartNew();
            while ((video.DecodedFrames < expectedVideoFrames || video.LastPresentedPtsUs != lastSentVideoPts ||
                testOcclusion && decodedWhileHidden == 0) &&
                wait.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(20, timeout.Token);
            presented = video.PresentedFrames;
            decodedFrames = video.DecodedFrames;
            skippedFrames = video.SkippedFrames;
            if (decodedFrames != expectedVideoFrames || presented == 0 || video.LastPresentedPtsUs != lastSentVideoPts ||
                presented + skippedFrames != decodedFrames)
                throw new InvalidOperationException($"Expected {expectedVideoFrames} decoded hardware frames and the latest picture; decoded={decodedFrames}, presented={presented}, skipped={skippedFrames}. " +
                    string.Join(" | ", notices.TakeLast(6)));
            if (testOcclusion && (!occlusionInjected || decodedWhileHidden == 0))
                throw new InvalidOperationException("Hardware decoding did not continue while the presentation window was hidden.");
            if (PageScroll.Visibility != Visibility.Collapsed || !showingPlayer)
                throw new InvalidOperationException("Actual playback did not hide the waiting-page UI.");
            displayAwakeDuringPlayback = video.KeepsDisplayAwake;
            if (!displayAwakeDuringPlayback) throw new InvalidOperationException("Playing video did not acquire its display/system power request.");
            var redraws = video.RedrawnFrames;
            if (!HandleShortcut(Windows.System.VirtualKey.F) ||
                AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
                throw new InvalidOperationException("Playing F did not enter fullscreen.");
            await Task.Delay(150, timeout.Token);
            if (video.RedrawnFrames <= redraws || video.PresentedFrames != presented || video.DecodedFrames != decodedFrames)
                throw new InvalidOperationException("Static video was not redrawn on resize without advancing media.");
            if (!HandleShortcut(Windows.System.VirtualKey.Escape) ||
                AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen ||
                PageScroll.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Playing F/fullscreen and Escape/exit failed.");
            if (!video.KeepsDisplayAwake) throw new InvalidOperationException("Fullscreen transition released the active playback power request.");
            if (audioFeed is not null)
            {
                await audioFeed;
                playedAudioFrames = video.PlayedAudioFrames;
                if (playedAudioFrames <= 0) throw new InvalidOperationException("Audio hardware clock did not advance.");
                maximumAvSkewUs = video.MaximumAvSkewUs;
                var measuredSkew = longRun ? video.MaximumSteadySkewUs : maximumAvSkewUs;
                if (video.AvMeasurements == 0 || jitterMs == 0 && measuredSkew > 150_000)
                    throw new InvalidOperationException($"Nominal independent synthetic A/V presentation skew exceeds 150 ms: {maximumAvSkewUs}.");
                (recoverySilence, droppedAudio) = video.AudioRecovery;
                if (jitterMs > 0 && (jitterEvents == 0 || !jitterRecoveredDuringStream || recoverySilence == 0))
                    throw new InvalidOperationException("Jitter did not exercise recoverable underruns during active source delivery.");
                if (testedAudioCodec == "opus")
                {
                    var shift = audioFixture.GetProperty("packets").EnumerateArray().Sum(packet => (long)packet.GetProperty("durationFrames").GetInt32()) * 1_000_000 / 48000 + 200_000;
                    await Send(new(PacketType.StreamConfig, 1, 2, 2, -1, 0,
                        System.Text.Encoding.UTF8.GetBytes(audioFixture.GetProperty("configuration").GetRawText())));
                    var audioReady = await Expect(PacketType.StreamReady);
                    if (audioReady.Track != 2 || audioReady.Generation != 2) throw new InvalidOperationException("New audio generation was not ready.");
                    await FeedAudio(2, shift);
                    audioGenerations = 2;
                    playedAudioFrames += video.PlayedAudioFrames;
                }
            }
            decoderDiagnostics = video.DecoderDiagnostics;
            audioTargetMs = video.AudioTargetFrames * 1000 / AudioFormat.Rate;
            if (withAudio && audioTargetMs is < 50 or > 150)
                throw new InvalidOperationException("Adaptive audio target exceeded 50..150 ms.");
            hardwareSurfaces = video.HardwareSurfaceCount;
            if (hardwareSurfaces < video.MinimumHardwareSurfaces || hardwareSurfaces > 64)
                throw new InvalidOperationException("The decoder surface reserve was not applied.");
            await Send(Packet.Control(PacketType.Stop, 1, new { reason = "synthetic test complete" }));
            await Expect(PacketType.SessionEnd);
            displayRequestReleasedAfterStop = !video.KeepsDisplayAwake;
            if (!displayRequestReleasedAfterStop) throw new InvalidOperationException("STOP left a playback power request active.");
            detached = !video.IsAttached;
            if (!detached) throw new InvalidOperationException("STOP acknowledged before detaching the video surface.");
            if (PageScroll.Visibility != Visibility.Visible || showingPlayer)
                throw new InvalidOperationException("Stopping video did not restore the waiting page.");
            if (HandleShortcut(Windows.System.VirtualKey.F) || HandleShortcut(Windows.System.VirtualKey.Enter))
                throw new InvalidOperationException("Stopped playback accepted fullscreen shortcuts.");
            await StopAsync();

            async Task Send(Packet packet)
            {
                await sendGate.WaitAsync(timeout.Token);
                try
                {
                    if (jitterMs > 0 && Wire.IsMedia(packet.Type) && networkTimer.ElapsedMilliseconds >= nextJitterAtMs)
                    {
                        var before = video.AudioRecovery.Silence;
                        await Task.Delay(jitterMs, timeout.Token);
                        jitterRecoveredDuringStream |= video.AudioRecovery.Silence > before;
                        jitterEvents++;
                        nextJitterAtMs += 1000;
                    }
                    await Wire.WriteAsync(stream, packet, timeout.Token);
                }
                finally { sendGate.Release(); }
            }

            async Task FeedAudio(uint generation = 1, long shift = 0)
            {
                var timer = Stopwatch.StartNew();
                long sentFrames = 0;
                var index = 0;
                var packets = audioFixture.GetProperty("packets").EnumerateArray().ToArray();
                foreach (var packet in packets)
                {
                    var delay = sentFrames * 1_000_000L / 48000 - timer.Elapsed.TotalMicroseconds;
                    if (delay > 0) await Task.Delay(TimeSpan.FromMicroseconds(delay), timeout.Token);
                    var pts = packet.GetProperty("ptsUs").GetInt64() + shift;
                    if (testedAudioCodec == "opus")
                    {
                        if (index == 1) pts = packets[0].GetProperty("ptsUs").GetInt64() + shift - 449;
                        if (index is >= 20 and <= 22) pts -= 300_000 + (index - 20) * 25_000;
                    }
                    await Send(new(PacketType.AudioPacket, 1, 2, generation, pts, 0,
                        Convert.FromBase64String(packet.GetProperty("data").GetString()!)));
                    sentFrames += packet.GetProperty("durationFrames").GetInt32();
                    index++;
                }
            }

            async Task<Packet> Expect(PacketType type)
            {
                while (true)
                {
                    var packet = await Wire.ReadAsync(stream, timeout.Token);
                    if (packet.Type == PacketType.Ping)
                    {
                        await Send(Packet.Control(PacketType.Pong, packet.Session,
                            new { nonce = JsonObject.Number(packet.Object(), "nonce") }));
                        continue;
                    }
                    if (packet.Type != type)
                        throw new InvalidOperationException($"Expected {type}, received {packet.Type}: " +
                            System.Text.Encoding.UTF8.GetString(packet.Payload));
                    return packet;
                }
            }
        }
        catch (Exception error) { failure = $"{error.GetType().Name}: {error.Message}"; }
        finally { if (server is not null) await StopAsync(); }
        var scale = LayoutRoot.XamlRoot.RasterizationScale;
        await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
        {
            passed = failure is null, failure, hardwareDecodedAndPresented = presented, detachedBeforeStopAcknowledgement = detached,
            configuredGenerations,
            testedVideoCodec, testedAudioCodec, playedAudioFrames,
            opusGainRatio, opusPreSkipValidated,
            audioGenerations,
            maximumAvSkewUs,
            hardwareDecodedFrames = decodedFrames, skippedDecodedVideoFrames = skippedFrames,
            insertedRecoverySilenceFrames = recoverySilence, droppedDecodedAudioFrames = droppedAudio,
            jitterEvents, jitterRecoveredDuringStream, independentLowLatencyPlayback = true,
            occlusionInjected, hardwareFramesDecodedWhileHidden = decodedWhileHidden,
            decoderDiagnostics, hardwareSurfaces,
            adaptiveAudioTargetMs = audioTargetMs,
            displayAwakeDuringPlayback, displayRequestReleasedAfterStop,
            rasterizationScale = scale, clientWidthDips = AppWindow.ClientSize.Width / scale,
            clientHeightDips = AppWindow.ClientSize.Height / scale, scrollableHeightDips = PageScroll.ScrollableHeight,
            syntheticOnly = true, screenOrAudioCaptured = false, physicalSenderPlaybackTested = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
