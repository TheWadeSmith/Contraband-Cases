using System.Text.Json;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class CompactRewardTests
{
    [Fact]
    public void All_paid_definitions_from_0_4_11_remain_exact_and_new_recipes_are_compact()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Tests/Fixtures/reward-packs-0.4.11.json")));
        var compactCount = 0;
        foreach (var entry in baseline.RootElement.EnumerateObject())
        {
            var path = Path.Combine(root, "config/reward-packs", entry.Name);
            using var current = JsonDocument.Parse(File.ReadAllText(path));
            var lots = current.RootElement.GetProperty("lots").EnumerateArray().ToDictionary(l => l.GetProperty("lotId").GetString()!);
            foreach (var old in entry.Value.GetProperty("lots").EnumerateArray())
                Assert.True(JsonElement.DeepEquals(old, lots[old.GetProperty("lotId").GetString()!]), entry.Name);
            foreach (var property in entry.Value.EnumerateObject().Where(p => p.Name is not ("lots" or "requiredTemplateIds" or "requiredPresetIds")))
                Assert.True(JsonElement.DeepEquals(property.Value, current.RootElement.GetProperty(property.Name)), entry.Name + property.Name);
            foreach (var requirement in new[] { "requiredTemplateIds", "requiredPresetIds" })
                foreach (var old in entry.Value.GetProperty(requirement).EnumerateArray())
                    Assert.Contains(current.RootElement.GetProperty(requirement).EnumerateArray(), item => JsonElement.DeepEquals(old, item));
            var pack = new JsonRewardPackLoader().LoadFile(path);
            var compact = pack.Lots.Where(l => ShipmentEconomy.IsCompact(l.LotId)).ToArray();
            Assert.NotEmpty(compact);
            foreach (var lot in compact)
            {
                compactCount++;
                Assert.InRange(lot.RecipeLines.Sum(line => line is TemplateLine t ? t.InstanceCount : 1), 1, 8);
                Assert.All(lot.RecipeLines.OfType<TemplateLine>().GroupBy(t => t.TemplateId),
                    group => Assert.InRange(group.Sum(t => t.InstanceCount), 1, 4));
                Assert.All(lot.RecipeLines.OfType<PresetLine>().GroupBy(t => t.PresetId),
                    group => Assert.InRange(group.Count(), 1, 2));
            }
        }
        Assert.Equal(122, compactCount);
    }

    [Fact]
    public void Extraction_chemistry_is_a_useful_equipment_package_not_an_injector_pile()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../config/reward-packs/core.json"));
        var pack = new JsonRewardPackLoader().LoadFile(root);
        var old = pack.Lots.Single(l => l.LotId == "extraction-chemistry.shipment-v1");
        var compact = pack.Lots.Single(l => l.LotId == "extraction-chemistry.shipment-v1.compact-v1");
        Assert.Equal(48, old.RecipeLines.OfType<TemplateLine>().Sum(t => t.InstanceCount));
        Assert.Contains(compact.RecipeLines.OfType<TemplateLine>(), t => t.TemplateId == "619cbf7d23893217ec30b689");
        Assert.InRange(compact.RecipeLines.OfType<TemplateLine>().Where(t => t.TemplateId is
            "544fb3f34bdc2d03748b456a" or "5c0e531d86f7747fa23f4d42" or "5ed51652f6c34d2cc26336a1")
            .Sum(t => t.InstanceCount), 1, 3);
        Assert.Equal(2, ShipmentEconomy.Generation(compact.LotId));
        Assert.Equal(1, ShipmentEconomy.Generation(old.LotId));
        Assert.Equal("extraction-chemistry", ShipmentEconomy.BaseId(compact.LotId));
    }

    [Fact]
    public void Current_curated_packages_never_duplicate_a_weapon_or_consumable_template()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../config/reward-packs"));
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        foreach (var lot in new JsonRewardPackLoader().LoadFile(file).Lots.Where(l => ShipmentEconomy.IsCompact(l.LotId)))
        {
            Assert.All(lot.RecipeLines.OfType<PresetLine>().GroupBy(t => t.PresetId), group =>
                Assert.True(group.Count() == 1, $"{lot.ProviderId}/{lot.LotId}: duplicate preset {group.Key}"));
            Assert.All(lot.RecipeLines.OfType<TemplateLine>().GroupBy(t => t.TemplateId), group =>
                Assert.True(group.Sum(t => t.InstanceCount) <= 2, $"{lot.ProviderId}/{lot.LotId}: repeated template {group.Key}"));
        }
    }
}
