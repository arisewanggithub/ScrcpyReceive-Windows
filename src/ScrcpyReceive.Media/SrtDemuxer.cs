using System.Buffers.Binary;
using System.Text.Json;
using FFmpeg.AutoGen;
using ScrcpyReceive.Core;

namespace ScrcpyReceive.Media;

internal sealed unsafe class SrtDemuxer : IDisposable
{
    internal const int MaximumVideo = 4 * 1024 * 1024;
    internal const int MaximumAudio = 256 * 1024;
    private const int MaximumExtra = 45000;
    private readonly CastRequest request;
    private readonly Func<Packet, CancellationToken, ValueTask> emit;
    private readonly CancellationToken token;
    private readonly SrtFragmentReader input;
    private readonly avio_alloc_context_read_packet readCallback;
    private readonly AVIOInterruptCB_callback interruptCallback;
    private readonly AVFormatContext_io_open rejectOpen;
    private readonly List<Track> tracks = [];
    private AVFormatContext* format;
    private AVIOContext* io;
    private Exception? readFailure;

    private sealed class Track(AVStream* stream, uint number)
    {
        internal readonly AVStream* Stream = stream;
        internal readonly uint Number = number;
        internal AVBSFContext* Filter;
        internal AVCodecParserContext* Parser;
        internal AVCodecContext* ParserCodec;
        internal byte[] Source = [], Configuration = [];
        internal int Width, Height;
        // Compare future container declarations with their last snapshot, not with newer in-band dimensions.
        internal int MetadataWidth, MetadataHeight, NalLengthSize;
        internal readonly Dictionary<(int Type, uint Id), Parameter> Parameters = [];
        internal uint Generation;
        internal long LastPts = -1;
    }

    internal static void Preflight(string codec, string? audio)
    {
        NativeRuntime.Initialize();
        if (!File.Exists(Path.Combine(NativeRuntime.RootPath, "avformat-61.dll")) ||
            ffmpeg.avformat_version() >> 16 != 61)
            throw new NotSupportedException("Pinned FFmpeg avformat 61 is missing. Run tools\\PrepareSrt.ps1 and rebuild.");
        if (ffmpeg.av_find_input_format("mov") == null)
            throw new NotSupportedException("Pinned FFmpeg MP4 demuxer is unavailable.");
        if (codec != "av1" && ffmpeg.av_bsf_get_by_name(FilterName(codec)) == null)
            throw new NotSupportedException("Required MP4-to-Annex-B bitstream filter is unavailable.");
        if (codec == "av1")
        {
            var parser = ffmpeg.av_parser_init((int)AVCodecID.AV_CODEC_ID_AV1);
            if (parser == null) throw new NotSupportedException("Required AV1 frame-header parser is unavailable.");
            ffmpeg.av_parser_close(parser);
        }
        if (audio is not null && (ffmpeg.avcodec_find_decoder_by_name(audio) == null ||
            !File.Exists(Path.Combine(NativeRuntime.RootPath, "swresample-5.dll")) ||
            ffmpeg.swresample_version() >> 16 != 5))
            throw new NotSupportedException($"Pinned {audio} decoder/sample-conversion runtime is unavailable.");
    }

    private static string FilterName(string codec) => codec == "h264" ? "h264_mp4toannexb" : "hevc_mp4toannexb";
    private static uint Tag(string text) => (uint)(text[0] | text[1] << 8 | text[2] << 16 | text[3] << 24);

    internal SrtDemuxer(CastRequest request, SrtTransport transport,
        Func<Packet, CancellationToken, ValueTask> emit, CancellationToken token)
    {
        this.request = request;
        this.emit = emit;
        this.token = token;
        input = new(transport, token);
        readCallback = Read;
        interruptCallback = _ => token.IsCancellationRequested ? 1 : 0;
        rejectOpen = (_, _, _, _, _) => -13;
    }

    private int Read(void* opaque, byte* buffer, int size)
    {
        if (readFailure is not null) return ffmpeg.AVERROR_EXIT;
        try { return input.Read(new Span<byte>(buffer, size)); }
        catch (Exception error) { readFailure ??= error; return ffmpeg.AVERROR_EXIT; }
    }

    private void Check(int result, string operation)
    {
        token.ThrowIfCancellationRequested();
        if (readFailure is EndOfStreamException ended) throw ended;
        if (readFailure is ProtocolException invalid) throw invalid;
        if (readFailure is not null) throw new IOException("SRT/fMP4 input failed.", readFailure);
        if (result == ffmpeg.AVERROR_EOF) throw new EndOfStreamException("Live fMP4 ended without control STOP.");
        NativeRuntime.Check(result, operation);
    }

