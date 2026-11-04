using FFmpeg.AutoGen;
using ScrcpyReceive.Core;

namespace ScrcpyReceive.Media;

public sealed unsafe class AudioRegulator : IDisposable
{
    private SwrContext* converter;
    private readonly AudioRateControl control = new();
    private AudioCompensation? compensation;
    private long lastInputPts;
    private int lastInputFrames;
    public AudioBuffer Buffer { get; } = new();
    public string Diagnostics
    {
        get
        {
            var snapshot = Buffer.Snapshot;
            return $"audio targetMs={snapshot.Target * 1000 / AudioFormat.Rate}, fifoFrames={snapshot.Frames}, silenceFrames={snapshot.Silence}, droppedFrames={snapshot.Dropped}, " +
                $"compensation={compensation?.Samples ?? 0}/{compensation?.Distance ?? 0}";
        }
    }

    public AudioRegulator()
    {
        NativeRuntime.Initialize();
        try
        {
            AVChannelLayout stereo;
            ffmpeg.av_channel_layout_default(&stereo, 2);
            SwrContext* resampler = null;
            NativeRuntime.Check(ffmpeg.swr_alloc_set_opts2(&resampler, &stereo, AVSampleFormat.AV_SAMPLE_FMT_S16,
                AudioFormat.Rate, &stereo, AVSampleFormat.AV_SAMPLE_FMT_S16, AudioFormat.Rate, 0, null), "Prepare audio regulation");
            converter = resampler;
            // Keep the resampler active even at 1:1 so compensation never resets its filter history.
            NativeRuntime.Check(ffmpeg.av_opt_set_int(converter, "flags", 1, 0), "Enable audio clock compensation");
            NativeRuntime.Check(ffmpeg.swr_init(converter), "Initialize audio regulator");
        }
        catch { Dispose(); throw; }
    }

    public void Push(DecodedAudio decoded)
    {
        var inputFrames = decoded.Data.Length / 4;
        if (inputFrames == 0) return;
        lastInputPts = decoded.PtsUs;
        lastInputFrames = inputFrames;
        var capacity = ffmpeg.swr_get_out_samples(converter, inputFrames);
        JsonObject.Require(capacity is > 0 and <= AudioBuffer.MaximumConvertedFrames,
            "Regulated audio exceeds the converted-frame bound.", "CONGESTION");
        var data = new byte[capacity * 4];
        int converted;
        fixed (byte* input = decoded.Data)
        fixed (byte* output = data)
        {
            var source = input;
            var destination = output;
            converted = ffmpeg.swr_convert(converter, &destination, capacity, &source, inputFrames);
        }
        NativeRuntime.Check(converted, "Regulate decoded audio");
        if (converted > 0) Buffer.Push(data.AsSpan(0, converted * 4).ToArray(), decoded.PtsUs, inputFrames);
        var snapshot = Buffer.Snapshot;
        var next = control.Observe(snapshot.Frames, inputFrames, converted, snapshot.Silence, snapshot.Dropped, snapshot.Target);
        if (next is not null)
        {
            NativeRuntime.Check(ffmpeg.swr_set_compensation(converter, next.Samples, next.Distance), "Apply audio clock compensation");
            compensation = next;
        }
    }

    public void BeginDrain()
    {
        if (lastInputFrames > 0)
        {
            var capacity = ffmpeg.swr_get_out_samples(converter, 0);
            JsonObject.Require(capacity is >= 0 and <= AudioBuffer.MaximumConvertedFrames,
                "Audio regulator drain exceeded bounds.", "CONGESTION");
            if (capacity > 0)
            {
                var data = new byte[capacity * 4];
                int converted;
                fixed (byte* target = data)
                {
                    var destination = target;
                    converted = ffmpeg.swr_convert(converter, &destination, capacity, null, 0);
                }
                NativeRuntime.Check(converted, "Drain audio regulator");
                if (converted > 0)
                {
                    var pts = checked(lastInputPts + Math.Max(0, lastInputFrames - converted) * 1_000_000L / AudioFormat.Rate);
                    Buffer.Push(data.AsSpan(0, converted * 4).ToArray(), pts, converted);
                }
            }
        }
        Buffer.BeginDrain();
    }

    public void Dispose()
    {
        var owned = converter;
        converter = null;
        if (owned != null) ffmpeg.swr_free(&owned);
    }
}
