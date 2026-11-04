namespace ScrcpyReceive.Core;

public sealed record AudioCompensation(int Samples, int Distance);

public sealed class AudioRateControl
{
    public const int Distance = AudioFormat.Rate * 4;
    public const int MaximumCompensation = Distance / 50;
    private double average = AudioBuffer.TargetFrames;
    private long previousSilence, previousDropped;
    private int sinceAdjustment;
    private bool active;

    public AudioCompensation? Observe(int bufferedFrames, int inputFrames, int outputFrames, long silence, long dropped,
        int targetFrames = AudioBuffer.TargetFrames)
    {
        JsonObject.Require(targetFrames is >= AudioBuffer.TargetFrames and <= AudioBuffer.MaximumTargetFrames &&
            bufferedFrames >= 0 && bufferedFrames <= targetFrames * 11 / 10 + AudioFormat.Rate * 60 / 1000 &&
            inputFrames is > 0 and <= AudioFormat.MaximumFrames &&
            outputFrames is >= 0 and <= AudioBuffer.MaximumConvertedFrames &&
            silence >= previousSilence && dropped >= previousDropped,
            "Invalid audio regulator observation.", "AUDIO_FAILED");
        average = Math.Max(0, average + outputFrames - inputFrames +
            (silence - previousSilence) - (dropped - previousDropped));
        previousSilence = silence;
        previousDropped = dropped;
        average += (bufferedFrames - average) / 32;
        sinceAdjustment += outputFrames;
        if (sinceAdjustment < AudioFormat.Rate) return null;
        sinceAdjustment %= AudioFormat.Rate;
        var difference = targetFrames - average;
        var threshold = active ? AudioFormat.Rate / 1000 : AudioFormat.Rate * 4 / 1000;
        if (Math.Abs(difference) < threshold || difference < 0 && bufferedFrames < targetFrames)
            difference = 0;
        var correction = (int)Math.Clamp(difference, -MaximumCompensation, MaximumCompensation);
        active = correction != 0;
        return new(correction, active ? Distance : 0);
    }
}
