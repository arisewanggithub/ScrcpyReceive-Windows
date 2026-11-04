using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class ReceiverUiStateTests
{
    private static ReceiverUiState Ready() =>
        new ReceiverUiState().Apply(new("Listening", "ready", ["127.0.0.1 : 27184"]));

    [Fact]
    public void ReadyRequiresBothAnOpenListenerAndKnownAddresses()
    {
        var state = new ReceiverUiState();
        Assert.Equal(ReceiverUiPhase.Starting, state.Phase);
        state = state.Apply(new("Listening", "socket open"));
        Assert.Equal(ReceiverUiPhase.Starting, state.Phase);
        state = state.Apply(new("Listening", "network snapshot", []));
        Assert.Equal(ReceiverUiPhase.NoNetwork, state.Phase);
        state = state.Apply(new("Listening", "network snapshot", ["127.0.0.1 : 27184"]));
        Assert.Equal(ReceiverUiPhase.Ready, state.Phase);
    }

    [Fact]
    public void DuplicateAddressesAreRemovedAndInputCollectionsAreNotRetained()
    {
        var addresses = new[] { "127.0.0.1 : 27184", "127.0.0.1 : 27184" };
        var state = new ReceiverUiState().Apply(new("Listening", "snapshot", addresses));
        addresses[0] = "changed";
        Assert.Equal("127.0.0.1 : 27184", Assert.Single(state.Addresses));
    }

    [Fact]
    public void RoutineNoticesDoNotReplaceConnectingOrClearFailure()
    {
        var state = Ready().Apply(new("Initializing", "codec details"));
        state = state.Apply(new("Listening", "network refresh", ["127.0.0.1 : 27184"]));
        Assert.Equal(ReceiverUiPhase.Connecting, state.Phase);
        state = state.Apply(new("DECODER_FAILED", "raw diagnostic"));
        state = state.Apply(new("Listening", "resources released"));
        Assert.Equal(ReceiverUiPhase.Error, state.Phase);
        Assert.Equal("DECODER_FAILED", state.ProblemCode);
        Assert.DoesNotContain("raw diagnostic", state.ProblemMessage);
        Assert.Equal(ReceiverUiPhase.Connecting, state.Apply(new("Initializing", "new cast")).Phase);
        Assert.Null(state.Apply(new("Initializing", "new cast")).ProblemCode);
    }

    [Fact]
    public void DiscoveryWarningsDoNotHideAPlaybackFailure()
    {
        var warning = Ready().Apply(new("Discovery warning", "raw network details"));
        Assert.True(warning.IsWarning);
        Assert.Equal(ReceiverUiPhase.Ready, warning.Phase);
        var failed = warning.Apply(new("AUDIO_FAILED", "device details"));
        Assert.Equal("AUDIO_FAILED", failed.Apply(new("Discovery warning", "later warning")).ProblemCode);
    }

    [Fact]
    public void InitialDiscoveryFailureEndsStartupWithoutClosingTheTcpListener()
    {
        var state = new ReceiverUiState().Apply(new("Listening", "socket open"))
            .Apply(new("Discovery failed", "enumeration failed"));
        Assert.True(state.ListenerOpen);
        Assert.True(state.DiscoveryStopped);
        Assert.False(state.AddressesKnown);
        Assert.Equal(ReceiverUiPhase.DiscoveryUnavailable, state.Phase);
        Assert.Contains("reopen", state.Hint);
        Assert.Contains("reopen", state.ProblemMessage);
        Assert.DoesNotContain("manually", state.ProblemMessage);
        Assert.Equal(ReceiverUiPhase.DiscoveryUnavailable, state.Apply(new("Listening", "resources released")).Phase);
    }

    [Fact]
    public void TerminalDiscoveryFailureAfterAnEmptySnapshotDoesNotPromiseAutomaticRecovery()
    {
        var state = new ReceiverUiState().Apply(new("Listening", "no network", []))
            .Apply(new("Discovery failed", "enumeration failed"));
        Assert.Equal(ReceiverUiPhase.DiscoveryUnavailable, state.Phase);
        Assert.DoesNotContain("automatically", state.Hint);
        Assert.Contains("reopen", state.Hint);
    }

    [Fact]
    public void DiscoveryFailureRetainsKnownEndpointsWithManualAndRestartGuidance()
    {
        var state = Ready().Apply(new("Discovery failed", "enumeration failed"));
        Assert.True(state.ListenerOpen);
        Assert.Equal(ReceiverUiPhase.Ready, state.Phase);
        Assert.Single(state.Addresses);
        Assert.Contains("manually", state.Hint);
        Assert.Contains("stopped", state.ProblemMessage);
        Assert.Contains("reopen", state.ProblemMessage);
    }

    [Fact]
    public void TransientBroadcastWarningsDoNotMarkAddressEnumerationAsTerminated()
    {
        var state = new ReceiverUiState().Apply(new("Listening", "socket open"))
            .Apply(new("Discovery warning", "broadcast failed"));
        Assert.False(state.DiscoveryStopped);
        Assert.Equal(ReceiverUiPhase.Starting, state.Phase);
        state = state.Apply(new("Listening", "address snapshot", ["127.0.0.1 : 27184"]));
        Assert.Equal(ReceiverUiPhase.Ready, state.Phase);
    }

    [Fact]
    public void DiscoveryTerminationSurvivesCastSetupAndDoesNotMaskPlaybackErrors()
    {
        var state = new ReceiverUiState().Apply(new("Listening", "socket open"))
            .Apply(new("Discovery failed", "enumeration failed"))
            .Apply(new("Initializing", "manual request"));
        Assert.Equal(ReceiverUiPhase.Connecting, state.Phase);
        state = state.Apply(new("Disconnected", "manual cast ended"));
        Assert.Equal(ReceiverUiPhase.DiscoveryUnavailable, state.Phase);
        var failed = Ready().Apply(new("AUDIO_FAILED", "audio failed"))
            .Apply(new("Discovery failed", "enumeration failed"));
        Assert.Equal("AUDIO_FAILED", failed.ProblemCode);
        Assert.True(failed.DiscoveryStopped);
    }

    [Fact]
    public void NetworkChangeClearsStaleAddressesUntilFreshSnapshot()
    {
        var state = Ready().Apply(new("Network changed", "changed"));
        Assert.Empty(state.Addresses);
        Assert.Equal(ReceiverUiPhase.Starting, state.Phase);
        Assert.Equal(ReceiverUiPhase.NoNetwork, state.Apply(new("Listening", "no network", [])).Phase);
    }

    [Theory]
    [InlineData("Start failed")]
    [InlineData("Listener failed")]
    [InlineData("Cleanup failed")]
    public void UnavailableStatesDoNotClaimToBeListening(string code)
    {
        var state = Ready().Apply(new(code, "technical details"));
        Assert.False(state.ListenerOpen);
        Assert.Equal(ReceiverUiPhase.Unavailable, state.Phase);
        Assert.Equal(ReceiverUiPhase.Unavailable, state.Apply(new("Listening", "late address update", ["127.0.0.1 : 27184"])).Phase);
    }

    [Theory]
    [InlineData("UNSUPPORTED_CONFIG")]
    [InlineData("DECODER_FAILED")]
    [InlineData("AUDIO_FAILED")]
    [InlineData("CONGESTION")]
    [InlineData("TIMEOUT")]
    [InlineData("VERSION")]
    [InlineData("PROTOCOL")]
    [InlineData("INTERNAL")]
    [InlineData("Sender error")]
    public void ErrorsHaveActionableUserMessages(string code)
    {
        var state = Ready().Apply(new(code, "private technical details"));
        Assert.Equal(ReceiverUiPhase.Error, state.Phase);
        Assert.False(string.IsNullOrWhiteSpace(state.ProblemMessage));
        Assert.DoesNotContain("private technical details", state.ProblemMessage);
    }

    [Fact]
    public void AdapterLabelsFollowSnapshotAndAreNotAdvertisedIdentity()
    {
        var labels = new Dictionary<string, string> { ["127.0.0.1 : 27184"] = "Ethernet", ["unused"] = "Unused" };
        var state = new ReceiverUiState().Apply(new("Listening", "snapshot", ["127.0.0.1 : 27184"], labels));
        labels["127.0.0.1 : 27184"] = "Changed";
        Assert.Single(state.AdapterLabels);
        Assert.Equal("Ethernet", state.AdapterLabels["127.0.0.1 : 27184"]);
        Assert.Empty(state.Apply(new("Network changed", "changed")).AdapterLabels);
        Assert.Empty(state.Apply(new("Listening", "snapshot", [])).AdapterLabels);
    }

    [Fact]
    public void ShutdownHidesListeningStateAndConnectionProgress()
    {
        var state = Ready().Apply(new("Initializing", "request")).Apply(new("Stopped", "closed"));
        Assert.Equal(ReceiverUiPhase.Stopped, state.Phase);
        Assert.False(state.ListenerOpen);
        Assert.False(state.Connecting);
        Assert.Equal(ReceiverUiPhase.Stopped, state.Apply(new("Listening", "late update", ["127.0.0.1 : 27184"])).Phase);
    }
}
