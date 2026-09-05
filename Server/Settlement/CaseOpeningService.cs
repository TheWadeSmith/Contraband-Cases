using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace ContrabandCases.Server.Settlement;

public sealed class CaseOpeningService
{
    private readonly ICaseOpeningJournalStore _journalStore;
    private readonly IOpeningPreparation _preparation;
    private readonly IOpeningInventory _inventory;
    private readonly IProfileCommitter _committer;
    private readonly ProfileLockPool _lockPool;
    private readonly RaidSessionState _raidSessions;
    private readonly Func<DateTimeOffset> _utcNow;

    public CaseOpeningService(
        ICaseOpeningJournalStore journalStore,
        IOpeningPreparation preparation,
        IOpeningInventory inventory,
        IProfileCommitter committer,
        ProfileLockPool lockPool,
        RaidSessionState raidSessions,
        Func<DateTimeOffset>? utcNow = null)
    {
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        _preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        _lockPool = lockPool ?? throw new ArgumentNullException(nameof(lockPool));
        _raidSessions = raidSessions ?? throw new ArgumentNullException(nameof(raidSessions));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ItemEventRouterResponse> OpenAsync(
        OpeningContext context,
        MongoId caseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var profileLock = await _lockPool.AcquireAsync(context.ProfileId, cancellationToken).ConfigureAwait(false);
        _raidSessions.RequireLobby(context.ProfileId);
        var journal = await _journalStore.LoadAsync(context.ProfileId, cancellationToken).ConfigureAwait(false);
        var record = journal.Find(caseId);
        if (record is not { Status: OpeningRecordStatus.Committed })
        {
            RejectDifferentPreparedTransaction(journal, caseId);
        }

        var decision = OpeningStateMachine.Decide(record, _inventory.Inspect(context, caseId, record));

        switch (decision)
        {
            case OpeningDecision.PrepareNew:
                record = await _preparation.PrepareAsync(context, caseId, cancellationToken).ConfigureAwait(false);
                if (record is null || !record.CaseId.Equals(caseId) || record.Status != OpeningRecordStatus.Prepared)
                {
                    throw new InvalidOperationException("Preparation must return a prepared record for the requested case.");
                }

                journal.Add(record);
                await _journalStore.SaveAsync(context.ProfileId, journal, cancellationToken).ConfigureAwait(false);
                return await ApplyAndCommitAsync(context, journal, record, cancellationToken).ConfigureAwait(false);

            case OpeningDecision.ApplyPrepared:
                return await ApplyAndCommitAsync(context, journal, record!, cancellationToken).ConfigureAwait(false);

            case OpeningDecision.RecoverCommitted:
                return await RecoverAndCommitAsync(context, journal, record!).ConfigureAwait(false);

            case OpeningDecision.ReplayCommitted:
                _inventory.Replay(context, record!);
                return context.Response;

            default:
                throw new InvalidOperationException("Case opening state contradicts the persisted settlement record.");
        }
    }

    private async Task<ItemEventRouterResponse> ApplyAndCommitAsync(
        OpeningContext context,
        CaseOpeningJournal journal,
        CaseOpeningRecord record,
        CancellationToken cancellationToken)
    {
        var checkpoint = _inventory.Capture(context);
        var commitStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var appliedRecord = _inventory.ApplyPrepared(context, record);
            ValidateAppliedRecord(record, appliedRecord);
            journal.Replace(appliedRecord);
            await _journalStore
                .SaveAsync(context.ProfileId, journal, CancellationToken.None)
                .ConfigureAwait(false);

            commitStarted = true;
            await _committer.CommitAsync(context.ProfileId, CancellationToken.None).ConfigureAwait(false);

            var committedRecord = appliedRecord.Commit(UtcNow());
            journal.Replace(committedRecord);
            journal.PruneCommitted();
            await _journalStore.SaveAsync(context.ProfileId, journal, CancellationToken.None).ConfigureAwait(false);
            return context.Response;
        }
        catch when (!commitStarted)
        {
            _inventory.Restore(context, checkpoint);
            throw;
        }
    }

    private static void ValidateAppliedRecord(CaseOpeningRecord preparedRecord, CaseOpeningRecord appliedRecord)
    {
        if (appliedRecord is null ||
            appliedRecord.Status != OpeningRecordStatus.Prepared ||
            appliedRecord.CommittedAtUtc is not null ||
            !appliedRecord.CaseId.Equals(preparedRecord.CaseId) ||
            !appliedRecord.KeyId.Equals(preparedRecord.KeyId) ||
            !string.Equals(appliedRecord.RewardId, preparedRecord.RewardId, StringComparison.Ordinal) ||
            appliedRecord.PreparedAtUtc != preparedRecord.PreparedAtUtc)
        {
            throw new InvalidOperationException("Live settlement changed the prepared transaction identity.");
        }

        var expectedItems = preparedRecord.RewardItems.ToDictionary(item => item.Id);
        var actualItems = appliedRecord.RewardItems.ToDictionary(item => item.Id);
        if (actualItems.Count != expectedItems.Count ||
            actualItems.Any(pair =>
                !expectedItems.TryGetValue(pair.Key, out var expected) ||
                pair.Value.Template != expected.Template))
        {
            throw new InvalidOperationException("Live settlement changed the selected reward item identities.");
        }
    }

    private static void RejectDifferentPreparedTransaction(CaseOpeningJournal journal, MongoId caseId)
    {
        var pendingOpening = journal.PreparedOpening;
        if (journal.PreparedRelay is not null ||
            pendingOpening is not null && pendingOpening.CaseId != caseId)
        {
            throw new InvalidOperationException(
                "The profile already has a different prepared settlement transaction that must be resumed first.");
        }
    }

    private async Task<ItemEventRouterResponse> RecoverAndCommitAsync(
        OpeningContext context,
        CaseOpeningJournal journal,
        CaseOpeningRecord record)
    {
        await _committer.CommitAsync(context.ProfileId, CancellationToken.None).ConfigureAwait(false);

        var committedRecord = record.Commit(UtcNow());
        journal.Replace(committedRecord);
        journal.PruneCommitted();
        await _journalStore.SaveAsync(context.ProfileId, journal, CancellationToken.None).ConfigureAwait(false);
        _inventory.Replay(context, committedRecord);
        return context.Response;
    }

    private DateTimeOffset UtcNow()
    {
        var value = _utcNow();
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Settlement timestamps must be UTC.");
        }

        return value;
    }
}
