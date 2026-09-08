using System.Text.Json.Serialization;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace ContrabandCases.Server.Settlement;

[Injectable]
public sealed class RelaySnapshotRouter : StaticRouter
{
    public RelaySnapshotRouter(
        JsonUtil jsonUtil,
        HttpResponseUtil httpResponseUtil,
        SptCaseJournal journalStore,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        ServerRewardCatalog catalog)
        : base(jsonUtil,
        [
            new RouteAction<RelaySnapshotRequest>(
                ModConstants.RelaySnapshotRoute,
                (_, request, sessionId, _, cancellationToken) => CreateSnapshotResponseAsync(
                    request,
                    sessionId,
                    httpResponseUtil,
                    journalStore,
                    profileLocks,
                    raidSessions,
                    catalog,
                    cancellationToken)),
            new RouteAction<RelayPendingRequest>(
                ModConstants.RelayPendingRoute,
                (_, request, sessionId, _, cancellationToken) => CreatePendingResponseAsync(
                    request,
                    sessionId,
                    httpResponseUtil,
                    journalStore,
                    profileLocks,
                    raidSessions,
                    catalog,
                    cancellationToken))
        ])
    {
    }

    internal static async ValueTask<string> CreateSnapshotResponseAsync(
        RelaySnapshotRequest request,
        MongoId profileId,
        HttpResponseUtil httpResponseUtil,
        ICaseOpeningJournalStore journalStore,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        IRelayRewardCatalog catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(httpResponseUtil);
        if (!IsMongoId(request.StakeRootId))
        {
            throw new InvalidOperationException("Relay snapshot requires a valid stake root ID.");
        }

        await using var profileLock = await profileLocks.AcquireAsync(profileId, cancellationToken).ConfigureAwait(false);
        raidSessions.RequireLobby(profileId);
        var journal = await journalStore.LoadAsync(profileId, cancellationToken).ConfigureAwait(false);
        var stakeRootId = (MongoId)request.StakeRootId;
        var stake = RelayChainResolver.Resolve(journal, stakeRootId, catalog.FindReward);
        var snapshot = BuildSnapshot(journal, stake, catalog);
        return httpResponseUtil.GetBody(snapshot);
    }

    internal static async ValueTask<string> CreatePendingResponseAsync(
        RelayPendingRequest request,
        MongoId profileId,
        HttpResponseUtil httpResponseUtil,
        ICaseOpeningJournalStore journalStore,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        IRelayRewardCatalog catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(httpResponseUtil);
        var discovery = await DiscoverPendingAsync(
                profileId,
                journalStore,
                profileLocks,
                raidSessions,
                catalog,
                cancellationToken)
            .ConfigureAwait(false);
        return httpResponseUtil.GetBody(CreatePendingResponseData(discovery));
    }

    internal static async ValueTask<RelayPendingDiscovery> DiscoverPendingAsync(
        MongoId profileId,
        ICaseOpeningJournalStore journalStore,
        ProfileLockPool profileLocks,
        RaidSessionState raidSessions,
        IRelayRewardCatalog catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journalStore);
        ArgumentNullException.ThrowIfNull(profileLocks);
        ArgumentNullException.ThrowIfNull(raidSessions);
        ArgumentNullException.ThrowIfNull(catalog);

