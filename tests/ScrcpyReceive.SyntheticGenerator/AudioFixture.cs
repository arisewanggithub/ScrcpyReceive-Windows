using FFmpeg.AutoGen;
using ScrcpySend.Core;

internal static unsafe class AudioFixture
{
    public static object Generate(string codec, int seconds = 2)
    {
        const long origin = 9_000_000_000;
        if (codec == "raw")
            return new
            {
                codec, configuration = new { codec, sampleRate = 48000, channels = 2, sampleFormat = "s16le", csd = Array.Empty<string>() },
                packets = Enumerable.Range(0, seconds * 50).Select(index =>
                {
                    var bytes = new byte[960 * 4];
                    for (var frame = 0; frame < 960; frame++)
                    {
                        var sample = seconds > 2 ? (short)0 : (short)(655 * Math.Sin(2 * Math.PI * 440 * (index * 960L + frame) / 48000));
                        for (var channel = 0; channel < 2; channel++)
                        {
                            bytes[frame * 4 + channel * 2] = (byte)sample;
                            bytes[frame * 4 + channel * 2 + 1] = (byte)(sample >> 8);
                        }
                    }
                    return new { data = Convert.ToBase64String(bytes), ptsUs = origin + index * 20000L, durationFrames = 960 };
                }).ToArray()
            };
        ffmpeg.RootPath = NativeArchitecture.LibraryDirectory;
        var encoder = ffmpeg.avcodec_find_encoder_by_name(codec == "aac" ? "aac" : "libopus");
        if (encoder == null) throw new NotSupportedException($"Synthetic {codec} encoder missing.");
        AVCodecContext* context = null;
        AVFrame* input = null;
        AVPacket* packet = null;
        var packets = new List<object>();
        try
        {
            context = ffmpeg.avcodec_alloc_context3(encoder);
            if (context == null) throw new OutOfMemoryException();
            context->sample_rate = 48000;
            context->sample_fmt = codec == "aac" ? AVSampleFormat.AV_SAMPLE_FMT_FLTP : AVSampleFormat.AV_SAMPLE_FMT_FLT;
            context->bit_rate = 192000;
            context->time_base = new AVRational { num = 1, den = 48000 };
            if (codec == "aac") context->profile = ffmpeg.FF_PROFILE_AAC_LOW;
            ffmpeg.av_channel_layout_default(&context->ch_layout, 2);
            Check(ffmpeg.avcodec_open2(context, encoder, null));
            if (context->frame_size is <= 0 or > 5760 || context->extradata_size <= 0) throw new IOException("Unexpected synthetic audio encoder configuration.");
            var configuration = new ReadOnlySpan<byte>(context->extradata, context->extradata_size).ToArray();
            if (codec == "aac") configuration = AacConfiguration.CanonicalCore(configuration);
            input = ffmpeg.av_frame_alloc(); packet = ffmpeg.av_packet_alloc();
            if (input == null || packet == null) throw new OutOfMemoryException();
            input->format = (int)context->sample_fmt; input->sample_rate = 48000; input->nb_samples = context->frame_size;
            Check(ffmpeg.av_channel_layout_copy(&input->ch_layout, &context->ch_layout));
            Check(ffmpeg.av_frame_get_buffer(input, 0));
            for (var index = 0; index < 100; index++)
            {
                Check(ffmpeg.av_frame_make_writable(input));
                for (var sample = 0; sample < input->nb_samples; sample++)
                {
                    var value = (float)(0.02 * Math.Sin(2 * Math.PI * 440 * (index * (long)input->nb_samples + sample) / 48000));
                    if (codec == "aac") { ((float*)input->data[0])[sample] = value; ((float*)input->data[1])[sample] = value; }
                    else { ((float*)input->data[0])[sample * 2] = value; ((float*)input->data[0])[sample * 2 + 1] = value; }
                }
                input->pts = index * (long)input->nb_samples;
                Check(ffmpeg.avcodec_send_frame(context, input));
                Receive(context, packet);
            }
            Check(ffmpeg.avcodec_send_frame(context, null));
            Receive(context, packet);
            var skip = codec == "opus" ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(configuration.AsSpan(10)) : 0;
            return new
            {
                codec, configuration = new { codec, sampleRate = 48000, channels = 2, csd = new[] { Convert.ToBase64String(configuration) },
                    codecDelayNs = skip * 1_000_000_000L / 48000, seekPrerollNs = 80000000 },
                packets
            };

            void Receive(AVCodecContext* encoderContext, AVPacket* encoded)
            {
                while (true)
                {
                    var result = ffmpeg.avcodec_receive_packet(encoderContext, encoded);
                    if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return;
                    Check(result);
                    packets.Add(new
                    {
                        data = Convert.ToBase64String(new ReadOnlySpan<byte>(encoded->data, encoded->size)),
                        ptsUs = origin + encoded->pts * 1_000_000 / 48000,
                        durationFrames = encoderContext->frame_size
                    });
                    ffmpeg.av_packet_unref(encoded);
                }
            }
        }
        finally { ffmpeg.av_packet_free(&packet); ffmpeg.av_frame_free(&input); ffmpeg.avcodec_free_context(&context); }
    }

    private static void Check(int result) { if (result < 0) throw new IOException($"Synthetic audio encoding failed: {result}."); }
}
