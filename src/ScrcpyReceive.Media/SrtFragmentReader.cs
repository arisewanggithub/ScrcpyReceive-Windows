using System.Buffers.Binary;
using System.Text;
using ScrcpyReceive.Core;

namespace ScrcpyReceive.Media;

// Validate allocation-bearing MP4 metadata before passing it to the native demuxer.
// mdat bodies stream directly; only bounded init/fragment metadata is buffered.
internal sealed class SrtFragmentReader(SrtTransport transport, CancellationToken token)
{
    private const int MaximumMetadata = 65536;
    private readonly Dictionary<uint, uint> defaults = [];
    private readonly Dictionary<uint, int> trackLimits = [];
    private byte[] pending = [];
    private int offset, bodyRemaining, initializationBytes;
    private bool initialized, fragment;

    internal int Read(Span<byte> destination)
    {
        token.ThrowIfCancellationRequested();
        if (offset < pending.Length)
        {
            var count = Math.Min(destination.Length, pending.Length - offset);
            pending.AsSpan(offset, count).CopyTo(destination);
            offset += count;
            return count;
        }
        if (bodyRemaining > 0)
        {
            var count = transport.Read(destination[..Math.Min(destination.Length, bodyRemaining)], token);
            bodyRemaining -= count;
            return count;
        }
        Span<byte> header = stackalloc byte[16];
        ReadExactly(header[..8]);
        var size = (ulong)U32(header);
        var type = Type(header);
        var headerSize = 8;
        if (size == 1) { ReadExactly(header[8..]); size = BinaryPrimitives.ReadUInt64BigEndian(header[8..]); headerSize = 16; }
        JsonObject.Require(size >= (ulong)headerSize && size <= SrtDemuxer.MaximumVideo + 16,
            "Unbounded or oversized live MP4 box.");
        if (type == "mdat")
        {
            JsonObject.Require(initialized && fragment, "fMP4 mdat requires a preceding initialization and fragment.");
            fragment = false;
            pending = header[..headerSize].ToArray();
            offset = 0;
            bodyRemaining = checked((int)size - headerSize);
        }
        else
        {
            JsonObject.Require(size <= MaximumMetadata && type is "ftyp" or "moov" or "moof" or "free" or "skip" or "styp" or "sidx",
                "Unsupported or oversized fMP4 metadata.");
            if (!initialized)
            {
                initializationBytes = checked(initializationBytes + (int)size);
                JsonObject.Require(initializationBytes <= MaximumMetadata, "fMP4 initialization exceeds its bound.");
            }
            var metadata = new byte[(int)size];
            header[..headerSize].CopyTo(metadata);
            ReadExactly(metadata.AsSpan(headerSize));
            if (type == "moov")
            {
                JsonObject.Require(!initialized, "Unexpected replacement MP4 initialization.");
                var children = Children(metadata.AsMemory(headerSize)).ToArray();
                JsonObject.Require(children.Any(box => box.Type == "mvex"), "Fragmented MP4 initialization requires mvex.");
                RegisterTracks(children);
                ValidateMetadata(metadata.AsMemory(headerSize), 0);
                initialized = true;
            }
            else if (type == "moof")
            {
                JsonObject.Require(initialized && !fragment, "Invalid fMP4 fragment ordering.");
                ValidateMetadata(metadata.AsMemory(headerSize), 0);
                ValidateFragment(metadata.AsMemory(headerSize));
                fragment = true;
            }
            pending = metadata;
            offset = 0;
        }
        return Read(destination);
    }

