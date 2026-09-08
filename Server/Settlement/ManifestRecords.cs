using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace ContrabandCases.Server.Settlement;

public enum ManifestRngPurpose
{
    OfferSelection,
    RelayOutcome,
    RelayTargetSelection
}

public sealed class CanonicalRngEvidence
{
    public const long UnitDenominator = 9_007_199_254_740_992L;
    public const int MaximumDrawOrdinal = 4_096;

    public CanonicalRngEvidence(
        ManifestRngPurpose purpose,
        int drawOrdinal,
        long unitNumerator)
    {
        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose));
        }
        if (drawOrdinal is < 1 or > MaximumDrawOrdinal)
        {
            throw new ArgumentOutOfRangeException(nameof(drawOrdinal));
        }
        if (unitNumerator is < 0 or >= UnitDenominator)
        {
            throw new ArgumentOutOfRangeException(nameof(unitNumerator));
        }

        Purpose = purpose;
        DrawOrdinal = drawOrdinal;
        UnitNumerator = unitNumerator;
    }

    public ManifestRngPurpose Purpose { get; }

    public int DrawOrdinal { get; }

    public long UnitNumerator { get; }

    public double UnitValue => UnitNumerator / (double)UnitDenominator;
}

public sealed class ManifestOfferSnapshot
{
    public ManifestOfferSnapshot(
        int ordinal,
        RewardRarity rarity,
        CargoLotIdentitySnapshot identity,
        RewardForest forest,
        RewardForestFingerprintV2 fingerprint,
        CanonicalRngEvidence rngEvidence)
    {
        if (ordinal is < 1 or > ManifestRecord.OfferCount)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        ManifestRecordValidation.ValidateRarity(rarity, nameof(rarity));

        ManifestRecordValidation.ValidateSemanticLot(identity, forest, fingerprint);
        ArgumentNullException.ThrowIfNull(rngEvidence);
        if (rngEvidence.Purpose != ManifestRngPurpose.OfferSelection ||
            rngEvidence.DrawOrdinal != ordinal)
        {
            throw new ArgumentException(
                "Offer RNG evidence must be an offer-selection draw with the same ordinal.",
                nameof(rngEvidence));
        }

        Ordinal = ordinal;
        Rarity = rarity;
        Identity = identity;
        Forest = forest;
        Fingerprint = fingerprint;
        RngEvidence = rngEvidence;
    }

    public int Ordinal { get; }

    public RewardRarity Rarity { get; }

    public CargoLotIdentitySnapshot Identity { get; }

    public RewardForest Forest { get; }

    public RewardForestFingerprintV2 Fingerprint { get; }

    public CanonicalRngEvidence RngEvidence { get; }
}

public sealed class ManifestDecisionRecord
{
    public ManifestDecisionRecord(
        int ordinal,
        ManifestOfferDecision decision,
        DateTimeOffset decidedAtUtc)
    {
        if (ordinal < 1 || ordinal > (decision == ManifestOfferDecision.Choose ? 3 : 2))
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }
        ManifestRecordValidation.RequireUtc(decidedAtUtc, nameof(decidedAtUtc));

        Ordinal = ordinal;
        Decision = decision;
        DecidedAtUtc = decidedAtUtc;
    }

    public int Ordinal { get; }

    public ManifestOfferDecision Decision { get; }

    public DateTimeOffset DecidedAtUtc { get; }
}

public sealed class ManifestEntitlementSnapshot
{
    public ManifestEntitlementSnapshot(
        RewardRarity rarity,
        CargoLotIdentitySnapshot identity,
        RewardForest forest,
        RewardForestFingerprintV2 fingerprint)
    {
        ManifestRecordValidation.ValidateRarity(rarity, nameof(rarity));
        ManifestRecordValidation.ValidateSemanticLot(identity, forest, fingerprint);
        Rarity = rarity;
        Identity = identity;
        Forest = forest;
        Fingerprint = fingerprint;
    }

    public RewardRarity Rarity { get; }

    public CargoLotIdentitySnapshot Identity { get; }

    public RewardForest Forest { get; }

    public RewardForestFingerprintV2 Fingerprint { get; }
}

public sealed class ManifestRelayCandidateSnapshot
{
    public ManifestRelayCandidateSnapshot(
        ManifestRelayResult targetResult,
        RewardRarity rarity,
        CargoLotIdentitySnapshot identity,
        RewardForest forest,
        RewardForestFingerprintV2 fingerprint)
    {
        if (targetResult is not (ManifestRelayResult.Upgrade or ManifestRelayResult.Sidegrade))
        {
            throw new ArgumentOutOfRangeException(nameof(targetResult));
        }

        ManifestRecordValidation.ValidateRarity(rarity, nameof(rarity));

        ManifestRecordValidation.ValidateSemanticLot(identity, forest, fingerprint);
        TargetResult = targetResult;
        Rarity = rarity;
        Identity = identity;
        Forest = forest;
        Fingerprint = fingerprint;
    }

    public ManifestRelayResult TargetResult { get; }

    public RewardRarity Rarity { get; }

    public CargoLotIdentitySnapshot Identity { get; }

    public RewardForest Forest { get; }

    public RewardForestFingerprintV2 Fingerprint { get; }
}

public sealed class ManifestTicketPayload
{
    public ManifestTicketPayload(
        MongoId caseId,
        MongoId keyId,
        DateTimeOffset preparedAtUtc,
        bool profileCommitStarted,
        bool committed,
        DateTimeOffset? committedAtUtc,
        long? commitGeneration = null,
        string? commitPredecessorHash = null,
        string caseTemplateId = ModConstants.CaseTemplateId,
        ManifestOpeningQuality? openingQuality = null)
    {
        CaseTemplateId = CaseContracts.Require(caseTemplateId);
        if (caseTemplateId == CaseContracts.CashCache && openingQuality is not null)
            throw new ArgumentException("Cash Cache does not participate in surprise tiers.", nameof(openingQuality));
        OpeningQuality = openingQuality;
        if (caseId.IsEmpty || keyId.IsEmpty || caseId == keyId)
        {
            throw new ArgumentException("Manifest ticket case and key IDs must be present and distinct.");
        }
        ManifestRecordValidation.RequireUtc(preparedAtUtc, nameof(preparedAtUtc));
        if (committedAtUtc is DateTimeOffset committedTimestamp)
        {
            ManifestRecordValidation.RequireUtc(committedTimestamp, nameof(committedAtUtc));
            if (committedTimestamp < preparedAtUtc)
            {
                throw new ArgumentException("Ticket commit cannot precede preparation.", nameof(committedAtUtc));
            }
        }
        if (committed != (committedAtUtc is not null))
        {
            throw new ArgumentException("Ticket committed marker and timestamp must agree.", nameof(committedAtUtc));
        }
        if (committed && !profileCommitStarted)
        {
            throw new ArgumentException("A committed ticket requires a profile-commit marker.", nameof(profileCommitStarted));
        }
        if ((commitGeneration is null) != (commitPredecessorHash is null))
        {
            throw new ArgumentException("Ticket commit generation and predecessor hash must be supplied together.");
        }
        if (commitGeneration is not null)
        {
            ManifestInputCommitWitness.ValidateGeneration(commitGeneration.Value, nameof(commitGeneration));
            ManifestInputCommitWitness.ValidateHash(commitPredecessorHash!, nameof(commitPredecessorHash));
        }

        CaseId = caseId;
        KeyId = keyId;
        PreparedAtUtc = preparedAtUtc;
        ProfileCommitStarted = profileCommitStarted;
        Committed = committed;
        CommittedAtUtc = committedAtUtc;
        CommitGeneration = commitGeneration;
        CommitPredecessorHash = commitPredecessorHash;
    }

    public MongoId CaseId { get; }

    public string CaseTemplateId { get; }

    public ManifestOpeningQuality? OpeningQuality { get; }

    internal ManifestTicketPayload WithOpeningQuality(ManifestOpeningQuality quality)
    {
        if (OpeningQuality is not null || ProfileCommitStarted || Committed)
            throw new InvalidOperationException("Opening quality can only be assigned to a fresh ticket.");
        return new ManifestTicketPayload(CaseId, KeyId, PreparedAtUtc, false, false, null,
            CommitGeneration, CommitPredecessorHash, CaseTemplateId, quality);
    }

    public MongoId KeyId { get; }

    public DateTimeOffset PreparedAtUtc { get; }

    public bool ProfileCommitStarted { get; }

    public bool Committed { get; }

    public DateTimeOffset? CommittedAtUtc { get; }

    public long? CommitGeneration { get; }

    public string? CommitPredecessorHash { get; }

    internal ManifestTicketPayload WithCommitPlan(long generation, string predecessorHash)
    {
        if (ProfileCommitStarted || Committed || CommitGeneration is not null)
        {
            throw new InvalidOperationException("Only a fresh manifest ticket can receive a commit plan.");
        }

        return new ManifestTicketPayload(
            CaseId,
            KeyId,
            PreparedAtUtc,
            profileCommitStarted: false,
            committed: false,
            committedAtUtc: null,
            generation,
            predecessorHash,
            CaseTemplateId, OpeningQuality);
    }

    internal ManifestTicketPayload BeginProfileCommit()
    {
        if (ProfileCommitStarted || Committed)
        {
            throw new InvalidOperationException("The manifest ticket profile commit has already started.");
        }

        return new ManifestTicketPayload(
            CaseId,
            KeyId,
            PreparedAtUtc,
            profileCommitStarted: true,
            committed: false,
            committedAtUtc: null,
            CommitGeneration,
            CommitPredecessorHash,
            CaseTemplateId, OpeningQuality);
    }

    internal ManifestTicketPayload Commit(DateTimeOffset committedAtUtc)
    {
        if (!ProfileCommitStarted || Committed)
        {
            throw new InvalidOperationException(
                "Only a ticket with a started profile commit can be committed.");
        }

        return new ManifestTicketPayload(
            CaseId,
            KeyId,
            PreparedAtUtc,
            profileCommitStarted: true,
            committed: true,
            committedAtUtc,
            CommitGeneration,
            CommitPredecessorHash,
            CaseTemplateId, OpeningQuality);
    }
}

public enum ClaimDeliveryKind
{
    LegacyInventory,
    Messenger
}

public sealed class ManifestClaimPreparedPayload
{
    private readonly IReadOnlyList<Item> _items;
    private readonly IReadOnlyList<MongoId> _exactItemIds;
    private readonly IReadOnlyList<MongoId> _rootIds;

    public ManifestClaimPreparedPayload(
        IEnumerable<Item> items,
        IEnumerable<MongoId> rootIds,
        bool profileCommitStarted,
        DateTimeOffset preparedAtUtc,
        long? commitGeneration = null,
        string? commitPredecessorHash = null,
        ClaimDeliveryKind delivery = ClaimDeliveryKind.LegacyInventory)
    {
        var copiedItems = items?.Select(CaseOpeningRecord.CloneItem).ToArray()
            ?? throw new ArgumentNullException(nameof(items));
        var copiedRootIds = rootIds?.ToArray()
            ?? throw new ArgumentNullException(nameof(rootIds));

        if (copiedItems.Length is 0 or > RewardForest.MaxNodeCount)
        {
            throw new ArgumentException("Claim items must be non-empty and bounded.", nameof(items));
        }
        if (copiedRootIds.Length is 0 or > RewardForest.MaxRootCount ||
            copiedRootIds.Any(id => id.IsEmpty) ||
            copiedRootIds.Distinct().Count() != copiedRootIds.Length)
        {
            throw new ArgumentException("Claim root IDs must be non-empty, unique, and bounded.", nameof(rootIds));
        }
        if (copiedItems.Any(item => item.Id.IsEmpty || item.Template.IsEmpty) ||
            copiedItems.Select(item => item.Id).Distinct().Count() != copiedItems.Length)
        {
            throw new ArgumentException("Claim item IDs and templates must be non-empty and item IDs unique.", nameof(items));
        }

        ValidatePhysicalForest(copiedItems, copiedRootIds);
        if (!Enum.IsDefined(delivery)) throw new ArgumentOutOfRangeException(nameof(delivery));
        ManifestRecordValidation.RequireUtc(preparedAtUtc, nameof(preparedAtUtc));
        if ((commitGeneration is null) != (commitPredecessorHash is null))
        {
            throw new ArgumentException("Claim commit generation and predecessor hash must be supplied together.");
        }
        if (commitGeneration is not null)
        {
            ManifestClaimCommitWitness.ValidateGeneration(commitGeneration.Value, nameof(commitGeneration));
            ManifestClaimCommitWitness.ValidateHash(commitPredecessorHash!, nameof(commitPredecessorHash));
        }

        _items = new ReadOnlyCollection<Item>(copiedItems);
        _exactItemIds = new ReadOnlyCollection<MongoId>(copiedItems.Select(item => item.Id).ToArray());
        _rootIds = new ReadOnlyCollection<MongoId>(copiedRootIds);
        ProfileCommitStarted = profileCommitStarted;
        PreparedAtUtc = preparedAtUtc;
        CommitGeneration = commitGeneration;
        CommitPredecessorHash = commitPredecessorHash;
        Delivery = delivery;
    }

