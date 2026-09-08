using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace ContrabandCases.Server.Settlement;

public sealed class RelaySettlementService
{
    private readonly ICaseOpeningJournalStore _journalStore;
    private readonly IRelayPreparation _preparation;
    private readonly IRelayInventory _inventory;
    private readonly IProfileCommitter _committer;
    private readonly ProfileLockPool _lockPool;
    private readonly RaidSessionState _raidSessions;
    private readonly IRelayRewardCatalog _catalog;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ManifestClaimCommitUncertaintyCoordinator _uncertainty;

    public RelaySettlementService(
        ICaseOpeningJournalStore journalStore,
        IRelayPreparation preparation,
        IRelayInventory inventory,
        IProfileCommitter committer,
        ProfileLockPool lockPool,
        RaidSessionState raidSessions,
        IRelayRewardCatalog catalog,
        Func<DateTimeOffset>? utcNow = null,
        ManifestClaimCommitUncertaintyCoordinator? uncertaintyCoordinator = null)
    {
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        _preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        _lockPool = lockPool ?? throw new ArgumentNullException(nameof(lockPool));
        _raidSessions = raidSessions ?? throw new ArgumentNullException(nameof(raidSessions));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _uncertainty = uncertaintyCoordinator ?? ManifestClaimCommitUncertaintyCoordinator.Process;
    }

    public async Task<ItemEventRouterResponse> SecureAsync(
        OpeningContext context,
        MongoId stakeRootId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var profileLock = await _lockPool.AcquireAsync(context.ProfileId, cancellationToken).ConfigureAwait(false);
        _uncertainty.ThrowIfUncertain(context.ProfileId);
        _raidSessions.RequireLobby(context.ProfileId);
        var journal = await _journalStore.LoadAsync(context.ProfileId, cancellationToken).ConfigureAwait(false);
        var existing = journal.FindRelay(stakeRootId);
        if (existing is { Status: RelayRecordStatus.Committed })
        {
            if (existing.Action != RelayRecordAction.Secure)
            {
                throw new InvalidOperationException("The requested stake is a stale or already-settled chain ancestor.");
            }

            return await CompleteSecureAsync(context, journal, existing, replay: true).ConfigureAwait(false);
        }
        RejectDifferentPreparedTransaction(journal, stakeRootId, RelayRecordAction.Secure);
        if (existing is not null)
        {
            if (existing.Action != RelayRecordAction.Secure)
            {
                throw new InvalidOperationException("The requested stake is a stale or already-settled chain ancestor.");
            }

            return await CompleteSecureAsync(context, journal, existing, replay: true).ConfigureAwait(false);
        }

        var stake = RelayChainResolver.Resolve(journal, stakeRootId, _catalog.FindReward);
        if (journal.IsLegacySecured(stakeRootId))
        {
            AttachReceipt(context.Response, new RelayReceipt
            {
                Action = RelayRecordAction.Secure.ToString(),
                Outcome = RelayOutcome.Secured.ToString(),
                StakeRootId = stakeRootId.ToString(),
                Stage = stake.Stage,
                PreviousRewardId = stake.RewardId,
                PreviousRarity = stake.Rarity.ToString(),
                RarityLadderVersion = stake.RarityLadderVersion.ToString(),
                RecoveryMeter = journal.RecoveryMeter,
                Terminal = true,
                Replay = true
            });
            return context.Response;
        }
        var prepared = await _preparation
            .PrepareSecureAsync(context, stake, journal.RecoveryMeter, cancellationToken)
            .ConfigureAwait(false);
        ValidatePrepared(context, prepared, stake, journal.RecoveryMeter, RelayRecordAction.Secure);
        journal.AddRelay(prepared);
        await _journalStore.SaveAsync(context.ProfileId, journal, cancellationToken).ConfigureAwait(false);
        return await CompleteSecureAsync(context, journal, prepared, replay: false).ConfigureAwait(false);
    }

