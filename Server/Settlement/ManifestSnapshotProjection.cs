using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;

namespace ContrabandCases.Server.Settlement;

internal static class ManifestSnapshotProjection
{
    internal static ManifestSnapshotData FromActive(
        ManifestRecord manifest,
        CargoCatalogSnapshot? catalog,
        IReadOnlyDictionary<string, string>? locale,
        CargoCatalogSnapshot? relayCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        locale ??= new Dictionary<string, string>(StringComparer.Ordinal);

        var current = CurrentLot(manifest);
        var resolved = current is null || catalog is null
            ? null
            : catalog.ResolveExact(
                current.Value.Rarity,
                current.Value.Identity,
                current.Value.Forest,
                current.Value.Fingerprint, manifest.RarityLadderVersion);
        var phase = manifest.FlowState.Phase;
        var premium = manifest.Ticket.OpeningQuality?.IsPremium == true;
        var premiumChoicePhase = premium && phase == ManifestPhase.Offer1;
        var missingContent = phase is (ManifestPhase.Offer1 or
                ManifestPhase.Offer2 or ManifestPhase.Entitlement) &&
            current is not null && resolved is null;
        missingContent |= premiumChoicePhase && manifest.Offers.Any(offer => catalog?.ResolveExact(
            offer.Rarity, offer.Identity, offer.Forest, offer.Fingerprint, manifest.RarityLadderVersion) is null);
        var canLock = phase is ManifestPhase.Offer1 or ManifestPhase.Offer2 && !missingContent;
        // Runtime callers supply the coordinator's cached case view. Keep the
        // full catalog for exact recovery without repricing on each UI refresh.
        var offerRelayCatalog = canLock && catalog is not null
            ? relayCatalog ?? CaseCatalogs.ForCase(catalog, manifest.Ticket.CaseTemplateId) : null;
        var nextOffer = canLock && manifest.FlowState.CurrentOrdinal < ManifestRecord.OfferCount
            ? manifest.Offers[manifest.FlowState.CurrentOrdinal]
            : null;
        var canBurn = !premium && nextOffer is not null && catalog?.ResolveExact(
            nextOffer.Rarity,
            nextOffer.Identity,
            nextOffer.Forest,
            nextOffer.Fingerprint, manifest.RarityLadderVersion) is not null;
        var canClaim = phase == ManifestPhase.Entitlement && !missingContent ||
            phase is ManifestPhase.ClaimPrepared or ManifestPhase.RewardOwed;
        var relayContentAvailable = phase != ManifestPhase.Entitlement ||
            RelayContentAvailable(manifest, catalog);
        var canRelay = phase == ManifestPhase.Entitlement &&
                !manifest.FlowState.RelayTerminal &&
                manifest.RelayCandidates.Count > 0 &&
                relayContentAvailable &&
                !missingContent ||
            phase == ManifestPhase.RelayPrepared;

        return new ManifestSnapshotData
        {
            ManifestId = manifest.ManifestId,
            CaseTemplateId = manifest.Ticket.CaseTemplateId,
            OpeningTier = manifest.Ticket.Committed
                ? (manifest.Ticket.OpeningQuality?.Tier ?? ManifestOpeningTier.Normal).ToString() : "Normal",
            PremiumChoices = premiumChoicePhase
                ? manifest.Offers.Select(offer => CreateLot(offer.Rarity, offer.Identity, offer.Forest,
                    offer.Fingerprint, catalog?.ResolveExact(offer.Rarity, offer.Identity, offer.Forest,
                        offer.Fingerprint, manifest.RarityLadderVersion), locale,
                    RelayEligibleAfterChoosing(manifest, offer, offerRelayCatalog))).ToArray() : [],
            Phase = phase.ToString(),
            CurrentOrdinal = manifest.FlowState.CurrentOrdinal,
            LockedOrdinal = manifest.FlowState.LockedOrdinal,
            RelayStage = manifest.FlowState.RelayStage,
            RelayTerminal = manifest.FlowState.RelayTerminal,
            RarityLadderVersion = manifest.RarityLadderVersion.ToString(),
            CatalogSnapshotId = manifest.CatalogSnapshotId,
            TicketCommitted = manifest.Ticket.Committed,
            RecoveryCaseItemId = phase == ManifestPhase.TicketPrepared
                ? manifest.Ticket.CaseId.ToString()
                : null,
            BrokerFavor = manifest.BrokerFavor,
            BrokerFavorMaximum = RelayRules.MaximumRecoveryMeter,
            FamilySeals = CreateSeals(
                manifest.Offers.Select(offer => (offer.Ordinal, SealGroup(manifest.Ticket.CaseTemplateId, offer.Identity,
                    manifest.Offers.Select(o => o.Identity)))),
                manifest.Decisions,
                manifest.FlowState.CurrentOrdinal,
                manifest.FlowState.LockedOrdinal,
                manifest.Ticket.Committed,
                manifest.Ticket.CaseTemplateId, premium),
            CurrentLot = current is null
                ? null
                : CreateLot(
                    current.Value.Rarity,
                    current.Value.Identity,
                    current.Value.Forest,
                    current.Value.Fingerprint,
                    resolved,
                    locale,
                    canLock ? RelayEligibleAfterChoosing(manifest, manifest.Offers[manifest.FlowState.CurrentOrdinal - 1], offerRelayCatalog) : null),
            AvailableActions = new ManifestAvailableActionsData
            {
                CanLock = canLock,
                CanBurn = canBurn,
                CanClaim = canClaim,
                CanRelay = canRelay,
                CanForfeit = missingContent && phase is (
                    ManifestPhase.Offer1 or
                    ManifestPhase.Offer2 or
                    ManifestPhase.Entitlement),
                ExpectedOrdinal = manifest.FlowState.CurrentOrdinal,
                ExpectedPhase = phase.ToString(),
                ExpectedRelayStage = manifest.FlowState.RelayStage
            },
            Relay = missingContent ? null : CreateRelayProposition(manifest, catalog),
            LatestReceipt = manifest.RelayHistory.LastOrDefault() is { } receipt
                ? CreateReceipt(receipt)
                : null,
            MissingContentBlocked = missingContent
        };
    }

