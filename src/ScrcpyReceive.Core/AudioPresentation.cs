namespace ScrcpyReceive.Core;

public sealed class AudioPresentation
{
    private sealed record Span(long Start, int Frames, long FirstPtsUs, long EndPtsUs)
    {
        public long End => Start + Frames;
        public long Pts(long offset) => checked(FirstPtsUs + (EndPtsUs - FirstPtsUs) * offset / Frames);
    }
    private readonly object gate = new();
    private readonly Queue<Span> spans = new();
    private long submitted, played, completedContent;
    private long? lastPts;

    public void Submit(long outputPosition, AudioBlock block)
    {
        lock (gate)
        {
            JsonObject.Require(block.Data.Length % 4 == 0 && block.Frames is > 0 and <= AudioFormat.MaximumFrames,
                "Invalid audio output block.", "AUDIO_FAILED");
            JsonObject.Require(outputPosition == submitted, "Audio output submission is not sequential.", "AUDIO_FAILED");
            var end = 0;
            foreach (var span in block.Spans)
            {
                JsonObject.Require(span.Offset == end && span.Frames > 0 &&
                    span.Frames <= block.Frames - end && span.FirstPtsUs >= 0 && span.EndPtsUs >= span.FirstPtsUs,
                    "Invalid source-audio presentation span.", "AUDIO_FAILED");
                end += span.Frames;
            }
            JsonObject.Require(spans.Count + block.Spans.Length <= AudioFormat.MaximumFrames,
                "Audio presentation history exceeded bounds.", "CONGESTION");
            foreach (var span in block.Spans)
                spans.Enqueue(new(outputPosition + span.Offset, span.Frames, span.FirstPtsUs, span.EndPtsUs));
            submitted = checked(submitted + block.Frames);
        }
    }

    public (long ContentFrames, long? PtsUs) Current(long outputPosition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outputPosition);
        lock (gate)
        {
            played = Math.Max(played, Math.Min(outputPosition, submitted));
            while (spans.TryPeek(out var span) && span.End <= played)
            {
                completedContent += span.Frames;
                lastPts = span.EndPtsUs;
                spans.Dequeue();
            }
            var content = completedContent;
            if (spans.TryPeek(out var current) && current.Start < played)
            {
                var count = played - current.Start;
                content += count;
                lastPts = current.Pts(count);
            }
            return (content, lastPts);
        }
    }
}
