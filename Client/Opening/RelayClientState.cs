using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ContrabandCases.Client.Opening;

public sealed class RelaySnapshotException : InvalidOperationException
{
    public RelaySnapshotException(string message)
        : base(message)
    {
    }

    public RelaySnapshotException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class OperationCallbackGate
{
    private readonly object _sync = new();
    private long _nextGeneration;
    private long _pendingGeneration;

    public bool IsPending
    {
        get
        {
            lock (_sync)
            {
                return _pendingGeneration != 0;
            }
        }
    }

    public long Begin()
    {
        lock (_sync)
        {
            if (_pendingGeneration != 0)
            {
                throw new InvalidOperationException("A Contraband Cases item operation is already pending.");
            }

            _pendingGeneration = checked(++_nextGeneration);
            return _pendingGeneration;
        }
    }

    public bool TryClaim(long generation)
    {
        lock (_sync)
        {
            if (generation <= 0 || _pendingGeneration != generation)
            {
                return false;
            }

            _pendingGeneration = 0;
            return true;
        }
    }

    public bool TryCancel(long generation)
    {
        lock (_sync)
        {
            if (generation <= 0 || _pendingGeneration != generation)
            {
                return false;
            }

            _pendingGeneration = 0;
            return true;
        }
    }
}

public enum RelayDispatchFailureRecovery
{
    RestoreVerifiedDecision,
    OfferDecisionSnapshotRetry,
    ReleaseDetachedRun
}

public static class RelayDispatchFailurePolicy
{
    public static RelayDispatchFailureRecovery Decide(
        bool hasVerifiedDecisionSnapshot,
        bool presentationDetached)
    {
        if (hasVerifiedDecisionSnapshot)
        {
            return RelayDispatchFailureRecovery.RestoreVerifiedDecision;
        }

        return presentationDetached
            ? RelayDispatchFailureRecovery.ReleaseDetachedRun
            : RelayDispatchFailureRecovery.OfferDecisionSnapshotRetry;
    }
}

public sealed class RelaySnapshotTimeoutBudget
{
    public const double DefaultTimeoutSeconds = 15d;

    private readonly double _timeoutSeconds;
    private double _elapsedSeconds;

    public RelaySnapshotTimeoutBudget(double timeoutSeconds = DefaultTimeoutSeconds)
    {
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        }

        _timeoutSeconds = timeoutSeconds;
    }

    public double ElapsedSeconds => _elapsedSeconds;

    public bool Advance(double unscaledDeltaSeconds)
    {
        if (!double.IsFinite(unscaledDeltaSeconds) || unscaledDeltaSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(unscaledDeltaSeconds));
        }

        _elapsedSeconds = Math.Min(_timeoutSeconds, _elapsedSeconds + unscaledDeltaSeconds);
        return _elapsedSeconds >= _timeoutSeconds;
    }
}

public sealed class VerificationRetryBudget
{
    private readonly int _maximumAutomaticRetries;
    private int _automaticRetries;

    public VerificationRetryBudget(int maximumAutomaticRetries = 1)
    {
        if (maximumAutomaticRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAutomaticRetries));
        }

        _maximumAutomaticRetries = maximumAutomaticRetries;
    }

    public int AutomaticRetries => _automaticRetries;

    public bool TryTakeAutomaticRetry()
    {
        if (_automaticRetries >= _maximumAutomaticRetries)
        {
            return false;
        }

        _automaticRetries++;
        return true;
    }

    public void Reset() => _automaticRetries = 0;
}

public sealed class RelayPreparedActionRecovery
{
    public bool IsPending { get; private set; }

    public void Begin()
    {
        if (IsPending)
        {
            throw new InvalidOperationException(
                "A restart-prepared Relay action is already active.");
        }

        IsPending = true;
    }

    internal void Complete(ref RelaySnapshot? decisionSnapshot)
    {
        if (!IsPending)
        {
            throw new InvalidOperationException(
                "No restart-prepared Relay action is active.");
        }

        if (decisionSnapshot is null)
        {
            throw new InvalidOperationException(
                "The restart-prepared Relay snapshot is unavailable.");
        }

        decisionSnapshot = null;
        IsPending = false;
    }
}

