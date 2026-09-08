using System.Text.Json;
using System.Text.Json.Serialization;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Client.Opening;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Json;
using Xunit;
using Path = System.IO.Path;

namespace ContrabandCases.Tests.Server;

public sealed class InstalledOptionalPackCompatibilityTests
{
    [Theory]
    [InlineData("amonya.arcane-cache", 3)]
    [InlineData("isb-aishi.elite-armory", 4)]
    [InlineData("natalya.elite-armor", 3)]
    [InlineData("wtt-contentbackport.elite-optics", 3)]
    public void Current_recipes_resolve_against_installed_mod_definitions(string name, int expected)
    {
        var snapshot = ReadCatalog(name);
        Assert.Empty(snapshot.SkippedPacks);
        Assert.True(expected == snapshot.FreshOpeningLots.Count, string.Join("; ", snapshot.Lots
            .Where(l => ShipmentEconomy.IsCompact(l.Identity.LotId))
            .Select(l => $"{l.Identity.LotId}: {l.Forest.Roots.Count} roots, {l.Forest.Nodes.Count} nodes, {l.Evaluation.FootprintCells} cells")));
        Assert.All(snapshot.FreshOpeningLots, lot =>
        {
            Assert.EndsWith(".compatible-v1.shipment-v1.compact-v1", lot.Identity.LotId);
            Assert.InRange(lot.Forest.Roots.Count, 1, 8);
            Assert.InRange(lot.Forest.Nodes.Count, 1, 128);
            Assert.InRange(lot.Evaluation.FootprintCells, 1, 64);
        });
        var library = ManifestLibraryProjection.Create(new CaseOpeningJournal(), snapshot, new Dictionary<string, string>());
        var parsed = ManifestSnapshotParser.ParseLibrary(JsonSerializer.Serialize(new { err = 0, errmsg = (string?)null, data = library }));
        Assert.Equal(expected, parsed.Lots.Count);
    }

    internal static CargoCatalogSnapshot ReadCatalog(string name)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString };
        foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters()) options.Converters.Add(converter);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Tests/Fixtures/curated-mod-templates.json")));
        var templates = doc.RootElement.GetProperty("templates").Deserialize<Dictionary<string, TemplateItem>>(options)!;
        var presets = doc.RootElement.GetProperty("presets").Deserialize<Dictionary<string, Preset>>(options)!;
        var prices = doc.RootElement.GetProperty("prices").Deserialize<Dictionary<string, double>>(options)!;
        var pack = new JsonRewardPackLoader().LoadFile(Path.Combine(root, "config/reward-packs", name + ".json"));
        var snapshot = new CargoCatalogSnapshotBuilder(
            new CargoLotResolver(new CargoLotResolverDependencies(templates.GetValueOrDefault, presets.GetValueOrDefault)),
            new CargoLotEvaluator(templates.GetValueOrDefault, id => prices.GetValueOrDefault(id)),
            new CargoPackRequirementValidator(templates.GetValueOrDefault, presets.GetValueOrDefault).Validate).Build(pack);
        return snapshot;
    }

    [Fact]
    public void Every_0_4_10_definition_is_unchanged_and_explicitly_retired_not_substituted()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Tests/Fixtures/optional-packs-0.4.10.json")));
        var count = 0;
        foreach (var entry in baseline.RootElement.EnumerateObject())
        {
            using var current = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config/reward-packs", entry.Name + ".json")));
            var lots = current.RootElement.GetProperty("lots").EnumerateArray().ToDictionary(lot => lot.GetProperty("lotId").GetString()!);
            var retired = current.RootElement.GetProperty("retiredLotIds").EnumerateArray().Select(id => id.GetString()!).ToHashSet();
            foreach (var old in entry.Value.GetProperty("lots").EnumerateArray())
            {
                var id = old.GetProperty("lotId").GetString()!;
                Assert.True(JsonElement.DeepEquals(old, lots[id]), entry.Name + "/" + id);
                Assert.Contains(id, retired);
                count++;
            }
            Assert.Equal(entry.Value.GetProperty("packVersion").GetString(), current.RootElement.GetProperty("packVersion").GetString());
        }
        Assert.Equal(26, count);
    }

    [Theory]
    [InlineData("Soft_armor_front")]
    [InlineData("absent")]
    public void Preset_correction_cannot_remove_required_or_unknown_slots(string slot)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString };
        foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters()) options.Converters.Add(converter);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Tests/Fixtures/optional-mod-templates.json")));
        var templates = doc.RootElement.GetProperty("templates").Deserialize<Dictionary<string, TemplateItem>>(options)!;
        var presets = doc.RootElement.GetProperty("presets").Deserialize<Dictionary<string, Preset>>(options)!;
        var lot = new CargoLotDefinition("test", "1.0.0", "test", "Test", "Test", new FamilyId("operator"),
            new TrackId("test"), "6a07e920d7a2c6d597db893c", 1, new RaidRole("medic"),
            [new PresetLine("6a07f2f08e75711e0854623e", [slot])]);
        var resolver = new CargoLotResolver(new CargoLotResolverDependencies(templates.GetValueOrDefault, presets.GetValueOrDefault));
        Assert.Throws<CargoCatalogValidationException>(() => resolver.Resolve(lot));
    }
}
