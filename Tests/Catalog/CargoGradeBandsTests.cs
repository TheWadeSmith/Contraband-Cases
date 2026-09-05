using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Catalog;

public sealed class CargoGradeBandsTests
{
    [Theory]
    [InlineData(1, RewardRarity.ScavGrade)]
    [InlineData(CargoGradeBands.UncommonMinimum - 1, RewardRarity.ScavGrade)]
    [InlineData(CargoGradeBands.UncommonMinimum, RewardRarity.Uncommon)]
    [InlineData(CargoGradeBands.RareMinimum - 1, RewardRarity.Uncommon)]
    [InlineData(CargoGradeBands.RareMinimum, RewardRarity.Contractor)]
    [InlineData(CargoGradeBands.EpicMinimum - 1, RewardRarity.Contractor)]
    [InlineData(CargoGradeBands.EpicMinimum, RewardRarity.Restricted)]
    [InlineData(CargoGradeBands.LegendaryMinimum - 1, RewardRarity.Restricted)]
    [InlineData(CargoGradeBands.LegendaryMinimum, RewardRarity.BlackLabel)]
    public void Assigns_every_value_to_exactly_one_player_facing_tier(
        long useValue,
        RewardRarity expected)
    {
        Assert.Equal(expected, CargoGradeBands.Assign(useValue));
    }

    [Theory]
    [InlineData(RewardRarity.ScavGrade, "Common", "gray")]
    [InlineData(RewardRarity.Uncommon, "Uncommon", "green")]
    [InlineData(RewardRarity.Contractor, "Rare", "blue")]
    [InlineData(RewardRarity.Restricted, "Epic", "violet")]
    [InlineData(RewardRarity.BlackLabel, "Legendary", "amber")]
    public void Keeps_wire_values_but_exposes_standard_rarity_labels(
        RewardRarity rarity,
        string displayName,
        string displayColor)
    {
        var info = RewardRarities.GetInfo(rarity);

        Assert.Equal(displayName, info.DisplayName);
        Assert.Equal(displayColor, info.DisplayColor);
    }
}
