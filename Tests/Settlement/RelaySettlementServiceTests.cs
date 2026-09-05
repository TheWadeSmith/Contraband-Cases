using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class RelaySettlementServiceTests
{
    private static readonly MongoId StashId = (MongoId)"111111111111111111111111";
    private static readonly MongoId SortingTableId = (MongoId)"222222222222222222222222";

    [Fact]
    public async Task New_relay_journals_selection_then_commit_marker_before_profile_save()
    {
        var fixture = Fixture.NewRelay();
        fixture.Committer.BeforeCommit = () =>
        {
            var durable = Assert.Single(fixture.Store.Stored.RelayRecords);
            Assert.Equal(RelayRecordStatus.Prepared, durable.Status);
            Assert.True(durable.ProfileCommitStarted);
        };

        var response = await fixture.Service.RelayAsync(fixture.Context, fixture.RootId, CancellationToken.None);

        Assert.Equal(1, fixture.Preparation.RelayCalls);
        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        Assert.Equal(1, fixture.Committer.Calls);
        Assert.Equal(RelayRecordStatus.Committed, fixture.Store.Stored.RelayRecords.Single().Status);
        Assert.Equal(0, fixture.Store.Stored.RecoveryMeter);
        var receipt = Assert.IsType<RelayReceipt>(response.ExtensionData![ModConstants.RelayReceiptExtensionKey]);
        Assert.Equal("RarityUpgrade", receipt.Outcome);
        Assert.False(receipt.Replay);
        Assert.Collection(
            fixture.Store.Saved,
            saved => Assert.False(saved.RelayRecords.Single().ProfileCommitStarted),
            saved => Assert.True(saved.RelayRecords.Single().ProfileCommitStarted),
            saved => Assert.Equal(RelayRecordStatus.Committed, saved.RelayRecords.Single().Status));
    }

    [Fact]
    public async Task Prepared_retry_reuses_planned_outcome_without_preparing_or_rerolling()
    {
        var fixture = Fixture.PreparedRelay();

        await fixture.Service.RelayAsync(fixture.Context, fixture.RootId, CancellationToken.None);

        Assert.Equal(0, fixture.Preparation.RelayCalls);
        Assert.Equal(1, fixture.Inventory.ApplyCalls);
        Assert.Equal(RelayOutcome.RarityUpgrade, fixture.Inventory.Applied!.Outcome);
    }

    [Fact]
    public async Task Outputless_confiscation_recovers_after_commit_marker_and_applies_meter_once()
    {
        var fixture = Fixture.PreparedConfiscation(commitStarted: true, meterBefore: 1, stage: 2);
        fixture.Inventory.Evidence = new RelayInventoryEvidence(RewardPresence.Absent, false, RewardPresence.Absent);

        await fixture.Service.RelayAsync(fixture.Context, fixture.RootId, CancellationToken.None);
        await fixture.Service.RelayAsync(
            new OpeningContext(null!, new ItemEventRouterResponse(), fixture.ProfileId),
            fixture.RootId,
            CancellationToken.None);

        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(1, fixture.Committer.Calls);
        Assert.Equal(2, fixture.Inventory.ReplayCalls);
        Assert.Equal(3, fixture.Store.Stored.RecoveryMeter);
        Assert.Equal(RelayRecordStatus.Committed, fixture.Store.Stored.RelayRecords.Single().Status);
    }

    [Fact]
    public async Task Journal_failure_before_commit_boundary_restores_inventory()
    {
        var fixture = Fixture.PreparedRelay();
        fixture.Store.FailOnSaveAttempt = 1;

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.RelayAsync(fixture.Context, fixture.RootId, CancellationToken.None));

        Assert.Equal(1, fixture.Inventory.RestoreCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.False(fixture.Store.Stored.RelayRecords.Single().ProfileCommitStarted);
    }

    [Fact]
    public async Task Profile_save_failure_after_marker_does_not_roll_back_and_retry_recovers()
    {
        var fixture = Fixture.PreparedRelay();
        fixture.Committer.Exception = new IOException("profile save failed");

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.RelayAsync(fixture.Context, fixture.RootId, CancellationToken.None));

        Assert.Equal(0, fixture.Inventory.RestoreCalls);
        Assert.True(fixture.Store.Stored.RelayRecords.Single().ProfileCommitStarted);
        fixture.Committer.Exception = null;
        fixture.Inventory.Evidence = new RelayInventoryEvidence(RewardPresence.Absent, false, RewardPresence.Complete);
        await fixture.Service.RelayAsync(fixture.Context, fixture.RootId, CancellationToken.None);
        Assert.Equal(RelayRecordStatus.Committed, fixture.Store.Stored.RelayRecords.Single().Status);
    }

    [Fact]
    public async Task Secure_is_a_durable_idempotent_terminal_action_without_profile_mutation()
    {
        var fixture = Fixture.NewSecure();

        await fixture.Service.SecureAsync(fixture.Context, fixture.RootId, CancellationToken.None);
        var replayContext = new OpeningContext(null!, new ItemEventRouterResponse(), fixture.ProfileId);
        await fixture.Service.SecureAsync(replayContext, fixture.RootId, CancellationToken.None);

        Assert.Equal(1, fixture.Preparation.SecureCalls);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(RelayRecordStatus.Committed, fixture.Store.Stored.RelayRecords.Single().Status);
        Assert.True(Assert.IsType<RelayReceipt>(
            replayContext.Response.ExtensionData![ModConstants.RelayReceiptExtensionKey]).Replay);
    }

    [Fact]
    public async Task Different_action_on_consumed_ancestor_is_rejected()
    {
        var fixture = Fixture.CommittedRelay();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SecureAsync(fixture.Context, fixture.RootId, CancellationToken.None));
    }

    [Theory]
    [InlineData(RelayRecordAction.Secure, RelayRecordAction.Secure)]
    [InlineData(RelayRecordAction.Secure, RelayRecordAction.Relay)]
    [InlineData(RelayRecordAction.Relay, RelayRecordAction.Secure)]
    [InlineData(RelayRecordAction.Relay, RelayRecordAction.Relay)]
    public async Task Different_stake_cannot_start_while_profile_transaction_is_prepared(
        RelayRecordAction pendingAction,
        RelayRecordAction requestedAction)
    {
        var pending = Fixture.Record(pendingAction);
        var requested = Fixture.Record(requestedAction);
        var fixture = Fixture.Create(
            new CaseOpeningJournal(
                [Fixture.OpeningFor(pending), Fixture.OpeningFor(requested)],
                [pending]),
            requested);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => requestedAction switch
        {
            RelayRecordAction.Secure => fixture.Service.SecureAsync(
                fixture.Context,
                fixture.RootId,
                CancellationToken.None),
            RelayRecordAction.Relay => fixture.Service.RelayAsync(
                fixture.Context,
                fixture.RootId,
                CancellationToken.None),
            _ => throw new InvalidOperationException()
        });

        Assert.Contains("different prepared Relay transaction", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Preparation.SecureCalls);
        Assert.Equal(0, fixture.Preparation.RelayCalls);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
    }

    [Theory]
    [InlineData(RelayRecordAction.Secure)]
    [InlineData(RelayRecordAction.Relay)]
    public async Task Prepared_opening_blocks_a_new_relay_transaction_before_preparation(
        RelayRecordAction requestedAction)
    {
        var requested = Fixture.Record(requestedAction);
        var fixture = Fixture.Create(
            new CaseOpeningJournal(
                [Fixture.PreparedOpening(), Fixture.OpeningFor(requested)]),
            requested);

        await Assert.ThrowsAsync<InvalidOperationException>(() => requestedAction switch
        {
            RelayRecordAction.Secure => fixture.Service.SecureAsync(
                fixture.Context,
                fixture.RootId,
                CancellationToken.None),
            RelayRecordAction.Relay => fixture.Service.RelayAsync(
                fixture.Context,
                fixture.RootId,
                CancellationToken.None),
            _ => throw new InvalidOperationException()
        });

        Assert.Equal(0, fixture.Preparation.SecureCalls);
        Assert.Equal(0, fixture.Preparation.RelayCalls);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(0, fixture.Store.SaveAttempts);
    }

    [Theory]
    [InlineData(RelayRecordAction.Secure)]
    [InlineData(RelayRecordAction.Relay)]
    public async Task Prepared_transaction_with_stale_meter_is_rejected_before_commit(
        RelayRecordAction action)
    {
        var prepared = Fixture.Record(action);
        var fixture = Fixture.Create(
            new CaseOpeningJournal([Fixture.OpeningFor(prepared)], [prepared], recoveryMeter: 1),
            prepared);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => action switch
        {
            RelayRecordAction.Secure => fixture.Service.SecureAsync(
                fixture.Context,
                fixture.RootId,
                CancellationToken.None),
            RelayRecordAction.Relay => fixture.Service.RelayAsync(
                fixture.Context,
                fixture.RootId,
                CancellationToken.None),
            _ => throw new InvalidOperationException()
        });

        Assert.Contains("recovery meter", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
    }

    [Fact]
    public async Task Prepared_secure_retry_remains_available_for_a_changed_stake_tree()
    {
        var prepared = Fixture.Record(RelayRecordAction.Secure);
        var fixture = Fixture.Create(
            new CaseOpeningJournal([Fixture.OpeningFor(prepared)], [prepared]),
            prepared);
        fixture.Inventory.Evidence = new RelayInventoryEvidence(
            RewardPresence.Partial,
            false,
            RewardPresence.Absent);

        await fixture.Service.SecureAsync(fixture.Context, fixture.RootId, CancellationToken.None);

        Assert.Equal(RelayRecordStatus.Committed, fixture.Store.Stored.RelayRecords.Single().Status);
        Assert.Equal(0, fixture.Committer.Calls);
    }

    [Fact]
    public async Task Committed_replay_succeeds_after_later_transactions_change_meter()
    {
        var prepared = Fixture.Record(RelayRecordAction.Relay);
        var committed = prepared.BeginProfileCommit(prepared.OutputItems)
            .Commit(DateTimeOffset.UnixEpoch.AddMinutes(2));
        var fixture = Fixture.Create(
            new CaseOpeningJournal([Fixture.OpeningFor(prepared)], [committed], recoveryMeter: 2),
            prepared);

        var response = await fixture.Service.RelayAsync(
            fixture.Context,
            fixture.RootId,
            CancellationToken.None);

        Assert.Equal(0, fixture.Preparation.RelayCalls);
        Assert.Equal(0, fixture.Inventory.ApplyCalls);
        Assert.Equal(0, fixture.Committer.Calls);
        Assert.Equal(1, fixture.Inventory.ReplayCalls);
        Assert.Equal(2, fixture.Store.Stored.RecoveryMeter);
        Assert.True(Assert.IsType<RelayReceipt>(
            response.ExtensionData![ModConstants.RelayReceiptExtensionKey]).Replay);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("slot")]
    [InlineData("location")]
    [InlineData("upd")]
    [InlineData("desc")]
    [InlineData("extension")]
    public async Task Applied_output_child_must_preserve_exact_prepared_state(string mutation)
    {
        var fixture = Fixture.PreparedRelay();
        fixture.Inventory.ApplyResult = record =>
        {
            var output = record.OutputItems.Select(CaseOpeningRecord.CloneItem).ToArray();
            var child = output[1];
            switch (mutation)
            {
                case "parent":
                    output[1] = child with { ParentId = "333333333333333333333333" };
                    break;
                case "slot":
                    output[1] = child with { SlotId = "changed-slot" };
                    break;
                case "location":
                    Assert.IsType<ItemLocation>(child.Location).X = 99;
                    break;
                case "upd":
                    Assert.NotNull(child.Upd);
                    child.Upd!.StackObjectsCount = 9;
                    break;
                case "desc":
                    output[1] = child with { Desc = "changed" };
                    break;
                case "extension":
                    child.ExtensionData!["marker"] = "changed";
                    break;
                default:
                    throw new InvalidOperationException();
            }

            return record.BeginProfileCommit(output);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.RelayAsync(fixture.Context, fixture.RootId, CancellationToken.None));

        Assert.Equal(1, fixture.Inventory.RestoreCalls);
        Assert.Equal(0, fixture.Committer.Calls);
    }

    [Fact]
    public async Task Applied_output_root_may_move_to_the_profile_sorting_table()
    {
        var fixture = Fixture.PreparedRelay();
        fixture.Inventory.ApplyResult = record =>
        {
            var output = record.OutputItems.Select(CaseOpeningRecord.CloneItem).ToArray();
            output[0] = output[0] with
            {
                ParentId = SortingTableId.ToString(),
                SlotId = "sorting-table-slot",
                Location = new ItemLocation { X = 8, Y = 4 }
            };
            return record.BeginProfileCommit(output);
        };

        await fixture.Service.RelayAsync(fixture.Context, fixture.RootId, CancellationToken.None);

        Assert.Equal(RelayRecordStatus.Committed, fixture.Store.Stored.RelayRecords.Single().Status);
        Assert.Equal(SortingTableId.ToString(), fixture.Store.Stored.RelayRecords.Single().OutputItems[0].ParentId);
    }

    [Fact]
    public async Task Applied_output_root_outside_profile_inventory_is_rejected()
    {
        var fixture = Fixture.PreparedRelay();
        fixture.Inventory.ApplyResult = record =>
        {
            var output = record.OutputItems.Select(CaseOpeningRecord.CloneItem).ToArray();
            output[0] = output[0] with { ParentId = "333333333333333333333333" };
            return record.BeginProfileCommit(output);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.RelayAsync(fixture.Context, fixture.RootId, CancellationToken.None));

        Assert.Equal(1, fixture.Inventory.RestoreCalls);
        Assert.Equal(0, fixture.Committer.Calls);
    }

    private sealed class Fixture
    {
        private Fixture(CaseOpeningJournal journal, RelaySettlementRecord prepared)
        {
            ProfileId = new MongoId();
            RootId = prepared.StakeRootId;
            Store = new FakeStore(journal);
            Preparation = new FakePreparation(prepared);
            Inventory = new FakeInventory();
            Committer = new FakeCommitter();
            var sessions = new RaidSessionState();
            sessions.BeginGameStart(ProfileId);
            sessions.CompleteGameStart(ProfileId, true);
            Context = new OpeningContext(
                new PmcData
                {
                    Inventory = new BotBaseInventory
                    {
                        Stash = StashId,
                        SortingTable = SortingTableId,
                        Items = []
                    }
                },
                new ItemEventRouterResponse(),
                ProfileId);
            Service = new RelaySettlementService(
                Store, Preparation, Inventory, Committer, new ProfileLockPool(), sessions, new FakeCatalog(),
                () => DateTimeOffset.UnixEpoch.AddHours(1));
        }

        public MongoId ProfileId { get; }
        public MongoId RootId { get; }
        public FakeStore Store { get; }
        public FakePreparation Preparation { get; }
        public FakeInventory Inventory { get; }
        public FakeCommitter Committer { get; }
        public OpeningContext Context { get; }
        public RelaySettlementService Service { get; }

        public static Fixture NewRelay()
        {
            var prepared = RelayRecord();
            return new Fixture(new CaseOpeningJournal([Opening(prepared)]), prepared);
        }

        public static Fixture NewSecure()
        {
            var prepared = SecureRecord();
            return new Fixture(new CaseOpeningJournal([Opening(prepared)]), prepared);
        }

        public static Fixture PreparedRelay()
        {
            var prepared = RelayRecord();
            return new Fixture(new CaseOpeningJournal([Opening(prepared)], [prepared]), prepared);
        }

        public static Fixture CommittedRelay()
        {
            var prepared = RelayRecord();
            var committed = prepared.BeginProfileCommit(prepared.OutputItems).Commit(DateTimeOffset.UnixEpoch.AddMinutes(2));
            return new Fixture(new CaseOpeningJournal([Opening(prepared)], [committed]), prepared);
        }

        public static Fixture PreparedConfiscation(bool commitStarted, int meterBefore, int stage)
        {
            var root = new MongoId();
            var prepared = new RelaySettlementRecord(
                new MongoId(), root, [root], "scav", RewardRarity.ScavGrade, stage,
                RelayRecordAction.Relay, new MongoId(), RelayOutcome.Confiscated, null, [],
                meterBefore, RelayRules.MeterAfterConfiscation(meterBefore, stage), false,
                commitStarted, DateTimeOffset.UnixEpoch, RelayRecordStatus.Prepared, null);
            return new Fixture(new CaseOpeningJournal([Opening(prepared)], [prepared], meterBefore), prepared);
        }

        public static Fixture Create(CaseOpeningJournal journal, RelaySettlementRecord prepared) =>
            new(journal, prepared);

        public static RelaySettlementRecord Record(RelayRecordAction action) =>
            action == RelayRecordAction.Secure ? SecureRecord() : RelayRecord();

        public static CaseOpeningRecord OpeningFor(RelaySettlementRecord relay) => Opening(relay);

        public static CaseOpeningRecord PreparedOpening() => new(
            new MongoId(),
            new MongoId(),
            "pending",
            [new Item { Id = new MongoId(), Template = (MongoId)"dddddddddddddddddddddddd" }],
            DateTimeOffset.UnixEpoch,
            OpeningRecordStatus.Prepared,
            null);

        private static RelaySettlementRecord RelayRecord()
        {
            var root = new MongoId();
            var inputRoot = new Item
            {
                Id = root,
                Template = (MongoId)"aaaaaaaaaaaaaaaaaaaaaaaa",
                ParentId = StashId.ToString(),
                SlotId = "hideout"
            };
            var outputRoot = new Item
            {
                Id = new MongoId(),
                Template = (MongoId)"bbbbbbbbbbbbbbbbbbbbbbbb",
                ParentId = StashId.ToString(),
                SlotId = "hideout",
                Location = new ItemLocation { X = 1, Y = 2 },
                Desc = "prepared-root",
                Upd = new Upd { StackObjectsCount = 1 }
            };
            var outputChild = new Item
            {
                Id = new MongoId(),
                Template = (MongoId)"cccccccccccccccccccccccc",
                ParentId = outputRoot.Id.ToString(),
                SlotId = "mod_scope",
                Location = new ItemLocation { X = 3, Y = 4 },
                Desc = "prepared-child",
                Upd = new Upd { StackObjectsCount = 1 }
            };
            outputChild.ExtensionData!["marker"] = "original";
            return new RelaySettlementRecord(
                new MongoId(), root, [root], "scav", RewardRarity.ScavGrade, 1,
                RelayRecordAction.Relay, new MongoId(), RelayOutcome.RarityUpgrade, "uncommon",
                [outputRoot, outputChild],
                0, 0, false, false, DateTimeOffset.UnixEpoch, RelayRecordStatus.Prepared, null,
                [inputRoot]);
        }

        private static RelaySettlementRecord SecureRecord()
        {
            var root = new MongoId();
            var inputRoot = new Item
            {
                Id = root,
                Template = (MongoId)"aaaaaaaaaaaaaaaaaaaaaaaa",
                ParentId = StashId.ToString(),
                SlotId = "hideout"
            };
            return new RelaySettlementRecord(
                new MongoId(), root, [root], "scav", RewardRarity.ScavGrade, 1,
                RelayRecordAction.Secure, null, RelayOutcome.Secured, null, [],
                0, 0, false, false, DateTimeOffset.UnixEpoch, RelayRecordStatus.Prepared, null,
                [inputRoot]);
        }

        private static CaseOpeningRecord Opening(RelaySettlementRecord relay) => new(
            relay.OriginCaseId,
            new MongoId(),
            relay.InputRewardId,
            relay.InputItems.Count > 0
                ? relay.InputItems.ToList()
                :
                [
                    new Item
                    {
                        Id = relay.StakeRootId,
                        Template = (MongoId)"aaaaaaaaaaaaaaaaaaaaaaaa",
                        ParentId = StashId.ToString(),
                        SlotId = "hideout"
                    }
                ],
            DateTimeOffset.UnixEpoch,
            OpeningRecordStatus.Committed,
            DateTimeOffset.UnixEpoch);
    }

    private sealed class FakeStore(CaseOpeningJournal stored) : ICaseOpeningJournalStore
    {
        public CaseOpeningJournal Stored { get; private set; } = Copy(stored);
        public List<CaseOpeningJournal> Saved { get; } = [];
        public int SaveAttempts { get; private set; }
        public int? FailOnSaveAttempt { get; set; }

        public ValueTask<CaseOpeningJournal> LoadAsync(MongoId profileId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Copy(Stored));

        public ValueTask SaveAsync(MongoId profileId, CaseOpeningJournal journal, CancellationToken cancellationToken)
        {
            SaveAttempts++;
            if (FailOnSaveAttempt == SaveAttempts)
            {
                throw new IOException("journal save failed");
            }
            Stored = Copy(journal);
            Saved.Add(Copy(journal));
            return ValueTask.CompletedTask;
        }

        private static CaseOpeningJournal Copy(CaseOpeningJournal value) => new(
            value.Records,
            value.RelayRecords,
            value.RecoveryMeter,
            value.LegacySecuredStakeRoots);
    }

    private sealed class FakePreparation(RelaySettlementRecord record) : IRelayPreparation
    {
        public int SecureCalls { get; private set; }
        public int RelayCalls { get; private set; }

        public ValueTask<RelaySettlementRecord> PrepareSecureAsync(
            OpeningContext context, RelayStake stake, int recoveryMeter, CancellationToken cancellationToken)
        {
            SecureCalls++;
            return ValueTask.FromResult(record);
        }

        public ValueTask<RelaySettlementRecord> PrepareRelayAsync(
            OpeningContext context, RelayStake stake, int recoveryMeter, CancellationToken cancellationToken)
        {
            RelayCalls++;
            return ValueTask.FromResult(record);
        }
    }

    private sealed class FakeInventory : IRelayInventory
    {
        public RelayInventoryEvidence Evidence { get; set; } =
            new(RewardPresence.Complete, true, RewardPresence.Absent);
        public int ApplyCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public int ReplayCalls { get; private set; }
        public RelaySettlementRecord? Applied { get; private set; }
        public Func<RelaySettlementRecord, RelaySettlementRecord>? ApplyResult { get; set; }

        public RelayInventoryEvidence InspectRelay(OpeningContext context, RelaySettlementRecord record) => Evidence;
        public InventoryCheckpoint CaptureRelay(OpeningContext context) => new FakeCheckpoint();
        public RelaySettlementRecord ApplyPreparedRelay(OpeningContext context, RelaySettlementRecord record)
        {
            ApplyCalls++;
            Applied = record;
            return ApplyResult?.Invoke(record) ?? record.BeginProfileCommit(record.OutputItems);
        }
        public void RestoreRelay(OpeningContext context, InventoryCheckpoint checkpoint) => RestoreCalls++;
        public void ReplayRelay(OpeningContext context, RelaySettlementRecord record) => ReplayCalls++;
    }

    private sealed record FakeCheckpoint : InventoryCheckpoint;

    private sealed class FakeCommitter : IProfileCommitter
    {
        public int Calls { get; private set; }
        public Exception? Exception { get; set; }
        public Action? BeforeCommit { get; set; }
        public Task CommitAsync(MongoId profileId, CancellationToken cancellationToken)
        {
            Calls++;
            BeforeCommit?.Invoke();
            return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
        }
    }

    private sealed class FakeCatalog : IRelayRewardCatalog
    {
        private readonly IReadOnlyList<ValidatedReward> _rewards = RewardCatalog.Create([
            new RewardDefinition("scav", "Scav", "aaaaaaaaaaaaaaaaaaaaaaaa", "scav-preset", RewardRarity.ScavGrade, 0.5),
            new RewardDefinition("uncommon", "Uncommon", "bbbbbbbbbbbbbbbbbbbbbbbb", "uncommon-preset", RewardRarity.Uncommon, 0.5)
        ]).Validate(new Resolver());

        public IReadOnlyList<ValidatedReward> Rewards => _rewards;
        public ValidatedReward FindReward(string rewardId) => _rewards.Single(reward => reward.Id == rewardId);

        private sealed class Resolver : IRewardPresetResolver
        {
            public RewardPresetTree Resolve(string presetId) => presetId switch
            {
                "scav-preset" => new([new RewardPresetItem("root-a", "aaaaaaaaaaaaaaaaaaaaaaaa", null)]),
                "uncommon-preset" => new([new RewardPresetItem("root-b", "bbbbbbbbbbbbbbbbbbbbbbbb", null)]),
                _ => throw new InvalidOperationException()
            };
        }
    }
}
