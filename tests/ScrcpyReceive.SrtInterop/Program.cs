using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScrcpySend.Core;
using ScrcpySend.Media;
using static ScrcpyReceive.SrtInterop.SyntheticVideo;

namespace ScrcpyReceive.SrtInterop;

internal static class Program
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };
    private sealed record Case(string Name, string Codec, bool Audio, string Fixture, bool Rotation = false, bool Partial = false, bool Loss = false, bool GapStop = false, bool Opus = false, bool Mono = false, bool Gain = false);
    private sealed record Options(string App, string Fixtures, string Reports, string Selected, int TimeoutSeconds, int PlaySeconds);
    private sealed record Result(string Name, bool Passed, string? Failure, int? ProcessId, long ElapsedMs,
        long VideoFramesSent, long AudioSourceFramesSent, string NativeReportFile, JsonElement? NativeReport, PlaybackWindow? PlaybackWindow);
    private sealed record SessionResult(long AudioFrames, PlaybackWindow Window);
    private static readonly Case[] Cases =
    [
        new("h264-video", "h264", false, "synthetic-h264-generations.json"),
        new("h264-aac", "h264", true, "synthetic-h264-generations.json"),
        new("h265-aac", "h265", true, "synthetic-hevc.json"),
        new("av1-aac", "av1", true, "synthetic-av1.json"),
        new("h264-rotate-aac", "h264", true, "synthetic-h264-generations.json", true),
        new("h265-rotate-aac", "h265", true, "synthetic-hevc.json", true),
        new("av1-rotate-aac", "av1", true, "synthetic-av1.json", true),
        new("h264-partial-rotate-aac", "h264", true, "synthetic-h264-generations.json", true, true),
        new("h265-partial-rotate-aac", "h265", true, "synthetic-hevc.json", true, true),
        new("h265-loss-aac", "h265", true, "synthetic-hevc.json", Loss: true),
        new("h265-gap-stop", "h265", false, "synthetic-hevc.json", Loss: true, GapStop: true),
        new("h264-opus", "h264", true, "synthetic-h264-generations.json", Opus: true),
        new("h265-opus-rotate", "h265", true, "synthetic-hevc.json", Rotation: true, Opus: true),
        new("av1-opus", "av1", true, "synthetic-av1.json", Opus: true),
        new("h265-opus-mono-gain", "h265", true, "synthetic-hevc.json", Opus: true, Mono: true, Gain: true),
        new("h265-opus-loss", "h265", true, "synthetic-hevc.json", Loss: true, Opus: true)
    ];

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--metadata-regression")
            return MetadataRegression.Run();
        if (args.Length == 1 && args[0] == "--metadata-avio-regression")
            return await MetadataRegression.RunAvioAsync();
        if (args.Length == 3 && args[0] == "--demux-regression" && args[1] == "--fixtures")
            return await DemuxRegression.RunAsync(args[2]);
        if (args.Length == 3 && args[0] == "--opus-demux-regression" && args[1] == "--fixtures")
            return await OpusDemuxRegression.RunAsync(args[2]);
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("SrtInterop --app <published-exe> --fixtures <synthetic-directory> " +
                "[--reports <new-output-directory>] [--case all|rotation|<case-name>] " +
                "[--timeout-seconds 5..30] [--play-seconds 2..5]");
            return 0;
        }
        try
        {
            var options = Parse(args);
            var selected = Cases.Where(item => options.Selected == "all" ||
                options.Selected == "rotation" && item.Rotation || options.Selected == "opus" && item.Opus || item.Name == options.Selected).ToArray();
            Require(selected.Length > 0, "Unknown --case.");
            Require(!selected.Any(item => item.Rotation) || options.PlaySeconds >= 3,
                "Rotation cases require at least three seconds of post-readiness playback.");
            Directory.CreateDirectory(options.Reports);
            var summary = Path.Combine(options.Reports, "interop-summary.json");
            Require(!File.Exists(summary), "Refusing to overwrite an existing summary.");
            var results = new List<Result>();
            foreach (var test in selected)
            {
                var result = await RunCaseAsync(options, test);
                results.Add(result);
                Console.WriteLine($"{test.Name}: {(result.Passed ? "PASS" : "FAIL")} ({result.ElapsedMs} ms)" +
                    (result.Failure is null ? "" : $" — {result.Failure}"));
            }
            await WriteNewAsync(summary, JsonSerializer.SerializeToUtf8Bytes(new
            {
                passed = results.All(result => result.Passed), syntheticOnly = true,
                actualWindowsSenderSrtMedia = selected.All(test => !test.Opus),
                syntheticFfmpegOpusMuxer = selected.Any(test => test.Opus), nativeReceiverPresentationRequired = true, cases = results
            }, ReportJson));
            Console.WriteLine(summary);
            return results.All(result => result.Passed) ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowed = new[] { "--app", "--fixtures", "--reports", "--case", "--timeout-seconds", "--play-seconds" };
        for (var index = 0; index < args.Length; index += 2)
        {
            Require(index + 1 < args.Length && allowed.Contains(args[index]) && values.TryAdd(args[index], args[index + 1]),
                "Expected unique, recognized --name value arguments. Use --help.");
        }
        Require(values.ContainsKey("--app") && values.ContainsKey("--fixtures"), "--app and --fixtures are required.");
        var app = Path.GetFullPath(values["--app"]);
        var fixtures = Path.GetFullPath(values["--fixtures"]);
        Require(File.Exists(app) && app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), "Published receiver executable not found.");
        Require(Directory.Exists(fixtures), "Synthetic fixture directory not found.");
        var reports = Path.GetFullPath(values.GetValueOrDefault("--reports") ??
            Path.Combine("artifacts", "srt-interop", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
        var timeout = int.Parse(values.GetValueOrDefault("--timeout-seconds") ?? "30", System.Globalization.CultureInfo.InvariantCulture);
        var play = int.Parse(values.GetValueOrDefault("--play-seconds") ?? "3", System.Globalization.CultureInfo.InvariantCulture);
        Require(timeout is >= 5 and <= 30 && play is >= 2 and <= 5 && play + 3 < timeout,
            "Use a 5..30-second case deadline with 2..5 seconds of continuing playback and setup/cleanup headroom.");
        return new(app, fixtures, reports, values.GetValueOrDefault("--case") ?? "all", timeout, play);
    }

    private static async Task<Result> RunCaseAsync(Options options, Case test)
    {
        var clock = Stopwatch.StartNew();
        var reportFile = Path.Combine(options.Reports, test.Name + ".native.json");
        var readyFile = reportFile + ".ready.json";
        Require(!File.Exists(reportFile) && !File.Exists(readyFile) &&
            !File.Exists(reportFile + ".stdout.txt") && !File.Exists(reportFile + ".stderr.txt"),
            $"Preserving existing diagnostics for {test.Name}; select a new report directory.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds - 2));
        var token = deadline.Token;
        Process? process = null;
        Task<string>? stdout = null, stderr = null;
        int? processId = null;
        string? failure = null;
        JsonElement? native = null;
        PlaybackWindow? playbackWindow = null;
        long videoFrames = 0, audioFrames = 0;
        try
        {
            await using var video = new SyntheticVideo(Path.Combine(options.Fixtures, test.Fixture), test.Codec, test.Rotation, test.Partial);
            await using var audio = new SyntheticSilence(video.OriginUs);
            var start = new ProcessStartInfo(options.App)
            {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(options.App)!,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--srt-receiver-self-test");
            start.ArgumentList.Add(reportFile);
            process = Process.Start(start) ?? throw new IOException("Unable to start the isolated receiver.");
            processId = process.Id;
            stdout = DrainAsync(process.StandardOutput);
            stderr = DrainAsync(process.StandardError);
            var port = await WaitForPortAsync(readyFile, process, token);
            try
            {
                var result = await RunSessionAsync(port, test, options.PlaySeconds, video, audio, options.Fixtures, token);
                audioFrames = result.AudioFrames;
                playbackWindow = result.Window;
            }
            finally { videoFrames = video.FramesProduced; }
            await process.WaitForExitAsync(token);
            Require(process.ExitCode == 0, $"Receiver exited with code {process.ExitCode}.");
            native = await ReadNativeReportAsync(reportFile, token);
            if (test.Loss && !test.GapStop)
                Require(native.Value.GetProperty("hardwareDecodedFrames").GetInt64() >= videoFrames - 8,
                    "Hardware video did not recover and catch up after the injected SRT sequence gap.");
            ValidateNativeReport(native.Value, test.Audio, options.PlaySeconds);
            Require(native.Value.GetProperty("presentationClockFrequency").GetInt64() == Stopwatch.Frequency,
                "Receiver and harness presentation clocks differ.");
            PresentationProgress.Validate(native.Value.GetProperty("presentationSamples"), playbackWindow,
                Stopwatch.Frequency, options.PlaySeconds);
            Require(native.Value.GetProperty("configuredVideoGenerations").GetInt32() == (test.Rotation ? 3 : 1),
                "Native video generation count differs; unchanged duplicates or stale metadata must not create generations.");
            if (test.Rotation)
                Require(video.GenerationsProduced == 3,
                    "In-band rotation must configure exactly three video generations; duplicates must not create generations.");
            Require(videoFrames >= options.PlaySeconds * 15 &&
                (!test.Audio || audioFrames >= options.PlaySeconds * 24000),
                "Synthetic sources did not continue after actual srt-playing.");
        }
        catch (Exception error)
        {
            failure = error is OperationCanceledException && deadline.IsCancellationRequested
                ? $"Case exceeded its {options.TimeoutSeconds}-second bounded deadline."
                : $"{error.GetType().Name}: {error.Message}";
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await process.WaitForExitAsync(cleanup.Token);
                    if (stdout is not null) await WriteNewAsync(reportFile + ".stdout.txt", Encoding.UTF8.GetBytes(await stdout.WaitAsync(cleanup.Token)));
                    if (stderr is not null) await WriteNewAsync(reportFile + ".stderr.txt", Encoding.UTF8.GetBytes(await stderr.WaitAsync(cleanup.Token)));
                }
                catch (Exception error) { failure = $"{failure} Cleanup: {error.GetType().Name}: {error.Message}".Trim(); }
                finally { process.Dispose(); }
            }
            if (native is null && File.Exists(reportFile))
            {
                try { native = await ReadNativeReportAsync(reportFile, CancellationToken.None); }
                catch (Exception error) { failure = $"{failure} Report: {error.Message}".Trim(); }
            }
        }
        return new(test.Name, failure is null, failure, processId, clock.ElapsedMilliseconds,
            videoFrames, audioFrames, reportFile, native, playbackWindow);
    }

    private static async Task<SessionResult> RunSessionAsync(int port, Case test, int playSeconds,
        SyntheticVideo video, SyntheticSilence audio, string fixtures, CancellationToken token)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port, token);
        var stream = client.GetStream();
        using var writer = new SemaphoreSlim(1);
        async Task Send(MessageType type, long session, object body)
        {
            await writer.WaitAsync(token);
            try { await Wire.WriteAsync(stream, Message.Control(type, session, body), token); }
            finally { writer.Release(); }
        }
        await Send(MessageType.Hello, 0, new { role = "sender", versions = new[] { Wire.Version }, name = "Synthetic SRT interoperability" });
        var hello = await Wire.ReadAsync(stream, token);
        Require(hello.Type == MessageType.Hello && hello.Session == 0 &&
            StrictJson.String(hello.Json(), "role", 80) == "receiver" &&
            hello.Json().GetProperty("versions").EnumerateArray().Any(version => version.GetInt32() == Wire.Version),
            "Receiver did not complete the actual sender protocol HELLO.");
        var session = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        await using ISrtMedia sender = test.Opus
            ? new SyntheticOpusSender(Path.Combine(fixtures, "synthetic-av-opus.json"), test.Mono, test.Gain)
            : new SrtMedia();
        var descriptor = sender.Prepare(IPAddress.Loopback, IPAddress.Loopback, session);
        await using var proxy = test.Loss ? new SrtLossProxy(descriptor.Url, test.GapStop) : null;
        if (proxy is not null) descriptor = descriptor with { Url = proxy.Url };
        var settings = new CaptureSettings(MaxSize: Math.Max(video.Width, video.Height), MaxFps: 30,
            EnableAudio: test.Audio, VideoCodec: test.Codec, MediaTransport: "srt");
        var request = JsonSerializer.SerializeToElement(settings.Request(video.EncoderName, descriptor))
            .EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        if (test.Opus) request["audio"] = JsonSerializer.SerializeToElement(new
        {
            enabled = true, codec = "opus", encoder = "", bitrate = 128000, source = "output", codecOptions = ""
        });
        await Send(MessageType.SessionRequest, session, request);
        var accepted = await Wire.ReadAsync(stream, token);
        if (accepted.Type is MessageType.Reject or MessageType.Error)
            throw RemoteFailure(accepted);
        Require(accepted.Type == MessageType.SessionAccept && accepted.Session == session,
            "Expected SESSION_ACCEPT for the exact active session.");
        var acceptance = accepted.Json();
        Require(acceptance.GetProperty("mediaTransport").GetProperty("kind").GetString() == "srt",
            "Receiver did not explicitly accept SRT; fallback is forbidden.");
        var tracks = acceptance.GetProperty("tracks").EnumerateArray().Select(track => track.GetInt32()).ToArray();
        Require(tracks.SequenceEqual(test.Audio ? new[] { 1, 2 } : new[] { 1 }), "Accepted tracks differ from the explicit request.");
        using var mediaStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var controlStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var playing = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopSent = 0;
        long stoppedAt = 0;
        var media = sender.RunAsync(video, test.Audio ? audio : null, settings, mediaStop.Token);
        var reader = ReadControlsAsync();
        var heartbeat = HeartbeatAsync();
        try
        {
            await ObserveAsync(playing.Task, reader, media, heartbeat, token);
            proxy?.Arm();
            Console.WriteLine($"{test.Name}: actual receiver srt-playing; continuing synthetic input for {playSeconds}s.");
            await ObserveAsync(Task.Delay(TimeSpan.FromSeconds(playSeconds), token), reader, media, heartbeat, token);
            if (proxy is not null)
                Require(proxy.DroppedPackets >= 2, "SRT loss test did not suppress a packet and its retransmission.");
            stoppedAt = Stopwatch.GetTimestamp();
            mediaStop.Cancel();
            if (!test.GapStop) sender.Abort();
            Interlocked.Exchange(ref stopSent, 1);
            await Send(MessageType.Stop, session, new { reason = "synthetic-interop-complete" });
            await ObserveAsync(ended.Task, reader, null, heartbeat, token);
            try { await media; }
            catch (OperationCanceledException) when (mediaStop.IsCancellationRequested) { }
            await reader;
        }
        finally
        {
            mediaStop.Cancel();
            sender.Abort();
            controlStop.Cancel();
            client.Close();
            try { await media; } catch (Exception) when (mediaStop.IsCancellationRequested) { }
            try { await reader; } catch (Exception) when (controlStop.IsCancellationRequested) { }
            try { await heartbeat; } catch (Exception) when (controlStop.IsCancellationRequested) { }
        }
        return new(sender is SyntheticOpusSender opus ? opus.FramesProduced : audio.FramesProduced,
            new(await playing.Task, stoppedAt, proxy?.LossStartedAt ?? 0, proxy?.LossRecoveredAt ?? 0, test.GapStop));

        async Task ReadControlsAsync()
        {
            while (true)
            {
                var message = await Wire.ReadAsync(stream, controlStop.Token);
                Require(message.Session == session, "Receiver changed the active session.");
                var json = message.Json();
                switch (message.Type)
                {
                    case MessageType.Status:
                        var state = StrictJson.String(json, "state", 80);
                        _ = StrictJson.String(json, "message", 2048);
                        if (state == "srt-playing") playing.TrySetResult(Stopwatch.GetTimestamp());
                        break;
                    case MessageType.Ping:
                        await Send(MessageType.Pong, session, new { nonce = StrictJson.Integer(json, "nonce") });
                        break;
                    case MessageType.Pong:
                        _ = StrictJson.Integer(json, "nonce");
                        break;
                    case MessageType.SessionEnd:
                        Require(Volatile.Read(ref stopSent) == 1 &&
                            StrictJson.String(json, "reason", 1024) == "synthetic-interop-complete",
                            "SESSION_END did not acknowledge the normal sender STOP.");
                        ended.TrySetResult();
                        return;
                    case MessageType.Error:
                    case MessageType.Reject:
                        throw RemoteFailure(message);
                    default:
                        throw new IOException($"Unexpected TCP control {message.Type}; SRT must not use TCP stream readiness/media.");
                }
            }
        }

        async Task HeartbeatAsync()
        {
            var nonce = 0;
            while (true)
            {
                await Task.Delay(1000, controlStop.Token);
                await Send(MessageType.Ping, session, new { nonce = ++nonce });
            }
        }
    }

    private static async Task ObserveAsync(Task milestone, Task reader, Task? media, Task heartbeat, CancellationToken token)
    {
        var tasks = media is null ? new[] { milestone, reader, heartbeat } : new[] { milestone, reader, media, heartbeat };
        var finished = await Task.WhenAny(tasks).WaitAsync(token);
        await finished;
        if (finished != milestone && !milestone.IsCompletedSuccessfully)
            throw new IOException("Sender media or receiver control ended before the required milestone.");
        await milestone.WaitAsync(token);
    }

    private static IOException RemoteFailure(Message message)
    {
        var json = message.Json();
        return new IOException($"Receiver {message.Type}: {StrictJson.String(json, "code", 80)}: {StrictJson.String(json, "message", 2048)}");
    }

    private static async Task<int> WaitForPortAsync(string readyFile, Process process, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Require(!process.HasExited, $"Receiver exited before publishing its isolated ready port (PID {process.Id}).");
            if (File.Exists(readyFile))
            {
                Require(new FileInfo(readyFile).Length <= 4096, "Oversized receiver ready report.");
                try
                {
                    using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(readyFile, token));
                    var port = json.RootElement.GetProperty("port").GetInt32();
                    Require(port is >= 1 and <= 65535, "Invalid isolated receiver TCP port.");
                    return port;
                }
                catch (JsonException) { }
                catch (IOException) { }
            }
            await Task.Delay(25, token);
        }
    }

    private static async Task<JsonElement> ReadNativeReportAsync(string path, CancellationToken token)
    {
        Require(File.Exists(path) && new FileInfo(path).Length is > 0 and <= 1024 * 1024,
            "Native receiver report is missing or oversized.");
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, token), new() { MaxDepth = 32 });
        return document.RootElement.Clone();
    }

    private static void ValidateNativeReport(JsonElement report, bool audio, int playSeconds)
    {
        Require(report.GetProperty("passed").ValueKind == JsonValueKind.True,
            "Native UI report failed: " + (report.TryGetProperty("failure", out var failure) ? failure.ToString() : "missing failure detail"));
        foreach (var flag in new[] { "syntheticOnly", "displayAwakeDuringPlayback", "detachedAfterStop", "displayReleasedAfterStop" })
            Require(report.GetProperty(flag).ValueKind == JsonValueKind.True, $"Native receiver did not confirm {flag}.");
        var presented = report.GetProperty("presentedFrames").GetInt64();
        var decoded = report.GetProperty("hardwareDecodedFrames").GetInt64();
        var played = report.GetProperty("playedSourceAudioFrames").GetInt64();
        Require(presented >= 2 && decoded >= presented, "Actual hardware decode and continuing native presentation were not observed.");
        Require(audio ? played >= playSeconds * 24000L : played == 0,
            "Actual enabled source audio did not continue advancing, or unrequested audio was presented.");
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        const int maximum = 65536;
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (result.Length < maximum) result.Append(buffer, 0, Math.Min(count, maximum - result.Length));
        return result.ToString();
    }

    private static async Task WriteNewAsync(string path, byte[] bytes)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true);
        await stream.WriteAsync(bytes);
    }
}
