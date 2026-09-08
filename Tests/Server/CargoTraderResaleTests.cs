using ContrabandCases.Client.Opening;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class CargoTraderResaleTests
{
    private const string Item = "900000000000000000000001";
    private const string Part = "900000000000000000000002";
    private const string Category = "900000000000000000000003";
    private const string Base = "54009119af1c881c07000029";
    private static TemplateItem? Find(string id) => new TemplateItem
    {
        Id = id, Parent = id == Category ? Base : Category, Name = id, Type = id == Category ? "Node" : "Item",
        Properties = new TemplateItemProperties { Width = 1, Height = 1, StackMaxSize = 10, Prefab = new Prefab { Path = "test.bundle" } }
    };
    private static TraderBase Buyer(double coefficient = 40) => new()
    {
        Id = "900000000000000000000004", Currency = CurrencyType.RUB, UnlockedByDefault = true,
        LoyaltyLevels = [new TraderLoyaltyLevel { BuyPriceCoefficient = coefficient }],
        ItemsBuy = new ItemBuyData { IdList = [], Category = [(MongoId)Category] },
        ItemsBuyProhibited = new ItemBuyData { IdList = [], Category = [] }
    };
    private static RewardForest Forest() => RewardForest.Create([new("root/0", "root/0", Item, null, null, null, 2)]);
    private static Dictionary<string, double> Prices => new() { [Item] = 100_000, [Part] = 50_000 };

    [Fact]
    public void Best_eligible_RUB_buyer_uses_its_coefficient_and_actual_quantities()
    {
        var buyers = new[] { Buyer(40), Buyer(30), Buyer(1) with { Currency = CurrencyType.USD },
            Buyer(1) with { UnlockedByDefault = false }, Buyer(1) with { Id = Traders.FENCE } };
        Assert.Equal(140_000, new CargoTraderResale(Find, Prices, buyers).Estimate(Forest()));
    }

    [Fact]
    public void Full_equipment_tree_is_valued_once_at_the_root_buyers_rate()
    {
        var tree = RewardForest.Create([new("root/0", "root/0", Item, null, null, null, 1),
            new("root/0", "root/0/part", Part, "root/0", "part", null, 1)]);
        Assert.Equal(90_000, new CargoTraderResale(Find, Prices, [Buyer()]).Estimate(tree));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(double.NaN)]
    public void Invalid_quotes_are_unavailable_not_zero(double coefficient) =>
        Assert.Null(new CargoTraderResale(Find, Prices, [Buyer(coefficient)]).Estimate(Forest()));

    [Fact]
    public void Rejection_missing_prices_unsaleable_items_and_broken_ancestry_do_not_claim_a_cash_value()
    {
        var buyer = Buyer();
        buyer.ItemsBuyProhibited!.IdList!.Add((MongoId)Item);
        Assert.Null(new CargoTraderResale(Find, Prices, [buyer]).Estimate(Forest()));
        Assert.Null(new CargoTraderResale(Find, new Dictionary<string, double>(), [Buyer()]).Estimate(Forest()));
        Assert.Null(new CargoTraderResale(_ => null, Prices, [Buyer()]).Estimate(Forest()));
        Assert.Null(new CargoTraderResale(id => Find(id)! with { Parent = id }, Prices, [Buyer()]).Estimate(Forest()));
        Assert.Null(new CargoTraderResale(id =>
        {
            var template = Find(id)!;
            template.Properties!.IsUnsaleable = true;
            return template;
        }, Prices, [Buyer()]).Estimate(Forest()));
    }

    [Fact]
    public void Diagnostic_resale_survives_server_projection_and_client_parser_without_changing_economy()
    {
        var pack = new CargoLotPack("test", "1.0.0", [new CargoLotDefinition("test", "1.0.0", "lot", "Test", "Test",
            new FamilyId("operator"), new TrackId("test"), Item, 1, new RaidRole("medic"), [new TemplateLine(Item, 2, 1)])]);
        var resolver = new CargoLotResolver(new CargoLotResolverDependencies(Find, _ => null));
        CargoCatalogSnapshot Build(bool resale) => new CargoCatalogSnapshotBuilder(resolver,
            new CargoLotEvaluator(Find, id => Prices.GetValueOrDefault(id),
                resale ? new CargoTraderResale(Find, Prices, [Buyer()]).Estimate : null)).Build(pack);
        var catalog = Build(true);
        Assert.Equal(Build(false).SnapshotId, catalog.SnapshotId);
        var library = ManifestLibraryProjection.Create(new CaseOpeningJournal(), catalog, new Dictionary<string, string>());
        var parsed = ManifestSnapshotParser.ParseLibrary(System.Text.Json.JsonSerializer.Serialize(new { err = 0, errmsg = (string?)null, data = library }));
        var lot = Assert.Single(parsed.Lots);
        Assert.Equal(200_000, lot.UseValue);
        Assert.Equal(120_000, lot.TraderResaleEstimate);
        Assert.Contains("Reference value ₽200,000", BrokerPresentation.Value(lot));
        Assert.Contains("Est. resale ₽120,000", BrokerPresentation.Value(lot));
        Assert.Contains("not a guaranteed quote", BrokerPresentation.PackageDetails(lot));
    }
}
