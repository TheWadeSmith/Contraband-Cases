using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace ContrabandCases.Server.Settlement;

public sealed class OpeningContext
{
    public OpeningContext(PmcData pmcData, ItemEventRouterResponse response, MongoId profileId, string? sessionId = null)
    {
        PmcData = pmcData;
        Response = response;
        ProfileId = profileId;
        SessionId = sessionId;
    }

    public PmcData PmcData { get; }
    public ItemEventRouterResponse Response { get; }
    public MongoId ProfileId { get; }
    public string? SessionId { get; }
}

public abstract record InventoryCheckpoint;

public enum ManifestInventoryPresence
{
    Present,
    Absent,
    Partial
}

public enum ManifestCommitWitnessState
{
    Predecessor,
    Current,
    Other
}

public sealed class ManifestRelayKeyPreparation
{
    public ManifestRelayKeyPreparation(
        MongoId keyId,
        DateTimeOffset preparedAtUtc,
        long commitGeneration,
        string commitPredecessorHash)
    {
        if (keyId.IsEmpty)
        {
            throw new ArgumentException("A Relay key ID is required.", nameof(keyId));
        }
        ManifestRecordValidation.RequireUtc(preparedAtUtc, nameof(preparedAtUtc));
        ManifestInputCommitWitness.ValidateGeneration(commitGeneration, nameof(commitGeneration));
        ManifestInputCommitWitness.ValidateHash(commitPredecessorHash, nameof(commitPredecessorHash));

        KeyId = keyId;
        PreparedAtUtc = preparedAtUtc;
        CommitGeneration = commitGeneration;
        CommitPredecessorHash = commitPredecessorHash;
    }

    public MongoId KeyId { get; }
    public DateTimeOffset PreparedAtUtc { get; }
    public long CommitGeneration { get; }
    public string CommitPredecessorHash { get; }
}

public interface ICaseOpeningJournalStore
{
    ValueTask<CaseOpeningJournal> LoadAsync(MongoId profileId, CancellationToken cancellationToken);
    ValueTask SaveAsync(MongoId profileId, CaseOpeningJournal journal, CancellationToken cancellationToken);
}

public interface IOpeningPreparation
{
    ValueTask<CaseOpeningRecord> PrepareAsync(
        OpeningContext context,
        MongoId caseId,
        CancellationToken cancellationToken);
}

public interface IOpeningInventory
{
    CaseOpeningRecord PrepareDelivery(OpeningContext context, CaseOpeningRecord record) => record;
    void StageDeliveryCommit(OpeningContext context, CaseOpeningRecord record) { }
    Task NotifyDeliveryAsync(OpeningContext context, CaseOpeningRecord record) => Task.CompletedTask;
    InventoryEvidence Inspect(OpeningContext context, MongoId caseId, CaseOpeningRecord? record);
    InventoryCheckpoint Capture(OpeningContext context);
    CaseOpeningRecord ApplyPrepared(OpeningContext context, CaseOpeningRecord record);
    void Restore(OpeningContext context, InventoryCheckpoint checkpoint);
    void Replay(OpeningContext context, CaseOpeningRecord record);
}

public interface IManifestClaimInventory
{
    ManifestClaimPreparedPayload PrepareDelivery(ManifestClaimPreparedPayload prepared) => prepared;
    Task NotifyClaimAsync(OpeningContext context, ManifestClaimPreparedPayload prepared) => Task.CompletedTask;

    bool TryPrepareClaim(
        OpeningContext context,
        IReadOnlyList<Item> materializedItems,
        IReadOnlyList<MongoId> rootIds,
        DateTimeOffset preparedAtUtc,
        out ManifestClaimPreparedPayload? prepared);

    RewardPresence InspectClaim(OpeningContext context, ManifestClaimPreparedPayload prepared);
    ManifestClaimPreparedPayload ReconcileAppliedClaim(
        OpeningContext context,
        ManifestClaimPreparedPayload prepared);
    InventoryCheckpoint Capture(OpeningContext context);
    ManifestClaimPreparedPayload ApplyPreparedClaim(
        OpeningContext context,
        ManifestClaimPreparedPayload prepared);
    void Restore(OpeningContext context, InventoryCheckpoint checkpoint);
    void ReplayClaim(OpeningContext context, ManifestClaimPreparedPayload prepared);
}

