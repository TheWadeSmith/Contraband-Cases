using System.Collections.ObjectModel;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;

namespace ContrabandCases.Client.Opening;

public sealed class InventorySnapshotException : InvalidOperationException
{
    public InventorySnapshotException(string message)
        : base(message)
    {
    }

    public InventorySnapshotException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class InventorySnapshotNode
{
    public InventorySnapshotNode(
        string itemId,
        string templateId,
        string? parentItemId,
        int stackMaxSize)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new InventorySnapshotException("Snapshot item IDs cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new InventorySnapshotException("Snapshot template IDs cannot be empty.");
        }

        if (parentItemId is not null && string.IsNullOrWhiteSpace(parentItemId))
        {
            throw new InventorySnapshotException("Snapshot parent IDs must be null or non-empty.");
        }

        if (stackMaxSize < 1)
        {
            throw new InventorySnapshotException("Snapshot stack limits must be positive.");
        }

        ItemId = itemId;
        TemplateId = templateId;
        ParentItemId = parentItemId;
        StackMaxSize = stackMaxSize;
    }

    public string ItemId { get; }

    public string TemplateId { get; }

    public string? ParentItemId { get; }

    public int StackMaxSize { get; }

    internal bool HasSameState(InventorySnapshotNode other)
    {
        return string.Equals(ItemId, other.ItemId, StringComparison.Ordinal) &&
            string.Equals(TemplateId, other.TemplateId, StringComparison.Ordinal) &&
            string.Equals(ParentItemId, other.ParentItemId, StringComparison.Ordinal) &&
            StackMaxSize == other.StackMaxSize;
    }
}

public sealed class InventorySnapshot
{
    public InventorySnapshot(string profileId, IEnumerable<InventorySnapshotNode> nodes)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new InventorySnapshotException("A profile ID is required for an inventory snapshot.");
        }

        if (nodes is null)
        {
            throw new ArgumentNullException(nameof(nodes));
        }

        var byId = new Dictionary<string, InventorySnapshotNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is null)
            {
                throw new InventorySnapshotException("Inventory snapshots cannot contain null nodes.");
            }

            if (!byId.TryAdd(node.ItemId, node))
            {
                throw new InventorySnapshotException("Inventory snapshots cannot contain duplicate item IDs.");
            }
        }

        ValidateGraph(byId);

        var orderedNodes = byId.Values
            .OrderBy(node => node.ItemId, StringComparer.Ordinal)
            .ToArray();
        ProfileId = profileId;
        Nodes = new ReadOnlyCollection<InventorySnapshotNode>(orderedNodes);
        ItemsById = new ReadOnlyDictionary<string, InventorySnapshotNode>(byId);
    }

    public string ProfileId { get; }

    public IReadOnlyList<InventorySnapshotNode> Nodes { get; }

    public IReadOnlyDictionary<string, InventorySnapshotNode> ItemsById { get; }

    private static void ValidateGraph(IReadOnlyDictionary<string, InventorySnapshotNode> byId)
    {
        foreach (var node in byId.Values)
        {
            if (node.ParentItemId is not null && !byId.ContainsKey(node.ParentItemId))
            {
                throw new InventorySnapshotException("Inventory snapshot contains an orphaned item.");
            }
        }

        foreach (var node in byId.Values)
        {
            var path = new HashSet<string>(StringComparer.Ordinal);
            var cursor = node;
            while (cursor.ParentItemId is not null)
            {
                if (!path.Add(cursor.ItemId))
                {
                    throw new InventorySnapshotException("Inventory snapshot contains a parent cycle.");
                }

                cursor = byId[cursor.ParentItemId];
            }
        }
    }
}

public sealed class OpeningInventoryBaseline
{
    private OpeningInventoryBaseline(
        InventorySnapshot snapshot,
        string caseItemId,
        IReadOnlyList<string> matchingKeyItemIds)
    {
        Snapshot = snapshot;
        CaseItemId = caseItemId;
        MatchingKeyItemIds = matchingKeyItemIds;
        ExpectedKeyItemId = matchingKeyItemIds[0];
    }

