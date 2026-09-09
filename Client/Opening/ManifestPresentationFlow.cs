using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;

namespace ContrabandCases.Client.Opening;

internal static class LegacyOpeningPresentation
{
    internal static void RequireResumeAuthority(bool libraryOnly, LegacyOpeningSnapshot saved, string? caseId)
    {
        if (libraryOnly || saved is null || saved.Committed || saved.CaseId != caseId)
            throw new InvalidOperationException("Only the exact authenticated pending legacy opening may be resumed.");
    }

    internal static string Summary(LegacyOpeningSnapshot saved) => !saved.Committed
        ? "SAVED OPENING — READY TO RESUME\n\nYour prize was already selected by an older version. Resume sends that exact prize through Mechanic in Messenger. No new roll is made, and no extra case or key is charged."
        : saved.DeliveredToMessenger
            ? "SENT TO MESSENGER\n\nMechanic has your saved prize. Claim individual attachments as stash space allows. Uncollected attachments remain for 10 years; deleting the message discards them. Reopening this receipt never rolls or delivers the prize again."
            : "PREVIOUSLY DELIVERED\n\nThis older reward was already delivered directly to your inventory. It will not be moved or delivered a second time. Check your stash or sorting table.";
}

public enum ManifestResumeKind
{
    ConfirmNew,
    ResumeTicket,
    ShowOffer,
    ShowEntitlement,
    ResumeClaim,
    ResumeRelay,
    ShowTerminal,
    NoPending
}

public enum ManifestEconomicAction
{
    OpenTicket,
    Lock,
    Burn,
    Claim,
    Relay,
    Forfeit
}

public enum ManifestPostOperationRoute
{
    Continue,
    ManualPreparedRecovery
}

public enum ManifestOpeningPreflightDecision
{
    Dispatch,
    Reconfirm,
    ResumeActive
}

public sealed class ManifestTilePresentation
{
    public ManifestTilePresentation(
        string id,
        string displayName,
        string detail,
        RewardRarity grade,
        string? templateId = null,
        string? providerId = null)
    {
        Id = Require(id, nameof(id));
        DisplayName = Require(displayName, nameof(displayName));
        Detail = Require(detail, nameof(detail));
        Grade = Enum.IsDefined(typeof(RewardRarity), grade)
            ? grade
            : throw new ArgumentOutOfRangeException(nameof(grade));
        TemplateId = templateId;
        ProviderId = providerId;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Detail { get; }

    public RewardRarity Grade { get; }

    public string? TemplateId { get; }

    // The reward provider that sourced this tile (e.g. "vault"), when known.
    // Cosmetic only -- drives accent color in the reveal UI, never odds.
    public string? ProviderId { get; }

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty presentation value is required.", parameterName)
            : value;
}

public sealed class ManifestRevealPresentation
{
    internal ManifestRevealPresentation(
        RouletteRevealPlan motion,
        IReadOnlyDictionary<string, ManifestTilePresentation> tiles,
        ManifestLotSnapshot lot,
        string header)
    {
        Motion = motion ?? throw new ArgumentNullException(nameof(motion));
        Tiles = tiles ?? throw new ArgumentNullException(nameof(tiles));
        Lot = lot ?? throw new ArgumentNullException(nameof(lot));
        Header = string.IsNullOrWhiteSpace(header)
            ? throw new ArgumentException("A reveal header is required.", nameof(header))
            : header;
    }

    public RouletteRevealPlan Motion { get; }

    public IReadOnlyDictionary<string, ManifestTilePresentation> Tiles { get; }

    public ManifestLotSnapshot Lot { get; }