    public IReadOnlyList<Item> Items =>
        new ReadOnlyCollection<Item>(_items.Select(CaseOpeningRecord.CloneItem).ToArray());

    public IReadOnlyList<MongoId> ExactItemIds => _exactItemIds;

    public IReadOnlyList<MongoId> RootIds => _rootIds;

    public bool ProfileCommitStarted { get; }

    public DateTimeOffset PreparedAtUtc { get; }

    /// <summary>Durable Claim-chain generation; mandatory for active schema-five Claims.</summary>
    public long? CommitGeneration { get; }

    /// <summary>Hash of the exact profile-side head that preceded this Claim.</summary>
    public string? CommitPredecessorHash { get; }

    public ClaimDeliveryKind Delivery { get; }

    internal ManifestClaimPreparedPayload Clone() => new(
        Items,
        RootIds,
        ProfileCommitStarted,
        PreparedAtUtc,
        CommitGeneration,
        CommitPredecessorHash,
        Delivery);

    internal ManifestClaimPreparedPayload WithCommitPlan(long generation, string predecessorHash) => new(
        Items,
        RootIds,
        ProfileCommitStarted,
        PreparedAtUtc,
        generation,
        predecessorHash,
        Delivery);

    internal ManifestClaimPreparedPayload WithDelivery(ClaimDeliveryKind delivery) => new(
        Items, RootIds, ProfileCommitStarted, PreparedAtUtc, CommitGeneration, CommitPredecessorHash, delivery);

    internal void RejectUnsupportedExtensionData()
    {
        if (_items.Any(item =>
                item.ExtensionData?.Count > 0 ||
                item.Upd?.ExtensionData?.Count > 0 ||
                item.Location is ItemLocation location && location.ExtensionData?.Count > 0))
        {
            throw new ArgumentException("Claim item extension data is not supported by the durable journal.");
        }
    }

    internal void ValidateAgainst(RewardForest forest)
    {
        ArgumentNullException.ThrowIfNull(forest);
        if (_items.Count != forest.Nodes.Count || _rootIds.Count != forest.Roots.Count)
        {
            throw new ArgumentException("Claim payload shape does not match the entitlement forest.");
        }

        var physicalByLogicalPath = new Dictionary<string, Item>(StringComparer.Ordinal);
        var rootIndex = 0;
        for (var index = 0; index < forest.Nodes.Count; index++)
        {
            var node = forest.Nodes[index];
            var item = _items[index];
            if (!string.Equals(item.Template.ToString(), node.TemplateId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Claim item templates do not match the entitlement forest.");
            }

            var stackCount = item.Upd?.StackObjectsCount ?? 1d;
            if (!double.IsFinite(stackCount) || stackCount != node.StackCount)
            {
                throw new ArgumentException("Claim item stack counts do not match the entitlement forest.");
            }

            if (node.ParentLogicalPath is null)
            {
                if (_rootIds[rootIndex++] != item.Id)
                {
                    throw new ArgumentException("Claim root order does not match the entitlement forest.");
                }
            }
            else
            {
                var parent = physicalByLogicalPath[node.ParentLogicalPath];
                if (!string.Equals(item.ParentId, parent.Id.ToString(), StringComparison.Ordinal) ||
                    !string.Equals(item.SlotId, node.SlotId, StringComparison.Ordinal))
                {
                    throw new ArgumentException("Claim item topology does not match the entitlement forest.");
                }
                ValidateInternalLocation(item.Location, node.InternalLocation);
            }

            ValidateStableState(item, node.StableState);

            physicalByLogicalPath.Add(node.LogicalPath, item);
        }
    }

    private static void ValidateInternalLocation(
        object? physicalLocation,
        CanonicalInternalLocation? semanticLocation)
    {
        if (semanticLocation is null)
        {
            if (physicalLocation is not null)
            {
                throw new ArgumentException("Claim item internal location contradicts the entitlement forest.");
            }
            return;
        }
        if (physicalLocation is not ItemLocation location ||
            location.X != semanticLocation.X ||
            location.Y != semanticLocation.Y)
        {
            throw new ArgumentException("Claim item internal location contradicts the entitlement forest.");
        }

        if (!Enum.IsDefined(location.R))
        {
            throw new ArgumentException("Claim item internal rotation is not recognized.");
        }

        var rotation = location.R == ItemRotation.Vertical
            ? CanonicalRotation.Vertical
            : CanonicalRotation.Horizontal;
        if (location.Rotation is bool legacyRotation &&
            (legacyRotation ? CanonicalRotation.Vertical : CanonicalRotation.Horizontal) != rotation)
        {
            throw new ArgumentException("Claim item internal rotation representations conflict.");
        }
        if (rotation != semanticLocation.Rotation)
        {
            throw new ArgumentException("Claim item internal rotation contradicts the entitlement forest.");
        }
    }

    private static void ValidateStableState(Item item, RewardStableState? semanticState)
    {
        var repairable = item.Upd?.Repairable;
        var resourceComponents = new (RewardResourceKind Kind, bool Present, double? Value)[]
        {
            (RewardResourceKind.MedKit, item.Upd?.MedKit is not null, item.Upd?.MedKit?.HpResource),
            (RewardResourceKind.RepairKit, item.Upd?.RepairKit is not null, item.Upd?.RepairKit?.Resource),
            (RewardResourceKind.FoodDrink, item.Upd?.FoodDrink is not null, item.Upd?.FoodDrink?.HpPercent),
            (RewardResourceKind.Generic, item.Upd?.Resource is not null, item.Upd?.Resource?.Value)
        };
        var physicalResources = resourceComponents.Where(component => component.Present).ToArray();
        if (physicalResources.Length > 1)
        {
            throw new ArgumentException("Claim item has ambiguous physical resource state.");
        }

        if (semanticState is null)
        {
            if (repairable is not null || physicalResources.Length != 0)
            {
                throw new ArgumentException("Claim item stable state contradicts the entitlement forest.");
            }
            return;
        }

        if (semanticState.ResourceKind is RewardResourceKind expectedResourceKind)
        {
            if (physicalResources.Length != 1 || physicalResources[0].Kind != expectedResourceKind)
            {
                throw new ArgumentException(
                    "Claim item physical resource kind contradicts the entitlement forest.");
            }
        }
        else if (physicalResources.Length != 0)
        {
            throw new ArgumentException("Claim item stable state contradicts the entitlement forest.");
        }

        ValidatePhysicalDecimal(repairable?.Durability, semanticState.Durability, "durability");
        ValidatePhysicalDecimal(repairable?.MaxDurability, semanticState.MaximumDurability, "maximum durability");
        // SPT item instances carry the current resource value; the semantic fingerprint keeps its finalized maximum.
        ValidatePhysicalDecimal(
            physicalResources.SingleOrDefault().Value,
            semanticState.ResourceValue,
            "resource value");
    }

    private static void ValidatePhysicalDecimal(double? physical, decimal? semantic, string stateName)
    {
        if (physical is null != semantic is null ||
            physical is not null &&
            (!double.IsFinite(physical.Value) || checked((decimal)physical.Value) != semantic))
        {
            throw new ArgumentException($"Claim item {stateName} contradicts the entitlement forest.");
        }
    }

    private static void ValidatePhysicalForest(
        IReadOnlyList<Item> items,
        IReadOnlyList<MongoId> rootIds)
    {
        var itemIndex = items
            .Select((item, index) => (item, index))
            .ToDictionary(pair => pair.item.Id.ToString(), pair => pair.index, StringComparer.Ordinal);
        var rootSet = rootIds.ToHashSet();
        var actualRootOrder = items.Where(item => rootSet.Contains(item.Id)).Select(item => item.Id);
        if (!actualRootOrder.SequenceEqual(rootIds) || rootIds.Any(root => !itemIndex.ContainsKey(root.ToString())))
        {
            throw new ArgumentException("Claim root IDs must identify roots in exact item order.", nameof(rootIds));
        }

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var parentIndex = -1;
            var parentIsPreparedItem = item.ParentId is not null && itemIndex.TryGetValue(item.ParentId, out parentIndex);
            if (rootSet.Contains(item.Id))
            {
                if (parentIsPreparedItem)
                {
                    throw new ArgumentException("A Claim root cannot have another prepared item as its parent.", nameof(items));
                }
            }
            else if (!parentIsPreparedItem || parentIndex >= index)
            {
                throw new ArgumentException(
                    "Every non-root Claim item must follow its prepared parent.",
                    nameof(items));
            }
        }
    }
}

public sealed class ManifestClaimGrantRecord
{
    private readonly ManifestClaimPreparedPayload _claimPayload;

    public ManifestClaimGrantRecord(
        string manifestId,
        ManifestEntitlementSnapshot entitlement,
        ManifestClaimPreparedPayload claimPayload,
        DateTimeOffset committedAtUtc)
    {
        ManifestId = ManifestRecordValidation.RequireIdentifier(manifestId, nameof(manifestId));
        Entitlement = entitlement ?? throw new ArgumentNullException(nameof(entitlement));
        ArgumentNullException.ThrowIfNull(claimPayload);
        if (!claimPayload.ProfileCommitStarted)
        {
            throw new ArgumentException(
                "A durable Claim grant requires a persisted profile-commit marker.",
                nameof(claimPayload));
        }

        ManifestRecordValidation.RequireUtc(committedAtUtc, nameof(committedAtUtc));
        if (committedAtUtc < claimPayload.PreparedAtUtc)
        {
            throw new ArgumentException(
                "Claim grant completion cannot precede Claim preparation.",
                nameof(committedAtUtc));
        }

        claimPayload.RejectUnsupportedExtensionData();
        claimPayload.ValidateAgainst(entitlement.Forest);
        _claimPayload = claimPayload.Clone();
        CommittedAtUtc = committedAtUtc;
    }

    public string ManifestId { get; }

    public ManifestEntitlementSnapshot Entitlement { get; }

    public ManifestClaimPreparedPayload ClaimPayload => _claimPayload.Clone();

    public DateTimeOffset CommittedAtUtc { get; }

    internal ManifestClaimGrantRecord Clone() => new(
        ManifestId,
        Entitlement,
        _claimPayload,
        CommittedAtUtc);
}

public sealed class ManifestRelayPreparedPayload
{
    private readonly IReadOnlyList<ManifestRelayCandidateSnapshot> _nextRelayCandidates;

