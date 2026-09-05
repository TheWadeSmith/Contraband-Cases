using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class CaseOpeningServiceTests
{
    [Fact]
    public async Task Non_lobby_request_fails_before_loading_or_preparing_a_settlement()
    {
        var fixture = Fixture.New(enterLobby: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, CancellationToken.None));

        Assert.Equal(0, fixture.Store.LoadCalls);
        Assert.Equal(0, fixture.Preparation.Calls);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
    }

    [Fact]
    public async Task New_open_persists_prepared_before_mutation_then_commits_once()
    {
        var fixture = Fixture.New();
        var preparedSaveCount = 0;
        fixture.Store.BeforeSave = journal =>
        {
            if (journal.Records.Single().Status == OpeningRecordStatus.Prepared)
            {
                preparedSaveCount++;
                Assert.Equal(preparedSaveCount - 1, fixture.Inventory.ApplyCalls);
            }
        };

        var response = await fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, CancellationToken.None);

        Assert.Same(fixture.Context.Response, response);
        Assert.Equal(1, fixture.Preparation.Calls);
        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        Assert.Equal(1, fixture.Committer.Calls);
        Assert.True(fixture.Committer.SawNonCancellableToken);
        Assert.Equal(new[] { "reward" }, fixture.Inventory.InventoryItems);
        Assert.Empty(fixture.Inventory.InsuredItems);
        Assert.Equal(ExpectedChanges(fixture.Record), fixture.Inventory.ResponseChanges(fixture.Context));
        Assert.Collection(
            fixture.Store.Saved,
            prepared => Assert.Equal(OpeningRecordStatus.Prepared, prepared.Records.Single().Status),
            applied => Assert.Equal(OpeningRecordStatus.Prepared, applied.Records.Single().Status),
            committed => Assert.Equal(OpeningRecordStatus.Committed, committed.Records.Single().Status));
    }

    [Fact]
    public async Task Prepared_retry_applies_the_saved_tree_without_preparing_again()
    {
        var fixture = Fixture.Prepared();

        await fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, CancellationToken.None);

        Assert.Equal(0, fixture.Preparation.Calls);
        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        Assert.Equal(fixture.Record.ExactRewardIds, fixture.Inventory.AppliedRecord!.ExactRewardIds);
    }

    [Fact]
    public async Task Prepared_retry_persists_the_live_applied_payload_before_profile_commit()
    {
        var fixture = Fixture.Prepared();
        var liveRecord = Relocate(fixture.Record, 9, 4);
        fixture.Inventory.AppliedResult = liveRecord;
        fixture.Committer.BeforeCommit = () =>
        {
            var durablePrepared = Assert.Single(fixture.Store.Stored.Records);
            Assert.Equal(OpeningRecordStatus.Prepared, durablePrepared.Status);
            AssertLocation(durablePrepared, 9, 4);
        };

        using var requestCancellation = new CancellationTokenSource();
        await fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, requestCancellation.Token);

        Assert.Collection(
            fixture.Store.Saved,
            prepared =>
            {
                Assert.Equal(OpeningRecordStatus.Prepared, prepared.Records.Single().Status);
                AssertLocation(prepared.Records.Single(), 9, 4);
            },
            committed =>
            {
                Assert.Equal(OpeningRecordStatus.Committed, committed.Records.Single().Status);
                AssertLocation(committed.Records.Single(), 9, 4);
            });
        Assert.All(fixture.Store.SaveTokens, token => Assert.False(token.CanBeCanceled));
        AssertLocation(fixture.Store.Stored.Records.Single(), 9, 4);
    }

    [Fact]
    public async Task Live_prepared_journal_failure_restores_before_profile_commit()
    {
        var fixture = Fixture.Prepared();
        fixture.Inventory.AppliedResult = Relocate(fixture.Record, 9, 4);
        fixture.Store.FailOnSaveAttempt = 1;

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, CancellationToken.None));

        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        Assert.Equal(1, fixture.Inventory.RestoreCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(new[] { "case", "key" }, fixture.Inventory.InventoryItems);
        Assert.Empty(fixture.Inventory.ResponseChanges(fixture.Context));
        Assert.Null(Assert.Single(fixture.Store.Stored.Records.Single().RewardItems).Location);
    }

    [Fact]
    public async Task Invalid_live_applied_identity_restores_before_profile_commit()
    {
        var fixture = Fixture.Prepared();
        var changedItem = fixture.Record.RewardItems.Single() with { Id = new MongoId() };
        fixture.Inventory.AppliedResult = new CaseOpeningRecord(
            fixture.Record.CaseId,
            fixture.Record.KeyId,
            fixture.Record.RewardId,
            [changedItem],
            fixture.Record.PreparedAtUtc,
            OpeningRecordStatus.Prepared,
            null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, CancellationToken.None));

        Assert.Equal(1, fixture.Inventory.RestoreCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Empty(fixture.Store.Saved);
    }

    [Fact]
    public async Task Profile_saved_recovery_commits_non_cancellably_marks_record_and_replays_response()
    {
        var fixture = Fixture.Prepared(new InventoryEvidence(false, false, RewardPresence.Complete));

        await fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, CancellationToken.None);

        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(1, fixture.Committer.Calls);
        Assert.True(fixture.Committer.SawNonCancellableToken);
        Assert.Equal(1, fixture.Inventory.ReplayCalls);
        Assert.Equal(OpeningRecordStatus.Committed, fixture.Store.Stored.Records.Single().Status);
        Assert.Equal(ExpectedChanges(fixture.Record), fixture.Inventory.ResponseChanges(fixture.Context));
    }

    [Fact]
    public async Task Committed_retry_replays_without_mutation_selection_or_profile_save()
    {
        var fixture = Fixture.Committed();

        await fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, CancellationToken.None);

        Assert.Equal(0, fixture.Preparation.Calls);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(1, fixture.Inventory.ReplayCalls);
        Assert.Equal(ExpectedChanges(fixture.Record), fixture.Inventory.ResponseChanges(fixture.Context));
    }

    [Fact]
    public async Task Preparation_failure_leaves_inventory_and_journal_untouched()
    {
        var fixture = Fixture.New();
        fixture.Preparation.Exception = new InvalidOperationException("no key");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, CancellationToken.None));

        Assert.Empty(fixture.Store.Saved);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
    }

    [Fact]
    public async Task Prepared_relay_blocks_a_new_opening_before_preparation_mutation_or_save()
    {
        var fixture = Fixture.NewWithPreparedRelay();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, CancellationToken.None));

        Assert.Equal(0, fixture.Preparation.Calls);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(0, fixture.Store.SaveAttempts);
        Assert.Equal(RelayRecordStatus.Prepared, Assert.Single(fixture.Store.Stored.RelayRecords).Status);
    }

    [Fact]
    public async Task Prepared_journal_save_failure_causes_no_mutation()
    {
        var fixture = Fixture.New();
        fixture.Store.FailWhen = record => record.Status == OpeningRecordStatus.Prepared;

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, CancellationToken.None));

        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Empty(fixture.Store.Stored.Records);
    }

    [Fact]
    public async Task Apply_failure_restores_inventory_and_response_before_commit()
    {
        var fixture = Fixture.Prepared();
        fixture.Inventory.ApplyException = new InvalidOperationException("apply failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, CancellationToken.None));

        Assert.Equal(1, fixture.Inventory.RestoreCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(OpeningRecordStatus.Prepared, fixture.Store.Stored.Records.Single().Status);
        Assert.Equal(new[] { "case", "key" }, fixture.Inventory.InventoryItems);
        Assert.Equal(new[] { "case", "key" }, fixture.Inventory.InsuredItems);
        Assert.Empty(fixture.Inventory.ResponseChanges(fixture.Context));
    }

    [Fact]
    public async Task Profile_commit_failure_after_boundary_leaves_prepared_without_restore()
    {
        var fixture = Fixture.Prepared();
        fixture.Committer.Exception = new IOException("profile save failed");

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, CancellationToken.None));

        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Inventory.RestoreCalls);
        Assert.Equal(OpeningRecordStatus.Prepared, fixture.Store.Stored.Records.Single().Status);
    }

    [Fact]
    public async Task Committed_write_failure_leaves_prepared_without_restore()
    {
        var fixture = Fixture.Prepared();
        fixture.Store.FailWhen = record => record.Status == OpeningRecordStatus.Committed;

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, CancellationToken.None));

        Assert.Equal(1, fixture.Committer.Calls);
        Assert.Equal(0, fixture.Inventory.RestoreCalls);
        Assert.Equal(OpeningRecordStatus.Prepared, fixture.Store.Stored.Records.Single().Status);
    }

    [Fact]
    public async Task Request_cancellation_after_apply_does_not_reach_profile_commit()
    {
        var fixture = Fixture.Prepared();
        using var cancellation = new CancellationTokenSource();
        fixture.Inventory.AfterApply = cancellation.Cancel;

        await fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, fixture.Committer.Calls);
        Assert.True(fixture.Committer.SawNonCancellableToken);
    }

    [Fact]
    public async Task Request_cancellation_before_commit_start_restores_the_full_checkpoint()
    {
        var fixture = Fixture.Prepared();
        using var cancellation = new CancellationTokenSource();
        fixture.Inventory.AfterCapture = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, cancellation.Token));

        Assert.Equal(1, fixture.Inventory.RestoreCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(new[] { "case", "key" }, fixture.Inventory.InventoryItems);
        Assert.Equal(new[] { "case", "key" }, fixture.Inventory.InsuredItems);
        Assert.Empty(fixture.Inventory.ResponseChanges(fixture.Context));
    }

    [Fact]
    public async Task Concurrent_duplicate_requests_prepare_and_apply_exactly_once()
    {
        var fixture = Fixture.New();
        fixture.Committer.Delay = TimeSpan.FromMilliseconds(50);

        var duplicateContext = new OpeningContext(null!, new ItemEventRouterResponse(), fixture.ProfileId);
        await Task.WhenAll(
            fixture.Service.OpenAsync(fixture.Context, fixture.CaseId, CancellationToken.None),
            fixture.Service.OpenAsync(duplicateContext, fixture.CaseId, CancellationToken.None));

        Assert.Equal(1, fixture.Preparation.Calls);
        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        Assert.Equal(1, fixture.Committer.Calls);
        Assert.Equal(1, fixture.Inventory.ReplayCalls);
        Assert.Equal(ExpectedChanges(fixture.Record), fixture.Inventory.ResponseChanges(fixture.Context));
        Assert.Equal(ExpectedChanges(fixture.Record), fixture.Inventory.ResponseChanges(duplicateContext));
    }

    private static string[] ExpectedChanges(CaseOpeningRecord record) =>
        new[] { "deleted:case", "deleted:key", $"new:{record.ExactRewardIds.Single()}" };

    private static CaseOpeningRecord Relocate(CaseOpeningRecord record, int x, int y)
    {
        var reward = record.RewardItems.Single() with
        {
            ParentId = "stash",
            SlotId = "hideout",
            Location = new ItemLocation { X = x, Y = y }
        };
        return new CaseOpeningRecord(
            record.CaseId,
            record.KeyId,
            record.RewardId,
            [reward],
            record.PreparedAtUtc,
            OpeningRecordStatus.Prepared,
            null);
    }

    private static void AssertLocation(CaseOpeningRecord record, int x, int y)
    {
        var location = Assert.IsType<ItemLocation>(Assert.Single(record.RewardItems).Location);
        Assert.Equal(x, location.X);
        Assert.Equal(y, location.Y);
    }

    private sealed class Fixture
    {
        private Fixture(
            MongoId caseId,
            CaseOpeningJournal journal,
            InventoryEvidence evidence,
            CaseOpeningRecord preparationRecord,
            bool enterLobby = true)
        {
            ProfileId = new MongoId();
            CaseId = caseId;
            Record = preparationRecord;
            Store = new FakeJournalStore(journal);
            Preparation = new FakePreparation(Record);
            Inventory = new FakeInventory(evidence);
            Committer = new FakeCommitter();
            Context = new OpeningContext(null!, new ItemEventRouterResponse(), ProfileId);
            RaidSessions = new RaidSessionState();
            if (enterLobby)
            {
                RaidSessions.BeginGameStart(ProfileId);
                RaidSessions.CompleteGameStart(ProfileId, profileIsReady: true);
            }

            Service = new CaseOpeningService(
                Store,
                Preparation,
                Inventory,
                Committer,
                new ProfileLockPool(),
                RaidSessions,
                () => DateTimeOffset.UtcNow);
        }

        public MongoId ProfileId { get; }
        public MongoId CaseId { get; }
        public CaseOpeningRecord Record { get; }
        public FakeJournalStore Store { get; }
        public FakePreparation Preparation { get; }
        public FakeInventory Inventory { get; }
        public FakeCommitter Committer { get; }
        public RaidSessionState RaidSessions { get; }
        public OpeningContext Context { get; }
        public CaseOpeningService Service { get; }

        public static Fixture New(bool enterLobby = true)
        {
            var caseId = new MongoId();
            return new Fixture(
                caseId,
                new CaseOpeningJournal(),
                new InventoryEvidence(true, true, RewardPresence.Absent),
                NewRecord(caseId),
                enterLobby);
        }

        public static Fixture Prepared(InventoryEvidence? evidence = null)
        {
            var caseId = new MongoId();
            var record = NewRecord(caseId);
            return new Fixture(caseId, new CaseOpeningJournal(new[] { record }), evidence ?? new InventoryEvidence(true, true, RewardPresence.Absent), record);
        }

        public static Fixture Committed()
        {
            var caseId = new MongoId();
            var record = NewRecord(caseId).Commit(DateTimeOffset.UtcNow);
            return new Fixture(caseId, new CaseOpeningJournal(new[] { record }), new InventoryEvidence(false, false, RewardPresence.Complete), record);
        }

        public static Fixture NewWithPreparedRelay()
        {
            var caseId = new MongoId();
            var relayRoot = new MongoId();
            var relay = new RelaySettlementRecord(
                new MongoId(),
                relayRoot,
                [relayRoot],
                "stake",
                RewardRarity.ScavGrade,
                1,
                RelayRecordAction.Relay,
                new MongoId(),
                RelayOutcome.Confiscated,
                null,
                [],
                0,
                RelayRules.MeterAfterConfiscation(0, 1),
                false,
                false,
                DateTimeOffset.UnixEpoch,
                RelayRecordStatus.Prepared,
                null);
            return new Fixture(
                caseId,
                new CaseOpeningJournal(relayRecords: [relay]),
                new InventoryEvidence(true, true, RewardPresence.Absent),
                NewRecord(caseId));
        }

        private static CaseOpeningRecord NewRecord(MongoId caseId) => new(
            caseId,
            new MongoId(),
            "reward",
            new List<Item> { new() { Id = new MongoId(), Template = new MongoId(), Desc = "prepared" } },
            DateTimeOffset.UtcNow,
            OpeningRecordStatus.Prepared,
            null);
    }

    private sealed class FakeJournalStore : ICaseOpeningJournalStore
    {
        public FakeJournalStore(CaseOpeningJournal stored) => Stored = Copy(stored);

        public CaseOpeningJournal Stored { get; private set; }
        public int LoadCalls { get; private set; }
        public List<CaseOpeningJournal> Saved { get; } = new();
        public List<CancellationToken> SaveTokens { get; } = new();
        public int SaveAttempts { get; private set; }
        public int? FailOnSaveAttempt { get; set; }
        public Action<CaseOpeningJournal>? BeforeSave { get; set; }
        public Func<CaseOpeningRecord, bool>? FailWhen { get; set; }

        public ValueTask<CaseOpeningJournal> LoadAsync(MongoId profileId, CancellationToken cancellationToken)
        {
            LoadCalls++;
            return ValueTask.FromResult(Copy(Stored));
        }

        public ValueTask SaveAsync(MongoId profileId, CaseOpeningJournal journal, CancellationToken cancellationToken)
        {
            SaveAttempts++;
            SaveTokens.Add(cancellationToken);
            BeforeSave?.Invoke(journal);
            var record = journal.Records.SingleOrDefault();
            if (FailOnSaveAttempt == SaveAttempts ||
                (record is not null && FailWhen?.Invoke(record) == true))
            {
                throw new IOException("journal save failed");
            }

            Stored = Copy(journal);
            Saved.Add(Copy(journal));
            return ValueTask.CompletedTask;
        }

        private static CaseOpeningJournal Copy(CaseOpeningJournal journal) => new(
            journal.Records.Select(record => new CaseOpeningRecord(
                record.CaseId,
                record.KeyId,
                record.RewardId,
                record.RewardItems.ToList(),
                record.PreparedAtUtc,
                record.Status,
                record.CommittedAtUtc)),
            journal.RelayRecords,
            journal.RecoveryMeter,
            journal.LegacySecuredStakeRoots);
    }

    private sealed class FakePreparation(CaseOpeningRecord record) : IOpeningPreparation
    {
        public int Calls { get; private set; }
        public Exception? Exception { get; set; }

        public ValueTask<CaseOpeningRecord> PrepareAsync(OpeningContext context, MongoId caseId, CancellationToken cancellationToken)
        {
            Calls++;
            if (Exception is not null)
            {
                throw Exception;
            }

            return ValueTask.FromResult(record);
        }
    }

    private sealed class FakeInventory(InventoryEvidence evidence) : IOpeningInventory
    {
        private readonly List<ResponseState> _responseStates = new();

        public int ApplyCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public int ReplayCalls { get; private set; }
        public CaseOpeningRecord? AppliedRecord { get; private set; }
        public CaseOpeningRecord? AppliedResult { get; set; }
        public CaseOpeningRecord? ReplayedRecord { get; private set; }
        public Exception? ApplyException { get; set; }
        public Action? AfterCapture { get; set; }
        public Action? AfterApply { get; set; }
        public List<string> InventoryItems { get; } = new() { "case", "key" };
        public List<string> InsuredItems { get; } = new() { "case", "key" };

        public InventoryEvidence Inspect(OpeningContext context, MongoId caseId, CaseOpeningRecord? record) => evidence;
        public InventoryCheckpoint Capture(OpeningContext context)
        {
            var checkpoint = new FakeCheckpoint(InventoryItems.ToArray(), InsuredItems.ToArray(), ResponseChanges(context).ToArray());
            AfterCapture?.Invoke();
            return checkpoint;
        }

        public CaseOpeningRecord ApplyPrepared(OpeningContext context, CaseOpeningRecord record)
        {
            ApplyCalls++;
            AppliedRecord = record;
            var appliedResult = AppliedResult ?? record;
            InventoryItems.Remove("case");
            InventoryItems.Remove("key");
            InventoryItems.Add("reward");
            InsuredItems.Remove("case");
            InsuredItems.Remove("key");
            AddResponseChanges(context, appliedResult);
            if (ApplyException is not null)
            {
                throw ApplyException;
            }

            AfterApply?.Invoke();
            return appliedResult;
        }

        public void Restore(OpeningContext context, InventoryCheckpoint checkpoint)
        {
            RestoreCalls++;
            var fakeCheckpoint = Assert.IsType<FakeCheckpoint>(checkpoint);
            Reset(InventoryItems, fakeCheckpoint.InventoryItems);
            Reset(InsuredItems, fakeCheckpoint.InsuredItems);
            Reset(ResponseChanges(context), fakeCheckpoint.ResponseChanges);
        }

        public void Replay(OpeningContext context, CaseOpeningRecord record)
        {
            ReplayCalls++;
            ReplayedRecord = record;
            AddResponseChanges(context, record);
        }

        public List<string> ResponseChanges(OpeningContext context)
        {
            var state = _responseStates.SingleOrDefault(candidate => ReferenceEquals(candidate.Response, context.Response));
            if (state is null)
            {
                state = new ResponseState(context.Response);
                _responseStates.Add(state);
            }

            return state.Changes;
        }

        private void AddResponseChanges(OpeningContext context, CaseOpeningRecord record)
        {
            var changes = ResponseChanges(context);
            changes.Add("deleted:case");
            changes.Add("deleted:key");
            changes.Add($"new:{record.ExactRewardIds.Single()}");
        }

        private static void Reset(List<string> destination, IReadOnlyList<string> source)
        {
            destination.Clear();
            destination.AddRange(source);
        }

        private sealed class ResponseState(ItemEventRouterResponse response)
        {
            public ItemEventRouterResponse Response { get; } = response;
            public List<string> Changes { get; } = new();
        }
    }

    private sealed record FakeCheckpoint(
        IReadOnlyList<string> InventoryItems,
        IReadOnlyList<string> InsuredItems,
        IReadOnlyList<string> ResponseChanges) : InventoryCheckpoint;

    private sealed class FakeCommitter : IProfileCommitter
    {
        public int Calls { get; private set; }
        public bool SawNonCancellableToken { get; private set; }
        public Exception? Exception { get; set; }
        public TimeSpan Delay { get; set; }
        public Action? BeforeCommit { get; set; }

        public async Task CommitAsync(MongoId profileId, CancellationToken cancellationToken)
        {
            Calls++;
            SawNonCancellableToken |= !cancellationToken.CanBeCanceled;
            BeforeCommit?.Invoke();
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (Exception is not null)
            {
                throw Exception;
            }
        }
    }
}
