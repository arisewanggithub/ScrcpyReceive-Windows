using System.Text.Json;

namespace ScrcpyReceive.Core;

public static class CodecConfiguration
{
    public static byte[] VideoData(string codec, byte[][] buffers)
    {
        var data = buffers.SelectMany(buffer => buffer).ToArray();
        return codec == "av1" ? Av1Stream.Configuration(data) : data;
    }
    public static void ValidateVideo(string codec, byte[] data, int width, int height)
    {
        switch (codec)
        {
            case "h264": H264Stream.ValidateConfiguration(data, width, height); break;
            case "h265": HevcStream.ValidateConfiguration(data, width, height); break;
            case "av1": Av1Stream.ValidateConfiguration(data, width, height); break;
            default: throw new ProtocolException("Unsupported video codec.", "UNSUPPORTED_CONFIG");
        }
    }
    public static bool ValidateVideoFrame(string codec, byte[] data, byte[] configuration)
    {
        if (codec == "h265") return HevcStream.ValidateFrame(data, configuration);
        if (codec == "av1") return Av1Stream.ValidateFrame(data, configuration);
        JsonObject.Require(codec == "h264", "Unsupported video codec.", "UNSUPPORTED_CONFIG");
        var key = H264Stream.ValidateFrame(data);
        var parameters = H264Stream.Units(configuration).Where(unit => (unit.Span[0] & 31) is 7 or 8).ToArray();
        foreach (var parameter in H264Stream.Units(data).Where(unit => (unit.Span[0] & 31) is 7 or 8))
            JsonObject.Require(parameters.Any(known => known.Span.SequenceEqual(parameter.Span)), "Undeclared H.264 parameter-set change.");
        return key;
    }

    public static byte[][] Buffers(JsonElement json)
    {
        var array = JsonObject.Member(json, "csd", JsonValueKind.Array);
        JsonObject.Require(array.GetArrayLength() <= 8, "Too many configuration buffers.");
        return array.EnumerateArray().Select(item =>
        {
            JsonObject.Require(item.ValueKind == JsonValueKind.String, "CSD requires Base64 strings.");
            var text = item.GetString()!;
            byte[] data;
            try { data = Convert.FromBase64String(text); }
            catch (FormatException) { throw new ProtocolException("Invalid Base64 CSD."); }
            JsonObject.Require(Convert.ToBase64String(data) == text, "Noncanonical Base64.");
            return data;
        }).ToArray();
    }
}
