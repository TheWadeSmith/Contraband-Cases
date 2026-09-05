using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using Xunit;

namespace ContrabandCases.Tests.Relay;

public sealed class RelayEconomyAnalyzerTests
{
    [Fact]
    public void Analysis_uses_catalog_values_and_published_odds_without_claiming_base_case_return()
    {
        RelayEconomyReward[] rewards =
        [
            new("low-a", RewardRarity.ScavGrade, 100, 0.4),
            new("low-b", RewardRarity.ScavGrade, 200, 0.2),
            new("uncommon", RewardRarity.Uncommon, 500, 0.2),
            new("uncommon-b", RewardRarity.Uncommon, 500, 0.01),
            new("mid", RewardRarity.Contractor, 500, 0.2),
            new("mid-b", RewardRarity.Contractor, 500, 0.01),
            new("high", RewardRarity.Restricted, 1_000, 0.15),
            new("high-b", RewardRarity.Restricted, 1_000, 0.01),
            new("top", RewardRarity.BlackLabel, 2_000, 0.05)
        ];

        var report = RelayEconomyAnalyzer.Analyze(rewards, keyPrice: 50);
        var lowA = Assert.Single(report.Opportunities, value => value.RewardId == "low-a" && value.Stage == 1);

        // 55% * 500 upgrade + 30% * 200 sidegrade + 15% * 0 confiscation.
        Assert.Equal(335m, lowA.ExpectedOutputValue);
        Assert.Equal(185m, lowA.ExpectedNetValue);
        Assert.Equal(50, report.KeyPrice);
        Assert.False(report.IncludesRecoveryGuarantee);
        Assert.Contains("does not include", report.Disclosure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analysis_rejects_catalogs_without_an_upgrade_or_sidegrade_target()
    {
        RelayEconomyReward[] rewards =
        [
            new("only-low", RewardRarity.ScavGrade, 100, 0.8),
            new("only-mid", RewardRarity.Contractor, 200, 0.1),
            new("only-high", RewardRarity.Restricted, 300, 0.1)
        ];

        Assert.Throws<InvalidOperationException>(() => RelayEconomyAnalyzer.Analyze(rewards, 10));
    }
}