    public async Task<ItemEventRouterResponse> RelayAsync(
        OpeningContext context,
        MongoId stakeRootId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var profileLock = await _lockPool.AcquireAsync(context.ProfileId, cancellationToken).ConfigureAwait(false);
        _uncertainty.ThrowIfUncertain(context.ProfileId);
        _raidSessions.RequireLobby(context.ProfileId);
        var journal = await _journalStore.LoadAsync(context.ProfileId, cancellationToken).ConfigureAwait(false);
        var record = journal.FindRelay(stakeRootId);
        if (record is { Status: RelayRecordStatus.Committed })
        {
            if (record.Action != RelayRecordAction.Relay)
            {
                throw new InvalidOperationException("The requested stake is a stale or already-secured chain ancestor.");
            }

            return Replay(context, record);
        }

        RejectDifferentPreparedTransaction(journal, stakeRootId, RelayRecordAction.Relay);
        if (record is null)
        {
            var stake = RelayChainResolver.Resolve(journal, stakeRootId, _catalog.FindReward);
            if (stake.Terminal || !RelayRules.CanRelay(
                    stake.Rarity,
                    stake.Stage,
                    stake.RarityLadderVersion))
            {
                throw new InvalidOperationException("The requested reward is terminal and cannot be relayed.");
            }

            record = await _preparation
                .PrepareRelayAsync(context, stake, journal.RecoveryMeter, cancellationToken)
                .ConfigureAwait(false);
            ValidatePrepared(context, record, stake, journal.RecoveryMeter, RelayRecordAction.Relay);
            journal.AddRelay(record);
            await _journalStore.SaveAsync(context.ProfileId, journal, cancellationToken).ConfigureAwait(false);
        }
        else if (record.Action != RelayRecordAction.Relay)
        {
            throw new InvalidOperationException("The requested stake is a stale or already-secured chain ancestor.");
        }

        RequirePreparedMeter(journal, record);
        return RelayStateMachine.Decide(record, _inventory.InspectRelay(context, record)) switch
        {
            RelayDecision.ApplyPrepared => await ApplyAndCommitAsync(context, journal, record, cancellationToken).ConfigureAwait(false),
            RelayDecision.RecoverCommitted => await RecoverAndCommitAsync(context, journal, record).ConfigureAwait(false),
            RelayDecision.ReplayCommitted => Replay(context, record),
            _ => throw new InvalidOperationException("Relay inventory state contradicts the persisted settlement record.")
        };
    }

    private async Task<ItemEventRouterResponse> CompleteSecureAsync(
        OpeningContext context,
        CaseOpeningJournal journal,
        RelaySettlementRecord record,
        bool replay)
    {
        if (record.Status == RelayRecordStatus.Prepared)
        {
            RequirePreparedMeter(journal, record);
            record = record.Commit(UtcNow());
            journal.ReplaceRelay(record);
            journal.ApplyCommittedMeter(record);
            journal.PruneCommitted();
            await _journalStore.SaveAsync(context.ProfileId, journal, CancellationToken.None).ConfigureAwait(false);
            replay = false;
        }

        AttachReceipt(context.Response, CreateReceipt(record, replay));
        return context.Response;
    }

