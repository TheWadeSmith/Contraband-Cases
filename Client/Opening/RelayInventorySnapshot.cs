using System.Collections.ObjectModel;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;

namespace ContrabandCases.Client.Opening;

public sealed class RelayInventoryBaseline
{
    private readonly HashSet<string> _stakeItemIds;

    private RelayInventoryBaseline(
        InventorySnapshot snapshot,
        string stakeRootId,
        IEnumerable<string> stakeItemIds,
        IReadOnlyList<string> matchingKeyItemIds)
    {
        Snapshot = snapshot;
        StakeRootId = stakeRootId;
        _stakeItemIds = stakeItemIds.ToHashSet(StringComparer.Ordinal);
        StakeItemIds = new ReadOnlyCollection<string>(
            _stakeItemIds.OrderBy(itemId => itemId, StringComparer.Ordinal).ToArray());
        MatchingKeyItemIds = matchingKeyItemIds;
        ExpectedKeyItemId = matchingKeyItemIds[0];
    }

    public InventorySnapshot Snapshot { get; }

    public string ProfileId => Snapshot.ProfileId;

    public string StakeRootId { get; }

    public IReadOnlyList<string> StakeItemIds { get; }

    public string ExpectedKeyItemId { get; }

    public IReadOnlyList<string> MatchingKeyItemIds { get; }

    internal bool ContainsStakeItem(string itemId) => _stakeItemIds.Contains(itemId);

    public static RelayInventoryBaseline Capture(InventorySnapshot snapshot, string stakeRootId)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        if (!RelaySnapshotEnvelope.IsMongoId(stakeRootId) ||
            !snapshot.ItemsById.ContainsKey(stakeRootId))
        {
            throw new InventorySnapshotException("The exact Relay stake root is unavailable.");
        }

        var stakeItemIds = snapshot.Nodes
            .Where(node => IsDescendantOrSelf(node, stakeRootId, snapshot.ItemsById))
            .Select(node => node.ItemId)
            .ToHashSet(StringComparer.Ordinal);
        if (!stakeItemIds.Contains(stakeRootId))
        {
            throw new InventorySnapshotException("The Relay stake tree is disconnected.");
        }

        var matchingKeys = snapshot.Nodes
            .Where(node =>
                string.Equals(node.TemplateId, ModConstants.KeyTemplateId, StringComparison.Ordinal) &&
                !stakeItemIds.Contains(node.ItemId))
            .Select(node => node.ItemId)
            .OrderBy(itemId => itemId, StringComparer.Ordinal)
            .ToArray();
        if (matchingKeys.Length == 0)
        {
            throw new InventorySnapshotException("No BR-12 Relay Key is locally available.");
        }

        return new RelayInventoryBaseline(
            snapshot,
            stakeRootId,
            stakeItemIds,
            new ReadOnlyCollection<string>(matchingKeys));
    }

    public static bool HasKey(InventorySnapshot snapshot) =>
        snapshot.Nodes.Any(node =>
            string.Equals(node.TemplateId, ModConstants.KeyTemplateId, StringComparison.Ordinal));

    private static bool IsDescendantOrSelf(
        InventorySnapshotNode node,
        string rootItemId,
        IReadOnlyDictionary<string, InventorySnapshotNode> byId)
    {
        var cursor = node;
        while (true)
        {
            if (string.Equals(cursor.ItemId, rootItemId, StringComparison.Ordinal))
            {
                return true;
            }

            if (cursor.ParentItemId is null)
            {
                return false;
            }

            cursor = byId[cursor.ParentItemId];
        }
    }
}

public static class RelayInventoryReconciler
{
    public static CommittedRewardMatch? Reconcile(
        RelayInventoryBaseline before,
        InventorySnapshot after,
        IReadOnlyList<ValidatedReward> catalog,
        RelaySnapshot decisionSnapshot,
        RelayReceipt receipt)
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
        if (decisionSnapshot is null)
        {
            throw new ArgumentNullException(nameof(decisionSnapshot));
        }
        if (receipt is null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }
        RelaySnapshotEnvelope.Validate(decisionSnapshot, before.StakeRootId);

        if (!string.Equals(before.ProfileId, after.ProfileId, StringComparison.Ordinal))
        {
            throw new InventorySnapshotException("The authenticated profile changed during Relay settlement.");
        }

