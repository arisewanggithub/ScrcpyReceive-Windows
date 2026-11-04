using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FFmpeg.AutoGen;
using ScrcpyReceive.Core;
using ScrcpyReceive.Media;
using Sender = ScrcpySend.Core;

namespace ScrcpyReceive.SrtInterop;

// Test-only encoded synthetic input; never captures, changes phone state, or changes the Windows sender.
internal sealed class SyntheticOpusSender : Sender.ISrtMedia
{
    private readonly byte[] head;
    private readonly byte[][] packets;
    private readonly int[] durations;
    private readonly LoopbackSrtListener transport = new();
    public long FramesProduced { get; private set; }

    internal SyntheticOpusSender(string fixture, bool mono = false, bool gain = false)
    {
        SyntheticVideo.Require(new FileInfo(fixture).Length <= 1024 * 1024, "Oversized synthetic Opus fixture.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(fixture));
        var root = document.RootElement;
        SyntheticVideo.Require(root.GetProperty("syntheticOnly").GetBoolean(), "Only synthetic Opus is allowed.");
        var audio = root.GetProperty("audio");
        var format = AudioFormat.Parse(audio.GetProperty("configuration"), "opus");
        head = format.Configuration.ToArray();
        if (mono) head[9] = 1;
        if (gain) BinaryPrimitives.WriteInt16LittleEndian(head.AsSpan(16), 1536);
        packets = audio.GetProperty("packets").EnumerateArray()
            .Select(packet => Convert.FromBase64String(packet.GetProperty("data").GetString()!)).ToArray();
        durations = audio.GetProperty("packets").EnumerateArray()
            .Select(packet => packet.GetProperty("durationFrames").GetInt32()).ToArray();
        SyntheticVideo.Require(packets.Length is > 0 and <= 512 && packets.All(packet => packet.Length is > 0 and <= 65536) &&
            durations.All(duration => duration is >= 120 and <= 5760), "Invalid synthetic Opus packets.");
    }

    public Sender.SrtDescriptor Prepare(IPAddress localAddress, IPAddress receiverAddress, long session) => transport.Prepare(session);

    public Task RunAsync(Sender.IVideoSource video, Sender.IAudioSource? audio, Sender.CaptureSettings settings, CancellationToken token) =>
        Task.Run(async () =>
        {
            await transport.AcceptAsync(token);
            await using var frames = video.ReadFramesAsync(token).GetAsyncEnumerator(token);
            SyntheticVideo.Require(await frames.MoveNextAsync(), "Missing synthetic video.");
            var first = frames.Current;
            using var mux = new OpusMuxer(settings.VideoCodec, video.Width, video.Height, first.Configuration, head,
                bytes => transport.Write(bytes, token));
            mux.WriteVideo(first, first.PtsUs);
            var next = frames.MoveNextAsync().AsTask();
            var start = Stopwatch.GetTimestamp();
            var flush = start;
            long samples = 0;
            var index = 0;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (next.IsCompleted)
                    {
                        SyntheticVideo.Require(await next, "Synthetic video ended.");
                        mux.WriteVideo(frames.Current, first.PtsUs);
                        next = frames.MoveNextAsync().AsTask();
                    }
                    while (samples * 1_000_000L / AudioFormat.Rate <= Stopwatch.GetElapsedTime(start).TotalMicroseconds)
                    {
                        mux.WriteAudio(packets[index], samples, durations[index]);
                        samples += durations[index];
                        FramesProduced = samples;
                        index = (index + 1) % packets.Length;
                    }
                    if (Stopwatch.GetElapsedTime(flush) >= TimeSpan.FromMilliseconds(100))
                    {
                        mux.Flush();
                        flush = Stopwatch.GetTimestamp();
                    }
                    await Task.Delay(2, token);
                }
            }
            finally
            {
                if (!next.IsCompleted)
                {
                    try { await next; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                }
            }
        }, CancellationToken.None);

    public void Abort() => transport.Dispose();
    public ValueTask DisposeAsync() { Abort(); return ValueTask.CompletedTask; }