    private void RegisterTracks(Box[] children)
    {
        foreach (var track in children.Where(box => box.Type == "trak"))
        {
            JsonObject.Require(trackLimits.Count < 2, "Too many MP4 tracks.");
            var boxes = Children(track.Body).ToArray();
            var headers = boxes.Where(box => box.Type == "tkhd").ToArray();
            var media = boxes.Where(box => box.Type == "mdia").ToArray();
            JsonObject.Require(headers.Length == 1 && headers[0].Body.Length >= 4 && media.Length == 1,
                "Invalid MP4 track declaration.");
            var version = headers[0].Body.Span[0];
            JsonObject.Require(version <= 1, "Unsupported MP4 track header version.");
            var id = U32(headers[0].Body.Span[(version == 1 ? 20 : 12)..]);
            var handlers = Children(media[0].Body).Where(box => box.Type == "hdlr").ToArray();
            JsonObject.Require(handlers.Length == 1 && handlers[0].Body.Length >= 12, "Invalid MP4 track handler.");
            var handler = Encoding.ASCII.GetString(handlers[0].Body.Span.Slice(8, 4));
            JsonObject.Require(handler is "vide" or "soun" && id != 0 &&
                trackLimits.TryAdd(id, handler == "vide" ? SrtDemuxer.MaximumVideo : SrtDemuxer.MaximumAudio),
                "Unsupported or duplicate MP4 track.");
        }
    }

    private void ReadExactly(Span<byte> bytes)
    {
        while (!bytes.IsEmpty) bytes = bytes[transport.Read(bytes, token)..];
    }

