using System.Buffers.Binary;
using System.Text.Json;

namespace ScrcpyReceive.Core;

public sealed record AudioFormat(string Codec, int Channels, byte[] Configuration, int PreSkip, int GainQ8, int SamplesPerFrame)
{
    public const int Rate = 48000;
    public const int MaximumFrames = Rate / 4;

    public static AudioFormat Parse(JsonElement json, string requested)
    {
        var codec = JsonObject.Text(json, "codec");
        JsonObject.Require(codec == requested, "Audio codec substitution is forbidden.", "UNSUPPORTED_CONFIG");
        var channels = JsonObject.Number(json, "channels");
        JsonObject.Require(JsonObject.Number(json, "sampleRate") == Rate && channels is 1 or 2,
            "Audio requires 48 kHz mono/stereo.", "UNSUPPORTED_CONFIG");
        var data = CodecConfiguration.Buffers(json);
        if (codec == "raw")
        {
            JsonObject.Require(channels == 2 && data.Length == 0 && JsonObject.Text(json, "sampleFormat") == "s16le",
                "PCM requires stereo s16le.", "UNSUPPORTED_CONFIG");
            return new(codec, channels, [], 0, 0, 0);
        }
        JsonObject.Require(data.Length == 1, "Compressed audio requires one configuration buffer.", "UNSUPPORTED_CONFIG");
        if (codec == "opus")
        {
            var head = data[0];
            JsonObject.Require(head.Length == 19 && head.AsSpan(0, 8).SequenceEqual("OpusHead"u8) &&
                head[8] == 1 && head[9] == channels && head[18] == 0,
                "Canonical OpusHead v1 mapping-family 0 is required.", "UNSUPPORTED_CONFIG");
            var skip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
            JsonObject.Require(JsonObject.Number(json, "codecDelayNs") == skip * 1_000_000_000L / Rate &&
                JsonObject.Number(json, "seekPrerollNs") == 80_000_000, "Opus delay/preroll differs from OpusHead.");
            return new(codec, channels, head, skip, BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(16)), 0);
        }
        JsonObject.Require(codec == "aac" && channels == 2, "Only stereo AAC-LC is supported.", "UNSUPPORTED_CONFIG");
        var asc = data[0];
        var position = 0;
        int Read(int count)
        {
            JsonObject.Require(position + count <= asc.Length * 8, "Truncated AAC AudioSpecificConfig.", "UNSUPPORTED_CONFIG");
            var result = 0;
            for (var index = 0; index < count; index++, position++) result = (result << 1) | ((asc[position / 8] >> (7 - position % 8)) & 1);
            return result;
        }
        JsonObject.Require(Read(5) == 2, "AAC-LC is required.", "UNSUPPORTED_CONFIG");
        var frequency = Read(4);
        var rate = frequency == 15 ? Read(24) : frequency == 3 ? Rate : 0;
        JsonObject.Require(rate == Rate && Read(4) == 2, "AAC requires 48 kHz stereo.", "UNSUPPORTED_CONFIG");
        var samples = Read(1) == 0 ? 1024 : 960;
        JsonObject.Require(Read(1) == 0 && Read(1) == 0, "AAC core extensions are unsupported.", "UNSUPPORTED_CONFIG");
        while (position < asc.Length * 8) JsonObject.Require(Read(1) == 0, "AAC extensions are unsupported.", "UNSUPPORTED_CONFIG");
        return new(codec, channels, asc, 0, 0, samples);
    }
}
