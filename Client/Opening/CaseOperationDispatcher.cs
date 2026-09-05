using Comfort.Common;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Manifest;
using EFT;
using Newtonsoft.Json;

namespace ContrabandCases.Client.Opening;

internal sealed class CaseOperationDispatcher
{
    public void DispatchManifestOpen(IClientSession session, Profile expectedProfile,
        string expectedProfileId, string caseItemId, string? expectedCatalogSnapshotId, Callback callback) =>
        DispatchManifestOperation(session, expectedProfile, expectedProfileId,
            new ManifestOpenOperationParams(caseItemId, expectedCatalogSnapshotId),
            RelayInteractionOrigin.RealOpening, callback);

    public void Dispatch(
        IClientSession session,
        Profile expectedProfile,
        string expectedProfileId,
        string caseItemId,
        Callback callback)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        DispatchAction(
            session,
            expectedProfile,
            expectedProfileId,
            ModConstants.OpenAction,
            caseItemId,
            RelayInteractionOrigin.RealOpening,
            callback);
    }

    public void DispatchRelay(
        IClientSession session,
        Profile expectedProfile,
        string expectedProfileId,
        string action,
        string stakeRootId,
        RelayInteractionOrigin origin,
        Callback callback)
    {
        if (action is not (ModConstants.RelaySecureAction or ModConstants.RelayAction))
        {
            throw new ArgumentException("A supported Relay action is required.", nameof(action));
        }

        DispatchAction(
            session,
            expectedProfile,
            expectedProfileId,
            action,
            stakeRootId,
            origin,
            callback);
    }

    public void DispatchManifestDecision(
        IClientSession session,
        Profile expectedProfile,
        string expectedProfileId,
        string action,
        string manifestId,
        int expectedOrdinal,
        RelayInteractionOrigin origin,
        Callback callback,
        int? selectedOrdinal = null)
    {
        var operation = new ManifestDecisionOperationParams(
            action,
            manifestId,
            expectedOrdinal, selectedOrdinal);
        DispatchManifestOperation(
            session,
            expectedProfile,
            expectedProfileId,
            operation,
            origin,
            callback);
    }

    public void DispatchManifestClaim(
        IClientSession session,
        Profile expectedProfile,
        string expectedProfileId,
        string manifestId,
        ManifestPhase expectedPhase,
        RelayInteractionOrigin origin,
        Callback callback)
    {
        var operation = new ManifestClaimOperationParams(manifestId, expectedPhase);
        DispatchManifestOperation(
            session,
            expectedProfile,
            expectedProfileId,
            operation,
            origin,
            callback);
    }

    public void DispatchManifestRelay(
        IClientSession session,
        Profile expectedProfile,
        string expectedProfileId,
        string manifestId,
        ManifestPhase expectedPhase,
        int expectedRelayStage,
        RelayInteractionOrigin origin,
        Callback callback)
    {
        var operation = new ManifestRelayOperationParams(
            manifestId,
            expectedPhase,
            expectedRelayStage);
        DispatchManifestOperation(
            session,
            expectedProfile,
            expectedProfileId,
            operation,
            origin,
            callback);
    }

    public void DispatchManifestForfeit(
        IClientSession session,
        Profile expectedProfile,
        string expectedProfileId,
        string manifestId,
        ManifestPhase expectedPhase,
        RelayInteractionOrigin origin,
        Callback callback)
    {
        var operation = new ManifestForfeitOperationParams(manifestId, expectedPhase);
        DispatchManifestOperation(
            session,
            expectedProfile,
            expectedProfileId,
            operation,
            origin,
            callback);
    }

    private static void DispatchManifestOperation<TRequest>(
        IClientSession session,
        Profile expectedProfile,
        string expectedProfileId,
        TRequest operation,
        RelayInteractionOrigin origin,
        Callback callback)
        where TRequest : class
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }
        if (!RelayInteractionPolicy.CanDispatch(origin))
        {
            throw new InvalidOperationException("Cosmetic previews cannot dispatch item operations.");
        }
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        var profile = session.Profile
            ?? throw new InvalidOperationException("The authenticated Tarkov profile is unavailable.");
        if (!ReferenceEquals(profile, expectedProfile) ||
            !string.Equals(profile.Id, expectedProfileId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The authenticated Tarkov profile changed before enqueue.");
        }

        session.SendOperationRightNow(operation, callback);
    }

    private static void DispatchAction(
        IClientSession session,
        Profile expectedProfile,
        string expectedProfileId,
        string action,
        string itemId,
        RelayInteractionOrigin origin,
        Callback callback)
    {
        if (!RelayInteractionPolicy.CanDispatch(origin))
        {
            throw new InvalidOperationException("Cosmetic previews cannot dispatch item operations.");
        }
        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new ArgumentException("An item ID is required.", nameof(itemId));
        }
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        var profile = session.Profile
            ?? throw new InvalidOperationException("The authenticated Tarkov profile is unavailable.");
        if (!ReferenceEquals(profile, expectedProfile) ||
            !string.Equals(profile.Id, expectedProfileId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The authenticated Tarkov profile changed before enqueue.");
        }

        var operation = new OpenRandomLootContainerOperationParams<string, string, UnpackInfo[]>(
            action,
            itemId,
            [new UnpackInfo(profile.Id)]);
        session.SendOperationRightNow(operation, callback);
    }
}

