using System.Text.Json;
using ContrabandCases.Server.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class CoreCatalogCompatibilityTests
{
    [Fact]
    public void Expanded_core_preserves_all_original_lot_definitions_and_pack_identity()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        using var old = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Tests/Fixtures/core-0.3.3-original.json")));
        using var current = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config/reward-packs/core.json")));
        var currentLots = current.RootElement.GetProperty("lots").EnumerateArray()
            .ToDictionary(lot => lot.GetProperty("lotId").GetString()!);
        Assert.Equal(100, currentLots.Count);
        foreach (var lot in old.RootElement.GetProperty("lots").EnumerateArray())
            Assert.True(JsonElement.DeepEquals(lot, currentLots[lot.GetProperty("lotId").GetString()!]));
        foreach (var property in old.RootElement.EnumerateObject().Where(p => p.Name != "lots"))
            Assert.True(JsonElement.DeepEquals(property.Value, current.RootElement.GetProperty(property.Name)));
        Assert.Equal(100, new JsonRewardPackLoader().LoadFile(Path.Combine(root, "config/reward-packs/core.json")).Lots.Count);
    }

    [Fact]
    public void Gameplay_additions_preserve_every_preexisting_lot_and_pack_dependency()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "Tests/Fixtures/reward-packs-0.4.1-selected.json")));
        foreach (var pack in baseline.RootElement.EnumerateObject())
        {
            using var current = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
                "config/reward-packs", pack.Name + ".json")));
            foreach (var metadata in pack.Value.EnumerateObject().Where(p => p.Name != "lots"))
                Assert.True(JsonElement.DeepEquals(metadata.Value, current.RootElement.GetProperty(metadata.Name)));
            var lots = current.RootElement.GetProperty("lots").EnumerateArray()
                .ToDictionary(lot => lot.GetProperty("lotId").GetString()!);
            foreach (var historical in pack.Value.GetProperty("lots").EnumerateArray())
                Assert.True(JsonElement.DeepEquals(historical, lots[historical.GetProperty("lotId").GetString()!]));
        }
    }
}
