using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;

namespace ContrabandCases.Server.Catalog;

/// <summary>
/// Publishes the complete pre-opening probability table for one immutable,
/// finalized catalog without selecting or exposing any future offer.
/// </summary>
public static class ManifestOpeningOdds
{
    public static ManifestOpeningOddsData Create(CargoCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.OpeningEnabled)
        {
            throw new InvalidOperationException(
                catalog.OpeningDisabledReason ??
                "The frozen cargo catalog does not permit Manifest openings.");
        }

        if (catalog.FreshOpeningLots.Count > ManifestOpeningOddsData.MaximumTotalLotCount)
        {
            throw new CargoCatalogValidationException(
                "The opening-odds table exceeds the supported total lot count.");
        }

        var lots = ManifestSelectionMath.CanonicalLots(catalog.FreshOpeningLots);
        var families = lots
            .GroupBy(catalog.SelectionFamily)
            .OrderBy(group => group.Key)
            .ToArray();
        if (families.Length < catalog.OfferCount ||
            families.Length > ManifestOpeningOddsData.MaximumFamilyCount)
        {
            throw new CargoCatalogValidationException(
                "The opening-odds table has an unsupported family count.");
        }

        var perSlot = new ExactProbability(1, families.Length);
        var inclusion = new ExactProbability(catalog.OfferCount, families.Length);
        var familyRows = new ManifestOpeningFamilyOddsData[families.Length];
        for (var familyIndex = 0; familyIndex < families.Length; familyIndex++)
        {
            var family = families[familyIndex];
            var familyLots = family.ToArray();
            if (familyLots.Length > ManifestOpeningOddsData.MaximumLotsPerFamily)
            {
                throw new CargoCatalogValidationException(
                    $"Opening-odds family '{family.Key.Value}' exceeds the supported lot count.");
            }

            familyRows[familyIndex] = new ManifestOpeningFamilyOddsData
            {
                FamilyId = family.Key.Value,
                FamilyLabel = CaseContracts.UsesTrackGroups(catalog.CaseTemplateId)
                    ? CaseContracts.GroupLabel(catalog.CaseTemplateId, family.Key.Value)
                    : ManifestSnapshotProjection.FamilyLabel(family.Key.Value),
                PerSlotNumerator = perSlot.NumeratorText,
                PerSlotDenominator = perSlot.DenominatorText,
                PerSlotPercent = perSlot.PercentText,
                InclusionNumerator = inclusion.NumeratorText,
                InclusionDenominator = inclusion.DenominatorText,
                InclusionPercent = inclusion.PercentText,
                Lots = CreateLotRows(catalog, familyLots)
            };
        }

        return new ManifestOpeningOddsData
        {
            CaseTemplateId = catalog.CaseTemplateId,
            PremiumOdds = catalog.CaseTemplateId == CaseContracts.CashCache ? null : new ManifestPremiumOddsData
            {
                Epic = CreatePremiumTier(catalog, ManifestOpeningTier.Epic),
                Legendary = CreatePremiumTier(catalog, ManifestOpeningTier.Legendary)
            },
            SelectionRule = catalog.CaseTemplateId == CaseContracts.CashCache
                ? CashPayouts.SelectionRule : ManifestOpeningOddsData.SelectionRuleValue,
            CasePrice = catalog.CasePrice,
            CatalogSnapshotId = catalog.SnapshotId,
            OfferCount = catalog.OfferCount,
            FamilyCount = families.Length,
            Families = new ReadOnlyCollection<ManifestOpeningFamilyOddsData>(familyRows)
        };
    }

    private static ManifestPremiumTierOddsData CreatePremiumTier(CargoCatalogSnapshot catalog, ManifestOpeningTier tier)
    {
        var available = ManifestPremiumPool.IsAvailable(catalog, tier);
        return new ManifestPremiumTierOddsData
        {
            ChanceBasisPoints = !available ? 0 : tier == ManifestOpeningTier.Epic
                ? ManifestOpeningTierRules.EpicBasisPoints : ManifestOpeningTierRules.LegendaryBasisPoints,
            MinimumUseValue = ManifestPremiumPool.MinimumUseValue(catalog, tier),
            Lots = available ? CreateLotRows(catalog, ManifestPremiumPool.Candidates(catalog, tier)) : []
        };
    }

    private static IReadOnlyList<ManifestOpeningLotOddsData> CreateLotRows(
        CargoCatalogSnapshot catalog,
        IReadOnlyList<ResolvedCargoLot> familyLots)
    {
        var pool = ManifestOpeningPool.Create(catalog, familyLots);
        var rows = new List<ManifestOpeningLotOddsData>(familyLots.Count);
        for (var index = 0; index < pool.Lots.Count; index++)
        {
            var lot = pool.Lots[index];
            var conditional = pool.Weights.ProbabilityAt(index);
            rows.Add(new ManifestOpeningLotOddsData
            {
                ProviderId = lot.Identity.ProviderId,
                ProviderLabel = ManifestSnapshotProjection.ProviderLabel(lot.Identity.ProviderId),
                LotId = lot.Identity.LotId,
                DisplayName = lot.Identity.DisplayName,
                Grade = lot.Evaluation.Grade.ToString(),
                AnchorTemplateId = lot.Identity.AnchorTemplateId,
                ConditionalNumerator = conditional.NumeratorText,
                ConditionalDenominator = conditional.DenominatorText,
                ConditionalPercent = conditional.PercentText
            });
        }

        // Pool order is part of deterministic selection. Sort only the published
        // rows, keeping each exact probability attached to its original package.
        return Array.AsReadOnly(rows.OrderBy(row => row.ProviderId, StringComparer.Ordinal)
            .ThenBy(row => row.LotId, StringComparer.Ordinal).ToArray());
    }

}