    internal static bool IsMissingCurrentContent(
        ManifestRecord manifest,
        CargoCatalogSnapshot? catalog)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Ticket.OpeningQuality?.IsPremium == true && manifest.FlowState.Phase == ManifestPhase.Offer1)
            return manifest.Offers.Any(offer => catalog?.ResolveExact(offer.Rarity, offer.Identity,
                offer.Forest, offer.Fingerprint, manifest.RarityLadderVersion) is null);
        var current = CurrentLot(manifest);
        return current is not null &&
               (catalog is null ||
                catalog.ResolveExact(
                    current.Value.Rarity,
                    current.Value.Identity,
                    current.Value.Forest,
                    current.Value.Fingerprint, manifest.RarityLadderVersion) is null);
    }

    internal static ManifestSnapshotData FromTerminal(ManifestTerminalReceipt receipt, bool deliveredToMessenger = false)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var (currentOrdinal, lockedOrdinal) = receipt.CaseTemplateId == CaseContracts.CashCache
            ? (1, (int?)1) : ResolveTerminalSelection(receipt.Decisions);
        var latestRelay = receipt.RelayHistory.LastOrDefault();
        var relayStage = latestRelay is null
            ? 1
            : latestRelay.Outcome == ManifestRelayResult.Upgrade &&
                latestRelay.RelayStage < RelayRules.MaximumStage
                    ? latestRelay.RelayStage + 1
                    : latestRelay.RelayStage;
        var phase = receipt.TerminalPhase;

        return new ManifestSnapshotData
        {
            DeliveredToMessenger = deliveredToMessenger,
            ManifestId = receipt.ManifestId,
            CaseTemplateId = receipt.CaseTemplateId,
            OpeningTier = (receipt.OpeningQuality?.Tier ?? ManifestOpeningTier.Normal).ToString(),
            Phase = phase.ToString(),
            CurrentOrdinal = currentOrdinal,
            LockedOrdinal = lockedOrdinal,
            RelayStage = relayStage,
            RelayTerminal = true,
            RarityLadderVersion = receipt.RarityLadderVersion.ToString(),
            CatalogSnapshotId = null,
            TicketCommitted = true,
            RecoveryCaseItemId = null,
            BrokerFavor = receipt.BrokerFavorAfter,
            BrokerFavorMaximum = RelayRules.MaximumRecoveryMeter,
            FamilySeals = CreateSeals(
                receipt.Offers.Select((offer, index) => (index + 1, SealGroup(receipt.CaseTemplateId, offer.Identity,
                    receipt.Offers.Select(o => o.Identity)))),
                receipt.Decisions,
                currentOrdinal,
                lockedOrdinal,
                ticketCommitted: true,
                receipt.CaseTemplateId, receipt.OpeningQuality?.IsPremium == true),
            CurrentLot = null,
            AvailableActions = new ManifestAvailableActionsData
            {
                ExpectedOrdinal = currentOrdinal,
                ExpectedPhase = phase.ToString(),
                ExpectedRelayStage = relayStage
            },
            Relay = null,
            LatestReceipt = latestRelay is null ? null : CreateReceipt(latestRelay),
            MissingContentBlocked = false
        };
    }

    private static (
        RewardRarity Rarity,
        CargoLotIdentitySnapshot Identity,
        RewardForest Forest,
        RewardForestFingerprintV2 Fingerprint)? CurrentLot(ManifestRecord manifest)
    {
        if (manifest.FlowState.Phase == ManifestPhase.TicketPrepared || manifest.FlowState.IsTerminal)
        {
            return null;
        }

        if (manifest.FlowState.Phase is ManifestPhase.Offer1 or ManifestPhase.Offer2)
        {
            var offer = manifest.Offers[manifest.FlowState.CurrentOrdinal - 1];
            return (offer.Rarity, offer.Identity, offer.Forest, offer.Fingerprint);
        }

        return manifest.Entitlement is { } entitlement
            ? (entitlement.Rarity, entitlement.Identity, entitlement.Forest, entitlement.Fingerprint)
            : null;
    }

    internal static ManifestLotData CreateLot(
        RewardRarity rarity,
        CargoLotIdentitySnapshot identity,
        RewardForest forest,
        RewardForestFingerprintV2 fingerprint,
        ResolvedCargoLot? resolved,
        IReadOnlyDictionary<string, string> locale,
        bool? relayEligible = null)
    {
        var evaluation = resolved?.EvaluationOrNull;
        return new ManifestLotData
        {
            RelayEligible = relayEligible,
            ProviderId = identity.ProviderId,
            ProviderLabel = ProviderLabel(identity.ProviderId),
            LotId = identity.LotId,
            DisplayName = identity.DisplayName,
            Purpose = identity.Purpose,
            FamilyId = identity.FamilyId.Value,
            TrackId = identity.TrackId.Value,
            Grade = rarity.ToString(),
            AnchorTemplateId = identity.AnchorTemplateId,
            Fingerprint = fingerprint.Sha256Hex,
            LiquidationValue = evaluation?.HandbookValue,
            UseValue = evaluation?.UseValue,
            TraderResaleEstimate = evaluation?.TraderResaleEstimate,
            FootprintCells = evaluation?.FootprintCells,
            Contents = forest.Nodes
                .GroupBy(node => node.TemplateId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new ManifestLotContentData
                {
                    TemplateId = group.Key,
                    DisplayName = ResolveItemName(locale, group.Key),
                    Quantity = checked(group.Sum(node => node.StackCount))
                })
                .ToArray()
        };
    }

    // Read-only preview of the same bounded candidate selection frozen when a
    // player chooses. Only revealed offers are projected; no hidden draw leaks.
    private static bool RelayEligibleAfterChoosing(ManifestRecord manifest, ManifestOfferSnapshot offer,
        CargoCatalogSnapshot? catalog)
    {
        if (catalog is null || catalog.ResolveExact(offer.Rarity, offer.Identity, offer.Forest,
                offer.Fingerprint, manifest.RarityLadderVersion) is null) return false;
        return new ManifestCatalogSelector().CreateRelayCandidatesForStage(catalog,
            new ManifestEntitlementSnapshot(offer.Rarity, offer.Identity, offer.Forest, offer.Fingerprint),
            manifest.FlowState.RelayStage, manifest.RarityLadderVersion).Count > 0;
    }

    private static string SealGroup(string template, CargoLotIdentitySnapshot identity,
        IEnumerable<CargoLotIdentitySnapshot> offers) =>
        CaseContracts.UsesTrackGroups(template) &&
        (template != CaseContracts.BlackSite || offers.Select(o => o.TrackId).Distinct().Count() == ManifestRecord.OfferCount)
            ? identity.TrackId.Value : identity.FamilyId.Value;

    private static IReadOnlyList<ManifestFamilySealData> CreateSeals(
        IEnumerable<(int Ordinal, string FamilyId)> offers,
        IReadOnlyList<ManifestDecisionRecord> decisions,
        int currentOrdinal,
        int? lockedOrdinal,
        bool ticketCommitted,
        string caseTemplateId,
        bool premium = false)
    {
        var burned = decisions
            .Where(decision => decision.Decision == ManifestOfferDecision.Burn)
            .Select(decision => decision.Ordinal)
            .ToHashSet();
        return offers
            .OrderBy(offer => offer.Ordinal)
            .Select(offer =>
            {
                var revealed = ticketCommitted && (premium || offer.Ordinal <= currentOrdinal);
                return new ManifestFamilySealData
                {
                    Ordinal = offer.Ordinal,
                    FamilyId = revealed ? offer.FamilyId : $"sealed-{offer.Ordinal}",
                    FamilyLabel = revealed
                        ? CaseContracts.UsesTrackGroups(caseTemplateId)
                            ? CaseContracts.GroupLabel(caseTemplateId, offer.FamilyId)
                            : FamilyLabel(offer.FamilyId)
                        : ticketCommitted && offer.Ordinal == currentOrdinal + 1
                            ? CargoFamilies.Signal(offer.FamilyId) ?? $"Sealed Family {offer.Ordinal}"
                            : $"Sealed Family {offer.Ordinal}",
                    RiskBand = "Mixed",
                    Revealed = revealed,
                    Burned = burned.Contains(offer.Ordinal),
                    Locked = lockedOrdinal == offer.Ordinal
                };
            })
            .ToArray();
    }

    private static ManifestRelayPropositionData? CreateRelayProposition(
        ManifestRecord manifest,
        CargoCatalogSnapshot? catalog)
    {
        var entitlement = manifest.Entitlement;
        if (manifest.Ticket.CaseTemplateId == CaseContracts.CashCache || entitlement is null || manifest.FlowState.Phase is not (
                ManifestPhase.Entitlement or ManifestPhase.RelayPrepared))
        {
            return null;
        }

        var stage = manifest.FlowState.RelayStage;
        var odds = RelayRules.GetOdds(stage);
        var resolvedCandidates = catalog is null
            ? []
            : manifest.RelayCandidates
                .Select(candidate => catalog.ResolveExact(
                    candidate.Rarity,
                    candidate.Identity,
                    candidate.Forest,
                    candidate.Fingerprint, manifest.RarityLadderVersion))
                .ToArray();
        var completeCandidatePool = resolvedCandidates.Length == manifest.RelayCandidates.Count &&
            resolvedCandidates.All(candidate => candidate is not null);
        var values = completeCandidatePool
            ? resolvedCandidates.Select(candidate => candidate!.Evaluation.UseValue).ToArray()
            : [];
        var upgradeGrade = entitlement.Rarity == RewardRarity.BlackLabel || manifest.FlowState.RelayTerminal
            ? null
            : RelayRules.GetUpgradeRarity(
                entitlement.Rarity,
                manifest.RarityLadderVersion).ToString();

        return new ManifestRelayPropositionData
        {
            Stage = stage,
            UpgradePercent = odds.UpgradePercent,
            SidegradePercent = odds.SidegradePercent,
            ConfiscatePercent = odds.ConfiscatePercent,
            FavorBefore = manifest.BrokerFavor,
            FavorAfterOnLoss = Math.Min(
                RelayRules.MaximumRecoveryMeter,
                checked(manifest.BrokerFavor + 1)),
            GuaranteeActive = manifest.BrokerFavor == RelayRules.MaximumRecoveryMeter,
            KeyCost = 1,
            UpgradeGrade = upgradeGrade,
            CandidateValueMin = values.Length == 0 ? null : values.Min(),
            CandidateValueMax = values.Length == 0 ? null : values.Max(),
            SidegradeEndsChain = true,
            TerminalReason = manifest.FlowState.Phase == ManifestPhase.Entitlement &&
                             manifest.RelayCandidates.Count > 0 &&
                             !completeCandidatePool
                ? "Frozen Relay content is unavailable; Claim remains safe."
                : ResolveRelayTerminalReason(manifest, entitlement)
        };
    }

    private static bool RelayContentAvailable(
        ManifestRecord manifest,
        CargoCatalogSnapshot? catalog) =>
        catalog is not null &&
        manifest.RelayCandidates.All(candidate => catalog.ResolveExact(
            candidate.Rarity,
            candidate.Identity,
            candidate.Forest,
            candidate.Fingerprint, manifest.RarityLadderVersion) is not null);

    private static string? ResolveRelayTerminalReason(
        ManifestRecord manifest,
        ManifestEntitlementSnapshot entitlement)
    {
        if (entitlement.Rarity == RewardRarity.BlackLabel)
        {
            return "Legendary is the top rarity.";
        }
        if (manifest.FlowState.RelayTerminal)
        {
            return manifest.RelayHistory.LastOrDefault()?.Outcome == ManifestRelayResult.Sidegrade
                ? "A sidegrade settles the Relay chain."
                : "The Relay chain has ended.";
        }
        if (manifest.RelayCandidates.Count == 0)
        {
            return "No complete same-track Relay pool is available.";
        }

        return null;
    }

    private static ManifestRelayReceiptData CreateReceipt(ManifestRelayReceipt receipt) => new()
    {
        Stage = receipt.RelayStage,
        Outcome = receipt.Outcome.ToString(),
        InputDisplayName = receipt.Input.Identity.DisplayName,
        InputGrade = receipt.Input.Rarity.ToString(),
        OutputDisplayName = receipt.Output?.Identity.DisplayName,
        OutputGrade = receipt.Output?.Rarity.ToString(),
        BrokerFavorBefore = receipt.BrokerFavorBefore,
        BrokerFavorAfter = receipt.BrokerFavorAfter
    };

    private static (int CurrentOrdinal, int? LockedOrdinal) ResolveTerminalSelection(
        IReadOnlyList<ManifestDecisionRecord> decisions)
    {
        if (decisions.Count == 0)
        {
            return (1, null);
        }
        var last = decisions[^1];
        if (last.Decision is ManifestOfferDecision.Lock or ManifestOfferDecision.Choose)
        {
            return (last.Ordinal, last.Ordinal);
        }
        if (decisions.Count == 2 && last.Ordinal == 2)
        {
            return (3, 3);
        }

        return (checked(last.Ordinal + 1), null);
    }

    private static string ResolveItemName(
        IReadOnlyDictionary<string, string> locale,
        string templateId)
    {
        if (locale.TryGetValue(string.Concat(templateId, " Name"), out var name) &&
            !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }
        if (locale.TryGetValue(string.Concat(templateId, " ShortName"), out var shortName) &&
            !string.IsNullOrWhiteSpace(shortName))
        {
            return shortName;
        }

        return templateId;
    }

    internal static string FamilyLabel(string familyId) => familyId switch
    {
        "arsenal" => "Weapons",
        "operator" => "Equipment",
        "field-supply" => "Supplies",
        _ => Humanize(familyId)
    };

    internal static string ProviderLabel(string providerId) => providerId switch
    {
        "core" => "Base Game",
        _ => Humanize(providerId)
    };

    private static string Humanize(string value)
    {
        var words = value
            .Replace('.', ' ')
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? value
            : string.Join(' ', words.Select(word =>
                word.Length == 1
                    ? word.ToUpperInvariant()
                    : char.ToUpperInvariant(word[0]) + word[1..]));
    }
}
