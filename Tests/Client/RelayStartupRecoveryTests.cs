using ContrabandCases.Client.Opening;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class RelayStartupRecoveryTests
{
    private readonly object _session = new();
    private readonly object _profile = new();

    [Fact]
    public void Loading_or_transit_cannot_start_discovery_even_for_a_new_profile_or_manual_retry()
    {
        var recovery = new RelayStartupRecoveryGate();
        Assert.False(recovery.TryBegin(_session, _profile, "pmc", lobbyReady: false, forceRetry: true, out _));
        Assert.Equal(RelayStartupRecoveryState.Waiting, recovery.State);
        Assert.True(recovery.TryBegin(_session, _profile, "pmc", true, false, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interrupted_discovery_rejects_old_success_or_failure_and_resumes_on_lobby_return(bool fail)
    {
        var recovery = new RelayStartupRecoveryGate();
        Assert.True(recovery.TryBegin(_session, _profile, "pmc", true, false, out var old));
        recovery.DeferProbe();
        Assert.Equal(RelayStartupRecoveryState.Waiting, recovery.State);
        Assert.False(recovery.TryBegin(_session, _profile, "pmc", false, false, out _));
        Assert.True(recovery.TryBegin(_session, _profile, "pmc", true, false, out var current));
        Assert.False(fail ? recovery.TryFail(old) : recovery.TryCompleteProbe(old, hasPending: false));
        Assert.Equal(RelayStartupRecoveryState.Probing, recovery.State);
        Assert.True(recovery.TryCompleteProbe(current, hasPending: false));
        Assert.Equal(RelayStartupRecoveryState.Ready, recovery.State);
    }

    [Fact]
    public void Completed_response_is_not_current_when_loading_begins_before_it_is_consumed()
    {
        var recovery = new RelayStartupRecoveryGate();
        Assert.True(recovery.TryBegin(_session, _profile, "pmc", true, false, out var token));
        Assert.False(recovery.IsCurrent(token, _session, _profile, "pmc", lobbyReady: false));
    }

    [Fact]
    public void New_session_profile_or_profile_id_invalidates_an_older_response()
    {
        var recovery = new RelayStartupRecoveryGate();
        Assert.True(recovery.TryBegin(_session, _profile, "pmc", true, false, out var token));
        Assert.False(recovery.IsCurrent(token, new object(), _profile, "pmc", true));
        Assert.False(recovery.IsCurrent(token, _session, new object(), "pmc", true));
        Assert.False(recovery.IsCurrent(token, _session, _profile, "scav", true));
        Assert.True(recovery.TryBegin(new object(), new object(), "pmc", true, false, out var next));
        Assert.False(recovery.TryFail(token));
        Assert.True(recovery.TryCompleteProbe(next, hasPending: false));
    }

    [Fact]
    public void Genuine_failure_stays_blocked_without_an_automatic_retry_loop()
    {
        var recovery = new RelayStartupRecoveryGate();
        Assert.True(recovery.TryBegin(_session, _profile, "pmc", true, false, out var token));
        Assert.True(recovery.TryFail(token));
        recovery.DeferProbe();
        Assert.Equal(RelayStartupRecoveryState.Failed, recovery.State);
        Assert.False(recovery.TryBegin(_session, _profile, "pmc", true, false, out _));
        Assert.True(recovery.TryBegin(_session, _profile, "pmc", true, true, out var retry));
        Assert.True(recovery.TryCompleteProbe(retry, hasPending: false));
    }

    [Fact]
    public void Transition_does_not_release_an_already_prepared_settlement()
    {
        var recovery = new RelayStartupRecoveryGate();
        Assert.True(recovery.TryBegin(_session, _profile, "pmc", true, false, out var token));
        Assert.True(recovery.TryCompleteProbe(token, hasPending: true));
        recovery.DeferProbe();
        Assert.Equal(RelayStartupRecoveryState.Recovering, recovery.State);
        Assert.False(recovery.TryBegin(new object(), new object(), "other", true, true, out _));
        recovery.CompleteRecovery(terminal: false);
        Assert.Equal(RelayStartupRecoveryState.Failed, recovery.State);
    }

    [Fact]
    public void Recovery_cleanup_keeps_failure_reportable_without_unlocking_new_openings()
    {
        var recovery = new RelayStartupRecoveryGate();
        Assert.True(recovery.TryBegin(_session, _profile, "pmc", true, false, out var token));
        Assert.True(recovery.TryCompleteProbe(token, hasPending: true));
        recovery.CompleteRecovery(terminal: false);
        Assert.True(recovery.TryFail(token));
        Assert.False(recovery.TryBegin(_session, _profile, "pmc", true, false, out _));
    }

    [Fact]
    public void Disposed_recovery_cannot_restart_or_accept_a_late_response()
    {
        var recovery = new RelayStartupRecoveryGate();
        Assert.True(recovery.TryBegin(_session, _profile, "pmc", true, false, out var token));
        recovery.Dispose();
        recovery.DeferProbe();
        recovery.CompleteRecovery(terminal: true);
        Assert.False(recovery.TryFail(token));
        Assert.False(recovery.TryCompleteProbe(token, hasPending: false));
        Assert.False(recovery.TryBegin(_session, _profile, "pmc", true, true, out _));
        Assert.Equal(RelayStartupRecoveryState.Disposed, recovery.State);
    }
}