    public ManifestRelayPreparedPayload(
        MongoId keyId,
        ManifestRelayResult outcome,
        ManifestEntitlementSnapshot? output,
        RelayOdds odds,
        CanonicalRngEvidence outcomeRng,
        CanonicalRngEvidence? targetRng,
        int brokerFavorBefore,
        int brokerFavorAfter,
        bool profileCommitStarted,
        DateTimeOffset preparedAtUtc,
        IEnumerable<ManifestRelayCandidateSnapshot>? nextRelayCandidates = null,
        long? commitGeneration = null,
        string? commitPredecessorHash = null)
    {
        if (keyId.IsEmpty)
        {
            throw new ArgumentException("A Relay key ID is required.", nameof(keyId));
        }
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
        ManifestRecordValidation.ValidateOdds(odds);
        ArgumentNullException.ThrowIfNull(outcomeRng);
        if (outcomeRng.Purpose != ManifestRngPurpose.RelayOutcome)
        {
            throw new ArgumentException("Relay outcome RNG evidence has the wrong purpose.", nameof(outcomeRng));
        }

        if (outcome == ManifestRelayResult.Confiscated)
        {
            if (output is not null || targetRng is not null)
            {
                throw new ArgumentException("Confiscated Relay preparation cannot contain an output or target draw.");
            }
        }
        else if (output is null || targetRng?.Purpose != ManifestRngPurpose.RelayTargetSelection)
        {
            throw new ArgumentException("Upgrade and sidegrade Relay preparation require semantic output and target RNG.");
        }

        ManifestRecordValidation.ValidateFavorTransition(
            brokerFavorBefore,
            brokerFavorAfter,
            outcome);
        if (ManifestRecordValidation.SelectRelayOutcome(odds, brokerFavorBefore, outcomeRng) != outcome)
        {
            throw new ArgumentException("Relay outcome contradicts its persisted odds and RNG evidence.", nameof(outcome));
        }
        ManifestRecordValidation.RequireUtc(preparedAtUtc, nameof(preparedAtUtc));
        if ((commitGeneration is null) != (commitPredecessorHash is null))
        {
            throw new ArgumentException("Relay commit generation and predecessor hash must be supplied together.");
        }
        if (commitGeneration is not null)
        {
            ManifestInputCommitWitness.ValidateGeneration(commitGeneration.Value, nameof(commitGeneration));
            ManifestInputCommitWitness.ValidateHash(commitPredecessorHash!, nameof(commitPredecessorHash));
        }

        var copiedNextCandidates = nextRelayCandidates?.ToArray() ?? [];
        if (copiedNextCandidates.Any(candidate => candidate is null) ||
            copiedNextCandidates.Length > ManifestRecord.MaximumRelayCandidateCount ||
            copiedNextCandidates
                .Select(ManifestRecordValidation.SemanticKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != copiedNextCandidates.Length)
        {
            throw new ArgumentException(
                "Prepared next-stage Relay candidates must be non-null, distinct, and bounded.",
                nameof(nextRelayCandidates));
        }
        if (outcome != ManifestRelayResult.Upgrade && copiedNextCandidates.Length != 0)
        {
            throw new ArgumentException(
                "Only a Relay upgrade can prepare a next-stage candidate pool.",
                nameof(nextRelayCandidates));
        }
        copiedNextCandidates = ManifestRecordValidation.CanonicalOrder(copiedNextCandidates).ToArray();

        KeyId = keyId;
        Outcome = outcome;
        Output = output;
        Odds = odds;
        OutcomeRng = outcomeRng;
        TargetRng = targetRng;
        BrokerFavorBefore = brokerFavorBefore;
        BrokerFavorAfter = brokerFavorAfter;
        ProfileCommitStarted = profileCommitStarted;
        PreparedAtUtc = preparedAtUtc;
        CommitGeneration = commitGeneration;
        CommitPredecessorHash = commitPredecessorHash;
        _nextRelayCandidates = new ReadOnlyCollection<ManifestRelayCandidateSnapshot>(copiedNextCandidates);
    }

    public MongoId KeyId { get; }

    public ManifestRelayResult Outcome { get; }

    public ManifestEntitlementSnapshot? Output { get; }

    public RelayOdds Odds { get; }

    public CanonicalRngEvidence OutcomeRng { get; }

    public CanonicalRngEvidence? TargetRng { get; }

    public int BrokerFavorBefore { get; }

    public int BrokerFavorAfter { get; }

    public bool ProfileCommitStarted { get; }

    public DateTimeOffset PreparedAtUtc { get; }

    public long? CommitGeneration { get; }

    public string? CommitPredecessorHash { get; }

    public IReadOnlyList<ManifestRelayCandidateSnapshot> NextRelayCandidates =>
        _nextRelayCandidates;

    internal ManifestRelayPreparedPayload BeginProfileCommit()
    {
        if (ProfileCommitStarted)
        {
            throw new InvalidOperationException("The Manifest Relay profile commit has already started.");
        }

        return new ManifestRelayPreparedPayload(
            KeyId,
            Outcome,
            Output,
            Odds,
            OutcomeRng,
            TargetRng,
            BrokerFavorBefore,
            BrokerFavorAfter,
            profileCommitStarted: true,
            PreparedAtUtc,
            NextRelayCandidates,
            CommitGeneration,
            CommitPredecessorHash);
    }
}

public sealed class ManifestLotReceiptSnapshot
{
    public ManifestLotReceiptSnapshot(
        RewardRarity rarity,
        CargoLotIdentitySnapshot identity,
        RewardForestFingerprintV2 fingerprint)
    {
        ManifestRecordValidation.ValidateRarity(rarity, nameof(rarity));
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (!identity.Fingerprint.Equals(fingerprint))
        {
            throw new ArgumentException("Receipt identity and fingerprint must agree.", nameof(fingerprint));
        }

        Rarity = rarity;
        Identity = identity;
        Fingerprint = fingerprint;
    }

    public RewardRarity Rarity { get; }

    public CargoLotIdentitySnapshot Identity { get; }

    public RewardForestFingerprintV2 Fingerprint { get; }
}

public sealed class ManifestRelayReceipt
{
    private readonly IReadOnlyList<ManifestLotReceiptSnapshot> _eligibleTargets;

    public ManifestRelayReceipt(
        int relayStage,
        ManifestLotReceiptSnapshot input,
        ManifestRelayResult outcome,
        ManifestLotReceiptSnapshot? output,
        RelayOdds odds,
        CanonicalRngEvidence outcomeRng,
        CanonicalRngEvidence? targetRng,
        IEnumerable<ManifestLotReceiptSnapshot>? eligibleTargets,
        int brokerFavorBefore,
        int brokerFavorAfter,
        DateTimeOffset completedAtUtc,
        RarityLadderVersion rarityLadderVersion = RarityLadderVersion.FiveTier)
    {
        RelayRules.ValidateLadderVersion(rarityLadderVersion, nameof(rarityLadderVersion));
        _ = RelayRules.GetOdds(relayStage);
        ArgumentNullException.ThrowIfNull(input);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
        ManifestRecordValidation.ValidateOdds(odds);
        if (odds != RelayRules.GetOdds(relayStage))
        {
            throw new ArgumentException("Committed Relay odds must match its stage.", nameof(odds));
        }
        ArgumentNullException.ThrowIfNull(outcomeRng);
        if (outcomeRng.Purpose != ManifestRngPurpose.RelayOutcome)
        {
            throw new ArgumentException("Committed Relay outcome RNG has the wrong purpose.", nameof(outcomeRng));
        }
        if (outcomeRng.DrawOrdinal != relayStage)
        {
            throw new ArgumentException("Committed Relay outcome RNG draw ordinal must equal its Relay stage.", nameof(outcomeRng));
        }

        var copiedEligibleTargets = eligibleTargets?.ToArray() ?? [];
        if (copiedEligibleTargets.Any(candidate => candidate is null) ||
            copiedEligibleTargets.Length > ManifestRecord.MaximumRelayCandidateCount)
        {
            throw new ArgumentException("Committed Relay eligible targets must be non-null and bounded.", nameof(eligibleTargets));
        }
        copiedEligibleTargets = ManifestRecordValidation.CanonicalOrder(copiedEligibleTargets).ToArray();
        if (outcome == ManifestRelayResult.Confiscated)
        {
            if (output is not null || targetRng is not null || copiedEligibleTargets.Length != 0)
            {
                throw new ArgumentException("A confiscated Relay receipt cannot contain output metadata, a target draw, or eligible targets.");
            }
        }
        else
        {
            if (output is null ||
                targetRng?.Purpose != ManifestRngPurpose.RelayTargetSelection ||
                copiedEligibleTargets.Length == 0)
            {
                throw new ArgumentException("A successful Relay receipt requires output metadata, target RNG, and its frozen eligible target set.");
            }
            if (targetRng.DrawOrdinal != relayStage)
            {
                throw new ArgumentException("Committed Relay target RNG draw ordinal must equal its Relay stage.", nameof(targetRng));
            }
            if (!output.Identity.TrackId.Equals(input.Identity.TrackId))
            {
                throw new ArgumentException("A committed Relay output must remain in the input track.", nameof(output));
            }

            var expectedRarity = outcome == ManifestRelayResult.Sidegrade
                ? input.Rarity
                : RelayRules.GetUpgradeRarity(input.Rarity, rarityLadderVersion);
            if (output.Rarity != expectedRarity)
            {
                throw new ArgumentException("Committed Relay output grade contradicts its outcome.", nameof(output));
            }

            if (ManifestRecordValidation.SameSemanticIdentity(
                    output.Identity,
                    output.Fingerprint,
                    input.Identity,
                    input.Fingerprint))
            {
                throw new ArgumentException("A committed Relay output cannot be the staked semantic lot.", nameof(output));
            }
            if (copiedEligibleTargets.Any(candidate =>
                    !candidate.Identity.TrackId.Equals(input.Identity.TrackId) ||
                    candidate.Rarity != expectedRarity))
            {
                throw new ArgumentException("Committed Relay eligible targets must match the outcome track and grade.", nameof(eligibleTargets));
            }
            if (copiedEligibleTargets.Any(candidate =>
                    ManifestRecordValidation.SameSemanticIdentity(
                        candidate.Identity,
                        candidate.Fingerprint,
                        input.Identity,
                        input.Fingerprint)))
            {
                throw new ArgumentException("Committed Relay eligible targets cannot contain the staked semantic lot.", nameof(eligibleTargets));
            }
            if (copiedEligibleTargets
                    .Select(ManifestRecordValidation.SemanticKey)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != copiedEligibleTargets.Length)
            {
                throw new ArgumentException("Committed Relay eligible targets must be distinct.", nameof(eligibleTargets));
            }

            var selected = ManifestRecordValidation.SelectRelayTarget(copiedEligibleTargets, targetRng);
            if (!ManifestRecordValidation.SameReceiptLot(selected, output))
            {
                throw new ArgumentException("Committed Relay output contradicts its eligible targets and target RNG.", nameof(output));
            }
        }

        ManifestRecordValidation.ValidateFavorTransition(brokerFavorBefore, brokerFavorAfter, outcome);
        if (ManifestRecordValidation.SelectRelayOutcome(odds, brokerFavorBefore, outcomeRng) != outcome)
        {
            throw new ArgumentException("Committed Relay outcome contradicts its odds and RNG.", nameof(outcome));
        }
        ManifestRecordValidation.RequireUtc(completedAtUtc, nameof(completedAtUtc));

        RelayStage = relayStage;
        Input = input;
        Outcome = outcome;
        Output = output;
        Odds = odds;
        OutcomeRng = outcomeRng;
        TargetRng = targetRng;
        _eligibleTargets = new ReadOnlyCollection<ManifestLotReceiptSnapshot>(copiedEligibleTargets);
        BrokerFavorBefore = brokerFavorBefore;
        BrokerFavorAfter = brokerFavorAfter;
        CompletedAtUtc = completedAtUtc;
        RarityLadderVersion = rarityLadderVersion;
    }

    public int RelayStage { get; }

    public ManifestLotReceiptSnapshot Input { get; }

    public ManifestRelayResult Outcome { get; }

    public ManifestLotReceiptSnapshot? Output { get; }

    public RelayOdds Odds { get; }

    public CanonicalRngEvidence OutcomeRng { get; }

    public CanonicalRngEvidence? TargetRng { get; }

    public IReadOnlyList<ManifestLotReceiptSnapshot> EligibleTargets => _eligibleTargets;

    public int BrokerFavorBefore { get; }

    public int BrokerFavorAfter { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public RarityLadderVersion RarityLadderVersion { get; }
}

public sealed class ManifestTerminalReceipt
{
    private readonly IReadOnlyList<ManifestLotReceiptSnapshot> _offers;
    private readonly IReadOnlyList<ManifestDecisionRecord> _decisions;
    private readonly IReadOnlyList<ManifestRelayReceipt> _relayHistory;

    public ManifestTerminalReceipt(
        string manifestId,
        ManifestPhase terminalPhase,
        IEnumerable<ManifestLotReceiptSnapshot> offers,
        ManifestLotReceiptSnapshot? entitlement,
        IEnumerable<ManifestDecisionRecord>? decisions,
        IEnumerable<ManifestRelayReceipt>? relayHistory,
        int brokerFavorBefore,
        int brokerFavorAfter,
        DateTimeOffset completedAtUtc,
        RarityLadderVersion rarityLadderVersion = RarityLadderVersion.FiveTier,
        string caseTemplateId = ModConstants.CaseTemplateId,
        ManifestOpeningQuality? openingQuality = null)
    {
        CaseTemplateId = CaseContracts.Require(caseTemplateId);
        if (caseTemplateId == CaseContracts.CashCache && openingQuality is not null)
            throw new ArgumentException("Cash Cache cannot have surprise tiers.");
        OpeningQuality = openingQuality;
        RelayRules.ValidateLadderVersion(rarityLadderVersion, nameof(rarityLadderVersion));
        ManifestId = ManifestRecordValidation.RequireIdentifier(manifestId, nameof(manifestId));
        if (terminalPhase is not (ManifestPhase.Granted or ManifestPhase.Confiscated or ManifestPhase.Forfeited))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalPhase));
        }

