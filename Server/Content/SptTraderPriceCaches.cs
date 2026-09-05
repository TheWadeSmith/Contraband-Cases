using System.Reflection;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Ragfair;

namespace ContrabandCases.Server.Content;

/// <summary>
/// Startup-only adapter for SPT 4.1's two cached trader-price sources. Bind before
/// finalizing prices, then publish after every case has its finalized handbook value.
/// </summary>
internal sealed class SptTraderPriceCaches
{
    private readonly Dictionary<MongoId, double> _handbook;
    private readonly Dictionary<MongoId, double> _trader;

    private SptTraderPriceCaches(Dictionary<MongoId, double> handbook, Dictionary<MongoId, double> trader)
    {
        _handbook = handbook;
        _trader = trader;
    }

    internal static SptTraderPriceCaches Bind(HandbookHelper handbook, RagfairPriceService ragfair)
    {
        ArgumentNullException.ThrowIfNull(handbook);
        ArgumentNullException.ThrowIfNull(ragfair);

        // SPT exposes no handbook-cache update/invalidation API. Access only its
        // price dictionary through the lazy property (also respects helper overrides).
        // Hydrate BEFORE finalization: native hydration can apply global handbook
        // overrides. Resetting the whole cache afterward would repeat those overrides
        // and discard other mods' cached values. Guard the pinned SPT shape explicitly.
        var cacheProperty = typeof(HandbookHelper).GetProperty("HandbookPriceCache",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var itemsProperty = cacheProperty?.PropertyType.GetProperty("Items");
        var pricesProperty = itemsProperty?.PropertyType.GetProperty("ById");
        if (cacheProperty is null || itemsProperty is null ||
            pricesProperty?.PropertyType != typeof(Dictionary<MongoId, double>))
            throw UnsupportedCache();

        var cache = cacheProperty.GetValue(handbook) ?? throw UnsupportedCache();
        var items = itemsProperty.GetValue(cache) ?? throw UnsupportedCache();
        if (pricesProperty.GetValue(items) is not Dictionary<MongoId, double> handbookPrices)
            throw UnsupportedCache();

        // The native API returns the same mutable dictionary used by TraderController.
        var traderPrices = ragfair.GetAllStaticPrices() ?? throw UnsupportedCache();
        return new SptTraderPriceCaches(handbookPrices, traderPrices);
    }

    internal void Publish(TemplateTable templates)
    {
        // Validate every owned entry before updating either cache. Never recalculate
        // prices here: the handbook already includes configured case/key sell targets.
        var prices = ContrabandContentDefinitions.GetRegisteredHandbookPrices(templates);
        foreach (var (id, price) in prices)
        {
            _handbook[id] = price;
            _trader[id] = price;
        }
    }

    private static InvalidOperationException UnsupportedCache() => new(
        "[Contraband Cases] SPT's trader-price cache is incompatible with this mod's SPT 4.1 adapter. " +
        "Startup stopped to avoid publishing incorrect case resale prices.");
}
