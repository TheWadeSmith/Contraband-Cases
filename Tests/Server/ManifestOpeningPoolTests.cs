using System.Numerics;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class ManifestOpeningPoolTests
{
    [Fact]
    public void Combined_chases_are_capped_and_ordinary_high_grade_wins_keep_their_ratios()
    {
        var lots = new[] { Lot("a", 100_000), Lot("b", 400_000, 3), Lot("c", 1_000_000), Lot("d", 2_000_000, 2) };
        var catalog = CaseCatalogTests.Snapshot(lots);
        var pool = ManifestOpeningPool.Create(catalog, lots);
        var chase = pool.Lots.Select((lot, i) => ManifestOpeningPool.IsChase(lot) ? pool.Weights[i] : BigInteger.Zero).Aggregate(BigInteger.Add);
        Assert.Equal(pool.Weights.Total, chase * 400);
        Assert.Equal(pool.Weights[0] * 3, pool.Weights[1]);
        Assert.Equal(pool.Weights[2] * 2, pool.Weights[3]);
        Assert.All(lots, lot => Assert.Same(lot, catalog.ResolveExact(lot.Evaluation.Grade, lot.Identity, lot.Forest, lot.Fingerprint)));
    }

    [Theory]
    [InlineData(0.0001)]
    [InlineData(1e-40)]
    public void Already_rarer_chases_are_not_promoted(double weight)
    {
        var lots = new[] { Lot("a", 100_000), Lot("b", 1_000_000, weight) };
        var pool = ManifestOpeningPool.Create(CaseCatalogTests.Snapshot(lots), lots);
        var baseline = ManifestSelectionMath.CreateExactWeights(lots, lot => lot.Identity.Weight);
        Assert.Equal(baseline.ProbabilityAt(1).Numerator, pool.Weights.ProbabilityAt(1).Numerator);
        Assert.Equal(baseline.ProbabilityAt(1).Denominator, pool.Weights.ProbabilityAt(1).Denominator);
    }

    [Fact]
    public void Published_odds_and_selector_agree_at_the_exact_chase_boundary()
    {
        var ordinary = Lot("a", 100_000);
        var chase = Lot("z", 1_000_000);
        var catalog = CaseCatalogTests.Snapshot([ordinary, chase, Lot("b", 100_000, family: "family-b"), Lot("c", 100_000, family: "family-c")]);
        var rows = ManifestOpeningOdds.Create(catalog).Families[0].Lots;
        Assert.Equal("399", rows[0].ConditionalNumerator);
        Assert.Equal("400", rows[0].ConditionalDenominator);
        Assert.Equal("1", rows[1].ConditionalNumerator);
        Assert.Equal("400", rows[1].ConditionalDenominator);
        var boundary = (long)(new BigInteger(CanonicalRngEvidence.UnitDenominator) * 399 / 1200);
        Assert.Equal("a", new ManifestCatalogSelector(() => boundary).CreateOffers(catalog)[0].Identity.LotId);
        Assert.Equal("z", new ManifestCatalogSelector(() => boundary + 1).CreateOffers(catalog)[0].Identity.LotId);
        Assert.Equal((100_000m * 399 / 400 + 1_000_000m / 400 + 200_000) / 3,
            ManifestCatalogEconomy.CalculateExpectedHandbookValue(catalog));
    }

    [Fact]
    public void A_large_jackpot_does_not_price_typical_openings_above_their_reward()
    {
        var catalog = CaseCatalogTests.Snapshot([Lot("a", 100_000), Lot("z", 100_000_000),
            Lot("b", 100_000, family: "family-b"), Lot("c", 100_000, family: "family-c")]);
        var price = ManifestCatalogEconomy.CalculateAutomaticPrices(catalog).CasePrice;
        Assert.Equal(70_000, price);
        var summary = new ManifestEconomyAnalysis(catalog).SummarizeOpening(price);
        Assert.True(summary.ExpectedUseValue > summary.MedianUseValue);
        Assert.True(summary.KeyCostScenarios[0].BelowTotalCostPercent < 50);
    }

    [Fact]
    public void Fresh_relay_excludes_chases_but_recovery_can_build_a_legacy_continuation()
    {
        var stake = Lot("stake", 200_000);
        var other = Lot("other", 220_000);
        var jackpot = Lot("jackpot", 1_000_000);
        var catalog = CaseCatalogTests.Snapshot([stake, other, jackpot]);
        var entitlement = new ManifestEntitlementSnapshot(stake.Evaluation.Grade, stake.Identity, stake.Forest, stake.Fingerprint);
        var selector = new ManifestCatalogSelector();
        Assert.Empty(selector.CreateRelayCandidatesForStage(catalog, entitlement, 2));
        Assert.Contains(selector.CreateRelayCandidatesForStage(catalog, entitlement, 2, allowOpeningChases: true),
            candidate => candidate.Identity.LotId == "jackpot");
        Assert.Same(jackpot, catalog.ResolveExact(jackpot.Evaluation.Grade, jackpot.Identity, jackpot.Forest, jackpot.Fingerprint));
    }

    [Theory]
    [InlineData("krackasourus.anime-cards", "erica-ultimate")]
    [InlineData("krackasourus.pokemon-cards", "dragonite-holo")]
    [InlineData("krackasourus.yugioh-cards", "tri-horned-dragon")]
    [InlineData("vault", "vault-twin-rifles")]
    [InlineData("sjx.combat-chemistry", "precision-assault")]
    [InlineData("core", "black-site-marksman")]
    public void Thematic_chases_do_not_need_to_cost_three_quarters_of_a_million(string provider, string id)
    {
        Assert.True(ManifestOpeningPool.IsChase(Lot(id, 350_000, provider: provider)));
        Assert.False(ManifestOpeningPool.IsChase(Lot("ordinary-legendary", 350_000, provider: provider)));
    }

    internal static ResolvedCargoLot Lot(string id, long value, double weight = 1,
        string family = "family-a", string track = "test-track", string provider = "test")
    {
        var definition = new CargoLotDefinition(provider, "1", id, id, "Test package", new FamilyId(family),
            new TrackId(track), id, weight, new RaidRole("test"), [new TemplateLine(id, 1, 1)]);
        var forest = RewardForest.Create([new RewardForestNode("root", "root", id, null, null, null, 1)]);
        var fingerprint = RewardForestFingerprintV2.Compute(provider, id, forest);
        return new ResolvedCargoLot(definition, forest, fingerprint, CargoLotIdentitySnapshot.Capture(definition, fingerprint),
            new CargoLotEvaluation(value, value, 1, CargoGradeBands.Assign(value)));
    }
}