        var copiedOffers = offers?.ToArray()
            ?? throw new ArgumentNullException(nameof(offers));
        if (copiedOffers.Length != CaseContracts.OfferCount(caseTemplateId) || copiedOffers.Any(offer => offer is null))
        {
            throw new ArgumentException("A terminal receipt requires exactly three offer snapshots.", nameof(offers));
        }
        if (terminalPhase is ManifestPhase.Granted or ManifestPhase.Confiscated && entitlement is null)
        {
            throw new ArgumentException("This terminal phase requires entitlement metadata.", nameof(entitlement));
        }

        var copiedDecisions = decisions?.ToArray() ?? [];
        ManifestRecordValidation.ValidateDecisionOrdering(copiedDecisions);
        openingQuality?.ValidateOffers(copiedOffers.Select(offer => offer.Rarity));
        if (openingQuality?.IsPremium == true)
        {
            if (copiedDecisions.Length > 1 || copiedDecisions.Any(d => d.Decision != ManifestOfferDecision.Choose) ||
                terminalPhase != ManifestPhase.Forfeited && copiedDecisions.Length != 1)
                throw new ArgumentException("Premium receipt must retain its single package choice.");
        }
        else if (copiedDecisions.Any(d => d.Decision == ManifestOfferDecision.Choose))
            throw new ArgumentException("An ordinary receipt cannot contain a premium choice.");
        var copiedRelayHistory = relayHistory?.ToArray() ?? [];
        ManifestRecordValidation.ValidateRelayHistory(copiedRelayHistory);
        if (caseTemplateId == CaseContracts.CashCache &&
            (copiedDecisions.Length != 0 || copiedRelayHistory.Length != 0 || terminalPhase == ManifestPhase.Confiscated))
            throw new ArgumentException("A cash receipt cannot contain discard or Relay history.");
        if (caseTemplateId == CaseContracts.CashCache &&
            (entitlement is null || !ManifestRecordValidation.SameReceiptLot(entitlement, copiedOffers[0])))
            throw new ArgumentException("A cash receipt must retain the single committed payout.", nameof(entitlement));
        if (copiedRelayHistory.Any(receipt => receipt.RarityLadderVersion != rarityLadderVersion))
        {
            throw new ArgumentException(
                "Terminal Relay history must use the terminal receipt's rarity ladder version.",
                nameof(relayHistory));
        }
        ManifestRecordValidation.ValidateFavor(brokerFavorBefore, nameof(brokerFavorBefore));
        ManifestRecordValidation.ValidateFavor(brokerFavorAfter, nameof(brokerFavorAfter));
        if (terminalPhase is ManifestPhase.Granted or ManifestPhase.Forfeited &&
            brokerFavorBefore != brokerFavorAfter)
        {
            throw new ArgumentException("Claim and Forfeit cannot mutate Broker Favor.", nameof(brokerFavorAfter));
        }
        if (terminalPhase == ManifestPhase.Confiscated &&
            (copiedRelayHistory.LastOrDefault()?.Outcome != ManifestRelayResult.Confiscated ||
             copiedRelayHistory[^1].BrokerFavorBefore != brokerFavorBefore ||
             copiedRelayHistory[^1].BrokerFavorAfter != brokerFavorAfter))
        {
            throw new ArgumentException("A confiscated receipt requires matching committed Relay evidence.", nameof(relayHistory));
        }
        if (copiedRelayHistory.LastOrDefault() is ManifestRelayReceipt latestRelay)
        {
            var expectedEntitlement = latestRelay.Outcome == ManifestRelayResult.Confiscated
                ? latestRelay.Input
                : latestRelay.Output;
            if (entitlement is not null &&
                (expectedEntitlement is null || !ManifestRecordValidation.SameReceiptLot(entitlement, expectedEntitlement)))
            {
                throw new ArgumentException("Terminal entitlement metadata contradicts committed Relay history.", nameof(entitlement));
            }
        }
        if (copiedDecisions.LastOrDefault()?.DecidedAtUtc > completedAtUtc ||
            copiedRelayHistory.LastOrDefault()?.CompletedAtUtc > completedAtUtc)
        {
            throw new ArgumentException("Terminal completion cannot precede its persisted history.", nameof(completedAtUtc));
        }
        ManifestRecordValidation.RequireUtc(completedAtUtc, nameof(completedAtUtc));

        TerminalPhase = terminalPhase;
        _offers = new ReadOnlyCollection<ManifestLotReceiptSnapshot>(copiedOffers);
        Entitlement = entitlement;
        _decisions = new ReadOnlyCollection<ManifestDecisionRecord>(copiedDecisions);
        _relayHistory = new ReadOnlyCollection<ManifestRelayReceipt>(copiedRelayHistory);
        BrokerFavorBefore = brokerFavorBefore;
        BrokerFavorAfter = brokerFavorAfter;
        CompletedAtUtc = completedAtUtc;
        RarityLadderVersion = rarityLadderVersion;
    }

    public string ManifestId { get; }

    public ManifestPhase TerminalPhase { get; }

    public string CaseTemplateId { get; }

    public ManifestOpeningQuality? OpeningQuality { get; }

    public IReadOnlyList<ManifestLotReceiptSnapshot> Offers => _offers;

    public ManifestLotReceiptSnapshot? Entitlement { get; }

    public IReadOnlyList<ManifestDecisionRecord> Decisions => _decisions;

    public IReadOnlyList<ManifestRelayReceipt> RelayHistory => _relayHistory;

    public int BrokerFavorBefore { get; }

    public int BrokerFavorAfter { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public RarityLadderVersion RarityLadderVersion { get; }
}

public sealed class ManifestRecord
{
    public const int OfferCount = 3;
    public const int MaximumRelayReceiptCount = RelayRules.MaximumStage;
    public const int MaximumRelayCandidateCount = 64;
    public const int MaximumFrozenRelayNodeCount = 8_192;

    private readonly IReadOnlyList<ManifestOfferSnapshot> _offers;
    private readonly IReadOnlyList<ManifestDecisionRecord> _decisions;
    private readonly IReadOnlyList<ManifestRelayCandidateSnapshot> _relayCandidates;
    private readonly IReadOnlyList<ManifestRelayReceipt> _relayHistory;

    public ManifestRecord(
        string manifestId,
        string catalogSnapshotId,
        ManifestCommitmentEvidence commitment,
        ManifestFlowState flowState,
        ManifestTicketPayload ticket,
        IEnumerable<ManifestOfferSnapshot> offers,
        IEnumerable<ManifestDecisionRecord>? decisions,
        ManifestEntitlementSnapshot? entitlement,
        IEnumerable<ManifestRelayCandidateSnapshot>? relayCandidates,
        IEnumerable<ManifestRelayReceipt>? relayHistory,
        int brokerFavor,
        ManifestClaimPreparedPayload? claimPrepared = null,
        ManifestRelayPreparedPayload? relayPrepared = null,
        ManifestTerminalReceipt? terminalReceipt = null,
        RarityLadderVersion rarityLadderVersion = RarityLadderVersion.FiveTier)
    {
        ManifestId = ManifestRecordValidation.RequireIdentifier(manifestId, nameof(manifestId));
        CatalogSnapshotId = ManifestRecordValidation.RequireIdentifier(catalogSnapshotId, nameof(catalogSnapshotId));
        Commitment = commitment ?? throw new ArgumentNullException(nameof(commitment));
        FlowState = flowState ?? throw new ArgumentNullException(nameof(flowState));
        Ticket = ticket ?? throw new ArgumentNullException(nameof(ticket));
        if (terminalReceipt is not null && terminalReceipt.CaseTemplateId != ticket.CaseTemplateId)
            throw new ArgumentException("The terminal receipt must retain its ticket's case type.", nameof(terminalReceipt));
        if (terminalReceipt is not null && terminalReceipt.OpeningQuality != ticket.OpeningQuality)
            throw new ArgumentException("The terminal receipt must retain its ticket's opening quality.");
        RelayRules.ValidateLadderVersion(rarityLadderVersion, nameof(rarityLadderVersion));
        ManifestRecordValidation.ValidateFavor(brokerFavor, nameof(brokerFavor));
        if ((flowState.Phase == ManifestPhase.TicketPrepared) == ticket.Committed)
        {
            throw new ArgumentException(
                "TicketPrepared requires uncommitted ticket evidence and every later phase requires committed evidence.",
                nameof(ticket));
        }

        var copiedOffers = offers?.ToArray() ?? throw new ArgumentNullException(nameof(offers));
        var expectedOfferCount = CaseContracts.OfferCount(ticket.CaseTemplateId);
        var singlePayout = ticket.CaseTemplateId == CaseContracts.CashCache;
        if (copiedOffers.Length != expectedOfferCount || copiedOffers.Any(offer => offer is null) ||
            !copiedOffers.Select(offer => offer.Ordinal).SequenceEqual(Enumerable.Range(1, expectedOfferCount)))
        {
            throw new ArgumentException("A manifest requires exactly three offers in ordinal order.", nameof(offers));
        }
        if (copiedOffers.Select(ManifestRecordValidation.SemanticKey).Distinct(StringComparer.Ordinal).Count() != expectedOfferCount)
        {
            throw new ArgumentException("Manifest offers must be distinct.", nameof(offers));
        }
        // Accept both legitimate Black Site generations. Fresh selection uses
        // tracks, while old commitment-checked transcripts used authored families.
        var distinctFamilies = copiedOffers.Select(o => o.Identity.FamilyId).Distinct().Count() == expectedOfferCount;
        var distinctTracks = copiedOffers.Select(o => o.Identity.TrackId).Distinct().Count() == expectedOfferCount;
        var validGroups = ticket.CaseTemplateId switch
        {
            CaseContracts.Relics => distinctTracks,
            CaseContracts.BlackSite => distinctTracks || distinctFamilies,
            _ => distinctFamilies
        };
        if (!validGroups && ticket.OpeningQuality?.IsPremium != true)
        {
            throw new ArgumentException("Manifest offers must use three distinct families.", nameof(offers));
        }
        if (!Commitment.VerifyReveal(ManifestId, CatalogSnapshotId, copiedOffers))
        {
            throw new ArgumentException("Manifest commitment does not verify against its nonce and persisted offer transcript.", nameof(commitment));
        }

        var copiedDecisions = decisions?.ToArray() ?? [];
        ManifestRecordValidation.ValidateDecisionOrdering(copiedDecisions);
        ticket.OpeningQuality?.ValidateOffers(copiedOffers.Select(offer => offer.Rarity));
        if (singlePayout)
        {
            if (copiedDecisions.Length != 0 || flowState.CurrentOrdinal != 1 ||
                flowState.Phase is ManifestPhase.Offer1 or ManifestPhase.Offer2 or ManifestPhase.RelayPrepared or ManifestPhase.Confiscated ||
                flowState.Phase != ManifestPhase.TicketPrepared && flowState.LockedOrdinal != 1)
                throw new ArgumentException("Cash Cache has one automatically locked payout and no offer decisions.");
        }
        else if (ticket.OpeningQuality?.IsPremium == true)
        {
            var chosen = flowState.LockedOrdinal;
            if (flowState.Phase == ManifestPhase.Offer2 ||
                copiedDecisions.Length != (chosen is null ? 0 : 1) ||
                copiedDecisions.Any(d => d.Decision != ManifestOfferDecision.Choose || d.Ordinal != chosen))
                throw new ArgumentException("Premium openings require one explicit choice without discards.");
        }
        else ValidateDecisionHistory(flowState, copiedDecisions);

        var copiedCandidates = relayCandidates?.ToArray() ?? [];
        if (copiedCandidates.Any(candidate => candidate is null) || copiedCandidates.Length > MaximumRelayCandidateCount)
        {
            throw new ArgumentException("Frozen Relay candidates must be non-null and bounded.", nameof(relayCandidates));
        }
        copiedCandidates = ManifestRecordValidation.CanonicalOrder(copiedCandidates).ToArray();
        ValidateCandidates(entitlement, flowState, copiedCandidates, rarityLadderVersion);

        var copiedRelayHistory = relayHistory?.ToArray() ?? [];
        if (singlePayout && (copiedCandidates.Length != 0 || copiedRelayHistory.Length != 0 || relayPrepared is not null))
            throw new ArgumentException("Cash Cache cannot participate in Relay or Favor.");
        ManifestRecordValidation.ValidateRelayHistory(copiedRelayHistory);
        if (copiedRelayHistory.Any(receipt => receipt.RarityLadderVersion != rarityLadderVersion))
        {
            throw new ArgumentException(
                "Manifest Relay history must use the manifest's rarity ladder version.",
                nameof(relayHistory));
        }
        ValidateRelayHistory(
            flowState,
            copiedOffers,
            entitlement,
            brokerFavor,
            copiedRelayHistory,
            rarityLadderVersion);

        ValidatePhasePayloads(
            ManifestId,
            flowState,
            entitlement,
            copiedCandidates,
            copiedRelayHistory,
            brokerFavor,
            claimPrepared,
            relayPrepared,
            terminalReceipt,
            copiedOffers,
            copiedDecisions,
            rarityLadderVersion);
        if (claimPrepared is not null)
        {
            ManifestRecordValidation.ValidateClaimItemIdsAgainstTicket(claimPrepared, Ticket);
        }

        _offers = new ReadOnlyCollection<ManifestOfferSnapshot>(copiedOffers);
        _decisions = new ReadOnlyCollection<ManifestDecisionRecord>(copiedDecisions);
        Entitlement = entitlement;
        _relayCandidates = new ReadOnlyCollection<ManifestRelayCandidateSnapshot>(copiedCandidates);
        _relayHistory = new ReadOnlyCollection<ManifestRelayReceipt>(copiedRelayHistory);
        BrokerFavor = brokerFavor;
        ClaimPrepared = claimPrepared;
        RelayPrepared = relayPrepared;
        TerminalReceipt = terminalReceipt;
        RarityLadderVersion = rarityLadderVersion;
    }