    public string Header { get; }
}

public static class ManifestPresentationPolicy
{
    public static string RejectedActionMessage(ManifestEconomicAction action, string? serverError)
    {
        if (action == ManifestEconomicAction.OpenTicket &&
            (serverError?.StartsWith("Epic testing is unavailable for this case:", StringComparison.Ordinal) == true ||
             serverError?.StartsWith("Legendary testing is unavailable for this case:", StringComparison.Ordinal) == true))
        {
            return "This premium testing opening is unavailable: not enough qualifying packages are installed.\n\n" +
                "Your case and key were not consumed. Spawn a different testing case or restore its reward packs.";
        }
        if (action == ManifestEconomicAction.Relay &&
            serverError?.StartsWith("BR-12 Relay Key required.", StringComparison.Ordinal) == true)
        {
            return "You need another BR-12 Relay Key to risk this reward.\n\n" +
                "Your reward is still available. Return to collect it, or put a usable key in your stash before trying Relay again.";
        }

        return action switch
        {
            ManifestEconomicAction.OpenTicket =>
                "The case could not be opened. No saved opening was reported by the server.\n\n" +
                "Return to your stash, check the case and key, then review the current case odds before trying again.",
            ManifestEconomicAction.Relay =>
                "Relay did not go through. Your saved reward has not changed.\n\n" +
                "Return to your reward to collect it or check the Relay requirements. This button does not spend a key or roll again.",
            ManifestEconomicAction.Claim =>
                "Reward delivery could not be confirmed. Your saved result will be checked.\n\n" +
                "Check Mechanic in Messenger, then return to the reward. This button does not reroll or duplicate it.",
            _ =>
                "That action did not go through. Your saved opening has not changed.\n\n" +
                "Return to the opening to review your options. This button does not spend items or repeat your choice."
        };
    }

    internal static void RequireDispatchAuthority(bool libraryOnly, bool recoveryOnly,
        ManifestEconomicAction action, ManifestSnapshot? before)
    {
        if (libraryOnly)
            throw new InvalidOperationException("The read-only gallery/dossier cannot send economic actions.");
        if (recoveryOnly && action == ManifestEconomicAction.OpenTicket && before is null)
            throw new InvalidOperationException("Payout recovery cannot open a new case.");
    }

    // Recovery may follow a successful server commit that already consumed the last key.
    public static bool RequiresFreshOpeningKey(ManifestEconomicAction action, ManifestSnapshot? before) =>
        action == ManifestEconomicAction.OpenTicket && before is null;

    public static bool KeepDetachedObservationAlive(
        bool callbackPending,
        bool operationPendingStage,
        bool verifyingOperationStage,
        bool observationPending) =>
        callbackPending ||
        operationPendingStage ||
        verifyingOperationStage && observationPending;

    public static ManifestResumeKind Resume(ManifestSnapshot? snapshot, bool recoveryOnly = false)
    {
        if (snapshot is null)
        {
            return recoveryOnly ? ManifestResumeKind.NoPending : ManifestResumeKind.ConfirmNew;
        }

        return snapshot.Phase switch
        {
            ManifestPhase.TicketPrepared => ManifestResumeKind.ResumeTicket,
            ManifestPhase.Offer1 or ManifestPhase.Offer2 => ManifestResumeKind.ShowOffer,
            ManifestPhase.Entitlement => ManifestResumeKind.ShowEntitlement,
            ManifestPhase.ClaimPrepared or ManifestPhase.RewardOwed => ManifestResumeKind.ResumeClaim,
            ManifestPhase.RelayPrepared => ManifestResumeKind.ResumeRelay,
            ManifestPhase.Granted or ManifestPhase.Confiscated or ManifestPhase.Forfeited =>
                ManifestResumeKind.ShowTerminal,
            _ => throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot.Phase, null)
        };
    }

    public static ManifestOpeningPreflightDecision OpeningPreflight(
        ManifestOpeningOddsSnapshot displayedOdds,
        ManifestCurrentState current)
    {
        if (displayedOdds is null)
        {
            throw new ArgumentNullException(nameof(displayedOdds));
        }
        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }
        if (current.Snapshot is not null)
        {
            return ManifestOpeningPreflightDecision.ResumeActive;
        }

