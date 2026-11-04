using System.Text.Json;

namespace ScrcpyReceive.Core;

public sealed record CastRequest(string VideoCodec, string? AudioCodec, int FramesPerSecond,
    string ReceiverOrientation)
{
    public uint[] Tracks => AudioCodec is null ? [1u] : [1u, 2u];

    public static CastRequest Parse(JsonElement root)
    {
        JsonObject.Require(!root.TryGetProperty("mediaTransport", out _),
            "Only built-in TCP media is supported; transport overrides are not accepted.", "UNSUPPORTED_CONFIG");
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
        return new(codec, enabled ? audioCodec : null, fps, orientation);
    }
}