internal static class ManifestSelectionMath
{
    internal static IReadOnlyList<ResolvedCargoLot> CanonicalLots(
        IEnumerable<ResolvedCargoLot> lots)
    {
        ArgumentNullException.ThrowIfNull(lots);
        return new ReadOnlyCollection<ResolvedCargoLot>(lots
            .Select(lot => lot ?? throw new CargoCatalogValidationException(
                "A frozen catalog cannot contain a null lot."))
            .OrderBy(lot => lot.Identity.FamilyId.Value, StringComparer.Ordinal)
            .ThenBy(lot => lot.Identity.ProviderId, StringComparer.Ordinal)
            .ThenBy(lot => lot.Identity.PackVersion, StringComparer.Ordinal)
            .ThenBy(lot => lot.Identity.LotId, StringComparer.Ordinal)
            .ThenBy(lot => lot.Fingerprint.Sha256Hex, StringComparer.Ordinal)
            .ToArray());
    }

    internal static double GetProviderWeight(
        CargoCatalogSnapshot catalog,
        string providerId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.ProviderWeights.TryGetValue(providerId, out var weight) ||
            !double.IsFinite(weight) ||
            weight <= 0d)
        {
            throw new CargoCatalogValidationException(
                $"Frozen catalog provider '{providerId}' has no valid selection weight.");
        }

        return weight;
    }

    internal static ExactWeightSet CreateExactWeights<T>(
        IReadOnlyList<T> candidates,
        Func<T, double> weightSelector)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(weightSelector);
        if (candidates.Count == 0)
        {
            throw new CargoCatalogValidationException(
                "Weighted selection requires at least one candidate.");
        }

        var exactWeights = new (BigInteger Significand, int BinaryExponent)[candidates.Count];
        var minimumExponent = int.MaxValue;
        for (var index = 0; index < candidates.Count; index++)
        {
            var weight = weightSelector(candidates[index]);
            if (!double.IsFinite(weight) || weight <= 0d)
            {
                throw new CargoCatalogValidationException(
                    "Selection weights must be positive and finite.");
            }

            exactWeights[index] = ExactPositiveDouble(weight);
            minimumExponent = Math.Min(minimumExponent, exactWeights[index].BinaryExponent);
        }

        var scaledWeights = exactWeights
            .Select(weight =>
                weight.Significand << checked(weight.BinaryExponent - minimumExponent))
            .ToArray();
        return new ExactWeightSet(scaledWeights);
    }

    private static (BigInteger Significand, int BinaryExponent) ExactPositiveDouble(
        double value)
    {
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var exponentBits = checked((int)((bits >> 52) & 0x7ffUL));
        var fraction = bits & 0x000f_ffff_ffff_ffffUL;
        return exponentBits == 0
            ? (new BigInteger(fraction), -1074)
            : (new BigInteger(fraction | (1UL << 52)), exponentBits - 1075);
    }
}

internal sealed class ExactWeightSet
{
    private readonly BigInteger[] _weights;

    internal ExactWeightSet(BigInteger[] weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Length == 0 || weights.Any(weight => weight <= BigInteger.Zero))
        {
            throw new CargoCatalogValidationException(
                "Canonical selection weights must be positive and non-empty.");
        }

        _weights = weights;
        Total = weights.Aggregate(BigInteger.Zero, (sum, weight) => sum + weight);
    }

    internal BigInteger Total { get; }

    internal BigInteger this[int index] => _weights[index];

    internal ExactProbability ProbabilityAt(int index) =>
        new(_weights[index], Total);
}

internal readonly struct ExactProbability
{
    internal ExactProbability(BigInteger numerator, BigInteger denominator)
    {
        if (numerator < BigInteger.Zero ||
            denominator <= BigInteger.Zero ||
            numerator > denominator)
        {
            throw new CargoCatalogValidationException(
                "A Manifest probability must be between zero and one.");
        }

        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
        NumeratorText = FormatBoundedInteger(Numerator);
        DenominatorText = FormatBoundedInteger(Denominator);
        PercentText = FormatPercent(Numerator, Denominator);
    }

    internal BigInteger Numerator { get; }

    internal BigInteger Denominator { get; }

    internal string NumeratorText { get; }

    internal string DenominatorText { get; }

    internal string PercentText { get; }

    internal decimal ApproximateDecimal
    {
        get
        {
            const decimal scale = 1_000_000_000_000_000_000_000_000m;
            return (decimal)(Numerator * new BigInteger(scale) / Denominator) / scale;
        }
    }

    internal static ExactProbability Multiply(ExactProbability left, ExactProbability right) =>
        new(left.Numerator * right.Numerator, left.Denominator * right.Denominator);

    private static string FormatBoundedInteger(BigInteger value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Length > ManifestOpeningOddsData.MaximumRationalDigits)
        {
            throw new CargoCatalogValidationException(
                "An opening-odds rational exceeds the supported digit count.");
        }

        return text;
    }

    private static string FormatPercent(BigInteger numerator, BigInteger denominator)
    {
        var hundredths = BigInteger.DivRem(
            numerator * 10_000,
            denominator,
            out var remainder);
        if (remainder * 2 >= denominator)
        {
            hundredths++;
        }

        var whole = BigInteger.DivRem(hundredths, 100, out var fraction);
        return string.Concat(
            whole.ToString(CultureInfo.InvariantCulture),
            ".",
            checked((int)fraction).ToString("D2", CultureInfo.InvariantCulture),
            "%");
    }
}
