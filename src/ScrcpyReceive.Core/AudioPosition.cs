namespace ScrcpyReceive.Core;

public static class AudioPosition
{
    public static long RetainedTransit(long consumedFrames, long transitFrames, long previous) =>
        Math.Min(consumedFrames, Math.Max(previous, Math.Max(0, consumedFrames - transitFrames)));
    public static long Presented(long deviceFrames, ulong timestamp100Ns, ulong now100Ns, long consumedFrames, long previous, float speed = 1f)
    {
        var estimate = deviceFrames;
        if (timestamp100Ns > 0 && timestamp100Ns <= now100Ns && now100Ns - timestamp100Ns <= 1_000_000)
            estimate = checked(deviceFrames + (long)((now100Ns - timestamp100Ns) * AudioFormat.Rate / 10_000_000.0 * speed));
        return Math.Min(consumedFrames, Math.Max(previous, estimate));
    }
}