    public InventorySnapshot Snapshot { get; }

    public string ProfileId => Snapshot.ProfileId;

    public string CaseItemId { get; }

    public string ExpectedKeyItemId { get; }

    public IReadOnlyList<string> MatchingKeyItemIds { get; }

    public static OpeningInventoryBaseline Capture(InventorySnapshot snapshot, string caseItemId)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (string.IsNullOrWhiteSpace(caseItemId))
        {
            throw new InventorySnapshotException("A target case item ID is required.");
        }

        if (!snapshot.ItemsById.TryGetValue(caseItemId, out var caseNode) ||
            !string.Equals(caseNode.TemplateId, ModConstants.CaseTemplateId, StringComparison.Ordinal))
        {
            throw new InventorySnapshotException("The target case is missing or has the wrong template.");
        }

        var matchingKeyItemIds = snapshot.Nodes
            .Where(node => string.Equals(node.TemplateId, ModConstants.KeyTemplateId, StringComparison.Ordinal))
            .Select(node => node.ItemId)
            .OrderBy(itemId => itemId, StringComparer.Ordinal)
            .ToArray();
        if (matchingKeyItemIds.Length == 0)
        {
            throw new InventorySnapshotException("No matching key exists in the captured inventory.");
        }

        return new OpeningInventoryBaseline(
            snapshot,
            caseItemId,
            new ReadOnlyCollection<string>(matchingKeyItemIds));
    }

    public static OpeningInventoryBaseline CaptureForDispatch(
        string expectedProfileId,
        InventorySnapshot currentSnapshot,
        string caseItemId)
    {
        if (string.IsNullOrWhiteSpace(expectedProfileId))
        {
            throw new InventorySnapshotException("An expected authenticated profile ID is required.");
        }

        if (currentSnapshot is null)
        {
            throw new ArgumentNullException(nameof(currentSnapshot));
        }

        if (!string.Equals(expectedProfileId, currentSnapshot.ProfileId, StringComparison.Ordinal))
        {
            throw new InventorySnapshotException("The authenticated profile changed before the opening was enqueued.");
        }

        return Capture(currentSnapshot, caseItemId);
    }
}

public sealed class CommittedRewardMatch
{
    internal CommittedRewardMatch(
        string rootItemId,
        ValidatedReward reward,
        RewardFingerprint fingerprint)
    {
        RootItemId = rootItemId;
        Reward = reward;
        Fingerprint = fingerprint;
    }

    public string RootItemId { get; }

    public ValidatedReward Reward { get; }

    public RewardFingerprint Fingerprint { get; }
}

