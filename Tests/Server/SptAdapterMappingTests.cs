using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class SptAdapterMappingTests
{
    private static readonly MongoId ProfileId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly MongoId CaseId = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly MongoId KeyId = "cccccccccccccccccccccccc";
    private static readonly MongoId RewardId = "dddddddddddddddddddddddd";
    private static readonly MongoId ChildId = "121212121212121212121212";
    private static readonly MongoId AttachedItemId = "999999999999999999999999";
    private static readonly MongoId StashId = "777777777777777777777777";
    private static readonly MongoId SortingTableId = "888888888888888888888888";
    private static readonly MongoId FailedSaveProfileId = "111111111111111111111111";
    private static readonly MongoId CanceledSaveProfileId = "222222222222222222222222";

    [Fact]
    public void Journal_document_round_trip_preserves_the_domain_record()
    {
        var preparedAt = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        var committedAt = preparedAt.AddSeconds(2);
        var journal = new CaseOpeningJournal([
            new CaseOpeningRecord(
                CaseId,
                KeyId,
                "rsass",
                [new Item { Id = RewardId, Template = "eeeeeeeeeeeeeeeeeeeeeeee" }],
                preparedAt,
                OpeningRecordStatus.Committed,
                committedAt)
        ]);

        var document = SptCaseJournal.ToDocument(journal);
        var restored = SptCaseJournal.FromDocument(document);

        Assert.Equal("contraband-cases-openings-v1", SptCaseJournal.JournalKey);
        var record = Assert.Single(restored.Records);
        Assert.Equal(CaseId, record.CaseId);
        Assert.Equal(KeyId, record.KeyId);
        Assert.Equal("rsass", record.RewardId);
        Assert.Equal(RewardId, Assert.Single(record.RewardItems).Id);
        Assert.Equal(OpeningRecordStatus.Committed, record.Status);
        Assert.Equal(committedAt, record.CommittedAtUtc);
    }

    [Fact]
    public void Replay_writes_profile_scoped_item_changes_without_mutating_inventory()
    {
        var record = new CaseOpeningRecord(
            CaseId,
            KeyId,
            "reward",
            [new Item { Id = RewardId, Template = "eeeeeeeeeeeeeeeeeeeeeeee", ParentId = "stash", SlotId = "hideout" }],
            DateTimeOffset.UtcNow,
            OpeningRecordStatus.Committed,
            DateTimeOffset.UtcNow);
        var response = new ItemEventRouterResponse();
        var inventory = new List<Item> { new() { Id = "ffffffffffffffffffffffff", Template = "111111111111111111111111" } };

        SptResponseChanges.Replay(response, ProfileId, record);

        var changes = response.ProfileChanges[ProfileId].Items;
        Assert.NotNull(changes);
        Assert.Equal(RewardId, Assert.Single(changes!.NewItems!).Id);
        Assert.Equal(new[] { CaseId, KeyId }, changes.DeletedItems!.Select(item => item.Id));
        Assert.Single(inventory);
        Assert.Null(response.Warnings);
    }

    [Fact]
    public void Replay_fails_closed_on_colliding_response_changes()
    {
        var record = new CaseOpeningRecord(
            CaseId,
            KeyId,
            "reward",
            [new Item { Id = RewardId, Template = "eeeeeeeeeeeeeeeeeeeeeeee" }],
            DateTimeOffset.UtcNow,
            OpeningRecordStatus.Committed,
            DateTimeOffset.UtcNow);
        var response = new ItemEventRouterResponse();
        var changes = SptResponseChanges.GetOrCreate(response, ProfileId);
        changes.NewItems!.Add(new Item { Id = RewardId, Template = "eeeeeeeeeeeeeeeeeeeeeeee" });

        Assert.Throws<InvalidOperationException>(() => SptResponseChanges.Replay(response, ProfileId, record));
    }

    [Theory]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("cccccccccccccccccccccccc")]
    public void Settlement_rejects_items_attached_to_either_consumed_input(string parentId)
    {
        var inventory = new[]
        {
            new Item { Id = CaseId, Template = "111111111111111111111111" },
            new Item { Id = KeyId, Template = "222222222222222222222222" },
            new Item
            {
                Id = RewardId,
                Template = "333333333333333333333333",
                ParentId = parentId,
                SlotId = "main"
            }
        };

        Assert.Throws<InvalidOperationException>(() =>
            SptOpeningInventory.EnsureConsumedItemsAreLeaves(inventory, CaseId, KeyId));
    }

    [Fact]
    public void Settlement_leaf_check_ignores_items_attached_elsewhere()
    {
        var inventory = new[]
        {
            new Item { Id = CaseId, Template = "111111111111111111111111" },
            new Item { Id = KeyId, Template = "222222222222222222222222" },
            new Item
            {
                Id = RewardId,
                Template = "333333333333333333333333",
                ParentId = ProfileId.ToString(),
                SlotId = "main"
            }
        };

        SptOpeningInventory.EnsureConsumedItemsAreLeaves(inventory, CaseId, KeyId);
    }

    [Fact]
    public void Settlement_leaf_check_rejects_an_arbitrarily_deep_case_descendant()
    {
        var inventory = new[]
        {
            new Item { Id = CaseId, Template = "111111111111111111111111" },
            new Item { Id = KeyId, Template = "222222222222222222222222" },
            new Item { Id = "343434343434343434343434", Template = "444444444444444444444444", ParentId = CaseId.ToString() },
            new Item { Id = RewardId, Template = "333333333333333333333333", ParentId = "343434343434343434343434" }
        };

        Assert.Throws<InvalidOperationException>(() =>
            SptOpeningInventory.EnsureConsumedItemsAreLeaves(inventory, CaseId, KeyId));
    }

    [Fact]
    public void Settlement_leaf_check_rejects_one_consumed_input_nested_beneath_the_other()
    {
        var inventory = new[]
        {
            new Item { Id = CaseId, Template = "111111111111111111111111", ParentId = KeyId.ToString() },
            new Item { Id = KeyId, Template = "222222222222222222222222" }
        };

        Assert.Throws<InvalidOperationException>(() =>
            SptOpeningInventory.EnsureConsumedItemsAreLeaves(inventory, CaseId, KeyId));
    }

    [Fact]
    public void Settlement_leaf_check_allows_consumed_inputs_inside_ordinary_ancestor_trees()
    {
        var inventory = new[]
        {
            new Item { Id = "343434343434343434343434", Template = "444444444444444444444444", ParentId = ProfileId.ToString() },
            new Item { Id = CaseId, Template = "111111111111111111111111", ParentId = "343434343434343434343434" },
            new Item { Id = "565656565656565656565656", Template = "666666666666666666666666", ParentId = ProfileId.ToString() },
            new Item { Id = KeyId, Template = "222222222222222222222222", ParentId = "565656565656565656565656" }
        };

        SptOpeningInventory.EnsureConsumedItemsAreLeaves(inventory, CaseId, KeyId);
    }

    [Fact]
    public void Settlement_leaf_check_keeps_parent_identifier_matching_ordinal()
    {
        var inventory = new[]
        {
            new Item { Id = CaseId, Template = "111111111111111111111111" },
            new Item { Id = KeyId, Template = "222222222222222222222222" },
            new Item
            {
                Id = RewardId,
                Template = "333333333333333333333333",
                ParentId = CaseId.ToString().ToUpperInvariant()
            }
        };

        SptOpeningInventory.EnsureConsumedItemsAreLeaves(inventory, CaseId, KeyId);
    }

    [Fact]
    public void Settlement_leaf_check_rejects_duplicate_ids_even_when_they_are_unrelated_to_inputs()
    {
        var inventory = new[]
        {
            new Item { Id = CaseId, Template = "111111111111111111111111" },
            new Item { Id = KeyId, Template = "222222222222222222222222" },
            new Item { Id = RewardId, Template = "333333333333333333333333", ParentId = ProfileId.ToString() },
            new Item { Id = RewardId, Template = "444444444444444444444444", ParentId = ProfileId.ToString() }
        };

        Assert.Throws<InvalidOperationException>(() =>
            SptOpeningInventory.EnsureConsumedItemsAreLeaves(inventory, CaseId, KeyId));
    }

    [Fact]
    public void Settlement_leaf_check_rejects_unrelated_parent_cycles_without_hanging()
    {
        var inventory = new[]
        {
            new Item { Id = CaseId, Template = "111111111111111111111111" },
            new Item { Id = KeyId, Template = "222222222222222222222222" },
            new Item { Id = "343434343434343434343434", Template = "444444444444444444444444", ParentId = "565656565656565656565656" },
            new Item { Id = "565656565656565656565656", Template = "666666666666666666666666", ParentId = "343434343434343434343434" }
        };

        Assert.Throws<InvalidOperationException>(() =>
            SptOpeningInventory.EnsureConsumedItemsAreLeaves(inventory, CaseId, KeyId));
    }

    [Fact]
    public void Persisted_prepared_retry_rechecks_consumed_inputs_are_leaves_before_mutation()
    {
        var profile = new PmcData
        {
            Inventory = new BotBaseInventory
            {
                Items =
                [
                    new Item { Id = CaseId, Template = ModConstants.CaseTemplateId },
                    new Item { Id = KeyId, Template = ModConstants.KeyTemplateId },
                    new Item
                    {
                        Id = AttachedItemId,
                        Template = "333333333333333333333333",
                        ParentId = CaseId.ToString(),
                        SlotId = "main"
                    }
                ]
            },
            InsuredItems = []
        };
        var response = new ItemEventRouterResponse();
        var context = new OpeningContext(profile, response, ProfileId);
        var inventory = new SptOpeningInventory(null!, null!, null!);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            inventory.ApplyPrepared(context, PreparedRecord()));

        Assert.Equal("BR-12 case and key instances must not contain attached items.", exception.Message);
        Assert.Equal(new[] { CaseId, KeyId, AttachedItemId }, profile.Inventory.Items.Select(item => item.Id));
        Assert.Null(response.ProfileChanges);
    }

    [Fact]
    public void Persisted_relay_retry_rechecks_the_reserved_key_is_a_leaf_before_mutation()
    {
        var stake = new Item
        {
            Id = RewardId,
            Template = "eeeeeeeeeeeeeeeeeeeeeeee",
            ParentId = StashId.ToString(),
            SlotId = "hideout"
        };
        var profile = new PmcData
        {
            Inventory = new BotBaseInventory
            {
                Stash = StashId,
                SortingTable = SortingTableId,
                Items =
                [
                    stake,
                    new Item
                    {
                        Id = KeyId,
                        Template = ModConstants.KeyTemplateId,
                        ParentId = StashId.ToString(),
                        SlotId = "hideout"
                    },
                    new Item
                    {
                        Id = AttachedItemId,
                        Template = "333333333333333333333333",
                        ParentId = KeyId.ToString(),
                        SlotId = "main"
                    }
                ]
            },
            InsuredItems = []
        };
        var prepared = new RelaySettlementRecord(
            CaseId,
            RewardId,
            [RewardId],
            "reward",
            RewardRarity.ScavGrade,
            1,
            RelayRecordAction.Relay,
            KeyId,
            RelayOutcome.Confiscated,
            null,
            [],
            0,
            RelayRules.MeterAfterConfiscation(0, 1),
            false,
            false,
            DateTimeOffset.UnixEpoch,
            RelayRecordStatus.Prepared,
            null,
            [stake]);
        var response = new ItemEventRouterResponse();
        var context = new OpeningContext(profile, response, ProfileId);
        var inventory = new SptOpeningInventory(null!, null!, null!);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            inventory.ApplyPreparedRelay(context, prepared));

        Assert.Equal("BR-12 Relay Key instances must not contain attached items.", exception.Message);
        Assert.Equal(new[] { RewardId, KeyId, AttachedItemId }, profile.Inventory.Items.Select(item => item.Id));
        Assert.Null(response.ProfileChanges);
    }

    [Theory]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbb", "bbbbbbbbbbbbbbbbbbbbbbbb", "dddddddddddddddddddddddd")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbb", "cccccccccccccccccccccccc", "bbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbb", "cccccccccccccccccccccccc", "cccccccccccccccccccccccc")]
    public void Settlement_record_rejects_case_key_reward_id_collisions(
        string caseId,
        string keyId,
        string rewardId)
    {
        Assert.Throws<ArgumentException>(() => new CaseOpeningRecord(
            caseId,
            keyId,
            "reward",
            [new Item { Id = rewardId, Template = "eeeeeeeeeeeeeeeeeeeeeeee" }],
            DateTimeOffset.UtcNow,
            OpeningRecordStatus.Committed,
            DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("slot")]
    [InlineData("location")]
    [InlineData("upd")]
    [InlineData("extension")]
    public void Live_reward_validation_rejects_every_replay_relevant_divergence(string divergence)
    {
        var prepared = PreparedRecord();
        var responseItems = DetailedRewardTree(9, 4);
        var profileItems = DetailedRewardTree(9, 4);
        var actual = profileItems.Single(item => item.Id == RewardId);
        switch (divergence)
        {
            case "parent":
                actual.ParentId = "sorting-table";
                break;
            case "slot":
                actual.SlotId = "different-slot";
                break;
            case "location":
                Assert.IsType<ItemLocation>(actual.Location).X = 99;
                break;
            case "upd":
                actual.Upd!.StackObjectsCount = 2;
                break;
            case "extension":
                Assert.IsType<ItemLocation>(actual.ExtensionData!["layout"]).Y = 99;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(divergence), divergence, null);
        }

        Assert.Throws<InvalidOperationException>(() =>
            SptOpeningInventory.ReconcileAppliedRecord(prepared, responseItems, profileItems));
    }

    [Fact]
    public void Live_reward_reconciliation_accepts_recomputed_placement_and_records_the_exact_live_payload()
    {
        var prepared = PreparedRecord();
        var responseItems = DetailedRewardTree(9, 4);
        var profileItems = DetailedRewardTree(9, 4);
        profileItems.Reverse();

        var reconciled = SptOpeningInventory.ReconcileAppliedRecord(prepared, responseItems, profileItems);

        Assert.Equal(OpeningRecordStatus.Prepared, reconciled.Status);
        Assert.Equal(prepared.CaseId, reconciled.CaseId);
        Assert.Equal(prepared.KeyId, reconciled.KeyId);
        Assert.Equal(prepared.RewardId, reconciled.RewardId);
        Assert.Equal(prepared.PreparedAtUtc, reconciled.PreparedAtUtc);
        AssertRewardPayload(reconciled.RewardItems, 9, 4);

        Assert.IsType<ItemLocation>(responseItems.Single(item => item.Id == RewardId).Location).X = 99;
        AssertRewardPayload(reconciled.RewardItems, 9, 4);
    }

    [Fact]
    public void Committed_replay_uses_the_reconciled_live_payload()
    {
        var prepared = PreparedRecord();
        var reconciled = SptOpeningInventory.ReconcileAppliedRecord(
            prepared,
            DetailedRewardTree(9, 4),
            DetailedRewardTree(9, 4));
        var committed = reconciled.Commit(DateTimeOffset.UnixEpoch.AddSeconds(1));
        var response = new ItemEventRouterResponse();

        SptResponseChanges.Replay(response, ProfileId, committed);

        AssertRewardPayload(response.ProfileChanges[ProfileId].Items!.NewItems!, 9, 4);
    }

    [Theory]
    [MemberData(nameof(ReplayCollisionCases))]
    public void Replay_rejects_every_settlement_id_in_every_existing_change_collection(
        string collisionId,
        ExistingChangeCollection collection)
    {
        var record = CommittedRecord();
        var response = new ItemEventRouterResponse();
        var changes = SptResponseChanges.GetOrCreate(response, ProfileId);
        AddCollision(changes, collection, collisionId);
        var originalNewItems = changes.NewItems!.ToArray();
        var originalChangedItems = changes.ChangedItems!.ToArray();
        var originalDeletedItems = changes.DeletedItems!.ToArray();

        Assert.Throws<InvalidOperationException>(() => SptResponseChanges.Replay(response, ProfileId, record));

        Assert.Equal(originalNewItems, changes.NewItems);
        Assert.Equal(originalChangedItems, changes.ChangedItems);
        Assert.Equal(originalDeletedItems, changes.DeletedItems);
    }

    public static IEnumerable<object[]> ReplayCollisionCases()
    {
        foreach (var settlementId in new[] { CaseId, KeyId, RewardId })
        {
            foreach (var collection in Enum.GetValues<ExistingChangeCollection>())
            {
                yield return new object[] { settlementId.ToString(), collection };
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_journal_save_poisons_same_process_retries(bool canceled)
    {
        var profileId = canceled ? CanceledSaveProfileId : FailedSaveProfileId;
        var loadCalls = 0;
        var saveCalls = 0;
        var phantomDocument = new SptCaseJournalDocument();
        var journal = new CaseOpeningJournal([PreparedRecord()]);
        var failingStore = new SptCaseJournal(
            (_, _) =>
            {
                loadCalls++;
                return Task.FromResult<SptCaseJournalDocument?>(phantomDocument);
            },
            (_, document, _) =>
            {
                saveCalls++;
                phantomDocument = document;
                return canceled
                    ? Task.FromCanceled(new CancellationToken(canceled: true))
                    : Task.FromException(new IOException("simulated durable write failure"));
            });

        var firstFailure = await Record.ExceptionAsync(() =>
            failingStore.SaveAsync(profileId, journal, CancellationToken.None).AsTask());

        Assert.NotNull(firstFailure);
        Assert.Equal(canceled, firstFailure is OperationCanceledException);
        var retryStore = new SptCaseJournal(
            (_, _) =>
            {
                loadCalls++;
                return Task.FromResult<SptCaseJournalDocument?>(phantomDocument);
            },
            (_, _, _) =>
            {
                saveCalls++;
                return Task.CompletedTask;
            });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            retryStore.LoadAsync(profileId, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            retryStore.SaveAsync(profileId, journal, CancellationToken.None).AsTask());
        Assert.Equal(0, loadCalls);
        Assert.Equal(1, saveCalls);
    }

    private static CaseOpeningRecord CommittedRecord() => new(
        CaseId,
        KeyId,
        "reward",
        [new Item { Id = RewardId, Template = "eeeeeeeeeeeeeeeeeeeeeeee" }],
        DateTimeOffset.UtcNow,
        OpeningRecordStatus.Committed,
        DateTimeOffset.UtcNow);

    private static CaseOpeningRecord PreparedRecord() => new(
        CaseId,
        KeyId,
        "reward",
        DetailedRewardTree(1, 2),
        DateTimeOffset.UtcNow,
        OpeningRecordStatus.Prepared,
        null);

    private static List<Item> DetailedRewardTree(int x, int y)
    {
        var item = new Item
        {
            Id = RewardId,
            Template = "eeeeeeeeeeeeeeeeeeeeeeee",
            ParentId = "stash",
            SlotId = "hideout",
            Location = new ItemLocation { X = x, Y = y },
            Upd = new Upd { StackObjectsCount = 1 }
        };
        item.ExtensionData!["layout"] = new ItemLocation { X = 3, Y = 4 };
        return
        [
            item,
            new Item
            {
                Id = ChildId,
                Template = "343434343434343434343434",
                ParentId = RewardId.ToString(),
                SlotId = "mod_scope"
            }
        ];
    }

    private static void AssertRewardPayload(IEnumerable<Item> items, int x, int y)
    {
        var byId = items.ToDictionary(item => item.Id);
        Assert.Equal(2, byId.Count);
        var root = byId[RewardId];
        Assert.Equal("stash", root.ParentId);
        Assert.Equal("hideout", root.SlotId);
        var location = Assert.IsType<ItemLocation>(root.Location);
        Assert.Equal(x, location.X);
        Assert.Equal(y, location.Y);
        Assert.Equal(1, root.Upd!.StackObjectsCount);
        var layout = Assert.IsType<ItemLocation>(root.ExtensionData!["layout"]);
        Assert.Equal(3, layout.X);
        Assert.Equal(4, layout.Y);

        var child = byId[ChildId];
        Assert.Equal(RewardId.ToString(), child.ParentId);
        Assert.Equal("mod_scope", child.SlotId);
    }

    private static void AddCollision(
        ItemChanges changes,
        ExistingChangeCollection collection,
        MongoId collisionId)
    {
        switch (collection)
        {
            case ExistingChangeCollection.New:
                changes.NewItems!.Add(new Item { Id = collisionId, Template = "eeeeeeeeeeeeeeeeeeeeeeee" });
                break;
            case ExistingChangeCollection.Changed:
                changes.ChangedItems!.Add(new Item { Id = collisionId, Template = "eeeeeeeeeeeeeeeeeeeeeeee" });
                break;
            case ExistingChangeCollection.Deleted:
                changes.DeletedItems!.Add(new DeletedItem { Id = collisionId });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(collection), collection, null);
        }
    }

    public enum ExistingChangeCollection
    {
        New,
        Changed,
        Deleted
    }
}
