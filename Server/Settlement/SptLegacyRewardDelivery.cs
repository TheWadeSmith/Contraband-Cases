using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace ContrabandCases.Server.Settlement;

/// <summary>Recovery adapter for pre-Manifest physical prizes. Paid inventory stays put.</summary>
[Injectable(InjectionType.Singleton)]
public sealed class SptLegacyRewardDelivery : IOpeningInventory, IRelayInventory
{
    private readonly IOpeningInventory _opening;
    private readonly IRelayInventory _relay;
    private readonly SptManifestRewardDelivery _mail;
    private readonly Action<OpeningContext, CaseOpeningRecord> _consumeOpening;
    private readonly Action<OpeningContext, RelaySettlementRecord> _consumeRelay;

    public SptLegacyRewardDelivery(SptOpeningInventory inventory, SptManifestRewardDelivery mail)
        : this(inventory, inventory, mail, inventory.ConsumePreparedOpening, inventory.ConsumePreparedRelay) { }

    internal SptLegacyRewardDelivery(IOpeningInventory opening, IRelayInventory relay, SptManifestRewardDelivery mail,
        Action<OpeningContext, CaseOpeningRecord> consumeOpening, Action<OpeningContext, RelaySettlementRecord> consumeRelay)
    { _opening = opening; _relay = relay; _mail = mail; _consumeOpening = consumeOpening; _consumeRelay = consumeRelay; }

    public CaseOpeningRecord PrepareDelivery(OpeningContext context, CaseOpeningRecord record)
    {
        if (record.MailDelivery is not null || record.Status == OpeningRecordStatus.Committed) return record;
        if (_opening.Inspect(context, record.CaseId, record) != new InventoryEvidence(true, true, RewardPresence.Absent))
            throw new InvalidOperationException("Only an unpaid legacy opening can change its delivery destination.");
        return record.WithMailDelivery(Plan(context, record.RewardItems, record.PreparedAtUtc));
    }

    public RelaySettlementRecord PrepareRelayDelivery(OpeningContext context, RelaySettlementRecord record)
    {
        if (record.MailDelivery is not null || record.Status == RelayRecordStatus.Committed || record.OutputItems.Count == 0) return record;
        if (_relay.InspectRelay(context, record) != new RelayInventoryEvidence(RewardPresence.Complete, true, RewardPresence.Absent))
            throw new InvalidOperationException("Only an unpaid legacy Relay can change its delivery destination.");
        return record.WithMailDelivery(Plan(context, record.OutputItems, record.PreparedAtUtc));
    }

    private ManifestClaimPreparedPayload Plan(OpeningContext context, IReadOnlyList<Item> items, DateTimeOffset preparedAt)
    {
        var root = SettlementItemTrees.FindRootId(items) ?? throw new InvalidOperationException("Legacy reward tree has no unique root.");
        if (!_mail.TryPrepareClaim(context, items, [root], preparedAt, out var prepared) || prepared is null)
            throw new InvalidOperationException("Legacy reward mail could not be prepared.");
        return ManifestClaimCommitWitness.PlanNext(context.PmcData, context.ProfileId, prepared);
    }

    public InventoryEvidence Inspect(OpeningContext context, MongoId caseId, CaseOpeningRecord? record)
    {
        var evidence = _opening.Inspect(context, caseId, record);
        if (record?.MailDelivery is not { } mail) return evidence;
        return evidence with { RewardPresence = Presence(context, LegacyRewardMail.OpeningId(record), record.RewardId,
            mail, !evidence.CasePresent && !evidence.KeyPresent) };
    }

    public RelayInventoryEvidence InspectRelay(OpeningContext context, RelaySettlementRecord record)
    {
        var evidence = _relay.InspectRelay(context, record);
        if (record.MailDelivery is not { } mail) return evidence;
        return evidence with { OutputPresence = Presence(context, LegacyRewardMail.RelayId(record), record.OutputRewardId!,
            mail, evidence.InputPresence == RewardPresence.Absent && !evidence.KeyPresent) };
    }

    private RewardPresence Presence(OpeningContext context, string id, string rewardId,
        ManifestClaimPreparedPayload mail, bool inputsAbsent)
    {
        var presence = _mail.InspectClaim(context, mail); // Also authenticates the owning profile.
        var witness = ManifestClaimCommitWitness.Inspect(context.PmcData, context.ProfileId,
            id, LegacyRewardMail.Identity(rewardId), mail);
        if (witness == ManifestClaimCommitWitnessInspection.Current)
            return inputsAbsent && mail.ProfileCommitStarted ? RewardPresence.Complete : RewardPresence.Partial;
        return witness == ManifestClaimCommitWitnessInspection.Predecessor && presence == RewardPresence.Absent
            ? RewardPresence.Absent : RewardPresence.Partial;
    }

