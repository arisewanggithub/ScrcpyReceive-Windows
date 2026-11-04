using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ScrcpyReceive.Core;

public sealed record SrtDescriptor(string Url, int LatencyMs, string StreamId)
{
    public static SrtDescriptor Parse(JsonElement element, long session, IPAddress controlPeer)
    {
        JsonObject.Require(element.ValueKind == JsonValueKind.Object &&
            element.EnumerateObject().Count() == 5, "SRT requires exactly five descriptor fields.", "UNSUPPORTED_CONFIG");
        JsonObject.Require(JsonObject.Text(element, "kind") == "srt" &&
            JsonObject.Text(element, "container") == "fmp4", "Only explicit SRT/fMP4 is recognized.", "UNSUPPORTED_CONFIG");
        var url = JsonObject.Text(element, "url", 256);
        var latency = JsonObject.Number(element, "latencyMs");
        var streamId = JsonObject.Text(element, "streamId");
        JsonObject.Require(session > 0 && streamId == $"sc-{session:x16}", "SRT stream ID does not match session.", "UNSUPPORTED_CONFIG");
        JsonObject.Require(latency is >= 20 and <= 2000, "SRT latency outside 20..2000 ms.", "UNSUPPORTED_CONFIG");

        // Inspect the authority literally: Uri normalizes away a trailing slash.
        JsonObject.Require(url.StartsWith("srt://", StringComparison.Ordinal), "Invalid SRT scheme.", "UNSUPPORTED_CONFIG");
        var authority = url[6..];
        JsonObject.Require(!authority.Any(c => c is '/' or '\\' or '?' or '#' or '@' || char.IsWhiteSpace(c)),
            "SRT URL must contain only a numeric host and explicit port.", "UNSUPPORTED_CONFIG");
        var colon = authority.LastIndexOf(':');
        JsonObject.Require(colon > 0, "SRT port is required.", "UNSUPPORTED_CONFIG");
        var host = authority[..colon];
        if (host.StartsWith('['))
        {
            JsonObject.Require(host.EndsWith(']'), "Invalid IPv6 brackets.", "UNSUPPORTED_CONFIG");
            host = host[1..^1];
        }
        else
        {
            JsonObject.Require(!host.Contains(':'), "IPv6 requires brackets.", "UNSUPPORTED_CONFIG");
            JsonObject.Require(host.Split('.').Length == 4 &&
                host.Split('.').All(part => part.Length is > 0 and <= 3 && part.All(char.IsAsciiDigit)),
                "SRT requires a numeric IPv4 address.", "UNSUPPORTED_CONFIG");
        }
        var portText = authority[(colon + 1)..];
        JsonObject.Require(portText.Length > 0 && portText.All(char.IsAsciiDigit) &&
            int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535,
            "Invalid SRT port.", "UNSUPPORTED_CONFIG");
        JsonObject.Require(IPAddress.TryParse(host, out var address) &&
            address.MapToIPv6().Equals(controlPeer.MapToIPv6()), "SRT host differs from control peer.", "UNSUPPORTED_CONFIG");
        return new(url, latency, streamId);
    }
}

public sealed record CastRequest(string VideoCodec, string? AudioCodec, int FramesPerSecond,
    string ReceiverOrientation, SrtDescriptor? Srt)
{
    public uint[] Tracks => AudioCodec is null ? [1u] : [1u, 2u];

    public static CastRequest Parse(JsonElement root, long session, IPAddress controlPeer)
    {
        var video = JsonObject.Member(root, "video", JsonValueKind.Object);
        var codec = JsonObject.Text(video, "codec");
        JsonObject.Require(codec is "h264" or "h265" or "av1", "Unsupported video codec.", "UNSUPPORTED_CONFIG");
        _ = JsonObject.Text(video, "encoder", 256);
        _ = JsonObject.Text(video, "codecOptions", 8192);
        JsonObject.Require(JsonObject.Number(video, "bitrate") > 0 &&
            JsonObject.Number(video, "maxSize") is >= 0 and <= 16384 &&
            JsonObject.Number(video, "displayId") >= 0, "Invalid video limits.", "UNSUPPORTED_CONFIG");
        var fps = JsonObject.Number(video, "maxFps");
        JsonObject.Require(fps is >= 1 and <= 240, "Invalid FPS.", "UNSUPPORTED_CONFIG");
        JsonObject.Require(JsonObject.Text(video, "orientation") is "0" or "90" or "180" or "270" or
            "@0" or "@90" or "@180" or "@270", "Invalid capture orientation.", "UNSUPPORTED_CONFIG");
        JsonObject.Require(video.TryGetProperty("crop", out var crop), "Missing crop.", "UNSUPPORTED_CONFIG");
        if (crop.ValueKind != JsonValueKind.Null)
        {
            JsonObject.Require(crop.ValueKind == JsonValueKind.Object &&
                JsonObject.Number(crop, "width") > 0 && JsonObject.Number(crop, "height") > 0 &&
                JsonObject.Number(crop, "x") >= 0 && JsonObject.Number(crop, "y") >= 0,
                "Invalid crop.", "UNSUPPORTED_CONFIG");
        }
        var orientation = root.TryGetProperty("receiverOrientation", out _)
            ? JsonObject.Text(root, "receiverOrientation") : "auto";
        JsonObject.Require(orientation is "auto" or "landscape" or "portrait",
            "Invalid receiver orientation.", "UNSUPPORTED_CONFIG");
        var audio = JsonObject.Member(root, "audio", JsonValueKind.Object);
        var enabled = JsonObject.Boolean(audio, "enabled");
        var audioCodec = JsonObject.Text(audio, "codec");
        JsonObject.Require(audioCodec is "raw" or "opus" or "aac", "Unsupported audio codec.", "UNSUPPORTED_CONFIG");
        _ = JsonObject.Text(audio, "encoder", 256);
        _ = JsonObject.Text(audio, "source");
        _ = JsonObject.Text(audio, "codecOptions", 8192);
        JsonObject.Require(JsonObject.Number(audio, "bitrate") > 0, "Invalid audio bitrate.", "UNSUPPORTED_CONFIG");
        var srt = root.TryGetProperty("mediaTransport", out var descriptor)
            ? SrtDescriptor.Parse(descriptor, session, controlPeer) : null;
        JsonObject.Require(srt is null || !enabled || audioCodec != "raw",
            "SRT does not support PCM; no substitution is performed.", "UNSUPPORTED_CONFIG");
        return new(codec, enabled ? audioCodec : null, fps, orientation, srt);
    }
}