    private static uint U32(ReadOnlySpan<byte> bytes)
    {
        JsonObject.Require(bytes.Length >= 4, "Truncated MP4 field.");
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static string Type(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes.Slice(4, 4));
    private sealed record Box(string Type, ReadOnlyMemory<byte> Body);

    private static IEnumerable<Box> Children(ReadOnlyMemory<byte> bytes)
    {
        var offset = 0;
        var count = 0;
        while (offset < bytes.Length)
        {
            JsonObject.Require(bytes.Length - offset >= 8 && ++count <= 4096, "Invalid MP4 child box count/size.");
            var size = U32(bytes.Span[offset..]);
            JsonObject.Require(size >= 8 && size <= bytes.Length - offset, "Invalid MP4 child length.");
            yield return new(Type(bytes.Span[offset..]), bytes.Slice(offset + 8, (int)size - 8));
            offset += (int)size;
        }
    }

    internal void ValidateMetadata(ReadOnlyMemory<byte> body, int depth)
    {
        JsonObject.Require(depth <= 8, "MP4 metadata nesting exceeds its bound.");
        foreach (var box in Children(body))
        {
            JsonObject.Require(box.Type != "cmov", "Compressed MP4 movie metadata is unsupported.");
            JsonObject.Require(box.Type is not ("iprp" or "iinf" or "iref"),
                "Image-container metadata is unsupported in live fMP4.");
            if (box.Type is "moov" or "moof" or "trak" or "mdia" or "minf" or "stbl" or "mvex" or
                "traf" or "dinf" or "edts" or "tref" or "udta" or "ilst" or "wave" or "sinf" or "schi")
                ValidateMetadata(box.Body, depth + 1);
            else if (box.Type == "meta")
            {
                JsonObject.Require(box.Body.Length >= 4 && U32(box.Body.Span) == 0,
                    "Only version-zero full-box MP4 metadata is supported.");
                ValidateMetadata(box.Body[4..], depth + 1);
            }
            else if (box.Type == "trex")
            {
                JsonObject.Require(box.Body.Length == 24 && defaults.Count < 2, "Invalid fMP4 track defaults.");
                var size = U32(box.Body.Span[16..]);
                var id = U32(box.Body.Span[4..]);
                JsonObject.Require(trackLimits.TryGetValue(id, out var maximum) && size <= maximum &&
                    defaults.TryAdd(id, size), "Invalid/duplicate fMP4 track defaults.");
            }
            else if (box.Type == "stsd")
            {
                JsonObject.Require(box.Body.Length >= 8 && U32(box.Body.Span[4..]) is > 0 and <= 8,
                    "Invalid MP4 sample-description count.");
                var entries = Children(box.Body[8..]).ToArray();
                JsonObject.Require(entries.Length == U32(box.Body.Span[4..]),
                    "MP4 sample-description count differs from its entries.");
                foreach (var entry in entries)
                {
                    JsonObject.Require(entry.Type is "avc3" or "hev1" or "av01" or "mp4a" or "Opus",
                        "Unsupported live fMP4 sample entry.");
                    var video = entry.Type is "avc3" or "hev1" or "av01";
                    var header = video ? 78 : 28;
                    JsonObject.Require(entry.Body.Length >= header, "Truncated MP4 sample-entry header.");
                    if (!video)
                        JsonObject.Require(BinaryPrimitives.ReadUInt16BigEndian(entry.Body.Span[8..]) == 0,
                            "Only version-zero MP4 audio sample entries are supported.");
                    ValidateMetadata(entry.Body[header..], depth + 1);
                }
            }
            else if (box.Type is "stsz" or "stz2")
            {
                JsonObject.Require(box.Body.Length >= 12 && U32(box.Body.Span[8..]) == 0,
                    "Live fragmented MP4 must not declare nonfragmented samples.");
            }
            else if (box.Type is "stco" or "co64" or "stsc" or "stts" or "ctts" or "stss")
                JsonObject.Require(box.Body.Length >= 8 && U32(box.Body.Span[4..]) == 0,
                    "Live fragmented MP4 requires empty initial sample tables.");
        }
    }

    private void ValidateFragment(ReadOnlyMemory<byte> body)
    {
        long bytes = 0;
        var samples = 0;
        foreach (var traf in Children(body).Where(box => box.Type == "traf"))
        {
            var children = Children(traf.Body).ToArray();
            var headers = children.Where(box => box.Type == "tfhd").ToArray();
            JsonObject.Require(headers.Length == 1 && headers[0].Body.Length >= 8, "fMP4 traf requires one track header.");
            var header = headers[0].Body.Span;
            var flags = U32(header) & 0x00ffffff;
            var track = U32(header[4..]);
            JsonObject.Require(defaults.TryGetValue(track, out var defaultSize) && trackLimits.ContainsKey(track),
                "Unknown fragment track ID.");
            var maximum = trackLimits[track];
            var cursor = 8 + ((flags & 1) != 0 ? 8 : 0) + ((flags & 2) != 0 ? 4 : 0) + ((flags & 8) != 0 ? 4 : 0);
            if ((flags & 16) != 0) defaultSize = U32(header[cursor..]);
            JsonObject.Require(defaultSize <= maximum, "Oversized fragment default sample.");
            foreach (var run in children.Where(box => box.Type == "trun"))
            {
                var data = run.Body.Span;
                JsonObject.Require(data.Length >= 8, "Truncated fMP4 sample run.");
                var runFlags = U32(data) & 0x00ffffff;
                var count = U32(data[4..]);
                JsonObject.Require(count <= 4096 && (samples += (int)count) <= 4096, "Too many fragment samples.");
                var position = 8 + ((runFlags & 1) != 0 ? 4 : 0) + ((runFlags & 4) != 0 ? 4 : 0);
                for (var index = 0; index < count; index++)
                {
                    if ((runFlags & 0x100) != 0) position += 4;
                    var size = defaultSize;
                    if ((runFlags & 0x200) != 0) { size = U32(data[position..]); position += 4; }
                    JsonObject.Require(size > 0 && size <= maximum &&
                        (bytes += size) <= SrtDemuxer.MaximumVideo, "fMP4 sample/fragment exceeds bounded admission.");
                    if ((runFlags & 0x400) != 0) position += 4;
                    if ((runFlags & 0x800) != 0) position += 4;
                }
                JsonObject.Require(position == data.Length, "Invalid fMP4 sample run fields.");
            }
        }
        JsonObject.Require(samples > 0, "Empty fMP4 media fragment.");
    }
}
