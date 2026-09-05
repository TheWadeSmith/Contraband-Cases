using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class RelayStakeInventoryValidationTests
{
    private const string StashId = "111111111111111111111111";
    private const string SortingTableId = "222222222222222222222222";

    [Fact]
    public void Exact_awarded_tree_is_eligible_even_after_moving_the_root()
    {
        var tree = Tree();
        var moved = tree.Select(item => item.Id == tree[0].Id
            ? CaseOpeningRecord.CloneItem(item) with
            {
                ParentId = SortingTableId,
                SlotId = "sorting-table-slot",
                Location = new ItemLocation { X = 4, Y = 2 }
            }
            : CaseOpeningRecord.CloneItem(item)).ToArray();

        SptOpeningInventory.EnsureExactStakeTree(tree, moved, tree[0].Id, AllowedRootParents());
    }

    [Fact]
    public void Stripped_attachment_is_rejected()
    {
        var tree = Tree();

        Assert.Throws<InvalidOperationException>(() =>
            SptOpeningInventory.EnsureExactStakeTree(tree, [tree[0]], tree[0].Id, AllowedRootParents()));
    }

    [Fact]
    public void Detached_or_reparented_attachment_is_rejected()
    {
        var tree = Tree();
        var detached = new[] { tree[0], tree[1] with { ParentId = "stash" } };

        Assert.Throws<InvalidOperationException>(() =>
            SptOpeningInventory.EnsureExactStakeTree(tree, detached, tree[0].Id, AllowedRootParents()));
    }

    [Fact]
    public void Added_attachment_is_rejected()
    {
        var tree = Tree();
        var added = tree.Append(new Item
        {
            Id = new MongoId(),
            Template = new MongoId(),
            ParentId = tree[0].Id.ToString(),
            SlotId = "mod_scope"
        }).ToArray();

        Assert.Throws<InvalidOperationException>(() =>
            SptOpeningInventory.EnsureExactStakeTree(tree, added, tree[0].Id, AllowedRootParents()));
    }

    [Theory]
    [InlineData("upd")]
    [InlineData("desc")]
    [InlineData("extension")]
    [InlineData("location")]
    public void Stable_child_payload_mutation_is_rejected(string mutation)
    {
        var tree = Tree();
        var live = tree.Select(CaseOpeningRecord.CloneItem).ToArray();
        var child = live[1];
        switch (mutation)
        {
            case "upd":
                Assert.NotNull(child.Upd);
                child.Upd!.StackObjectsCount = 7;
                break;
            case "desc":
                live[1] = child with { Desc = "changed" };
                break;
            case "extension":
                child.ExtensionData!["marker"] = "changed";
                break;
            case "location":
                Assert.IsType<ItemLocation>(child.Location).X = 99;
                break;
            default:
                throw new InvalidOperationException();
        }

        Assert.Throws<InvalidOperationException>(() =>
            SptOpeningInventory.EnsureExactStakeTree(tree, live, tree[0].Id, AllowedRootParents()));
    }

    [Fact]
    public void Root_outside_profile_stash_or_sorting_table_is_rejected()
    {
        var tree = Tree();
        var live = tree.Select(item => item.Id == tree[0].Id
            ? CaseOpeningRecord.CloneItem(item) with { ParentId = "333333333333333333333333" }
            : CaseOpeningRecord.CloneItem(item)).ToArray();

        Assert.Throws<InvalidOperationException>(() =>
            SptOpeningInventory.EnsureExactStakeTree(tree, live, tree[0].Id, AllowedRootParents()));
    }

    [Fact]
    public void Recovery_evidence_is_partial_when_exact_output_payload_changed()
    {
        var tree = Tree();
        var live = tree.Select(CaseOpeningRecord.CloneItem).ToArray();
        Assert.NotNull(live[1].Upd);
        live[1].Upd!.StackObjectsCount = 3;

        var presence = SptOpeningInventory.InspectExactTreePresence(
            tree,
            live,
            tree[0].Id,
            AllowedRootParents());

        Assert.Equal(RewardPresence.Partial, presence);
    }

    [Fact]
    public void Recovery_evidence_is_complete_only_for_the_exact_tree()
    {
        var tree = Tree();

        Assert.Equal(
            RewardPresence.Complete,
            SptOpeningInventory.InspectExactTreePresence(
                tree,
                tree.Select(CaseOpeningRecord.CloneItem).ToArray(),
                tree[0].Id,
                AllowedRootParents()));
        Assert.Equal(
            RewardPresence.Absent,
            SptOpeningInventory.InspectExactTreePresence(
                tree,
                [],
                tree[0].Id,
                AllowedRootParents()));
    }

    [Fact]
    public void Relay_consumes_the_lexically_lowest_eligible_key()
    {
        var excluded = (MongoId)"000000000000000000000001";
        Item[] items =
        [
            new() { Id = (MongoId)"000000000000000000000003", Template = (MongoId)ModConstants.KeyTemplateId },
            new() { Id = excluded, Template = (MongoId)ModConstants.KeyTemplateId },
            new() { Id = (MongoId)"000000000000000000000002", Template = (MongoId)ModConstants.KeyTemplateId }
        ];

        var selected = SptOpeningInventory.SelectLowestRelayKey(items, new HashSet<MongoId> { excluded });

        Assert.Equal("000000000000000000000002", selected.Id.ToString());
    }

    private static Item[] Tree()
    {
        var root = new Item
        {
            Id = new MongoId(),
            Template = new MongoId(),
            ParentId = StashId,
            SlotId = "hideout",
            Location = new ItemLocation { X = 1, Y = 2 },
            Desc = "root-state",
            Upd = new Upd { StackObjectsCount = 1 }
        };
        var child = new Item
        {
            Id = new MongoId(),
            Template = new MongoId(),
            ParentId = root.Id.ToString(),
            SlotId = "mod_scope",
            Location = new ItemLocation { X = 3, Y = 4 },
            Desc = "child-state",
            Upd = new Upd { StackObjectsCount = 1 }
        };
        child.ExtensionData!["marker"] = "original";
        return [root, child];
    }

    private static IReadOnlySet<string> AllowedRootParents() =>
        new HashSet<string>(StringComparer.Ordinal) { StashId, SortingTableId };
}
