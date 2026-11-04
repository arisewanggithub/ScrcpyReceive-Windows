using System.Runtime.InteropServices;
using System.Diagnostics;
using FFmpeg.AutoGen;
using ScrcpyReceive.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11VideoDevice = Vortice.Direct3D11.ID3D11VideoDevice;

namespace ScrcpyReceive.Media;

public sealed unsafe class HardwareDecoder : IDisposable
{
    private const int HardwareDeviceContextMethod = 1; // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX in codec.h.
    private static readonly Guid H264Profile = new("1b81be68-a0c7-11d3-b984-00c04f2e73c5");
    private readonly string codecName;
    private readonly Guid profile;
    private readonly AVCodecContext_get_format formatCallback = SelectHardwareFormat;
    private readonly ID3D11VideoDevice videoDevice;
    private AVBufferRef* hardware;
    private AVCodecContext* context;
    private AVCodec* codec;
    private bool needsKeyframe = true;
    private bool disposed;
    private int width;
    private int height;
    private byte[] configurationData = [];
    private string stage = "idle";
    private long stageAt, validationMaxUs, copyMaxUs, submissionMaxUs, receiveMaxUs, pauseMaxUs;
    private int surfaceCount;
    public const int AdditionalHardwareFrames = 3;
    public int MinimumHardwareSurfaces => (codecName == "av1" ? 9 : 17) + AdditionalHardwareFrames;
    public int HardwareSurfaceCount => Volatile.Read(ref surfaceCount);
    public string Diagnostics => $"decoder stage={Volatile.Read(ref stage)}, stageMs={(Volatile.Read(ref stage) == "idle" ? 0 : Environment.TickCount64 - Interlocked.Read(ref stageAt))}, " +
        $"validateMaxUs={Interlocked.Read(ref validationMaxUs)}, copyMaxUs={Interlocked.Read(ref copyMaxUs)}, " +
        $"submitMaxUs={Interlocked.Read(ref submissionMaxUs)}, receiveMaxUs={Interlocked.Read(ref receiveMaxUs)}, " +
        $"gcPauseMaxUs={Interlocked.Read(ref pauseMaxUs)}, surfaces={HardwareSurfaceCount}";

    private long Stage(string value)
    {
        Interlocked.Exchange(ref stageAt, Environment.TickCount64);
        Volatile.Write(ref stage, value);
        return Stopwatch.GetTimestamp();
    }

