using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Economy;

namespace ContrabandCases.Server.Catalog;

/// <summary>
/// Computes the value of one Manifest offer from the same family/provider/lot
/// hierarchy used by <see cref="ManifestCatalogSelector"/>.
/// </summary>
public static class ManifestCatalogEconomy
{
    // Standard foregone key sale, not a purchase price or a live trader quote.
    public const decimal OpeningKeyAllowance = Configuration.ModConfig.DefaultKeySellPrice;
    public static IReadOnlyList<decimal> KeyOpportunityCostScenarios { get; } =
        Array.AsReadOnly(new[] { 0m, 25_000m, 65_000m, OpeningKeyAllowance, 150_000m });

    /// <summary>One shared reference-price policy for Mixed and themed cases.</summary>
    public static TicketPrices CalculateAutomaticPrices(CargoCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        // Incomplete catalogs must still load for exact recovery. They cannot
        // open, so retain their single-offer registration valuation only.
        if (!catalog.OpeningEnabled)
            return TicketPriceCalculator.Calculate(CalculateExpectedHandbookValue(catalog), 0.85m, 1_000);
        var outcomes = new ManifestEconomyAnalysis(catalog).SummarizeOpening(1);
        // Raid-earned keys pay part of the access cost. Price below the lesser
        // of the mean and typical chosen reward; a huge tail cannot inflate the
        // ticket above what normal openings deliver. This is reference value,
        // not a promised cash resale return or an individual win/loss quota.
        var reference = Math.Min(outcomes.ExpectedUseValue, outcomes.MedianUseValue);
        return TicketPriceCalculator.Calculate(Math.Max(1_000m,
            reference * 0.90m - OpeningKeyAllowance), 1m, 1_000);
    }

    public static decimal CalculateExpectedHandbookValue(CargoCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        // Recovery-only catalogs retain their historical registration value.
        var families = (catalog.OpeningEnabled ? catalog.FreshOpeningLots : catalog.Lots)
            .GroupBy(catalog.SelectionFamily)
            .OrderBy(group => group.Key)
            .ToArray();
        if (families.Length == 0)
        {
            throw new CargoCatalogValidationException(
                "Manifest pricing requires at least one validated cargo family.");
        }

        decimal familyValueTotal = 0m;
        foreach (var family in families)
        {
            var pool = ManifestOpeningPool.Create(catalog, family.ToArray());
            familyValueTotal = checked(familyValueTotal + pool.Lots.Select((lot, i) =>
                pool.Weights.ProbabilityAt(i).ApproximateDecimal * lot.Evaluation.HandbookValue).Sum());
        }

        var expectedValue = familyValueTotal / families.Length;
        if (expectedValue <= 0m)
        {
            throw new CargoCatalogValidationException(
                "The finalized Manifest catalog has no positive expected handbook value.");
        }

        return expectedValue;
    }

}
