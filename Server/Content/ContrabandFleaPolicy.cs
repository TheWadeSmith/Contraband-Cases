using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace ContrabandCases.Server.Content;

/// <summary>
/// Cases are supplied by Mechanic/raid loot; keys are raid-earned. These six
/// templates must not enter the generated player market, even with BSG bans off.
/// This policy changes only owned item exclusions and cached owned offers.
/// </summary>
internal static class ContrabandFleaPolicy
{
    private static readonly HashSet<MongoId> OwnedTemplates =
        CaseContracts.Templates.Append(ModConstants.KeyTemplateId).Select(id => (MongoId)id).ToHashSet();

    internal static void RegisterExclusions(RagfairConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var custom = config.Dynamic?.Blacklist?.Custom;
        var barter = config.Dynamic?.Barter?.ItemTplBlacklist;
        if (custom is null || barter is null)
            throw new InvalidOperationException("[Contraband Cases] Native flea exclusions are unavailable; " +
                "startup stopped to protect case pricing and raid-earned key availability.");

        // Unlike CanSellOnRagfair, the native custom list is still enforced when a
        // compatibility mod disables the global BSG blacklist or restores item flags.
        custom.UnionWith(OwnedTemplates);
        barter.UnionWith(OwnedTemplates);
    }

    internal static int FinalizeStartup(TemplateTable templates, TradersTable traders,
        RagfairConfig config, RagfairOfferHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        var handbook = ContrabandContentDefinitions.GetRegisteredHandbookPrices(templates);
        var assort = ContrabandContentDefinitions.RequireMechanicAssort(traders);
        var offers = holder.GetOffers();
        var updates = offers.Where(IsOwnedMechanicOffer).Select(offer =>
            (Offer: offer, Price: RequireMechanicPrice(assort, offer))).ToArray();

        RegisterExclusions(config);
        foreach (var id in OwnedTemplates)
        {
            templates.Items[id].Properties!.CanSellOnRagfair = false;
            templates.Items[id].Properties!.CanRequireOnRagfair = false;
        }

        // Native initial offers precede the final catalog hook. Repair existing
        // Mechanic listings in place: preserve IDs/stock, separate handbook resale
        // basis from the actual RUB purchase requirement, and leave other assorts alone.
        foreach (var (offer, price) in updates)
        {
            offer.Requirements = [new OfferRequirement { TemplateId = Money.ROUBLES, Count = price }];
            offer.RequirementsCost = price;
            offer.SummaryCost = price;
            offer.ItemsCost = handbook[offer.Items![0].Template];
        }

        var removed = 0;
        foreach (var offer in offers.Where(offer => offer.CreatedBy == OfferCreator.FakePlayer &&
                     ((offer.Items?.Any(item => OwnedTemplates.Contains(item.Template)) ?? false) ||
                      (offer.Requirements?.Any(requirement => OwnedTemplates.Contains(requirement.TemplateId)) ?? false))))
        {
            // Native removal also clears the expired/index caches. Merely hiding
            // offers allows expired-offer regeneration to bypass the normal blacklist.
            holder.RemoveOffer(offer.Id);
            removed++;
        }

        // The themed case assorts were added after initial trader flea generation.
        // Let SPT publish them on its normal next refresh, without rebuilding any
        // unrelated trader's listings or changing global trader-flea preferences.
        if (traders[Traders.MECHANIC].Base is { } mechanic)
            mechanic.RefreshTraderRagfairOffers = true;
        return removed;
    }

    private static bool IsOwnedMechanicOffer(RagfairOffer offer)
    {
        if (offer.CreatedBy != OfferCreator.Trader || offer.User.Id != Traders.MECHANIC ||
            offer.Items?.FirstOrDefault() is not { } root || !CaseContracts.IsCase(root.Template.ToString()))
            return false;

        return root.Id == (MongoId)CaseContracts.AssortId(root.Template.ToString()) && offer.Root == root.Id;
    }

    private static double RequireMechanicPrice(TraderAssort assort, RagfairOffer offer)
    {
        var root = offer.Items![0];
        if (offer.Items.Count != 1 || offer.SellInOnePiece is true ||
            !assort.Items.Any(item => item.Id == root.Id && item.Template == root.Template) ||
            !assort.BarterScheme.TryGetValue(root.Id, out var schemes) || schemes.Count != 1 ||
            schemes[0].Count != 1 || schemes[0][0].Template != Money.ROUBLES ||
            schemes[0][0].Count is not { } price || !double.IsFinite(price) || price <= 0)
            throw new InvalidOperationException("[Contraband Cases] A cached owned Mechanic case listing " +
                "does not match its finalized RUB assort; startup stopped to prevent a stale purchase price.");

        return price;
    }
}