        var currentOdds = current.OpeningOdds
            ?? throw new InvalidOperationException(
                "The current Manifest state contains neither an active snapshot nor opening odds.");
        return string.Equals(
            displayedOdds.CatalogSnapshotId,
            currentOdds.CatalogSnapshotId,
            StringComparison.Ordinal)
                ? ManifestOpeningPreflightDecision.Dispatch
                : ManifestOpeningPreflightDecision.Reconfirm;
    }

    public static string OpeningSummaryText(ManifestOpeningOddsSnapshot odds)
    {
        if (odds is null)
        {
            throw new ArgumentNullException(nameof(odds));
        }

        if (odds.CaseTemplateId == CaseContracts.CashCache)
            return $"<b>BR-12 CASH CACHE</b>\nPublished case price: ₽{odds.CasePrice?.ToString("N0", CultureInfo.InvariantCulture)} + 1 universal key.\n\n" +
                "One spin, one committed payout. No discard, Relay or Favor.\n" +
                "Payouts may be worth less than your case and key. USD/EUR estimates describe purchase value; GP estimates describe barter reference value, not a rouble cash-out. Bitcoin estimates use standard Therapist pricing.\n\n" +
                "Open Full Odds for every exact amount and chance. No result is drawn until you confirm.";

        var builder = new StringBuilder();
        builder.AppendLine("<b>HOW THIS MANIFEST WORKS</b>");
        builder.AppendLine(CaseContracts.Name(odds.CaseTemplateId));
        builder.AppendLine(CaseContracts.Description(odds.CaseTemplateId));
        if (odds.CasePrice is long price)
            builder.AppendLine($"Published case price: ₽{price.ToString("N0", CultureInfo.InvariantCulture)} · Opening spends this case + 1 universal Relay Key.");
        builder.AppendLine();
        builder.AppendLine(PremiumOddsText(odds, detailed: false));
        builder.AppendLine("<b>NORMAL OPENINGS</b>");
        builder.AppendLine(
            $"1. The server commits {odds.OfferCount.ToString(CultureInfo.InvariantCulture)} different families before the reveal.");
        builder.AppendLine("2. LOCK makes the displayed lot claimable and ends the reveal.");
        builder.AppendLine(
            $"3. DISCARD permanently burns that lot and reveals the next. Offer {odds.OfferCount.ToString(CultureInfo.InvariantCulture)} is forced.");
        builder.AppendLine();
        builder.AppendLine("<b>SERVER-PUBLISHED FAMILY ODDS</b>");
        foreach (var family in odds.Families)
        {
            builder
                .Append("• <b>")
                .Append(family.FamilyLabel)
                .Append("</b>  •  SLOT ")
                .Append(family.PerSlotPercent)
                .Append("  •  INCLUDED ")
                .AppendLine(family.InclusionPercent);
        }

        builder.AppendLine();
        builder.Append(
            "No result has been drawn. The catalog is checked again before any case or key is consumed.");
        return builder.ToString();
    }

    public static string OpeningOddsText(ManifestOpeningOddsSnapshot odds)
    {
        if (odds is null)
        {
            throw new ArgumentNullException(nameof(odds));
        }
        var builder = new StringBuilder();
        builder.AppendLine("<b>FULL SERVER ODDS & AUDIT DETAILS</b>");
        builder.AppendLine(PremiumOddsText(odds, detailed: true));
        if (odds.PremiumOdds is not null) builder.AppendLine("<b>CONDITIONAL ON A NORMAL OPENING</b>");
        builder.AppendLine();
        builder.AppendLine(odds.CaseTemplateId == CaseContracts.CashCache
            ? "One payout is selected from the complete table below. Each payout chance is unconditional. No discard, Relay or Favor."
            : "Three distinct families are selected uniformly without replacement. " +
            "Provider and package weights set the baseline. Opening-only chase rewards share at most 0.25% " +
            "within each family that has ordinary alternatives. Ordinary rewards retain their relative weights.");
        builder.AppendLine(odds.CaseTemplateId == CaseContracts.CashCache
            ? "Exact amounts and probabilities are fixed for this catalog. Currency type is not a rarity or profit guarantee."
            :
            "Every percentage and exact integer ratio below is displayed from the server response. " +
            "Your final claimed reward still depends on your Lock/Burn choices.");
        builder.AppendLine();
        builder.AppendLine("<b>CATALOG SNAPSHOT</b>");
        builder.AppendLine(odds.CatalogSnapshotId);

        foreach (var family in odds.Families)
        {
            builder.AppendLine();
            builder
                .Append("<b>")
                .Append(family.FamilyLabel)
                .AppendLine("</b>")
                .Append("Slot chance: ")
                .Append(family.PerSlotPercent)
                .Append("  •  Included chance: ")
                .Append(family.InclusionPercent)
                .AppendLine()
                .Append("Exact slot numerator: ")
                .AppendLine(family.PerSlotNumerator)
                .Append("Exact slot denominator: ")
                .AppendLine(family.PerSlotDenominator)
                .Append("Exact inclusion numerator: ")
                .Append(family.InclusionNumerator)
                .AppendLine()
                .Append("Exact inclusion denominator: ")
                .AppendLine(family.InclusionDenominator);
            foreach (var lot in family.Lots)
            {
                builder
                    .Append("  • ")
                    .Append(lot.DisplayName)
                    .Append("  —  ")
                    .Append(lot.ProviderLabel)
                    .Append("  —  ")
                    .Append(RewardRarities.GetInfo(lot.Grade).DisplayName)
                    .AppendLine()
                    .Append("    Within family: ")
                    .Append(lot.ConditionalPercent)
                    .AppendLine()
                    .Append("    Exact numerator: ")
                    .AppendLine(lot.ConditionalNumerator)
                    .Append("    Exact denominator: ")
                    .AppendLine(lot.ConditionalDenominator);
            }
        }

        builder.AppendLine();
        builder.Append(
            "No result has been drawn. If the server catalog changes, confirmation is required again.");
        return builder.ToString();
    }

    public static string PremiumOddsText(ManifestOpeningOddsSnapshot odds, bool detailed)
    {
        if (odds.PremiumOdds is not { } premium) return string.Empty;
        string Rate(int basisPoints) => (basisPoints / 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";
        var builder = new StringBuilder("<b>RARE SURPRISE OPENINGS</b>\n");
        builder.AppendLine($"Normal {Rate(10_000 - premium.Epic.ChanceBasisPoints - premium.Legendary.ChanceBasisPoints)} • " +
            $"Epic {Rate(premium.Epic.ChanceBasisPoints)} • Legendary {Rate(premium.Legendary.ChanceBasisPoints)}");
        builder.AppendLine("Epic: browse three saved packages and choose ONE. Legendary: one rolled prize, no choice. No extra key.");
        foreach (var (label, tier) in new[] { ("Epic", premium.Epic), ("Legendary", premium.Legendary) })
        {
            if (tier.ChanceBasisPoints == 0)
            {
                builder.AppendLine($"{label} unavailable: not enough qualifying packages installed. This tier is not rolled.");
                continue;
            }
            builder.AppendLine($"{label}: {tier.Lots.Count} eligible packages • reference value at least ₽{tier.MinimumUseValue.ToString("N0", CultureInfo.InvariantCulture)} (not resale).");
            if (!detailed) continue;
            builder.AppendLine(label == "Legendary"
                ? "One prize from the draw below, conditional on a Legendary opening. Combined chase share stays at most 0.25%."
                : "First package draw below; subsequent draws exclude chosen packages and recalculate weights. Combined chase share stays at most 0.25% per draw. These are not final claim probabilities.");
            foreach (var lot in tier.Lots)
                builder.AppendLine($"• {PlainText(lot.DisplayName)} — {lot.ConditionalPercent} ({lot.ConditionalNumerator}/{lot.ConditionalDenominator})");
        }
        return builder.ToString();
    }

    public static string FamilySealLabel(ManifestFamilySealSnapshot seal)
    {
        if (seal is null)
        {
            throw new ArgumentNullException(nameof(seal));
        }
        return seal.Revealed || CargoFamilies.IsSignal(seal.FamilyLabel)
            ? seal.FamilyLabel
            : $"SEALED FAMILY {seal.Ordinal.ToString(CultureInfo.InvariantCulture)}";
    }

    public static bool MatchesActionPrecondition(
        ManifestSnapshot expected,
        ManifestSnapshot? current,
        ManifestEconomicAction action)
    {
        if (expected is null)
        {
            throw new ArgumentNullException(nameof(expected));
        }
        if (current is null ||
            !string.Equals(expected.ManifestId, current.ManifestId, StringComparison.Ordinal) ||
            expected.Phase != current.Phase ||
            expected.CurrentOrdinal != current.CurrentOrdinal ||
            expected.OpeningTier != current.OpeningTier ||
            !expected.PremiumChoices.Select(lot => (lot.ProviderId, lot.LotId, lot.Fingerprint))
                .SequenceEqual(current.PremiumChoices.Select(lot => (lot.ProviderId, lot.LotId, lot.Fingerprint))) ||
            expected.RelayStage != current.RelayStage ||
            !string.Equals(
                expected.CurrentLot?.Fingerprint,
                current.CurrentLot?.Fingerprint,
                StringComparison.Ordinal))
        {
            return false;
        }

        var actions = current.AvailableActions;
        return action switch
        {
            ManifestEconomicAction.Lock => actions.CanLock,
            ManifestEconomicAction.Burn => actions.CanBurn,
            ManifestEconomicAction.Claim => actions.CanClaim,
            ManifestEconomicAction.Relay => actions.CanRelay,
            ManifestEconomicAction.Forfeit => actions.CanForfeit,
            ManifestEconomicAction.OpenTicket => current.Phase == ManifestPhase.TicketPrepared,
            _ => false
        };
    }

    public static bool ShouldRevealAfter(
        ManifestSnapshot? before,
        ManifestSnapshot after,
        bool recovering)
    {
        if (after is null)
        {
            throw new ArgumentNullException(nameof(after));
        }
        if (after.CurrentLot is null)
        {
            return false;
        }
        if (before?.IsPremium == true && before.Phase == ManifestPhase.Offer1 &&
            after.Phase == ManifestPhase.Entitlement) return false;
        if (before?.CurrentLot is { } previous)
        {
            return !string.Equals(
                previous.Fingerprint,
                after.CurrentLot.Fingerprint,
                StringComparison.Ordinal);
        }

        // An existing offer is already saved. Reopening its window must not look
        // like a second roll. A newly committed, different lot still animates above.
        return !recovering;
    }

    public static bool IsClaimRetry(
        ManifestEconomicAction action,
        ManifestSnapshot before,
        ManifestSnapshot after) =>
        action == ManifestEconomicAction.Claim &&
        before.Phase == ManifestPhase.Entitlement &&
        after.Phase == ManifestPhase.Entitlement &&
        string.Equals(before.ManifestId, after.ManifestId, StringComparison.Ordinal) &&
        string.Equals(
            before.CurrentLot?.Fingerprint,
            after.CurrentLot?.Fingerprint,
            StringComparison.Ordinal);

    public static ManifestEconomicAction PreparedRecoveryAction(ManifestSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        return snapshot.Phase switch
        {
            ManifestPhase.TicketPrepared => ManifestEconomicAction.OpenTicket,
            ManifestPhase.ClaimPrepared or ManifestPhase.RewardOwed => ManifestEconomicAction.Claim,
            ManifestPhase.RelayPrepared => ManifestEconomicAction.Relay,
            _ => throw new InvalidOperationException(
                $"Manifest phase '{snapshot.Phase}' is not a prepared recovery phase.")
        };
    }

    public static ManifestPostOperationRoute AfterOperation(ManifestSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        return snapshot.Phase is ManifestPhase.TicketPrepared or
            ManifestPhase.ClaimPrepared or
            ManifestPhase.RewardOwed or
            ManifestPhase.RelayPrepared
                ? ManifestPostOperationRoute.ManualPreparedRecovery
                : ManifestPostOperationRoute.Continue;
    }

    public static ManifestRevealPresentation CreateReveal(
        ManifestSnapshot snapshot,
        bool reducedMotion,
        int seed,
        int tileCount,
        int landingIndex,
        double viewportWidth,
        double tileWidth,
        double tileSpacing,
        int nearMissChancePercent = 35,
        double baseDurationSeconds = 4.5d,
        ManifestOpeningOddsSnapshot? publishedCatalog = null)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        var lot = snapshot.CurrentLot
            ?? throw new InvalidOperationException("A disclosed current lot is required for a Manifest reveal.");
        var winnerId = $"lot:{lot.Fingerprint}";
        var tiles = new Dictionary<string, ManifestTilePresentation>(StringComparer.Ordinal)
        {
            [winnerId] = new(
                winnerId,
                lot.DisplayName,
                $"{lot.ProviderLabel} • ANCHOR",
                lot.Grade,
                lot.AnchorTemplateId,
                lot.ProviderId)
        };

        // Missing catalogs use neutral seals, never copies of the winning
        // contents/grade. No winner-dependent near-miss neighbors are forced.
        foreach (var seal in publishedCatalog is null && snapshot.PremiumChoices.Count == 0
                     ? snapshot.FamilySeals : Array.Empty<ManifestFamilySealSnapshot>())
        {
            var id = $"seal:{seal.Ordinal.ToString(CultureInfo.InvariantCulture)}";
            tiles[id] = new ManifestTilePresentation(
                id,
                FamilySealLabel(seal),
                "CONTENTS UNDISCLOSED",
                snapshot.OpeningTier == ManifestOpeningTier.Legendary ? RewardRarity.BlackLabel : RewardRarity.ScavGrade);
        }

        if (snapshot.PremiumChoices.Count > 0)
        {
            foreach (var choice in snapshot.PremiumChoices.Skip(1))
            {
                var id = $"premium:{choice.ProviderId}:{choice.LotId}";
                tiles[id] = new ManifestTilePresentation(id, choice.DisplayName, "ONE OF YOUR THREE CHOICES",
                    choice.Grade, choice.AnchorTemplateId, choice.ProviderId);
            }
        }
        else if (publishedCatalog is not null)
        {
            var previews = publishedCatalog.Families.SelectMany(family => family.Lots);
            if (snapshot.OpeningTier == ManifestOpeningTier.Legendary)
                previews = publishedCatalog.PremiumOdds?.Legendary.Lots ?? [];
            foreach (var preview in previews)
            {
                if (preview.ProviderId == lot.ProviderId && preview.LotId == lot.LotId) continue;
                var id = $"catalog:{preview.ProviderId}:{preview.LotId}";
                tiles[id] = new ManifestTilePresentation(id, preview.DisplayName,
                    preview.ProviderLabel, preview.Grade, preview.AnchorTemplateId, preview.ProviderId);
            }
        }
        var pool = tiles.Keys.Where(id => id != winnerId).ToArray();
        var motion = RouletteRevealPlan.Create(
            pool,
            winnerId,
            tileCount,
            landingIndex,
            seed,
            viewportWidth,
            tileWidth,
            tileSpacing,
            reducedMotion,
            null,
            0,
            baseDurationSeconds);
        var header = snapshot.PremiumChoices.Count > 0 ? $"SURPRISE {snapshot.OpeningTier.ToString().ToUpperInvariant()} OPENING • CHOOSE ONE AFTER REVEAL"
            : snapshot.OpeningTier == ManifestOpeningTier.Legendary ? "SURPRISE LEGENDARY OPENING • ONE PRIZE"
            : snapshot.CaseTemplateId == CaseContracts.CashCache ? "CASH CACHE • ONE PAYOUT" : snapshot.LatestReceipt is { } receipt
            ? receipt.BrokerFavorBefore == snapshot.BrokerFavorMaximum &&
              receipt.Outcome == ManifestRelayResult.Upgrade
                ? "BROKER FAVOR • GUARANTEED UPGRADE"
                : "RELAY RESULT"
            : snapshot.Phase == ManifestPhase.Entitlement && snapshot.CurrentOrdinal == 3
                ? "FINAL OFFER • YOUR REWARD"
                : $"OFFER {snapshot.CurrentOrdinal.ToString(CultureInfo.InvariantCulture)} OF 3";
        return new ManifestRevealPresentation(
            motion,
            new ReadOnlyDictionary<string, ManifestTilePresentation>(tiles),
            lot,
            header);
    }

    public static string LotSummary(ManifestSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        var lot = snapshot.CurrentLot
            ?? throw new InvalidOperationException("An active lot is required for its summary.");
        var grade = RewardRarities.GetInfo(lot.Grade).DisplayName;
        var contents = string.Join("\n", lot.Contents.Select(content =>
            $"• {content.DisplayName}" +
            (content.Quantity == 1
                ? string.Empty
                : $" ×{content.Quantity.ToString(CultureInfo.InvariantCulture)}")));
        var values = lot.UseValue is not null && lot.FootprintCells is int cells
            ? BrokerPresentation.Value(lot) + $"  •  {cells} cells"
            : "Valuation unavailable — this lot is blocked until its content pack returns.";
        return
            $"<b>{lot.DisplayName}</b>  •  {grade}\n" +
            $"Provider: {lot.ProviderLabel}  •  {lot.Purpose}\n" +
            $"{values}\n\n{contents}";
    }

    public static string LotHeading(ManifestSnapshot snapshot)
    {
        var lot = snapshot?.CurrentLot
            ?? throw new InvalidOperationException("An active lot is required for its heading.");
        return LotHeading(lot);
    }

    public static string LotHeading(ManifestLotSnapshot lot)
    {
        return
            $"<b>{PlainText(lot.DisplayName)}</b>  •  {RewardRarities.GetInfo(lot.Grade).DisplayName}\n" +
            $"{PlainText(lot.Purpose)}\n<color=#A9B7BE>{PlainText(lot.ProviderLabel)}</color>";
    }

    public static string LotDetails(ManifestSnapshot snapshot)
    {
        var lot = snapshot?.CurrentLot
            ?? throw new InvalidOperationException("An active lot is required for its details.");
        return LotDetails(lot, snapshot.CaseTemplateId, snapshot.MissingContentBlocked);
    }

    public static string LotDetails(ManifestLotSnapshot lot, string caseTemplateId, bool missingContentBlocked = false)
    {
        var values = lot.UseValue is long use &&
                     lot.FootprintCells is int cells
            ? caseTemplateId == CaseContracts.CashCache
                ? $"{CashPayouts.ValueLabel(lot.AnchorTemplateId)}: ₽{use.ToString("N0", CultureInfo.InvariantCulture)}  •  {cells} total storage cells"
                : BrokerPresentation.Value(lot) + $"\n{cells} total storage cells. " + BrokerPresentation.ResaleBasis
            : caseTemplateId == CaseContracts.CashCache && !missingContentBlocked
                ? "Current conversion estimate unavailable. Your committed payout is unchanged."
                : "Valuation unavailable — this lot is blocked until its content pack returns.";
        return values + "\n" + string.Join("\n", lot.Contents.Select(content =>
            $"• {PlainText(content.DisplayName)}" +
            (content.Quantity == 1
                ? string.Empty
                : $" ×{content.Quantity.ToString(CultureInfo.InvariantCulture)}")));
    }

    public static string PlainText(string text) => text.Replace("<", "‹").Replace(">", "›");

    public static string OfferDecisionPrompt(ManifestSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        if (!snapshot.AvailableActions.CanLock)
        {
            throw new InvalidOperationException("A safe Lock action is required for an offer decision.");
        }

        if (!snapshot.AvailableActions.CanBurn)
        {
            return
                "<b>LOCK THIS LOT</b>\n" +
                "The next persisted offer is unavailable; no discard action will be sent.";
        }

        return snapshot.CurrentOrdinal == 1
            ? "Keep this reward for Messenger delivery or Relay. Discard gives it up permanently.\nNext: " + FamilySealLabel(snapshot.FamilySeals[1])
            : "Keep this reward, or take the final offer. You cannot come back.\nNext: " + FamilySealLabel(snapshot.FamilySeals[2]);
    }

    public static string OfferDiscardActionLabel(ManifestSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        return !snapshot.AvailableActions.CanBurn
            ? "DISCARD UNAVAILABLE"
            : snapshot.CurrentOrdinal == 1
                ? "DISCARD & REVEAL NEXT"
                : "DISCARD & REVEAL FINAL";
    }

    public static string RelayOdds(ManifestSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        var relay = snapshot.Relay;
        if (relay is null || !snapshot.AvailableActions.CanRelay)
        {
            return snapshot.RelayTerminal
                ? "Relay chain complete — send your saved prize to Messenger."
                : "Relay unavailable — send your saved prize to Messenger.";
        }
        if (relay.GuaranteeActive)
        {
            return "<b>BROKER FAVOR 3/3 — NEXT ELIGIBLE RELAY IS A GUARANTEED UPGRADE</b>";
        }

        return
            $"<color=#85C994>↑ UPGRADE {relay.UpgradePercent}%</color>   •   REPLACE {relay.SidegradePercent}%   •   " +
            $"<color=#ED967D>× LOSE IT {relay.ConfiscatePercent}%</color>";
    }
}