public interface IManifestTicketInventory
{
    ManifestTicketPayload PrepareTicket(
        OpeningContext context,
        MongoId caseId,
        DateTimeOffset preparedAtUtc);

    ManifestInventoryPresence InspectTicket(
        OpeningContext context,
        ManifestTicketPayload prepared);

    ManifestCommitWitnessState InspectTicketCommit(
        OpeningContext context,
        string manifestId,
        ManifestTicketPayload prepared);

    InventoryCheckpoint CaptureTicket(OpeningContext context);
    ManifestTicketPayload ApplyPreparedTicket(
        OpeningContext context,
        ManifestTicketPayload prepared);
    void StageTicketCommit(
        OpeningContext context,
        string manifestId,
        ManifestTicketPayload prepared);
    void RestoreTicket(OpeningContext context, InventoryCheckpoint checkpoint);
    void ReplayTicket(OpeningContext context, ManifestTicketPayload prepared);
}

public interface IManifestRelayKeyInventory
{
    ManifestRelayKeyPreparation PrepareRelayKey(
        OpeningContext context,
        IReadOnlySet<MongoId> excludedIds,
        DateTimeOffset preparedAtUtc);

    ManifestInventoryPresence InspectRelayKey(
        OpeningContext context,
        ManifestRelayPreparedPayload prepared);

    ManifestCommitWitnessState InspectRelayKeyCommit(
        OpeningContext context,
        string manifestId,
        RewardForestFingerprintV2 inputFingerprint,
        ManifestRelayPreparedPayload prepared);

    InventoryCheckpoint CaptureRelayKey(OpeningContext context);
    ManifestRelayPreparedPayload ApplyPreparedRelayKey(
        OpeningContext context,
        ManifestRelayPreparedPayload prepared);
    void StageRelayKeyCommit(
        OpeningContext context,
        string manifestId,
        RewardForestFingerprintV2 inputFingerprint,
        ManifestRelayPreparedPayload prepared);
    void RestoreRelayKey(OpeningContext context, InventoryCheckpoint checkpoint);
    void ReplayRelayKey(OpeningContext context, ManifestRelayPreparedPayload prepared);
}

public interface IProfileCommitter
{
    // Exclude native background serialization while staging or rolling back a
    // profile mutation. Release before CommitAsync, which takes the same lock.
    // Pure in-memory committers do not have a concurrent native save path.
    ValueTask<IDisposable?> AcquireMutationLeaseAsync(MongoId profileId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IDisposable?>(null);

    Task CommitAsync(MongoId profileId, CancellationToken cancellationToken);
}

public interface IRelayPreparation
{
    ValueTask<RelaySettlementRecord> PrepareSecureAsync(
        OpeningContext context,
        RelayStake stake,
        int recoveryMeter,
        CancellationToken cancellationToken);

    ValueTask<RelaySettlementRecord> PrepareRelayAsync(
        OpeningContext context,
        RelayStake stake,
        int recoveryMeter,
        CancellationToken cancellationToken);
}

public interface IRelayInventory
{
    RelaySettlementRecord PrepareRelayDelivery(OpeningContext context, RelaySettlementRecord record) => record;
    void StageRelayDeliveryCommit(OpeningContext context, RelaySettlementRecord record) { }
    Task NotifyRelayDeliveryAsync(OpeningContext context, RelaySettlementRecord record) => Task.CompletedTask;
    RelayInventoryEvidence InspectRelay(OpeningContext context, RelaySettlementRecord record);
    InventoryCheckpoint CaptureRelay(OpeningContext context);
    RelaySettlementRecord ApplyPreparedRelay(OpeningContext context, RelaySettlementRecord record);
    void RestoreRelay(OpeningContext context, InventoryCheckpoint checkpoint);
    void ReplayRelay(OpeningContext context, RelaySettlementRecord record);
}