public sealed class RelayDecisionAvailability
{
    public RelayDecisionAvailability(bool relayEnabled, string? disabledReason)
    {
        if (relayEnabled && disabledReason is not null)
        {
            throw new ArgumentException("An enabled Relay decision cannot have a disabled reason.", nameof(disabledReason));
        }
        if (!relayEnabled && string.IsNullOrWhiteSpace(disabledReason))
        {
            throw new ArgumentException("A disabled Relay decision requires a visible reason.", nameof(disabledReason));
        }

        RelayEnabled = relayEnabled;
        DisabledReason = disabledReason;
    }

    public bool SecureEnabled => true;

    public bool RelayEnabled { get; }

    public string? DisabledReason { get; }

    public static RelayDecisionAvailability Available() => new(true, null);

    public static RelayDecisionAvailability Disabled(string reason) => new(false, reason);
}

public sealed class RelayHoldConfirmation
{
    public const double DefaultDurationSeconds = 1.2d;

    private readonly double _durationSeconds;
    private double _elapsedSeconds;
    private bool _holding;

    public RelayHoldConfirmation(double durationSeconds = DefaultDurationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        _durationSeconds = durationSeconds;
    }

    public double Progress => Math.Min(1d, _elapsedSeconds / _durationSeconds);

    public bool IsConfirmed { get; private set; }

    public bool Begin()
    {
        if (IsConfirmed)
        {
            return false;
        }

        _holding = true;
        return true;
    }

    public bool Advance(double unscaledDeltaSeconds)
    {
        if (!double.IsFinite(unscaledDeltaSeconds) || unscaledDeltaSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(unscaledDeltaSeconds));
        }

        if (!_holding || IsConfirmed)
        {
            return false;
        }

        _elapsedSeconds = Math.Min(_durationSeconds, _elapsedSeconds + unscaledDeltaSeconds);
        if (_elapsedSeconds < _durationSeconds)
        {
            return false;
        }

        _holding = false;
        IsConfirmed = true;
        return true;
    }

    public void Cancel()
    {
        if (IsConfirmed)
        {
            return;
        }

        _holding = false;
        _elapsedSeconds = 0d;
    }
}

public enum RelayClientDestination
{
    NextDecision,
    Terminal
}

public static class RelayTerminalPolicy
{
    public static RelayClientDestination After(RelayOutcome outcome, bool terminal) =>
        outcome == RelayOutcome.RarityUpgrade && !terminal
            ? RelayClientDestination.NextDecision
            : RelayClientDestination.Terminal;
}

public enum RelayPendingRecovery
{
    None,
    ResumeSecure,
    ResumeRelay
}

public static class RelaySnapshotRecovery
{
    public static RelayPendingRecovery Decide(RelaySnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        if (!snapshot.Status.SettlementPending)
        {
            if (snapshot.Status.PendingAction is not null)
            {
                throw new RelaySnapshotException("A completed Relay snapshot cannot publish a pending action.");
            }

            return RelayPendingRecovery.None;
        }

        return snapshot.Status.PendingAction switch
        {
            "Secure" => RelayPendingRecovery.ResumeSecure,
            "Relay" => RelayPendingRecovery.ResumeRelay,
            _ => throw new RelaySnapshotException("A pending Relay snapshot has an invalid action.")
        };
    }
}

public static class RelayPendingDiscoveryEnvelope
{
    public static RelayPendingDiscovery Parse(string json)
    {
        try
        {
            var token = JToken.Parse(json, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            });
            if (token is not JObject envelope ||
                envelope["err"]?.Type != JTokenType.Integer ||
                envelope["err"]!.Value<int>() != 0 ||
                envelope["data"] is not JObject data ||
                !data.Properties().Any(property =>
                    string.Equals(property.Name, "pendingSnapshot", StringComparison.Ordinal)))
            {
                throw new RelaySnapshotException(
                    "The Relay recovery server returned an unsuccessful or malformed response.");
            }

            var discovery = data.ToObject<RelayPendingDiscovery>(
                JsonSerializer.Create(new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    NullValueHandling = NullValueHandling.Include
                })) ?? throw new RelaySnapshotException(
                    "The Relay recovery response did not contain discovery data.");
            Validate(discovery);
            return discovery;
        }
        catch (RelaySnapshotException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new RelaySnapshotException(
                "The Relay recovery server response was not valid JSON.",
                exception);
        }
        catch (Exception exception)
        {
            throw new RelaySnapshotException(
                "The Relay recovery server response failed validation.",
                exception);
        }
    }

    public static void Validate(RelayPendingDiscovery discovery)
    {
        if (discovery is null)
        {
            throw new ArgumentNullException(nameof(discovery));
        }

        var snapshot = discovery.PendingSnapshot;
        if (snapshot is null)
        {
            return;
        }

        var stakeRootId = snapshot.Status?.StakeRootId;
        if (!RelaySnapshotEnvelope.IsMongoId(stakeRootId))
        {
            throw new RelaySnapshotException(
                "The pending Relay recovery has an invalid stake root ID.");
        }

        RelaySnapshotEnvelope.Validate(snapshot, stakeRootId!);
        if (RelaySnapshotRecovery.Decide(snapshot) == RelayPendingRecovery.None ||
            snapshot.LatestReceipt is not null)
        {
            throw new RelaySnapshotException(
                "Relay recovery discovery may publish only an unresolved prepared action.");
        }
    }
}