    public string ManifestId { get; }

    public string CatalogSnapshotId { get; }

    public ManifestCommitmentEvidence Commitment { get; }

    public ManifestFlowState FlowState { get; }

    public ManifestTicketPayload Ticket { get; }

    public IReadOnlyList<ManifestOfferSnapshot> Offers => _offers;

    public IReadOnlyList<ManifestDecisionRecord> Decisions => _decisions;

    public ManifestEntitlementSnapshot? Entitlement { get; }

    public IReadOnlyList<ManifestRelayCandidateSnapshot> RelayCandidates => _relayCandidates;

    public IReadOnlyList<ManifestRelayReceipt> RelayHistory => _relayHistory;

    public int BrokerFavor { get; }

    public ManifestClaimPreparedPayload? ClaimPrepared { get; }

    public ManifestRelayPreparedPayload? RelayPrepared { get; }

    public ManifestTerminalReceipt? TerminalReceipt { get; }

    public RarityLadderVersion RarityLadderVersion { get; }

    public bool IsTerminal => FlowState.IsTerminal;

    public bool RequiresExclusiveProfileMutation => FlowState.Phase is
        ManifestPhase.TicketPrepared or
        ManifestPhase.ClaimPrepared or
        ManifestPhase.RelayPrepared or
        ManifestPhase.RewardOwed;

    internal ManifestRecord BeginTicketProfileCommit()
    {
        if (FlowState.Phase != ManifestPhase.TicketPrepared)
        {
            throw new InvalidOperationException(
                "Only a prepared manifest ticket can begin its profile commit.");
        }

        return Copy(ticket: Ticket.BeginProfileCommit());
    }

    internal ManifestRecord ActivateTicket(DateTimeOffset committedAtUtc)
    {
        ManifestRecordValidation.RequireUtc(committedAtUtc, nameof(committedAtUtc));
        if (FlowState.Phase != ManifestPhase.TicketPrepared)
        {
            throw new InvalidOperationException(
                "Only a prepared manifest ticket can become active.");
        }

        var singlePayout = Ticket.CaseTemplateId == CaseContracts.CashCache;
        var payout = singlePayout ? Offers[0] : null;
        return Copy(
            flowState: ManifestFlowState.ActivateTicket(FlowState, singlePayout),
            ticket: Ticket.Commit(committedAtUtc),
            entitlement: payout is null ? null : new ManifestEntitlementSnapshot(
                payout.Rarity, payout.Identity, payout.Forest, payout.Fingerprint));
    }

    internal ManifestRecord DecideOffer(
        ManifestOfferDecision decision,
        DateTimeOffset decidedAtUtc,
        IEnumerable<ManifestRelayCandidateSnapshot>? relayCandidates = null)
    {
        ManifestRecordValidation.RequireUtc(decidedAtUtc, nameof(decidedAtUtc));
        if (Ticket.OpeningQuality?.IsPremium == true)
            throw new InvalidOperationException("Choose one of the three premium packages; premium offers cannot be burned.");
        if (FlowState.Phase is not (ManifestPhase.Offer1 or ManifestPhase.Offer2))
        {
            throw new InvalidOperationException(
                $"Cannot decide an offer while the manifest is in phase '{FlowState.Phase}'.");
        }

        var nextState = ManifestStateMachine.DecideOffer(FlowState, decision);
        var appendedDecisions = Decisions
            .Append(new ManifestDecisionRecord(FlowState.CurrentOrdinal, decision, decidedAtUtc))
            .ToArray();
        if (nextState.Phase != ManifestPhase.Entitlement)
        {
            if (relayCandidates?.Any() == true)
            {
                throw new ArgumentException(
                    "An unrevealed manifest offer cannot receive Relay candidates.",
                    nameof(relayCandidates));
            }

            return Copy(
                flowState: nextState,
                decisions: appendedDecisions,
                entitlement: null,
                relayCandidates: []);
        }

        var selectedOffer = Offers[nextState.LockedOrdinal!.Value - 1];
        return Copy(
            flowState: nextState,
            decisions: appendedDecisions,
            entitlement: ToEntitlement(selectedOffer),
            relayCandidates: relayCandidates ?? []);
    }

    internal ManifestRecord ChoosePremiumOffer(int ordinal, DateTimeOffset decidedAtUtc,
        IEnumerable<ManifestRelayCandidateSnapshot>? relayCandidates = null)
    {
        if (Ticket.OpeningQuality?.IsPremium != true)
            throw new InvalidOperationException("Only a premium opening supports choosing any package.");
        var next = ManifestStateMachine.ChoosePremiumOffer(FlowState, ordinal);
        return Copy(flowState: next,
            decisions: [new ManifestDecisionRecord(ordinal, ManifestOfferDecision.Choose, decidedAtUtc)],
            entitlement: ToEntitlement(Offers[ordinal - 1]), relayCandidates: relayCandidates ?? []);
    }

    internal ManifestRecord BeginRelay(ManifestRelayPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.ProfileCommitStarted)
        {
            throw new ArgumentException(
                "A newly prepared Manifest Relay cannot already have a profile-commit marker.",
                nameof(prepared));
        }
        if (prepared.KeyId == Ticket.CaseId || prepared.KeyId == Ticket.KeyId)
        {
            throw new ArgumentException(
                "A Manifest Relay must consume a new key instance.",
                nameof(prepared));
        }

