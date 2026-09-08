using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using System.Text.Json;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Xunit;
using Path = System.IO.Path;

namespace ContrabandCases.Tests.Server;

public sealed class ShipmentRebalanceTests
{
    [Fact]
    public void All_17_packs_preserve_every_pre_rebalance_definition_and_pack_requirement()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "Tests/Fixtures/reward-packs-0.4.9.json")));
        Assert.Equal(17, baseline.RootElement.EnumerateObject().Count());
        var count = 0;
        foreach (var pack in baseline.RootElement.EnumerateObject())
        {
            using var current = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config/reward-packs", pack.Name)));
            foreach (var property in pack.Value.EnumerateObject().Where(p => p.Name != "lots" && p.Name != "requiredPresetIds" && p.Name != "requiredTemplateIds"))
                Assert.True(JsonElement.DeepEquals(property.Value, current.RootElement.GetProperty(property.Name)), pack.Name);
            foreach (var template in pack.Value.GetProperty("requiredTemplateIds").EnumerateArray())
                Assert.Contains(current.RootElement.GetProperty("requiredTemplateIds").EnumerateArray(), t => JsonElement.DeepEquals(t, template));
            foreach (var preset in pack.Value.GetProperty("requiredPresetIds").EnumerateArray())
                Assert.Contains(current.RootElement.GetProperty("requiredPresetIds").EnumerateArray(), p => JsonElement.DeepEquals(p, preset));
            var lots = current.RootElement.GetProperty("lots").EnumerateArray().ToDictionary(l => l.GetProperty("lotId").GetString()!);
            foreach (var original in pack.Value.GetProperty("lots").EnumerateArray())
            {
                Assert.True(JsonElement.DeepEquals(original, lots[original.GetProperty("lotId").GetString()!]), pack.Name);
                count++;
            }
        }
        Assert.Equal(122, count);
    }

    [Fact]
    public void Every_authored_shipment_contains_bounded_whole_packages_not_a_fake_valuation_multiplier()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../config/reward-packs"));
        var count = 0;
        foreach (var file in Directory.GetFiles(root, "*.json"))
        {
            var pack = new JsonRewardPackLoader().LoadFile(file);
            var lots = pack.Lots.ToDictionary(l => l.LotId);
            foreach (var shipment in pack.Lots.Where(l => ShipmentEconomy.IsShipment(l.LotId) && !l.LotId.Contains(".compatible-v1.")))
            {
                var original = lots[ShipmentEconomy.BaseId(shipment.LotId)];
                Assert.Equal(original.Weight, shipment.Weight);
                Assert.Equal(original.TrackId, shipment.TrackId);
                Assert.Equal(original.AnchorTemplateId, shipment.AnchorTemplateId);
                Assert.Equal(0, shipment.RecipeLines.Count % original.RecipeLines.Count);
                var copies = shipment.RecipeLines.Count / original.RecipeLines.Count;
                Assert.InRange(copies, 2, 8);
                Assert.InRange(shipment.RecipeLines.Count, 1, 64);
                Assert.InRange(shipment.RecipeLines.Sum(l => l is TemplateLine t ? t.InstanceCount : 1), 1, 64);
                for (var i = 0; i < shipment.RecipeLines.Count; i++)
                    Assert.Equal(LineKey(original.RecipeLines[i % original.RecipeLines.Count]), LineKey(shipment.RecipeLines[i]));
                Assert.StartsWith($"{copies} complete ", shipment.Purpose);
                count++;
            }
        }
        Assert.Equal(117, count);
    }

    [Theory]
    [InlineData(239_999, RewardRarity.ScavGrade)]
    [InlineData(240_000, RewardRarity.Uncommon)]
    [InlineData(449_999, RewardRarity.Uncommon)]
    [InlineData(450_000, RewardRarity.Contractor)]
    [InlineData(899_999, RewardRarity.Contractor)]
    [InlineData(900_000, RewardRarity.Restricted)]
    [InlineData(1_799_999, RewardRarity.Restricted)]
    [InlineData(1_800_000, RewardRarity.BlackLabel)]
    public void Evaluator_uses_shipment_bands_but_preserves_historical_grades(long value, RewardRarity expected)
    {
        const string templateId = "900000000000000000000011";
        var template = new TemplateItem { Id = templateId, Properties = new TemplateItemProperties
            { Width = 1, Height = 1, CreditsPrice = value, StackMaxSize = 1 } };
        var evaluator = new CargoLotEvaluator(_ => template, _ => value);
        foreach (var id in new[] { "medical", "medical.shipment-v1" })
        {
            var definition = new CargoLotDefinition("core", "test-v1", id, id, "Test",
                new FamilyId("field-supply"), new TrackId("medical"), templateId, 1d, new RaidRole("medic"),
                [new TemplateLine(templateId, 1, 1)]);
            var forest = RewardForest.Create([new RewardForestNode("root", "root", templateId, null, null, null, 1)]);
            var fingerprint = RewardForestFingerprintV2.Compute("core", id, forest);
            var evaluated = evaluator.Evaluate(new ResolvedCargoLot(definition, forest, fingerprint,
                CargoLotIdentitySnapshot.Capture(definition, fingerprint)));
            Assert.Equal(value, evaluated.Evaluation.UseValue);
            Assert.Equal(id == "medical" ? CargoGradeBands.Assign(value) : expected, evaluated.Evaluation.Grade);
        }
    }

    [Theory]
    [InlineData("rub-150000.shipment-v1", 1_050_000)]
    [InlineData("rub-300000.shipment-v1", 2_400_000)]
    [InlineData("gp-25.shipment-v1", 200)]
    [InlineData("btc-1.shipment-v1", 4)]
    [InlineData("btc-2.shipment-v1", 10)]
    public void Larger_cash_payouts_materialize_all_promised_items_with_native_stack_bounds(string id, int amount)
    {
        var catalog = CashCacheTests.Catalog();
        var lot = Assert.Single(catalog.FreshOpeningLots, l => l.Identity.LotId == id);
        var items = new CargoLotMaterializer(CashCacheTests.FindTemplate).MaterializeCashPayout(lot.Forest, []).Items;
        Assert.Equal(amount, items.Sum(item => item.Upd!.StackObjectsCount));
        Assert.InRange(items.Count, 1, 32);
        Assert.Equal(items.Count, items.Select(item => item.Id).Distinct().Count());
        foreach (var current in new[] { catalog, CashPayoutCatalog.Disabled("No quotes", CashCacheTests.FindTemplate) })
            Assert.NotNull(current.ResolveExact(lot.Evaluation.Grade, lot.Identity, lot.Forest, lot.Fingerprint));
    }

    private static string LineKey(RewardRecipeLine line) => line switch
    {
        TemplateLine t => $"{t.TemplateId}/{t.InstanceCount}/{t.StackCountPerInstance}",
        PresetLine p => p.PresetId,
        _ => throw new InvalidOperationException()
    };

    [Fact]
    public void Validated_successor_retires_only_its_base_from_new_draws_not_exact_recovery()
    {
        var old = Lot("medical", 100_000);
        var next = Lot("medical.shipment-v1", 600_000);
        var other = Lot("other", 100_000);
        var catalog = CaseCatalogTests.Snapshot([old, next, other]);
        Assert.DoesNotContain(old, catalog.FreshOpeningLots);
        Assert.Contains(next, catalog.FreshOpeningLots);
        Assert.Contains(other, catalog.FreshOpeningLots);
        Assert.Same(old, catalog.ResolveExact(old.Evaluation.Grade, old.Identity, old.Forest, old.Fingerprint));
        Assert.Same(next, catalog.ResolveExact(next.Evaluation.Grade, next.Identity, next.Forest, next.Fingerprint));
        Assert.Contains(old, CaseCatalogTests.Snapshot([old]).FreshOpeningLots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Relay_cannot_cross_shipment_generations_even_during_legacy_continuation(bool legacy)
    {
        var old = Lot("medical", 100_000);
        var next = Lot("medical.shipment-v1", 600_000);
        var catalog = CaseCatalogTests.Snapshot(new[] { old, next,
            Lot("side", 110_000), Lot("upgrade", 200_000),
            Lot("side.shipment-v1", 660_000), Lot("upgrade.shipment-v1", 1_200_000) });
        var selector = new ManifestCatalogSelector(() => 0);
        foreach (var current in new[] { old, next })
        {
            var candidates = selector.CreateRelayCandidates(catalog,
                new ManifestEntitlementSnapshot(current.Evaluation.Grade, current.Identity, current.Forest, current.Fingerprint),
                RarityLadderVersion.FiveTier, legacy);
            Assert.NotEmpty(candidates);
            Assert.All(candidates, c => Assert.Equal(current.Identity.LotId.EndsWith(".shipment-v1"),
                c.Identity.LotId.EndsWith(".shipment-v1")));
        }
    }

    [Fact]
    public void Shipment_values_do_not_turn_every_card_into_a_legendary_or_chase()
    {
        var ordinary = Lot("medical.shipment-v1", 900_000);
        Assert.False(ManifestOpeningPool.IsChase(ordinary));
        Assert.True(ManifestOpeningPool.IsChase(Lot("jackpot.shipment-v1", 4_500_000)));
        Assert.True(ManifestOpeningPool.IsChase(Lot("jackpot", 750_000)));
    }

    [Fact]
    public void Cash_new_draws_have_real_million_scale_rewards_and_keep_old_GP_claims()
    {
        var catalog = CashCacheTests.Catalog();
        Assert.InRange(catalog.CasePrice!.Value, 850_000, 1_150_000);
        Assert.Equal(15, catalog.FreshOpeningLots.Count);
        Assert.All(catalog.FreshOpeningLots, lot => Assert.EndsWith(".shipment-v1", lot.Identity.LotId));
        foreach (var id in new[] { "gp-10", "gp-25" })
        {
            var old = Assert.Single(catalog.Lots, l => l.Identity.LotId == id);
            Assert.DoesNotContain(old, catalog.FreshOpeningLots);
            Assert.NotNull(catalog.ResolveExact(old.Evaluation.Grade, old.Identity, old.Forest, old.Fingerprint));
        }
    }

    private static ResolvedCargoLot Lot(string id, long value)
    {
        var definition = new CargoLotDefinition("core", "test-v1", id, id, "medical",
            new FamilyId("field-supply"), new TrackId("medical"), "medicine", 1d, new RaidRole("medic"),
            [new TemplateLine("medicine", 1, 1)]);
        var forest = RewardForest.Create([new RewardForestNode("root", "root", "medicine", null, null, null, 1)]);
        var fingerprint = RewardForestFingerprintV2.Compute("core", id, forest);
        var grade = CargoGradeBands.Assign(id.EndsWith(".shipment-v1") ? value / 6 : value);
        return new ResolvedCargoLot(definition, forest, fingerprint, CargoLotIdentitySnapshot.Capture(definition, fingerprint),
            new CargoLotEvaluation(value, value, 1, grade));
    }
}
