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
        CastRequest.Parse(JsonObject.Parse(JsonSerializer.SerializeToUtf8Bytes(Settings(audio, codec))));

    [Theory]
    [InlineData("h264")]
    [InlineData("h265")]
    [InlineData("av1")]
    public void RecognizesSenderVideoCodecs(string codec)
    {
        var bytes = JsonSerializer.Serialize(Settings()).Replace("\"h264\"", $"\"{codec}\"", StringComparison.Ordinal);
        var request = CastRequest.Parse(JsonObject.Parse(Encoding.UTF8.GetBytes(bytes)));
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
            CastRequest.Parse(JsonObject.Parse(Encoding.UTF8.GetBytes(text))));
    }

    [Fact]
    public void OptionalOrientationAndUnknownIdentityFieldsRemainCompatible()
    {
        var text = JsonSerializer.Serialize(Settings(false)).Replace("\"receiverOrientation\":\"auto\",", "", StringComparison.Ordinal);
        var request = CastRequest.Parse(JsonObject.Parse(Encoding.UTF8.GetBytes(text)));
        Assert.Equal("auto", request.ReceiverOrientation);
        Assert.Null(request.AudioCodec);
        Assert.Single(request.Tracks);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"kind\":\"external\",\"url\":\"udp://127.0.0.1:1234\"}")]
    [InlineData("{\"kind\":\"tcp\"}")]
    [InlineData("\"tcp\"")]
    [InlineData("[]")]
    [InlineData("false")]
    [InlineData("1")]
    public void TransportOverridesAreRejectedWithoutFallback(string value)
    {
        var root = JsonSerializer.SerializeToElement(Settings()).EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value);
        using var transport = JsonDocument.Parse(value);
        root["mediaTransport"] = transport.RootElement;
        var error = Assert.Throws<ProtocolException>(() =>
            CastRequest.Parse(JsonObject.Parse(JsonSerializer.SerializeToUtf8Bytes(root))));
        Assert.Equal("UNSUPPORTED_CONFIG", error.Code);
    }
}
