using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using Xunit;

namespace ContrabandCases.Tests.Relay;

public sealed class RelayRulesTests
{
    [Theory]
    [InlineData(1, 0.00, RelayOutcome.RarityUpgrade)]
    [InlineData(1, 0.549999, RelayOutcome.RarityUpgrade)]
    [InlineData(1, 0.55, RelayOutcome.SameRaritySidegrade)]
    [InlineData(1, 0.849999, RelayOutcome.SameRaritySidegrade)]
    [InlineData(1, 0.85, RelayOutcome.Confiscated)]
    [InlineData(2, 0.449999, RelayOutcome.RarityUpgrade)]
    [InlineData(2, 0.45, RelayOutcome.SameRaritySidegrade)]
    [InlineData(2, 0.70, RelayOutcome.Confiscated)]
    [InlineData(3, 0.349999, RelayOutcome.RarityUpgrade)]
    [InlineData(3, 0.35, RelayOutcome.SameRaritySidegrade)]
    [InlineData(3, 0.55, RelayOutcome.Confiscated)]
    public void Published_ladder_uses_exact_stage_boundaries(int stage, double unitValue, RelayOutcome expected)
    {
        Assert.Equal(expected, RelayRules.SelectOutcome(stage, recoveryMeter: 0, unitValue));
    }

    [Theory]
    [InlineData(1, 55, 30, 15)]
    [InlineData(2, 45, 25, 30)]
    [InlineData(3, 35, 20, 45)]
    public void Published_odds_are_stable(int stage, int upgrade, int sidegrade, int confiscate)
    {
        var odds = RelayRules.GetOdds(stage);

        Assert.Equal(upgrade, odds.UpgradePercent);
        Assert.Equal(sidegrade, odds.SidegradePercent);
        Assert.Equal(confiscate, odds.ConfiscatePercent);
    }

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(1, 2, 3)]
    [InlineData(2, 0, 2)]
    [InlineData(2, 2, 3)]
    [InlineData(3, 0, 3)]
    public void Confiscation_charges_meter_by_failed_stage(int stage, int before, int after)
    {
        Assert.Equal(after, RelayRules.MeterAfterConfiscation(before, stage));
    }

    [Theory]
    [InlineData(1, 0.999999)]
    [InlineData(2, 0.999999)]
    [InlineData(3, 0.999999)]
    public void Full_meter_guarantees_upgrade_at_every_eligible_stage(int stage, double unitValue)
    {
        Assert.Equal(RelayOutcome.RarityUpgrade, RelayRules.SelectOutcome(stage, RelayRules.MaximumRecoveryMeter, unitValue));
    }

    [Theory]
    [InlineData(RewardRarity.ScavGrade, RewardRarity.Uncommon)]
    [InlineData(RewardRarity.Uncommon, RewardRarity.Contractor)]
    [InlineData(RewardRarity.Contractor, RewardRarity.Restricted)]
    [InlineData(RewardRarity.Restricted, RewardRarity.BlackLabel)]
    public void Upgrade_means_exactly_one_rarity_higher(RewardRarity current, RewardRarity expected)
    {
        Assert.Equal(expected, RelayRules.GetUpgradeRarity(current));
    }

    [Fact]
    public void Black_label_and_invalid_stages_are_terminal()
    {
        Assert.False(RelayRules.CanRelay(RewardRarity.BlackLabel, 1));
        Assert.False(RelayRules.CanRelay(RewardRarity.Restricted, 4));
        Assert.Throws<InvalidOperationException>(() => RelayRules.GetUpgradeRarity(RewardRarity.BlackLabel));
        Assert.Throws<ArgumentOutOfRangeException>(() => RelayRules.GetOdds(0));
    }
}