    internal void Run()
    {
        token.ThrowIfCancellationRequested();
        format = ffmpeg.avformat_alloc_context();
        if (format == null) throw new OutOfMemoryException();
        format->max_streams = request.AudioCodec is null ? 1 : 2;
        format->probesize = MaximumAudio;
        format->max_analyze_duration = 0;
        format->max_index_size = MaximumAudio;
        format->max_probe_packets = 1;
        format->interrupt_callback = new() { callback = interruptCallback };
        format->io_open = rejectOpen;
        var buffer = (byte*)ffmpeg.av_malloc(32768);
        if (buffer == null) throw new OutOfMemoryException();
        io = ffmpeg.avio_alloc_context(buffer, 32768, 0, null, readCallback, null, null);
        if (io == null) { ffmpeg.av_free(buffer); throw new OutOfMemoryException(); }
        io->seekable = 0;
        format->pb = io;
        format->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
        AVDictionary* options = null;
        try
        {
            ffmpeg.av_dict_set(&options, "enable_drefs", "0", 0);
            ffmpeg.av_dict_set(&options, "use_absolute_path", "0", 0);
            ffmpeg.av_dict_set(&options, "ignore_editlist", "1", 0);
            ffmpeg.av_dict_set(&options, "use_mfra_for", "0", 0);
            ffmpeg.av_dict_set(&options, "format_whitelist", "mov", 0);
            ffmpeg.av_dict_set(&options, "protocol_whitelist", "", 0);
            var owned = format;
            var result = ffmpeg.avformat_open_input(&owned, null, ffmpeg.av_find_input_format("mov"), &options);
            format = owned;
            Check(result, "Open custom-IO live fragmented MP4");
        }
        finally { ffmpeg.av_dict_free(&options); }

        JsonObject.Require(format->nb_streams == request.Tracks.Length,
            "fMP4 tracks differ from the explicit request.", "UNSUPPORTED_CONFIG");
        for (var index = 0; index < format->nb_streams; index++)
        {
            var stream = format->streams[index];
            var video = stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO;
            var number = video ? 1u : 2u;
            JsonObject.Require(!tracks.Any(track => track.Number == number),
                "Duplicate/unsupported fMP4 track.", "UNSUPPORTED_CONFIG");
            var track = new Track(stream, number);
            tracks.Add(track);
            Configure(track);
        }
        JsonObject.Require(tracks.Any(track => track.Number == 1) &&
            (request.AudioCodec is null || tracks.Any(track => track.Number == 2)),
            "Missing requested fMP4 track.", "UNSUPPORTED_CONFIG");
        foreach (var track in tracks.OrderBy(track => track.Number)) EmitConfiguration(track);

        var packet = ffmpeg.av_packet_alloc();
        var filtered = ffmpeg.av_packet_alloc();
        if (packet == null || filtered == null)
        {
            ffmpeg.av_packet_free(&packet); ffmpeg.av_packet_free(&filtered);
            throw new OutOfMemoryException();
        }
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Check(ffmpeg.av_read_frame(format, packet), "Read fMP4 sample");
                try
                {
                    JsonObject.Require(format->nb_streams == tracks.Count, "Undeclared fMP4 track change.");
                    var streamIndex = packet->stream_index;
                    var track = tracks.SingleOrDefault(item => item.Stream->index == streamIndex)
                        ?? throw new ProtocolException("Unknown fMP4 packet track.");
                    var maximum = track.Number == 1 ? MaximumVideo : MaximumAudio;
                    JsonObject.Require(packet->size is > 0 && packet->size <= maximum,
                        "fMP4 packet exceeds wire bounds.");
                    ulong extraSize = 0;
                    var extra = ffmpeg.av_packet_get_side_data(packet, AVPacketSideDataType.AV_PKT_DATA_NEW_EXTRADATA, &extraSize);
                    if (extra != null)
                    {
                        JsonObject.Require(extraSize is > 0 and <= MaximumExtra, "Invalid changed fMP4 CSD.");
                        var parameters = track.Stream->codecpar;
                        var replacement = (byte*)ffmpeg.av_mallocz(extraSize + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
                        if (replacement == null) throw new OutOfMemoryException();
                        new ReadOnlySpan<byte>(extra, (int)extraSize).CopyTo(new Span<byte>(replacement, (int)extraSize));
                        ffmpeg.av_free(parameters->extradata);
                        parameters->extradata = replacement;
                        parameters->extradata_size = (int)extraSize;
                    }
                    ApplyParameterChanges(track, packet);
                    if (Configure(track)) EmitConfiguration(track);
                    if (track.Filter == null)
                        EmitMedia(track, packet, track.Stream->time_base);
                    else
                    {
                        RememberSampleParameters(track, new ReadOnlySpan<byte>(packet->data, packet->size));
                        Check(ffmpeg.av_bsf_send_packet(track.Filter, packet), "Convert MP4 sample to Annex B");
                        var count = 0;
                        while (true)
                        {
                            var result = ffmpeg.av_bsf_receive_packet(track.Filter, filtered);
                            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN)) break;
                            Check(result, "Read Annex B sample");
                            try { EmitMedia(track, filtered, track.Filter->time_base_out); count++; }
                            finally { ffmpeg.av_packet_unref(filtered); }
                        }
                        JsonObject.Require(count == 1, "MP4 conversion must yield one access unit per sample.");
                    }
                }
                finally { ffmpeg.av_packet_unref(packet); }
            }
        }
        finally { ffmpeg.av_packet_free(&packet); ffmpeg.av_packet_free(&filtered); }
    }

    private static void ApplyParameterChanges(Track track, AVPacket* packet)
    {
        ulong size = 0;
        var pointer = ffmpeg.av_packet_get_side_data(packet, AVPacketSideDataType.AV_PKT_DATA_PARAM_CHANGE, &size);
        if (pointer == null) return;
        JsonObject.Require(size is >= 4 and <= 32, "Invalid fMP4 parameter-change declaration.");
        var length = (int)size;
        var data = new ReadOnlySpan<byte>(pointer, length);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data);
        JsonObject.Require((flags & ~15u) == 0, "Unsupported fMP4 parameter change.");
        var offset = 4;
        int Read()
        {
            JsonObject.Require(offset + 4 <= length, "Truncated fMP4 parameter change.");
            var value = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(pointer + offset, 4));
            offset += 4;
            return value;
        }
        if ((flags & 1) != 0)
            JsonObject.Require(track.Number == 2 && Read() == track.Stream->codecpar->ch_layout.nb_channels,
                "fMP4 audio channel change is unsupported.", "UNSUPPORTED_CONFIG");
        if ((flags & 2) != 0)
        {
            JsonObject.Require(track.Number == 2 && Read() == (track.Stream->codecpar->ch_layout.nb_channels == 1 ? 4 : 3) && Read() == 0,
                "fMP4 audio layout change is unsupported.", "UNSUPPORTED_CONFIG");
        }
        if ((flags & 4) != 0)
            JsonObject.Require(track.Number == 2 && Read() == 48000, "fMP4 audio rate change is unsupported.", "UNSUPPORTED_CONFIG");
        if ((flags & 8) != 0)
        {
            JsonObject.Require(track.Number == 1, "Audio packet declares video dimensions.");
            track.Stream->codecpar->width = Read();
            track.Stream->codecpar->height = Read();
        }
        JsonObject.Require(offset == (int)size, "Trailing fMP4 parameter-change data.");
    }

    private bool Configure(Track track)
    {
        var parameters = track.Stream->codecpar;
        JsonObject.Require(track.Stream->time_base.num > 0 && track.Stream->time_base.den > 0,
            "Invalid fMP4 track timebase.");
        var video = track.Number == 1;
        var expected = video ? request.VideoCodec switch
        {
            "h264" => AVCodecID.AV_CODEC_ID_H264,
            "h265" => AVCodecID.AV_CODEC_ID_HEVC,
            _ => AVCodecID.AV_CODEC_ID_AV1
        } : request.AudioCodec == "opus" ? AVCodecID.AV_CODEC_ID_OPUS : AVCodecID.AV_CODEC_ID_AAC;
        JsonObject.Require(parameters->codec_type == (video ? AVMediaType.AVMEDIA_TYPE_VIDEO : AVMediaType.AVMEDIA_TYPE_AUDIO) &&
            parameters->codec_id == expected && (video
                ? parameters->codec_tag == Tag(request.VideoCodec switch { "h264" => "avc3", "h265" => "hev1", _ => "av01" })
                : parameters->sample_rate == AudioFormat.Rate &&
                  (request.AudioCodec == "opus"
                      ? parameters->codec_tag == Tag("Opus") && parameters->ch_layout.nb_channels is 1 or 2
                      : request.AudioCodec == "aac" && parameters->codec_tag == Tag("mp4a") && parameters->ch_layout.nb_channels == 2)),
            "fMP4 codec/tag/track differs from the explicit request.", "UNSUPPORTED_CONFIG");
        JsonObject.Require(parameters->extradata != null && parameters->extradata_size is > 0 and <= MaximumExtra,
            "Missing or oversized fMP4 codec configuration.", "UNSUPPORTED_CONFIG");
        if (video)
            JsonObject.Require(parameters->width is > 0 and <= 16384 && parameters->height is > 0 and <= 16384,
                "Invalid fMP4 video dimensions.", "UNSUPPORTED_CONFIG");
        var source = new ReadOnlySpan<byte>(parameters->extradata, parameters->extradata_size);
        if (track.Generation != 0 && track.MetadataWidth == parameters->width && track.MetadataHeight == parameters->height &&
            source.SequenceEqual(track.Source)) return false;
        FreeConverters(track);
        track.Source = source.ToArray();
        track.Width = parameters->width;
        track.Height = parameters->height;
        track.MetadataWidth = parameters->width;
        track.MetadataHeight = parameters->height;
        if (video && request.VideoCodec != "av1")
        {
            AVBSFContext* filter = null;
            Check(ffmpeg.av_bsf_alloc(ffmpeg.av_bsf_get_by_name(FilterName(request.VideoCodec)), &filter),
                "Allocate Annex B converter");
            track.Filter = filter;
            Check(ffmpeg.avcodec_parameters_copy(filter->par_in, parameters), "Copy MP4 parameters");
            filter->time_base_in = track.Stream->time_base;
            Check(ffmpeg.av_bsf_init(filter), "Initialize Annex B converter");
            var output = filter->par_out;
            JsonObject.Require(output->extradata != null && output->extradata_size is > 0 and <= MaximumExtra,
                "Annex B CSD exceeds configuration bounds.");
            track.Configuration = new ReadOnlySpan<byte>(output->extradata, output->extradata_size).ToArray();
            track.NalLengthSize = source[0] == 1
                ? (request.VideoCodec == "h264" ? source[4] & 3 : source[21] & 3) + 1 : 0;
            track.Parameters.Clear();
            foreach (var unit in H264Stream.Units(track.Configuration)) RememberParameter(track, unit.Span);
            track.Configuration = AnnexB(track.Parameters.Values.OrderBy(item => item.Type).ThenBy(item => item.Id)
                .Select(item => (ReadOnlyMemory<byte>)item.Data), MaximumExtra);
        }
        else
        {
            track.Configuration = video ? Av1Stream.Configuration(SizedAv1(track.Source, true)) : track.Source;
            if (video)
            {
                track.Parser = ffmpeg.av_parser_init((int)AVCodecID.AV_CODEC_ID_AV1);
                track.ParserCodec = ffmpeg.avcodec_alloc_context3(null);
                if (track.Parser == null || track.ParserCodec == null) throw new OutOfMemoryException("AV1 parser initialization failed.");
                track.Parser->flags |= ffmpeg.PARSER_FLAG_COMPLETE_FRAMES;
                Check(ffmpeg.avcodec_parameters_to_context(track.ParserCodec, parameters), "Initialize AV1 frame-header parameters");
            }
        }
        if (video) CodecConfiguration.ValidateVideo(request.VideoCodec, track.Configuration, track.Width, track.Height);
        track.Generation = checked(track.Generation + 1);
        JsonObject.Require(track.Generation <= int.MaxValue, "Too many fMP4 generations.");
        track.LastPts = -1;
        return true;
    }

    private void EmitConfiguration(Track track)
    {
        var csd = new[] { Convert.ToBase64String(track.Configuration) };
        byte[] payload;
        if (track.Number == 1)
            payload = JsonSerializer.SerializeToUtf8Bytes(new { codec = request.VideoCodec, width = track.Width, height = track.Height, csd });
        else
        {
            var channels = track.Stream->codecpar->ch_layout.nb_channels;
            if (request.AudioCodec == "opus")
            {
                JsonObject.Require(track.Configuration.Length == 19, "Canonical MP4 OpusHead is required.", "UNSUPPORTED_CONFIG");
                var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(track.Configuration.AsSpan(10));
                payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    codec = "opus", sampleRate = AudioFormat.Rate, channels, csd,
                    codecDelayNs = preSkip * 1_000_000_000L / AudioFormat.Rate, seekPrerollNs = 80_000_000
                });
            }
            else payload = JsonSerializer.SerializeToUtf8Bytes(new { codec = "aac", sampleRate = AudioFormat.Rate, channels, csd });
            _ = AudioFormat.Parse(JsonObject.Parse(payload), request.AudioCodec!);
        }
        JsonObject.Require(payload.Length <= 65536, "fMP4 configuration exceeds wire bounds.");
        Emit(new(PacketType.StreamConfig, 0, track.Number, track.Generation, -1, 0, payload));
    }

    private void EmitMedia(Track track, AVPacket* packet, AVRational timebase)
    {
        var maximum = track.Number == 1 ? MaximumVideo : MaximumAudio;
        JsonObject.Require(packet->data != null && packet->size is > 0 && packet->size <= maximum &&
            packet->pts != ffmpeg.AV_NOPTS_VALUE && packet->pts >= 0 &&
            (packet->flags & (ffmpeg.AV_PKT_FLAG_CORRUPT | ffmpeg.AV_PKT_FLAG_DISCARD)) == 0,
            "Invalid fMP4 media sample.");
        var pts = ffmpeg.av_rescale_q(packet->pts, timebase, new() { num = 1, den = 1_000_000 });
        JsonObject.Require(pts >= 0 && pts >= track.LastPts, "fMP4 media PTS moved backwards.");
        var data = new ReadOnlySpan<byte>(packet->data, packet->size).ToArray();
        var key = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
        if (track.Number == 1)
        {
            byte[] candidate;
            (int Width, int Height)? frameDimensions = null;
            if (request.VideoCodec == "av1")
            {
                data = SizedAv1(data, false);
                candidate = SequenceConfiguration(data) ?? track.Configuration;
                frameDimensions = Av1PictureDimensions(track, data, key);
            }
            else (data, candidate) = NalPicture(track, data, key);
            if (!candidate.AsSpan().SequenceEqual(track.Configuration) ||
                frameDimensions is { } size && (size.Width != track.Width || size.Height != track.Height))
            {
                var dimensions = frameDimensions ?? (request.VideoCodec switch
                {
                    "h264" => H264Stream.Dimensions(candidate),
                    "h265" => HevcStream.Dimensions(candidate),
                    _ => Av1Stream.Dimensions(candidate)
                });
                JsonObject.Require(dimensions.Width is > 0 and <= 16384 && dimensions.Height is > 0 and <= 16384,
                    "Invalid changed in-band video dimensions.");
                CodecConfiguration.ValidateVideo(request.VideoCodec, candidate, dimensions.Width, dimensions.Height);
                JsonObject.Require(key && CodecConfiguration.ValidateVideoFrame(request.VideoCodec, data, candidate),
                    "An in-band configuration change requires an authentic random-access picture.");
                track.Configuration = candidate;
                track.Width = dimensions.Width;
                track.Height = dimensions.Height;
                track.Generation = checked(track.Generation + 1);
                JsonObject.Require(track.Generation <= int.MaxValue, "Too many fMP4 generations.");
                EmitConfiguration(track);
            }
            JsonObject.Require(CodecConfiguration.ValidateVideoFrame(request.VideoCodec, data, track.Configuration) == key,
                "fMP4 sync flag differs from the actual random-access picture.");
            JsonObject.Require(track.LastPts >= 0 || key, "A new fMP4 generation requires a real keyframe.");
        }
        track.LastPts = pts;
        Emit(new(track.Number == 1 ? PacketType.VideoFrame : PacketType.AudioPacket, 0,
            track.Number, track.Generation, pts, track.Number == 1 && key ? 1u : 0u, data));
    }

    private (int Width, int Height) Av1PictureDimensions(Track track, byte[] data, bool key)
    {
        // Sequence maximum dimensions need not change when a coded AV1 frame rotates.
        // The native parser reads headers only; no decoder is opened and no pixels are produced.
        var buffer = (byte*)ffmpeg.av_mallocz((ulong)data.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
        if (buffer == null) throw new OutOfMemoryException();
        try
        {
            data.CopyTo(new Span<byte>(buffer, data.Length));
            track.Parser->width = track.Parser->height = 0;
            track.Parser->key_frame = -1;
            byte* parsed = null;
            var size = 0;
            var consumed = ffmpeg.av_parser_parse2(track.Parser, track.ParserCodec, &parsed, &size,
                buffer, data.Length, ffmpeg.AV_NOPTS_VALUE, ffmpeg.AV_NOPTS_VALUE, -1);
            Check(consumed, "Parse AV1 displayed frame header");
            JsonObject.Require(consumed == data.Length && size == data.Length &&
                track.Parser->width is > 0 and <= 16384 && track.Parser->height is > 0 and <= 16384 &&
                track.Parser->key_frame == (key ? 1 : 0), "Invalid AV1 frame dimensions or authentic sync flag.");
            return (track.Parser->width, track.Parser->height);
        }
        finally { ffmpeg.av_free(buffer); }
    }

    private sealed record Parameter(int Type, uint Id, uint Parent, byte[] Data);

    private sealed class ParameterBits
    {
        private readonly byte[] data;
        private int position;
        internal ParameterBits(ReadOnlySpan<byte> escaped)
        {
            var prefix = Math.Min(escaped.Length, 256);
            var bytes = new List<byte>(prefix);
            var zeros = 0;
            for (var index = 0; index < prefix; index++)
            {
                var value = escaped[index];
                if (zeros >= 2 && value == 3)
                {
                    JsonObject.Require(index + 1 < escaped.Length && escaped[index + 1] <= 3,
                        "Invalid in-band emulation prevention.");
                    zeros = 0;
                    continue;
                }
                bytes.Add(value);
                zeros = value == 0 ? zeros + 1 : 0;
            }
            data = bytes.ToArray();
        }
        internal uint Read(int count)
        {
            JsonObject.Require(count is >= 0 and <= 32 && (long)position + count <= data.Length * 8L,
                "Truncated in-band parameter/slice identifier.");
            uint result = 0;
            for (var index = 0; index < count; index++, position++)
                result = (result << 1) | (uint)(data[position / 8] >> (7 - position % 8) & 1);
            return result;
        }
        internal void Skip(int count)
        {
            while (count > 0) { var part = Math.Min(32, count); Read(part); count -= part; }
        }
        internal uint Unsigned()
        {
            var leading = 0;
            while (Read(1) == 0) JsonObject.Require(++leading < 32, "Oversized in-band parameter identifier.");
            return ((1u << leading) - 1) + Read(leading);
        }
    }

    private int NalType(ReadOnlySpan<byte> unit)
    {
        JsonObject.Require(unit.Length > 0 && (unit[0] & 128) == 0, "Invalid in-band NAL header.");
        if (request.VideoCodec == "h264") return unit[0] & 31;
        JsonObject.Require(unit.Length >= 3 && (unit[0] & 1) == 0 && (unit[1] >> 3) == 0 && (unit[1] & 7) != 0,
            "Unsupported in-band HEVC NAL header/layer.", "UNSUPPORTED_CONFIG");
        return unit[0] >> 1 & 63;
    }

    private bool IsParameter(int type) => request.VideoCodec == "h264" ? type is 7 or 8 : type is 32 or 33 or 34;

    private Parameter? ParseParameter(ReadOnlySpan<byte> unit)
    {
        var type = NalType(unit);
        if (!IsParameter(type)) return null;
        JsonObject.Require(unit.Length <= MaximumExtra, "In-band parameter set exceeds CSD bounds.");
        var bits = new ParameterBits(unit[(request.VideoCodec == "h264" ? 1 : 2)..]);
        uint id, parent = 0;
        if (request.VideoCodec == "h264")
        {
            if (type == 7) { bits.Skip(24); id = bits.Unsigned(); }
            else { id = bits.Unsigned(); parent = bits.Unsigned(); }
            JsonObject.Require(id <= (type == 7 ? 31u : 255u) && parent <= 31, "Invalid H.264 parameter-set IDs.");
        }
        else
        {
            if (type == 32) id = bits.Read(4);
            else if (type == 34) { id = bits.Unsigned(); parent = bits.Unsigned(); }
            else
            {
                parent = bits.Read(4);
                var layers = (int)bits.Read(3);
                JsonObject.Require(layers <= 6, "Invalid HEVC sublayer count.");
                bits.Skip(1 + 96);
                var profiles = new bool[layers];
                var levels = new bool[layers];
                for (var index = 0; index < layers; index++) { profiles[index] = bits.Read(1) != 0; levels[index] = bits.Read(1) != 0; }
                if (layers > 0) bits.Skip((8 - layers) * 2);
                for (var index = 0; index < layers; index++) { if (profiles[index]) bits.Skip(88); if (levels[index]) bits.Skip(8); }
                id = bits.Unsigned();
            }
            JsonObject.Require(id <= (type == 34 ? 63u : 15u) && parent <= 15, "Invalid HEVC parameter-set IDs.");
        }
        return new(type, id, parent, unit.ToArray());
    }

    private void RememberParameter(Track track, ReadOnlySpan<byte> unit,
        Dictionary<(int Type, uint Id), Parameter>? declared = null)
    {
        while (!unit.IsEmpty && unit[^1] == 0) unit = unit[..^1];
        var parameter = ParseParameter(unit);
        if (parameter is null) return;
        var key = (parameter.Type, parameter.Id);
        if (declared is not null)
        {
            JsonObject.Require(!declared.TryGetValue(key, out var previous) || previous.Data.AsSpan().SequenceEqual(parameter.Data),
                "Conflicting parameter-set declarations in one MP4 access unit.");
            declared[key] = parameter;
        }
        var bytes = track.Parameters.Values.Sum(item => item.Data.Length + 4);
        if (track.Parameters.TryGetValue(key, out var known)) bytes -= known.Data.Length + 4;
        JsonObject.Require(bytes + parameter.Data.Length + 4 <= MaximumExtra, "In-band parameter-set cache exceeds CSD bounds.");
        track.Parameters[key] = parameter;
    }

    private void RememberSampleParameters(Track track, ReadOnlySpan<byte> sample)
    {
        var declared = new Dictionary<(int Type, uint Id), Parameter>();
        if (track.NalLengthSize == 0)
        {
            foreach (var unit in H264Stream.Units(sample.ToArray())) RememberParameter(track, unit.Span, declared);
            return;
        }
        var offset = 0;
        var count = 0;
        while (offset < sample.Length)
        {
            JsonObject.Require(++count <= 4096 && offset + track.NalLengthSize <= sample.Length, "Invalid MP4 NAL count/length.");
            uint length = 0;
            for (var index = 0; index < track.NalLengthSize; index++) length = length << 8 | sample[offset++];
            JsonObject.Require(length > 0 && length <= sample.Length - offset, "Invalid MP4 NAL size.");
            RememberParameter(track, sample.Slice(offset, (int)length), declared);
            offset += (int)length;
        }
    }

    private (byte[] Data, byte[] Configuration) NalPicture(Track track, byte[] data, bool key)
    {
        var units = H264Stream.Units(data);
        uint? pictureId = null;
        var picture = new List<ReadOnlyMemory<byte>>();
        foreach (var unit in units)
        {
            var type = NalType(unit.Span);
            if (IsParameter(type)) continue; // BSFs may inject stale moov parameter sets; only original samples update the cache.
            picture.Add(unit);
            var slice = request.VideoCodec == "h264" ? type is 1 or 5 : type <= 31;
            if (!slice) continue;
            var bits = new ParameterBits(unit.Span[(request.VideoCodec == "h264" ? 1 : 2)..]);
            if (request.VideoCodec == "h264") { bits.Unsigned(); bits.Unsigned(); }
            else
            {
                bits.Read(1);
                if (type is >= 16 and <= 23) bits.Skip(1);
            }
            var id = bits.Unsigned();
            JsonObject.Require(pictureId is null || pictureId == id, "An access unit cannot switch PPS IDs between slices.");
            pictureId = id;
        }
        JsonObject.Require(pictureId is not null, "MP4 access unit lacks its first coded picture.");
        var ppsType = request.VideoCodec == "h264" ? 8 : 34;
        JsonObject.Require(track.Parameters.TryGetValue((ppsType, pictureId!.Value), out var pps), "Picture references an unknown in-band PPS.");
        JsonObject.Require(track.Parameters.TryGetValue((ppsType - 1, pps!.Parent), out var sps), "PPS references an unknown in-band SPS.");
        var active = new List<ReadOnlyMemory<byte>>();
        if (request.VideoCodec == "h265")
        {
            JsonObject.Require(track.Parameters.TryGetValue((32, sps!.Parent), out var vps), "SPS references an unknown in-band VPS.");
            active.Add(vps!.Data);
        }
        active.Add(sps!.Data);
        active.Add(pps.Data);
        var known = H264Stream.Units(track.Configuration);
        var configuration = active.All(unit => known.Any(old => old.Span.SequenceEqual(unit.Span)))
            ? track.Configuration : AnnexB(active, MaximumExtra);
        return (AnnexB(key ? H264Stream.Units(configuration).Concat(picture) : picture, MaximumVideo), configuration);
    }

    private static byte[] AnnexB(IEnumerable<ReadOnlyMemory<byte>> units, int maximum)
    {
        var list = units.ToArray();
        var length = list.Sum(unit => (long)unit.Length + 4);
        JsonObject.Require(length is > 0 && length <= maximum, "Annex B data exceeds its allocation bound.");
        var result = new byte[(int)length];
        var offset = 0;
        foreach (var unit in list)
        {
            result[offset + 3] = 1;
            unit.Span.CopyTo(result.AsSpan(offset + 4));
            offset += unit.Length + 4;
        }
        return result;
    }

    private static byte[]? SequenceConfiguration(byte[] data)
    {
        byte[]? configuration = null;
        for (var offset = 0; offset < data.Length;)
        {
            var begin = offset;
            var header = data[offset++];
            if ((header & 4) != 0) offset++;
            ulong length = 0;
            for (var index = 0; index < 8; index++)
            {
                JsonObject.Require(offset < data.Length, "Truncated in-band AV1 OBU.");
                var value = data[offset++];
                length |= (ulong)(value & 127) << (7 * index);
                if ((value & 128) == 0) break;
            }
            JsonObject.Require(length <= (ulong)(data.Length - offset), "Invalid in-band AV1 OBU length.");
            offset += (int)length;
            if ((header >> 3 & 15) != 1) continue;
            JsonObject.Require(offset - begin <= MaximumExtra, "In-band AV1 sequence exceeds CSD bounds.");
            var candidate = Av1Stream.Configuration(data[begin..offset]);
            JsonObject.Require(configuration is null || configuration.AsSpan().SequenceEqual(candidate),
                "Conflicting AV1 sequences in one MP4 access unit.");
            configuration = candidate;
        }
        return configuration;
    }

    private void Emit(Packet packet)
    {
        token.ThrowIfCancellationRequested();
        emit(packet, token).AsTask().WaitAsync(token).GetAwaiter().GetResult();
    }

    private static byte[] SizedAv1(byte[] source, bool configuration)
    {
        var offset = configuration && source.Length >= 4 && source[0] == 0x81 ? 4 : 0;
        using var output = new MemoryStream();
        if (offset == 4) output.Write(source, 0, 4);
        var units = 0;
        while (offset < source.Length)
        {
            JsonObject.Require(++units <= 4096, "Too many AV1 OBUs.");
            var header = source[offset++];
            JsonObject.Require((header & 0x81) == 0, "Invalid AV1 OBU header.");
            output.WriteByte((byte)(header | 2));
            if ((header & 4) != 0)
            {
                JsonObject.Require(offset < source.Length, "Truncated AV1 extension.");
                output.WriteByte(source[offset++]);
            }
            ulong length = 0;
            if ((header & 2) != 0)
            {
                var complete = false;
                for (var index = 0; index < 8; index++)
                {
                    JsonObject.Require(offset < source.Length, "Truncated AV1 OBU size.");
                    var value = source[offset++];
                    length |= (ulong)(value & 127) << (7 * index);
                    if ((value & 128) == 0) { complete = true; break; }
                }
                JsonObject.Require(complete && length <= (ulong)(source.Length - offset), "Invalid AV1 OBU size.");
            }
            else length = (ulong)(source.Length - offset); // Only the final MP4 OBU may omit its size.
            var remaining = length;
            do
            {
                var value = (byte)(remaining & 127);
                remaining >>= 7;
                output.WriteByte((byte)(value | (remaining != 0 ? 128 : 0)));
            } while (remaining != 0);
            JsonObject.Require(output.Length + (long)length <= (configuration ? MaximumExtra : MaximumVideo),
                "Sized AV1 data exceeds wire bounds.");
            output.Write(source, offset, (int)length);
            offset += (int)length;
        }
        return output.ToArray();
    }

    private static void FreeConverters(Track track)
    {
        var owned = track.Filter;
        if (owned != null) ffmpeg.av_bsf_free(&owned);
        track.Filter = null;
        if (track.Parser != null) ffmpeg.av_parser_close(track.Parser);
        track.Parser = null;
        var codec = track.ParserCodec;
        if (codec != null) ffmpeg.avcodec_free_context(&codec);
        track.ParserCodec = null;
    }

    public void Dispose()
    {
        foreach (var track in tracks) FreeConverters(track);
        var owned = format;
        if (owned != null) ffmpeg.avformat_close_input(&owned);
        format = null;
        var custom = io;
        if (custom != null)
        {
            ffmpeg.av_free(custom->buffer);
            custom->buffer = null;
            ffmpeg.avio_context_free(&custom);
        }
        io = null;
        GC.KeepAlive(readCallback); GC.KeepAlive(interruptCallback); GC.KeepAlive(rejectOpen);
    }
}
