using ContrabandCases.Server.Configuration;
using ContrabandCases.Server.Content;
using ContrabandCases.Shared;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace ContrabandCases.Server.Loot;

/// <summary>
/// Makes the BR-12 Relay Key find-only. Introduced in 0.3.18 when the key stopped being sold by Mechanic
/// (see ContrabandContentDefinitions/ContrabandContentLoader): this class is what actually puts it back
/// into the game, by adding it to raid loot instead.
///
/// Rather than hand-picking specific static containers or bot types (which would mean hardcoding dozens of
/// template ids and re-guessing which ones "feel right"), this targets exactly the static loot containers
/// and bot loot pools that SPT's own database already places at least one other vanilla item from the
/// "Keys" category into (<see cref="ContrabandContentDefinitions.KeyParentId"/> -- the same category the
/// BR-12 Relay Key itself is registered under). That mirrors vanilla's own placement decisions instead of
/// guessing at new ones, and it naturally spans every map and every bot role that already spawns keys.
///
/// The extra weight given to the key in each targeted pool is proportional to that pool's own existing
/// total weight (<see cref="ModConfig.KeyLootWeightPercent"/> percent of it), so the key's relative rarity
/// stays roughly consistent even though different containers and bot pools use wildly different weight
/// scales. See <see cref="ContrabandKeyLootWeighting.ComputeAdditionalWeight"/>.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 2)]
public sealed class ContrabandKeyLootInjector(
    ISptLogger<ContrabandKeyLootInjector> logger,
    LocationTable locations,
    BotTable bots,
    TemplateTable templates,
    ContrabandContentState contentState) : IOnLoad
{
    private static readonly MongoId KeyTemplateId = (MongoId)ModConstants.KeyTemplateId;
    private static readonly MongoId KeysCategoryParentId = (MongoId)ContrabandContentDefinitions.KeyParentId;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = contentState.RequireConfig();

        var mapsWithLazyStaticLoot = InjectIntoStaticLoot(config);
        var injectedBotPoolCount = InjectIntoBotLoot(config);

        logger.Success(
            $"[{ModConstants.ModName}] Registered lazy static-loot key injection across " +
            $"{mapsWithLazyStaticLoot} map(s); added the BR-12 Relay Key to {injectedBotPoolCount} " +
            "bot loot pool(s) that already carry a vanilla key");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Static loot is lazily deserialized per map (see <c>Location.StaticLoot</c>'s <c>LazyLoad</c>
    /// wrapper), so rather than reading each map's data now (which would force-load every map up front,
    /// something the base game deliberately avoids), this registers a transformer that runs whenever that
    /// map's static loot is actually built -- lazily, and every time it's rebuilt, regardless of whether the
    /// LazyLoad instance caches its value. Returns the number of maps a transformer was registered against
    /// (not the number of containers actually modified, which isn't known until each map's data is loaded).
    /// </summary>
    private int InjectIntoStaticLoot(ModConfig config)
    {
        var mapsWithLazyStaticLoot = 0;
        foreach (var location in locations.GetDictionary().Values)
        {
            var lazyStaticLoot = location?.StaticLoot;
            if (lazyStaticLoot is null)
            {
                continue;
            }

            mapsWithLazyStaticLoot++;
            lazyStaticLoot.AddTransformer(dictionary => InjectStaticLootDictionary(dictionary, config));
        }

        return mapsWithLazyStaticLoot;
    }

    private Dictionary<MongoId, StaticLootDetails>? InjectStaticLootDictionary(
        Dictionary<MongoId, StaticLootDetails>? dictionary,
        ModConfig config)
    {
        if (dictionary is null)
        {
            return dictionary;
        }

        foreach (var details in dictionary.Values)
        {
            if (details?.ItemDistribution is null)
            {
                continue;
            }

            var itemDistribution = details.ItemDistribution.ToList();
            if (itemDistribution.Any(entry => entry.Tpl == KeyTemplateId))
            {
                continue; // Idempotency guard in case this transformer ever runs more than once.
            }

            if (!itemDistribution.Any(entry => IsKeyCategoryItem(entry.Tpl)))
            {
                continue; // Only add the key where vanilla already places some other key.
            }

            var existingWeightSum = itemDistribution.Sum(entry => (double)(entry.RelativeProbability ?? 0f));
            var additionalWeight = ContrabandKeyLootWeighting.ComputeAdditionalWeight(
                existingWeightSum,
                config.KeyLootWeightPercent);
            if (additionalWeight is not double weight)
            {
                continue;
            }

            itemDistribution.Add(new ItemDistribution
            {
                Tpl = KeyTemplateId,
                RelativeProbability = (float)weight
            });
            details.ItemDistribution = itemDistribution;
        }

        return dictionary;
    }

    /// <summary>
    /// Unlike static loot, <see cref="BotTable.Types"/> is not LazyLoad-wrapped -- it's fully loaded by the
    /// time any mod's OnLoad hooks run (the same assumption <c>ContrabandContentLoader</c> already relies
    /// on for <c>TemplateTable</c>/<c>TradersTable</c>) -- so bot pools are mutated directly, once, here.
    /// Returns the number of pools actually modified.
    /// </summary>
    private int InjectIntoBotLoot(ModConfig config)
    {
        var injectedPoolCount = 0;
        foreach (var botType in bots.Types.Values)
        {
            var itemPools = botType?.BotInventory?.Items;
            if (itemPools is null)
            {
                continue;
            }

            injectedPoolCount += InjectPool(itemPools.Backpack, config);
            injectedPoolCount += InjectPool(itemPools.Pockets, config);
            injectedPoolCount += InjectPool(itemPools.TacticalVest, config);
            injectedPoolCount += InjectPool(itemPools.SecuredContainer, config);
            injectedPoolCount += InjectPool(itemPools.SpecialLoot, config);
        }

        return injectedPoolCount;
    }

    private int InjectPool(Dictionary<MongoId, double>? pool, ModConfig config)
    {
        if (pool is null || pool.ContainsKey(KeyTemplateId))
        {
            return 0;
        }

        if (!pool.Keys.Any(IsKeyCategoryItem))
        {
            return 0;
        }

        var additionalWeight = ContrabandKeyLootWeighting.ComputeAdditionalWeight(
            pool.Values.Sum(),
            config.KeyLootWeightPercent);
        if (additionalWeight is not double weight)
        {
            return 0;
        }

        pool[KeyTemplateId] = weight;
        return 1;
    }

    private bool IsKeyCategoryItem(MongoId templateId) =>
        templates.Items.TryGetValue(templateId, out var item) && item?.Parent == KeysCategoryParentId;
}

/// <summary>
/// Pure weighting math for <see cref="ContrabandKeyLootInjector"/>, split out so it can be unit tested
/// without constructing any of SPT's table types.
/// </summary>
public static class ContrabandKeyLootWeighting
{
    /// <summary>
    /// Computes the weight to give the BR-12 Relay Key inside a loot pool whose other entries already sum
    /// to <paramref name="existingWeightSum"/>, as <paramref name="weightPercent"/> percent of that sum.
    /// Returns null when there's nothing sensible to add: no existing weight to scale from, a non-finite
    /// input, or a non-positive result.
    /// </summary>
    public static double? ComputeAdditionalWeight(double existingWeightSum, double weightPercent)
    {
        if (!double.IsFinite(existingWeightSum) || existingWeightSum <= 0d)
        {
            return null;
        }

        if (!double.IsFinite(weightPercent) || weightPercent <= 0d)
        {
            return null;
        }

        var weight = existingWeightSum * (weightPercent / 100d);
        if (!double.IsFinite(weight) || weight <= 0d)
        {
            return null;
        }

        return weight;
    }
}
