using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class CaseCatalogTests
{
    [Theory]
    [InlineData(500_000, true)]
    [InlineData(500_001, true)]
    [InlineData(1_873_660, true)]
    public void OperationsRetainsValuableRewardsWithoutRewritingMixedOrSavedRewards(long value, bool inOperations)
    {
        var jackpot = Lot("wtt-contentbackport.field-resupply", "field-supply", "wtt-field-resupply", value: value);
        var source = Snapshot(new[]
        {
            Lot("core", "arsenal", "rifle"), Lot("core", "operator", "recon"),
            Lot("core", "field-supply", "medical"), jackpot
        });

        var operations = CaseCatalogs.ForCase(source, CaseContracts.Operations);

        Assert.Equal(inOperations, operations.Lots.Contains(jackpot));
        Assert.True(operations.OpeningEnabled);
        Assert.Contains(jackpot, CaseCatalogs.ForCase(source, ModConstants.CaseTemplateId).Lots);
        Assert.DoesNotContain(jackpot, CaseCatalogs.ForCase(source, CaseContracts.BlackSite).Lots);
        Assert.Same(jackpot, source.ResolveExact(jackpot.Evaluation.Grade,
            jackpot.Identity, jackpot.Forest, jackpot.Fingerprint));
        Assert.All(operations.Lots, lot => Assert.Contains(source.Lots, original => ReferenceEquals(original, lot)));
    }

    [Fact]
    public void ChaseOnlyCategoryPublishesItsRealOddsInsteadOfInventingFiller()
    {
        var source = Snapshot(new[]
        {
            Lot("core", "arsenal", "rifle"), Lot("core", "operator", "recon"),
            Lot("test", "field-supply", "jackpot", value: 1_000_000)
        });
        var operations = CaseCatalogs.ForCase(source, CaseContracts.Operations);
        Assert.True(operations.OpeningEnabled);
        Assert.NotNull(operations.CasePrice);
        Assert.Equal(3, operations.Lots.Count);
        Assert.Equal("100.00%", ManifestOpeningOdds.Create(operations).Families
            .Single(family => family.FamilyId == "field-supply").Lots.Single().ConditionalPercent);
        Assert.True(source.OpeningEnabled);
    }

    [Fact]
    public void ValuableCollectiblesAndSpecialistRewardsStayInTheirThemes()
    {
        var card = Lot("krackasourus.pokemon-cards", "field-supply", "pokemon-cards", collection: true, value: 1_000_000);
        var specialist = Lot("vault", "arsenal", "vault", value: 1_000_000);
        var source = Snapshot(new[] { card, specialist });
        Assert.Contains(card, CaseCatalogs.ForCase(source, CaseContracts.Relics).Lots);
        Assert.Contains(specialist, CaseCatalogs.ForCase(source, CaseContracts.BlackSite).Lots);
    }

    [Fact]
    public void RelicsPreservesAllThreeCardSeriesWithoutUnrelatedFiller()
    {
        var cards = new[] { "anime", "pokemon", "yugioh" }.Select(series =>
            Lot($"krackasourus.{series}-cards", "field-supply", $"{series}-cards", collection: true)).ToArray();
        var ordinary = Lot("core", "arsenal", "rifle");
        var source = Snapshot(cards.Append(ordinary));
        var relics = CaseCatalogs.ForCase(source, CaseContracts.Relics);

        Assert.True(relics.OpeningEnabled);
        Assert.Equal(cards, relics.Lots);
        Assert.NotEqual(source.SnapshotId, relics.SnapshotId);
        Assert.True(relics.CasePrice > 0);
        Assert.Equal(0, relics.CasePrice % 1000);
        var offers = new ManifestCatalogSelector(() => 0).CreateOffers(relics);
        Assert.Equal(3, offers.Select(o => o.Identity.TrackId).Distinct().Count());
        Assert.Single(offers.Select(o => o.Identity.FamilyId).Distinct());
        Assert.All(offers, offer => Assert.Contains(cards, card => ReferenceEquals(card.Identity, offer.Identity)));
        Assert.Equal(3, ManifestOpeningOdds.Create(relics).Families.Count);
        Assert.Same(source, CaseCatalogs.ForCase(source, ModConstants.CaseTemplateId));
    }

    [Fact]
    public void IncompleteRelicsPoolIsUnavailableInsteadOfFallingBackToMixedLoot()
    {
        var source = Snapshot(new[]
        {
            Lot("krackasourus.pokemon-cards", "field-supply", "pokemon-cards", collection: true),
            Lot("core", "arsenal", "rifle"), Lot("core", "operator", "recon"),
            Lot("core", "field-supply", "medical")
        });
        var relics = CaseCatalogs.ForCase(source, CaseContracts.Relics);
        Assert.True(source.OpeningEnabled);
        Assert.False(relics.OpeningEnabled);
        Assert.Single(relics.Lots);
        Assert.Null(relics.CasePrice);
        Assert.Throws<InvalidOperationException>(() => new ManifestCatalogSelector().CreateOffers(relics));
    }

    [Fact]
    public void OperationsAndBlackSiteHaveDistinctRealPools()
    {
        var source = Snapshot(new[]
        {
            Lot("core", "arsenal", "rifle"), Lot("core", "operator", "recon"),
            Lot("core", "field-supply", "medical"), Lot("vault", "arsenal", "vault"),
            Lot("core", "operator", "night"), Lot("core", "field-supply", "ordnance")
        });
        var operations = CaseCatalogs.ForCase(source, CaseContracts.Operations);
        var blackSite = CaseCatalogs.ForCase(source, CaseContracts.BlackSite);
        Assert.True(operations.OpeningEnabled);
        Assert.True(blackSite.OpeningEnabled);
        Assert.Empty(operations.Lots.Intersect(blackSite.Lots));
        Assert.Equal(source.Lots.Count, operations.Lots.Count + blackSite.Lots.Count);
        Assert.NotEqual(operations.SnapshotId, blackSite.SnapshotId);
        Assert.Equal(operations.SnapshotId, CaseCatalogs.ForCase(source, CaseContracts.Operations).SnapshotId);
    }

    internal static CargoCatalogSnapshot Snapshot(IEnumerable<ResolvedCargoLot> lots)
    {
        var values = lots.ToArray();
        return new CargoCatalogSnapshot(new string('a', 64), values, [],
            values.Select(l => l.Identity.ProviderId).Distinct().ToDictionary(id => id, _ => 1d));
    }

    internal static ResolvedCargoLot Lot(string provider, string family, string track, bool collection = false, long value = 100_000)
    {
        var template = "template-" + track;
        var definition = new CargoLotDefinition(provider, "1.0.0", track, track, "Test purpose",
            new FamilyId(family), new TrackId(track), template, 1d,
            collection ? new Collection("album", track) : new RaidRole("testing"),
            [new TemplateLine(template, 1, 1)]);
        var forest = RewardForest.Create([new RewardForestNode("root", "root", template, null, null, null, 1)]);
        var fingerprint = RewardForestFingerprintV2.Compute(provider, track, forest);
        return new ResolvedCargoLot(definition, forest, fingerprint,
            CargoLotIdentitySnapshot.Capture(definition, fingerprint),
            new CargoLotEvaluation(value, value, 1, CargoGradeBands.Assign(value)));
    }
}
