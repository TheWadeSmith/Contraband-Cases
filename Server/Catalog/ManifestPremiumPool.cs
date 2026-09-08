using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;

namespace ContrabandCases.Server.Catalog;

internal static class ManifestPremiumPool
{
    internal static long MinimumUseValue(CargoCatalogSnapshot catalog, ManifestOpeningTier tier)
    {
        var totalCost = (decimal)(catalog.CasePrice ?? 0) + ManifestCatalogEconomy.OpeningKeyAllowance;
        return checked((long)decimal.Ceiling(tier == ManifestOpeningTier.Legendary
            ? Math.Max(300_000m, totalCost * 1.6m)
            : Math.Max(200_000m, totalCost * 1.15m)));
    }

    internal static IReadOnlyList<ResolvedCargoLot> Candidates(CargoCatalogSnapshot catalog, ManifestOpeningTier tier)
    {
        if (tier is not (ManifestOpeningTier.Epic or ManifestOpeningTier.Legendary))
            throw new ArgumentOutOfRangeException(nameof(tier));
        if (catalog.CaseTemplateId == CaseContracts.CashCache || !catalog.OpeningEnabled)
            return [];
        var floor = MinimumUseValue(catalog, tier);
        return ManifestSelectionMath.CanonicalLots(catalog.FreshOpeningLots.Where(lot =>
            lot.Evaluation.UseValue >= floor && (tier == ManifestOpeningTier.Legendary
                ? lot.Evaluation.Grade == RewardRarity.BlackLabel
                : lot.Evaluation.Grade is RewardRarity.Restricted or RewardRarity.BlackLabel)));
    }

    // Three ordinary qualifying packages ensure without-replacement draws never
    // force a chase because other candidates ran out. Each draw retains the chase cap.
    internal static bool IsAvailable(CargoCatalogSnapshot catalog, ManifestOpeningTier tier) =>
        Candidates(catalog, tier).Count(lot => !ManifestOpeningPool.IsChase(lot)) >= 3;
}
