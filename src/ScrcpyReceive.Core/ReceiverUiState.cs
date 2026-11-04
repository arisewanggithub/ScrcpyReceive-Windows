namespace ScrcpyReceive.Core;

public enum ReceiverUiPhase { Starting, Ready, Connecting, NoNetwork, DiscoveryUnavailable, Error, Unavailable, Stopped }

public sealed record ReceiverUiState
{
    public bool ListenerOpen { get; init; }
    public bool AddressesKnown { get; init; }
    public bool DiscoveryStopped { get; init; }
    public IReadOnlyList<string> Addresses { get; init; } = [];
    public IReadOnlyDictionary<string, string> AdapterLabels { get; init; } = new Dictionary<string, string>();
    public bool Connecting { get; init; }
    public bool Stopped { get; init; }
    public string? ProblemCode { get; init; }
    public bool IsWarning => ProblemCode is "Discovery warning" or "Discovery failed" or "Window layout failed";
    public ReceiverUiPhase Phase => !ListenerOpen
        ? Stopped ? ReceiverUiPhase.Stopped : ProblemCode is null ? ReceiverUiPhase.Starting : ReceiverUiPhase.Unavailable
        : Connecting ? ReceiverUiPhase.Connecting
        : DiscoveryStopped && Addresses.Count == 0 ? ReceiverUiPhase.DiscoveryUnavailable
        : !AddressesKnown ? ReceiverUiPhase.Starting
        : Addresses.Count == 0 ? ReceiverUiPhase.NoNetwork
        : ProblemCode is not null && !IsWarning ? ReceiverUiPhase.Error : ReceiverUiPhase.Ready;

    public string Status => Phase switch
    {
        ReceiverUiPhase.Ready => "Ready to receive",
        ReceiverUiPhase.Connecting => "Connecting",
        ReceiverUiPhase.NoNetwork => "No network connection",
        ReceiverUiPhase.DiscoveryUnavailable => "Network discovery unavailable",
        ReceiverUiPhase.Error => "Playback stopped",
        ReceiverUiPhase.Unavailable => "Receiver unavailable",
        ReceiverUiPhase.Stopped => "Receiver closed",
        _ => "Starting receiver"
    };

    public string Hint => Phase switch
    {
        ReceiverUiPhase.Ready => DiscoveryStopped
            ? "Enter this PC's address manually in ScrcpySend.\nReopen the receiver to restart automatic discovery."
            : "Choose this screen in ScrcpySend.\nBoth devices need the same local network.",
        ReceiverUiPhase.Connecting => "Preparing your remote screen...",
        ReceiverUiPhase.NoNetwork => "Connect this PC to Wi-Fi or Ethernet.\nIts address will update automatically.",
        ReceiverUiPhase.DiscoveryUnavailable => "Check this PC's Wi-Fi or Ethernet connection,\nthen close and reopen the receiver.",
        ReceiverUiPhase.Error => "Start casting again from the sender.",
        ReceiverUiPhase.Unavailable => "Close and reopen the receiver.\nCheck this PC's network connection if the problem continues.",
        ReceiverUiPhase.Stopped => "Open the app again to receive a cast.",
        _ => "Opening this PC for your sender..."
    };

    public string ProblemTitle => IsWarning ? "Connection notice" : Phase == ReceiverUiPhase.Unavailable ? "Receiver unavailable" : "Playback stopped";

    public string ProblemMessage => ProblemCode switch
    {
        "Start failed" => "Could not open the receiver. Close any other receiver using this port, then reopen this app.",
        "Listener failed" or "Cleanup failed" => "The receiver could not continue safely. Close and reopen this app.",
        "Discovery warning" => "Automatic discovery may be unavailable. Enter this PC's address manually on the sender.",
        "Discovery failed" => Addresses.Count == 0
            ? "Network address lookup stopped. Check this PC's network connection, then close and reopen the receiver to try again."
            : "Automatic discovery and address updates have stopped. You can try the displayed address manually, or reopen the receiver to restart discovery.",
        "Window layout failed" => "The window could not adjust to this display. Try moving or reopening it.",
        "UNSUPPORTED_CONFIG" => "Those settings are not supported. Choose a supported codec, resolution and frame rate on the sender, then try again.",
        "DECODER_FAILED" => "Video playback stopped. Restart the cast; if it repeats, try another codec or a lower resolution.",
        "AUDIO_FAILED" => "Audio playback stopped. Check the Windows output device, then restart the cast.",
        "CONGESTION" => "Playback could not keep up. Try a lower resolution, frame rate or bitrate on the sender.",
        "TIMEOUT" => "The sender stopped responding or playback could not start. Start casting again from the sender.",
        "VERSION" => "Update both apps, then try connecting again.",
        "Sender error" => "The sender reported an error. Check its screen, then start casting again.",
        _ => "The cast stopped unexpectedly. Restart from the sender; if it repeats, close and reopen the receiver."
    };

    public ReceiverUiState Apply(ReceiverNotice notice)
    {
        var state = notice.ListeningEndpoints is null ? this : this with
        {
            Addresses = notice.ListeningEndpoints.Distinct(StringComparer.Ordinal).ToArray(),
            AdapterLabels = notice.AdapterLabels is null ? new Dictionary<string, string>() :
                notice.AdapterLabels.Where(item => notice.ListeningEndpoints.Contains(item.Key))
                    .ToDictionary(item => item.Key, item => item.Value),
            AddressesKnown = true
        };
        return notice.State switch
        {
            "Listening" when state.Stopped || state.ProblemCode is "Start failed" or "Listener failed" or "Cleanup failed" => state,
            "Listening" => state with { ListenerOpen = true, Stopped = false },
            "Initializing" => state with { Connecting = true, ProblemCode = null },
            "Disconnected" => state with { Connecting = false },
            "Network changed" => state with { Addresses = [], AdapterLabels = new Dictionary<string, string>(),
                AddressesKnown = false, Connecting = false },
            "Stopped" => state with { ListenerOpen = false, Stopped = true, Connecting = false },
            "Start failed" or "Listener failed" or "Cleanup failed" =>
                state with { ListenerOpen = false, Stopped = false, Connecting = false, ProblemCode = notice.State },
            "Discovery failed" => state with
            {
                DiscoveryStopped = true,
                ProblemCode = state.ProblemCode is not null && !state.IsWarning ? state.ProblemCode : notice.State
            },
            "Discovery warning" or "Window layout failed" =>
                state.ProblemCode is not null && !state.IsWarning ? state : state with { ProblemCode = notice.State },
            "UNSUPPORTED_CONFIG" or "DECODER_FAILED" or "AUDIO_FAILED" or "CONGESTION" or
                "TIMEOUT" or "VERSION" or "PROTOCOL" or "INTERNAL" or "Sender error" =>
                state with { Connecting = false, ProblemCode = notice.State },
            _ => state
        };
    }
}