public enum RelayInteractionOrigin
{
    RealOpening,
    CosmeticPreview
}

public static class RelayInteractionPolicy
{
    public static bool CanDispatch(RelayInteractionOrigin origin) =>
        origin == RelayInteractionOrigin.RealOpening;

    public static bool CanFetchSnapshot(RelayInteractionOrigin origin) =>
        origin == RelayInteractionOrigin.RealOpening;
}

public static class RelaySnapshotEnvelope
{
    public static RelaySnapshot Parse(string json, string requestedStakeRootId)
    {
        if (!IsMongoId(requestedStakeRootId))
        {
            throw new RelaySnapshotException("The requested Relay stake root ID is invalid.");
        }

        try
        {
            var token = JToken.Parse(json, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            });
            if (token is not JObject envelope ||
                envelope["err"]?.Type != JTokenType.Integer ||
                envelope["err"]!.Value<int>() != 0 ||
                envelope["data"] is not JObject data)
            {
                throw new RelaySnapshotException("The Relay server returned an unsuccessful or malformed response.");
            }

            var snapshot = data.ToObject<RelaySnapshot>(JsonSerializer.Create(new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Include
            })) ?? throw new RelaySnapshotException("The Relay server response did not contain snapshot data.");
            Validate(snapshot, requestedStakeRootId);
            return snapshot;
        }
        catch (RelaySnapshotException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new RelaySnapshotException("The Relay server response was not valid JSON.", exception);
        }
        catch (Exception exception)
        {
            throw new RelaySnapshotException("The Relay server response failed validation.", exception);
        }
    }

    public static void Validate(RelaySnapshot snapshot, string requestedStakeRootId)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        var status = snapshot.Status
            ?? throw new RelaySnapshotException("The Relay snapshot is missing status.");
        if (!string.Equals(status.StakeRootId, requestedStakeRootId, StringComparison.Ordinal) ||
            !IsMongoId(status.StakeRootId))
        {
            throw new RelaySnapshotException("The Relay snapshot does not describe the requested stake.");
        }

        if (string.IsNullOrWhiteSpace(status.RewardId) ||
            !TryRarity(status.Rarity, out var rarity) ||
            !TryRarityLadderVersion(status.RarityLadderVersion, out var rarityLadderVersion) ||
            !RelayRules.IsSupportedRarity(rarity, rarityLadderVersion) ||
            status.Stage is < 1 or > RelayRules.MaximumStage ||
            status.RecoveryMeterMaximum != RelayRules.MaximumRecoveryMeter ||
            status.RecoveryMeter is < 0 or > RelayRules.MaximumRecoveryMeter ||
            status.GuaranteedUpgradeReady != (status.RecoveryMeter == RelayRules.MaximumRecoveryMeter) ||
            status.Terminal && status.RelayEligible)
        {
            throw new RelaySnapshotException("The Relay status contains contradictory state.");
        }

        if (status.RelayEligible != (!status.Terminal && RelayRules.CanRelay(
                rarity,
                status.Stage,
                rarityLadderVersion)))
        {
            throw new RelaySnapshotException("The Relay eligibility flag contradicts the published chain state.");
        }

        _ = RelaySnapshotRecovery.Decide(snapshot);
        ValidateLadder(snapshot.PublishedLadder);
        ValidateCandidates(snapshot, rarity, rarityLadderVersion);
        if (snapshot.LatestReceipt is not null)
        {
            ValidateReceipt(snapshot.LatestReceipt, status.StakeRootId, rarityLadderVersion);
            if (status.SettlementPending)
            {
                throw new RelaySnapshotException("A pending Relay settlement cannot also publish a committed receipt.");
            }
        }
    }

    public static RelayOutcome ParseOutcome(RelayReceipt receipt)
    {
        if (receipt is null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }
        if (!Enum.TryParse<RelayOutcome>(receipt.Outcome, ignoreCase: false, out var outcome) ||
            !string.Equals(outcome.ToString(), receipt.Outcome, StringComparison.Ordinal))
        {
            throw new RelaySnapshotException("The Relay receipt contains an unknown outcome.");
        }

        return outcome;
    }

    private static void ValidateLadder(IReadOnlyList<RelayPublishedStage>? ladder)
    {
        if (ladder is null || ladder.Count != RelayRules.MaximumStage)
        {
            throw new RelaySnapshotException("The Relay snapshot does not publish the complete risk ladder.");
        }

        for (var index = 0; index < ladder.Count; index++)
        {
            var stage = ladder[index]
                ?? throw new RelaySnapshotException("The Relay risk ladder contains an empty stage.");
            var expectedStage = index + 1;
            var expected = RelayRules.GetOdds(expectedStage);
            if (stage.Stage != expectedStage ||
                stage.UpgradePercent != expected.UpgradePercent ||
                stage.SidegradePercent != expected.SidegradePercent ||
                stage.ConfiscatePercent != expected.ConfiscatePercent)
            {
                throw new RelaySnapshotException("The Relay risk ladder does not match the published rules.");
            }
        }
    }

    private static void ValidateCandidates(
        RelaySnapshot snapshot,
        RewardRarity currentRarity,
        RarityLadderVersion rarityLadderVersion)
    {
        var upgrades = snapshot.UpgradeCandidates
            ?? throw new RelaySnapshotException("The Relay snapshot is missing upgrade candidates.");
        var sidegrades = snapshot.SidegradeCandidates
            ?? throw new RelaySnapshotException("The Relay snapshot is missing sidegrade candidates.");
        ValidateCandidateSet(upgrades, "upgrade");
        ValidateCandidateSet(sidegrades, "sidegrade");

        if (!snapshot.Status.RelayEligible)
        {
            if (upgrades.Count != 0 || sidegrades.Count != 0)
            {
                throw new RelaySnapshotException("A terminal Relay stake cannot publish candidate rewards.");
            }

            return;
        }

        var upgradeRarity = RelayRules.GetUpgradeRarity(currentRarity, rarityLadderVersion);
        if (upgrades.Count == 0 || upgrades.Any(candidate =>
                !string.Equals(candidate.Rarity, upgradeRarity.ToString(), StringComparison.Ordinal)) ||
            sidegrades.Count == 0 || sidegrades.Any(candidate =>
                !string.Equals(candidate.Rarity, currentRarity.ToString(), StringComparison.Ordinal) ||
                string.Equals(candidate.RewardId, snapshot.Status.RewardId, StringComparison.Ordinal)))
        {
            throw new RelaySnapshotException("The Relay candidate pools contradict the advertised rarity change.");
        }
    }

    private static void ValidateCandidateSet(IReadOnlyList<RelayCandidate> candidates, string label)
    {
        if (candidates.Any(candidate => candidate is null ||
                string.IsNullOrWhiteSpace(candidate.RewardId) ||
                string.IsNullOrWhiteSpace(candidate.DisplayName) ||
                !TryRarity(candidate.Rarity, out _) ||
                candidate.HandbookValue < 0) ||
            candidates.Select(candidate => candidate.RewardId).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
        {
            throw new RelaySnapshotException($"The Relay {label} candidate pool is invalid.");
        }
    }

    private static void ValidateReceipt(
        RelayReceipt receipt,
        string stakeRootId,
        RarityLadderVersion rarityLadderVersion)
    {
        var outcome = ParseOutcome(receipt);
        if (!string.Equals(receipt.StakeRootId, stakeRootId, StringComparison.Ordinal) ||
            !string.Equals(receipt.RarityLadderVersion, rarityLadderVersion.ToString(), StringComparison.Ordinal) ||
            receipt.Stage is < 1 or > RelayRules.MaximumStage ||
            receipt.RecoveryMeterMaximum != RelayRules.MaximumRecoveryMeter ||
            receipt.RecoveryMeter is < 0 or > RelayRules.MaximumRecoveryMeter ||
            string.IsNullOrWhiteSpace(receipt.PreviousRewardId) ||
            !TryRarity(receipt.PreviousRarity, out _))
        {
            throw new RelaySnapshotException("The Relay receipt contains invalid chain identity.");
        }

        if (receipt.Action == "Secure")
        {
            if (outcome != RelayOutcome.Secured || !receipt.Terminal ||
                receipt.RewardId is not null || receipt.RewardRootId is not null || receipt.Rarity is not null)
            {
                throw new RelaySnapshotException("The secure receipt contains an economic mutation.");
            }

            return;
        }

        if (receipt.Action != "Relay" || outcome == RelayOutcome.Secured)
        {
            throw new RelaySnapshotException("The Relay receipt contains an invalid action/outcome pair.");
        }

        var hasReward = outcome != RelayOutcome.Confiscated;
        if (hasReward != (receipt.RewardId is not null &&
                receipt.RewardRootId is not null &&
                receipt.Rarity is not null) ||
            receipt.RewardRootId is not null && !IsMongoId(receipt.RewardRootId) ||
            receipt.Rarity is not null && !TryRarity(receipt.Rarity, out _))
        {
            throw new RelaySnapshotException("The Relay receipt output does not match its outcome.");
        }

        if (outcome != RelayOutcome.RarityUpgrade && !receipt.Terminal)
        {
            throw new RelaySnapshotException("Only a rarity upgrade may continue a Relay chain.");
        }
        if (outcome == RelayOutcome.RarityUpgrade &&
            (!TryRarity(receipt.PreviousRarity, out var inputRarity) ||
             !TryRarity(receipt.Rarity, out var outputRarity) ||
             outputRarity != RelayRules.GetUpgradeRarity(inputRarity, rarityLadderVersion)) ||
            outcome == RelayOutcome.SameRaritySidegrade &&
            (!TryRarity(receipt.PreviousRarity, out var sidegradeInputRarity) ||
             !TryRarity(receipt.Rarity, out var sidegradeOutputRarity) ||
             sidegradeOutputRarity != sidegradeInputRarity))
        {
            throw new RelaySnapshotException("The Relay receipt output contradicts its rarity ladder.");
        }
        if (outcome == RelayOutcome.RarityUpgrade &&
            (receipt.Stage >= RelayRules.MaximumStage ||
             string.Equals(receipt.Rarity, RewardRarity.BlackLabel.ToString(), StringComparison.Ordinal)) &&
            !receipt.Terminal)
        {
            throw new RelaySnapshotException("A stage-three or Legendary rarity upgrade must be terminal.");
        }
    }

    private static bool TryRarity(string? value, out RewardRarity rarity) =>
        Enum.TryParse(value, ignoreCase: false, out rarity) &&
        string.Equals(rarity.ToString(), value, StringComparison.Ordinal);

    private static bool TryRarityLadderVersion(
        string? value,
        out RarityLadderVersion rarityLadderVersion) =>
        Enum.TryParse(value, ignoreCase: false, out rarityLadderVersion) &&
        string.Equals(rarityLadderVersion.ToString(), value, StringComparison.Ordinal);

    public static bool IsMongoId(string? value) =>
        value is { Length: 24 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

public static class RelayCandidateCatalogValidator
{
    public static void Validate(
        IEnumerable<RelayCandidate> candidates,
        IReadOnlyList<ValidatedReward> localRewards)
    {
        if (candidates is null)
        {
            throw new ArgumentNullException(nameof(candidates));
        }
        if (localRewards is null)
        {
            throw new ArgumentNullException(nameof(localRewards));
        }
        var localCatalog = localRewards.ToDictionary(reward => reward.Id, StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (candidate is null ||
                !localCatalog.TryGetValue(candidate.RewardId, out var local) ||
                !string.Equals(candidate.DisplayName, local.DisplayName, StringComparison.Ordinal) ||
                !string.Equals(candidate.Rarity, local.Rarity.ToString(), StringComparison.Ordinal))
            {
                throw new RelaySnapshotException(
                    "A published Relay candidate does not match the validated local catalog.");
            }
        }
    }
}
