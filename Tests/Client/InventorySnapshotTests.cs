using ContrabandCases.Client.Opening;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class InventorySnapshotTests
{
    private const string ProfileId = "profile";
    private const string CaseId = "case";
    private const string LowestKeyId = "key-a";
    private const string OtherKeyId = "key-b";
    private const string ExistingRootId = "existing-root";
    private const string ExistingChildId = "existing-child";
    private const string RewardRootId = "reward-root";

    [Fact]
    public void Reconcile_returns_the_exact_committed_root_reward_and_fingerprint()
    {
        var before = CaptureBaseline();
        var reward = CreateReward("reward", "weapon", "attachment-z", "attachment-a", "attachment-a");
        var after = Snapshot(ProfileId,
            Node(OtherKeyId, ModConstants.KeyTemplateId),
            Node(ExistingRootId, "existing-template"),
            Node(ExistingChildId, "existing-child-template", ExistingRootId),
            Node(RewardRootId, "weapon"),
            Node("attachment-1", "attachment-z", RewardRootId),
            Node("attachment-2", "attachment-a", RewardRootId),
            Node("attachment-3", "attachment-a", RewardRootId));

        var result = InventorySnapshotReconciler.Reconcile(before, after, new[] { reward });

        Assert.Equal(RewardRootId, result.RootItemId);
        Assert.Same(reward, result.Reward);
        Assert.Equal("weapon", result.Fingerprint.RootTemplateId);
        Assert.Equal(new[] { "attachment-a", "attachment-a", "attachment-z" }, result.Fingerprint.ChildTemplateIds);
        Assert.Equal(CaseId, before.CaseItemId);
        Assert.Equal(LowestKeyId, before.ExpectedKeyItemId);
        Assert.Equal(new[] { LowestKeyId, OtherKeyId }, before.MatchingKeyItemIds);
    }

    [Fact]
    public void Reconcile_is_order_independent_deterministic_and_does_not_mutate_snapshots()
    {
        var beforeSnapshot = BeforeSnapshot();
        var before = OpeningInventoryBaseline.Capture(beforeSnapshot, CaseId);
        var reward = CreateReward("reward", "weapon", "attachment-a", "attachment-z");
        var afterNodes = new[]
        {
            Node(OtherKeyId, ModConstants.KeyTemplateId),
            Node(ExistingRootId, "existing-template"),
            Node(ExistingChildId, "existing-child-template", ExistingRootId),
            Node(RewardRootId, "weapon"),
            Node("attachment-z", "attachment-z", RewardRootId),
            Node("attachment-a", "attachment-a", RewardRootId)
        };
        var afterForward = Snapshot(ProfileId, afterNodes);
        var afterReverse = Snapshot(ProfileId, afterNodes.Reverse().ToArray());
        var beforeIds = beforeSnapshot.Nodes.Select(node => node.ItemId).ToArray();
        var afterIds = afterForward.Nodes.Select(node => node.ItemId).ToArray();

        var first = InventorySnapshotReconciler.Reconcile(before, afterForward, new[] { reward });
        var second = InventorySnapshotReconciler.Reconcile(before, afterReverse, new[] { reward });

        Assert.Equal(first.RootItemId, second.RootItemId);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(beforeIds, beforeSnapshot.Nodes.Select(node => node.ItemId));
        Assert.Equal(afterIds, afterForward.Nodes.Select(node => node.ItemId));
    }

    [Fact]
    public void Snapshot_rejects_duplicate_item_ids()
    {
        Assert.Throws<InventorySnapshotException>(() => Snapshot(ProfileId,
            Node("same", "one"),
            Node("same", "two")));
    }

    [Fact]
    public void Snapshot_uses_ordinal_case_sensitive_item_identity()
    {
        var snapshot = Snapshot(ProfileId,
            Node("item", "one"),
            Node("ITEM", "two"));

        Assert.Equal(2, snapshot.Nodes.Count);
        Assert.True(snapshot.ItemsById.ContainsKey("item"));
        Assert.True(snapshot.ItemsById.ContainsKey("ITEM"));
    }

    [Fact]
    public void Snapshot_rejects_an_orphaned_parent()
    {
        Assert.Throws<InventorySnapshotException>(() => Snapshot(ProfileId,
            Node("orphan", "template", "missing-parent")));
    }

    [Fact]
    public void Snapshot_rejects_a_cycle()
    {
        Assert.Throws<InventorySnapshotException>(() => Snapshot(ProfileId,
            Node("cycle-a", "a", "cycle-b"),
            Node("cycle-b", "b", "cycle-a")));
    }

    [Fact]
    public void Capture_rejects_a_case_template_casing_variant()
    {
        var before = Snapshot(ProfileId,
            Node(CaseId, ModConstants.CaseTemplateId.ToUpperInvariant()),
            Node(LowestKeyId, ModConstants.KeyTemplateId));

        Assert.Throws<InventorySnapshotException>(() => OpeningInventoryBaseline.Capture(before, CaseId));
    }

    [Fact]
    public void Capture_rejects_when_no_matching_key_exists()
    {
        var before = Snapshot(ProfileId,
            Node(CaseId, ModConstants.CaseTemplateId),
            Node(LowestKeyId, "not-the-key-template"));

        Assert.Throws<InventorySnapshotException>(() => OpeningInventoryBaseline.Capture(before, CaseId));
    }

    [Fact]
    public void Capture_chooses_the_ordinal_lowest_matching_key_id()
    {
        var before = Snapshot(ProfileId,
            Node(CaseId, ModConstants.CaseTemplateId),
            Node("key-a", ModConstants.KeyTemplateId),
            Node("Key-a", ModConstants.KeyTemplateId),
            Node("ignored", ModConstants.KeyTemplateId.ToUpperInvariant()));

        var baseline = OpeningInventoryBaseline.Capture(before, CaseId);

        Assert.Equal("Key-a", baseline.ExpectedKeyItemId);
        Assert.Equal(new[] { "Key-a", "key-a" }, baseline.MatchingKeyItemIds);
    }

    [Fact]
    public void Dispatch_capture_rejects_a_profile_switch_before_enqueue()
    {
        var switched = Snapshot("different-profile",
            Node(CaseId, ModConstants.CaseTemplateId),
            Node(LowestKeyId, ModConstants.KeyTemplateId));

        Assert.Throws<InventorySnapshotException>(() =>
            OpeningInventoryBaseline.CaptureForDispatch(ProfileId, switched, CaseId));
    }

    [Fact]
    public void Dispatch_capture_uses_the_fresh_case_key_and_inventory_state()
    {
        var preliminary = OpeningInventoryBaseline.Capture(BeforeSnapshot(), CaseId);
        var dispatchSnapshot = Snapshot(ProfileId,
            Node(CaseId, ModConstants.CaseTemplateId),
            Node("key-0-added-after-confirmation", ModConstants.KeyTemplateId),
            Node(LowestKeyId, ModConstants.KeyTemplateId),
            Node(OtherKeyId, ModConstants.KeyTemplateId),
            Node(ExistingRootId, "existing-template"),
            Node(ExistingChildId, "existing-child-template", ExistingRootId));

        var dispatch = OpeningInventoryBaseline.CaptureForDispatch(ProfileId, dispatchSnapshot, CaseId);

        Assert.Equal(LowestKeyId, preliminary.ExpectedKeyItemId);
        Assert.Equal("key-0-added-after-confirmation", dispatch.ExpectedKeyItemId);
        Assert.Same(dispatchSnapshot, dispatch.Snapshot);
    }

    [Fact]
    public void Reconcile_rejects_zero_new_roots()
    {
        var after = Snapshot(ProfileId,
            Node(OtherKeyId, ModConstants.KeyTemplateId),
            Node(ExistingRootId, "existing-template"),
            Node(ExistingChildId, "existing-child-template", ExistingRootId));

        AssertRejected(CaptureBaseline(), after);
    }

    [Fact]
    public void Reconcile_rejects_two_new_roots()
    {
        var after = HappyAfter(
            Node(RewardRootId, "weapon"),
            Node("second-root", "unrelated"));

        AssertRejected(CaptureBaseline(), after);
    }

    [Fact]
    public void Reconcile_rejects_a_new_attachment_under_an_old_root()
    {
        var after = HappyAfter(
            Node("new-attachment", "attachment", ExistingRootId));

        AssertRejected(CaptureBaseline(), after);
    }

    [Fact]
    public void Reconcile_rejects_an_old_item_reparented_under_the_new_root()
    {
        var after = Snapshot(ProfileId,
            Node(OtherKeyId, ModConstants.KeyTemplateId),
            Node(ExistingRootId, "existing-template", RewardRootId),
            Node(ExistingChildId, "existing-child-template", ExistingRootId),
            Node(RewardRootId, "weapon"));

        AssertRejected(CaptureBaseline(), after, CreateReward("reward", "weapon", "existing-template", "existing-child-template"));
    }

    [Fact]
    public void Reconcile_rejects_a_partial_reward_tree_by_fingerprint()
    {
        var after = HappyAfter(
            Node(RewardRootId, "weapon"),
            Node("attachment-a", "attachment-a", RewardRootId));
        var completeReward = CreateReward("reward", "weapon", "attachment-a", "attachment-b");

        AssertRejected(CaptureBaseline(), after, completeReward);
    }

    [Fact]
    public void Reconcile_rejects_a_stackable_root()
    {
        var after = HappyAfter(Node(RewardRootId, "weapon", stackMaxSize: 2));

        AssertRejected(CaptureBaseline(), after, CreateReward("reward", "weapon"));
    }

    [Fact]
    public void Reconcile_rejects_an_unknown_fingerprint()
    {
        var after = HappyAfter(Node(RewardRootId, "unknown-weapon"));

        AssertRejected(CaptureBaseline(), after, CreateReward("known", "known-weapon"));
    }

    [Fact]
    public void Reconcile_rejects_multiple_catalog_matches()
    {
        var after = HappyAfter(Node(RewardRootId, "weapon"));
        var firstReward = CreateReward("first", "weapon");
        var secondReward = CreateReward("second", "weapon");

        Assert.Throws<InventorySnapshotException>(() =>
            InventorySnapshotReconciler.Reconcile(CaptureBaseline(), after, new[] { firstReward, secondReward }));
    }

    [Fact]
    public void Reconcile_rejects_when_the_target_case_remains()
    {
        var after = Snapshot(ProfileId,
            Node(CaseId, ModConstants.CaseTemplateId),
            Node(OtherKeyId, ModConstants.KeyTemplateId),
            Node(ExistingRootId, "existing-template"),
            Node(ExistingChildId, "existing-child-template", ExistingRootId),
            Node(RewardRootId, "weapon"));

        AssertRejected(CaptureBaseline(), after, CreateReward("reward", "weapon"));
    }

    [Fact]
    public void Reconcile_rejects_when_a_different_case_is_removed()
    {
        var before = CaptureBaseline(Node("other-case", ModConstants.CaseTemplateId));
        var after = Snapshot(ProfileId,
            Node(CaseId, ModConstants.CaseTemplateId),
            Node(OtherKeyId, ModConstants.KeyTemplateId),
            Node(ExistingRootId, "existing-template"),
            Node(ExistingChildId, "existing-child-template", ExistingRootId),
            Node(RewardRootId, "weapon"));

        AssertRejected(before, after, CreateReward("reward", "weapon"));
    }

    [Fact]
    public void Reconcile_rejects_an_additional_case_removal()
    {
        var before = CaptureBaseline(Node("other-case", ModConstants.CaseTemplateId));
        var after = HappyAfter(Node(RewardRootId, "weapon"));

        AssertRejected(before, after, CreateReward("reward", "weapon"));
    }

    [Fact]
    public void Reconcile_rejects_an_extra_unrelated_removal()
    {
        var before = CaptureBaseline(Node("unrelated", "unrelated-template"));
        var after = HappyAfter(Node(RewardRootId, "weapon"));

        AssertRejected(before, after, CreateReward("reward", "weapon"));
    }

    [Fact]
    public void Reconcile_rejects_when_no_key_is_removed()
    {
        var after = Snapshot(ProfileId,
            Node(LowestKeyId, ModConstants.KeyTemplateId),
            Node(OtherKeyId, ModConstants.KeyTemplateId),
            Node(ExistingRootId, "existing-template"),
            Node(ExistingChildId, "existing-child-template", ExistingRootId),
            Node(RewardRootId, "weapon"));

        AssertRejected(CaptureBaseline(), after, CreateReward("reward", "weapon"));
    }

    [Fact]
    public void Reconcile_rejects_when_a_non_lowest_key_is_removed()
    {
        var after = Snapshot(ProfileId,
            Node(LowestKeyId, ModConstants.KeyTemplateId),
            Node(ExistingRootId, "existing-template"),
            Node(ExistingChildId, "existing-child-template", ExistingRootId),
            Node(RewardRootId, "weapon"));

        AssertRejected(CaptureBaseline(), after, CreateReward("reward", "weapon"));
    }

    [Fact]
    public void Reconcile_rejects_when_multiple_matching_keys_are_removed()
    {
        var after = Snapshot(ProfileId,
            Node(ExistingRootId, "existing-template"),
            Node(ExistingChildId, "existing-child-template", ExistingRootId),
            Node(RewardRootId, "weapon"));

        AssertRejected(CaptureBaseline(), after, CreateReward("reward", "weapon"));
    }

    [Fact]
    public void Reconcile_rejects_a_profile_switch()
    {
        var after = Snapshot("different-profile",
            Node(OtherKeyId, ModConstants.KeyTemplateId),
            Node(ExistingRootId, "existing-template"),
            Node(ExistingChildId, "existing-child-template", ExistingRootId),
            Node(RewardRootId, "weapon"));

        AssertRejected(CaptureBaseline(), after, CreateReward("reward", "weapon"));
    }

    [Fact]
    public void Reconcile_rejects_a_retained_item_mutation()
    {
        var after = Snapshot(ProfileId,
            Node(OtherKeyId, ModConstants.KeyTemplateId),
            Node(ExistingRootId, "changed-template"),
            Node(ExistingChildId, "existing-child-template", ExistingRootId),
            Node(RewardRootId, "weapon"));

        AssertRejected(CaptureBaseline(), after, CreateReward("reward", "weapon"));
    }

    private static OpeningInventoryBaseline CaptureBaseline(params InventorySnapshotNode[] extraNodes)
    {
        var nodes = BeforeNodes().Concat(extraNodes).ToArray();
        return OpeningInventoryBaseline.Capture(Snapshot(ProfileId, nodes), CaseId);
    }

    private static InventorySnapshot BeforeSnapshot() => Snapshot(ProfileId, BeforeNodes());

    private static InventorySnapshotNode[] BeforeNodes() =>
    [
        Node(CaseId, ModConstants.CaseTemplateId),
        Node(OtherKeyId, ModConstants.KeyTemplateId),
        Node(LowestKeyId, ModConstants.KeyTemplateId),
        Node(ExistingRootId, "existing-template"),
        Node(ExistingChildId, "existing-child-template", ExistingRootId)
    ];

    private static InventorySnapshot HappyAfter(params InventorySnapshotNode[] newNodes)
    {
        return Snapshot(ProfileId, new[]
        {
            Node(OtherKeyId, ModConstants.KeyTemplateId),
            Node(ExistingRootId, "existing-template"),
            Node(ExistingChildId, "existing-child-template", ExistingRootId)
        }.Concat(newNodes).ToArray());
    }

    private static InventorySnapshot Snapshot(string profileId, params InventorySnapshotNode[] nodes) =>
        new(profileId, nodes);

    private static InventorySnapshotNode Node(
        string itemId,
        string templateId,
        string? parentItemId = null,
        int stackMaxSize = 1) =>
        new(itemId, templateId, parentItemId, stackMaxSize);

    private static void AssertRejected(
        OpeningInventoryBaseline before,
        InventorySnapshot after,
        ValidatedReward? reward = null)
    {
        reward ??= CreateReward("reward", "weapon");
        Assert.Throws<InventorySnapshotException>(() =>
            InventorySnapshotReconciler.Reconcile(before, after, new[] { reward }));
    }

    private static ValidatedReward CreateReward(
        string id,
        string rootTemplateId,
        params string[] childTemplateIds)
    {
        var items = new List<RewardPresetItem>
        {
            new("catalog-root", rootTemplateId, null)
        };
        for (var index = 0; index < childTemplateIds.Length; index++)
        {
            items.Add(new RewardPresetItem(
                $"catalog-child-{index}",
                childTemplateIds[index],
                "catalog-root"));
        }

        var catalog = RewardCatalog.Create([
            new RewardDefinition(
                id,
                $"{id} display",
                rootTemplateId,
                $"{id}-preset",
                RewardRarity.ScavGrade,
                1d)
        ]);
        return Assert.Single(catalog.Validate(new FixedResolver(new RewardPresetTree(items))));
    }

    private sealed class FixedResolver : IRewardPresetResolver
    {
        private readonly RewardPresetTree _tree;

        public FixedResolver(RewardPresetTree tree)
        {
            _tree = tree;
        }

        public RewardPresetTree? Resolve(string presetId) => _tree;
    }
}
