using ContrabandCases.Server.Configuration;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class ModConfigTests
{
    [Fact]
    public void Parse_applies_defaults_when_properties_are_omitted()
    {
        var config = ModConfig.Parse("{}");

        Assert.Equal(5, config.TraderStock);
        Assert.Equal(5, config.CaseStock);
        Assert.Equal(4.5d, config.AnimationDurationSeconds);
        Assert.False(config.ReducedMotionDefault);
        Assert.False(config.DebugLogging);
        Assert.False(config.TestingInventoryGrantsEnabled);
        Assert.Null(config.FixedCasePrice);
        Assert.Equal(2d, config.KeyLootWeightPercent);
    }

    [Fact]
    public void Parse_accepts_jsonc_and_reads_every_supported_property()
    {
        var config = ModConfig.Parse("""
            {
              // User-facing presentation and pricing overrides.
              "traderStock": 7,
              "animationDurationSeconds": 2.25,
              "reducedMotionDefault": true,
              "debugLogging": true,
              "testingInventoryGrantsEnabled": true,
              "fixedCasePrice": 70000,
              "therapistSellPriceKey": 30000,
              "keyLootWeightPercent": 3.25,
            }
            """);

        Assert.Equal(7, config.TraderStock);
        Assert.Equal(7, config.CaseStock);
        Assert.Equal(2.25d, config.AnimationDurationSeconds);
        Assert.True(config.ReducedMotionDefault);
        Assert.True(config.DebugLogging);
        Assert.True(config.TestingInventoryGrantsEnabled);
        Assert.Equal(70_000, config.FixedCasePrice);
        Assert.Equal(30_000, config.TherapistSellPriceKey);
        Assert.Equal(3.25d, config.KeyLootWeightPercent);
    }

    [Fact]
    public void Legacy_trader_stock_migrates_to_case_stock()
    {
        var config = ModConfig.Parse("{ \"traderStock\": 7 }");

        Assert.Equal(7, config.CaseStock);
    }

    [Fact]
    public void Explicit_case_stock_is_respected()
    {
        var config = ModConfig.Parse("{ \"caseStock\": 4 }");

        Assert.Equal(4, config.CaseStock);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ \"unexpected\": true }")]
    [InlineData("{ \"traderStock\": 5, \"traderStock\": 6 }")]
    [InlineData("{ \"traderStock\": \"5\" }")]
    [InlineData("{ \"traderStock\": 5, \"caseStock\": 6 }")]
    [InlineData("{ \"caseStock\": 0 }")]
    [InlineData("{ \"keyStock\": 5 }")]
    [InlineData("{ \"fixedKeyPrice\": 1 }")]
    [InlineData("{ \"animationDurationSeconds\": true }")]
    [InlineData("{ \"reducedMotionDefault\": 1 }")]
    [InlineData("{ \"testingInventoryGrantsEnabled\": 1 }")]
    [InlineData("{ \"fixedCasePrice\": 1.5 }")]
    [InlineData("{ \"keyLootWeightPercent\": 0 }")]
    [InlineData("{ \"keyLootWeightPercent\": -1 }")]
    public void Parse_rejects_unknown_duplicate_or_wrongly_typed_values(string json)
    {
        Assert.Throws<InvalidOperationException>(() => ModConfig.Parse(json));
    }
}
