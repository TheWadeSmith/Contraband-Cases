using ContrabandCases.Shared.Economy;
using Xunit;

namespace ContrabandCases.Tests.Economy;

public sealed class TicketPriceCalculatorTests
{
    [Theory]
    [InlineData(85_000L, 100_000L)]
    [InlineData(85_001L, 101_000L)]
    [InlineData(1L, 1_000L)]
    public void Calculate_rounds_up_to_the_nearest_rounding_unit(long expectedValue, long casePrice)
    {
        var result = TicketPriceCalculator.Calculate(expectedValue, 0.85m, 1_000);

        Assert.Equal(casePrice, result.CasePrice);
    }

    [Fact]
    public void Calculate_applies_only_the_final_thousand_rouble_ceiling()
    {
        var exactBoundary = TicketPriceCalculator.Calculate(850m, 0.85m, 1_000);
        var fractionalExpectedValue = TicketPriceCalculator.Calculate(850.1m, 0.85m, 1_000);

        Assert.Equal(1_000, exactBoundary.CasePrice);
        Assert.Equal(2_000, fractionalExpectedValue.CasePrice);
    }

    [Fact]
    public void Calculate_rejects_invalid_inputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TicketPriceCalculator.Calculate(-1, 0.85m, 1_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => TicketPriceCalculator.Calculate(1, 0m, 1_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => TicketPriceCalculator.Calculate(1, 0.85m, 0));
    }
}
