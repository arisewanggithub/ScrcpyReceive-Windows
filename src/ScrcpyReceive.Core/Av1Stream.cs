namespace ScrcpyReceive.Core;

public static class Av1Stream
{
    private sealed record Obu(int Type, ReadOnlyMemory<byte> Data, ReadOnlyMemory<byte> Payload);
    private sealed record Sequence(bool Reduced, uint Width, uint Height);
    private static IReadOnlyList<Obu> Units(byte[] data)
    {
        var result = new List<Obu>();
        var offset = 0;
        while (offset < data.Length)
        {
            var begin = offset;
            var header = data[offset++];
            var type = header >> 3 & 15;
            JsonObject.Require((header & 0x81) == 0 && (header & 2) != 0 &&
                type is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 15, "AV1 requires supported sized low-overhead OBUs.");
            if ((header & 4) != 0)
            {
                JsonObject.Require(offset < data.Length && (data[offset] & 31) == 0, "AV1 spatial layers/reserved extension bits are unsupported.", "UNSUPPORTED_CONFIG");
                offset++;
            }
            ulong size = 0;
            var complete = false;
            for (var index = 0; index < 8; index++)
            {
                JsonObject.Require(offset < data.Length, "Truncated AV1 OBU length.");
                var value = data[offset++];
                size |= (ulong)(value & 127) << (index * 7);
                if ((value & 128) == 0) { complete = true; break; }
            }
            JsonObject.Require(complete && size <= (ulong)(data.Length - offset) && result.Count < 4096, "Invalid AV1 OBU length/count.");
            var end = offset + (int)size;
            result.Add(new(type, data.AsMemory(begin, end - begin), data.AsMemory(offset, end - offset)));
            offset = end;
        }
        return result;
    }

    public static byte[] Configuration(byte[] data)
    {
        if (data.Length >= 4 && data[0] == 0x81)
        {
            JsonObject.Require((data[1] >> 5) == 0 && (data[2] & 0x70) == 0 &&
                (data[2] & 12) == 12 && (data[3] & 0xe0) == 0, "Unsupported AV1 codec record.", "UNSUPPORTED_CONFIG");
            data = data[4..];
        }
        var units = Units(data);
        JsonObject.Require(units.Count(unit => unit.Type == 1) == 1, "AV1 requires exactly one sequence-header OBU.");
        JsonObject.Require(units.All(unit => unit.Type is 1 or 2 or 5 or 15), "AV1 CSD cannot contain pictures.");
        return units.Single(unit => unit.Type == 1).Data.ToArray();
    }

    private static Sequence Parse(byte[] data)
    {
        var header = Units(Configuration(data)).Single();
        var bits = new CodecBits(header.Payload);
        JsonObject.Require(bits.Read(3) == 0, "Only AV1 Profile 0 is supported.", "UNSUPPORTED_CONFIG");
        var still = bits.Read(1) != 0;
        var reduced = bits.Read(1) != 0;
        JsonObject.Require(!reduced || still, "Invalid AV1 reduced sequence.");
        if (reduced) bits.Skip(5);
        else
        {
            var decoderModel = false;
            var delayBits = 0;
            if (bits.Read(1) != 0)
            {
                bits.Skip(64);
                if (bits.Read(1) != 0) bits.Unsigned();
                decoderModel = bits.Read(1) != 0;
                if (decoderModel) { delayBits = (int)bits.Read(5) + 1; bits.Skip(42); }
            }
            var initialDelay = bits.Read(1) != 0;
            var points = bits.Read(5) + 1;
            for (var index = 0; index < points; index++)
            {
                bits.Skip(12);
                if (bits.Read(5) > 7) bits.Skip(1);
                if (decoderModel && bits.Read(1) != 0) bits.Skip(delayBits * 2 + 1);
                if (initialDelay && bits.Read(1) != 0) bits.Skip(4);
            }
        }
        var widthBits = (int)bits.Read(4) + 1;
        var heightBits = (int)bits.Read(4) + 1;
        var width = bits.Read(widthBits) + 1;
        var height = bits.Read(heightBits) + 1;
        if (!reduced && bits.Read(1) != 0) bits.Skip(7);
        bits.Skip(3);
        if (!reduced)
        {
            bits.Skip(4);
            var orderHint = bits.Read(1) != 0;
            if (orderHint) bits.Skip(2);
            var screenTools = bits.Read(1) != 0 ? 2u : bits.Read(1);
            if (screenTools > 0) { if (bits.Read(1) == 0) bits.Skip(1); }
            if (orderHint) bits.Skip(3);
        }
        bits.Skip(3);
        JsonObject.Require(bits.Read(1) == 0, "AV1 requires 8-bit output.", "UNSUPPORTED_CONFIG");
        JsonObject.Require(bits.Read(1) == 0, "AV1 monochrome output is unsupported.", "UNSUPPORTED_CONFIG");
        uint primaries = 2, transfer = 2, matrix = 2;
        if (bits.Read(1) != 0) { primaries = bits.Read(8); transfer = bits.Read(8); matrix = bits.Read(8); }
        JsonObject.Require(!(primaries == 1 && transfer == 13 && matrix == 0), "AV1 RGB output is unsupported.", "UNSUPPORTED_CONFIG");
        JsonObject.Require(transfer is not (16 or 18) && matrix is 1 or 2 or 5 or 6, "AV1 HDR/color matrix is unsupported.", "UNSUPPORTED_CONFIG");
        bits.Skip(1);
        JsonObject.Require(bits.Read(2) <= 2, "Invalid AV1 chroma sample position.");
        bits.Skip(1);
        bits.Skip(1);
        bits.Trailing();
        return new(reduced, width, height);
    }

    public static void ValidateConfiguration(byte[] data, int width, int height)
    {
        var sequence = Parse(data);
        JsonObject.Require(width > 0 && height > 0 && width <= sequence.Width && height <= sequence.Height,
            "AV1 sequence dimensions cannot contain STREAM_CONFIG dimensions.");
    }
    public static (int Width, int Height) Dimensions(byte[] data)
    {
        var sequence = Parse(data);
        JsonObject.Require(sequence.Width is > 0 and <= 16384 && sequence.Height is > 0 and <= 16384,
            "Invalid AV1 sequence dimensions.");
        return ((int)sequence.Width, (int)sequence.Height);
    }

    public static bool ValidateFrame(byte[] data, byte[] configuration)
    {
        var sequence = Parse(configuration);
        var expected = Configuration(configuration);
        var units = Units(data);
        foreach (var header in units.Where(unit => unit.Type == 1))
            JsonObject.Require(header.Data.Span.SequenceEqual(expected), "Undeclared AV1 sequence change.");
        var frames = units.Where(unit => unit.Type is 3 or 6).ToArray();
        JsonObject.Require(frames.Length == 1, "AV1 access unit must contain exactly one displayed frame.");
        JsonObject.Require(frames[0].Payload.Length > 0 && (frames[0].Type == 6 || units.Any(unit => unit.Type == 4)),
            "AV1 frame header has no coded picture/tile data.");
        if (sequence.Reduced) return true;
        var bits = new CodecBits(frames[0].Payload);
        JsonObject.Require(bits.Read(1) == 0, "AV1 show-existing frames are unsupported.", "UNSUPPORTED_CONFIG");
        var type = bits.Read(2);
        JsonObject.Require(bits.Read(1) != 0, "AV1 hidden/reordered frames are unsupported.", "UNSUPPORTED_CONFIG");
        return type == 0;
    }
}