        var status = decisionSnapshot.Status;
        var outcome = RelaySnapshotEnvelope.ParseOutcome(receipt);
        var expectedMeter = RelayRules.MeterAfterOutcome(
            status.RecoveryMeter,
            status.Stage,
            outcome);
        if (!string.Equals(receipt.Action, "Relay", StringComparison.Ordinal) ||
            !string.Equals(receipt.StakeRootId, before.StakeRootId, StringComparison.Ordinal) ||
            !string.Equals(receipt.PreviousRewardId, status.RewardId, StringComparison.Ordinal) ||
            !string.Equals(receipt.PreviousRarity, status.Rarity, StringComparison.Ordinal) ||
            !string.Equals(receipt.RarityLadderVersion, status.RarityLadderVersion, StringComparison.Ordinal) ||
            receipt.Stage != status.Stage ||
            receipt.GuaranteedUpgrade != status.GuaranteedUpgradeReady ||
            receipt.RecoveryMeter != expectedMeter)
        {
            throw new InventorySnapshotException("The Relay receipt does not match the displayed stake decision.");
        }

        var beforeItems = before.Snapshot.ItemsById;
        var afterItems = after.ItemsById;
        var removed = beforeItems.Keys
            .Where(itemId => !afterItems.ContainsKey(itemId))
            .ToHashSet(StringComparer.Ordinal);
        var expectedRemoved = before.StakeItemIds
            .Append(before.ExpectedKeyItemId)
            .ToHashSet(StringComparer.Ordinal);
        if (!removed.SetEquals(expectedRemoved))
        {
            throw new InventorySnapshotException("Relay did not remove exactly the displayed weapon tree and captured key.");
        }

        var removedMatchingKeys = before.MatchingKeyItemIds
            .Where(itemId => !afterItems.ContainsKey(itemId))
            .ToArray();
        if (removedMatchingKeys.Length != 1 ||
            !string.Equals(removedMatchingKeys[0], before.ExpectedKeyItemId, StringComparison.Ordinal))
        {
            throw new InventorySnapshotException("Relay removed the wrong local BR-12 key set.");
        }

        foreach (var node in before.Snapshot.Nodes)
        {
            if (afterItems.TryGetValue(node.ItemId, out var retained) && !node.HasSameState(retained))
            {
                throw new InventorySnapshotException("An unrelated retained inventory item changed during Relay.");
            }
        }

        var newItemIds = afterItems.Keys
            .Where(itemId => !beforeItems.ContainsKey(itemId))
            .ToHashSet(StringComparer.Ordinal);
        if (receipt.DeliveredToMessenger)
        {
            if (newItemIds.Count != 0)
                throw new InventorySnapshotException("A Messenger payout must not inject items into inventory.");
            return MatchMailedReward(catalog, decisionSnapshot, receipt, requirePublishedCandidate: true);
        }
        if (outcome == RelayOutcome.Confiscated)
        {
            if (newItemIds.Count != 0 || receipt.RewardId is not null ||
                receipt.RewardRootId is not null || receipt.Rarity is not null || !receipt.Terminal)
            {
                throw new InventorySnapshotException("A confiscated Relay unexpectedly added a reward.");
            }

            return null;
        }

        if (outcome is not (RelayOutcome.RarityUpgrade or RelayOutcome.SameRaritySidegrade) ||
            newItemIds.Count == 0)
        {
            throw new InventorySnapshotException("Relay did not add its receipt-declared reward tree.");
        }

        var newNodes = after.Nodes.Where(node => newItemIds.Contains(node.ItemId)).ToArray();
        if (newNodes.Any(node => node.ParentItemId is not null && !newItemIds.Contains(node.ParentItemId)))
        {
            throw new InventorySnapshotException("A Relay output is attached beneath a pre-existing item.");
        }

        var roots = newNodes.Where(node => node.ParentItemId is null).ToArray();
        if (roots.Length != 1 || roots[0].StackMaxSize != 1 ||
            !string.Equals(roots[0].ItemId, receipt.RewardRootId, StringComparison.Ordinal))
        {
            throw new InventorySnapshotException("The Relay output root does not match the authoritative receipt.");
        }

