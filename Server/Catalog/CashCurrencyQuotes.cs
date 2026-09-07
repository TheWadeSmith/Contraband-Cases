using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace ContrabandCases.Server.Catalog;

/// <summary>Frozen published-trader estimates; never an unconditional player sale guarantee.</summary>
internal static class CashCurrencyQuotes
{
    internal static CargoCatalogSnapshot Freeze(IReadOnlyDictionary<MongoId, TemplateItem> items, TradersTable traders,
        IReadOnlyDictionary<string, double> handbookPrices)
    {
        TemplateItem? Find(string id) => items.GetValueOrDefault((MongoId)id);
        if (!traders.TryGetValue(Traders.THERAPIST, out var therapist) ||
            therapist?.Base?.Currency != CurrencyType.RUB || therapist.Base.UnlockedByDefault != true)
            throw new CargoCatalogValidationException("Cash Cache needs an available rouble-paying Therapist.");
        var ancestry = new HashSet<MongoId>();
        var cursor = (MongoId)CashPayouts.Bitcoin;
        for (var depth = 0; cursor.ToString() != "54009119af1c881c07000029"; depth++)
        {
            if (depth >= 64 || !ancestry.Add(cursor) || !items.TryGetValue(cursor, out var item) || item is null)
                throw new CargoCatalogValidationException("Bitcoin template ancestry is missing or cyclic.");
            cursor = item.Parent;
        }
        bool Matches(ItemBuyData? rule)
        {
            if (rule?.IdList is null || rule.Category is null)
                throw new CargoCatalogValidationException("Therapist purchase rules are incomplete.");
            return rule.IdList.Contains((MongoId)CashPayouts.Bitcoin) || rule.Category.Overlaps(ancestry);
        }
        if (!Matches(therapist.Base.ItemsBuy) || Matches(therapist.Base.ItemsBuyProhibited))
            throw new CargoCatalogValidationException("Therapist does not accept physical Bitcoin in the finalized trader rules.");
        var coefficient = therapist.Base.LoyaltyLevels?.FirstOrDefault()?.BuyPriceCoefficient;
        if (coefficient is null || !double.IsFinite(coefficient.Value) || coefficient < 0 || coefficient >= 100 ||
            !handbookPrices.TryGetValue(CashPayouts.Bitcoin, out var bitcoinBase) ||
            !double.IsFinite(bitcoinBase) || bitcoinBase <= 0 || bitcoinBase > 100_000_000)
            throw new CargoCatalogValidationException("Bitcoin standard trader valuation is unavailable.");
        var bitcoinSale = decimal.Floor((decimal)bitcoinBase * (100m - (decimal)coefficient.Value) / 100m);
        // GP is barter currency. Do not invent a Therapist cash-out or use a
        // dynamic flea quote for its stable, explicitly labelled reference value.
        if (!handbookPrices.TryGetValue(CashPayouts.GpCoin, out var gpReference) ||
            !double.IsFinite(gpReference) || gpReference <= 0 || gpReference > 100_000_000)
            throw new CargoCatalogValidationException("GP Coin handbook barter valuation is unavailable.");
        return CashPayoutCatalog.Build(Find, PurchaseRate(traders, CashPayouts.Dollars),
            PurchaseRate(traders, CashPayouts.Euros), bitcoinSale, (decimal)gpReference);
    }

    private static decimal PurchaseRate(TradersTable traders, string currency)
    {
        var prices = new List<decimal>();
        foreach (var trader in traders.Values)
        {
            if (trader?.Assort?.Items is null || trader.Assort.BarterScheme is null) continue;
            foreach (var item in trader.Assort.Items.Where(item => item is not null && item.Template.ToString() == currency && item.ParentId == "hideout"))
            {
                if (!trader.Assort.BarterScheme.TryGetValue(item.Id, out var options) || options is null) continue;
                foreach (var option in options)
                {
                    if (option is null || option.Count != 1 || option[0] is null || option[0].Template.ToString() != CashPayouts.Roubles) continue;
                    var cost = option[0].Count;
                    if (cost is > 0 and < 100_000 && double.IsFinite(cost.Value)) prices.Add((decimal)cost.Value);
                }
            }
        }
        return prices.Count > 0 ? prices.Min() : throw new CargoCatalogValidationException(
            "No direct published rouble purchase offer exists for " + currency + ".");
    }
}
