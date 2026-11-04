using System.Diagnostics;
using System.Text.Json;
using ScrcpySend.Core;
using ScrcpySend.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

if (args is ["--decoder-capabilities"])
{
    using var probeDevice = D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.VideoSupport);
    using var videoDevice = probeDevice.QueryInterface<ID3D11VideoDevice>();
    for (uint index = 0; index < videoDevice.VideoDecoderProfileCount; index++)
    {
        videoDevice.GetVideoDecoderProfile(index, out var profile).CheckError();
        videoDevice.CheckVideoDecoderFormat(profile, Format.NV12, out var nv12).CheckError();
        Console.WriteLine($"Profile={profile}, NV12={nv12}");
    }
    return;
}

if (args.Length is < 1 or > 8 || File.Exists(args[0]) ||
    args.Skip(1).Any(value => value is not ("--high" or "--long" or "--occlusion" or "--audio=raw" or "--audio=aac" or "--audio=opus") &&
        !value.StartsWith("--jitter=", StringComparison.Ordinal) && !value.StartsWith("--duration=", StringComparison.Ordinal) &&
        value is not ("--video=h264" or "--video=h265" or "--video=av1")))
    throw new ArgumentException("Specify a new fixture file, optional --video=h264|h265|av1, --high, --long, --duration=10..60, --occlusion, --audio=raw|aac|opus and --jitter=200..400.");
var high = args.Contains("--high");
var videoCodec = args.FirstOrDefault(value => value.StartsWith("--video=", StringComparison.Ordinal))?[8..] ?? "h264";
if (high && videoCodec != "h264") throw new ArgumentException("--high is an H.264-only synthetic mode.");
var audioCodec = args.Skip(1).FirstOrDefault(value => value.StartsWith("--audio=", StringComparison.Ordinal))?[8..];
var duration = args.Contains("--long") ? 30 : 2;
var durationOptions = args.Skip(1).Where(value => value.StartsWith("--duration=", StringComparison.Ordinal)).ToArray();
if (durationOptions.Length > 1 || durationOptions.Length == 1 &&
    (!args.Contains("--long") || !int.TryParse(durationOptions[0].AsSpan(11), System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out duration) || duration is < 10 or > 60))
    throw new ArgumentException("Long synthetic duration requires one 10..60 second value.");
var jitterOptions = args.Skip(1).Where(value => value.StartsWith("--jitter=", StringComparison.Ordinal)).ToArray();
var jitterMs = 0;
if (jitterOptions.Length > 1 || jitterOptions.Length == 1 &&
    (!int.TryParse(jitterOptions[0].AsSpan(9), System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out jitterMs) || jitterMs is < 200 or > 400 || audioCodec is null))
    throw new ArgumentException("Synthetic jitter requires enabled audio and a single 200..400 ms gap.");
if (duration > 2 && audioCodec != "raw") throw new ArgumentException("The long silent-output check uses PCM.");
using var device = D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
using (var multi = device.ImmediateContext.QueryInterface<ID3D11Multithread>()) multi.SetMultithreadProtected(true);
var generations = duration > 2 ? new[] { Generate(1088, 1920) } : [Generate(320, 192), Generate(192, 320), Generate(320, 192)];
File.WriteAllText(args[0], JsonSerializer.Serialize(new { syntheticOnly = true, videoCodec, profile = high ? "8-bit High compatible subset" : "8-bit",
    generations, testDurationSeconds = duration, networkJitterMs = jitterMs, testOcclusion = args.Contains("--occlusion"),
    audio = audioCodec is null ? null : AudioFixture.Generate(audioCodec, duration > 2 ? duration + 2 : duration) }));
Console.WriteLine($"Generated {generations.Length * 8} synthetic {videoCodec} frames in {generations.Length} generations, duration={duration}s, jitter={jitterMs}ms. No screen/audio capture.");

object Generate(int width, int height)
{
    using var encoder = new HardwareEncoder(device, width, height, new CaptureSettings(MaxFps: duration > 2 ? 60 : 30, VideoCodec: videoCodec));
    encoder.Start();
    var frames = new List<EncodedVideo>();
    var textures = new List<ID3D11Texture2D>();
    var elapsed = Stopwatch.StartNew();
    var submitted = 0;
    try
    {
        while (frames.Count < 8 && elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (submitted < 12 && encoder.CanAcceptInput)
            {
                var bytes = new byte[width * height * 3 / 2];
                Array.Fill(bytes, (byte)(48 + (submitted % 8) * 20), 0, width * height);
                Array.Fill(bytes, (byte)128, width * height, width * height / 2);
                unsafe
                {
                    fixed (byte* pointer = bytes)
                    {
                        var texture = device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height,
                            1, 1, BindFlags.RenderTarget), new SubresourceData((nint)pointer, (uint)width, (uint)bytes.Length));
                        textures.Add(texture);
                        encoder.Submit(texture, (9_000_000_000L + submitted * 33_333L) * 10);
                    }
                }
                submitted++;
            }
            frames.AddRange(encoder.TakeOutput());
            Thread.Sleep(2);
        }
        if (frames.Count < 8) throw new IOException("Synthetic hardware encoder timed out.");
        return new
        {
            width, height, frames = frames.Take(8).Select(frame => new
            {
                data = Convert.ToBase64String(high ? H264HighFixture.Convert(frame.Data) : frame.Data), ptsUs = frame.PtsUs,
                configuration = Convert.ToBase64String(high ? H264HighFixture.Convert(frame.Configuration) : frame.Configuration),
                key = VideoBitstream.IsKeyFrame(videoCodec, frame.Data, frame.Configuration)
            }).ToArray()
        };
    }
    finally { foreach (var texture in textures) texture.Dispose(); }
}
