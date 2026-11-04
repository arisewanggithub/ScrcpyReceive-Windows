using System.Net;
using System.Text;
using System.Text.Json;
using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class RequestTests
{
    internal static object Settings(bool audio = true, string audioCodec = "raw", object? transport = null) =>
        transport is null ? new
        {
            video = Video(), receiverOrientation = "auto",
            audio = new { enabled = audio, codec = audioCodec, encoder = "", bitrate = 192000, source = "output", codecOptions = "" }
        } : (object)new
        {
            video = Video(), receiverOrientation = "auto",
            audio = new { enabled = audio, codec = audioCodec, encoder = "", bitrate = 192000, source = "output", codecOptions = "" },
            mediaTransport = transport
        };

    private static object Video() => new
    {
        codec = "h264", encoder = "", bitrate = 8000000, maxSize = 1920, maxFps = 60,
        displayId = 0, orientation = "0", crop = (object?)null, codecOptions = ""
    };

    internal static CastRequest Request(bool audio = true, string codec = "raw") =>
        CastRequest.Parse(JsonObject.Parse(JsonSerializer.SerializeToUtf8Bytes(Settings(audio, codec))), 1, IPAddress.Loopback);

    [Theory]
    [InlineData("h264")]
    [InlineData("h265")]
    [InlineData("av1")]
    public void RecognizesSenderVideoCodecs(string codec)
    {
        var bytes = JsonSerializer.Serialize(Settings()).Replace("\"h264\"", $"\"{codec}\"", StringComparison.Ordinal);
        var request = CastRequest.Parse(JsonObject.Parse(Encoding.UTF8.GetBytes(bytes)), 1, IPAddress.Loopback);
        Assert.Equal(codec, request.VideoCodec);
        Assert.Equal(new[] { 1u, 2u }, request.Tracks);
    }

    [Theory]
    [InlineData("\"maxFps\":60", "\"maxFps\":0")]
    [InlineData("\"bitrate\":8000000", "\"bitrate\":0")]
    [InlineData("\"maxSize\":1920", "\"maxSize\":16385")]
    [InlineData("\"displayId\":0", "\"displayId\":-1")]
    [InlineData("\"orientation\":\"0\"", "\"orientation\":\"auto\"")]
    [InlineData("\"receiverOrientation\":\"auto\"", "\"receiverOrientation\":null")]
    [InlineData("\"receiverOrientation\":\"auto\"", "\"receiverOrientation\":\"Auto\"")]
    [InlineData("\"crop\":null", "\"crop\":{}")]
    [InlineData("\"enabled\":true", "\"enabled\":\"true\"")]
    [InlineData("\"codec\":\"raw\"", "\"codec\":\"pcm\"")]
    public void InvalidSettingsAreRejected(string oldText, string newText)
    {
        var text = JsonSerializer.Serialize(Settings()).Replace(oldText, newText, StringComparison.Ordinal);
        Assert.Throws<ProtocolException>(() =>
            CastRequest.Parse(JsonObject.Parse(Encoding.UTF8.GetBytes(text)), 1, IPAddress.Loopback));
    }

    [Fact]
    public void OptionalOrientationAndUnknownIdentityFieldsRemainCompatible()
    {
        var text = JsonSerializer.Serialize(Settings(false)).Replace("\"receiverOrientation\":\"auto\",", "", StringComparison.Ordinal);
        var request = CastRequest.Parse(JsonObject.Parse(Encoding.UTF8.GetBytes(text)), 1, IPAddress.Loopback);
        Assert.Equal("auto", request.ReceiverOrientation);
        Assert.Null(request.AudioCodec);
        Assert.Single(request.Tracks);
    }

    [Theory]
    [InlineData("srt://127.0.0.1:1234", true)]
    [InlineData("srt://[::1]:1234", false)]
    [InlineData("srt://127.0.0.2:1234", false)]
    [InlineData("srt://localhost:1234", false)]
    [InlineData("srt://127.0.0.1:1234/", false)]
    [InlineData("srt://127.0.0.1:1234?x=1", false)]
    [InlineData("srt://user@127.0.0.1:1234", false)]
    [InlineData("srt://127.0.0.1:1234#fragment", false)]
    [InlineData("srt://127.0.0.1:0", false)]
    [InlineData("srt://127.0.0.1:65536", false)]
    [InlineData("srt://127.0.0.1", false)]
    [InlineData("srt://127.1:1234", false)]
    [InlineData("SRT://127.0.0.1:1234", false)]
    public void SrtPinsLiteralAuthorityToControlPeer(string url, bool valid)
    {
        var json = JsonObject.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "srt", container = "fmp4", url, latencyMs = 100, streamId = "sc-0000000000000001"
        }));
        if (valid) Assert.Equal(url, SrtDescriptor.Parse(json, 1, IPAddress.Loopback).Url);
        else Assert.Throws<ProtocolException>(() => SrtDescriptor.Parse(json, 1, IPAddress.Loopback));
    }

    [Theory]
    [InlineData("sc-0000000000000002", 100, "fmp4")]
    [InlineData("sc-0000000000000001", 19, "fmp4")]
    [InlineData("sc-0000000000000001", 2001, "fmp4")]
    [InlineData("sc-0000000000000001", 100, "mpegts")]
    public void SrtRequiresExactSessionAndLimits(string streamId, int latencyMs, string container)
    {
        var json = JsonObject.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "srt", container, url = "srt://127.0.0.1:1234", latencyMs, streamId
        }));
        Assert.Throws<ProtocolException>(() => SrtDescriptor.Parse(json, 1, IPAddress.Loopback));
    }

    [Fact]
    public void SrtSupportsBracketedPinnedIpv6()
    {
        var json = JsonObject.Parse("""{"kind":"srt","container":"fmp4","url":"srt://[::1]:1234","latencyMs":100,"streamId":"sc-0000000000000001"}"""u8);
        Assert.Equal("srt://[::1]:1234", SrtDescriptor.Parse(json, 1, IPAddress.IPv6Loopback).Url);
    }

    [Fact]
    public void SrtRejectsExtraFields()
    {
        var json = JsonObject.Parse("""{"kind":"srt","container":"fmp4","url":"srt://127.0.0.1:1234","latencyMs":100,"streamId":"sc-0000000000000001","extra":0}"""u8);
        Assert.Throws<ProtocolException>(() => SrtDescriptor.Parse(json, 1, IPAddress.Loopback));
    }
}
