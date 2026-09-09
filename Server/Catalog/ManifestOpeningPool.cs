using System.Numerics;
using ContrabandCases.Shared.Catalog;

namespace ContrabandCases.Server.Catalog;

/// <summary>
/// One exact package distribution shared by selection, published odds and pricing.
/// Reweights future openings without changing saved package identities or contents.
/// </summary>
internal sealed record ManifestOpeningPool(IReadOnlyList<ResolvedCargoLot> Lots, ExactWeightSet Weights)
{
    internal const string SelectionVersion = "curated-compact-opening-v5";
    internal const int ChaseShareDenominator = 400;

    // Historical lots remain resolvable, but component-only kits have been
    // replaced by complete optics/armorer packages for new offers and Relays.
    internal static bool IsFreshEligible(ResolvedCargoLot lot) =>
        (!ShipmentEconomy.IsCompact(lot.Identity.LotId) ||
            lot.Forest.Roots.Count <= 8 && lot.Forest.Nodes.Count <= 128 && lot.Evaluation.FootprintCells <= 64) &&
        (lot.Identity.ProviderId, ShipmentEconomy.BaseId(lot.Identity.LotId)) is not
            (("eco-attachment.elite-optics", "micro-red-dot-mounts") or
             ("eco-attachment.elite-optics", "larue-rail-system") or
             ("eco-attachment.field-cache", "offset-mount-kit") or
             ("eco-attachment.field-cache", "grip-upgrade-kit") or
             ("eco-attachment.field-cache", "iron-sight-swap"));

    // Desirable thematic chase rewards, plus a guard against mod price outliers.
    // This does not alter pack identities, grades, contents or old commitments.
    internal static bool IsChase(ResolvedCargoLot lot) =>
        (ShipmentEconomy.IsCompact(lot.Identity.LotId) && lot.Identity.ProviderId == "core" &&
            ShipmentEconomy.BaseId(lot.Identity.LotId) == "night-extraction-cache") ||
        lot.Evaluation.UseValue >= (ShipmentEconomy.Generation(lot.Identity.LotId) > 0 ? 4_500_000 : 750_000) ||
        (lot.Identity.ProviderId, ShipmentEconomy.BaseId(lot.Identity.LotId)) is
            ("core", "black-site-marksman") or
            ("core", "black-site-expedition-jackpot") or
            ("more-cases.storage", "equipment-cabinet") or
            ("krackasourus.anime-cards", "erica-ultimate") or
            ("krackasourus.pokemon-cards", "dragonite-holo") or
            ("krackasourus.yugioh-cards", "tri-horned-dragon") or
            ("vault", "vault-twin-rifles") or
            ("sjx.combat-chemistry", "precision-assault");

    internal static ManifestOpeningPool Create(CargoCatalogSnapshot catalog,
        IReadOnlyList<ResolvedCargoLot> familyLots, IReadOnlySet<string>? allowedProviders = null)
    {
        if (catalog.CaseTemplateId == CaseContracts.CashCache)
        {
            var cashLots = ManifestSelectionMath.CanonicalLots(familyLots);
            return new ManifestOpeningPool(cashLots,
                ManifestSelectionMath.CreateExactWeights(cashLots, lot => CashPayoutCatalog.OpeningWeight(lot.Identity.LotId)));
        }
        var providers = ManifestSelectionMath.CanonicalLots(familyLots)
            .GroupBy(lot => lot.Identity.ProviderId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        // Test-tagged cases retain the existing provider-preference fallback.
        if (allowedProviders is { Count: > 0 } && providers.Any(p => allowedProviders.Contains(p.Key)))
            providers = providers.Where(p => allowedProviders.Contains(p.Key)).ToArray();
        var providerWeights = ManifestSelectionMath.CreateExactWeights(providers,
            p => ManifestSelectionMath.GetProviderWeight(catalog, p.Key));
        var lotWeights = providers.Select(p => ManifestSelectionMath.CreateExactWeights(p.ToArray(),
            lot => lot.Identity.Weight)).ToArray();
        var scale = CommonMultiple(lotWeights.Select(w => w.Total));
        var lots = providers.SelectMany(p => p).ToArray();
        var weights = new List<BigInteger>(lots.Length);
        for (var p = 0; p < providers.Length; p++)
            for (var i = 0; i < providers[p].Count(); i++)
                weights.Add(providerWeights[p] * lotWeights[p][i] * (scale / lotWeights[p].Total));

        // Cap the combined chase share at 1/400 within this category, never
        // increase an already rarer share. Ordinary Epic/Legendary wins retain
        // their relative weights. The cap cannot fabricate missing alternatives:
        // a chase-only category keeps its actual, fully published distribution.
        var chase = BigInteger.Zero;
        var ordinary = BigInteger.Zero;
        for (var i = 0; i < lots.Length; i++)
            if (IsChase(lots[i])) chase += weights[i]; else ordinary += weights[i];
        if (ordinary > 0 && chase * ChaseShareDenominator > ordinary + chase)
            for (var i = 0; i < lots.Length; i++)
                weights[i] *= IsChase(lots[i]) ? ordinary : chase * (ChaseShareDenominator - 1);
        var divisor = weights.Aggregate(BigInteger.GreatestCommonDivisor);
        return new ManifestOpeningPool(Array.AsReadOnly(lots),
            new ExactWeightSet(weights.Select(w => w / divisor).ToArray()));
    }

    private static BigInteger CommonMultiple(IEnumerable<BigInteger> values) =>
        values.Aggregate(BigInteger.One, (current, next) =>
            current / BigInteger.GreatestCommonDivisor(current, next) * next);
}
