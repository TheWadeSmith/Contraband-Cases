using ContrabandCases.Server.Configuration;
using ContrabandCases.Server.Content;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class ContrabandContentDefinitionsTests
{
    [Fact]
    public void Compute_handbook_price_for_trader_sell_target_meets_or_exceeds_target_at_therapist_coefficient()
    {
        // Therapist's live buy_price_coef is 37, confirmed against her shipped base.json.
        var handbookPrice = ContrabandContentDefinitions.ComputeHandbookPriceForTraderSellTarget(150_000, 37d);

        Assert.Equal(238_096, handbookPrice);
        var realizedSellPrice = handbookPrice * 0.63d;
        Assert.True(realizedSellPrice >= 150_000d, $"Realized sell price {realizedSellPrice} fell short of the target.");
    }

    [Fact]
    public void Compute_handbook_price_for_trader_sell_target_rounds_up_rather_than_undershooting()
    {
        var handbookPrice = ContrabandContentDefinitions.ComputeHandbookPriceForTraderSellTarget(1, 50d);

        // 1 / 0.5 = 2 exactly, no rounding needed, but this pins the "no accidental floor" behaviour.
        Assert.Equal(2, handbookPrice);
    }

    [Theory]
    [InlineData(0, 37d)]
    [InlineData(-1, 37d)]
    public void Compute_handbook_price_for_trader_sell_target_rejects_non_positive_target(long target, double coef)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContrabandContentDefinitions.ComputeHandbookPriceForTraderSellTarget(target, coef));
    }

    [Theory]
    [InlineData(-0.1d)]
    [InlineData(100d)]
    [InlineData(150d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Compute_handbook_price_for_trader_sell_target_rejects_invalid_coefficient(double coef)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContrabandContentDefinitions.ComputeHandbookPriceForTraderSellTarget(150_000, coef));
    }

    [Fact]
    public void Ensure_therapist_buys_configured_items_adds_only_the_configured_template_ids()
    {
        var traders = TradersWithTherapist();
        var config = ModConfig.Parse("""{ "therapistSellPriceCase": 150000 }""");

        ContrabandContentDefinitions.EnsureTherapistBuysConfiguredItems(traders, config);

        var idList = traders[Traders.THERAPIST].Base.ItemsBuy!.IdList;
        Assert.Contains((MongoId)ModConstants.CaseTemplateId, idList);
        Assert.DoesNotContain((MongoId)ModConstants.KeyTemplateId, idList);
    }

    [Fact]
    public void Ensure_therapist_buys_configured_items_adds_both_when_both_are_configured()
    {
        var traders = TradersWithTherapist();
        var config = ModConfig.Parse("""
            { "therapistSellPriceCase": 150000, "therapistSellPriceKey": 65000 }
            """);

        ContrabandContentDefinitions.EnsureTherapistBuysConfiguredItems(traders, config);

        var idList = traders[Traders.THERAPIST].Base.ItemsBuy!.IdList;
        Assert.Contains((MongoId)ModConstants.CaseTemplateId, idList);
        Assert.Contains((MongoId)ModConstants.KeyTemplateId, idList);
    }

    [Fact]
    public void Therapist_buys_every_case_by_default_without_duplicate_entries_or_enabling_key_sales()
    {
        var traders = TradersWithTherapist();
        var config = ModConfig.Parse("{}");
        ContrabandContentDefinitions.EnsureTherapistBuysConfiguredItems(traders, config);
        ContrabandContentDefinitions.EnsureTherapistBuysConfiguredItems(traders, config);
        var ids = traders[Traders.THERAPIST].Base.ItemsBuy!.IdList;
        foreach (var template in CaseContracts.Templates)
            Assert.Single(ids, id => id == (MongoId)template);
        Assert.DoesNotContain((MongoId)ModConstants.KeyTemplateId, ids);
    }

    [Fact]
    public void Ensure_therapist_buys_configured_items_throws_when_therapist_is_missing()
    {
        var traders = new TradersTable();
        var config = ModConfig.Parse("""{ "therapistSellPriceCase": 150000 }""");

        Assert.Throws<InvalidOperationException>(() =>
            ContrabandContentDefinitions.EnsureTherapistBuysConfiguredItems(traders, config));
    }

    private static TradersTable TradersWithTherapist()
    {
        var traders = new TradersTable();
        traders[Traders.THERAPIST] = new Trader
        {
            Assort = new TraderAssort
            {
                Items = [],
                BarterScheme = new Dictionary<MongoId, List<List<BarterScheme>>>(),
                LoyalLevelItems = new Dictionary<MongoId, int>()
            },
            Base = new TraderBase
            {
                Id = Traders.THERAPIST,
                Name = "Therapist",
                ItemsBuy = new ItemBuyData
                {
                    Category = [],
                    IdList = []
                }
            },
            Dialogue = new Dictionary<string, List<string>?>(),
            QuestAssort = new Dictionary<string, Dictionary<MongoId, MongoId>>()
        };
        return traders;
    }
}