        return Copy(
            flowState: ManifestStateMachine.PrepareRelay(
                FlowState,
                relayEligible: RelayCandidates.Count > 0),
            relayPrepared: prepared);
    }

    internal ManifestRecord BeginRelayProfileCommit()
    {
        if (FlowState.Phase != ManifestPhase.RelayPrepared || RelayPrepared is null)
        {
            throw new InvalidOperationException(
                "Only a prepared Manifest Relay can begin its profile commit.");
        }

        return Copy(relayPrepared: RelayPrepared.BeginProfileCommit());
    }

    internal ManifestRecord CompleteRelay(DateTimeOffset completedAtUtc)
    {
        ManifestRecordValidation.RequireUtc(completedAtUtc, nameof(completedAtUtc));
        var prepared = RelayPrepared is { ProfileCommitStarted: true }
            ? RelayPrepared
            : throw new InvalidOperationException(
                "Only a Manifest Relay with persisted profile-commit evidence can complete.");
        var input = Entitlement
            ?? throw new InvalidOperationException("A prepared Manifest Relay has no entitlement.");

        completedAtUtc = AtLeastLatestActivity(completedAtUtc, prepared.PreparedAtUtc);
        var eligibleTargets = prepared.Outcome == ManifestRelayResult.Confiscated
            ? []
            : RelayCandidates
                .Where(candidate => candidate.TargetResult == prepared.Outcome)
                .Select(ToReceipt)
                .ToArray();
        var receipt = new ManifestRelayReceipt(
            FlowState.RelayStage,
            ToReceipt(input),
            prepared.Outcome,
            prepared.Output is null ? null : ToReceipt(prepared.Output),
            prepared.Odds,
            prepared.OutcomeRng,
            prepared.TargetRng,
            eligibleTargets,
            prepared.BrokerFavorBefore,
            prepared.BrokerFavorAfter,
            completedAtUtc,
            RarityLadderVersion);
        var relayHistory = RelayHistory.Append(receipt).ToArray();
        var nextState = ManifestStateMachine.CompleteRelay(
            FlowState,
            prepared.Outcome,
            prepared.Output?.Rarity);
        var nextEntitlement = prepared.Outcome == ManifestRelayResult.Confiscated
            ? null
            : prepared.Output;
        var nextCandidates = nextState.Phase == ManifestPhase.Entitlement &&
            !nextState.RelayTerminal
                ? prepared.NextRelayCandidates
                : [];

        ManifestTerminalReceipt? terminalReceipt = null;
        if (nextState.Phase == ManifestPhase.Confiscated)
        {
            terminalReceipt = new ManifestTerminalReceipt(
                ManifestId,
                ManifestPhase.Confiscated,
                Offers.Select(ToReceipt),
                ToReceipt(input),
                Decisions,
                relayHistory,
                BrokerFavor,
                prepared.BrokerFavorAfter,
                completedAtUtc,
                RarityLadderVersion,
                Ticket.CaseTemplateId, Ticket.OpeningQuality);
        }

        return Copy(
            flowState: nextState,
            entitlement: nextEntitlement,
            clearEntitlement: nextEntitlement is null,
            relayCandidates: nextCandidates,
            relayHistory: relayHistory,
            brokerFavor: prepared.BrokerFavorAfter,
            relayPrepared: null,
            terminalReceipt: terminalReceipt);
    }

    internal ManifestRecord ForfeitMissingContent(DateTimeOffset completedAtUtc)
    {
        ManifestRecordValidation.RequireUtc(completedAtUtc, nameof(completedAtUtc));
        completedAtUtc = AtLeastLatestActivity(completedAtUtc);
        var nextState = ManifestStateMachine.ForfeitMissingContent(
            FlowState,
            missingContentBlocked: true);
        var entitlementReceipt = Entitlement is not null
            ? ToReceipt(Entitlement)
            : null;
        var receipt = new ManifestTerminalReceipt(
            ManifestId,
            ManifestPhase.Forfeited,
            Offers.Select(ToReceipt),
            entitlementReceipt,
            Decisions,
            RelayHistory,
            BrokerFavor,
            BrokerFavor,
            completedAtUtc,
            RarityLadderVersion,
            Ticket.CaseTemplateId, Ticket.OpeningQuality);
        return Copy(
            flowState: nextState,
            entitlement: null,
            clearEntitlement: true,
            relayCandidates: [],
            terminalReceipt: receipt);
    }

    internal ManifestRecord BeginClaim(ManifestClaimPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.ProfileCommitStarted)
        {
            throw new ArgumentException(
                "A newly prepared Claim cannot already have a profile-commit marker.",
                nameof(prepared));
        }

        return new ManifestRecord(
            ManifestId,
            CatalogSnapshotId,
            Commitment,
            ManifestStateMachine.PrepareClaim(FlowState),
            Ticket,
            Offers,
            Decisions,
            Entitlement,
            RelayCandidates,
            RelayHistory,
            BrokerFavor,
            prepared,
            rarityLadderVersion: RarityLadderVersion);
    }

    internal ManifestRecord ReconcileClaim(ManifestClaimPreparedPayload prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (FlowState.Phase is not (ManifestPhase.ClaimPrepared or ManifestPhase.RewardOwed) ||
            ClaimPrepared is null)
        {
            throw new InvalidOperationException("Only an active prepared Claim can reconcile live profile evidence.");
        }
        if (!prepared.ProfileCommitStarted)
        {
            throw new ArgumentException(
                "Reconciled Claim evidence requires a profile-commit marker.",
                nameof(prepared));
        }

        return new ManifestRecord(
            ManifestId,
            CatalogSnapshotId,
            Commitment,
            FlowState,
            Ticket,
            Offers,
            Decisions,
            Entitlement,
            RelayCandidates,
            RelayHistory,
            BrokerFavor,
            prepared,
            rarityLadderVersion: RarityLadderVersion);
    }

    // Only the settlement service may migrate a pending inventory claim, after
    // proving its profile commit has not occurred. The prize itself is unchanged.
    internal ManifestRecord WithClaimDelivery(ManifestClaimPreparedPayload prepared) => new(
        ManifestId, CatalogSnapshotId, Commitment, FlowState, Ticket, Offers, Decisions,
        Entitlement, RelayCandidates, RelayHistory, BrokerFavor, prepared,
        rarityLadderVersion: RarityLadderVersion);

    internal ManifestRecord MarkClaimRewardOwed()
    {
        if (ClaimPrepared is not { ProfileCommitStarted: true })
        {
            throw new InvalidOperationException(
                "Claim profile-commit evidence must be persisted before creating reward debt.");
        }

        return new ManifestRecord(
            ManifestId,
            CatalogSnapshotId,
            Commitment,
            ManifestStateMachine.MarkRewardOwed(FlowState),
            Ticket,
            Offers,
            Decisions,
            Entitlement,
            RelayCandidates,
            RelayHistory,
            BrokerFavor,
            ClaimPrepared,
            rarityLadderVersion: RarityLadderVersion);
    }

    internal (ManifestRecord TerminalManifest, ManifestClaimGrantRecord Grant) CompleteClaim(
        DateTimeOffset completedAtUtc)
    {
        ManifestRecordValidation.RequireUtc(completedAtUtc, nameof(completedAtUtc));
        var prepared = ClaimPrepared is { ProfileCommitStarted: true }
            ? ClaimPrepared
            : throw new InvalidOperationException(
                "Only a Claim with persisted profile-commit evidence can be completed.");
        var entitlement = Entitlement
            ?? throw new InvalidOperationException("A prepared Claim has no entitlement.");

        var minimumCompletion = prepared.PreparedAtUtc;
        if (Ticket.CommittedAtUtc is DateTimeOffset ticketCommittedAt && ticketCommittedAt > minimumCompletion)
        {
            minimumCompletion = ticketCommittedAt;
        }
        if (Decisions.LastOrDefault()?.DecidedAtUtc is DateTimeOffset decidedAt && decidedAt > minimumCompletion)
        {
            minimumCompletion = decidedAt;
        }
        if (RelayHistory.LastOrDefault()?.CompletedAtUtc is DateTimeOffset relayCompletedAt &&
            relayCompletedAt > minimumCompletion)
        {
            minimumCompletion = relayCompletedAt;
        }
        if (completedAtUtc < minimumCompletion)
        {
            completedAtUtc = minimumCompletion;
        }

        var entitlementReceipt = ToReceipt(entitlement);
        var terminalReceipt = new ManifestTerminalReceipt(
            ManifestId,
            ManifestPhase.Granted,
            Offers.Select(ToReceipt),
            entitlementReceipt,
            Decisions,
            RelayHistory,
            BrokerFavor,
            BrokerFavor,
            completedAtUtc,
            RarityLadderVersion,
            Ticket.CaseTemplateId, Ticket.OpeningQuality);
        var terminal = new ManifestRecord(
            ManifestId,
            CatalogSnapshotId,
            Commitment,
            ManifestStateMachine.CompleteClaim(FlowState),
            Ticket,
            Offers,
            Decisions,
            entitlement: null,
            relayCandidates: null,
            RelayHistory,
            BrokerFavor,
            terminalReceipt: terminalReceipt,
            rarityLadderVersion: RarityLadderVersion);
        var grant = new ManifestClaimGrantRecord(
            ManifestId,
            entitlement,
            prepared,
            completedAtUtc);
        return (terminal, grant);
    }

    private static ManifestLotReceiptSnapshot ToReceipt(ManifestOfferSnapshot offer) =>
        new(offer.Rarity, offer.Identity, offer.Fingerprint);

    private static ManifestLotReceiptSnapshot ToReceipt(ManifestEntitlementSnapshot entitlement) =>
        new(entitlement.Rarity, entitlement.Identity, entitlement.Fingerprint);

    private static ManifestLotReceiptSnapshot ToReceipt(ManifestRelayCandidateSnapshot candidate) =>
        new(candidate.Rarity, candidate.Identity, candidate.Fingerprint);

    private static ManifestEntitlementSnapshot ToEntitlement(ManifestOfferSnapshot offer) =>
        new(offer.Rarity, offer.Identity, offer.Forest, offer.Fingerprint);

    private DateTimeOffset AtLeastLatestActivity(
        DateTimeOffset completedAtUtc,
        DateTimeOffset? additionalMinimum = null)
    {
        var minimum = Ticket.CommittedAtUtc ?? Ticket.PreparedAtUtc;
        if (Decisions.LastOrDefault()?.DecidedAtUtc is DateTimeOffset decidedAt && decidedAt > minimum)
        {
            minimum = decidedAt;
        }
        if (RelayHistory.LastOrDefault()?.CompletedAtUtc is DateTimeOffset relayAt && relayAt > minimum)
        {
            minimum = relayAt;
        }
        if (additionalMinimum is DateTimeOffset additional && additional > minimum)
        {
            minimum = additional;
        }
        return completedAtUtc < minimum ? minimum : completedAtUtc;
    }

    private ManifestRecord Copy(
        ManifestFlowState? flowState = null,
        ManifestTicketPayload? ticket = null,
        IEnumerable<ManifestDecisionRecord>? decisions = null,
        ManifestEntitlementSnapshot? entitlement = null,
        bool clearEntitlement = false,
        IEnumerable<ManifestRelayCandidateSnapshot>? relayCandidates = null,
        IEnumerable<ManifestRelayReceipt>? relayHistory = null,
        int? brokerFavor = null,
        ManifestClaimPreparedPayload? claimPrepared = null,
        ManifestRelayPreparedPayload? relayPrepared = null,
        ManifestTerminalReceipt? terminalReceipt = null) => new(
        ManifestId,
        CatalogSnapshotId,
        Commitment,
        flowState ?? FlowState,
        ticket ?? Ticket,
        Offers,
        decisions ?? Decisions,
        clearEntitlement ? null : entitlement ?? Entitlement,
        relayCandidates ?? RelayCandidates,
        relayHistory ?? RelayHistory,
        brokerFavor ?? BrokerFavor,
        claimPrepared,
        relayPrepared,
        terminalReceipt,
        RarityLadderVersion);

    private static void ValidateCandidates(
        ManifestEntitlementSnapshot? entitlement,
        ManifestFlowState flowState,
        IReadOnlyList<ManifestRelayCandidateSnapshot> candidates,
        RarityLadderVersion rarityLadderVersion)
    {
        ValidateCandidatesForEntitlement(
            entitlement,
            flowState.RelayTerminal,
            candidates,
            rarityLadderVersion);
    }

    private static void ValidateCandidatesForEntitlement(
        ManifestEntitlementSnapshot? entitlement,
        bool relayTerminal,
        IReadOnlyList<ManifestRelayCandidateSnapshot> candidates,
        RarityLadderVersion rarityLadderVersion)
    {
        if (candidates.Count == 0)
        {
            return;
        }
        if (entitlement is null)
        {
            throw new ArgumentException("Frozen Relay candidates require a current entitlement.");
        }
        if (relayTerminal)
        {
            throw new ArgumentException("A terminal Relay chain cannot retain candidates.");
        }
        if (candidates.Any(candidate => !candidate.Identity.TrackId.Equals(entitlement.Identity.TrackId)))
        {
            throw new ArgumentException("Every Relay candidate must stay in the entitlement track.");
        }
        if (candidates.Any(candidate => ManifestRecordValidation.SameSemanticIdentity(
                candidate.Identity,
                candidate.Fingerprint,
                entitlement.Identity,
                entitlement.Fingerprint)))
        {
            throw new ArgumentException("A Relay candidate cannot be the staked semantic lot.");
        }
        if (entitlement.Rarity == RewardRarity.BlackLabel)
        {
            throw new ArgumentException("Black Label entitlements cannot retain Relay candidates.");
        }
        var upgradeRarity = RelayRules.GetUpgradeRarity(entitlement.Rarity, rarityLadderVersion);
        if (candidates.Any(candidate => candidate.TargetResult == ManifestRelayResult.Sidegrade
                ? candidate.Rarity != entitlement.Rarity
                : candidate.Rarity != upgradeRarity))
        {
            throw new ArgumentException("Relay candidate grades contradict their target kind.");
        }
        if (candidates.Select(ManifestRecordValidation.SemanticKey).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
        {
            throw new ArgumentException("Frozen Relay candidates must be distinct.");
        }
        if (!candidates.Any(candidate => candidate.TargetResult == ManifestRelayResult.Upgrade) ||
            !candidates.Any(candidate => candidate.TargetResult == ManifestRelayResult.Sidegrade))
        {
            throw new ArgumentException("A frozen Relay pool requires both upgrade and sidegrade candidates.");
        }

        var nodeCount = candidates.Sum(candidate => candidate.Forest.Nodes.Count);
        if (nodeCount > MaximumFrozenRelayNodeCount)
        {
            throw new ArgumentException("Frozen Relay candidate forests exceed the supported bound.");
        }
    }

    private static void ValidateRelayHistory(
        ManifestFlowState flowState,
        IReadOnlyList<ManifestOfferSnapshot> offers,
        ManifestEntitlementSnapshot? entitlement,
        int brokerFavor,
        IReadOnlyList<ManifestRelayReceipt> relayHistory,
        RarityLadderVersion rarityLadderVersion)
    {
        if (relayHistory.Count == 0)
        {
            if (flowState.RelayStage != 1)
            {
                throw new ArgumentException("A manifest cannot advance Relay stage without committed Relay evidence.");
            }
            if (!flowState.IsTerminal && flowState.LockedOrdinal is not null && flowState.RelayTerminal)
            {
                throw new ArgumentException("An initial entitlement cannot be Relay-terminal without committed Relay evidence.");
            }
            if (entitlement is not null && flowState.LockedOrdinal is int lockedOrdinal &&
                !ManifestRecordValidation.SameSemanticLot(offers[lockedOrdinal - 1], entitlement))
            {
                throw new ArgumentException("Initial entitlement must match the locked offer.");
            }
            return;
        }

        var first = relayHistory[0];
        if (relayHistory.Any(receipt => receipt.RarityLadderVersion != rarityLadderVersion))
        {
            throw new ArgumentException("Relay history contains a different rarity ladder version.");
        }
        if (flowState.LockedOrdinal is not int selectedOrdinal ||
            !ManifestRecordValidation.SameReceiptLot(first.Input, offers[selectedOrdinal - 1]))
        {
            throw new ArgumentException("Relay history must begin with the locked offer.");
        }
        for (var index = 1; index < relayHistory.Count; index++)
        {
            if (relayHistory[index - 1].Output is null ||
                !ManifestRecordValidation.SameReceiptLot(relayHistory[index].Input, relayHistory[index - 1].Output!))
            {
                throw new ArgumentException("Committed Relay receipt ancestry is discontinuous.");
            }
        }

        var latest = relayHistory[^1];
        if (brokerFavor != latest.BrokerFavorAfter)
        {
            throw new ArgumentException("Manifest Broker Favor must equal the latest committed Relay receipt.");
        }
        if ((latest.Outcome == ManifestRelayResult.Confiscated) !=
            (flowState.Phase == ManifestPhase.Confiscated))
        {
            throw new ArgumentException("Confiscated flow state and committed Relay history must agree.");
        }

        if (!flowState.IsTerminal)
        {
            var expectedRelayTerminal = latest.Outcome switch
            {
                ManifestRelayResult.Sidegrade => true,
                ManifestRelayResult.Upgrade =>
                    latest.Output?.Rarity == RewardRarity.BlackLabel ||
                    latest.RelayStage == RelayRules.MaximumStage,
                ManifestRelayResult.Confiscated => true,
                _ => throw new ArgumentOutOfRangeException(nameof(latest.Outcome))
            };
            if (flowState.RelayTerminal != expectedRelayTerminal)
            {
                throw new ArgumentException("Relay terminal state contradicts the latest committed Relay receipt.");
            }
        }

        var expectedStage = latest.Outcome == ManifestRelayResult.Upgrade && latest.RelayStage < RelayRules.MaximumStage
            ? latest.RelayStage + 1
            : latest.RelayStage;
        if (flowState.RelayStage != expectedStage)
        {
            throw new ArgumentException("Relay stage contradicts committed Relay history.");
        }
        if (entitlement is not null &&
            (latest.Output is null || !ManifestRecordValidation.SameReceiptLot(latest.Output, entitlement)))
        {
            throw new ArgumentException("Current entitlement must equal the latest committed Relay output.");
        }
    }

    private static void ValidatePhasePayloads(
        string manifestId,
        ManifestFlowState flowState,
        ManifestEntitlementSnapshot? entitlement,
        IReadOnlyList<ManifestRelayCandidateSnapshot> candidates,
        IReadOnlyList<ManifestRelayReceipt> relayHistory,
        int brokerFavor,
        ManifestClaimPreparedPayload? claimPrepared,
        ManifestRelayPreparedPayload? relayPrepared,
        ManifestTerminalReceipt? terminalReceipt,
        IReadOnlyList<ManifestOfferSnapshot> offers,
        IReadOnlyList<ManifestDecisionRecord> decisions,
        RarityLadderVersion rarityLadderVersion)
    {
        var requiresEntitlement = flowState.Phase is ManifestPhase.Entitlement or
            ManifestPhase.ClaimPrepared or ManifestPhase.RelayPrepared or ManifestPhase.RewardOwed;
        if (requiresEntitlement != (entitlement is not null))
        {
            throw new ArgumentException("Manifest phase and current entitlement contradict each other.");
        }

        var requiresClaim = flowState.Phase is ManifestPhase.ClaimPrepared or ManifestPhase.RewardOwed;
        if (requiresClaim != (claimPrepared is not null) ||
            (flowState.Phase == ManifestPhase.RewardOwed && claimPrepared is { ProfileCommitStarted: false }))
        {
            throw new ArgumentException("Manifest phase and ClaimPrepared payload contradict each other.");
        }
        if (claimPrepared is not null)
        {
            claimPrepared.ValidateAgainst(entitlement!.Forest);
        }

        if ((flowState.Phase == ManifestPhase.RelayPrepared) != (relayPrepared is not null))
        {
            throw new ArgumentException("Manifest phase and RelayPrepared payload contradict each other.");
        }
        if (relayPrepared is not null)
        {
            if (candidates.Count == 0)
            {
                throw new ArgumentException("Prepared Relay requires its frozen complete candidate pool.");
            }
            if (brokerFavor != relayPrepared.BrokerFavorBefore)
            {
                throw new ArgumentException("Prepared Relay Favor must begin at the manifest's current Favor.");
            }

            var expectedOdds = RelayRules.GetOdds(flowState.RelayStage);
            if (relayPrepared.Odds != expectedOdds)
            {
                throw new ArgumentException("Prepared Relay odds do not match the current Relay stage.");
            }
            if (relayPrepared.OutcomeRng.DrawOrdinal != flowState.RelayStage ||
                relayPrepared.TargetRng is not null && relayPrepared.TargetRng.DrawOrdinal != flowState.RelayStage)
            {
                throw new ArgumentException("Prepared Relay RNG draw ordinals must equal the current Relay stage.");
            }
            if (relayPrepared.Output is not null)
            {
                var eligibleTargets = candidates
                    .Where(candidate => candidate.TargetResult == relayPrepared.Outcome)
                    .ToArray();
                var selected = ManifestRecordValidation.SelectRelayTarget(
                    eligibleTargets,
                    relayPrepared.TargetRng!);
                if (!ManifestRecordValidation.SameSemanticLot(selected, relayPrepared.Output))
                {
                    throw new ArgumentException("Prepared Relay output contradicts the frozen eligible targets and target RNG.");
                }
            }

            var completedRelayState = ManifestStateMachine.CompleteRelay(
                flowState,
                relayPrepared.Outcome,
                relayPrepared.Output?.Rarity);
            var requiresNextCandidates = completedRelayState.Phase == ManifestPhase.Entitlement &&
                !completedRelayState.RelayTerminal;
            if (requiresNextCandidates)
            {
                if (relayPrepared.Output is null || relayPrepared.NextRelayCandidates.Count == 0)
                {
                    throw new ArgumentException(
                        "A non-terminal Relay upgrade must persist its next-stage candidate pool before key consumption.");
                }
                ValidateCandidatesForEntitlement(
                    relayPrepared.Output,
                    relayTerminal: false,
                    relayPrepared.NextRelayCandidates,
                    rarityLadderVersion);
            }
            else if (relayPrepared.NextRelayCandidates.Count != 0)
            {
                throw new ArgumentException(
                    "A terminal Relay result cannot retain a next-stage candidate pool.");
            }
        }

        if (flowState.IsTerminal != (terminalReceipt is not null))
        {
            throw new ArgumentException("Manifest phase and terminal receipt contradict each other.");
        }
        if (terminalReceipt is null)
        {
            return;
        }
        if (!string.Equals(terminalReceipt.ManifestId, manifestId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Terminal receipt manifest ID is invalid.");
        }
        if (terminalReceipt.RarityLadderVersion != rarityLadderVersion)
        {
            throw new ArgumentException("Terminal receipt rarity ladder version does not match the manifest.");
        }
        if (terminalReceipt.TerminalPhase != flowState.Phase || terminalReceipt.BrokerFavorAfter != brokerFavor ||
            terminalReceipt.Offers.Count != offers.Count ||
            terminalReceipt.Decisions.Count != decisions.Count ||
            terminalReceipt.RelayHistory.Count != relayHistory.Count)
        {
            throw new ArgumentException("Terminal receipt does not match the terminal manifest state.");
        }
        for (var index = 0; index < offers.Count; index++)
        {
            if (!ManifestRecordValidation.SameReceiptLot(terminalReceipt.Offers[index], offers[index]))
            {
                throw new ArgumentException("Terminal receipt offer metadata does not match persisted offers.");
            }
        }
        var expectedTerminalEntitlement = relayHistory.LastOrDefault() is ManifestRelayReceipt latest
            ? latest.Outcome == ManifestRelayResult.Confiscated ? latest.Input : latest.Output
            : flowState.LockedOrdinal is int lockedOrdinal
                ? new ManifestLotReceiptSnapshot(
                    offers[lockedOrdinal - 1].Rarity,
                    offers[lockedOrdinal - 1].Identity,
                    offers[lockedOrdinal - 1].Fingerprint)
                : null;
        if ((flowState.LockedOrdinal is not null) != (terminalReceipt.Entitlement is not null) ||
            terminalReceipt.Entitlement is not null &&
            (expectedTerminalEntitlement is null ||
             !ManifestRecordValidation.SameReceiptLot(terminalReceipt.Entitlement, expectedTerminalEntitlement)))
        {
            throw new ArgumentException("Terminal receipt entitlement metadata does not match the manifest.");
        }
        for (var index = 0; index < relayHistory.Count; index++)
        {
            if (!ManifestRecordValidation.SameRelayReceipt(terminalReceipt.RelayHistory[index], relayHistory[index]))
            {
                throw new ArgumentException("Terminal receipt Relay history does not match the manifest.");
            }
        }
        for (var index = 0; index < decisions.Count; index++)
        {
            if (terminalReceipt.Decisions[index].Ordinal != decisions[index].Ordinal ||
                terminalReceipt.Decisions[index].Decision != decisions[index].Decision ||
                terminalReceipt.Decisions[index].DecidedAtUtc != decisions[index].DecidedAtUtc)
            {
                throw new ArgumentException("Terminal receipt decision history does not match the manifest.");
            }
        }
    }

    private static void ValidateDecisionHistory(
        ManifestFlowState flowState,
        IReadOnlyList<ManifestDecisionRecord> decisions)
    {
        (int Ordinal, ManifestOfferDecision Decision)[] expected = flowState.LockedOrdinal switch
        {
            1 => [(1, ManifestOfferDecision.Lock)],
            2 => [(1, ManifestOfferDecision.Burn), (2, ManifestOfferDecision.Lock)],
            3 => [(1, ManifestOfferDecision.Burn), (2, ManifestOfferDecision.Burn)],
            null when flowState.CurrentOrdinal == 1 => [],
            null when flowState.CurrentOrdinal == 2 => [(1, ManifestOfferDecision.Burn)],
            _ => throw new ArgumentException("Manifest flow state has no canonical decision history.")
        };
        if (decisions.Count != expected.Length || decisions.Where((decision, index) =>
                decision.Ordinal != expected[index].Ordinal || decision.Decision != expected[index].Decision).Any())
        {
            throw new ArgumentException("Lock/Burn decision history contradicts the manifest flow state.", nameof(decisions));
        }
    }
}

internal static class ManifestRecordValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static void ValidateRarity(RewardRarity rarity, string parameterName)
    {
        if (!Enum.IsDefined(rarity))
        {
            throw new ArgumentOutOfRangeException(parameterName, rarity, "Cargo lot grade is not recognized.");
        }
    }

    internal static void ValidateSemanticLot(
        CargoLotIdentitySnapshot? identity,
        RewardForest? forest,
        RewardForestFingerprintV2? fingerprint)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(forest);
        ArgumentNullException.ThrowIfNull(fingerprint);
        var computed = RewardForestFingerprintV2.Compute(identity.ProviderId, identity.LotId, forest);
        if (!computed.Equals(fingerprint) || !identity.Fingerprint.Equals(fingerprint))
        {
            throw new ArgumentException("Persisted semantic lot fingerprint does not match its identity and forest.", nameof(fingerprint));
        }
    }

    internal static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Manifest timestamps must be UTC.", parameterName);
        }
    }

    internal static string RequireIdentifier(string? value, string parameterName)
    {
        int byteCount;
        try
        {
            byteCount = value is null ? 0 : StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Manifest identifiers must contain valid Unicode scalar text.", parameterName);
        }
        if (string.IsNullOrWhiteSpace(value) || byteCount > 512)
        {
            throw new ArgumentException("A bounded manifest identifier is required.", parameterName);
        }

        return value;
    }

    internal static void ValidateFavor(int value, string parameterName)
    {
        if (value is < 0 or > RelayRules.MaximumRecoveryMeter)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Broker Favor must be between zero and three.");
        }
    }

    internal static void ValidateClaimItemIdsAgainstTicket(
        ManifestClaimPreparedPayload claim,
        ManifestTicketPayload ticket)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(ticket);
        if (claim.ExactItemIds.Contains(ticket.CaseId) || claim.ExactItemIds.Contains(ticket.KeyId))
        {
            throw new ArgumentException(
                "Claim item IDs must be distinct from the consumed case and key IDs.",
                nameof(claim));
        }
    }

    internal static void ValidateFavorTransition(
        int before,
        int after,
        ManifestRelayResult outcome)
    {
        ValidateFavor(before, nameof(before));
        ValidateFavor(after, nameof(after));
        var expected = before == RelayRules.MaximumRecoveryMeter
            ? outcome == ManifestRelayResult.Upgrade
                ? 0
                : throw new ArgumentException("Full Broker Favor requires an upgrade.", nameof(outcome))
            : outcome == ManifestRelayResult.Confiscated
                ? checked(before + 1)
                : before;
        if (after != expected)
        {
            throw new ArgumentException("Broker Favor transition contradicts the Relay outcome.", nameof(after));
        }
    }

    internal static void ValidateOdds(RelayOdds odds)
    {
        if (odds.UpgradePercent is < 0 or > 100 ||
            odds.SidegradePercent is < 0 or > 100 ||
            odds.ConfiscatePercent is < 0 or > 100 ||
            odds.TotalPercent != 100)
        {
            throw new ArgumentException("Relay odds must be bounded percentages totaling 100.", nameof(odds));
        }
    }

    internal static ManifestRelayResult SelectRelayOutcome(
        RelayOdds odds,
        int brokerFavor,
        CanonicalRngEvidence evidence)
    {
        if (brokerFavor == RelayRules.MaximumRecoveryMeter)
        {
            return ManifestRelayResult.Upgrade;
        }

        var percentileNumerator = checked(evidence.UnitNumerator * 100L);
        var upgradeBoundary = checked((long)odds.UpgradePercent * CanonicalRngEvidence.UnitDenominator);
        if (percentileNumerator < upgradeBoundary)
        {
            return ManifestRelayResult.Upgrade;
        }

        var sidegradeBoundary = checked(
            (long)(odds.UpgradePercent + odds.SidegradePercent) * CanonicalRngEvidence.UnitDenominator);
        return percentileNumerator < sidegradeBoundary
            ? ManifestRelayResult.Sidegrade
            : ManifestRelayResult.Confiscated;
    }

    internal static void ValidateDecisionOrdering(IReadOnlyList<ManifestDecisionRecord> decisions)
    {
        if (decisions.Count > 2 || decisions.Any(decision => decision is null))
        {
            throw new ArgumentException("Decision history must be non-null and bounded.", nameof(decisions));
        }
        for (var index = 1; index < decisions.Count; index++)
        {
            if (decisions[index - 1].Ordinal >= decisions[index].Ordinal ||
                decisions[index - 1].DecidedAtUtc > decisions[index].DecidedAtUtc)
            {
                throw new ArgumentException("Decision history must use deterministic ordinal and timestamp order.", nameof(decisions));
            }
        }
    }

    internal static void ValidateRelayHistory(IReadOnlyList<ManifestRelayReceipt> history)
    {
        if (history.Count > ManifestRecord.MaximumRelayReceiptCount || history.Any(receipt => receipt is null))
        {
            throw new ArgumentException("Committed Relay history must be non-null and bounded.", nameof(history));
        }
        for (var index = 0; index < history.Count; index++)
        {
            if (history[index].RelayStage != index + 1)
            {
                throw new ArgumentException("Committed Relay history must use deterministic sequential stages.", nameof(history));
            }
            if (index > 0 &&
                (history[index - 1].Outcome != ManifestRelayResult.Upgrade ||
                 history[index - 1].BrokerFavorAfter != history[index].BrokerFavorBefore ||
                 history[index - 1].CompletedAtUtc > history[index].CompletedAtUtc))
            {
                throw new ArgumentException("Committed Relay history is not a continuous ordered chain.", nameof(history));
            }
            if (index < history.Count - 1 && history[index].Output?.Rarity == RewardRarity.BlackLabel)
            {
                throw new ArgumentException("Black Label Relay output must terminate receipt history.", nameof(history));
            }
        }
    }

    internal static string SemanticKey(ManifestOfferSnapshot snapshot) =>
        SemanticKey(snapshot.Identity, snapshot.Fingerprint);

    internal static string SemanticKey(ManifestRelayCandidateSnapshot snapshot) =>
        SemanticKey(snapshot.Identity, snapshot.Fingerprint);

    internal static string SemanticKey(ManifestLotReceiptSnapshot snapshot) =>
        SemanticKey(snapshot.Identity, snapshot.Fingerprint);

    private static string SemanticKey(CargoLotIdentitySnapshot identity, RewardForestFingerprintV2 fingerprint) =>
        string.Concat(identity.ProviderId, "\0", identity.LotId, "\0", fingerprint.Sha256Hex);

    internal static IEnumerable<ManifestRelayCandidateSnapshot> CanonicalOrder(
        IEnumerable<ManifestRelayCandidateSnapshot> candidates) =>
        candidates
            .OrderBy(candidate => candidate.TargetResult)
            .ThenBy(candidate => candidate.Identity.FamilyId.Value, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Identity.ProviderId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Identity.LotId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Fingerprint.Sha256Hex, StringComparer.Ordinal);

    internal static IEnumerable<ManifestLotReceiptSnapshot> CanonicalOrder(
        IEnumerable<ManifestLotReceiptSnapshot> candidates) =>
        candidates
            .OrderBy(candidate => candidate.Identity.FamilyId.Value, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Identity.ProviderId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Identity.LotId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Fingerprint.Sha256Hex, StringComparer.Ordinal);

    internal static ManifestRelayCandidateSnapshot SelectRelayTarget(
        IReadOnlyList<ManifestRelayCandidateSnapshot> candidates,
        CanonicalRngEvidence evidence) =>
        SelectRelayTarget(
            CanonicalOrder(candidates).ToArray(),
            evidence,
            candidate => candidate.Identity);

    internal static ManifestLotReceiptSnapshot SelectRelayTarget(
        IReadOnlyList<ManifestLotReceiptSnapshot> candidates,
        CanonicalRngEvidence evidence) =>
        SelectRelayTarget(
            CanonicalOrder(candidates).ToArray(),
            evidence,
            candidate => candidate.Identity);

    private static T SelectRelayTarget<T>(
        IReadOnlyList<T> candidates,
        CanonicalRngEvidence evidence,
        Func<T, CargoLotIdentitySnapshot> identitySelector)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Purpose != ManifestRngPurpose.RelayTargetSelection)
        {
            throw new ArgumentException("Relay target RNG evidence has the wrong purpose.", nameof(evidence));
        }
        if (candidates.Count == 0)
        {
            throw new ArgumentException("Relay target selection requires at least one eligible candidate.", nameof(candidates));
        }

        var exactWeights = new (BigInteger Significand, int BinaryExponent)[candidates.Count];
        var minimumExponent = int.MaxValue;
        for (var index = 0; index < candidates.Count; index++)
        {
            var weight = identitySelector(candidates[index]).Weight;
            if (!double.IsFinite(weight) || weight <= 0d)
            {
                throw new ArgumentException("Relay candidate identity weights must be positive and finite.", nameof(candidates));
            }

            exactWeights[index] = ExactPositiveDouble(weight);
            minimumExponent = Math.Min(minimumExponent, exactWeights[index].BinaryExponent);
        }

        var scaledWeights = exactWeights
            .Select(weight => weight.Significand << checked(weight.BinaryExponent - minimumExponent))
            .ToArray();
        var totalWeight = scaledWeights.Aggregate(BigInteger.Zero, (total, weight) => total + weight);
        var draw = new BigInteger(evidence.UnitNumerator);
        var denominator = new BigInteger(CanonicalRngEvidence.UnitDenominator);
        var cumulative = BigInteger.Zero;
        for (var index = 0; index < candidates.Count; index++)
        {
            cumulative += scaledWeights[index];
            if (draw * totalWeight < denominator * cumulative)
            {
                return candidates[index];
            }
        }

        throw new InvalidOperationException("A canonical Relay target draw did not select a candidate.");
    }

    private static (BigInteger Significand, int BinaryExponent) ExactPositiveDouble(double value)
    {
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var exponentBits = checked((int)((bits >> 52) & 0x7ffUL));
        var fraction = bits & 0x000f_ffff_ffff_ffffUL;
        return exponentBits == 0
            ? (new BigInteger(fraction), -1074)
            : (new BigInteger(fraction | (1UL << 52)), exponentBits - 1075);
    }

    internal static bool SameSemanticIdentity(
        CargoLotIdentitySnapshot leftIdentity,
        RewardForestFingerprintV2 leftFingerprint,
        CargoLotIdentitySnapshot rightIdentity,
        RewardForestFingerprintV2 rightFingerprint) =>
        string.Equals(leftIdentity.ProviderId, rightIdentity.ProviderId, StringComparison.Ordinal) &&
        string.Equals(leftIdentity.LotId, rightIdentity.LotId, StringComparison.Ordinal) &&
        leftFingerprint.Equals(rightFingerprint);

    internal static bool SameSemanticLot(
        ManifestRelayCandidateSnapshot candidate,
        ManifestEntitlementSnapshot entitlement) =>
        candidate.Rarity == entitlement.Rarity &&
        SameIdentity(candidate.Identity, entitlement.Identity) &&
        candidate.Fingerprint.Equals(entitlement.Fingerprint);

    internal static bool SameSemanticLot(
        ManifestOfferSnapshot offer,
        ManifestEntitlementSnapshot entitlement) =>
        offer.Rarity == entitlement.Rarity &&
        SameIdentity(offer.Identity, entitlement.Identity) &&
        offer.Fingerprint.Equals(entitlement.Fingerprint);

    internal static bool SameReceiptLot(
        ManifestLotReceiptSnapshot receipt,
        ManifestOfferSnapshot offer) =>
        receipt.Rarity == offer.Rarity &&
        SameIdentity(receipt.Identity, offer.Identity) &&
        receipt.Fingerprint.Equals(offer.Fingerprint);

    internal static bool SameReceiptLot(
        ManifestLotReceiptSnapshot receipt,
        ManifestEntitlementSnapshot entitlement) =>
        receipt.Rarity == entitlement.Rarity &&
        SameIdentity(receipt.Identity, entitlement.Identity) &&
        receipt.Fingerprint.Equals(entitlement.Fingerprint);

    internal static bool SameReceiptLot(
        ManifestLotReceiptSnapshot left,
        ManifestLotReceiptSnapshot right) =>
        left.Rarity == right.Rarity &&
        SameIdentity(left.Identity, right.Identity) &&
        left.Fingerprint.Equals(right.Fingerprint);

    internal static bool SameRelayReceipt(ManifestRelayReceipt left, ManifestRelayReceipt right) =>
        left.RelayStage == right.RelayStage &&
        SameReceiptLot(left.Input, right.Input) &&
        left.Outcome == right.Outcome &&
        ((left.Output is null && right.Output is null) ||
         (left.Output is not null && right.Output is not null && SameReceiptLot(left.Output, right.Output))) &&
        left.Odds == right.Odds &&
        SameRng(left.OutcomeRng, right.OutcomeRng) &&
        ((left.TargetRng is null && right.TargetRng is null) ||
         (left.TargetRng is not null && right.TargetRng is not null && SameRng(left.TargetRng, right.TargetRng))) &&
        left.EligibleTargets.Count == right.EligibleTargets.Count &&
        !left.EligibleTargets.Where((candidate, index) =>
            !SameReceiptLot(candidate, right.EligibleTargets[index])).Any() &&
        left.BrokerFavorBefore == right.BrokerFavorBefore &&
        left.BrokerFavorAfter == right.BrokerFavorAfter &&
        left.CompletedAtUtc == right.CompletedAtUtc;

    private static bool SameRng(CanonicalRngEvidence left, CanonicalRngEvidence right) =>
        left.Purpose == right.Purpose &&
        left.DrawOrdinal == right.DrawOrdinal &&
        left.UnitNumerator == right.UnitNumerator;

    internal static bool SameIdentity(
        CargoLotIdentitySnapshot left,
        CargoLotIdentitySnapshot right) =>
        string.Equals(left.ProviderId, right.ProviderId, StringComparison.Ordinal) &&
        string.Equals(left.PackVersion, right.PackVersion, StringComparison.Ordinal) &&
        string.Equals(left.LotId, right.LotId, StringComparison.Ordinal) &&
        string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) &&
        string.Equals(left.Purpose, right.Purpose, StringComparison.Ordinal) &&
        left.FamilyId.Equals(right.FamilyId) &&
        left.TrackId.Equals(right.TrackId) &&
        string.Equals(left.AnchorTemplateId, right.AnchorTemplateId, StringComparison.Ordinal) &&
        left.Weight.Equals(right.Weight) &&
        SameUsePath(left.UsePath, right.UsePath) &&
        left.Fingerprint.Equals(right.Fingerprint);

    private static bool SameUsePath(UsePath left, UsePath right) => (left, right) switch
    {
        (RaidRole leftRole, RaidRole rightRole) =>
            string.Equals(leftRole.RoleId, rightRole.RoleId, StringComparison.Ordinal),
        (Collection leftCollection, Collection rightCollection) =>
            string.Equals(leftCollection.ContainerTemplateId, rightCollection.ContainerTemplateId, StringComparison.Ordinal) &&
            string.Equals(leftCollection.CollectionId, rightCollection.CollectionId, StringComparison.Ordinal),
        (Craft leftCraft, Craft rightCraft) =>
            string.Equals(leftCraft.ProductionId, rightCraft.ProductionId, StringComparison.Ordinal),
        (Barter leftBarter, Barter rightBarter) =>
            string.Equals(leftBarter.TraderId, rightBarter.TraderId, StringComparison.Ordinal) &&
            string.Equals(leftBarter.AssortId, rightBarter.AssortId, StringComparison.Ordinal),
        _ => false
    };
}
