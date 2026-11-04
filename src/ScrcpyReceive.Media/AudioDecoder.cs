using FFmpeg.AutoGen;
using ScrcpyReceive.Core;

namespace ScrcpyReceive.Media;

public sealed unsafe class AudioDecoder : IDisposable
{
    private readonly AudioFormat format;
    private AVCodecContext* context;
    private SwrContext* converter;
    private AVSampleFormat? inputFormat;
    private int skip;
    public int PreSkip => format.PreSkip;

    public static void ValidateCodec(string codec)
    {
        if (codec == "raw") return;
        NativeRuntime.Initialize();
        if (ffmpeg.avcodec_find_decoder_by_name(codec) == null)
            throw new NotSupportedException($"Selected {codec} audio decoder is unavailable.");
    }

    public AudioDecoder(AudioFormat format)
    {
        this.format = format;
        skip = format.PreSkip;
        if (format.Codec == "raw") return;
        ValidateCodec(format.Codec);
        try
        {
            var codec = ffmpeg.avcodec_find_decoder_by_name(format.Codec);
            context = ffmpeg.avcodec_alloc_context3(codec);
            if (context == null) throw new OutOfMemoryException();
            context->sample_rate = AudioFormat.Rate;
            context->thread_count = 1;
            context->flags2 |= 1 << 29; // AV_CODEC_FLAG2_SKIP_MANUAL: apply protocol pre-skip exactly once.
            context->pkt_timebase = new() { num = 1, den = AudioFormat.Rate };
            ffmpeg.av_channel_layout_default(&context->ch_layout, format.Channels);
            context->extradata = (byte*)ffmpeg.av_mallocz((ulong)(format.Configuration.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
            if (context->extradata == null) throw new OutOfMemoryException();
            format.Configuration.CopyTo(new Span<byte>(context->extradata, format.Configuration.Length));
            context->extradata_size = format.Configuration.Length;
            NativeRuntime.Check(ffmpeg.avcodec_open2(context, codec, null), $"Open {format.Codec} audio decoder");
        }
        catch { Dispose(); throw; }
    }

    public DecodedAudio Decode(Packet packet)
    {
        if (format.Codec == "raw")
        {
            JsonObject.Require(packet.Payload.Length % 4 == 0 && packet.Payload.Length / 4 <= AudioFormat.MaximumFrames,
                "PCM packet exceeds the 250 ms decoded-audio bound.", "CONGESTION");
            return new(packet.Payload, packet.PtsUs);
        }
        var input = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        if (input == null || frame == null) { ffmpeg.av_packet_free(&input); ffmpeg.av_frame_free(&frame); throw new OutOfMemoryException(); }
        try
        {
            NativeRuntime.Check(ffmpeg.av_new_packet(input, packet.Payload.Length), "Allocate audio packet");
            packet.Payload.CopyTo(new Span<byte>(input->data, packet.Payload.Length));
            NativeRuntime.Check(ffmpeg.avcodec_send_packet(context, input), "Decode audio access unit");
            var result = ffmpeg.avcodec_receive_frame(context, frame);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN)) return new([], packet.PtsUs);
            NativeRuntime.Check(result, "Receive decoded audio");
            JsonObject.Require(frame->sample_rate == AudioFormat.Rate && frame->ch_layout.nb_channels == format.Channels,
                "Audio decoder changed the accepted sample rate/channels.", "AUDIO_FAILED");
            JsonObject.Require(frame->nb_samples is > 0 and <= AudioFormat.MaximumFrames,
                "Decoded audio exceeds 250 ms.", "CONGESTION");
            if (format.Codec == "aac")
                JsonObject.Require(frame->nb_samples == format.SamplesPerFrame && context->profile == ffmpeg.FF_PROFILE_AAC_LOW,
                    "AAC decoder differs from the accepted AAC-LC configuration.", "AUDIO_FAILED");
            var actual = (AVSampleFormat)frame->format;
            if (inputFormat is null)
            {
                AVChannelLayout output;
                ffmpeg.av_channel_layout_default(&output, 2);
                SwrContext* resampler = null;
                NativeRuntime.Check(ffmpeg.swr_alloc_set_opts2(&resampler, &output, AVSampleFormat.AV_SAMPLE_FMT_S16,
                    AudioFormat.Rate, &frame->ch_layout, actual, AudioFormat.Rate, 0, null), "Initialize audio sample conversion");
                converter = resampler;
                NativeRuntime.Check(ffmpeg.swr_init(converter), "Start audio sample conversion");
                inputFormat = actual;
            }
            JsonObject.Require(inputFormat == actual, "Audio sample format changed within a generation.", "AUDIO_FAILED");
            var capacity = ffmpeg.swr_get_out_samples(converter, frame->nb_samples);
            JsonObject.Require(capacity is > 0 and <= AudioFormat.MaximumFrames, "Audio conversion exceeds 250 ms.", "CONGESTION");
            var data = new byte[capacity * 4];
            int converted;
            fixed (byte* target = data)
            {
                var destination = target;
                converted = ffmpeg.swr_convert(converter, &destination, capacity, frame->extended_data, frame->nb_samples);
            }
            NativeRuntime.Check(converted, "Convert decoded audio");
            var trimmed = Math.Min(skip, converted);
            skip -= trimmed;
            var audible = data.AsSpan(trimmed * 4, (converted - trimmed) * 4).ToArray();
            ffmpeg.av_frame_unref(frame);
            var extra = ffmpeg.avcodec_receive_frame(context, frame);
            JsonObject.Require(extra == ffmpeg.AVERROR(ffmpeg.EAGAIN), "An audio packet produced unexpected additional frames.", "AUDIO_FAILED");
            // FFmpeg's native Opus decoder applies OpusHead gain; manual skip prevents double trimming.
            return new(audible, checked(packet.PtsUs + trimmed * 1_000_000L / AudioFormat.Rate));
        }
        finally { ffmpeg.av_packet_free(&input); ffmpeg.av_frame_free(&frame); }
    }

    public void Dispose()
    {
        var decoder = context; context = null;
        if (decoder != null) ffmpeg.avcodec_free_context(&decoder);
        var resampler = converter; converter = null;
        if (resampler != null) ffmpeg.swr_free(&resampler);
    }
}

public sealed record DecodedAudio(byte[] Data, long PtsUs);
