using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class TestingCrateProviderMapTests
{
    [Fact]
    public void TrueRandom_has_no_restriction()
    {
        Assert.Null(TestingCrateProviderMap.GetAllowedProviderIds(TestingCrateType.TrueRandom));
    }

    [Fact]
    public void Vault_forces_exactly_the_existing_vault_provider()
    {
        var allowed = TestingCrateProviderMap.GetAllowedProviderIds(TestingCrateType.Vault);

        Assert.NotNull(allowed);
        Assert.Equal(new[] { "vault" }, allowed!.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(ModConstants.VaultProviderId, Assert.Single(allowed));
    }

    [Fact]
    public void Themed_forces_the_existing_non_card_themed_packs()
    {
        var allowed = TestingCrateProviderMap.GetAllowedProviderIds(TestingCrateType.Themed);

        Assert.NotNull(allowed);
        Assert.Equal(
            new[] { "sjx.combat-chemistry", "vultify.cooler-stims" },
            allowed!.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Cards_forces_the_existing_krackasourus_card_packs()
    {
        var allowed = TestingCrateProviderMap.GetAllowedProviderIds(TestingCrateType.Cards);

        Assert.NotNull(allowed);
        Assert.Equal(
            new[]
            {
                "krackasourus.anime-cards",
                "krackasourus.pokemon-cards",
                "krackasourus.yugioh-cards"
            },
            allowed!.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Scrap_forces_core_plus_the_everyday_curated_mod_packs_and_never_the_elite_ones()
    {
        var allowed = TestingCrateProviderMap.GetAllowedProviderIds(TestingCrateType.Scrap);

        Assert.NotNull(allowed);
        Assert.Equal(
            new[]
            {
                "core",
                "eco-attachment.field-cache",
                "isb-aishi.field-armory",
                "natalya.field-gear",
                "wtt-contentbackport.field-resupply"
            },
            allowed!.OrderBy(id => id, StringComparer.Ordinal));
        Assert.DoesNotContain("vault", allowed);
        Assert.DoesNotContain("natalya.elite-armor", allowed);
        Assert.DoesNotContain("isb-aishi.elite-armory", allowed);
        Assert.DoesNotContain("wtt-contentbackport.elite-optics", allowed);
        Assert.DoesNotContain("eco-attachment.elite-optics", allowed);
        Assert.DoesNotContain("amonya.arcane-cache", allowed);
        Assert.DoesNotContain("eco-ww2.relic-cache", allowed);
    }

    [Fact]
    public void Mega_forces_the_vault_plus_the_elite_curated_mod_packs_and_never_the_everyday_ones()
    {
        var allowed = TestingCrateProviderMap.GetAllowedProviderIds(TestingCrateType.Mega);

        Assert.NotNull(allowed);
        Assert.Equal(
            new[]
            {
                "amonya.arcane-cache",
                "eco-attachment.elite-optics",
                "eco-ww2.relic-cache",
                "isb-aishi.elite-armory",
                "natalya.elite-armor",
                "vault",
                "wtt-contentbackport.elite-optics"
            },
            allowed!.OrderBy(id => id, StringComparer.Ordinal));
        Assert.DoesNotContain("core", allowed);
        Assert.DoesNotContain("natalya.field-gear", allowed);
        Assert.DoesNotContain("isb-aishi.field-armory", allowed);
        Assert.DoesNotContain("wtt-contentbackport.field-resupply", allowed);
        Assert.DoesNotContain("eco-attachment.field-cache", allowed);
    }

    [Fact]
    public void Every_forced_mixed_crate_type_maps_to_a_non_empty_provider_set()
    {
        foreach (var crateType in Enum.GetValues<TestingCrateType>().Where(value =>
            value != TestingCrateType.TrueRandom && TestingCrateTypeCodec.PremiumTier(value) is null &&
            TestingCrateTypeCodec.CaseTemplate(value) == ModConstants.CaseTemplateId))
        {
            var allowed = TestingCrateProviderMap.GetAllowedProviderIds(crateType);
            Assert.NotNull(allowed);
            Assert.NotEmpty(allowed!);
        }
    }

    [Theory]
    [InlineData(TestingCrateType.OperationsCase)]
    [InlineData(TestingCrateType.RelicsCase)]
    [InlineData(TestingCrateType.BlackSiteCase)]
    [InlineData(TestingCrateType.CashCache)]
    public void Actual_themed_cases_do_not_use_debug_provider_forcing(TestingCrateType type)
    {
        Assert.Null(TestingCrateProviderMap.GetAllowedProviderIds(type));
    }
}