        await using var profileLock = await profileLocks
            .AcquireAsync(profileId, cancellationToken)
            .ConfigureAwait(false);
        raidSessions.RequireLobby(profileId);
        var journal = await journalStore.LoadAsync(profileId, cancellationToken).ConfigureAwait(false);
        return BuildPendingDiscovery(journal, catalog);
    }

    internal static RelayPendingDiscovery BuildPendingDiscovery(
        CaseOpeningJournal journal,
        IRelayRewardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(catalog);
        var prepared = journal.PreparedRelay;
        if (prepared is null)
        {
            return new RelayPendingDiscovery();
        }

        var stake = RelayChainResolver.Resolve(journal, prepared.StakeRootId, catalog.FindReward);
        var snapshot = BuildSnapshot(journal, stake, catalog);
        if (!snapshot.Status.SettlementPending ||
            !string.Equals(snapshot.Status.StakeRootId, prepared.StakeRootId.ToString(), StringComparison.Ordinal) ||
            !string.Equals(snapshot.Status.PendingAction, prepared.Action.ToString(), StringComparison.Ordinal) ||
            snapshot.LatestReceipt is not null)
        {
            throw new InvalidOperationException(
                "The prepared Relay transaction cannot produce an exact pending recovery snapshot.");
        }

        return new RelayPendingDiscovery { PendingSnapshot = snapshot };
    }

    internal static RelayPendingResponseData CreatePendingResponseData(
        RelayPendingDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        return new RelayPendingResponseData { PendingSnapshot = discovery.PendingSnapshot };
    }

    internal static RelaySnapshot BuildSnapshot(
        CaseOpeningJournal journal,
        RelayStake stake,
        IRelayRewardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(stake);
        ArgumentNullException.ThrowIfNull(catalog);
        var consumer = journal.FindRelay(stake.RootId);
        var status = CreateStatus(journal, stake);
        var existing = consumer;
        var receipt = existing is { Status: RelayRecordStatus.Committed }
            ? CreateReceipt(existing, catalog, replay: true)
            : journal.IsLegacySecured(stake.RootId)
                ? new RelayReceipt
                {
                    Action = RelayRecordAction.Secure.ToString(),
                    Outcome = RelayOutcome.Secured.ToString(),
                    StakeRootId = stake.RootId.ToString(),
                    Stage = stake.Stage,
                    PreviousRewardId = stake.RewardId,
                    PreviousRarity = stake.Rarity.ToString(),
                    RarityLadderVersion = stake.RarityLadderVersion.ToString(),
                    RecoveryMeter = journal.RecoveryMeter,
                    Terminal = true,
                    Replay = true
                }
                : null;
        var upgradeCandidates = Array.Empty<RelayCandidate>();
        var sidegradeCandidates = Array.Empty<RelayCandidate>();
        if (status.RelayEligible)
        {
            upgradeCandidates = catalog.Rewards
                .Where(reward => reward.Rarity == RelayRules.GetUpgradeRarity(
                    stake.Rarity,
                    stake.RarityLadderVersion))
                .OrderBy(reward => reward.Id, StringComparer.Ordinal)
                .Select(ToCandidate)
                .ToArray();
            sidegradeCandidates = catalog.Rewards
                .Where(reward => reward.Rarity == stake.Rarity &&
                    !string.Equals(reward.Id, stake.RewardId, StringComparison.Ordinal))
                .OrderBy(reward => reward.Id, StringComparer.Ordinal)
                .Select(ToCandidate)
                .ToArray();
        }

        return new RelaySnapshot
        {
            Status = status,
            LatestReceipt = receipt,
            PublishedLadder = Enumerable.Range(1, RelayRules.MaximumStage)
                .Select(stage =>
                {
                    var odds = RelayRules.GetOdds(stage);
                    return new RelayPublishedStage
                    {
                        Stage = stage,
                        UpgradePercent = odds.UpgradePercent,
                        SidegradePercent = odds.SidegradePercent,
                        ConfiscatePercent = odds.ConfiscatePercent
                    };
                })
                .ToArray(),
            UpgradeCandidates = upgradeCandidates,
            SidegradeCandidates = sidegradeCandidates
        };
    }

    internal static RelayStatus CreateStatus(CaseOpeningJournal journal, RelayStake stake)
    {
        var consumer = journal.FindRelay(stake.RootId);
        var profileTransactionPending = journal.PreparedRelay is not null;
        return new RelayStatus
        {
            StakeRootId = stake.RootId.ToString(),
            RewardId = stake.RewardId,
            Rarity = stake.Rarity.ToString(),
            RarityLadderVersion = stake.RarityLadderVersion.ToString(),
            Stage = stake.Stage,
            RecoveryMeter = journal.RecoveryMeter,
            RelayEligible = !profileTransactionPending &&
                !stake.Terminal &&
                RelayRules.CanRelay(stake.Rarity, stake.Stage, stake.RarityLadderVersion),
            GuaranteedUpgradeReady = journal.RecoveryMeter == RelayRules.MaximumRecoveryMeter,
            Terminal = stake.Terminal,
            TerminalOutcome = stake.TerminalOutcome?.ToString(),
            SettlementPending = consumer?.Status == RelayRecordStatus.Prepared,
            PendingAction = consumer?.Status == RelayRecordStatus.Prepared ? consumer.Action.ToString() : null
        };
    }

    internal static RelayReceipt CreateReceipt(
        RelaySettlementRecord record,
        IRelayRewardCatalog catalog,
        bool replay)
    {
        var output = record.OutputRewardId is null ? null : catalog.FindReward(record.OutputRewardId);
        return new RelayReceipt
        {
            DeliveredToMessenger = record.MailDelivery is not null,
            Action = record.Action.ToString(),
            Outcome = record.Outcome.ToString(),
            StakeRootId = record.StakeRootId.ToString(),
            Stage = record.Stage,
            PreviousRewardId = record.InputRewardId,
            PreviousRarity = record.InputRarity.ToString(),
            RewardId = output?.Id,
            RewardRootId = record.OutputRootId?.ToString(),
            Rarity = output?.Rarity.ToString(),
            RarityLadderVersion = record.RarityLadderVersion.ToString(),
            RecoveryMeter = record.MeterAfter,
            GuaranteedUpgrade = record.GuaranteedUpgrade,
            Terminal = record.Outcome != RelayOutcome.RarityUpgrade ||
                record.Stage >= RelayRules.MaximumStage ||
                output?.Rarity == RewardRarity.BlackLabel,
            Replay = replay
        };
    }

    private static RelayCandidate ToCandidate(ValidatedReward reward) => new()
    {
        RewardId = reward.Id,
        DisplayName = reward.DisplayName,
        Rarity = reward.Rarity.ToString(),
        HandbookValue = reward.HandbookValue
    };

    private static bool IsMongoId(string? value) =>
        value is { Length: 24 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

public sealed class RelaySnapshotRequest : IRequestData
{
    [JsonPropertyName("stakeRootId")]
    public string StakeRootId { get; set; } = string.Empty;
}

public sealed class RelayPendingRequest : IRequestData;

internal sealed class RelayPendingResponseData
{
    [JsonPropertyName("pendingSnapshot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public RelaySnapshot? PendingSnapshot { get; init; }
}
