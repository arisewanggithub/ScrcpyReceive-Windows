using System.Text.Json;

namespace ScrcpyReceive.SrtInterop;

internal sealed record PlaybackWindow(long StartedAtTicks, long StoppedAtTicks,
    long LossStartedAtTicks = 0, long LossRecoveredAtTicks = 0, bool PendingLossStop = false);

internal static class PresentationProgress
{
    private sealed record Point(long TimestampTicks, long PresentedFrames, long SourcePtsUs);

    internal static void Validate(JsonElement samples, PlaybackWindow window, long frequency, int seconds)
    {
        Require(frequency > 0 && seconds is >= 2 and <= 5 && window.StoppedAtTicks > window.StartedAtTicks,
            "Invalid presentation observation interval.");
        Require(samples.ValueKind == JsonValueKind.Array && samples.GetArrayLength() is >= 2 and <= 1600,
            "Missing or unbounded presentation checkpoints.");
        var points = samples.EnumerateArray().Select(sample => new Point(
            sample.GetProperty("timestampTicks").GetInt64(), sample.GetProperty("presentedFrames").GetInt64(),
            sample.GetProperty("sourcePtsUs").GetInt64())).ToArray();
        for (var index = 0; index < points.Length; index++)
        {
            Require(points[index].PresentedFrames >= 0 && points[index].SourcePtsUs >= 0 &&
                (index == 0 || points[index].TimestampTicks > points[index - 1].TimestampTicks &&
                    points[index].PresentedFrames >= points[index - 1].PresentedFrames &&
                    points[index].SourcePtsUs >= points[index - 1].SourcePtsUs),
                "Presentation checkpoints must retain monotonic time, counts, and synthetic source PTS.");
        }
        if (window.PendingLossStop)
        {
            Require(window.LossStartedAtTicks >= window.StartedAtTicks &&
                window.LossStartedAtTicks < window.StoppedAtTicks && window.LossRecoveredAtTicks == 0 &&
                points.Any(point => point.TimestampTicks <= window.LossStartedAtTicks && point.PresentedFrames > 0),
                "Pending-gap STOP test lacks a presented picture before its unrecovered gap.");
            return;
        }
        var active = points.Where(point => point.TimestampTicks >= window.StartedAtTicks &&
            point.TimestampTicks <= window.StoppedAtTicks).ToArray();
        var tolerance = frequency / 4;
        Require(active.Length >= 2 && active[0].TimestampTicks - window.StartedAtTicks <= tolerance &&
            window.StoppedAtTicks - active[^1].TimestampTicks <= tolerance &&
            window.StoppedAtTicks - window.StartedAtTicks >= frequency * seconds - tolerance,
            "Presentation checkpoints do not cover the post-readiness active interval.");
        var previous = active[0];
        Require(previous.PresentedFrames > 0, "Readiness preceded actual presentation.");
        for (var index = 1; index <= seconds; index++)
        {
            var target = window.StartedAtTicks + (window.StoppedAtTicks - window.StartedAtTicks) * index / seconds;
            var current = active.Last(point => point.TimestampTicks <= target);
            Require(target - current.TimestampTicks <= tolerance, "Presentation checkpoint missing near an interval boundary.");
            Require(current.PresentedFrames > previous.PresentedFrames && current.SourcePtsUs > previous.SourcePtsUs,
                "Video presentation froze during a post-readiness observation interval.");
            previous = current;
        }
        if (window.LossStartedAtTicks != 0)
        {
            Require(window.LossStartedAtTicks >= window.StartedAtTicks &&
                window.LossRecoveredAtTicks > window.LossStartedAtTicks &&
                window.StoppedAtTicks - window.LossRecoveredAtTicks >= frequency / 2,
                "Loss recovery was not observed with enough active playback afterward.");
            var recovered = active.First(point => point.TimestampTicks >= window.LossRecoveredAtTicks);
            Require(active[^1].PresentedFrames > recovered.PresentedFrames && active[^1].SourcePtsUs > recovered.SourcePtsUs,
                "No continuing video presentation was observed after the injected loss recovered.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
