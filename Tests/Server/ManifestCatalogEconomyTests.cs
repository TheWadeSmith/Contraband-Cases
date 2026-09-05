using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Configuration;
using ContrabandCases.Server.Content;
using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class ManifestCatalogEconomyTests
{
    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 150)]
    public void Pricing_accepts_nonempty_recovery_catalogues_that_cannot_open_new_cases(
        int familyCount,
        int expectedValue)
    {
        var lots = Enumerable.Range(0, familyCount)
            .Select(index => Lot(
                $"provider-{index}",
                $"lot-{index}",
                $"family-{index}",
                100L * (index + 1)))
            .ToArray();
        var catalog = Snapshot(lots);

        var actual = ManifestCatalogEconomy.CalculateExpectedHandbookValue(catalog);

        Assert.False(catalog.OpeningEnabled);
        Assert.NotNull(catalog.OpeningDisabledReason);
        Assert.Equal(expectedValue, actual);
    }

    [Fact]
    public void Empty_catalogue_fails_closed_even_when_ticket_prices_are_fixed()
    {
        var catalog = Snapshot([]);
        var fixedConfig = ModConfig.Parse("""
            { "fixedCasePrice": 70000 }
            """);

        Assert.False(catalog.OpeningEnabled);
        Assert.Throws<CargoCatalogValidationException>(() =>
            ManifestCatalogEconomy.CalculateExpectedHandbookValue(catalog));
        Assert.Throws<CargoCatalogValidationException>(() =>
            ContrabandContentDefinitions.CalculatePrices(catalog, ModConfig.Parse("{}")));
        Assert.Throws<CargoCatalogValidationException>(() =>
            ContrabandContentDefinitions.CalculatePrices(catalog, fixedConfig));
    }

    [Fact]
    public void Fixed_ticket_prices_override_a_nonempty_recovery_only_catalogue()
    {
        var catalog = Snapshot([
            Lot("provider-a", "lot-a", "family-a", 1_000_000)
        ]);
        var config = ModConfig.Parse("""
            { "fixedCasePrice": 70000 }
            """);

        var prices = ContrabandContentDefinitions.CalculatePrices(catalog, config);

        Assert.False(catalog.OpeningEnabled);
        Assert.Equal(70_000L, prices.CasePrice);
    }

    [Fact]
    public void Family_selection_is_uniform_instead_of_lot_count_weighted()
    {
        var lots = new List<ResolvedCargoLot>
        {
            Lot("provider-a", "lot-a", "family-a", 100),
            Lot("provider-c", "lot-c", "family-c", 100)
        };
        lots.AddRange(Enumerable.Range(0, 10).Select(index =>
            Lot("provider-b", $"lot-b-{index}", "family-b", 1_000)));

        var expectedValue = ManifestCatalogEconomy.CalculateExpectedHandbookValue(Snapshot(lots));

        Assert.Equal(400m, expectedValue);
    }

    [Fact]
    public void Provider_weight_applies_only_inside_its_family()
    {
        var lots = new[]
        {
            Lot("provider-a1", "lot-a1", "family-a", 100),
            Lot("provider-a2", "lot-a2", "family-a", 700),
            Lot("provider-b", "lot-b", "family-b", 100),
            Lot("provider-c", "lot-c", "family-c", 100)
        };
        var weights = ProviderWeights(
            lots,
            ("provider-a1", 1d),
            ("provider-a2", 2d),
            ("provider-b", 10d));

        var expectedValue = ManifestCatalogEconomy.CalculateExpectedHandbookValue(
            Snapshot(lots, weights));

        Assert.Equal(decimal.Round(700m / 3m, 12), decimal.Round(expectedValue, 12));
    }

    [Fact]
    public void Lot_weight_applies_only_inside_its_provider_and_family()
    {
        var lots = new[]
        {
            Lot("provider-a", "lot-a1", "family-a", 100, weight: 1d),
            Lot("provider-a", "lot-a2", "family-a", 700, weight: 2d),
            Lot("provider-b", "lot-b", "family-b", 100, weight: 10d),
            Lot("provider-c", "lot-c", "family-c", 100)
        };

        var expectedValue = ManifestCatalogEconomy.CalculateExpectedHandbookValue(Snapshot(lots));

        Assert.Equal(decimal.Round(700m / 3m, 12), decimal.Round(expectedValue, 12));
    }

    [Fact]
    public void Reversing_catalogue_order_does_not_change_expected_value()
    {
        var lots = new[]
        {
            Lot("provider-a", "lot-a1", "family-a", 100, weight: 1d),
            Lot("provider-a", "lot-a2", "family-a", 700, weight: 2d),
            Lot("provider-b", "lot-b", "family-b", 1_200),
            Lot("provider-c", "lot-c", "family-c", 2_400)
        };
        var weights = ProviderWeights(lots, ("provider-a", 3d), ("provider-b", 2d));

        var forward = ManifestCatalogEconomy.CalculateExpectedHandbookValue(Snapshot(lots, weights));
        var reverse = ManifestCatalogEconomy.CalculateExpectedHandbookValue(
            Snapshot(lots.Reverse(), weights));

        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void Missing_provider_weight_fails_closed()
    {
        var lots = new[]
        {
            Lot("provider-a", "lot-a", "family-a", 100),
            Lot("provider-b", "lot-b", "family-b", 100),
            Lot("provider-c", "lot-c", "family-c", 100)
        };
        var incompleteWeights = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["provider-a"] = 1d,
            ["provider-b"] = 1d
        };

        Assert.Throws<CargoCatalogValidationException>(() =>
            ManifestCatalogEconomy.CalculateExpectedHandbookValue(
                Snapshot(lots, incompleteWeights)));
    }

    [Fact]
    public void Automatic_ticket_pricing_uses_finalized_use_values_like_themed_cases()
    {
        var catalog = Snapshot([
            Lot("provider-a", "lot-a", "family-a", 85_000, useValue: 500_000),
            Lot("provider-b", "lot-b", "family-b", 85_000, useValue: 500_000),
            Lot("provider-c", "lot-c", "family-c", 85_000, useValue: 500_000)
        ]);

        var prices = ContrabandContentDefinitions.CalculatePrices(catalog, ModConfig.Parse("{}"));

        Assert.Equal(450_000L, prices.CasePrice);
    }

    [Fact]
    public void Automatic_mixed_price_accounts_for_keep_discard_instead_of_one_random_offer()
    {
        var catalog = Snapshot([
            Lot("provider-a", "lot-a", "arsenal", 10_000),
            Lot("provider-b", "lot-b", "operator", 40_000),
            Lot("provider-c", "lot-c", "field-supply", 50_000)
        ]);

        var prices = ContrabandContentDefinitions.CalculatePrices(catalog, ModConfig.Parse("{}"));

        Assert.Equal(23_000L, prices.CasePrice);
    }

    [Fact]
    public void Key_allowance_never_makes_a_low_value_custom_case_free_or_negative()
    {
        var catalog = Snapshot([
            Lot("a", "a", "arsenal", 100),
            Lot("b", "b", "operator", 100),
            Lot("c", "c", "field-supply", 100)
        ]);

        Assert.Equal(1_000L, ManifestCatalogEconomy.CalculateAutomaticPrices(catalog).CasePrice);
    }

    private static CargoCatalogSnapshot Snapshot(
        IEnumerable<ResolvedCargoLot> lots,
        IReadOnlyDictionary<string, double>? providerWeights = null)
    {
        var snapshot = lots.ToArray();
        return new CargoCatalogSnapshot(
            new string('b', 64),
            snapshot,
            [],
            providerWeights ?? ProviderWeights(snapshot));
    }

    private static IReadOnlyDictionary<string, double> ProviderWeights(
        IEnumerable<ResolvedCargoLot> lots,
        params (string ProviderId, double Weight)[] overrides)
    {
        var weights = lots
            .Select(lot => lot.Identity.ProviderId)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(providerId => providerId, _ => 1d, StringComparer.Ordinal);
        foreach (var (providerId, weight) in overrides)
        {
            weights[providerId] = weight;
        }

        return weights;
    }

    private static ResolvedCargoLot Lot(
        string providerId,
        string lotId,
        string familyId,
        long handbookValue,
        double weight = 1d,
        long? useValue = null)
    {
        var templateId = string.Concat("template-", lotId);
        var definition = new CargoLotDefinition(
            providerId,
            "1.0.0",
            lotId,
            string.Concat("Display ", lotId),
            string.Concat("Purpose ", lotId),
            new FamilyId(familyId),
            new TrackId(string.Concat("track-", lotId)),
            templateId,
            weight,
            new RaidRole("testing"),
            [new TemplateLine(templateId, 1, 1)]);
        var forest = RewardForest.Create(
            [new RewardForestNode("root", "root", templateId, null, null, null, 1)]);
        var fingerprint = RewardForestFingerprintV2.Compute(providerId, lotId, forest);
        var identity = CargoLotIdentitySnapshot.Capture(definition, fingerprint);
        var finalizedUseValue = useValue ?? handbookValue;
        return new ResolvedCargoLot(
            definition,
            forest,
            fingerprint,
            identity,
            new CargoLotEvaluation(
                handbookValue,
                finalizedUseValue,
                1,
                CargoGradeBands.Assign(finalizedUseValue)));
    }
}
