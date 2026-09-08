using System.Globalization;
using System.Text;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;

namespace ContrabandCases.Client.Opening;

/// <summary>Player-facing copy over published data; no selection or valuation decisions.</summary>
internal static class BrokerPresentation
{
    internal static string Text(string value) => ManifestPresentationPolicy.PlainText(value);

    internal static string CaseName(string template) => template == ModConstants.CaseTemplateId
        ? "BR-12 Mixed Case" : CaseContracts.Name(template);

    internal static string Theme(string template) => template switch
    {
        CaseContracts.Operations => "Raid supplies • Equipment • Loadouts",
        CaseContracts.Relics => "Collectible cards • Relics • Curios",
        CaseContracts.BlackSite => "Night operations • Ordnance • Specialist gear",
        CaseContracts.CashCache => "Roubles • Dollars • Euros • GP Coins • Bitcoin",
        _ => "Mixed cargo • All available reward packs"
    };

    internal static string Overview(ManifestOpeningOddsSnapshot odds)
    {
        var price = odds.CasePrice is long value ? $"Trader purchase price: ₽{value.ToString("N0", CultureInfo.InvariantCulture)}\n" : "";
        var rules = odds.CaseTemplateId == CaseContracts.CashCache
            ? "One spin. One payout. No discard or Relay.\nForeign currency value is not a guaranteed rouble cash-out."
            : "Up to 3 offers. Choose one, or discard for the next.\nDiscard is permanent; offer 3 is final.\nThen claim your items, or risk them + 1 key on Relay.";
        return "Uses this case + 1 universal key.\nNo additional roubles charged.\n" + price + "\n" + rules +
            "\n\nValue can be below your cost. Rarity does not guarantee profit.";
    }

    internal static string TierRates(ManifestOpeningOddsSnapshot odds)
    {
        if (odds.PremiumOdds is not { } p) return odds.CaseTemplateId == CaseContracts.CashCache
            ? "CASH CACHE • ONE PAYOUT • NO PREMIUM TIER"
            : "Premium tier information unavailable from this server. Check the exact odds before opening.";
        string Rate(int n) => (n / 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";
        return $"Normal {Rate(10_000 - p.Epic.ChanceBasisPoints - p.Legendary.ChanceBasisPoints)}   •   " +
            $"Epic {Rate(p.Epic.ChanceBasisPoints)}   •   Legendary {Rate(p.Legendary.ChanceBasisPoints)}\n" +
            "Rare premium openings: compare three saved packages and choose ONE. No extra key.";
    }

    internal static string ReadableOdds(ManifestOpeningOddsSnapshot odds)
    {
        var cash = odds.CaseTemplateId == CaseContracts.CashCache;
        var b = new StringBuilder(TierRates(odds)).AppendLine().AppendLine();
        b.AppendLine(cash ? "PAYOUT CHANCES — one draw from this table." :
            "NORMAL OPENINGS ONLY\nCategories are selected without replacement. Package chances below are WITHIN their category, not final-claim odds. Your keep/discard choices affect what you claim.");
        foreach (var family in odds.Families)
        {
            b.AppendLine().AppendLine($"<b>{Text(family.FamilyLabel)}</b>");
            if (!cash) b.AppendLine($"Category per offer slot: {family.PerSlotPercent} • Included among saved offers: {family.InclusionPercent}");
            foreach (var lot in family.Lots)
                b.AppendLine($"{Text(lot.DisplayName)}\n  {RewardRarities.GetInfo(lot.Grade).DisplayName} • {lot.ConditionalPercent}" +
                    (cash ? " per opening" : " within this category"));
        }
        if (odds.PremiumOdds is not null)
        {
            b.AppendLine().AppendLine("<b>PREMIUM PACKAGE CHANCES</b>");
            b.AppendLine("First package draw, conditional on that opening tier. Later draws exclude earlier choices and recalculate weights. You choose ONE, so these are not final-claim probabilities.");
            foreach (var (name, tier) in new[] { ("EPIC", odds.PremiumOdds.Epic), ("LEGENDARY", odds.PremiumOdds.Legendary) })
            {
                b.AppendLine().AppendLine($"<b>{name}</b>");
                if (tier.ChanceBasisPoints == 0) { b.AppendLine("Unavailable with the installed catalog; not rolled."); continue; }
                b.AppendLine($"Minimum reference value ₽{tier.MinimumUseValue.ToString("N0", CultureInfo.InvariantCulture)} — not guaranteed resale.");
                foreach (var lot in tier.Lots)
                    b.AppendLine($"{Text(lot.DisplayName)} • {lot.ConditionalPercent} on the first package draw");
            }
        }
        return b.ToString();
    }

    internal static string PackageDetails(ManifestLotSnapshot lot) =>
        $"<b>{Text(lot.DisplayName)}</b>\n{Text(lot.ProviderLabel)} • {Text(CargoFamilies.Label(lot.FamilyId))}\n" +
        $"{Text(lot.Purpose)}\n" +
        Value(lot) + (lot.ProviderId == CashPayouts.Provider ? "" : "\n" + ResaleBasis) + "\n\n" + FullContents(lot);

    internal static string FullContents(ManifestLotSnapshot lot) =>
        string.Join("\n", lot.Contents.Select(item => $"{item.Quantity} × {Text(item.DisplayName)}"));

    internal static string ContentsPreview(ManifestLotSnapshot lot) =>
        string.Join("\n", lot.Contents.Take(2).Select(item => $"{item.Quantity} × {Text(item.DisplayName)}")) +
        (lot.Contents.Count > 2 ? $"\n+{lot.Contents.Count - 2} more item type{(lot.Contents.Count == 3 ? "" : "s")}" : "");

    internal static string RelayEssentials(ManifestSnapshot snapshot)
    {
        if (snapshot.Relay is not { } relay || !snapshot.AvailableActions.CanRelay)
            return "Claim this package at no extra cost. Relay is unavailable for this reward.";
        return ManifestPresentationPolicy.RelayOdds(snapshot) + "\n" +
            (relay.GuaranteeActive
                ? $"Hold to replace this package with a guaranteed upgrade + spend {relay.KeyCost} key. Favor resets."
                : $"Hold to stake this whole package + {relay.KeyCost} key. Loss takes both.\nReplace gives a different same-rarity package and ends the chain.");
    }

    internal static string Value(ManifestLotSnapshot lot, bool compact = false)
    {
        if (lot.UseValue is not long value) return "Reference value unavailable";
        var formatted = value.ToString("N0", CultureInfo.InvariantCulture);
        return lot.ProviderId == CashPayouts.Provider
            ? $"{CashPayouts.ValueLabel(lot.AnchorTemplateId)}: ₽{formatted}"
            : (compact ? $"Reference ₽{formatted}\n" : $"Reference value ₽{formatted} • not cash/resale\n") +
              (lot.TraderResaleEstimate is long resale
                  ? $"Est. resale ₽{resale.ToString("N0", CultureInfo.InvariantCulture)}*"
                  : "Resale estimate unavailable");
    }

    internal const string ResaleBasis = "*Handbook-based estimate at catalog startup: best eligible default-unlocked RUB buyer per complete item, excluding Fence. Before profile bonuses; not a guaranteed quote or flea value.";
}
