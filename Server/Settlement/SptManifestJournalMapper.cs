using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using CargoCollection = ContrabandCases.Shared.Catalog.Collection;

namespace ContrabandCases.Server.Settlement;

internal static class SptManifestJournalMapper
{
    private const string RaidRoleUsePath = "raidRole";
    private const string CollectionUsePath = "collection";
    private const string CraftUsePath = "craft";
    private const string BarterUsePath = "barter";

    internal static SptManifestRecordDocument ToDocument(ManifestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new SptManifestRecordDocument
        {
            ManifestId = record.ManifestId,
            CatalogSnapshotId = record.CatalogSnapshotId,
            RarityLadderVersion = record.RarityLadderVersion,
            Commitment = ToDocument(record.Commitment),
            FlowState = ToDocument(record.FlowState),
            Ticket = ToDocument(record.Ticket),
            Offers = record.Offers.Select(ToDocument).ToList(),
            Decisions = record.Decisions.Select(ToDocument).ToList(),
            Entitlement = record.Entitlement is null ? null : ToDocument(record.Entitlement),
            RelayCandidates = record.RelayCandidates.Select(ToDocument).ToList(),
            RelayHistory = record.RelayHistory.Select(ToDocument).ToList(),
            BrokerFavor = record.BrokerFavor,
            ClaimPrepared = record.ClaimPrepared is null ? null : ToDocument(record.ClaimPrepared),
            RelayPrepared = record.RelayPrepared is null ? null : ToDocument(record.RelayPrepared),
            TerminalReceipt = record.TerminalReceipt is null ? null : ToDocument(record.TerminalReceipt)
        };
    }

    internal static ManifestRecord FromDocument(
        SptManifestRecordDocument document,
        RarityLadderVersion fallbackRarityLadderVersion,
        bool requirePersistedRarityLadderVersion)
    {
        ArgumentNullException.ThrowIfNull(document);
        var rarityLadderVersion = ResolveRarityLadderVersion(
            document.RarityLadderVersion,
            fallbackRarityLadderVersion,
            requirePersistedRarityLadderVersion,
            "manifest rarity ladder version");
        return new ManifestRecord(
            RequireString(document.ManifestId, "manifest ID"),
            RequireString(document.CatalogSnapshotId, "catalog snapshot ID"),
            FromDocument(Require(document.Commitment, "manifest commitment")),
            FromDocument(Require(document.FlowState, "flow state")),
            FromDocument(Require(document.Ticket, "ticket")),
            MapRequired(document.Offers, "offers", 1, ManifestRecord.OfferCount, FromDocument),
            MapRequired(document.Decisions, "decisions", 0, ManifestRecord.OfferCount - 1, FromDocument),
            document.Entitlement is null ? null : FromDocument(document.Entitlement),
            MapRelayCandidates(document.RelayCandidates),
            MapRequired(
                document.RelayHistory,
                "Relay history",
                0,
                ManifestRecord.MaximumRelayReceiptCount,
                relayReceipt => FromDocument(
                    relayReceipt,
                    rarityLadderVersion,
                    requirePersistedRarityLadderVersion)),
            RequireValue(document.BrokerFavor, "Broker Favor"),
            document.ClaimPrepared is null ? null : FromDocument(document.ClaimPrepared),
            document.RelayPrepared is null ? null : FromDocument(document.RelayPrepared),
            document.TerminalReceipt is null
                ? null
                : FromDocument(
                    document.TerminalReceipt,
                    rarityLadderVersion,
                    requirePersistedRarityLadderVersion),
            rarityLadderVersion: rarityLadderVersion);
    }