    private async Task<ItemEventRouterResponse> ApplyAndCommitAsync(
        OpeningContext context,
        CaseOpeningJournal journal,
        RelaySettlementRecord prepared,
        CancellationToken cancellationToken)
    {
        RequirePreparedMeter(journal, prepared);
        using var saveLease = await _committer
            .AcquireMutationLeaseAsync(context.ProfileId, cancellationToken).ConfigureAwait(false);
        var checkpoint = _inventory.CaptureRelay(context);
        var commitStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deliveryRecord = _inventory.PrepareRelayDelivery(context, prepared);
            if (!ReferenceEquals(prepared, deliveryRecord))
            {
                journal.ReplaceRelay(deliveryRecord);
                await _journalStore.SaveAsync(context.ProfileId, journal, cancellationToken).ConfigureAwait(false);
                prepared = deliveryRecord;
            }
            var applied = _inventory.ApplyPreparedRelay(context, prepared);
            ValidateApplied(context, prepared, applied);
            journal.ReplaceRelay(applied);
            await _journalStore.SaveAsync(context.ProfileId, journal, CancellationToken.None).ConfigureAwait(false);

            _inventory.StageRelayDeliveryCommit(context, applied);
            commitStarted = true;
            saveLease?.Dispose();
            await CommitProfileAsync(context.ProfileId).ConfigureAwait(false);
            var committed = applied.Commit(UtcNow());
            journal.ReplaceRelay(committed);
            journal.ApplyCommittedMeter(committed);
            journal.PruneCommitted();
            await _journalStore.SaveAsync(context.ProfileId, journal, CancellationToken.None).ConfigureAwait(false);
            AttachReceipt(context.Response, CreateReceipt(committed, replay: false));
            await _inventory.NotifyRelayDeliveryAsync(context, committed).ConfigureAwait(false);
            return context.Response;
        }
        catch when (!commitStarted)
        {
            _inventory.RestoreRelay(context, checkpoint);
            throw;
        }
    }

    private async Task<ItemEventRouterResponse> RecoverAndCommitAsync(
        OpeningContext context,
        CaseOpeningJournal journal,
        RelaySettlementRecord record)
    {
        RequirePreparedMeter(journal, record);
        await CommitProfileAsync(context.ProfileId).ConfigureAwait(false);
        var committed = record.Commit(UtcNow());
        journal.ReplaceRelay(committed);
        journal.ApplyCommittedMeter(committed);
        journal.PruneCommitted();
        await _journalStore.SaveAsync(context.ProfileId, journal, CancellationToken.None).ConfigureAwait(false);
        _inventory.ReplayRelay(context, committed);
        AttachReceipt(context.Response, CreateReceipt(committed, replay: true));
        return context.Response;
    }

    private ItemEventRouterResponse Replay(OpeningContext context, RelaySettlementRecord record)
    {
        _inventory.ReplayRelay(context, record);
        AttachReceipt(context.Response, CreateReceipt(record, replay: true));
        return context.Response;
    }

    private static void RejectDifferentPreparedTransaction(
        CaseOpeningJournal journal,
        MongoId stakeRootId,
        RelayRecordAction action)
    {
        if (journal.PreparedOpening is not null)
        {
            throw new InvalidOperationException(
                "The profile already has a different prepared settlement transaction that must be resumed first.");
        }

        var pending = journal.PreparedRelay;
        if (pending is not null && (pending.StakeRootId != stakeRootId || pending.Action != action))
        {
            throw new InvalidOperationException(
                "The profile already has a different prepared Relay transaction that must be resumed first.");
        }
    }

    private static void RequirePreparedMeter(CaseOpeningJournal journal, RelaySettlementRecord record)
    {
        if (record.Status != RelayRecordStatus.Prepared || journal.RecoveryMeter != record.MeterBefore)
        {
            throw new InvalidOperationException("Relay recovery meter no longer matches the prepared transaction.");
        }
    }

    private void ValidatePrepared(
        OpeningContext context,
        RelaySettlementRecord record,
        RelayStake stake,
        int recoveryMeter,
        RelayRecordAction action)
    {
        if (record is null || record.Status != RelayRecordStatus.Prepared || record.ProfileCommitStarted ||
            record.Action != action || record.OriginCaseId != stake.OriginCaseId || record.StakeRootId != stake.RootId ||
            !string.Equals(record.InputRewardId, stake.RewardId, StringComparison.Ordinal) ||
            record.InputRarity != stake.Rarity ||
            record.RarityLadderVersion != stake.RarityLadderVersion ||
            record.Stage != stake.Stage || record.MeterBefore != recoveryMeter)
        {
            throw new InvalidOperationException("Relay preparation changed the authoritative chain identity.");
        }

        var input = _catalog.FindReward(record.InputRewardId);
        if (input.Rarity != record.InputRarity)
        {
            throw new InvalidOperationException("Relay input rarity does not match the server catalog.");
        }
        if (action != RelayRecordAction.Relay)
        {
            return;
        }

        var expectedInputIds = stake.ExpectedItems.Select(item => item.Id).ToHashSet();
        if (record.InputItems.Count == 0 ||
            !expectedInputIds.SetEquals(record.InputItemIds))
        {
            throw new InvalidOperationException("Relay preparation changed the authoritative stake item set.");
        }
        try
        {
            SptOpeningInventory.EnsureExactStakeTree(
                stake.ExpectedItems,
                record.InputItems,
                stake.RootId,
                SptOpeningInventory.RequireRelayRootParentIds(context.PmcData));
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                "Relay preparation changed the authoritative stake item state.",
                exception);
        }

        ValidatedReward? output = record.OutputRewardId is null ? null : _catalog.FindReward(record.OutputRewardId);
        var validTarget = record.Outcome switch
        {
            RelayOutcome.RarityUpgrade => output?.Rarity == RelayRules.GetUpgradeRarity(
                input.Rarity,
                record.RarityLadderVersion),
            RelayOutcome.SameRaritySidegrade => output?.Rarity == input.Rarity &&
                !string.Equals(output.Id, input.Id, StringComparison.Ordinal),
            RelayOutcome.Confiscated => output is null,
            _ => false
        };
        if (!validTarget)
        {
            throw new InvalidOperationException("Relay preparation selected a reward outside the authoritative rarity target pool.");
        }
    }

    private static void ValidateApplied(
        OpeningContext context,
        RelaySettlementRecord prepared,
        RelaySettlementRecord applied)
    {
        if (applied is not null)
            LegacyRewardMail.ValidateReplacement(prepared.MailDelivery, applied.MailDelivery, false);
        if (prepared.MailDelivery is not null && applied?.MailDelivery?.ProfileCommitStarted != true)
            throw new InvalidOperationException("Applied Relay mail must contain its profile-commit marker.");
        if (applied is null || applied.Status != RelayRecordStatus.Prepared || !applied.ProfileCommitStarted ||
            applied.OriginCaseId != prepared.OriginCaseId || applied.StakeRootId != prepared.StakeRootId ||
            applied.Action != prepared.Action || applied.KeyId != prepared.KeyId || applied.Outcome != prepared.Outcome ||
            !string.Equals(applied.InputRewardId, prepared.InputRewardId, StringComparison.Ordinal) ||
            !string.Equals(applied.OutputRewardId, prepared.OutputRewardId, StringComparison.Ordinal) ||
            applied.Stage != prepared.Stage || applied.MeterBefore != prepared.MeterBefore ||
            applied.MeterAfter != prepared.MeterAfter || applied.GuaranteedUpgrade != prepared.GuaranteedUpgrade ||
            !applied.InputItemIds.SequenceEqual(prepared.InputItemIds))
        {
            throw new InvalidOperationException("Live Relay settlement changed the prepared transaction identity.");
        }

        try
        {
            SptOpeningInventory.EnsureExactItemState(
                prepared.InputItems,
                applied.InputItems,
                "applied Relay input snapshot");
            if (prepared.MailDelivery is not null)
            {
                SptOpeningInventory.EnsureExactItemState(prepared.OutputItems, applied.OutputItems, "mailed Relay output snapshot");
                return;
            }
            if (prepared.OutputItems.Count == 0 && applied.OutputItems.Count == 0)
            {
                return;
            }

            var outputRoot = prepared.OutputRootId
                ?? throw new InvalidOperationException("Prepared Relay output has no root item.");
            SptOpeningInventory.EnsureExactStakeTree(
                prepared.OutputItems,
                applied.OutputItems,
                outputRoot,
                SptOpeningInventory.RequireRelayRootParentIds(context.PmcData));
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                "Live Relay settlement changed the prepared transaction identity.",
                exception);
        }
    }

    private RelayReceipt CreateReceipt(RelaySettlementRecord record, bool replay)
    {
        return RelaySnapshotRouter.CreateReceipt(record, _catalog, replay);
    }

    private static void AttachReceipt(ItemEventRouterResponse response, RelayReceipt receipt)
    {
        response.ExtensionData ??= new Dictionary<string, object>();
        if (!response.ExtensionData.TryAdd(ModConstants.RelayReceiptExtensionKey, receipt))
        {
            throw new InvalidOperationException("Relay receipt would collide with another response extension.");
        }
    }

    private DateTimeOffset UtcNow()
    {
        var value = _utcNow();
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Relay settlement timestamps must be UTC.");
        }

        return value;
    }

    private async Task CommitProfileAsync(MongoId profileId)
    {
        try { await _committer.CommitAsync(profileId, CancellationToken.None).ConfigureAwait(false); }
        catch { _uncertainty.MarkUncertain(profileId); throw; }
    }
}
