using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Configuration;
using ContrabandCases.Server.Content;
using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class FreshOpeningCompatibilityTests
{
    private static ResolvedCargoLot Retired(string family = "operator") => CaseCatalogTests.Lot(
        "eco-attachment.elite-optics", family, "micro-red-dot-mounts", value: 953);

    [Fact]
    public void Retired_package_still_resolves_exactly_but_is_absent_from_new_offers_and_odds()
    {
        var retired = Retired();
        var catalog = CaseCatalogTests.Snapshot(new[]
        {
            retired, CaseCatalogTests.Lot("core", "arsenal", "rifle"),
            CaseCatalogTests.Lot("core", "operator", "recon"),
            CaseCatalogTests.Lot("core", "field-supply", "medical")
        });

        Assert.Same(retired, catalog.ResolveExact(retired.Evaluation.Grade, retired.Identity,
            retired.Forest, retired.Fingerprint));
        Assert.Equal(4, catalog.Lots.Count);
        Assert.Equal(3, catalog.FreshOpeningLots.Count);
        var offers = new ManifestCatalogSelector(() => 0).CreateOffers(catalog);
        Assert.DoesNotContain(offers, offer => offer.Identity.LotId == retired.Identity.LotId);
        Assert.DoesNotContain(ManifestOpeningOdds.Create(catalog).Families.SelectMany(f => f.Lots),
            row => row.LotId == retired.Identity.LotId);
        Assert.Equal(100_000m, ManifestCatalogEconomy.CalculateExpectedHandbookValue(catalog));
        Assert.Equal(100_000m, decimal.Round(new ManifestEconomyAnalysis(catalog).OptimalKeepDiscardUseValue(), 8));
    }

    [Fact]
    public void Retired_only_family_cannot_enable_an_opening_or_leave_an_empty_draw_pool()
    {
        var catalog = CaseCatalogTests.Snapshot(new[]
        {
            Retired("old-optics-only"), CaseCatalogTests.Lot("core", "arsenal", "rifle"),
            CaseCatalogTests.Lot("core", "field-supply", "medical")
        });
        Assert.False(catalog.OpeningEnabled);
        Assert.Empty(catalog.FamilyAssignments);
        Assert.Throws<InvalidOperationException>(() => new ManifestCatalogSelector().CreateOffers(catalog));
        Assert.True(ManifestCatalogEconomy.CalculateAutomaticPrices(catalog).CasePrice > 0);
    }

    [Fact]
    public void All_retired_catalog_retains_registration_value_and_exact_recovery()
    {
        var retired = Retired();
        var catalog = CaseCatalogTests.Snapshot([retired]);
        Assert.Empty(catalog.FreshOpeningLots);
        Assert.False(catalog.OpeningEnabled);
        Assert.True(ContrabandContentDefinitions.CalculatePrices(catalog, ModConfig.Parse("{}")).CasePrice > 0);
        Assert.Same(retired, catalog.ResolveExact(retired.Evaluation.Grade, retired.Identity,
            retired.Forest, retired.Fingerprint));
    }

    [Fact]
    public void Themed_price_uses_the_same_allowance_exactly_once()
    {
        var catalog = CaseCatalogTests.Snapshot(new[]
        {
            CaseCatalogTests.Lot("core", "arsenal", "rifle", value: 200_000),
            CaseCatalogTests.Lot("core", "operator", "recon", value: 200_000),
            CaseCatalogTests.Lot("core", "field-supply", "medical", value: 200_000)
        });
        var operations = CaseCatalogs.ForCase(catalog, CaseContracts.Operations);
        Assert.Equal(105_000L, operations.CasePrice);
        Assert.Equal(105_000L, ManifestCatalogEconomy.CalculateAutomaticPrices(operations).CasePrice);
        Assert.Equal(150_000L, ContrabandContentDefinitions.CalculatePrices(catalog,
            ModConfig.Parse("""{"fixedCasePrice":150000}""")).CasePrice);
    }
}