    internal static List<SptManifestTerminalReceiptDocument> ToDocuments(
        IEnumerable<ManifestTerminalReceipt> receipts)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        return receipts.Select(ToDocument).ToList();
    }

    internal static IReadOnlyList<ManifestTerminalReceipt> FromDocuments(
        List<SptManifestTerminalReceiptDocument>? documents,
        RarityLadderVersion fallbackRarityLadderVersion,
        bool requirePersistedRarityLadderVersion) =>
        MapRequired(
            documents,
            "manifest receipts",
            0,
            CaseOpeningJournal.RetainedManifestReceiptCount,
            receipt => FromDocument(
                receipt,
                fallbackRarityLadderVersion,
                requirePersistedRarityLadderVersion));

    internal static List<SptManifestClaimGrantDocument> ToClaimGrantDocuments(
        IEnumerable<ManifestClaimGrantRecord> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        return grants.Select(ToDocument).ToList();
    }

    internal static IReadOnlyList<ManifestClaimGrantRecord> FromClaimGrantDocuments(
        List<SptManifestClaimGrantDocument>? documents) =>
        MapRequired(
            documents,
            "manifest Claim grants",
            0,
            CaseOpeningJournal.RetainedManifestReceiptCount,
            FromDocument);

    private static SptManifestFlowStateDocument ToDocument(ManifestFlowState state) => new()
    {
        Phase = state.Phase,
        CurrentOrdinal = state.CurrentOrdinal,
        LockedOrdinal = state.LockedOrdinal,
        RelayStage = state.RelayStage,
        RelayTerminal = state.RelayTerminal
    };

    private static ManifestFlowState FromDocument(SptManifestFlowStateDocument document) =>
        ManifestFlowState.Restore(
            RequireValue(document.Phase, "manifest phase"),
            RequireValue(document.CurrentOrdinal, "current offer ordinal"),
            document.LockedOrdinal,
            RequireValue(document.RelayStage, "Relay stage"),
            RequireValue(document.RelayTerminal, "Relay terminal marker"));

    private static SptManifestCommitmentDocument ToDocument(ManifestCommitmentEvidence commitment) => new()
    {
        Version = commitment.Version,
        CommitmentSha256Hex = commitment.CommitmentSha256Hex,
        NonceHex = commitment.NonceHex
    };

    private static ManifestCommitmentEvidence FromDocument(SptManifestCommitmentDocument document) => new(
        RequireValue(document.Version, "manifest commitment version"),
        RequireString(document.CommitmentSha256Hex, "manifest commitment digest"),
        RequireString(document.NonceHex, "manifest commitment nonce"));

    private static SptManifestTicketDocument ToDocument(ManifestTicketPayload ticket) => new()
    {
        CaseId = ticket.CaseId,
        CaseTemplateId = ticket.CaseTemplateId,
        OpeningQuality = ticket.OpeningQuality,
        KeyId = ticket.KeyId,
        PreparedAtUtc = ticket.PreparedAtUtc,
        ProfileCommitStarted = ticket.ProfileCommitStarted,
        Committed = ticket.Committed,
        CommittedAtUtc = ticket.CommittedAtUtc,
        CommitGeneration = ticket.CommitGeneration,
        CommitPredecessorHash = ticket.CommitPredecessorHash
    };

    private static ManifestTicketPayload FromDocument(SptManifestTicketDocument document) => new(
        RequireValue(document.CaseId, "ticket case ID"),
        RequireValue(document.KeyId, "ticket key ID"),
        RequireValue(document.PreparedAtUtc, "ticket prepared timestamp"),
        RequireValue(document.ProfileCommitStarted, "ticket profile-commit marker"),
        RequireValue(document.Committed, "ticket committed marker"),
        document.CommittedAtUtc,
        RequireValue(document.CommitGeneration, "ticket commit generation"),
        RequireString(document.CommitPredecessorHash, "ticket commit predecessor hash"),
        document.CaseTemplateId ?? ContrabandCases.Shared.ModConstants.CaseTemplateId,
        document.OpeningQuality);

    private static SptManifestOfferDocument ToDocument(ManifestOfferSnapshot offer) => new()
    {
        Ordinal = offer.Ordinal,
        Rarity = offer.Rarity,
        Identity = ToDocument(offer.Identity),
        Forest = ToDocument(offer.Forest),
        Fingerprint = ToDocument(offer.Fingerprint),
        RngEvidence = ToDocument(offer.RngEvidence)
    };

    private static ManifestOfferSnapshot FromDocument(SptManifestOfferDocument document) => new(
        RequireValue(document.Ordinal, "offer ordinal"),
        RequireValue(document.Rarity, "offer grade"),
        FromDocument(Require(document.Identity, "offer identity")),
        FromDocument(Require(document.Forest, "offer reward forest")),
        FromDocument(Require(document.Fingerprint, "offer fingerprint")),
        FromDocument(Require(document.RngEvidence, "offer RNG evidence")));

    private static SptManifestDecisionDocument ToDocument(ManifestDecisionRecord decision) => new()
    {
        Ordinal = decision.Ordinal,
        Decision = decision.Decision,
        DecidedAtUtc = decision.DecidedAtUtc
    };

    private static ManifestDecisionRecord FromDocument(SptManifestDecisionDocument document) => new(
        RequireValue(document.Ordinal, "decision ordinal"),
        RequireValue(document.Decision, "offer decision"),
        RequireValue(document.DecidedAtUtc, "decision timestamp"));

    private static SptManifestEntitlementDocument ToDocument(ManifestEntitlementSnapshot entitlement) => new()
    {
        Rarity = entitlement.Rarity,
        Identity = ToDocument(entitlement.Identity),
        Forest = ToDocument(entitlement.Forest),
        Fingerprint = ToDocument(entitlement.Fingerprint)
    };

    private static ManifestEntitlementSnapshot FromDocument(SptManifestEntitlementDocument document) => new(
        RequireValue(document.Rarity, "entitlement grade"),
        FromDocument(Require(document.Identity, "entitlement identity")),
        FromDocument(Require(document.Forest, "entitlement reward forest")),
        FromDocument(Require(document.Fingerprint, "entitlement fingerprint")));

    private static SptManifestRelayCandidateDocument ToDocument(ManifestRelayCandidateSnapshot candidate) => new()
    {
        TargetResult = candidate.TargetResult,
        Rarity = candidate.Rarity,
        Identity = ToDocument(candidate.Identity),
        Forest = ToDocument(candidate.Forest),
        Fingerprint = ToDocument(candidate.Fingerprint)
    };

    private static ManifestRelayCandidateSnapshot FromDocument(SptManifestRelayCandidateDocument document) => new(
        RequireValue(document.TargetResult, "Relay candidate result"),
        RequireValue(document.Rarity, "Relay candidate grade"),
        FromDocument(Require(document.Identity, "Relay candidate identity")),
        FromDocument(Require(document.Forest, "Relay candidate reward forest")),
        FromDocument(Require(document.Fingerprint, "Relay candidate fingerprint")));

    private static IReadOnlyList<ManifestRelayCandidateSnapshot> MapRelayCandidates(
        List<SptManifestRelayCandidateDocument>? documents,
        string fieldPrefix = "Relay")
    {
        var candidatesField = $"{fieldPrefix} candidates";
        var forestField = $"{fieldPrefix} candidate reward forest";
        var bounded = RequireBounded(
            documents,
            candidatesField,
            0,
            ManifestRecord.MaximumRelayCandidateCount);
        var nodeCount = 0;
        for (var index = 0; index < bounded.Count; index++)
        {
            var candidate = bounded[index] ?? throw Invalid(candidatesField);
            var forest = Require(candidate.Forest, forestField);
            var nodes = RequireBounded(
                forest.Nodes,
                $"{forestField} nodes",
                1,
                RewardForest.MaxNodeCount);
            if (nodeCount > ManifestRecord.MaximumFrozenRelayNodeCount - nodes.Count)
            {
                throw Invalid($"{forestField} node count");
            }
            nodeCount += nodes.Count;
        }

        return MapRequired(
            bounded,
            candidatesField,
            0,
            ManifestRecord.MaximumRelayCandidateCount,
            FromDocument);
    }

    private static SptManifestClaimPreparedDocument ToDocument(ManifestClaimPreparedPayload claim) => new()
    {
        Items = claim.Items.Select(ToClaimItemDocument).ToList(),
        RootIds = claim.RootIds.ToList(),
        ProfileCommitStarted = claim.ProfileCommitStarted,
        PreparedAtUtc = claim.PreparedAtUtc,
        CommitGeneration = claim.CommitGeneration,
        CommitPredecessorHash = claim.CommitPredecessorHash
    };

    private static ManifestClaimPreparedPayload FromDocument(
        SptManifestClaimPreparedDocument document,
        bool requireCommitPlan = true) => new(
        MapRequired(
            document.Items,
            "Claim items",
            1,
            RewardForest.MaxNodeCount,
            FromClaimItemDocument),
        RequireBounded(document.RootIds, "Claim root IDs", 1, RewardForest.MaxRootCount),
        RequireValue(document.ProfileCommitStarted, "Claim profile-commit marker"),
        RequireValue(document.PreparedAtUtc, "Claim prepared timestamp"),
        requireCommitPlan
            ? RequireValue(document.CommitGeneration, "Claim commit generation")
            : document.CommitGeneration,
        requireCommitPlan
            ? RequireString(document.CommitPredecessorHash, "Claim commit predecessor hash")
            : document.CommitPredecessorHash);

    private static SptManifestClaimGrantDocument ToDocument(ManifestClaimGrantRecord grant) => new()
    {
        ManifestId = grant.ManifestId,
        Entitlement = ToDocument(grant.Entitlement),
        ClaimPayload = ToDocument(grant.ClaimPayload),
        CommittedAtUtc = grant.CommittedAtUtc
    };

    private static ManifestClaimGrantRecord FromDocument(SptManifestClaimGrantDocument document) => new(
        RequireString(document.ManifestId, "Claim grant manifest ID"),
        FromDocument(Require(document.Entitlement, "Claim grant entitlement")),
        FromDocument(Require(document.ClaimPayload, "Claim grant payload"), requireCommitPlan: false),
        RequireValue(document.CommittedAtUtc, "Claim grant committed timestamp"));

    private static SptManifestClaimItemDocument ToClaimItemDocument(Item item)
    {
        RejectClaimExtensionData(item);
        var cloned = CaseOpeningRecord.CloneItem(item);
        var location = cloned.Location switch
        {
            null => null,
            ItemLocation itemLocation => ToDocument(itemLocation),
            _ => throw new InvalidOperationException(
                $"Unsupported Claim item location type '{cloned.Location.GetType().FullName}'.")
        };
        return new SptManifestClaimItemDocument
        {
            Item = cloned with { Location = null },
            Location = location
        };
    }

    private static Item FromClaimItemDocument(SptManifestClaimItemDocument document)
    {
        var item = Require(document.Item, "Claim item");
        RejectClaimExtensionData(item);
        if (item.Location is not null)
        {
            throw Invalid("Claim item location");
        }

        var restored = item with
        {
            Location = document.Location is null ? null : FromDocument(document.Location)
        };
        RejectClaimExtensionData(restored);
        return CaseOpeningRecord.CloneItem(restored);
    }

    private static void RejectClaimExtensionData(Item item)
    {
        var locationHasExtensionData = item.Location is ItemLocation itemLocation &&
            itemLocation.ExtensionData?.Count > 0;
        if (item.ExtensionData?.Count > 0 ||
            item.Upd?.ExtensionData?.Count > 0 ||
            locationHasExtensionData)
        {
            throw Invalid("Claim item extension");
        }
    }

    private static SptManifestRelayPreparedDocument ToDocument(ManifestRelayPreparedPayload relay) => new()
    {
        KeyId = relay.KeyId,
        Outcome = relay.Outcome,
        Output = relay.Output is null ? null : ToDocument(relay.Output),
        Odds = ToDocument(relay.Odds),
        OutcomeRng = ToDocument(relay.OutcomeRng),
        TargetRng = relay.TargetRng is null ? null : ToDocument(relay.TargetRng),
        BrokerFavorBefore = relay.BrokerFavorBefore,
        BrokerFavorAfter = relay.BrokerFavorAfter,
        ProfileCommitStarted = relay.ProfileCommitStarted,
        PreparedAtUtc = relay.PreparedAtUtc,
        NextRelayCandidates = relay.NextRelayCandidates.Select(ToDocument).ToList(),
        CommitGeneration = relay.CommitGeneration,
        CommitPredecessorHash = relay.CommitPredecessorHash
    };

    private static ManifestRelayPreparedPayload FromDocument(SptManifestRelayPreparedDocument document) => new(
        RequireValue(document.KeyId, "prepared Relay key ID"),
        RequireValue(document.Outcome, "prepared Relay outcome"),
        document.Output is null ? null : FromDocument(document.Output),
        FromDocument(Require(document.Odds, "prepared Relay odds")),
        FromDocument(Require(document.OutcomeRng, "prepared Relay outcome RNG")),
        document.TargetRng is null ? null : FromDocument(document.TargetRng),
        RequireValue(document.BrokerFavorBefore, "prepared Relay prior Broker Favor"),
        RequireValue(document.BrokerFavorAfter, "prepared Relay next Broker Favor"),
        RequireValue(document.ProfileCommitStarted, "prepared Relay profile-commit marker"),
        RequireValue(document.PreparedAtUtc, "prepared Relay timestamp"),
        MapRelayCandidates(document.NextRelayCandidates, "prepared next-stage Relay"),
        RequireValue(document.CommitGeneration, "prepared Relay commit generation"),
        RequireString(document.CommitPredecessorHash, "prepared Relay commit predecessor hash"));

    private static SptManifestLotReceiptDocument ToDocument(ManifestLotReceiptSnapshot lot) => new()
    {
        Rarity = lot.Rarity,
        Identity = ToDocument(lot.Identity),
        Fingerprint = ToDocument(lot.Fingerprint)
    };

    private static ManifestLotReceiptSnapshot FromDocument(SptManifestLotReceiptDocument document) => new(
        RequireValue(document.Rarity, "receipt grade"),
        FromDocument(Require(document.Identity, "receipt identity")),
        FromDocument(Require(document.Fingerprint, "receipt fingerprint")));

    private static SptManifestRelayReceiptDocument ToDocument(ManifestRelayReceipt receipt) => new()
    {
        RelayStage = receipt.RelayStage,
        RarityLadderVersion = receipt.RarityLadderVersion,
        Input = ToDocument(receipt.Input),
        Outcome = receipt.Outcome,
        Output = receipt.Output is null ? null : ToDocument(receipt.Output),
        Odds = ToDocument(receipt.Odds),
        OutcomeRng = ToDocument(receipt.OutcomeRng),
        TargetRng = receipt.TargetRng is null ? null : ToDocument(receipt.TargetRng),
        EligibleTargets = receipt.EligibleTargets.Select(ToDocument).ToList(),
        BrokerFavorBefore = receipt.BrokerFavorBefore,
        BrokerFavorAfter = receipt.BrokerFavorAfter,
        CompletedAtUtc = receipt.CompletedAtUtc
    };

    private static ManifestRelayReceipt FromDocument(
        SptManifestRelayReceiptDocument document,
        RarityLadderVersion fallbackRarityLadderVersion,
        bool requirePersistedRarityLadderVersion)
    {
        var rarityLadderVersion = ResolveRarityLadderVersion(
            document.RarityLadderVersion,
            fallbackRarityLadderVersion,
            requirePersistedRarityLadderVersion,
            "receipt rarity ladder version");
        return new ManifestRelayReceipt(
            RequireValue(document.RelayStage, "receipt Relay stage"),
            FromDocument(Require(document.Input, "receipt input")),
            RequireValue(document.Outcome, "receipt Relay outcome"),
            document.Output is null ? null : FromDocument(document.Output),
            FromDocument(Require(document.Odds, "receipt Relay odds")),
            FromDocument(Require(document.OutcomeRng, "receipt outcome RNG")),
            document.TargetRng is null ? null : FromDocument(document.TargetRng),
            MapRequired(
                document.EligibleTargets,
                "receipt eligible targets",
                0,
                ManifestRecord.MaximumRelayCandidateCount,
                FromDocument),
            RequireValue(document.BrokerFavorBefore, "receipt prior Broker Favor"),
            RequireValue(document.BrokerFavorAfter, "receipt next Broker Favor"),
            RequireValue(document.CompletedAtUtc, "receipt completion timestamp"),
            rarityLadderVersion);
    }

    private static SptManifestTerminalReceiptDocument ToDocument(ManifestTerminalReceipt receipt) => new()
    {
        ManifestId = receipt.ManifestId,
        CaseTemplateId = receipt.CaseTemplateId,
        OpeningQuality = receipt.OpeningQuality,
        TerminalPhase = receipt.TerminalPhase,
        RarityLadderVersion = receipt.RarityLadderVersion,
        Offers = receipt.Offers.Select(ToDocument).ToList(),
        Entitlement = receipt.Entitlement is null ? null : ToDocument(receipt.Entitlement),
        Decisions = receipt.Decisions.Select(ToDocument).ToList(),
        RelayHistory = receipt.RelayHistory.Select(ToDocument).ToList(),
        BrokerFavorBefore = receipt.BrokerFavorBefore,
        BrokerFavorAfter = receipt.BrokerFavorAfter,
        CompletedAtUtc = receipt.CompletedAtUtc
    };

    private static ManifestTerminalReceipt FromDocument(
        SptManifestTerminalReceiptDocument document,
        RarityLadderVersion fallbackRarityLadderVersion,
        bool requirePersistedRarityLadderVersion)
    {
        var rarityLadderVersion = ResolveRarityLadderVersion(
            document.RarityLadderVersion,
            fallbackRarityLadderVersion,
            requirePersistedRarityLadderVersion,
            "terminal receipt rarity ladder version");
        return new ManifestTerminalReceipt(
            RequireString(document.ManifestId, "terminal receipt manifest ID"),
            RequireValue(document.TerminalPhase, "terminal receipt phase"),
            MapRequired(
                document.Offers,
                "terminal receipt offers",
                1,
                ManifestRecord.OfferCount,
                FromDocument),
            document.Entitlement is null ? null : FromDocument(document.Entitlement),
            MapRequired(
                document.Decisions,
                "terminal receipt decisions",
                0,
                ManifestRecord.OfferCount - 1,
                FromDocument),
            MapRequired(
                document.RelayHistory,
                "terminal receipt Relay history",
                0,
                ManifestRecord.MaximumRelayReceiptCount,
                relayReceipt => FromDocument(
                    relayReceipt,
                    rarityLadderVersion,
                    requirePersistedRarityLadderVersion)),
            RequireValue(document.BrokerFavorBefore, "terminal receipt prior Broker Favor"),
            RequireValue(document.BrokerFavorAfter, "terminal receipt next Broker Favor"),
            RequireValue(document.CompletedAtUtc, "terminal receipt completion timestamp"),
            rarityLadderVersion,
            document.CaseTemplateId ?? ContrabandCases.Shared.ModConstants.CaseTemplateId,
            document.OpeningQuality);
    }

    private static SptCargoLotIdentityDocument ToDocument(CargoLotIdentitySnapshot identity) => new()
    {
        ProviderId = identity.ProviderId,
        PackVersion = identity.PackVersion,
        LotId = identity.LotId,
        DisplayName = identity.DisplayName,
        Purpose = identity.Purpose,
        FamilyId = identity.FamilyId.Value,
        TrackId = identity.TrackId.Value,
        AnchorTemplateId = identity.AnchorTemplateId,
        Weight = identity.Weight,
        UsePath = ToDocument(identity.UsePath),
        Fingerprint = ToDocument(identity.Fingerprint)
    };

    private static CargoLotIdentitySnapshot FromDocument(SptCargoLotIdentityDocument document) => new(
        RequireString(document.ProviderId, "cargo provider ID"),
        RequireString(document.PackVersion, "cargo pack version"),
        RequireString(document.LotId, "cargo lot ID"),
        RequireString(document.DisplayName, "cargo display name"),
        RequireString(document.Purpose, "cargo purpose"),
        new FamilyId(RequireString(document.FamilyId, "cargo family ID")),
        new TrackId(RequireString(document.TrackId, "cargo track ID")),
        RequireString(document.AnchorTemplateId, "cargo anchor template ID"),
        RequireValue(document.Weight, "cargo weight"),
        FromDocument(Require(document.UsePath, "cargo use path")),
        FromDocument(Require(document.Fingerprint, "cargo identity fingerprint")));

    private static SptUsePathDocument ToDocument(UsePath usePath) => usePath switch
    {
        RaidRole raidRole => new SptUsePathDocument
        {
            Kind = RaidRoleUsePath,
            RoleId = raidRole.RoleId
        },
        CargoCollection collection => new SptUsePathDocument
        {
            Kind = CollectionUsePath,
            ContainerTemplateId = collection.ContainerTemplateId,
            CollectionId = collection.CollectionId
        },
        Craft craft => new SptUsePathDocument
        {
            Kind = CraftUsePath,
            ProductionId = craft.ProductionId
        },
        Barter barter => new SptUsePathDocument
        {
            Kind = BarterUsePath,
            TraderId = barter.TraderId,
            AssortId = barter.AssortId
        },
        _ => throw new InvalidOperationException($"Unsupported cargo use-path type '{usePath.GetType().FullName}'.")
    };

    private static UsePath FromDocument(SptUsePathDocument document)
    {
        return document.Kind switch
        {
            RaidRoleUsePath when document.RoleId is not null &&
                document.ContainerTemplateId is null && document.CollectionId is null &&
                document.ProductionId is null && document.TraderId is null && document.AssortId is null =>
                new RaidRole(document.RoleId),
            CollectionUsePath when document.RoleId is null &&
                document.ContainerTemplateId is not null && document.CollectionId is not null &&
                document.ProductionId is null && document.TraderId is null && document.AssortId is null =>
                new CargoCollection(document.ContainerTemplateId, document.CollectionId),
            CraftUsePath when document.RoleId is null &&
                document.ContainerTemplateId is null && document.CollectionId is null &&
                document.ProductionId is not null && document.TraderId is null && document.AssortId is null =>
                new Craft(document.ProductionId),
            BarterUsePath when document.RoleId is null &&
                document.ContainerTemplateId is null && document.CollectionId is null &&
                document.ProductionId is null && document.TraderId is not null && document.AssortId is not null =>
                new Barter(document.TraderId, document.AssortId),
            _ => throw Invalid("cargo use-path discriminator or fields")
        };
    }

    private static SptRewardForestDocument ToDocument(RewardForest forest) => new()
    {
        Nodes = forest.Nodes.Select(ToDocument).ToList()
    };

    private static RewardForest FromDocument(SptRewardForestDocument document)
    {
        var nodes = RequireBounded(
            document.Nodes,
            "reward forest nodes",
            1,
            RewardForest.MaxNodeCount);
        var rootCount = 0;
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index] ?? throw Invalid("reward forest nodes");
            if (node.ParentLogicalPath is null && ++rootCount > RewardForest.MaxRootCount)
            {
                throw Invalid("reward forest roots");
            }
        }
        if (rootCount == 0)
        {
            throw Invalid("reward forest roots");
        }

        return RewardForest.Create(MapRequired(
            nodes,
            "reward forest nodes",
            1,
            RewardForest.MaxNodeCount,
            FromDocument));
    }

    private static SptRewardForestNodeDocument ToDocument(RewardForestNode node) => new()
    {
        TreeRootPath = node.TreeRootPath,
        LogicalPath = node.LogicalPath,
        TemplateId = node.TemplateId,
        ParentLogicalPath = node.ParentLogicalPath,
        SlotId = node.SlotId,
        InternalLocation = node.InternalLocation is null ? null : ToDocument(node.InternalLocation),
        StackCount = node.StackCount,
        StableState = node.StableState is null ? null : ToDocument(node.StableState)
    };

    private static RewardForestNode FromDocument(SptRewardForestNodeDocument document) => new(
        RequireString(document.TreeRootPath, "reward tree root path"),
        RequireString(document.LogicalPath, "reward logical path"),
        RequireString(document.TemplateId, "reward template ID"),
        document.ParentLogicalPath,
        document.SlotId,
        document.InternalLocation is null ? null : FromDocument(document.InternalLocation),
        RequireValue(document.StackCount, "reward stack count"),
        document.StableState is null ? null : FromDocument(document.StableState));

    private static SptCanonicalInternalLocationDocument ToDocument(CanonicalInternalLocation location) => new()
    {
        X = location.X,
        Y = location.Y,
        Rotation = location.Rotation
    };

    private static CanonicalInternalLocation FromDocument(SptCanonicalInternalLocationDocument document) => new(
        RequireValue(document.X, "internal location X"),
        RequireValue(document.Y, "internal location Y"),
        RequireValue(document.Rotation, "internal location rotation"));

    private static SptItemLocationDocument ToDocument(ItemLocation location) => new()
    {
        X = location.X,
        Y = location.Y,
        IsSearched = location.IsSearched,
        Rotation = location.Rotation,
        R = location.R,
        ExtensionData = []
    };

    private static ItemLocation FromDocument(SptItemLocationDocument document)
    {
        if (document.ExtensionData is { Count: > 0 })
        {
            throw Invalid("Claim item location extension");
        }

        return new ItemLocation
        {
            X = document.X,
            Y = document.Y,
            IsSearched = document.IsSearched,
            Rotation = document.Rotation,
            R = RequireValue(document.R, "Claim item location rotation"),
            ExtensionData = []
        };
    }

    private static SptRewardStableStateDocument ToDocument(RewardStableState stableState) => new()
    {
        Durability = stableState.Durability,
        MaximumDurability = stableState.MaximumDurability,
        ResourceValue = stableState.ResourceValue,
        MaximumResourceValue = stableState.MaximumResourceValue,
        ResourceKind = stableState.ResourceKind
    };

    private static RewardStableState FromDocument(SptRewardStableStateDocument document) => new(
        document.Durability,
        document.MaximumDurability,
        document.ResourceValue,
        document.MaximumResourceValue,
        document.ResourceKind);

    private static SptRewardForestFingerprintDocument ToDocument(RewardForestFingerprintV2 fingerprint) => new()
    {
        Sha256Hex = fingerprint.Sha256Hex
    };

    private static RewardForestFingerprintV2 FromDocument(SptRewardForestFingerprintDocument document) =>
        new(RequireString(document.Sha256Hex, "reward forest fingerprint"));

    private static SptCanonicalRngEvidenceDocument ToDocument(CanonicalRngEvidence evidence) => new()
    {
        Purpose = evidence.Purpose,
        DrawOrdinal = evidence.DrawOrdinal,
        UnitNumerator = evidence.UnitNumerator
    };

    private static CanonicalRngEvidence FromDocument(SptCanonicalRngEvidenceDocument document) => new(
        RequireValue(document.Purpose, "RNG purpose"),
        RequireValue(document.DrawOrdinal, "RNG draw ordinal"),
        RequireValue(document.UnitNumerator, "RNG unit numerator"));

    private static SptRelayOddsDocument ToDocument(RelayOdds odds) => new()
    {
        UpgradePercent = odds.UpgradePercent,
        SidegradePercent = odds.SidegradePercent,
        ConfiscatePercent = odds.ConfiscatePercent
    };

    private static RelayOdds FromDocument(SptRelayOddsDocument document) => new(
        RequireValue(document.UpgradePercent, "Relay upgrade odds"),
        RequireValue(document.SidegradePercent, "Relay sidegrade odds"),
        RequireValue(document.ConfiscatePercent, "Relay confiscation odds"));

    private static RarityLadderVersion ResolveRarityLadderVersion(
        RarityLadderVersion? persistedValue,
        RarityLadderVersion fallbackValue,
        bool requirePersistedValue,
        string field)
    {
        RelayRules.ValidateLadderVersion(fallbackValue, nameof(fallbackValue));
        if (persistedValue is null)
        {
            if (requirePersistedValue)
            {
                throw Invalid(field);
            }

            return fallbackValue;
        }

        RelayRules.ValidateLadderVersion(persistedValue.Value, field);
        if (!requirePersistedValue && persistedValue.Value != fallbackValue)
        {
            throw Invalid(field);
        }

        return persistedValue.Value;
    }

    private static IReadOnlyList<TResult> MapRequired<TDocument, TResult>(
        List<TDocument>? documents,
        string field,
        int minimumCount,
        int maximumCount,
        Func<TDocument, TResult> map)
        where TDocument : class
    {
        var bounded = RequireBounded(documents, field, minimumCount, maximumCount);
        var mapped = new TResult[bounded.Count];
        for (var index = 0; index < bounded.Count; index++)
        {
            mapped[index] = map(bounded[index] ?? throw Invalid(field));
        }

        return mapped;
    }

    // ProfileDataService has already parsed JSON before this trust boundary. These checks bound every
    // secondary collection allocation here; raw document byte size and parser depth remain upstream concerns.
    private static List<T> RequireBounded<T>(
        List<T>? values,
        string field,
        int minimumCount,
        int maximumCount)
    {
        if (values is null || values.Count < minimumCount || values.Count > maximumCount)
        {
            throw Invalid(field);
        }

        return values;
    }

    private static T Require<T>(T? value, string field) where T : class =>
        value ?? throw Invalid(field);

    private static T RequireValue<T>(T? value, string field) where T : struct =>
        value ?? throw Invalid(field);

    private static string RequireString(string? value, string field) =>
        value ?? throw Invalid(field);

    private static InvalidOperationException Invalid(string field) =>
        new($"The Contraband Cases journal contains invalid {field} data.");
}

