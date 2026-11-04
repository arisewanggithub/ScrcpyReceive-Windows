namespace ScrcpyReceive.Core;

internal sealed class CodecBits(ReadOnlyMemory<byte> data, bool escaped = false)
{
    private int offset, available, zeros;
    private byte current;
    public uint Read(int count)
    {
        JsonObject.Require(count is >= 0 and <= 32, "Invalid codec bit count.");
        uint result = 0;
        for (var index = 0; index < count; index++)
        {
            if (available == 0)
            {
                JsonObject.Require(offset < data.Length, "Truncated codec bits.");
                current = data.Span[offset++];
                if (escaped && zeros >= 2 && current == 3)
                {
                    JsonObject.Require(offset < data.Length && data.Span[offset] <= 3, "Invalid emulation prevention.");
                    current = data.Span[offset++];
                    zeros = 0;
                }
                zeros = current == 0 ? zeros + 1 : 0;
                available = 8;
            }
            result = (result << 1) | (uint)((current >> --available) & 1);
        }
        return result;
    }
    public void Skip(int count)
    {
        JsonObject.Require(count >= 0, "Invalid codec skip count.");
        while (count > 0) { var part = Math.Min(count, 32); Read(part); count -= part; }
    }
    public uint Unsigned()
    {
        var leading = 0;
        while (Read(1) == 0) JsonObject.Require(++leading < 32, "Codec Golomb value is too large.");
        return ((1u << leading) - 1) + Read(leading);
    }
    public int Signed()
    {
        var value = Unsigned();
        return (value & 1) == 0 ? -(int)(value / 2) : (int)(value / 2 + 1);
    }
    public void Trailing()
    {
        JsonObject.Require(Read(1) == 1, "Missing codec trailing bit.");
        while (available > 0 || offset < data.Length)
            JsonObject.Require(Read(1) == 0, "Nonzero codec trailing padding.");
    }
}