    private static void Measure(ref long maximum, long started) =>
        Interlocked.Exchange(ref maximum, Math.Max(Interlocked.Read(ref maximum), (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds));

    public HardwareDecoder(ID3D11Device device, string codecName = "h264")
    {
        this.codecName = codecName;
        profile = codecName switch
        {
            "h264" => H264Profile,
            "h265" => new Guid("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0"),
            "av1" => new Guid("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a"),
            _ => throw new NotSupportedException("Unsupported hardware video codec.")
        };
        NativeRuntime.Initialize();
        videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        try
        {
            videoDevice.CheckVideoDecoderFormat(profile, Format.NV12, out var supported).CheckError();
            if (!supported) throw new NotSupportedException($"Hardware {codecName}/NV12 decoding is unavailable. No software fallback.");
            codec = ffmpeg.avcodec_find_decoder_by_name(codecName == "h265" ? "hevc" : codecName);
            if (codec == null) throw new NotSupportedException($"Native {codecName} hardware decoder is missing.");
            var eligible = false;
            for (var index = 0; ; index++)
            {
                var configuration = ffmpeg.avcodec_get_hw_config(codec, index);
                if (configuration == null) break;
                eligible |= configuration->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA &&
                    configuration->pix_fmt == AVPixelFormat.AV_PIX_FMT_D3D11 &&
                    (configuration->methods & HardwareDeviceContextMethod) != 0;
            }
            if (!eligible) throw new NotSupportedException($"The pinned {codecName} decoder lacks D3D11VA device support.");
            hardware = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            if (hardware == null) throw new OutOfMemoryException();
            var hardwareContext = (AVHWDeviceContext*)hardware->data;
            var d3d = (AVD3D11VADeviceContext*)hardwareContext->hwctx;
            Marshal.AddRef(device.NativePointer);
            d3d->device = (FFmpeg.AutoGen.ID3D11Device*)device.NativePointer;
            NativeRuntime.Check(ffmpeg.av_hwdevice_ctx_init(hardware), "Initialize hardware D3D11VA decoder device");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Configure(byte[] configuration, int newWidth, int newHeight)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CodecConfiguration.ValidateVideo(codecName, configuration, newWidth, newHeight);
        configurationData = configuration;
        var description = new VideoDecoderDescription
        {
            Guid = profile,
            SampleWidth = (uint)newWidth,
            SampleHeight = (uint)newHeight,
            OutputFormat = Format.NV12
        };
        videoDevice.GetVideoDecoderConfigCount(ref description, out var configurations).CheckError();
        if (configurations == 0) throw new NotSupportedException($"Hardware {codecName} decoder cannot handle {newWidth}x{newHeight}.");
        if (context != null)
        {
            NativeRuntime.Check(ffmpeg.avcodec_send_packet(context, null), "Drain old decoder generation");
            using var remaining = Receive();
            if (remaining is not null) throw new ProtocolException("Decoder retained a delayed frame during reconfiguration.");
            FreeContext();
        }
        context = ffmpeg.avcodec_alloc_context3(codec);
        if (context == null) throw new OutOfMemoryException();
        context->hw_device_ctx = ffmpeg.av_buffer_ref(hardware);
        if (context->hw_device_ctx == null) throw new OutOfMemoryException();
        context->get_format = formatCallback;
        context->thread_count = 1;
        // Application-owned displayed, pending and in-progress frames must not exhaust the codec's reference surfaces.
        context->extra_hw_frames = AdditionalHardwareFrames;
        context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        context->pkt_timebase = new AVRational { num = 1, den = 1_000_000 };
        context->extradata = (byte*)ffmpeg.av_mallocz((ulong)(configuration.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        if (context->extradata == null) throw new OutOfMemoryException();
        configuration.CopyTo(new Span<byte>(context->extradata, configuration.Length));
        context->extradata_size = configuration.Length;
        NativeRuntime.Check(ffmpeg.avcodec_open2(context, codec, null), $"Open D3D11VA {codecName} decoder");
        width = newWidth;
        height = newHeight;
        needsKeyframe = true;
    }

    public IReadOnlyList<HardwareFrame> Decode(Packet packet)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (context == null) throw new InvalidOperationException("Decoder not configured.");
        var pauseBefore = GC.GetTotalPauseDuration();
        try
        {
            var started = Stage("validate");
            var idr = CodecConfiguration.ValidateVideoFrame(codecName, packet.Payload, configurationData);
            JsonObject.Require(idr == (packet.Flags == 1), $"{codecName} key flag differs from the actual random-access picture.");
            JsonObject.Require(!needsKeyframe || idr, "A new generation must start with a real random-access picture.");
            needsKeyframe = false;
            Measure(ref validationMaxUs, started);
            started = Stage("packet-copy");
            var input = ffmpeg.av_packet_alloc();
            if (input == null) throw new OutOfMemoryException();
            try
            {
                NativeRuntime.Check(ffmpeg.av_new_packet(input, packet.Payload.Length), "Allocate encoded packet");
                packet.Payload.CopyTo(new Span<byte>(input->data, packet.Payload.Length));
                input->pts = input->dts = packet.PtsUs;
                input->flags = idr ? ffmpeg.AV_PKT_FLAG_KEY : 0;
                Measure(ref copyMaxUs, started);
                started = Stage("submit");
                int result;
                try { result = ffmpeg.avcodec_send_packet(context, input); }
                finally { Measure(ref submissionMaxUs, started); }
                NativeRuntime.Check(result, "Submit hardware video packet");
            }
            finally { ffmpeg.av_packet_free(&input); }
            var frames = new List<HardwareFrame>();
            Stage("receive");
            try
            {
                while (Receive() is { } frame)
                {
                    frames.Add(frame);
                    JsonObject.Require(frames.Count <= 1, "A progressive VIDEO_FRAME produced more than one picture.", "PROTOCOL");
                    JsonObject.Require(context->has_b_frames == 0 && frame.Width == width && frame.Height == height,
                        "Unexpected decoder reordering or unannounced dimensions.");
                }
                return frames;
            }
            catch
            {
                foreach (var frame in frames) frame.Dispose();
                throw;
            }
        }
        finally
        {
            Interlocked.Exchange(ref pauseMaxUs, Math.Max(Interlocked.Read(ref pauseMaxUs),
                (long)(GC.GetTotalPauseDuration() - pauseBefore).TotalMicroseconds));
            Stage("idle");
        }
    }

    private HardwareFrame? Receive()
    {
        var frame = ffmpeg.av_frame_alloc();
        if (frame == null) throw new OutOfMemoryException();
        try
        {
            var started = Stopwatch.GetTimestamp();
            int result;
            try { result = ffmpeg.avcodec_receive_frame(context, frame); }
            finally { Measure(ref receiveMaxUs, started); }
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return null;
            NativeRuntime.Check(result, "Receive hardware video frame");
            if (frame->format != (int)AVPixelFormat.AV_PIX_FMT_D3D11 || frame->hw_frames_ctx == null || frame->data[0] == null)
                throw new NotSupportedException("Decoder did not produce a hardware D3D11 surface. Software output is forbidden.");
            var pool = (AVHWFramesContext*)frame->hw_frames_ctx->data;
            Volatile.Write(ref surfaceCount, pool->initial_pool_size);
            JsonObject.Require(pool->initial_pool_size >= MinimumHardwareSurfaces && pool->initial_pool_size <= 64,
                "Hardware decoder did not allocate the bounded reserved surface pool.", "DECODER_FAILED");
            if (pool->sw_format != AVPixelFormat.AV_PIX_FMT_NV12)
                throw new NotSupportedException("Only hardware NV12 output is currently supported.");
            var output = new HardwareFrame(frame);
            frame = null;
            return output;
        }
        finally { if (frame != null) ffmpeg.av_frame_free(&frame); }
    }

    private static AVPixelFormat SelectHardwareFormat(AVCodecContext* context, AVPixelFormat* choices)
    {
        for (var index = 0; choices[index] != AVPixelFormat.AV_PIX_FMT_NONE; index++)
            if (choices[index] == AVPixelFormat.AV_PIX_FMT_D3D11) return choices[index];
        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        FreeContext();
        var buffer = hardware;
        hardware = null;
        ffmpeg.av_buffer_unref(&buffer);
        videoDevice.Dispose();
        GC.KeepAlive(formatCallback);
    }

    private void FreeContext()
    {
        var value = context;
        context = null;
        ffmpeg.avcodec_free_context(&value);
    }
}

public sealed unsafe class HardwareFrame : IDisposable
{
    private AVFrame* frame;
    internal HardwareFrame(AVFrame* frame) => this.frame = frame;
    public nint Texture => (nint)frame->data[0];
    public uint ArraySlice => checked((uint)(nuint)frame->data[1]);
    public int Width => frame->width;
    public int Height => frame->height;
    public long PtsUs => frame->pts;
    public uint ColorMatrix => frame->colorspace switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => 1,
        AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M => 0,
        AVColorSpace.AVCOL_SPC_UNSPECIFIED => frame->height > 576 ? 1u : 0u,
        _ => throw new NotSupportedException("Only SDR BT.601/709 video color matrices are supported.")
    };
    public uint NominalRange => frame->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 2u : 1u;
    public HardwareFrame Clone()
    {
        var copy = ffmpeg.av_frame_clone(frame);
        if (copy == null) throw new OutOfMemoryException();
        return new HardwareFrame(copy);
    }
    public void Dispose()
    {
        var value = frame;
        frame = null;
        ffmpeg.av_frame_free(&value);
    }
}
