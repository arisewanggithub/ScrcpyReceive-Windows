using System.Net;
using ScrcpyReceive.Core;
using ScrcpySend.Core;
using Xunit;

namespace ScrcpyReceive.SenderInterop;

public sealed class RejectionInteropTests
{
    [Theory]
    [InlineData("h264", false)]
    [InlineData("h264", true)]
    [InlineData("h265", false)]
    [InlineData("h265", true)]
    [InlineData("av1", false)]
    [InlineData("av1", true)]
    public async Task ActualSenderSessionRejectsWithoutStartingCaptureOrAudioSuppression(string codec, bool audioEnabled)
    {
        await using var receiver = new ReceiverServer("Interop test", new UnavailablePlaybackFactory(), _ => { },
            0, IPAddress.Loopback);
        receiver.Start(false);
        var video = new NeverCaptureVideo();
        var audio = audioEnabled ? new NeverTransmitAudio() : null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var states = new List<string>();
        var error = await Assert.ThrowsAsync<IOException>(() => new SenderSession().RunAsync(
            new ReceiverEndpoint(IPAddress.Loopback, receiver.Port, "Interop test", false),
            new CaptureSettings(EnableAudio: audioEnabled, VideoCodec: codec),
            video, states.Add, timeout.Token, audio));
        Assert.StartsWith("UNSUPPORTED_CONFIG:", error.Message, StringComparison.Ordinal);
        Assert.False(video.Started);
        Assert.True(video.Disposed);
        Assert.DoesNotContain("Initializing capture", states);
        if (audio is not null)
        {
            Assert.False(audio.Transmitting);
            Assert.False(audio.Started);
            Assert.True(audio.Disposed);
        }
    }

    private sealed class NeverCaptureVideo : IVideoSource
    {
        public string EncoderName => "TEST-ONLY-NO-CAPTURE";
        public int Width => 1280;
        public int Height => 720;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public IAsyncEnumerable<EncodedVideo> ReadFramesAsync(CancellationToken cancellationToken)
        {
            Started = true;
            throw new InvalidOperationException("Rejection must precede capture.");
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NeverTransmitAudio : IAudioSource
    {
        public bool Transmitting { get; private set; }
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public object Configuration => new
        {
            codec = "raw", sampleRate = 48000, channels = 2, sampleFormat = "s16le", csd = Array.Empty<string>()
        };
        public void BeginTransmission() => Transmitting = true;
        public IAsyncEnumerable<ScrcpySend.Core.AudioPacket> ReadPacketsAsync(CancellationToken cancellationToken)
        {
            Started = true;
            throw new InvalidOperationException("Rejection must precede audio transmission.");
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