    public InventoryCheckpoint Capture(OpeningContext context) => _mail.Capture(context);
    public InventoryCheckpoint CaptureRelay(OpeningContext context) => Capture(context);
    public void Restore(OpeningContext context, InventoryCheckpoint checkpoint) => _mail.Restore(context, checkpoint);
    public void RestoreRelay(OpeningContext context, InventoryCheckpoint checkpoint) => Restore(context, checkpoint);

    public CaseOpeningRecord ApplyPrepared(OpeningContext context, CaseOpeningRecord record)
    {
        var mail = record.MailDelivery ?? throw new InvalidOperationException("Legacy reward must have a persisted mail plan before consumption.");
        if (Inspect(context, record.CaseId, record) != new InventoryEvidence(true, true, RewardPresence.Absent))
            throw new InvalidOperationException("Legacy mail does not match its saved inputs and commit witness.");
        _consumeOpening(context, record);
        return record.WithMailDelivery(_mail.ApplyPreparedClaim(context, LegacyRewardMail.Retry(mail)));
    }

    public RelaySettlementRecord ApplyPreparedRelay(OpeningContext context, RelaySettlementRecord record)
    {
        if (record.OutputItems.Count == 0) return _relay.ApplyPreparedRelay(context, record);
        var mail = record.MailDelivery ?? throw new InvalidOperationException("Legacy Relay must have a persisted mail plan before consumption.");
        if (InspectRelay(context, record) != new RelayInventoryEvidence(RewardPresence.Complete, true, RewardPresence.Absent))
            throw new InvalidOperationException("Legacy Relay mail does not match its saved inputs and commit witness.");
        _consumeRelay(context, record);
        var applied = _mail.ApplyPreparedClaim(context, LegacyRewardMail.Retry(mail));
        return record.WithMailDelivery(applied).BeginProfileCommit();
    }

    public void StageDeliveryCommit(OpeningContext context, CaseOpeningRecord record) =>
        Stage(context, LegacyRewardMail.OpeningId(record), record.RewardId, record.MailDelivery);
    public void StageRelayDeliveryCommit(OpeningContext context, RelaySettlementRecord record) =>
        Stage(context, LegacyRewardMail.RelayId(record), record.OutputRewardId!, record.MailDelivery);
    private static void Stage(OpeningContext context, string id, string rewardId, ManifestClaimPreparedPayload? mail)
    {
        if (mail is not null) ManifestClaimCommitWitness.Stage(context.PmcData, context.ProfileId,
            id, LegacyRewardMail.Identity(rewardId), mail);
    }

    public Task NotifyDeliveryAsync(OpeningContext context, CaseOpeningRecord record) => record.MailDelivery is { } mail
        ? _mail.NotifyClaimAsync(context, mail) : Task.CompletedTask;
    public Task NotifyRelayDeliveryAsync(OpeningContext context, RelaySettlementRecord record) => record.MailDelivery is { } mail
        ? _mail.NotifyClaimAsync(context, mail) : Task.CompletedTask;

    public void Replay(OpeningContext context, CaseOpeningRecord record)
    {
        if (record.MailDelivery is null) _opening.Replay(context, record);
        else ReplayConsumed(context, [record.CaseId, record.KeyId]);
    }
    public void ReplayRelay(OpeningContext context, RelaySettlementRecord record)
    {
        if (record.MailDelivery is null) _relay.ReplayRelay(context, record);
        else ReplayConsumed(context, record.InputItemIds.Append(record.KeyId!.Value));
    }
    private static void ReplayConsumed(OpeningContext context, IEnumerable<MongoId> consumed)
    {
        if (context.Response.Warnings?.Count > 0) throw new InvalidOperationException("Cannot replay a failed settlement.");
        var changes = SptResponseChanges.GetOrCreate(context.Response, context.ProfileId);
        foreach (var id in consumed)
        {
            if ((changes.NewItems ?? []).Concat(changes.ChangedItems ?? []).Any(i => i.Id == id))
                throw new InvalidOperationException("Legacy delivery replay collides with existing response changes.");
            if (!changes.DeletedItems!.Any(i => i.Id == id)) changes.DeletedItems.Add(new DeletedItem { Id = id });
        }
    }
}
