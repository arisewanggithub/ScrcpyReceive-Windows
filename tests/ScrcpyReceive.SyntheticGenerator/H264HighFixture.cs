using ScrcpyReceive.Core;

internal static class H264HighFixture
{
    // Baseline-coded pictures are a valid High-profile subset; only synthetic SPS metadata is extended.
    public static byte[] Convert(byte[] annexB)
    {
        var output = new List<byte>();
        foreach (var unit in H264Stream.Units(annexB))
        {
            var data = unit.ToArray();
            output.AddRange([0, 0, 0, 1]);
            if ((data[0] & 31) != 7) { output.AddRange(data); continue; }
            if (data.Length < 5 || data[1] != 66) throw new InvalidOperationException("Expected a synthetic Baseline SPS.");
            var raw = new List<byte>();
            var zeros = 0;
            foreach (var value in data.Skip(1))
            {
                if (zeros >= 2 && value == 3) { zeros = 0; continue; }
                raw.Add(value);
                zeros = value == 0 ? zeros + 1 : 0;
            }
            var bits = raw.SelectMany(value => Enumerable.Range(0, 8).Select(bit => (value >> (7 - bit)) & 1)).ToList();
            var offset = 24;
            var leading = 0;
            while (offset < bits.Count && bits[offset++] == 0) leading++;
            offset += leading;
            var stop = bits.FindLastIndex(bit => bit == 1);
            if (offset > stop) throw new InvalidOperationException("Truncated synthetic SPS.");
            var extended = new List<int>();
            void Byte(byte value) => extended.AddRange(Enumerable.Range(0, 8).Select(bit => (value >> (7 - bit)) & 1));
            Byte(100); Byte(0); Byte(raw[2]);
            extended.AddRange(bits.Skip(24).Take(offset - 24));
            extended.AddRange([0, 1, 0, 1, 1, 0, 0]); // 4:2:0, 8-bit luma/chroma, no bypass/scaling matrix.
            extended.AddRange(bits.Skip(offset).Take(stop - offset + 1));
            while (extended.Count % 8 != 0) extended.Add(0);
            output.Add(data[0]);
            zeros = 0;
            for (var index = 0; index < extended.Count; index += 8)
            {
                var value = (byte)extended.Skip(index).Take(8).Aggregate(0, (current, bit) => (current << 1) | bit);
                if (zeros >= 2 && value <= 3) { output.Add(3); zeros = 0; }
                output.Add(value);
                zeros = value == 0 ? zeros + 1 : 0;
            }
        }
        return output.ToArray();
    }
}
