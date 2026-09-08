using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace ContrabandCases.Server.Catalog;

/// <summary>
/// Baseline, handbook-based RUB resale of complete reward roots. Deliberately
/// separate from rarity/reference pricing. No flea speculation or profile bonus.
/// An unsupported root makes the whole estimate unavailable, not a partial total.
/// </summary>
internal sealed class CargoTraderResale(
    Func<string, TemplateItem?> findTemplate,
    IReadOnlyDictionary<string, double> prices,
    IEnumerable<TraderBase> traders)
{
    internal long? Estimate(RewardForest forest)
    {
        decimal total = 0;
        foreach (var root in forest.Roots)
        {
            if (root.TemplateId == CashPayouts.Roubles)
            {
                total += root.StackCount;
                if (total > CargoLotEvaluator.MaximumLotValue) return null;
                continue;
            }
            var ancestry = new HashSet<MongoId>();
            var cursor = root.TemplateId;
            for (var depth = 0; cursor != "54009119af1c881c07000029"; depth++)
            {
                if (depth >= 64 || !ancestry.Add((MongoId)cursor) || findTemplate(cursor) is not { } template)
                    return null;
                if (depth == 0 && template.Properties?.IsUnsaleable == true) return null;
                cursor = template.Parent.ToString();
            }
            decimal rootBase = 0;
            foreach (var node in forest.Nodes.Where(node => node.TreeRootPath == root.TreeRootPath))
            {
                if (node.InternalLocation is not null || // Filled grid containers need their own sale rules.
                    !prices.TryGetValue(node.TemplateId, out var value) || !double.IsFinite(value) ||
                    value <= 0 || value > (double)CargoLotEvaluator.MaximumUnitValue)
                    return null;
                if (node.StableState is { } state &&
                    (state.Durability != state.MaximumDurability || state.ResourceValue != state.MaximumResourceValue))
                    return null; // Do not invent condition-adjusted trader quotes.
                rootBase += (decimal)value * node.StackCount;
            }
            decimal? best = null;
            foreach (var trader in traders)
            {
                if (trader.Currency != CurrencyType.RUB || trader.UnlockedByDefault != true ||
                    trader.Id == Traders.FENCE || trader.ItemsBuy is not { IdList: not null, Category: not null } buy ||
                    trader.ItemsBuyProhibited is not { IdList: not null, Category: not null } prohibited)
                    continue;
                bool Matches(ItemBuyData rule) => rule.IdList!.Contains((MongoId)root.TemplateId) || rule.Category!.Overlaps(ancestry);
                var coefficient = trader.LoyaltyLevels?.FirstOrDefault()?.BuyPriceCoefficient;
                if (!Matches(buy) || Matches(prohibited) || coefficient is null ||
                    !double.IsFinite(coefficient.Value) || coefficient < 0 || coefficient >= 100) continue;
                var quote = decimal.Floor(rootBase * (100m - (decimal)coefficient.Value) / 100m);
                best = best is null ? quote : Math.Max(best.Value, quote);
            }
            if (best is null) return null;
            total += best.Value;
            if (total > CargoLotEvaluator.MaximumLotValue) return null;
        }
        return checked((long)total);
    }
}
