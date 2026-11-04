namespace ScrcpyReceive.Core;

public static class H264Stream
{
    public static IReadOnlyList<ReadOnlyMemory<byte>> Units(byte[] bytes)
    {
        var units = new List<ReadOnlyMemory<byte>>();
        var start = -1;
        var index = 0;
        while (index < bytes.Length)
        {
            var prefix = index + 3 <= bytes.Length && bytes[index] == 0 && bytes[index + 1] == 0 && bytes[index + 2] == 1 ? 3 :
                index + 4 <= bytes.Length && bytes[index] == 0 && bytes[index + 1] == 0 && bytes[index + 2] == 0 && bytes[index + 3] == 1 ? 4 : 0;
            if (prefix == 0) { index++; continue; }
            if (start >= 0) Add(index);
            else JsonObject.Require(index == 0, "Annex B must begin with a start code.");
            start = index + prefix;
            index = start;
        }
        JsonObject.Require(start >= 0, "Missing Annex B start code.");
        Add(bytes.Length);
        return units;

        void Add(int end)
        {
            while (end > start && bytes[end - 1] == 0) end--;
            JsonObject.Require(end > start && units.Count < 4096, "Invalid or excessive Annex B units.");
            JsonObject.Require((bytes[start] & 0x80) == 0, "H.264 forbidden bit is set.");
            units.Add(bytes.AsMemory(start, end - start));
        }
    }

    public static void ValidateConfiguration(byte[] configuration, int width, int height)
    {
        var actual = Dimensions(configuration);
        JsonObject.Require(actual.Width == width && actual.Height == height, "H.264 SPS differs from STREAM_CONFIG dimensions.");
    }

    public static (int Width, int Height) Dimensions(byte[] configuration)
    {
        (int Width, int Height)? dimensions = null;
        var units = Units(configuration);
        JsonObject.Require(units.Any(unit => (unit.Span[0] & 31) == 8), "H.264 PPS is missing.");
        var parameters = units.Where(unit => (unit.Span[0] & 31) == 7).ToArray();
        JsonObject.Require(parameters.Length > 0, "H.264 SPS is missing.");
        foreach (var sps in parameters)
        {
            var bits = new CodecBits(sps[1..], true);
            var profile = bits.Read(8);
            JsonObject.Require(profile is 66 or 77 or 100,
                $"Unsupported H.264 profile_idc {profile}; hardware preview supports 8-bit Baseline/Main/High.", "UNSUPPORTED_CONFIG");
            bits.Read(8);
            bits.Read(8);
            bits.Unsigned();
            if (profile == 100)
            {
                JsonObject.Require(bits.Unsigned() == 1, "Hardware NV12 playback requires H.264 4:2:0 chroma.", "UNSUPPORTED_CONFIG");
                var lumaDepth = bits.Unsigned();
                var chromaDepth = bits.Unsigned();
                JsonObject.Require(lumaDepth == 0 && chromaDepth == 0,
                    "Hardware NV12 playback requires 8-bit H.264 luma and chroma.", "UNSUPPORTED_CONFIG");
                bits.Read(1);
                if (bits.Read(1) != 0)
                    for (var list = 0; list < 8; list++)
                        if (bits.Read(1) != 0) SkipScalingList(bits, list < 6 ? 16 : 64);
            }
            JsonObject.Require(bits.Unsigned() <= 12, "Invalid H.264 frame number size.");
            var order = bits.Unsigned();
            if (order == 0) JsonObject.Require(bits.Unsigned() <= 12, "Invalid H.264 POC size.");
            else if (order == 1)
            {
                bits.Read(1);
                bits.Unsigned();
                bits.Unsigned();
                var cycle = bits.Unsigned();
                JsonObject.Require(cycle <= 255, "Invalid H.264 POC cycle.");
                for (var item = 0u; item < cycle; item++) bits.Unsigned();
            }
            else JsonObject.Require(order == 2, "Invalid H.264 picture order.");
            JsonObject.Require(bits.Unsigned() <= 16, "Excessive H.264 reference frames.");
            bits.Read(1);
            var codedWidth = ((long)bits.Unsigned() + 1) * 16;
            var codedHeight = ((long)bits.Unsigned() + 1) * 16;
            JsonObject.Require(bits.Read(1) == 1, "Interlaced video is unsupported.", "UNSUPPORTED_CONFIG");
            bits.Read(1);
            if (bits.Read(1) != 0)
            {
                var left = bits.Unsigned();
                var right = bits.Unsigned();
                var top = bits.Unsigned();
                var bottom = bits.Unsigned();
                codedWidth -= 2L * (left + (long)right);
                codedHeight -= 2L * (top + (long)bottom);
            }
            JsonObject.Require(codedWidth is > 0 and <= 16384 && codedHeight is > 0 and <= 16384, "Invalid H.264 SPS dimensions.");
            var actual = ((int)codedWidth, (int)codedHeight);
            JsonObject.Require(dimensions is null || dimensions.Value == actual, "Conflicting H.264 SPS dimensions.");
            dimensions = actual;
        }
        return dimensions ?? throw new ProtocolException("H.264 SPS is missing.");
    }

    public static bool ValidateFrame(byte[] data)
    {
        var slice = false;
        var idr = false;
        foreach (var unit in Units(data))
        {
            var type = unit.Span[0] & 31;
            JsonObject.Require(type is > 0 and < 24, "Unsupported H.264 NAL unit.");
            if (type is not (1 or 5)) continue;
            slice = true;
            idr |= type == 5;
            var bits = new CodecBits(unit[1..], true);
            bits.Unsigned();
            var sliceType = bits.Unsigned();
            JsonObject.Require(sliceType <= 9 && sliceType % 5 is 0 or 2, "B/SP/SI slices are forbidden.");
        }
        JsonObject.Require(slice, "VIDEO_FRAME must contain an actual picture.");
        return idr;
    }

    private static void SkipScalingList(CodecBits bits, int size)
    {
        var previous = 8;
        var next = 8;
        for (var index = 0; index < size; index++)
        {
            if (next != 0)
            {
                var delta = bits.Signed();
                JsonObject.Require(delta is >= -128 and <= 127, "Invalid H.264 scaling-list delta.");
                next = (previous + delta + 256) % 256;
            }
            if (next != 0) previous = next;
        }
    }

}
