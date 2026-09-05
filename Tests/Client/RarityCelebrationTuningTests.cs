using ContrabandCases.Client.Opening;
using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class RarityCelebrationTuningTests
{
    [Fact]
    public void Common_gets_the_unscaled_baseline_pulse()
    {
        Assert.Equal(1d, RarityCelebrationTuning.PulseHoldMultiplier(RewardRarity.ScavGrade));
        Assert.Equal(1d, RarityCelebrationTuning.PulseScaleMultiplier(RewardRarity.ScavGrade));
        Assert.Equal(1d, RarityCelebrationTuning.PulseFlashMultiplier(RewardRarity.ScavGrade));
    }

    [Theory]
    [InlineData(RewardRarity.ScavGrade)]
    [InlineData(RewardRarity.Uncommon)]
    [InlineData(RewardRarity.Contractor)]
    [InlineData(RewardRarity.Restricted)]
    [InlineData(RewardRarity.BlackLabel)]
    public void Every_multiplier_is_at_least_the_unscaled_baseline(RewardRarity grade)
    {
        Assert.True(RarityCelebrationTuning.PulseHoldMultiplier(grade) >= 1d);
        Assert.True(RarityCelebrationTuning.PulseScaleMultiplier(grade) >= 1d);
        Assert.True(RarityCelebrationTuning.PulseFlashMultiplier(grade) >= 1d);
    }

    [Fact]
    public void Pulse_multipliers_scale_monotonically_with_rarity()
    {
        var grades = new[]
        {
            RewardRarity.ScavGrade,
            RewardRarity.Uncommon,
            RewardRarity.Contractor,
            RewardRarity.Restricted,
            RewardRarity.BlackLabel
        };

        for (var i = 1; i < grades.Length; i++)
        {
            var previous = grades[i - 1];
            var current = grades[i];
            Assert.True(
                RarityCelebrationTuning.PulseHoldMultiplier(current) > RarityCelebrationTuning.PulseHoldMultiplier(previous),
                $"Hold multiplier did not increase from {previous} to {current}.");
            Assert.True(
                RarityCelebrationTuning.PulseScaleMultiplier(current) > RarityCelebrationTuning.PulseScaleMultiplier(previous),
                $"Scale multiplier did not increase from {previous} to {current}.");
            Assert.True(
                RarityCelebrationTuning.PulseFlashMultiplier(current) > RarityCelebrationTuning.PulseFlashMultiplier(previous),
                $"Flash multiplier did not increase from {previous} to {current}.");
        }
    }

    [Theory]
    [InlineData(RewardRarity.ScavGrade, false)]
    [InlineData(RewardRarity.Uncommon, false)]
    [InlineData(RewardRarity.Contractor, false)]
    [InlineData(RewardRarity.Restricted, false)]
    [InlineData(RewardRarity.BlackLabel, true)]
    public void Only_BlackLabel_bursts_confetti(RewardRarity grade, bool expected)
    {
        Assert.Equal(expected, RarityCelebrationTuning.ShouldBurstConfetti(grade));
    }

    [Theory]
    [InlineData(RewardRarity.ScavGrade, false)]
    [InlineData(RewardRarity.Uncommon, false)]
    [InlineData(RewardRarity.Contractor, false)]
    [InlineData(RewardRarity.Restricted, false)]
    [InlineData(RewardRarity.BlackLabel, true)]
    public void Only_BlackLabel_shimmers(RewardRarity grade, bool expected)
    {
        Assert.Equal(expected, RarityCelebrationTuning.ShouldShimmer(grade));
    }

    [Fact]
    public void Confetti_and_shimmer_gating_agree()
    {
        // Both flourishes are reserved for the same tier -- if that policy
        // ever diverges it should be a deliberate change to this test, not
        // an accidental drift between the two gates.
        foreach (var grade in new[]
                 {
                     RewardRarity.ScavGrade,
                     RewardRarity.Uncommon,
                     RewardRarity.Contractor,
                     RewardRarity.Restricted,
                     RewardRarity.BlackLabel
                 })
        {
            Assert.Equal(
                RarityCelebrationTuning.ShouldBurstConfetti(grade),
                RarityCelebrationTuning.ShouldShimmer(grade));
        }
    }
}
