using ContrabandCases.Server.Settlement;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Utils.Cloners;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class ManifestClaimInventoryTests
{
    private static readonly MongoId ProfileId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly MongoId StashId = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly MongoId SortingTableId = "cccccccccccccccccccccccc";
    private static readonly MongoId RootA = "111111111111111111111111";
    private static readonly MongoId RootB = "222222222222222222222222";
    private static readonly MongoId ChildA = "333333333333333333333333";
    private static readonly MongoId ChildB = "444444444444444444444444";
    private static readonly MongoId BaselineId = "555555555555555555555555";
    private static readonly DateTimeOffset PreparedAt =
        new(2026, 9, 2, 4, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Partition_preserves_each_tree_when_canonical_items_interleave_roots()
    {
        var trees = SptOpeningInventory.PartitionClaimTrees(MaterializedForest(), [RootA, RootB]);

        Assert.Equal(2, trees.Count);
        Assert.Equal(new[] { RootA, ChildA }, trees[0].Select(item => item.Id));
        Assert.Equal(new[] { RootB, ChildB }, trees[1].Select(item => item.Id));
    }

    [Fact]
    public void Aggregate_second_root_no_fit_returns_false_without_mutating_real_context()
    {
        var context = CreateContext([BaselineItem()]);
        var originalInventory = context.PmcData.Inventory!.Items!.Select(CloneItem).ToArray();
        var originalInsured = new InsuredItem { TId = RootA, ItemId = BaselineId };
        context.PmcData.InsuredItems!.Add(originalInsured);
        var adder = new FakeStashAdder { WarnWithoutMutationOnCall = 2 };
        var inventory = CreateInventory(adder);

        var result = inventory.TryPrepareClaim(
            context,
            MaterializedForest(),
            [RootA, RootB],
            PreparedAt,
            out var prepared);

        Assert.False(result);
        Assert.Null(prepared);
        Assert.Equal(2, adder.Calls);
        AssertItemsEqual(originalInventory, context.PmcData.Inventory.Items!);
        Assert.Same(originalInsured, Assert.Single(context.PmcData.InsuredItems!));
        Assert.Null(context.Response.Warnings);
        Assert.Null(context.Response.ProfileChanges);
    }

    [Fact]
    public void Non_capacity_warning_is_an_internal_failure_not_no_space()
    {
        var context = CreateContext([BaselineItem()]);
        var originalInventory = context.PmcData.Inventory!.Items!.Select(CloneItem).ToArray();
        var nonCapacityCode = Enum.GetValues<BackendErrorCodes>()
            .First(code => code != BackendErrorCodes.NotEnoughSpace);
        var inventory = CreateInventory(new FakeStashAdder
        {
            WarnWithoutMutationOnCall = 2,
            WarningCode = nonCapacityCode
        });

        Assert.Throws<InvalidOperationException>(() => inventory.TryPrepareClaim(
            context,
            MaterializedForest(),
            [RootA, RootB],
            PreparedAt,
            out _));

        AssertItemsEqual(originalInventory, context.PmcData.Inventory.Items!);
        Assert.Null(context.Response.Warnings);
        Assert.Null(context.Response.ProfileChanges);
    }

    [Fact]
    public void Preflight_accepts_only_spt_spawn_normalization_and_persists_located_output()
    {
        var context = CreateContext();
        var materialized = MaterializedForest();
        var ammoTemplates = new HashSet<MongoId>
        {
            materialized[1].Template,
            materialized[3].Template
        };
        var inventory = CreateInventory(
            new FakeStashAdder
            {
                SpawnedInSessionForItem = item => ammoTemplates.Contains(item.Template)
                    ? null
                    : false,
                UseSortingTableOnCall = 3
            },
            ammoTemplates.Contains);

        var result = inventory.TryPrepareClaim(
            context,
            materialized,
            [RootA, RootB],
            PreparedAt,
            out var prepared);

        Assert.True(result);
        Assert.NotNull(prepared);
        var preflightItems = prepared.Items;
        Assert.False(preflightItems.Single(item => item.Id == RootA).Upd!.SpawnedInSession);
        Assert.False(preflightItems.Single(item => item.Id == ChildA).Upd!.SpawnedInSession);
        Assert.Null(preflightItems.Single(item => item.Id == RootB).Upd!.SpawnedInSession);
        Assert.Null(preflightItems.Single(item => item.Id == ChildB).Upd!.SpawnedInSession);
        Assert.All(materialized, item => Assert.Null(item.Upd!.SpawnedInSession));
        AssertRootPlacement(preflightItems.Single(item => item.Id == RootA), 11);
        AssertRootPlacement(preflightItems.Single(item => item.Id == RootB), 12);

        var applied = inventory.ApplyPreparedClaim(context, prepared);

        Assert.True(applied.ProfileCommitStarted);
        var appliedItems = applied.Items;
        AssertSortingRootPlacement(appliedItems.Single(item => item.Id == RootA), 13);
        AssertRootPlacement(appliedItems.Single(item => item.Id == RootB), 14);
        Assert.False(appliedItems.Single(item => item.Id == RootA).Upd!.SpawnedInSession);
        Assert.Null(appliedItems.Single(item => item.Id == RootB).Upd!.SpawnedInSession);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Preflight_rejects_non_spt_grant_state_divergence(
        bool isAmmo,
        bool? spawnedInSession)
    {
        var context = CreateContext();
        var inventory = CreateInventory(
            new FakeStashAdder
            {
                SpawnedInSessionForItem = _ => spawnedInSession
            },
            _ => isAmmo);

        Assert.Throws<InvalidOperationException>(() => inventory.TryPrepareClaim(
            context,
            MaterializedForest(),
            [RootA, RootB],
            PreparedAt,
            out _));

        Assert.Empty(context.PmcData.Inventory!.Items!);
        Assert.Null(context.Response.ProfileChanges);
    }

    [Fact]
    public void Silent_preflight_failure_is_internal_not_no_space()
    {
        var context = CreateContext();
        var inventory = CreateInventory(new FakeStashAdder { SilentOnCall = 2 });

        Assert.Throws<InvalidOperationException>(() => inventory.TryPrepareClaim(
            context,
            MaterializedForest(),
            [RootA, RootB],
            PreparedAt,
            out _));

        Assert.Empty(context.PmcData.Inventory!.Items!);
        Assert.Null(context.Response.ProfileChanges);
    }

    [Fact]
    public void Preflight_exception_is_propagated_as_an_internal_failure()
    {
        var context = CreateContext();
        var inventory = CreateInventory(new FakeStashAdder { ThrowOnCall = 2 });

        var exception = Assert.Throws<IOException>(() => inventory.TryPrepareClaim(
            context,
            MaterializedForest(),
            [RootA, RootB],
            PreparedAt,
            out _));

        Assert.Equal("simulated AddItemToStash failure", exception.Message);
        Assert.Empty(context.PmcData.Inventory!.Items!);
        Assert.Null(context.Response.ProfileChanges);
    }

    [Fact]
    public void Apply_reorders_exact_live_items_and_reconciles_every_root_placement()
    {
        var context = CreateContext();
        var prepared = PreparedPayload(LocatedForest(10, 20), profileCommitStarted: false);
        var adder = new FakeStashAdder
        {
            PlacementBase = 30,
            ReverseProfileOrder = true,
            ReverseResponseOrder = true
        };
        var inventory = CreateInventory(adder);

        var applied = inventory.ApplyPreparedClaim(context, prepared);

        Assert.True(applied.ProfileCommitStarted);
        Assert.Equal(PreparedAt, applied.PreparedAtUtc);
        Assert.Equal(new[] { RootA, RootB, ChildA, ChildB }, applied.Items.Select(item => item.Id));
        AssertRootPlacement(applied.Items[0], 31);
        AssertRootPlacement(applied.Items[1], 32);
        AssertRootPlacement(prepared.Items[0], 10);
        AssertRootPlacement(prepared.Items[1], 20);
        Assert.Equal(
            new[] { ChildA, RootA, ChildB, RootB },
            context.Response.ProfileChanges[ProfileId].Items!.NewItems!.Select(item => item.Id));
        Assert.Equal(RewardPresence.Complete, inventory.InspectClaim(context, applied));
    }

    [Fact]
    public void Reconcile_extracts_the_exact_live_forest_in_semantic_order_without_mutation()
    {
        var prepared = PreparedPayload(LocatedForest(10, 20), profileCommitStarted: false);
        var live = LocatedForest(40, 50);
        live[1].ParentId = SortingTableId.ToString();
        live[1].SlotId = null;
        var context = CreateContext(live.AsEnumerable().Reverse());
        var originalInventoryOrder = context.PmcData.Inventory!.Items!.Select(item => item.Id).ToArray();
        var inventory = CreateInventory();

        var reconciled = inventory.ReconcileAppliedClaim(context, prepared);

        Assert.True(reconciled.ProfileCommitStarted);
        Assert.Equal(PreparedAt, reconciled.PreparedAtUtc);
        Assert.Equal(prepared.ExactItemIds, reconciled.ExactItemIds);
        Assert.Equal(prepared.RootIds, reconciled.RootIds);
        Assert.Equal(new[] { RootA, RootB, ChildA, ChildB }, reconciled.Items.Select(item => item.Id));
        AssertRootPlacement(reconciled.Items[0], 40);
        AssertSortingRootPlacement(reconciled.Items[1], 50);
        Assert.Equal(originalInventoryOrder, context.PmcData.Inventory!.Items!.Select(item => item.Id));
        Assert.Null(context.Response.ProfileChanges);
        Assert.Null(context.Response.Warnings);
    }

    [Theory]
    [InlineData(ReconcileCorruption.Absent)]
    [InlineData(ReconcileCorruption.Partial)]
    [InlineData(ReconcileCorruption.Duplicate)]
    [InlineData(ReconcileCorruption.Mutated)]
    [InlineData(ReconcileCorruption.InvalidPlacement)]
    public void Reconcile_rejects_non_exact_profile_evidence(ReconcileCorruption corruption)
    {
        var located = LocatedForest(10, 20);
        var prepared = PreparedPayload(located, profileCommitStarted: false);
        var live = located.Select(CloneItem).ToList();
        switch (corruption)
        {
            case ReconcileCorruption.Absent:
                live.Clear();
                break;
            case ReconcileCorruption.Partial:
                live.RemoveAll(item => item.Id == RootB || item.Id == ChildB);
                break;
            case ReconcileCorruption.Duplicate:
                live.Add(CloneItem(live[0]));
                break;
            case ReconcileCorruption.Mutated:
                live.Single(item => item.Id == ChildA).Upd!.StackObjectsCount = 2;
                break;
            case ReconcileCorruption.InvalidPlacement:
                live.Single(item => item.Id == RootA).SlotId = "not-hideout";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
        var context = CreateContext(live);
        var originalIds = context.PmcData.Inventory!.Items!.Select(item => item.Id).ToArray();

        Assert.Throws<InvalidOperationException>(() =>
            CreateInventory().ReconcileAppliedClaim(context, prepared));

        Assert.Equal(originalIds, context.PmcData.Inventory!.Items!.Select(item => item.Id));
        Assert.Null(context.Response.ProfileChanges);
        Assert.Null(context.Response.Warnings);
    }

    [Fact]
    public void Inspect_reports_absent_when_no_expected_id_exists()
    {
        var payload = PreparedPayload(LocatedForest(10, 20), profileCommitStarted: true);
        var context = CreateContext([
            new Item
            {
                Id = BaselineId,
                Template = "999999999999999999999999",
                ParentId = ChildA.ToString()
            }
        ]);

        Assert.Equal(RewardPresence.Absent, CreateInventory().InspectClaim(context, payload));
    }

    [Fact]
    public void Inspect_reports_complete_for_an_exact_reordered_forest()
    {
        var located = LocatedForest(10, 20);
        var payload = PreparedPayload(located, profileCommitStarted: true);
        var context = CreateContext(located.AsEnumerable().Reverse());

        Assert.Equal(RewardPresence.Complete, CreateInventory().InspectClaim(context, payload));
    }

    [Fact]
    public void Inspect_accepts_a_legal_sorting_table_root_placement()
    {
        var located = LocatedForest(10, 20);
        located[1].ParentId = SortingTableId.ToString();
        located[1].SlotId = null;
        var payload = PreparedPayload(located, profileCommitStarted: true);

        Assert.Equal(
            RewardPresence.Complete,
            CreateInventory().InspectClaim(CreateContext(located), payload));
    }

    [Fact]
    public void Inspect_rejects_a_parentless_live_root_when_only_sorting_table_exists()
    {
        var located = LocatedForest(10, 20);
        var payload = PreparedPayload(located, profileCommitStarted: true);
        var live = located.Select(CloneItem).ToList();
        live.Single(item => item.Id == RootA).ParentId = null;
        var context = CreateContext(live);
        context.PmcData.Inventory!.Stash = null;

        Assert.Equal(RewardPresence.Partial, CreateInventory().InspectClaim(context, payload));
    }

    [Theory]
    [InlineData(RootPlacementCorruption.InvalidStashSlot)]
    [InlineData(RootPlacementCorruption.InvalidSortingTableSlot)]
    [InlineData(RootPlacementCorruption.UntypedLocation)]
    [InlineData(RootPlacementCorruption.NegativeCoordinate)]
    [InlineData(RootPlacementCorruption.UndefinedRotation)]
    [InlineData(RootPlacementCorruption.ConflictingRotation)]
    public void Inspect_reports_partial_for_invalid_root_placement(RootPlacementCorruption corruption)
    {
        var located = LocatedForest(10, 20);
        var payload = PreparedPayload(located, profileCommitStarted: true);
        var live = located.Select(CloneItem).ToList();
        var root = live.Single(item => item.Id == RootA);
        switch (corruption)
        {
            case RootPlacementCorruption.InvalidStashSlot:
                root.SlotId = "not-hideout";
                break;
            case RootPlacementCorruption.InvalidSortingTableSlot:
                root.ParentId = SortingTableId.ToString();
                root.SlotId = "hideout";
                break;
            case RootPlacementCorruption.UntypedLocation:
                root.Location = "not-a-grid-location";
                break;
            case RootPlacementCorruption.NegativeCoordinate:
                Assert.IsType<ItemLocation>(root.Location).X = -1;
                break;
            case RootPlacementCorruption.UndefinedRotation:
                var undefinedRotation = Assert.IsType<ItemLocation>(root.Location);
                undefinedRotation.R = (ItemRotation)999;
                undefinedRotation.Rotation = null;
                break;
            case RootPlacementCorruption.ConflictingRotation:
                var conflictingRotation = Assert.IsType<ItemLocation>(root.Location);
                conflictingRotation.R = ItemRotation.Horizontal;
                conflictingRotation.Rotation = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }

        Assert.Equal(
            RewardPresence.Partial,
            CreateInventory().InspectClaim(CreateContext(live), payload));
    }

    [Fact]
    public void Inspect_reports_partial_when_one_root_tree_is_missing()
    {
        var located = LocatedForest(10, 20);
        var payload = PreparedPayload(located, profileCommitStarted: true);
        var context = CreateContext(located.Where(item => item.Id == RootA || item.Id == ChildA));

        Assert.Equal(RewardPresence.Partial, CreateInventory().InspectClaim(context, payload));
    }

    [Fact]
    public void Inspect_reports_partial_for_an_unexpected_descendant()
    {
        var located = LocatedForest(10, 20);
        var payload = PreparedPayload(located, profileCommitStarted: true);
        var live = located.Select(CloneItem).ToList();
        live.Add(new Item
        {
            Id = BaselineId,
            Template = "999999999999999999999999",
            ParentId = ChildA.ToString(),
            SlotId = "extra"
        });

        Assert.Equal(
            RewardPresence.Partial,
            CreateInventory().InspectClaim(CreateContext(live), payload));
    }

    [Fact]
    public void Inspect_reports_partial_when_stable_state_changed()
    {
        var located = LocatedForest(10, 20);
        var payload = PreparedPayload(located, profileCommitStarted: true);
        var live = located.Select(CloneItem).ToList();
        live.Single(item => item.Id == ChildA).Upd!.StackObjectsCount = 2;

        Assert.Equal(
            RewardPresence.Partial,
            CreateInventory().InspectClaim(CreateContext(live), payload));
    }

    [Fact]
    public void Inspect_reports_partial_when_live_inventory_contains_duplicate_ids()
    {
        var located = LocatedForest(10, 20);
        var payload = PreparedPayload(located, profileCommitStarted: true);
        var live = located.Select(CloneItem).ToList();
        live.Add(BaselineItem());
        live.Add(BaselineItem());

        Assert.Equal(
            RewardPresence.Partial,
            CreateInventory().InspectClaim(CreateContext(live), payload));
    }

    [Fact]
    public void Exact_forest_accepts_a_deep_reverse_ordered_parent_chain()
    {
        var expected = LinearForest(2_048);
        var actual = expected.Select(CloneItem).Reverse().ToArray();
        var profile = CreateContext().PmcData;

        SptOpeningInventory.EnsureExactClaimForest(
            expected,
            [expected[0].Id],
            actual,
            profile,
            "reverse-chain Claim test");
    }

    [Fact]
    public void Exact_forest_rejects_live_inventory_above_the_index_bound()
    {
        var expected = LinearForest(1);
        var actual = Enumerable.Range(1, SptOpeningInventory.MaxIndexedInventoryItemCount + 1)
            .Select(index => new Item
            {
                Id = IndexedId(index),
                Template = "aaaaaaaaaaaaaaaaaaaaaaa1"
            })
            .ToArray();
        var profile = CreateContext().PmcData;

        Assert.Throws<InvalidOperationException>(() =>
            SptOpeningInventory.EnsureExactClaimForest(
                expected,
                [expected[0].Id],
                actual,
                profile,
                "oversized Claim test"));
    }

    [Fact]
    public void Checkpoint_restores_first_tree_after_live_second_tree_failure()
    {
        var baseline = BaselineItem();
        var context = CreateContext([baseline]);
        context.PmcData.InsuredItems!.Add(new InsuredItem { TId = RootA, ItemId = BaselineId });
        var baselineChanges = SptResponseChanges.GetOrCreate(context.Response, ProfileId);
        baselineChanges.NewItems!.Add(CloneItem(baseline));
        var inventory = CreateInventory(new FakeStashAdder { WarnWithoutMutationOnCall = 2 });
        var checkpoint = inventory.Capture(context);
        var prepared = PreparedPayload(LocatedForest(10, 20), profileCommitStarted: false);

        Assert.Throws<InvalidOperationException>(() => inventory.ApplyPreparedClaim(context, prepared));
        Assert.Contains(context.PmcData.Inventory!.Items!, item => item.Id == RootA);
        Assert.NotNull(context.Response.Warnings);

        inventory.Restore(context, checkpoint);

        Assert.Equal(new[] { BaselineId }, context.PmcData.Inventory!.Items!.Select(item => item.Id));
        Assert.Equal(BaselineId, Assert.Single(context.PmcData.InsuredItems!).ItemId);
        Assert.Null(context.Response.Warnings);
        var restoredChanges = context.Response.ProfileChanges[ProfileId].Items!;
        Assert.Equal(new[] { BaselineId }, restoredChanges.NewItems!.Select(item => item.Id));
        Assert.Empty(restoredChanges.ChangedItems!);
        Assert.Empty(restoredChanges.DeletedItems);
    }

    [Fact]
    public void Checkpoint_removes_a_witness_added_after_capture_without_touching_other_extension_data()
    {
        var context = CreateContext();
        context.PmcData.ExtensionData = new Dictionary<string, object>
        {
            ["unrelated"] = "preserve"
        };
        var inventory = CreateInventory();
        var checkpoint = inventory.Capture(context);

        ManifestClaimCommitWitness.Stage(context.PmcData, WitnessToken('a'));
        inventory.Restore(context, checkpoint);

        Assert.False(context.PmcData.ExtensionData.ContainsKey(
            ManifestClaimCommitWitness.ExtensionDataKey));
        Assert.Equal("preserve", context.PmcData.ExtensionData["unrelated"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Checkpoint_restores_a_captured_witness_after_overwrite_or_removal(bool remove)
    {
        var context = CreateContext();
        var originalToken = WitnessToken('a');
        ManifestClaimCommitWitness.Stage(context.PmcData, originalToken);
        var inventory = CreateInventory();
        var checkpoint = inventory.Capture(context);
        if (remove)
        {
            context.PmcData.ExtensionData!.Remove(ManifestClaimCommitWitness.ExtensionDataKey);
        }
        else
        {
            ManifestClaimCommitWitness.Stage(context.PmcData, WitnessToken('b'));
        }

        inventory.Restore(context, checkpoint);

        Assert.Equal(
            ManifestClaimCommitWitnessInspection.Matching,
            ManifestClaimCommitWitness.Inspect(context.PmcData, originalToken));
        Assert.Equal(
            originalToken,
            Assert.IsType<string>(context.PmcData.ExtensionData![
                ManifestClaimCommitWitness.ExtensionDataKey]));
    }

    [Fact]
    public void Replay_adds_exact_new_items_once_and_is_idempotent()
    {
        var payload = PreparedPayload(LocatedForest(10, 20), profileCommitStarted: true);
        var context = CreateContext();
        var inventory = CreateInventory();

        inventory.ReplayClaim(context, payload);
        var changes = context.Response.ProfileChanges[ProfileId].Items!;
        changes.NewItems!.Reverse();
        inventory.ReplayClaim(context, payload);

        Assert.Equal(4, changes.NewItems.Count);
        Assert.Equal(payload.ExactItemIds.Order(), changes.NewItems.Select(item => item.Id).Order());
        Assert.Empty(changes.ChangedItems!);
        Assert.Empty(changes.DeletedItems);
    }

    [Theory]
    [InlineData(ReplayCollision.PartialNewItems)]
    [InlineData(ReplayCollision.ConflictingNewItem)]
    [InlineData(ReplayCollision.ChangedItem)]
    [InlineData(ReplayCollision.DeletedItem)]
    [InlineData(ReplayCollision.UnexpectedDescendant)]
    public void Replay_rejects_partial_or_conflicting_response_state(ReplayCollision collision)
    {
        var payload = PreparedPayload(LocatedForest(10, 20), profileCommitStarted: true);
        var context = CreateContext();
        var changes = SptResponseChanges.GetOrCreate(context.Response, ProfileId);
        switch (collision)
        {
            case ReplayCollision.PartialNewItems:
                changes.NewItems!.Add(payload.Items[0]);
                break;
            case ReplayCollision.ConflictingNewItem:
                changes.NewItems!.Add(payload.Items[0] with
                {
                    Template = (MongoId)"999999999999999999999999"
                });
                break;
            case ReplayCollision.ChangedItem:
                changes.ChangedItems!.Add(payload.Items[0]);
                break;
            case ReplayCollision.DeletedItem:
                changes.DeletedItems.Add(new DeletedItem { Id = RootA });
                break;
            case ReplayCollision.UnexpectedDescendant:
                changes.NewItems!.Add(new Item
                {
                    Id = BaselineId,
                    Template = "999999999999999999999999",
                    ParentId = ChildA.ToString(),
                    SlotId = "extra"
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(collision), collision, null);
        }

        var originalNewItems = changes.NewItems!.Select(CloneItem).ToArray();
        Assert.Throws<InvalidOperationException>(() => CreateInventory().ReplayClaim(context, payload));
        AssertItemsEqual(originalNewItems, changes.NewItems!);
    }

    private static SptOpeningInventory CreateInventory(
        FakeStashAdder? adder = null,
        Func<MongoId, bool>? isAmmoTemplate = null) =>
        SptOpeningInventory.CreateForTests(
            new TestCloner(),
            (adder ?? new FakeStashAdder()).Add,
            isAmmoTemplate);

    private static OpeningContext CreateContext(IEnumerable<Item>? items = null) =>
        new(
            new PmcData
            {
                Inventory = new BotBaseInventory
                {
                    Stash = StashId,
                    SortingTable = SortingTableId,
                    Items = items?.Select(CloneItem).ToList() ?? []
                },
                InsuredItems = []
            },
            new ItemEventRouterResponse(),
            ProfileId);

    private static ManifestClaimPreparedPayload PreparedPayload(
        IReadOnlyList<Item> items,
        bool profileCommitStarted) =>
        new(items, [RootA, RootB], profileCommitStarted, PreparedAt);

    private static List<Item> MaterializedForest() =>
    [
        new Item
        {
            Id = RootA,
            Template = "aaaaaaaaaaaaaaaaaaaaaaa1",
            Upd = new Upd { StackObjectsCount = 1 }
        },
        new Item
        {
            Id = RootB,
            Template = "aaaaaaaaaaaaaaaaaaaaaaa2",
            Upd = new Upd { StackObjectsCount = 1 }
        },
        new Item
        {
            Id = ChildA,
            Template = "aaaaaaaaaaaaaaaaaaaaaaa3",
            ParentId = RootA.ToString(),
            SlotId = "mod_a",
            Upd = new Upd { StackObjectsCount = 1 }
        },
        new Item
        {
            Id = ChildB,
            Template = "aaaaaaaaaaaaaaaaaaaaaaa4",
            ParentId = RootB.ToString(),
            SlotId = "mod_b",
            Upd = new Upd { StackObjectsCount = 1 }
        }
    ];

    private static List<Item> LocatedForest(int rootAX, int rootBX)
    {
        var items = MaterializedForest();
        LocateRoot(items[0], rootAX);
        LocateRoot(items[1], rootBX);
        return items;
    }

    private static List<Item> LinearForest(int count)
    {
        Assert.True(count > 0);
        var items = new List<Item>(count);
        for (var index = 1; index <= count; index++)
        {
            var item = new Item
            {
                Id = IndexedId(index),
                Template = "aaaaaaaaaaaaaaaaaaaaaaa1",
                ParentId = index == 1 ? null : IndexedId(index - 1).ToString(),
                SlotId = index == 1 ? null : "child",
                Upd = new Upd { StackObjectsCount = 1 }
            };
            items.Add(item);
        }

        LocateRoot(items[0], 0);
        return items;
    }

    private static MongoId IndexedId(int index) => index.ToString("x24");

    private static void LocateRoot(Item root, int x)
    {
        root.ParentId = StashId.ToString();
        root.SlotId = "hideout";
        root.Location = new ItemLocation
        {
            X = x,
            Y = 0,
            R = ItemRotation.Horizontal,
            Rotation = false
        };
    }

    private static Item BaselineItem() => new()
    {
        Id = BaselineId,
        Template = "999999999999999999999999",
        ParentId = StashId.ToString(),
        SlotId = "hideout",
        Location = new ItemLocation
        {
            X = 0,
            Y = 0,
            R = ItemRotation.Horizontal,
            Rotation = false
        }
    };

    private static void AssertRootPlacement(Item root, int expectedX)
    {
        Assert.Equal(StashId.ToString(), root.ParentId);
        Assert.Equal("hideout", root.SlotId);
        Assert.Equal(expectedX, Assert.IsType<ItemLocation>(root.Location).X);
    }

    private static void AssertSortingRootPlacement(Item root, int expectedX)
    {
        Assert.Equal(SortingTableId.ToString(), root.ParentId);
        Assert.Null(root.SlotId);
        Assert.Equal(expectedX, Assert.IsType<ItemLocation>(root.Location).X);
    }

    private static Item CloneItem(Item item) => CaseOpeningRecord.CloneItem(item);

    private static void AssertItemsEqual(IReadOnlyCollection<Item> expected, IReadOnlyCollection<Item> actual)
    {
        Assert.Equal(expected.Select(item => item.Id), actual.Select(item => item.Id));
        Assert.Equal(expected.Select(item => item.Template), actual.Select(item => item.Template));
        Assert.Equal(expected.Select(item => item.ParentId), actual.Select(item => item.ParentId));
        Assert.Equal(expected.Select(item => item.SlotId), actual.Select(item => item.SlotId));
    }

    public enum ReplayCollision
    {
        PartialNewItems,
        ConflictingNewItem,
        ChangedItem,
        DeletedItem,
        UnexpectedDescendant
    }

    public enum RootPlacementCorruption
    {
        InvalidStashSlot,
        InvalidSortingTableSlot,
        UntypedLocation,
        NegativeCoordinate,
        UndefinedRotation,
        ConflictingRotation
    }

    public enum ReconcileCorruption
    {
        Absent,
        Partial,
        Duplicate,
        Mutated,
        InvalidPlacement
    }

    private static string WitnessToken(char character) => "v1:" + new string(character, 64);

    private sealed class FakeStashAdder
    {
        public int Calls { get; private set; }
        public int PlacementBase { get; init; } = 10;
        public int? WarnWithoutMutationOnCall { get; init; }
        public BackendErrorCodes WarningCode { get; init; } = BackendErrorCodes.NotEnoughSpace;
        public int? SilentOnCall { get; init; }
        public int? ThrowOnCall { get; init; }
        public bool ReverseProfileOrder { get; init; }
        public bool ReverseResponseOrder { get; init; }
        public Func<Item, bool?>? SpawnedInSessionForItem { get; init; }
        public int? UseSortingTableOnCall { get; init; }

        public void Add(
            MongoId profileId,
            AddItemDirectRequest request,
            PmcData profile,
            ItemEventRouterResponse response)
        {
            Calls++;
            if (ThrowOnCall == Calls)
            {
                throw new IOException("simulated AddItemToStash failure");
            }
            if (SilentOnCall == Calls)
            {
                return;
            }
            if (WarnWithoutMutationOnCall == Calls)
            {
                response.Warnings = [new Warning
                {
                    ErrorMessage = "no room",
                    Code = WarningCode
                }];
                return;
            }

            var located = request.ItemWithModsToAdd!.Select(CloneItem).ToList();
            if (SpawnedInSessionForItem is not null)
            {
                foreach (var item in located)
                {
                    item.Upd ??= new Upd();
                    item.Upd.SpawnedInSession = SpawnedInSessionForItem(item);
                }
            }
            if (UseSortingTableOnCall == Calls)
            {
                located[0].ParentId = SortingTableId.ToString();
                located[0].Location = new ItemLocation
                {
                    X = PlacementBase + Calls,
                    Y = 0,
                    R = ItemRotation.Horizontal,
                    Rotation = false
                };
            }
            else
            {
                LocateRoot(located[0], PlacementBase + Calls);
            }
            var profileItems = ReverseProfileOrder ? located.AsEnumerable().Reverse() : located;
            profile.Inventory!.Items!.AddRange(profileItems.Select(CloneItem));
            var responseItems = ReverseResponseOrder ? located.AsEnumerable().Reverse() : located;
            SptResponseChanges.GetOrCreate(response, profileId).NewItems!
                .AddRange(responseItems.Select(CloneItem));
        }
    }

    private sealed class TestCloner : ICloner
    {
        public T? Clone<T>(T? value)
        {
            if (value is null)
            {
                return default!;
            }

            object clone = value switch
            {
                PmcData profile => CloneProfile(profile),
                BotBaseInventory inventory => CloneInventory(inventory),
                ItemEventRouterResponse response => CloneResponse(response),
                ItemChanges changes => CloneChanges(changes),
                List<InsuredItem> insured => insured.Select(CloneInsured).ToList(),
                List<Warning> warnings => warnings.Select(CloneWarning).ToList(),
                _ => throw new NotSupportedException($"Test cloner does not support {typeof(T).FullName}.")
            };
            return (T)clone;
        }

        private static PmcData CloneProfile(PmcData profile) => new()
        {
            Inventory = CloneInventory(profile.Inventory!),
            InsuredItems = (profile.InsuredItems ?? []).Select(CloneInsured).ToList()
        };

        private static BotBaseInventory CloneInventory(BotBaseInventory inventory) => new()
        {
            Items = (inventory.Items ?? []).Select(CloneItem).ToList(),
            Equipment = inventory.Equipment,
            Stash = inventory.Stash,
            SortingTable = inventory.SortingTable,
            QuestRaidItems = inventory.QuestRaidItems,
            QuestStashItems = inventory.QuestStashItems,
            HideoutCustomizationStashId = inventory.HideoutCustomizationStashId
        };

        private static ItemEventRouterResponse CloneResponse(ItemEventRouterResponse response) => new()
        {
            Warnings = response.Warnings?.Select(CloneWarning).ToList(),
            ProfileChanges = response.ProfileChanges?.ToDictionary(
                pair => pair.Key,
                pair => new ProfileChange
                {
                    Id = pair.Value.Id,
                    Items = pair.Value.Items is null ? null : CloneChanges(pair.Value.Items)
                })!
        };

        private static ItemChanges CloneChanges(ItemChanges changes) => new()
        {
            NewItems = changes.NewItems?.Select(CloneItem).ToList(),
            ChangedItems = changes.ChangedItems?.Select(CloneItem).ToList(),
            DeletedItems = changes.DeletedItems?.Select(item => new DeletedItem { Id = item.Id }).ToList() ?? []
        };

        private static InsuredItem CloneInsured(InsuredItem item) => new()
        {
            TId = item.TId,
            ItemId = item.ItemId
        };

        private static Warning CloneWarning(Warning warning) => new()
        {
            Index = warning.Index,
            ErrorMessage = warning.ErrorMessage,
            Code = warning.Code,
            Data = warning.Data
        };
    }
}
