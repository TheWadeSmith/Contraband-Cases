using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Configuration;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace ContrabandCases.Server.Loot;

/// <summary>Crate-only case supply, registered by the final catalog startup barrier.</summary>
internal static class ContrabandCaseLootInjector
{
    private static readonly HashSet<MongoId> CaseIds = CaseContracts.Templates.Select(id => (MongoId)id).ToHashSet();

    // SPT 4.1.3 static container IDs, verified against database/templates/items.json and English locales.
    // Do not use the loot-container parent: corpses, bags, jackets and caches share that same parent.
    private static readonly HashSet<MongoId> CrateIds =
    [
        "578f87ad245977356274f2cc", // Wooden crate
        "5909d36d86f774660f0bb900", // Grenade box
        "5909d45286f77465a8136dc6", // Wooden ammo box
        "5909d5ef86f77467974efbd8", // Weapon box (5x2)
        "5909d76c86f77471e53d2adf", // Weapon box (6x3)
        "5909d7cf86f77470ee57d75a", // Weapon box (4x4)
        "5909d89086f77472591234a0", // Weapon box (5x5)
        "5d6fd13186f77424ad2a8c69", // Ration supply crate
        "5d6fd45b86f774317075ed43", // Technical supply crate
        "5d6fe50986f77449d97f7463", // Medical supply crate
        "67adf4b81c58bd68b2002fec", // Labyrinth wooden ammo box
        "67adf4db515e3dd542077a1d", // Labyrinth wooden crate
        "67adf4eb110ba15da90c6413", // Labyrinth grenade box
        "67adf5f7adc1f43b0702b826"  // Labyrinth technical supply crate
    ];

    internal static int Register(LocationTable locations, BotTable bots, PmcConfig pmcConfig,
        CatalogSnapshotCoordinator coordinator, ModConfig config)
    {
        // GetCaseSnapshot enforces the existing startup gate. Never open it from an earlier loot hook,
        // and never access the Mixed view before the final barrier has published its price.
        var availableCases = CaseContracts.Templates
            .Where(id => coordinator.GetCaseSnapshot(id).OpeningEnabled)
            .Select(id => (MongoId)id).ToArray();
        var pmcBlacklist = pmcConfig.GlobalLootBlacklist
            ?? throw new InvalidOperationException("SPT PMC loot blacklist is unavailable; crate-only cases cannot be enforced.");

        // Native PMC loot is also synthesized from registered templates, independently of bot pools.
        // A PMC-only blacklist preserves crates, trader stock, keys, rewards and player inventory.
        foreach (var id in CaseIds)
            if (!pmcBlacklist.Contains(id)) pmcBlacklist.Add(id);
        foreach (var bot in bots.Types.Values)
        {
            var pools = bot?.BotInventory?.Items;
            if (pools is null) continue;
            foreach (var pool in new[] { pools.Backpack, pools.Pockets, pools.TacticalVest, pools.SecuredContainer, pools.SpecialLoot })
            {
                if (pool is null) continue;
                foreach (var id in CaseIds) pool.Remove(id);
            }
        }

        var registeredMaps = 0;
        foreach (var location in locations.GetDictionary().Values)
        {
            if (location?.StaticLoot is not { } lazy) continue;
            // SPT AddTransformer invalidates any previously cached value without forcing deserialization.
            lazy.AddTransformer(dictionary => Inject(dictionary, availableCases, config.CaseLootWeightPercent));
            registeredMaps++;
        }
        return registeredMaps;
    }

    private static Dictionary<MongoId, StaticLootDetails>? Inject(
        Dictionary<MongoId, StaticLootDetails>? dictionary, MongoId[] availableCases, double weightPercent)
    {
        if (dictionary is null) return null;
        foreach (var (container, details) in dictionary)
        {
            if (details?.ItemDistribution is null) continue;
            var original = details.ItemDistribution.ToList();
            var hasCases = original.Any(entry => entry is not null && CaseIds.Contains(entry.Tpl));
            if (!hasCases && !CrateIds.Contains(container)) continue;

            // Replace only our entries. This also removes stale/disabled cases and misplaced corpse loot,
            // and prevents weight growth when a cached/shared dictionary is transformed repeatedly.
            var distribution = original.Where(entry => entry is null || !CaseIds.Contains(entry.Tpl)).ToList();
            if (CrateIds.Contains(container) && availableCases.Length > 0 &&
                distribution.All(entry => entry is not null &&
                    float.IsFinite(entry.RelativeProbability ?? 0f) && (entry.RelativeProbability ?? 0f) >= 0f))
            {
                var total = distribution.Sum(entry => (double)(entry.RelativeProbability ?? 0f));
                var combinedWeight = ContrabandKeyLootWeighting.ComputeAdditionalWeight(total, weightPercent);
                var perCase = (float)((combinedWeight ?? 0d) / availableCases.Length);
                if (float.IsFinite(perCase) && perCase > 0f)
                {
                    distribution.AddRange(availableCases.Select(id => new ItemDistribution
                    {
                        Tpl = id,
                        RelativeProbability = perCase
                    }));
                }
            }
            if (hasCases || distribution.Count != original.Count) details.ItemDistribution = distribution;
        }
        return dictionary;
    }
}
