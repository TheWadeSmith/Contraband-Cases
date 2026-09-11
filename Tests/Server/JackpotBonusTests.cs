using System.Text.Json;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class JackpotBonusTests
{
    internal const string Equipment = "900000000000000000000011";

    [Theory]
    [InlineData(0, "kit.compact-v2.jackpot-v2")]
    [InlineData(3_000_000, "kit.compact-v2")]
    [InlineData(-1, "kit.compact-v2")]
    [InlineData(4_000_000, "kit.compact-v2.jackpot-v2")]
    public void Loader_rejects_bonuses_without_an_exact_new_identity_contract(int bonus, string id) =>
        Assert.Throws<CargoCatalogValidationException>(() => Pack(bonus, id));

    [Theory]
    [InlineData("stateful")]
    [InlineData("nested")]
    [InlineData("under-cash")]
    [InlineData("usd")]
    [InlineData("bitcoin")]
    [InlineData("wrong-total")]
    [InlineData("oversize-stack")]
    [InlineData("too-many-stacks")]
    [InlineData("too-many-equipment-roots")]
    [InlineData("too-many-equipment-nodes")]
    public void Materialization_rejects_malformed_mixed_prizes_before_allocating_items(string fault)
    {
        var nodes = new List<RewardForestNode> { Node("gear", Equipment) };
        var amount = fault == "too-many-stacks" ? 100_000 : fault == "oversize-stack" ? 1_000_000 : 500_000;
        var cashCount = fault == "too-many-stacks" ? 50 : 3_000_000 / amount;
        for (var i = 0; i < cashCount; i++)
        {
            var path = "cash-" + i;
            nodes.Add(fault == "nested" && i == 0
                ? new RewardForestNode("gear", path, CashPayouts.Roubles, "gear", "main", null, amount)
                : Node(path, CashPayouts.Roubles, amount, fault == "stateful" && i == 0 ? new RewardStableState(durability: 1) : null));
        }
        if (fault == "wrong-total") nodes.RemoveAt(nodes.Count - 1);
        if (fault is "usd" or "bitcoin") nodes.Add(Node("other-money", fault == "usd" ? CashPayouts.Dollars : CashPayouts.Bitcoin));
        if (fault == "under-cash") nodes.Add(new RewardForestNode("cash-0", "child", Equipment, "cash-0", "main", null, 1));
        if (fault == "too-many-equipment-roots") for (var i = 1; i <= 8; i++) nodes.Add(Node("gear-" + i, Equipment));
        if (fault == "too-many-equipment-nodes") for (var i = 1; i <= 128; i++)
            nodes.Add(new RewardForestNode("gear", "part-" + i, Equipment, "gear", "slot-" + i, null, 1));
        var forest = RewardForest.Create(nodes);
        var materializer = new CargoLotMaterializer(FindTemplate, () => throw new Xunit.Sdk.XunitException("Allocated before validation"));
        Assert.Throws<CargoCatalogValidationException>(() => materializer.MaterializeJackpotPayout(forest, [], fault == "too-many-stacks" ? 5_000_000 : 3_000_000));
    }

    [Fact]
    public void Too_small_native_currency_stacks_disable_the_prize_instead_of_creating_item_spam()
    {
        TemplateItem? Lookup(string id)
        {
            var item = FindTemplate(id);
            if (id == CashPayouts.Roubles) item!.Properties!.StackMaxSize = 50_000;
            return item;
        }
        var resolver = new CargoLotResolver(new CargoLotResolverDependencies(Lookup, _ => null));
        Assert.Throws<CargoCatalogValidationException>(() => resolver.Resolve(Assert.Single(Pack(3_000_000).Lots)));
    }

    [Fact]
    public void Cash_uses_face_value_and_resale_quotes_only_receive_equipment()
    {
        var lot = new CargoLotResolver(new CargoLotResolverDependencies(FindTemplate, _ => null)).Resolve(Assert.Single(Pack(3_000_000).Lots));
        var evaluated = new CargoLotEvaluator(FindTemplate, _ => 100_000, forest =>
        {
            Assert.Equal(Equipment, Assert.Single(forest.Nodes).TemplateId);
            return 40_000;
        }).Evaluate(lot);
        Assert.Equal(3_100_000, evaluated.Evaluation.UseValue);
        Assert.Equal(3_100_000, evaluated.Evaluation.HandbookValue);
        Assert.Equal(3_040_000, evaluated.Evaluation.TraderResaleEstimate);
    }

    private static RewardForestNode Node(string path, string id, int count = 1, RewardStableState? state = null) =>
        new(path, path, id, null, null, null, count, state);

    [Theory]
    [InlineData("\"3000000\"")]
    [InlineData("3000000.5")]
    [InlineData("true")]
    [InlineData("null")]
    public void Loader_rejects_non_integer_bonus_json(string token) => Assert.Throws<CargoCatalogValidationException>(() =>
        new JsonRewardPackLoader().Load(PackJson(3_000_000).Replace("\"roubleBonus\":3000000", "\"roubleBonus\":" + token)));

    [Theory]
    [InlineData(3_000_000, 6)]
    [InlineData(5_000_000, 10)]
    public void Explicit_jackpot_resolves_equipment_and_exact_cash_into_one_fingerprint(int bonus, int stacks)
    {
        var pack = Pack(bonus);
        var lot = new CargoLotResolver(new CargoLotResolverDependencies(FindTemplate, _ => null))
            .Resolve(Assert.Single(pack.Lots));
        Assert.Single(lot.Forest.Nodes, n => n.TemplateId == Equipment);
        var cash = lot.Forest.Nodes.Where(n => n.TemplateId == CashPayouts.Roubles).ToArray();
        Assert.Equal(stacks, cash.Length);
        Assert.Equal(bonus, cash.Sum(n => n.StackCount));
        Assert.All(cash, n => { Assert.Null(n.ParentLogicalPath); Assert.Null(n.StableState); Assert.Equal(500_000, n.StackCount); });
        Assert.Equal(RewardForestFingerprintV2.Compute("test", "kit.compact-v2.jackpot-v2", lot.Forest), lot.Fingerprint);
        Assert.Throws<CargoCatalogValidationException>(() => new CargoLotMaterializer(FindTemplate).Validate(lot.Forest));
    }

    internal static CargoLotPack Pack(int bonus, string lotId = "kit.compact-v2.jackpot-v2") =>
        new JsonRewardPackLoader().Load(PackJson(bonus, lotId));

    private static string PackJson(int bonus, string lotId = "kit.compact-v2.jackpot-v2") => JsonSerializer.Serialize(new
        {
            schemaVersion = 1, providerId = "test", packVersion = "1.0.0", displayLabel = "Test",
            providerWeight = 1, requiredTemplateIds = Array.Empty<string>(),
            requiredPresetIds = Array.Empty<string>(), requiredBundleKeys = Array.Empty<string>(),
            lots = new[] { new { lotId, displayName = "Test jackpot", purpose = "Test equipment and cash",
                familyId = "field-supply", trackId = "medical", anchorTemplateId = Equipment, weight = 1,
                usePath = new { kind = "raidRole", roleId = "medical" },
                recipe = new[] { new { kind = "template", templateId = Equipment, instanceCount = 1, stackCountPerInstance = 1 } },
                roubleBonus = bonus } }
        });

    internal static TemplateItem? FindTemplate(string id) => id == Equipment ? new TemplateItem
    {
        Id = id, Parent = "54009119af1c881c07000029", Name = "Equipment", Type = "Item",
        Properties = new TemplateItemProperties { StackMaxSize = 1, Width = 1, Height = 1,
            Slots = [], Chambers = [], Cartridges = [], StackSlots = [], Grids = [],
            Prefab = new Prefab { Path = "test/equipment.bundle" } }
    } : CashCacheTests.FindTemplate(id);
}
