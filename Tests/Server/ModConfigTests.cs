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
        Assert.Equal(2.2d, config.KeyLootWeightPercent);
        Assert.Equal(1.1d, config.CaseLootWeightPercent);
    }

    [Fact]
    public void Shipped_loot_weights_match_defaults_and_are_only_ten_percent_above_previous_weights()
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var shipped = ModConfig.Parse(File.ReadAllText(System.IO.Path.Combine(root, "config/config.jsonc")));
        var defaults = ModConfig.Parse("{}");
        Assert.Equal(2d * 1.1d, shipped.KeyLootWeightPercent);
        Assert.Equal(1d * 1.1d, shipped.CaseLootWeightPercent);
        Assert.Equal(defaults.KeyLootWeightPercent, shipped.KeyLootWeightPercent);
        Assert.Equal(defaults.CaseLootWeightPercent, shipped.CaseLootWeightPercent);
    }

    [Fact]
    public void Existing_explicit_loot_settings_are_preserved_instead_of_silently_migrated()
    {
        var config = ModConfig.Parse("""{ "keyLootWeightPercent": 2.0, "caseLootWeightPercent": 1.0 }""");
        Assert.Equal(2d, config.KeyLootWeightPercent);
        Assert.Equal(1d, config.CaseLootWeightPercent);
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
              "caseLootWeightPercent": 0.5,
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
        Assert.Equal(0.5d, config.CaseLootWeightPercent);
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

    [Fact]
    public void Case_loot_can_be_disabled_without_disabling_keys()
    {
        var config = ModConfig.Parse("""{ "caseLootWeightPercent": 0 }""");
        Assert.Equal(0d, config.CaseLootWeightPercent);
        Assert.Equal(2.2d, config.KeyLootWeightPercent);
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
    [InlineData("{ \"caseLootWeightPercent\": -1 }")]
    [InlineData("{ \"caseLootWeightPercent\": 1e999 }")]
    [InlineData("{ \"caseLootWeightPercent\": null }")]
    [InlineData("{ \"caseLootWeightPercent\": true }")]
    [InlineData("{ \"caseLootWeightPercent\": \"1\" }")]
    [InlineData("{ \"caseLootWeightPercent\": 1, \"caseLootWeightPercent\": 2 }")]
    public void Parse_rejects_unknown_duplicate_or_wrongly_typed_values(string json)
    {
        Assert.Throws<InvalidOperationException>(() => ModConfig.Parse(json));
    }
}