internal sealed class SptManifestRecordDocument
{
    public string? ManifestId { get; set; }
    public string? CatalogSnapshotId { get; set; }
    public RarityLadderVersion? RarityLadderVersion { get; set; }
    public SptManifestCommitmentDocument? Commitment { get; set; }
    public SptManifestFlowStateDocument? FlowState { get; set; }
    public SptManifestTicketDocument? Ticket { get; set; }
    public List<SptManifestOfferDocument>? Offers { get; set; } = [];
    public List<SptManifestDecisionDocument>? Decisions { get; set; } = [];
    public SptManifestEntitlementDocument? Entitlement { get; set; }
    public List<SptManifestRelayCandidateDocument>? RelayCandidates { get; set; } = [];
    public List<SptManifestRelayReceiptDocument>? RelayHistory { get; set; } = [];
    public int? BrokerFavor { get; set; }
    public SptManifestClaimPreparedDocument? ClaimPrepared { get; set; }
    public SptManifestRelayPreparedDocument? RelayPrepared { get; set; }
    public SptManifestTerminalReceiptDocument? TerminalReceipt { get; set; }
}

internal sealed class SptManifestCommitmentDocument
{
    public int? Version { get; set; }
    public string? CommitmentSha256Hex { get; set; }
    public string? NonceHex { get; set; }
}