        RequireConnectedTree(roots[0].ItemId, newNodes, afterItems, newItemIds);
        RejectOldDescendants(roots[0].ItemId, after.Nodes, afterItems, newItemIds);
        var fingerprint = CreateFingerprint(newNodes);
        var matches = catalog
            .Where(reward => reward is not null && reward.Fingerprint.Equals(fingerprint))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InventorySnapshotException("The Relay output must match exactly one local catalog reward.");
        }

        var reward = matches[0];
        if (!string.Equals(reward.Id, receipt.RewardId, StringComparison.Ordinal) ||
            !string.Equals(reward.Rarity.ToString(), receipt.Rarity, StringComparison.Ordinal))
        {
            throw new InventorySnapshotException("The Relay output does not match the receipt reward identity.");
        }

        var candidates = outcome == RelayOutcome.RarityUpgrade
            ? decisionSnapshot.UpgradeCandidates
            : decisionSnapshot.SidegradeCandidates;
        if (!candidates.Any(candidate =>
                string.Equals(candidate.RewardId, reward.Id, StringComparison.Ordinal)))
        {
            throw new InventorySnapshotException("The Relay output was not in the exact displayed candidate pool.");
        }

        var expectedRarity = outcome == RelayOutcome.RarityUpgrade
            ? RelayRules.GetUpgradeRarity(
                ParseRarity(status.Rarity),
                ParseRarityLadderVersion(status.RarityLadderVersion))
            : ParseRarity(status.Rarity);
        if (reward.Rarity != expectedRarity)
        {
            throw new InventorySnapshotException("The Relay outcome did not make the advertised rarity transition.");
        }

        var expectedTerminal = outcome != RelayOutcome.RarityUpgrade ||
            receipt.Stage >= RelayRules.MaximumStage ||
            reward.Rarity == RewardRarity.BlackLabel;
        if (receipt.Terminal != expectedTerminal)
        {
            throw new InventorySnapshotException("The Relay receipt terminal flag contradicts the chain rules.");
        }

        return new CommittedRewardMatch(roots[0].ItemId, reward, fingerprint);
    }

    public static CommittedRewardMatch? ReconcileRecovered(
        string expectedProfileId,
        InventorySnapshot current,
        IReadOnlyList<ValidatedReward> catalog,
        RelaySnapshot preparedSnapshot,
        RelayReceipt receipt)
    {
        if (string.IsNullOrWhiteSpace(expectedProfileId))
        {
            throw new InventorySnapshotException(
                "An authenticated profile ID is required for Relay restart recovery.");
        }
        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }
        if (catalog is null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }
        if (preparedSnapshot is null)
        {
            throw new ArgumentNullException(nameof(preparedSnapshot));
        }
        if (receipt is null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }
        if (!string.Equals(expectedProfileId, current.ProfileId, StringComparison.Ordinal))
        {
            throw new InventorySnapshotException(
                "The authenticated profile changed during Relay restart recovery.");
        }

        var status = preparedSnapshot.Status;
        RelaySnapshotEnvelope.Validate(preparedSnapshot, status.StakeRootId);
        var recovery = RelaySnapshotRecovery.Decide(preparedSnapshot);
        var expectedAction = recovery switch
        {
            RelayPendingRecovery.ResumeSecure => "Secure",
            RelayPendingRecovery.ResumeRelay => "Relay",
            _ => throw new InventorySnapshotException(
                "Relay restart recovery requires an unresolved prepared action.")
        };
        var outcome = RelaySnapshotEnvelope.ParseOutcome(receipt);
        var expectedMeter = recovery == RelayPendingRecovery.ResumeSecure
            ? status.RecoveryMeter
            : RelayRules.MeterAfterOutcome(status.RecoveryMeter, status.Stage, outcome);
        if (!string.Equals(receipt.Action, expectedAction, StringComparison.Ordinal) ||
            !string.Equals(receipt.StakeRootId, status.StakeRootId, StringComparison.Ordinal) ||
            !string.Equals(receipt.PreviousRewardId, status.RewardId, StringComparison.Ordinal) ||
            !string.Equals(receipt.PreviousRarity, status.Rarity, StringComparison.Ordinal) ||
            !string.Equals(receipt.RarityLadderVersion, status.RarityLadderVersion, StringComparison.Ordinal) ||
            receipt.Stage != status.Stage ||
            receipt.RecoveryMeter != expectedMeter ||
            receipt.RecoveryMeterMaximum != RelayRules.MaximumRecoveryMeter ||
            receipt.GuaranteedUpgrade !=
                (recovery == RelayPendingRecovery.ResumeRelay && status.GuaranteedUpgradeReady))
        {
            throw new InventorySnapshotException(
                "The Relay receipt does not match the persisted restart-recovery action.");
        }

        if (recovery == RelayPendingRecovery.ResumeSecure)
        {
            if (outcome != RelayOutcome.Secured || !receipt.Terminal ||
                receipt.DeliveredToMessenger ||
                receipt.RewardId is not null || receipt.RewardRootId is not null ||
                receipt.Rarity is not null)
            {
                throw new InventorySnapshotException(
                    "Recovered Secure must leave the exact staked reward in inventory.");
            }

            return MatchExistingRewardTree(
                current,
                catalog,
                status.StakeRootId,
                status.RewardId,
                status.Rarity);
        }

        if (current.ItemsById.ContainsKey(status.StakeRootId))
        {
            throw new InventorySnapshotException(
                "Recovered Relay did not remove the persisted stake root.");
        }
        if (receipt.DeliveredToMessenger)
            return MatchMailedReward(catalog, preparedSnapshot, receipt, requirePublishedCandidate: false);
        if (outcome == RelayOutcome.Confiscated)
        {
            if (!receipt.Terminal || receipt.RewardId is not null ||
                receipt.RewardRootId is not null || receipt.Rarity is not null)
            {
                throw new InventorySnapshotException(
                    "Recovered confiscation unexpectedly declares an output reward.");
            }

            return null;
        }
        if (outcome is not (RelayOutcome.RarityUpgrade or RelayOutcome.SameRaritySidegrade) ||
            !RelaySnapshotEnvelope.IsMongoId(receipt.RewardRootId) ||
            string.IsNullOrWhiteSpace(receipt.RewardId) ||
            string.IsNullOrWhiteSpace(receipt.Rarity))
        {
            throw new InventorySnapshotException(
                "Recovered Relay did not declare a valid reward output.");
        }

        var match = MatchExistingRewardTree(
            current,
            catalog,
            receipt.RewardRootId!,
            receipt.RewardId!,
            receipt.Rarity!);
        var inputRarity = ParseRarity(status.Rarity);
        var expectedRarity = outcome == RelayOutcome.RarityUpgrade
            ? RelayRules.GetUpgradeRarity(
                inputRarity,
                ParseRarityLadderVersion(status.RarityLadderVersion))
            : inputRarity;
        if (match.Reward.Rarity != expectedRarity ||
            outcome == RelayOutcome.SameRaritySidegrade &&
            string.Equals(match.Reward.Id, status.RewardId, StringComparison.Ordinal))
        {
            throw new InventorySnapshotException(
                "Recovered Relay output contradicts its persisted rarity transition.");
        }

        var expectedTerminal = outcome != RelayOutcome.RarityUpgrade ||
            receipt.Stage >= RelayRules.MaximumStage ||
            match.Reward.Rarity == RewardRarity.BlackLabel;
        if (receipt.Terminal != expectedTerminal)
        {
            throw new InventorySnapshotException(
                "Recovered Relay terminal state contradicts the chain rules.");
        }

        return match;
    }

    private static CommittedRewardMatch MatchMailedReward(IReadOnlyList<ValidatedReward> catalog,
        RelaySnapshot decision, RelayReceipt receipt, bool requirePublishedCandidate)
    {
        var outcome = RelaySnapshotEnvelope.ParseOutcome(receipt);
        var status = decision.Status;
        if (outcome is not (RelayOutcome.RarityUpgrade or RelayOutcome.SameRaritySidegrade) ||
            !RelaySnapshotEnvelope.IsMongoId(receipt.RewardRootId))
            throw new InventorySnapshotException("A Messenger receipt must identify a winning reward.");
        var matches = catalog.Where(r => r.Id == receipt.RewardId && r.Rarity.ToString() == receipt.Rarity).ToArray();
        if (matches.Length != 1)
            throw new InventorySnapshotException("The Messenger receipt does not identify one catalog reward.");
        var reward = matches[0];
        var expectedRarity = outcome == RelayOutcome.RarityUpgrade
            ? RelayRules.GetUpgradeRarity(ParseRarity(status.Rarity), ParseRarityLadderVersion(status.RarityLadderVersion))
            : ParseRarity(status.Rarity);
        var candidates = outcome == RelayOutcome.RarityUpgrade ? decision.UpgradeCandidates : decision.SidegradeCandidates;
        if (reward.Rarity != expectedRarity || outcome == RelayOutcome.SameRaritySidegrade && reward.Id == status.RewardId ||
            requirePublishedCandidate && !candidates.Any(c => c.RewardId == reward.Id) ||
            receipt.Terminal != (outcome != RelayOutcome.RarityUpgrade || receipt.Stage >= RelayRules.MaximumStage || reward.Rarity == RewardRarity.BlackLabel))
            throw new InventorySnapshotException("The Messenger receipt contradicts the saved Relay decision.");
        // The authenticated committed receipt owns mail delivery. No inventory
        // tree is fabricated, and the controller offers no Relay until collection.
        return new CommittedRewardMatch(receipt.RewardRootId!, reward, reward.Fingerprint);
    }

    private static CommittedRewardMatch MatchExistingRewardTree(
        InventorySnapshot snapshot,
        IReadOnlyList<ValidatedReward> catalog,
        string rootItemId,
        string rewardId,
        string rarity)
    {
        if (!snapshot.ItemsById.TryGetValue(rootItemId, out var root) ||
            root.StackMaxSize != 1)
        {
            throw new InventorySnapshotException(
                "The receipt-declared Relay reward root is unavailable.");
        }

        var nodes = snapshot.Nodes
            .Where(node => IsDescendantOrSelf(node, rootItemId, snapshot.ItemsById))
            .ToArray();
        var fingerprint = CreateFingerprint(nodes, rootItemId);
        var matches = catalog
            .Where(reward => reward is not null && reward.Fingerprint.Equals(fingerprint))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InventorySnapshotException(
                "The recovered Relay reward must match exactly one local catalog reward.");
        }

        var reward = matches[0];
        if (!string.Equals(reward.Id, rewardId, StringComparison.Ordinal) ||
            !string.Equals(reward.Rarity.ToString(), rarity, StringComparison.Ordinal))
        {
            throw new InventorySnapshotException(
                "The recovered Relay reward does not match the authoritative receipt identity.");
        }

        return new CommittedRewardMatch(rootItemId, reward, fingerprint);
    }

    private static RewardFingerprint CreateFingerprint(
        IEnumerable<InventorySnapshotNode> nodes,
        string? normalizedRootId = null)
    {
        try
        {
            return RewardFingerprint.FromPreset(new RewardPresetTree(nodes.Select(node =>
                new RewardPresetItem(
                    node.ItemId,
                    node.TemplateId,
                    string.Equals(node.ItemId, normalizedRootId, StringComparison.Ordinal)
                        ? null
                        : node.ParentItemId))));
        }
        catch (RewardCatalogValidationException exception)
        {
            throw new InventorySnapshotException("The Relay output graph is malformed.", exception);
        }
    }

    private static bool IsDescendantOrSelf(
        InventorySnapshotNode node,
        string rootItemId,
        IReadOnlyDictionary<string, InventorySnapshotNode> byId)
    {
        var cursor = node;
        while (true)
        {
            if (string.Equals(cursor.ItemId, rootItemId, StringComparison.Ordinal))
            {
                return true;
            }
            if (cursor.ParentItemId is null)
            {
                return false;
            }

            cursor = byId[cursor.ParentItemId];
        }
    }

    private static RewardRarity ParseRarity(string value)
    {
        if (!Enum.TryParse<RewardRarity>(value, ignoreCase: false, out var rarity) ||
            !string.Equals(rarity.ToString(), value, StringComparison.Ordinal))
        {
            throw new InventorySnapshotException("Relay published an invalid rarity.");
        }

        return rarity;
    }

    private static RarityLadderVersion ParseRarityLadderVersion(string value)
    {
        if (!Enum.TryParse<RarityLadderVersion>(value, ignoreCase: false, out var rarityLadderVersion) ||
            !string.Equals(rarityLadderVersion.ToString(), value, StringComparison.Ordinal))
        {
            throw new InventorySnapshotException("Relay published an invalid rarity ladder version.");
        }

        return rarityLadderVersion;
    }

    private static void RequireConnectedTree(
        string rootItemId,
        IEnumerable<InventorySnapshotNode> nodes,
        IReadOnlyDictionary<string, InventorySnapshotNode> afterItems,
        HashSet<string> newItemIds)
    {
        foreach (var node in nodes)
        {
            var cursor = node;
            var path = new HashSet<string>(StringComparer.Ordinal);
            while (cursor.ParentItemId is not null)
            {
                if (!path.Add(cursor.ItemId) || !newItemIds.Contains(cursor.ParentItemId))
                {
                    throw new InventorySnapshotException("The Relay output graph is cyclic or disconnected.");
                }

                cursor = afterItems[cursor.ParentItemId];
            }

            if (!string.Equals(cursor.ItemId, rootItemId, StringComparison.Ordinal))
            {
                throw new InventorySnapshotException("The Relay output graph has more than one root.");
            }
        }
    }

    private static void RejectOldDescendants(
        string rootItemId,
        IEnumerable<InventorySnapshotNode> afterNodes,
        IReadOnlyDictionary<string, InventorySnapshotNode> afterItems,
        HashSet<string> newItemIds)
    {
        foreach (var node in afterNodes.Where(node => !newItemIds.Contains(node.ItemId)))
        {
            var cursor = node;
            while (cursor.ParentItemId is not null)
            {
                if (string.Equals(cursor.ParentItemId, rootItemId, StringComparison.Ordinal))
                {
                    throw new InventorySnapshotException("A pre-existing item became attached to the Relay output.");
                }

                cursor = afterItems[cursor.ParentItemId];
            }
        }
    }
}
