using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using System.Text.Json;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class ManifestEconomyAnalysisTests
{
    [Fact]
    public void Relay_coverage_summary_excludes_recovery_only_reward_generations()
    {
        var lots = new[] { "arsenal", "operator", "field-supply" }.SelectMany(family =>
            new[] { "a", "b", "c" }.SelectMany(name =>
            {
                var id = family + "-" + name;
                var oldGrade = name == "c" ? RewardRarity.BlackLabel : RewardRarity.Restricted;
                var oldValue = name == "c" ? 200 : 100;
                return new[]
                {
                    Lot(id, family, oldValue, oldGrade),
                    Lot(id + ".shipment-v1", family, oldValue, oldGrade),
                    Lot(id + ".shipment-v1.compact-v1", family, 100, RewardRarity.Restricted)
                };
            }));
        var report = JsonSerializer.SerializeToElement(ManifestEconomyReport.Create(Catalog(lots)));
        var rows = report.GetProperty("Lots").EnumerateArray().ToArray();

        Assert.Contains(rows, row => !row.GetProperty("AvailableForFreshOpening").GetBoolean() &&
            row.GetProperty("RelayEligible").GetBoolean());
        Assert.All(rows.Where(row => row.GetProperty("AvailableForFreshOpening").GetBoolean()),
            row => Assert.False(row.GetProperty("RelayEligible").GetBoolean()));
        Assert.Equal(0, report.GetProperty("RelayEligibleNonLegendaryPercent").GetDecimal());
        Assert.All(report.GetProperty("ThemedCases").EnumerateArray(),
            view => Assert.Equal(0, view.GetProperty("RelayEligibleNonLegendaryPercent").GetDecimal()));
    }

    [Fact]
    public void First_offer_policy_does_not_receive_the_optimal_discard_advantage()
    {
        var analysis = new ManifestEconomyAnalysis(Catalog([
            Lot("a", "arsenal", 30_000), Lot("b", "operator", 60_000),
            Lot("c", "field-supply", 90_000)
        ]));

        Assert.Equal(60_000m, decimal.Round(analysis.SummarizeOpening(50_000,
            OpeningChoicePolicy.KeepFirst).ExpectedUseValue, 8));
        Assert.Equal(90_000m, decimal.Round(analysis.SummarizeOpening(50_000,
            OpeningChoicePolicy.KeepAtOpeningCost).ExpectedUseValue, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => analysis.SummarizeOpening(50_000,
            (OpeningChoicePolicy)99));
    }

    [Fact]
    public void Cost_threshold_policy_includes_the_key_and_can_end_below_cost()
    {
        var analysis = new ManifestEconomyAnalysis(Catalog([
            Lot("a", "arsenal", 30_000), Lot("b", "operator", 60_000),
            Lot("c", "field-supply", 90_000)
        ]));
        // No offer meets 80k + 25k. Every sequence reaches its random third lot.
        var summary = analysis.SummarizeOpening(80_000, OpeningChoicePolicy.KeepAtOpeningCost);
        Assert.Equal(60_000m, decimal.Round(summary.ExpectedUseValue, 8));
        Assert.Equal(100m, summary.KeyCostScenarios[1].BelowTotalCostPercent);
    }

    [Fact]
    public void Opening_summary_matches_the_non_clairvoyant_choice_policy()
    {
        var analysis = new ManifestEconomyAnalysis(Catalog([
            Lot("a-low", "arsenal", 10), Lot("a-high", "arsenal", 100),
            Lot("b", "operator", 40), Lot("c", "field-supply", 50)
        ]));

        var summary = analysis.SummarizeOpening(60);

        Assert.Equal(67.5m, decimal.Round(summary.ExpectedUseValue, 8));
        Assert.Equal(10, summary.MinimumUseValue);
        Assert.Equal(10, summary.P10UseValue);
        Assert.Equal(50, summary.MedianUseValue);
        Assert.Equal(100, summary.P90UseValue);
        Assert.Equal(100, summary.MaximumUseValue);
        Assert.Equal(50m, summary.KeyCostScenarios[0].BelowTotalCostPercent);
        Assert.Equal(7.5m, summary.KeyCostScenarios[0].ExpectedUseValueMinusTotalCost);
        Assert.Equal(50m, summary.KeyCostScenarios[0].LossBelow90PercentOfCost);
        Assert.Equal(0m, summary.KeyCostScenarios[0].NearBreakEvenWithin10Percent);
        Assert.Equal(50m, summary.KeyCostScenarios[0].WinAbove110PercentOfCost);
        Assert.All(summary.KeyCostScenarios, row =>
            Assert.Equal(60 + row.KeyOpportunityCost, row.TotalCost));
    }

    [Fact]
    public void Opening_summary_keeps_ties_and_does_not_count_equal_value_as_a_loss()
    {
        var analysis = new ManifestEconomyAnalysis(Catalog([
            Lot("a", "arsenal", 100_000), Lot("b", "operator", 100_000),
            Lot("c", "field-supply", 100_000)
        ]));

        var summary = analysis.SummarizeOpening(100_000);

        Assert.Equal(100_000, summary.MinimumUseValue);
        Assert.Equal(100_000, summary.MaximumUseValue);
        Assert.Equal(0, summary.KeyCostScenarios[0].BelowTotalCostPercent);
        Assert.Equal(100m, summary.KeyCostScenarios[0].NearBreakEvenWithin10Percent);
        Assert.Equal(100, summary.KeyCostScenarios[1].BelowTotalCostPercent);
        Assert.Equal(-25_000, summary.KeyCostScenarios[1].ExpectedUseValueMinusTotalCost);
        Assert.Throws<ArgumentOutOfRangeException>(() => analysis.SummarizeOpening(0));
    }

    [Theory]
    [InlineData(1e-40)]
    [InlineData(1e40)]
    public void Diagnostic_report_handles_legal_extreme_weights(double weight)
    {
        var lots = new[] { "arsenal", "operator", "field-supply" }.SelectMany(family => new[]
        {
            Lot(family + "-a", family, 100, RewardRarity.Restricted, weight),
            Lot(family + "-b", family, 100, RewardRarity.Restricted, weight),
            Lot(family + "-c", family, 200, RewardRarity.BlackLabel, weight)
        });
        var catalog = Catalog(lots);
        Assert.InRange(new ManifestEconomyAnalysis(catalog).OptimalKeepDiscardUseValue(), 100m, 200m);
        Assert.NotNull(ManifestEconomyReport.Create(catalog));
        Assert.InRange(new ManifestEconomyAnalysis(catalog).SummarizeOpening(100).MedianUseValue, 100, 200);
        Assert.Equal(1m, new ExactProbability(1, 1).ApproximateDecimal);
    }

    [Fact]
    public void Four_hidden_relic_tracks_match_exhaustive_non_clairvoyant_play()
    {
        long[] values = [10, 40, 50, 100];
        var source = CaseCatalogTests.Snapshot(values.Select((value, i) =>
            CaseCatalogTests.Lot("cards-" + i, "field-supply", "track-" + i, collection: true, value: value)));
        var view = CaseCatalogs.ForCase(source, CaseContracts.Relics);
        var summary = new ManifestEconomyAnalysis(view).SummarizeOpening(70);
        var realized = new List<long>();
        var indices = Enumerable.Range(0, 4).ToArray();
        foreach (var first in indices)
        {
            var seconds = indices.Where(i => i != first).ToArray();
            var continueFirst = seconds.Average(second => Math.Max((decimal)values[second],
                indices.Where(i => i != first && i != second).Average(i => (decimal)values[i])));
            foreach (var second in seconds)
            {
                var thirds = indices.Where(i => i != first && i != second).ToArray();
                var continueSecond = thirds.Average(i => (decimal)values[i]);
                foreach (var third in thirds)
                    realized.Add(values[first] >= continueFirst ? values[first] :
                        values[second] >= continueSecond ? values[second] : values[third]);
            }
        }
        realized.Sort();
        Assert.Equal(24, realized.Count);
        Assert.Equal(decimal.Round(realized.Average(v => (decimal)v), 8), decimal.Round(summary.ExpectedUseValue, 8));
        Assert.Equal(realized[2], summary.P10UseValue);
        Assert.Equal(realized[11], summary.MedianUseValue);
        Assert.Equal(realized[21], summary.P90UseValue);
        Assert.Equal(decimal.Round(100m * realized.Count(v => v < 70) / 24, 2), summary.KeyCostScenarios[0].BelowTotalCostPercent);
    }
    [Fact]
    public void Keep_discard_uses_visible_clues_not_knowledge_of_future_lots()
    {
        var analysis = new ManifestEconomyAnalysis(Catalog([
            Lot("a-low", "arsenal", 10), Lot("a-high", "arsenal", 100),
            Lot("b", "operator", 40), Lot("c", "field-supply", 50)
        ]));
        // Backward induction across the six orders gives
        // (75 + 75 + 75 + 55 + 70 + 55) / 6, not the clairvoyant 75.
        Assert.Equal(67.5m, decimal.Round(analysis.OptimalKeepDiscardUseValue(), 8));
    }

    [Fact]
    public void Every_opening_key_is_costed_even_when_relay_is_unavailable()
    {
        var analysis = new ManifestEconomyAnalysis(Catalog([
            Lot("a", "arsenal", 100), Lot("b", "operator", 100), Lot("c", "field-supply", 100)
        ]));
        var scenarios = analysis.Analyze(20, 25);
        Assert.All(scenarios, row =>
        {
            Assert.Equal(1500m, decimal.Round(row.ExpectedTotalUseValueAfterKeysBeforeCasePrices, 4));
            Assert.Equal(75m, decimal.Round(row.BreakEvenCasePrice, 4));
        });
    }

    [Fact]
    public void Persistent_favor_has_future_value_and_key_scarcity_reduces_returns()
    {
        var lots = new[] { "arsenal", "operator", "field-supply" }.SelectMany(family => new[]
        {
            Lot(family + "-a", family, 100, RewardRarity.ScavGrade),
            Lot(family + "-b", family, 100, RewardRarity.ScavGrade),
            Lot(family + "-c", family, 200, RewardRarity.Uncommon),
            Lot(family + "-d", family, 200, RewardRarity.Uncommon),
            Lot(family + "-e", family, 300, RewardRarity.Contractor),
            Lot(family + "-f", family, 300, RewardRarity.Contractor),
            Lot(family + "-g", family, 400, RewardRarity.Restricted),
            Lot(family + "-h", family, 400, RewardRarity.Restricted),
            Lot(family + "-i", family, 500, RewardRarity.BlackLabel)
        });
        var analysis = new ManifestEconomyAnalysis(Catalog(lots));
        var once = analysis.Analyze(1, 0);
        var campaign = analysis.Analyze(20, 0);
        var scarce = analysis.Analyze(20, 65);
        Assert.True(once[3].BreakEvenCasePrice > once[0].BreakEvenCasePrice);
        Assert.True(campaign[0].BreakEvenCasePrice > once[0].BreakEvenCasePrice);
        Assert.True(campaign[0].BreakEvenCasePrice > scarce[0].BreakEvenCasePrice);
    }

    private static CargoCatalogSnapshot Catalog(IEnumerable<ResolvedCargoLot> lots) =>
        new(new string('a', 64), lots, [], new Dictionary<string, double> { ["test"] = 1 });

    private static ResolvedCargoLot Lot(string id, string family, long value, RewardRarity grade = RewardRarity.Contractor, double weight = 1)
    {
        var definition = new CargoLotDefinition("test", "1", id, id, "Test lot", new FamilyId(family),
            new TrackId(family), id, weight, new RaidRole("test"), [new TemplateLine(id, 1, 1)]);
        var forest = RewardForest.Create([new RewardForestNode("root", "root", id, null, null, null, 1)]);
        var fingerprint = RewardForestFingerprintV2.Compute("test", id, forest);
        return new ResolvedCargoLot(definition, forest, fingerprint, CargoLotIdentitySnapshot.Capture(definition, fingerprint),
            new CargoLotEvaluation(value, value, 1, grade));
    }
}
