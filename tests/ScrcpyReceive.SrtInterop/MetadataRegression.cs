using System.Buffers.Binary;
using System.Text;
using ScrcpyReceive.Core;
using ScrcpyReceive.Media;

namespace ScrcpyReceive.SrtInterop;

internal static class MetadataRegression
{
    internal static int Run()
    {
        // Header only: no compressed data is inflated, no native parser or transport is started.
        var compressed = Box("cmov", Box("dcom", "zlib"u8.ToArray()),
            Box("cmvd", [0x7f, 0xff, 0xff, 0xff]));
        var cases = new List<byte[]>
        {
            Box("mvex").Concat(compressed).ToArray(),
            Box("udta", compressed),
            Box("meta", new byte[4], compressed),
            Box("moof", Box("traf", compressed))
        };
        foreach (var container in new[] { "trak", "mdia", "minf", "stbl", "mvex", "dinf", "edts", "tref", "ilst", "wave", "sinf", "schi" })
            cases.Add(Box(container, compressed));
        foreach (var entry in new[] { "avc3", "hev1", "av01", "mp4a", "Opus" })
            cases.Add(SampleDescription(entry, compressed));
        cases.Add(Box("iprp", compressed));
        foreach (var bytes in cases)
        {
            using var transport = Transport();
            var reader = new SrtFragmentReader(transport, CancellationToken.None);
            try
            {
                reader.ValidateMetadata(bytes, 0);
                throw new InvalidOperationException("Compressed MP4 metadata passed the managed admission boundary.");
            }
            catch (ProtocolException error) when (error.Code == "PROTOCOL") { }
        }

        using (var transport = Transport())
        {
            var reader = new SrtFragmentReader(transport, CancellationToken.None);
            reader.ValidateMetadata(Box("mvex"), 0);
            reader.ValidateMetadata(Box("udta", Box("meta", new byte[4], Box("ilst"))), 0);
            foreach (var entry in new[] { "avc3", "hev1", "av01", "mp4a", "Opus" })
            {
                // Codec configuration is opaque to this box walker, not a nested movie.
                reader.ValidateMetadata(SampleDescription(entry, Box("free", compressed)), 0);
            }
        }
        Console.WriteLine($"METADATA PASS: rejected {cases.Count} compressed/nested metadata cases before native parsing; supported sample entries and opaque bytes preserved.");
        return 0;
    }

    internal static async Task<int> RunAvioAsync()
    {
        // Keep the advertised expansion tiny even if the guard regresses.
        var compressed = Box("cmov", Box("dcom", "zlib"u8.ToArray()), Box("cmvd", [0, 0, 0, 64]));
        foreach (var nested in new[] { false, true })
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var sender = new SyntheticOpusSender.LoopbackSrtListener();
            var descriptor = sender.Prepare(1);
            await using var input = new SrtInput(new("h264", null, 30, "auto",
                new(descriptor.Url, descriptor.LatencyMs, descriptor.StreamId)));
            var metadata = Box("ftyp", "isom"u8.ToArray(), new byte[4], "isom"u8.ToArray())
                .Concat(Box("moov", Box("mvex"), nested ? Box("udta", Box("meta", new byte[4], compressed)) : compressed)).ToArray();
            var send = Task.Run(async () =>
            {
                await sender.AcceptAsync(lifetime.Token);
                sender.Write(metadata, lifetime.Token);
                await Task.Delay(Timeout.Infinite, lifetime.Token);
            });
            try
            {
                try
                {
                    await input.RunAsync((_, _) => throw new InvalidOperationException("Rejected metadata emitted a track configuration."),
                        lifetime.Token);
                    throw new InvalidOperationException("Compressed metadata was admitted through custom AVIO.");
                }
                catch (ProtocolException error) when (error.Message == "Compressed MP4 movie metadata is unsupported.") { }
            }
            finally
            {
                lifetime.Cancel();
                try { await send; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
        }
        Console.WriteLine("METADATA AVIO PASS: direct/nested compressed metadata rejected with explicit protocol error before configuration.");
        return 0;
    }

    private static SrtTransport Transport() => new(new("srt://127.0.0.1:1234", 100, "sc-0000000000000001"));

    private static byte[] SampleDescription(string type, byte[] child)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        return Box("stsd", header, Box(type, new byte[type is "avc3" or "hev1" or "av01" ? 78 : 28], child));
    }

    private static byte[] Box(string type, params byte[][] bodies)
    {
        var bytes = new byte[8 + bodies.Sum(body => body.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        Encoding.ASCII.GetBytes(type, bytes.AsSpan(4, 4));
        var offset = 8;
        foreach (var body in bodies) { body.CopyTo(bytes, offset); offset += body.Length; }
        return bytes;
    }
}