    internal sealed class LoopbackSrtListener : IDisposable
    {
        private const string Library = "SyntheticOpusSrt";
        private static bool initialized;
        private int listener = -1, peer = -1;
        private string streamId = "";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_startup();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_cleanup();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_create_socket();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_close(int socket);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_setsockflag(int socket, int option, byte[] value, int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_getsockflag(int socket, int option, byte[] value, ref int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_bind(int socket, byte[] address, int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_getsockname(int socket, byte[] address, ref int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_listen(int socket, int backlog);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_accept(int socket, byte[] address, ref int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_sendmsg(int socket, byte[] data, int length, int ttl, int ordered);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int srt_getlasterror(nint error);

        internal Sender.SrtDescriptor Prepare(long session)
        {
            if (!initialized)
            {
                AudioDecoder.ValidateCodec("opus");
                NativeLibrary.SetDllImportResolver(typeof(SyntheticOpusSender).Assembly, (name, _, _) =>
                    name == Library ? NativeLibrary.Load(Path.Combine(ffmpeg.RootPath, "srt.dll")) : 0);
                Check(srt_startup());
                AppDomain.CurrentDomain.ProcessExit += (_, _) => srt_cleanup();
                initialized = true;
            }
            listener = srt_create_socket();
            Check(listener);
            foreach (var (option, value) in new[] { (50, 0), (1, 0), (2, 0), (31, 0), (49, 1316), (7, 0) })
                Check(srt_setsockflag(listener, option, BitConverter.GetBytes(value), 4));
            var address = new IPEndPoint(IPAddress.Loopback, 0).Serialize();
            var bytes = Enumerable.Range(0, address.Size).Select(index => address[index]).ToArray();
            Check(srt_bind(listener, bytes, bytes.Length));
            var size = bytes.Length;
            Check(srt_getsockname(listener, bytes, ref size));
            Check(srt_listen(listener, 1));
            streamId = $"sc-{session:x16}";
            return new($"srt://127.0.0.1:{BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2))}", streamId);
        }

        internal async Task AcceptAsync(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var address = new byte[128];
                var size = address.Length;
                var accepted = srt_accept(listener, address, ref size);
                if (accepted >= 0)
                {
                    peer = accepted;
                    var id = new byte[512];
                    size = id.Length;
                    Check(srt_getsockflag(peer, 46, id, ref size));
                    SyntheticVideo.Require(Encoding.UTF8.GetString(id, 0, size).TrimEnd('\0') == streamId, "Unpinned test SRT caller.");
                    return;
                }
                if (srt_getlasterror(0) != 6002) Check(accepted);
                await Task.Delay(5, token);
            }
        }

        internal void Write(byte[] data, CancellationToken token)
        {
            var offset = 0;
            var progress = Stopwatch.GetTimestamp();
            while (offset < data.Length)
            {
                token.ThrowIfCancellationRequested();
                var chunk = data.AsSpan(offset, Math.Min(1316, data.Length - offset)).ToArray();
                var written = srt_sendmsg(peer, chunk, chunk.Length, -1, 1);
                if (written > 0) { offset += written; progress = Stopwatch.GetTimestamp(); continue; }
                if (srt_getlasterror(0) != 6001) Check(written);
                if (Stopwatch.GetElapsedTime(progress) > TimeSpan.FromSeconds(3)) throw new IOException("Synthetic SRT send stopped progressing.");
                token.WaitHandle.WaitOne(5);
            }
        }

        private static void Check(int result)
        {
            if (result < 0) throw new IOException($"Synthetic SRT failed: {srt_getlasterror(0)}.");
        }

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref peer, -1);
            if (owned >= 0) srt_close(owned);
            owned = Interlocked.Exchange(ref listener, -1);
            if (owned >= 0) srt_close(owned);
        }
    }

    private sealed unsafe class OpusMuxer : IDisposable
    {
        private AVFormatContext* context;
        private AVIOContext* io;
        private readonly AVStream* video;
        private readonly AVStream* audio;
        private readonly avio_alloc_context_write_packet callback;
        private Exception? failure;
        private static uint Tag(string text) => (uint)(text[0] | text[1] << 8 | text[2] << 16 | text[3] << 24);

        internal OpusMuxer(string codec, int width, int height, byte[] configuration, byte[] head, Action<byte[]> write)
        {
            callback = (_, bytes, size) =>
            {
                try { write(new ReadOnlySpan<byte>(bytes, size).ToArray()); return size; }
                catch (Exception error) { failure = error; return -5; }
            };
            try
            {
                AVFormatContext* created = null;
                Check(ffmpeg.avformat_alloc_output_context2(&created, null, "mp4", null));
                context = created;
                context->avoid_negative_ts = ffmpeg.AVFMT_AVOID_NEG_TS_DISABLED;
                video = ffmpeg.avformat_new_stream(context, null);
                audio = ffmpeg.avformat_new_stream(context, null);
                if (video == null || audio == null) throw new OutOfMemoryException();
                video->time_base = new() { num = 1, den = 1_000_000 };
                video->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
                video->codecpar->codec_id = codec switch { "h264" => AVCodecID.AV_CODEC_ID_H264, "h265" => AVCodecID.AV_CODEC_ID_HEVC, _ => AVCodecID.AV_CODEC_ID_AV1 };
                video->codecpar->codec_tag = Tag(codec switch { "h264" => "avc3", "h265" => "hev1", _ => "av01" });
                video->codecpar->width = width;
                video->codecpar->height = height;
                Extra(video, configuration);
                audio->time_base = new() { num = 1, den = AudioFormat.Rate };
                audio->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
                audio->codecpar->codec_id = AVCodecID.AV_CODEC_ID_OPUS;
                audio->codecpar->codec_tag = Tag("Opus");
                audio->codecpar->sample_rate = AudioFormat.Rate;
                audio->codecpar->initial_padding = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
                ffmpeg.av_channel_layout_default(&audio->codecpar->ch_layout, head[9]);
                Extra(audio, head);
                var buffer = (byte*)ffmpeg.av_malloc(32768);
                if (buffer == null) throw new OutOfMemoryException();
                io = ffmpeg.avio_alloc_context(buffer, 32768, 1, null, null, callback, null);
                if (io == null) { ffmpeg.av_free(buffer); throw new OutOfMemoryException(); }
                context->pb = io;
                context->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
                AVDictionary* options = null;
                try
                {
                    ffmpeg.av_dict_set(&options, "movflags", "empty_moov+frag_custom+default_base_moof", 0);
                    ffmpeg.av_dict_set(&options, "use_editlist", "0", 0);
                    Check(ffmpeg.avformat_write_header(context, &options));
                    ffmpeg.avio_flush(io);
                    Check(0);
                }
                finally { ffmpeg.av_dict_free(&options); }
            }
            catch { Dispose(); throw; }
        }

        private static void Extra(AVStream* stream, byte[] bytes)
        {
            stream->codecpar->extradata = (byte*)ffmpeg.av_mallocz((ulong)(bytes.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
            if (stream->codecpar->extradata == null) throw new OutOfMemoryException();
            bytes.CopyTo(new Span<byte>(stream->codecpar->extradata, bytes.Length));
            stream->codecpar->extradata_size = bytes.Length;
        }

        internal void WriteVideo(Sender.EncodedVideo frame, long origin) =>
            Write(video, frame.Data, frame.PtsUs - origin, 1, CodecConfiguration.ValidateVideoFrame(
                video->codecpar->codec_id == AVCodecID.AV_CODEC_ID_H264 ? "h264" :
                video->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC ? "h265" : "av1", frame.Data, frame.Configuration));
        internal void WriteAudio(byte[] data, long samples, int duration) => Write(audio, data, samples, duration, true);

        private void Write(AVStream* stream, byte[] data, long pts, long duration, bool key)
        {
            var packet = ffmpeg.av_packet_alloc();
            if (packet == null) throw new OutOfMemoryException();
            try
            {
                Check(ffmpeg.av_new_packet(packet, data.Length));
                data.CopyTo(new Span<byte>(packet->data, data.Length));
                packet->stream_index = stream->index;
                packet->pts = packet->dts = pts;
                packet->duration = duration;
                packet->flags = key ? ffmpeg.AV_PKT_FLAG_KEY : 0;
                Check(ffmpeg.av_write_frame(context, packet));
            }
            finally { ffmpeg.av_packet_free(&packet); }
        }

        internal void Flush()
        {
            Check(ffmpeg.av_write_frame(context, null));
            ffmpeg.avio_flush(io);
            Check(0);
        }

        private void Check(int result)
        {
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            if (result < 0) throw new IOException($"Synthetic Opus MP4 failed: {result}.");
        }

        public void Dispose()
        {
            if (context != null) { ffmpeg.avformat_free_context(context); context = null; }
            if (io != null)
            {
                ffmpeg.av_free(io->buffer);
                var owned = io;
                ffmpeg.avio_context_free(&owned);
                io = null;
            }
            GC.KeepAlive(callback);
        }
    }
}