internal sealed class SptManifestFlowStateDocument
{
    public ManifestPhase? Phase { get; set; }
    public int? CurrentOrdinal { get; set; }
    public int? LockedOrdinal { get; set; }
    public int? RelayStage { get; set; }
    public bool? RelayTerminal { get; set; }
}

internal sealed class SptManifestTicketDocument
{
    public ManifestOpeningQuality? OpeningQuality { get; set; }
    public string? CaseTemplateId { get; set; }
    public MongoId? CaseId { get; set; }
    public MongoId? KeyId { get; set; }
    public DateTimeOffset? PreparedAtUtc { get; set; }
    public bool? ProfileCommitStarted { get; set; }
    public bool? Committed { get; set; }
    public DateTimeOffset? CommittedAtUtc { get; set; }
    public long? CommitGeneration { get; set; }
    public string? CommitPredecessorHash { get; set; }
}

internal sealed class SptManifestOfferDocument
{
    public int? Ordinal { get; set; }
    public RewardRarity? Rarity { get; set; }
    public SptCargoLotIdentityDocument? Identity { get; set; }
    public SptRewardForestDocument? Forest { get; set; }
    public SptRewardForestFingerprintDocument? Fingerprint { get; set; }
    public SptCanonicalRngEvidenceDocument? RngEvidence { get; set; }
}

