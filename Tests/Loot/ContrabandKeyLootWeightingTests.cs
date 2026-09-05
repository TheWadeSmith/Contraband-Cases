using ContrabandCases.Server.Loot;
using Xunit;

namespace ContrabandCases.Tests.Loot;

public sealed class ContrabandKeyLootWeightingTests
{
    [Theory]
    [InlineData(1000d, 1.5d, 15d)]
    [InlineData(2000d, 1.5d, 30d)]
    [InlineData(100d, 100d, 100d)]
    [InlineData(3d, 50d, 1.5d)]
    public void Computes_weight_as_a_percentage_of_the_existing_pool_sum(
        double existingWeightSum,
        double weightPercent,
        double expectedWeight)
    {
        var weight = ContrabandKeyLootWeighting.ComputeAdditionalWeight(existingWeightSum, weightPercent);

        Assert.Equal(expectedWeight, weight);
    }

    [Theory]
    [InlineData(0d, 1.5d)]
    [InlineData(-5d, 1.5d)]
    [InlineData(double.NaN, 1.5d)]
    [InlineData(double.PositiveInfinity, 1.5d)]
    public void Returns_null_for_a_non_positive_or_non_finite_existing_sum(double existingWeightSum, double weightPercent)
    {
        Assert.Null(ContrabandKeyLootWeighting.ComputeAdditionalWeight(existingWeightSum, weightPercent));
    }

    [Theory]
    [InlineData(1000d, 0d)]
    [InlineData(1000d, -1d)]
    [InlineData(1000d, double.NaN)]
    [InlineData(1000d, double.PositiveInfinity)]
    public void Returns_null_for_a_non_positive_or_non_finite_weight_percent(double existingWeightSum, double weightPercent)
    {
        Assert.Null(ContrabandKeyLootWeighting.ComputeAdditionalWeight(existingWeightSum, weightPercent));
    }
}
