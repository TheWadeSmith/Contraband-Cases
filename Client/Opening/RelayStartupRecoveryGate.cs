namespace ContrabandCases.Client.Opening;

internal enum RelayStartupRecoveryState
{
    Waiting,
    Probing,
    Ready,
    Recovering,
    Failed,
    Disposed
}

// Main-thread ownership of discovery only. A prepared settlement retains its
// opening gate and server journal even when its presentation is detached.
internal sealed class RelayStartupRecoveryGate : IDisposable
{
    private object? _session;
    private object? _profile;
    private string? _profileId;

    internal long Generation { get; private set; }
    internal RelayStartupRecoveryState State { get; private set; } = RelayStartupRecoveryState.Waiting;

    internal bool TryBegin(object session, object profile, string profileId, bool lobbyReady,
        bool forceRetry, out long generation)
    {
        generation = 0;
        if (!lobbyReady || State is RelayStartupRecoveryState.Disposed or RelayStartupRecoveryState.Recovering ||
            string.IsNullOrWhiteSpace(profileId)) return false;

        if (SameContext(session, profile, profileId) &&
            (State is RelayStartupRecoveryState.Ready or RelayStartupRecoveryState.Probing ||
             State == RelayStartupRecoveryState.Failed && !forceRetry)) return false;

        _session = session;
        _profile = profile;
        _profileId = profileId;
        generation = ++Generation;
        State = RelayStartupRecoveryState.Probing;
        return true;
    }

    internal bool IsCurrent(long generation, object session, object profile, string profileId, bool lobbyReady) =>
        lobbyReady && State != RelayStartupRecoveryState.Disposed && generation == Generation &&
        SameContext(session, profile, profileId);

    internal void DeferProbe()
    {
        if (State != RelayStartupRecoveryState.Probing) return;
        ++Generation;
        State = RelayStartupRecoveryState.Waiting;
    }

    internal bool TryCompleteProbe(long generation, bool hasPending)
    {
        if (generation != Generation || State != RelayStartupRecoveryState.Probing) return false;
        State = hasPending ? RelayStartupRecoveryState.Recovering : RelayStartupRecoveryState.Ready;
        return true;
    }

    internal bool TryFail(long generation)
    {
        // Cleanup may mark a failed settlement before its exception reaches the
        // discovery caller. Keep that same-generation failure reportable.
        if (generation != Generation || State is not (RelayStartupRecoveryState.Probing or
                RelayStartupRecoveryState.Recovering or RelayStartupRecoveryState.Failed))
            return false;
        State = RelayStartupRecoveryState.Failed;
        return true;
    }

    internal void CompleteRecovery(bool terminal)
    {
        if (State != RelayStartupRecoveryState.Recovering) return;
        State = terminal ? RelayStartupRecoveryState.Ready : RelayStartupRecoveryState.Failed;
    }

    public void Dispose()
    {
        ++Generation;
        State = RelayStartupRecoveryState.Disposed;
    }

    private bool SameContext(object session, object profile, string profileId) =>
        ReferenceEquals(session, _session) && ReferenceEquals(profile, _profile) &&
        string.Equals(profileId, _profileId, StringComparison.Ordinal);
}