public static class InventorySnapshotReconciler
{
    public static CommittedRewardMatch Reconcile(
        OpeningInventoryBaseline before,
        InventorySnapshot after,
        IReadOnlyList<ValidatedReward> catalog)
    {
        if (before is null)
        {
            throw new ArgumentNullException(nameof(before));
        }

        if (after is null)
        {
            throw new ArgumentNullException(nameof(after));
        }

        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        if (!string.Equals(before.ProfileId, after.ProfileId, StringComparison.Ordinal))
        {
            throw new InventorySnapshotException("The authenticated profile changed during the operation.");
        }

        var beforeItems = before.Snapshot.ItemsById;
        var afterItems = after.ItemsById;
        var removedItemIds = beforeItems.Keys
            .Where(itemId => !afterItems.ContainsKey(itemId))
            .ToHashSet(StringComparer.Ordinal);
        if (removedItemIds.Count != 2 ||
            !removedItemIds.Contains(before.CaseItemId) ||
            !removedItemIds.Contains(before.ExpectedKeyItemId))
        {
            throw new InventorySnapshotException("The operation did not remove exactly the captured case and expected key.");
        }

        var removedMatchingKeyIds = before.MatchingKeyItemIds
            .Where(itemId => !afterItems.ContainsKey(itemId))
            .ToArray();
        if (removedMatchingKeyIds.Length != 1 ||
            !string.Equals(removedMatchingKeyIds[0], before.ExpectedKeyItemId, StringComparison.Ordinal))
        {
            throw new InventorySnapshotException("The operation removed the wrong matching key set.");
        }

        foreach (var beforeNode in before.Snapshot.Nodes)
        {
            if (afterItems.TryGetValue(beforeNode.ItemId, out var afterNode) &&
                !beforeNode.HasSameState(afterNode))
            {
                throw new InventorySnapshotException("A retained inventory item changed during reconciliation.");
            }
        }

        var newItemIds = afterItems.Keys
            .Where(itemId => !beforeItems.ContainsKey(itemId))
            .ToHashSet(StringComparer.Ordinal);
        if (newItemIds.Count == 0)
        {
            throw new InventorySnapshotException("The operation did not add a reward tree.");
        }

        var newNodes = after.Nodes
            .Where(node => newItemIds.Contains(node.ItemId))
            .ToArray();
        foreach (var node in newNodes)
        {
            if (node.ParentItemId is not null && !newItemIds.Contains(node.ParentItemId))
            {
                throw new InventorySnapshotException("A new item is attached beneath a pre-existing item.");
            }
        }

        var roots = newNodes
            .Where(node => node.ParentItemId is null)
            .ToArray();
        if (roots.Length != 1)
        {
            throw new InventorySnapshotException("The operation must add exactly one reward root.");
        }

        var root = roots[0];
        if (root.StackMaxSize != 1)
        {
            throw new InventorySnapshotException("The committed reward root must be non-stackable.");
        }

        RequireOneConnectedNewTree(root.ItemId, newNodes, afterItems, newItemIds);
        RejectOldDescendantsOfNewTree(root.ItemId, after.Nodes, afterItems, newItemIds);

        RewardFingerprint fingerprint;
        try
        {
            fingerprint = RewardFingerprint.FromPreset(new RewardPresetTree(
                newNodes.Select(node => new RewardPresetItem(
                    node.ItemId,
                    node.TemplateId,
                    node.ParentItemId))));
        }
        catch (RewardCatalogValidationException exception)
        {
            throw new InventorySnapshotException("The committed reward graph is malformed.", exception);
        }

        if (catalog.Any(reward => reward is null))
        {
            throw new InventorySnapshotException("The validated reward catalog contains a null entry.");
        }

        var matches = catalog
            .Where(reward => reward.Fingerprint.Equals(fingerprint))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InventorySnapshotException("The committed reward fingerprint must match exactly one catalog reward.");
        }

        return new CommittedRewardMatch(root.ItemId, matches[0], fingerprint);
    }

    private static void RequireOneConnectedNewTree(
        string rootItemId,
        IEnumerable<InventorySnapshotNode> newNodes,
        IReadOnlyDictionary<string, InventorySnapshotNode> afterItems,
        HashSet<string> newItemIds)
    {
        foreach (var node in newNodes)
        {
            var path = new HashSet<string>(StringComparer.Ordinal);
            var cursor = node;
            while (cursor.ParentItemId is not null)
            {
                if (!path.Add(cursor.ItemId) || !newItemIds.Contains(cursor.ParentItemId))
                {
                    throw new InventorySnapshotException("The new reward graph is cyclic or disconnected.");
                }

                cursor = afterItems[cursor.ParentItemId];
            }

            if (!string.Equals(cursor.ItemId, rootItemId, StringComparison.Ordinal))
            {
                throw new InventorySnapshotException("The new reward graph is disconnected from its root.");
            }
        }
    }

    private static void RejectOldDescendantsOfNewTree(
        string rootItemId,
        IEnumerable<InventorySnapshotNode> afterNodes,
        IReadOnlyDictionary<string, InventorySnapshotNode> afterItems,
        HashSet<string> newItemIds)
    {
        foreach (var node in afterNodes)
        {
            if (newItemIds.Contains(node.ItemId))
            {
                continue;
            }

            var cursor = node;
            while (cursor.ParentItemId is not null)
            {
                if (string.Equals(cursor.ParentItemId, rootItemId, StringComparison.Ordinal))
                {
                    throw new InventorySnapshotException("A pre-existing item is attached beneath the new reward root.");
                }

                cursor = afterItems[cursor.ParentItemId];
            }
        }
    }
}