internal sealed class SptManifestDecisionDocument
{
    public int? Ordinal { get; set; }
    public ManifestOfferDecision? Decision { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
}

internal sealed class SptManifestEntitlementDocument
{
    public RewardRarity? Rarity { get; set; }
    public SptCargoLotIdentityDocument? Identity { get; set; }
    public SptRewardForestDocument? Forest { get; set; }
    public SptRewardForestFingerprintDocument? Fingerprint { get; set; }
}

internal sealed class SptManifestRelayCandidateDocument
{
    public ManifestRelayResult? TargetResult { get; set; }
    public RewardRarity? Rarity { get; set; }
    public SptCargoLotIdentityDocument? Identity { get; set; }
    public SptRewardForestDocument? Forest { get; set; }
    public SptRewardForestFingerprintDocument? Fingerprint { get; set; }
}

internal sealed class SptManifestClaimPreparedDocument
{
    public List<SptManifestClaimItemDocument>? Items { get; set; } = [];
    public List<MongoId>? RootIds { get; set; } = [];
    public bool? ProfileCommitStarted { get; set; }
    public DateTimeOffset? PreparedAtUtc { get; set; }
    public long? CommitGeneration { get; set; }
    public string? CommitPredecessorHash { get; set; }
}

internal sealed class SptManifestClaimGrantDocument
{
    public string? ManifestId { get; set; }
    public SptManifestEntitlementDocument? Entitlement { get; set; }
    public SptManifestClaimPreparedDocument? ClaimPayload { get; set; }
    public DateTimeOffset? CommittedAtUtc { get; set; }
}

internal sealed class SptManifestClaimItemDocument
{
    public Item? Item { get; set; }
    public SptItemLocationDocument? Location { get; set; }
}

internal sealed class SptManifestRelayPreparedDocument
{
    public MongoId? KeyId { get; set; }
    public ManifestRelayResult? Outcome { get; set; }
    public SptManifestEntitlementDocument? Output { get; set; }
    public SptRelayOddsDocument? Odds { get; set; }
    public SptCanonicalRngEvidenceDocument? OutcomeRng { get; set; }
    public SptCanonicalRngEvidenceDocument? TargetRng { get; set; }
    public int? BrokerFavorBefore { get; set; }
    public int? BrokerFavorAfter { get; set; }
    public bool? ProfileCommitStarted { get; set; }
    public DateTimeOffset? PreparedAtUtc { get; set; }
    public List<SptManifestRelayCandidateDocument>? NextRelayCandidates { get; set; } = [];
    public long? CommitGeneration { get; set; }
    public string? CommitPredecessorHash { get; set; }
}

internal sealed class SptManifestLotReceiptDocument
{
    public RewardRarity? Rarity { get; set; }
    public SptCargoLotIdentityDocument? Identity { get; set; }
    public SptRewardForestFingerprintDocument? Fingerprint { get; set; }
}

internal sealed class SptManifestRelayReceiptDocument
{
    public int? RelayStage { get; set; }
    public RarityLadderVersion? RarityLadderVersion { get; set; }
    public SptManifestLotReceiptDocument? Input { get; set; }
    public ManifestRelayResult? Outcome { get; set; }
    public SptManifestLotReceiptDocument? Output { get; set; }
    public SptRelayOddsDocument? Odds { get; set; }
    public SptCanonicalRngEvidenceDocument? OutcomeRng { get; set; }
    public SptCanonicalRngEvidenceDocument? TargetRng { get; set; }
    public List<SptManifestLotReceiptDocument>? EligibleTargets { get; set; } = [];
    public int? BrokerFavorBefore { get; set; }
    public int? BrokerFavorAfter { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

internal sealed class SptManifestTerminalReceiptDocument
{
    public ManifestOpeningQuality? OpeningQuality { get; set; }
    public string? CaseTemplateId { get; set; }
    public string? ManifestId { get; set; }
    public ManifestPhase? TerminalPhase { get; set; }
    public RarityLadderVersion? RarityLadderVersion { get; set; }
    public List<SptManifestLotReceiptDocument>? Offers { get; set; } = [];
    public SptManifestLotReceiptDocument? Entitlement { get; set; }
    public List<SptManifestDecisionDocument>? Decisions { get; set; } = [];
    public List<SptManifestRelayReceiptDocument>? RelayHistory { get; set; } = [];
    public int? BrokerFavorBefore { get; set; }
    public int? BrokerFavorAfter { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

internal sealed class SptCargoLotIdentityDocument
{
    public string? ProviderId { get; set; }
    public string? PackVersion { get; set; }
    public string? LotId { get; set; }
    public string? DisplayName { get; set; }
    public string? Purpose { get; set; }
    public string? FamilyId { get; set; }
    public string? TrackId { get; set; }
    public string? AnchorTemplateId { get; set; }
    public double? Weight { get; set; }
    public SptUsePathDocument? UsePath { get; set; }
    public SptRewardForestFingerprintDocument? Fingerprint { get; set; }
}

internal sealed class SptUsePathDocument
{
    public string? Kind { get; set; }
    public string? RoleId { get; set; }
    public string? ContainerTemplateId { get; set; }
    public string? CollectionId { get; set; }
    public string? ProductionId { get; set; }
    public string? TraderId { get; set; }
    public string? AssortId { get; set; }
}

internal sealed class SptRewardForestDocument
{
    public List<SptRewardForestNodeDocument>? Nodes { get; set; } = [];
}

internal sealed class SptRewardForestNodeDocument
{
    public string? TreeRootPath { get; set; }
    public string? LogicalPath { get; set; }
    public string? TemplateId { get; set; }
    public string? ParentLogicalPath { get; set; }
    public string? SlotId { get; set; }
    public SptCanonicalInternalLocationDocument? InternalLocation { get; set; }
    public int? StackCount { get; set; }
    public SptRewardStableStateDocument? StableState { get; set; }
}

internal sealed class SptCanonicalInternalLocationDocument
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public CanonicalRotation? Rotation { get; set; }
}

internal sealed class SptItemLocationDocument
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool? IsSearched { get; set; }
    public bool? Rotation { get; set; }
    public ItemRotation? R { get; set; }
    public Dictionary<string, object>? ExtensionData { get; set; }
}

internal sealed class SptRewardStableStateDocument
{
    public decimal? Durability { get; set; }
    public decimal? MaximumDurability { get; set; }
    public decimal? ResourceValue { get; set; }
    public decimal? MaximumResourceValue { get; set; }
    public RewardResourceKind? ResourceKind { get; set; }
}

internal sealed class SptRewardForestFingerprintDocument
{
    public string? Sha256Hex { get; set; }
}

internal sealed class SptCanonicalRngEvidenceDocument
{
    public ManifestRngPurpose? Purpose { get; set; }
    public int? DrawOrdinal { get; set; }
    public long? UnitNumerator { get; set; }
}

internal sealed class SptRelayOddsDocument
{
    public int? UpgradePercent { get; set; }
    public int? SidegradePercent { get; set; }
    public int? ConfiscatePercent { get; set; }
}
