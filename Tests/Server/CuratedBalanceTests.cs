using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class CuratedBalanceTests
{
    [Theory]
    [InlineData(399999, RewardRarity.ScavGrade)]
    [InlineData(400000, RewardRarity.Uncommon)]
    [InlineData(799999, RewardRarity.Uncommon)]
    [InlineData(800000, RewardRarity.Contractor)]
    [InlineData(1499999, RewardRarity.Contractor)]
    [InlineData(1500000, RewardRarity.Restricted)]
    [InlineData(2399999, RewardRarity.Restricted)]
    [InlineData(2400000, RewardRarity.BlackLabel)]
    public void New_bands_do_not_reinterpret_historical_prizes(long value, RewardRarity expected)
    {
        Assert.Equal(expected, ShipmentEconomy.Grade("kit.shipment-v1.compact-v2", value));
        Assert.Equal(RewardRarity.Restricted, ShipmentEconomy.Grade("kit.shipment-v1.compact-v1", 1_000_000));
        Assert.Equal(RewardRarity.Restricted, ShipmentEconomy.Grade("kit.shipment-v1", 1_000_000));
        Assert.Equal(RewardRarity.BlackLabel, ShipmentEconomy.Grade("kit", 1_000_000));
        Assert.Equal("kit", ShipmentEconomy.BaseId("kit.shipment-v1.compact-v2"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Normal_claims_have_rare_gold_and_do_not_revolve_around_the_same_endgame_filler(bool nativeOnly)
    {
        foreach (var view in Views(nativeOnly))
        {
            var price = ManifestCatalogEconomy.CalculateAutomaticPrices(view).CasePrice;
            Assert.InRange(price, 750_000, 1_300_000);
            var claims = new ManifestEconomyAnalysis(view).OpeningClaims(price);
            decimal Chance(Func<OpeningClaimProbability, bool> test) => claims.Where(test).Sum(c => c.Probability);
            Assert.InRange(claims.Sum(c => c.Probability), .999999m, 1.000001m);
            Assert.InRange(Chance(c => c.Lot.Evaluation.Grade == RewardRarity.BlackLabel), 0, .05m);
            Assert.InRange(Chance(c => c.Lot.Evaluation.Grade is RewardRarity.Restricted or RewardRarity.BlackLabel), 0, .55m);
            foreach (var id in new[] { "5e4abb5086f77406975c9342", "628e4e576d783146b124c64d", "601948682627df266209af05" })
                Assert.InRange(Chance(c => c.Lot.Forest.Nodes.Any(n => n.TemplateId == id)), 0, .25m);
            // Also catch repetitive utility padding, not just iconic endgame items.
            foreach (var (id, cap) in new[] {
                ("5aafbcd986f7745e590fff23", .25m), // medicine case
                ("619cbf7d23893217ec30b689", .25m), // injector case
                ("5d02797c86f774203f38e30a", .35m), // Surv12
                ("5d02778e86f774203e7dedbe", .45m), // CMS
                ("591094e086f7747caa7bb2ef", .40m) }) // armor repair kit
                Assert.True(Chance(c => c.Lot.Forest.Roots.Any(n => n.TemplateId == id)) <= cap,
                    $"{CaseContracts.Name(view.CaseTemplateId)}: repeated {id} exceeds {cap:P0} of reference-optimal claims.");
            Assert.Contains(claims, c => ManifestOpeningPool.IsChase(c.Lot) && c.Lot.Evaluation.UseValue >= (price + ManifestCatalogEconomy.OpeningKeyAllowance) * 3m);
            Assert.InRange(Chance(c => ManifestOpeningPool.IsChase(c.Lot)), .000001m, .0075m);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Multi_wager_paths_are_reachable_and_never_cross_into_historical_or_chase_rewards(bool nativeOnly)
    {
        var selector = new ManifestCatalogSelector();
        foreach (var view in Views(nativeOnly))
        {
            int Depth(ResolvedCargoLot lot, int stage)
            {
                if (stage > 3 || lot.Evaluation.Grade == RewardRarity.BlackLabel) return 0;
                var candidates = selector.CreateRelayCandidatesForStage(view,
                    new ManifestEntitlementSnapshot(lot.Evaluation.Grade, lot.Identity, lot.Forest, lot.Fingerprint), stage);
                if (candidates.Count == 0) return 0;
                Assert.Contains(candidates, c => c.TargetResult == ManifestRelayResult.Sidegrade);
                Assert.Contains(candidates, c => c.TargetResult == ManifestRelayResult.Upgrade);
                var resolved = candidates.Select(c => (c, lot: view.ResolveExact(c.Rarity, c.Identity, c.Forest, c.Fingerprint)!)).ToArray();
                Assert.All(resolved, pair =>
                {
                    Assert.Equal(3, ShipmentEconomy.Generation(pair.lot.Identity.LotId));
                    Assert.False(ManifestOpeningPool.IsChase(pair.lot));
                });
                return 1 + resolved.Where(pair => pair.c.TargetResult == ManifestRelayResult.Upgrade)
                    .Select(pair => Depth(pair.lot, stage + 1)).DefaultIfEmpty(0).Max();
            }
            Assert.InRange(view.FreshOpeningLots.Max(l => Depth(l, 1)), 2, 3);
        }
    }

    private static IEnumerable<CargoCatalogSnapshot> Views(bool nativeOnly)
    {
        var mixed = CuratedRewardIntegrationTests.ReadCatalog(nativeOnly);
        return CaseContracts.Templates.Where(t => t != CaseContracts.CashCache)
            .Select(t => t == ModConstants.CaseTemplateId ? mixed : CaseCatalogs.ForCase(mixed, t))
            .Where(view => view.OpeningEnabled);
    }
}