internal sealed class ManifestDecisionOperationParams
{
    public ManifestDecisionOperationParams(
        string action,
        string manifestId,
        int expectedOrdinal,
        int? selectedOrdinal = null)
    {
        if (action is not (ModConstants.ManifestLockAction or ModConstants.ManifestBurnAction))
        {
            throw new ArgumentException("A Manifest Lock or Burn action is required.", nameof(action));
        }
        if (expectedOrdinal is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedOrdinal));
        }
        if (selectedOrdinal is < 1 or > 3 || selectedOrdinal.HasValue &&
            (action != ModConstants.ManifestLockAction || expectedOrdinal != 1))
            throw new ArgumentOutOfRangeException(nameof(selectedOrdinal));
        this.selectedOrdinal = selectedOrdinal;

        Action = action;
        this.manifestId = ManifestProtocolValidation.RequireIdentifier(
            manifestId,
            nameof(manifestId));
        this.expectedOrdinal = expectedOrdinal;
    }

    public string Action { get; }

    [JsonProperty("manifestId")]
    public string manifestId { get; }

    [JsonProperty("expectedOrdinal")]
    public int expectedOrdinal { get; }

    [JsonProperty("selectedOrdinal", NullValueHandling = NullValueHandling.Ignore)]
    public int? selectedOrdinal { get; }
}

internal sealed class ManifestClaimOperationParams
{
    public ManifestClaimOperationParams(string manifestId, ManifestPhase expectedPhase)
    {
        if (expectedPhase is not (
                ManifestPhase.Entitlement or
                ManifestPhase.ClaimPrepared or
                ManifestPhase.RewardOwed))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedPhase));
        }

        Action = ModConstants.ManifestClaimAction;
        this.manifestId = ManifestProtocolValidation.RequireIdentifier(
            manifestId,
            nameof(manifestId));
        this.expectedPhase = expectedPhase.ToString();
    }

    public string Action { get; }

    [JsonProperty("manifestId")]
    public string manifestId { get; }

    [JsonProperty("expectedPhase")]
    public string expectedPhase { get; }
}

internal sealed class ManifestRelayOperationParams
{
    public ManifestRelayOperationParams(
        string manifestId,
        ManifestPhase expectedPhase,
        int expectedRelayStage)
    {
        if (expectedPhase is not (ManifestPhase.Entitlement or ManifestPhase.RelayPrepared))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedPhase));
        }
        if (expectedRelayStage is < 1 or > Shared.Relay.RelayRules.MaximumStage)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRelayStage));
        }

        Action = ModConstants.ManifestRelayAction;
        this.manifestId = ManifestProtocolValidation.RequireIdentifier(
            manifestId,
            nameof(manifestId));
        this.expectedPhase = expectedPhase.ToString();
        this.expectedRelayStage = expectedRelayStage;
    }

    public string Action { get; }

    [JsonProperty("manifestId")]
    public string manifestId { get; }

    [JsonProperty("expectedPhase")]
    public string expectedPhase { get; }

    [JsonProperty("expectedRelayStage")]
    public int expectedRelayStage { get; }
}

internal sealed class ManifestForfeitOperationParams
{
    public ManifestForfeitOperationParams(string manifestId, ManifestPhase expectedPhase)
    {
        if (expectedPhase is not (
                ManifestPhase.Offer1 or
                ManifestPhase.Offer2 or
                ManifestPhase.Entitlement))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedPhase));
        }

        Action = ModConstants.ManifestForfeitAction;
        this.manifestId = ManifestProtocolValidation.RequireIdentifier(
            manifestId,
            nameof(manifestId));
        this.expectedPhase = expectedPhase.ToString();
    }

    public string Action { get; }

    [JsonProperty("manifestId")]
    public string manifestId { get; }

    [JsonProperty("expectedPhase")]
    public string expectedPhase { get; }
}
