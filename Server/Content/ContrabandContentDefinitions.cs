using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Configuration;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Economy;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace ContrabandCases.Server.Content;

public sealed record TraderOfferDefinition(
    MongoId AssortId,
    MongoId TemplateId,
    long PriceRoubles,
    int Stock,
    int BuyRestriction,
    int LoyaltyLevel,
    bool FoundInRaid);

public static class ContrabandContentDefinitions
{
    private const long ProvisionalTotalPrice = 1_000;
    private const long MaximumExactDoubleInteger = 9_007_199_254_740_992;

    /// <summary>
    /// Therapist's live "buy_price_coef" (flat across all her loyalty levels, confirmed against the shipped
    /// SPT trader database at authoring time: SPT_Data/database/traders/54cb57776803fa99248b456e/base.json).
    /// The client computes what it pays a player for an item as roughly
    /// handbookPrice * (100 - buyPriceCoef) / 100; this constant is the "37" from that data. If SPT or
    /// another mod ever changes Therapist's coefficient, the derived handbook price below will drift and
    /// this constant needs to be re-checked against her live base.json.
    /// </summary>
    private const double TherapistBuyPriceCoefficientPercent = 37d;

    /// <summary>
    /// Fixed internal registration handbook/credits price for the BR-12 Relay Key, introduced in 0.3.18
    /// when the key stopped being sold by Mechanic and became find-only instead. SPT still requires every
    /// registered item template to carry a positive handbook price, so this constant satisfies that
    /// requirement; it has no gameplay-visible effect on its own, since the key is not listed on the flea
    /// market (see <see cref="CommonProperties"/>'s CanSellOnRagfair = false) and is not offered by any
    /// trader unless <see cref="ModConfig.TherapistSellPriceKey"/> is configured, in which case
    /// <see cref="ApplyKeyRegistrationPrice"/> overrides this value with whatever hits that target
    /// instead. Chosen to match the key's long-standing effective worth from before 0.3.18 (its former
    /// fixedKeyPrice default), so nothing about the key's implied value actually changes -- only how a
    /// player can acquire one.
    /// </summary>
    private const long KeyRegistrationHandbookPriceRoubles = 65_000;

    public const string CaseCloneTemplateId = "64897ffc3656831810043165";
    public const string CaseParentId = "62f109593b54472778797866";
    public const string CaseHandbookParentId = "5b5f6fa186f77409407a7eb7";
    public const string KeyCloneTemplateId = "593962ca86f774068014d9af";
    public const string KeyParentId = "5c99f98d86f7745c314214b3";
    public const string KeyHandbookParentId = "5c518ec986f7743b68682ce2";
    public const string CaseDescription =
        "A sealed Northline Transit escrow case. Opening consumes this case and one BR-12 Relay Key, " +
        "then presents three sequential offers with Lock/Burn decisions on the first two. The selected " +
        "entitlement can be Claimed or " +
        "Relayed; Relay consumes one additional BR-12 Relay Key.";
    private const string KeyDescription =
        "Each key is universal and single-use: one opens any BR-12 case, while a separate key stakes its " +
        "selected entitlement in Relay. Found in raid only -- Mechanic no longer sells it. It has no " +
        "real-money value.";

    public static NewItemFromCloneDetails CreateCaseCloneDetails(long handbookPrice,
        string caseTemplateId = ModConstants.CaseTemplateId) => new()
        {
            ItemTplToClone = CaseCloneTemplateId,
            ParentId = CaseParentId,
            NewId = CaseContracts.Require(caseTemplateId),
            NewItemName = caseTemplateId == ModConstants.CaseTemplateId ? "br12_relay_case" : "br12_case_" + caseTemplateId,
            FleaPriceRoubles = PositivePrice(handbookPrice),
            HandbookPriceRoubles = PositivePrice(handbookPrice),
            HandbookParentId = CaseHandbookParentId,
            AddToHandbook = true,
            AddToFleaPriceDb = true,
            AddToWeaponShelf = false,
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new()
                {
                    Name = CaseContracts.Name(caseTemplateId),
                    ShortName = CaseContracts.ShortName(caseTemplateId),
                    Description = DescribeCase(caseTemplateId)
                }
            },
            OverrideProperties = CaseProperties(caseTemplateId, handbookPrice)
        };

    private static string DescribeCase(string template) => template == ModConstants.CaseTemplateId
        ? CaseDescription : template == CaseContracts.CashCache
            ? CaseContracts.Description(template) : CaseContracts.Description(template) + " " + CaseDescription;

    private static TemplateItemProperties CaseProperties(string template, long price)
    {
        var properties = CommonProperties(CaseContracts.Name(template), CaseContracts.ShortName(template),
            DescribeCase(template), ModConstants.CaseBundleKey, price, 3, 2);
        if (template != ModConstants.CaseTemplateId)
            properties.BackgroundColor = template switch
            {
                CaseContracts.Operations => "green",
                CaseContracts.Relics => "violet",
                CaseContracts.BlackSite => "red",
                CaseContracts.CashCache => "yellow",
                _ => throw new ArgumentException("Unknown case template.", nameof(template))
            };
        return properties;
    }

    public static IReadOnlyList<TraderOfferDefinition> PublishThemedCases(
        TemplateTable templates, TraderAssort assort, ModConfig config,
        CatalogSnapshotCoordinator coordinator)
    {
        var views = CaseContracts.Templates.Where(t => t != ModConstants.CaseTemplateId)
            .Select(coordinator.GetCaseSnapshot).ToArray();
        // Validate all sinks and offers before publishing any themed case.
        var sinks = views.Select(view => (
            View: view,
            Properties: RequireTemplateProperties(templates, view.CaseTemplateId, CaseParentId, "themed case"),
            Handbook: RequireHandbookEntry(templates, view.CaseTemplateId, CaseHandbookParentId, "themed case"))).ToArray();
        var offers = views.Where(view => view.OpeningEnabled).Select(view =>
        {
            var price = view.CasePrice ?? throw new InvalidOperationException("The themed case has no finalized price.");
            _ = ToExactDoublePrice(price, "themed case");
            return new TraderOfferDefinition(CaseContracts.AssortId(view.CaseTemplateId), view.CaseTemplateId,
                price, config.CaseStock, config.CaseStock, 1, false);
        }).ToArray();
        EnsureMechanicOfferIdsAvailable(assort, offers);
        ArgumentNullException.ThrowIfNull(templates.Prices);
        foreach (var sink in sinks.Where(sink => sink.View.OpeningEnabled))
        {
            sink.Properties.CreditsPrice = sink.View.CasePrice!.Value;
            sink.Handbook.Price = sink.View.CasePrice.Value;
            templates.Prices[(MongoId)sink.View.CaseTemplateId] = sink.View.CasePrice.Value;
        }
        ApplyMechanicOffers(assort, offers);
        return offers;
    }

    /// <summary>
    /// Registers the BR-12 Relay Key with its fixed internal handbook price
    /// (<see cref="KeyRegistrationHandbookPriceRoubles"/>). Unlike the case, the key's price is never
    /// deferred/patched against the catalog snapshot -- it isn't sold anywhere, so there is nothing for a
    /// catalog-driven price to finalize. <see cref="ApplyKeyRegistrationPrice"/> may still override this
    /// registered value shortly after, if <see cref="ModConfig.TherapistSellPriceKey"/> is configured.
    /// </summary>
    public static NewItemFromCloneDetails CreateKeyCloneDetails()
    {
        var properties = CommonProperties(
            "BR-12 Relay Key",
            "BR-12 Key",
            KeyDescription,
            ModConstants.KeyBundleKey,
            KeyRegistrationHandbookPriceRoubles,
            width: 1,
            height: 1);
        properties.MaxUsages = 1d;

        return new NewItemFromCloneDetails
        {
            ItemTplToClone = KeyCloneTemplateId,
            ParentId = KeyParentId,
            NewId = ModConstants.KeyTemplateId,
            NewItemName = "br12_relay_key",
            FleaPriceRoubles = PositivePrice(KeyRegistrationHandbookPriceRoubles),
            HandbookPriceRoubles = PositivePrice(KeyRegistrationHandbookPriceRoubles),
            HandbookParentId = KeyHandbookParentId,
            AddToHandbook = true,
            AddToFleaPriceDb = true,
            AddToWeaponShelf = false,
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new()
                {
                    Name = "BR-12 Relay Key",
                    ShortName = "BR-12 Key",
                    Description = KeyDescription
                }
            },
            OverrideProperties = properties
        };
    }

    public static TicketPrices CalculatePrices(CargoCatalogSnapshot catalog, ModConfig config)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(config);
        if (catalog.Lots.Count == 0)
        {
            throw new CargoCatalogValidationException(
                "Manifest ticket pricing requires at least one validated cargo lot.");
        }

        if (config.FixedCasePrice is long fixedCase)
        {
            return FixedPrices(fixedCase);
        }

        return ManifestCatalogEconomy.CalculateAutomaticPrices(catalog);
    }

    public static TicketPrices CalculateRegistrationPrices(ModConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var prices = config.FixedCasePrice is long fixedCase
            ? FixedPrices(fixedCase)
            : new TicketPrices(ProvisionalTotalPrice);
        ValidatePriceStructure(prices);
        _ = ToExactDoublePrice(prices.CasePrice, "case");
        return prices;
    }

    public static IReadOnlyList<TraderOfferDefinition> CreateMechanicOffers(TicketPrices prices, ModConfig config)
    {
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(config);
        ValidatePriceStructure(prices);
        _ = ToExactDoublePrice(prices.CasePrice, "case");
        return
        [
            new TraderOfferDefinition(
                ModConstants.MechanicCaseAssortRootId,
                ModConstants.CaseTemplateId,
                prices.CasePrice,
                config.CaseStock,
                config.CaseStock,
                1,
                false)
        ];
    }

    public static void EnsureMechanicOfferIdsAvailable(
        TraderAssort assort,
        IEnumerable<TraderOfferDefinition> offers)
    {
        ArgumentNullException.ThrowIfNull(assort);
        ArgumentNullException.ThrowIfNull(offers);
        var offerList = offers.ToList();
        if (offerList.Select(offer => offer.AssortId).Distinct().Count() != offerList.Count)
        {
            throw new InvalidOperationException("Mechanic offer definitions contain a duplicate assort id.");
        }

        foreach (var offer in offerList)
        {
            if (assort.Items.Any(item => item.Id == offer.AssortId) ||
                assort.BarterScheme.ContainsKey(offer.AssortId) ||
                assort.LoyalLevelItems.ContainsKey(offer.AssortId))
            {
                throw new InvalidOperationException($"Mechanic assort id '{offer.AssortId}' is already in use.");
            }
        }
    }

    public static void ApplyMechanicOffers(
        TraderAssort assort,
        IEnumerable<TraderOfferDefinition> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);
        var offerList = offers.ToList();
        EnsureMechanicOfferIdsAvailable(assort, offerList);
        foreach (var offer in offerList)
        {
            assort.Items.Add(new Item
            {
                Id = offer.AssortId,
                Template = offer.TemplateId,
                ParentId = "hideout",
                SlotId = "hideout",
                Upd = new Upd
                {
                    UnlimitedCount = false,
                    StackObjectsCount = offer.Stock,
                    BuyRestrictionMax = offer.BuyRestriction,
                    BuyRestrictionCurrent = 0,
                    SpawnedInSession = offer.FoundInRaid
                }
            });
            assort.BarterScheme.Add(offer.AssortId,
            [
                [new BarterScheme { Count = offer.PriceRoubles, Template = Money.ROUBLES }]
            ]);
            assort.LoyalLevelItems.Add(offer.AssortId, offer.LoyaltyLevel);
        }
    }

    public static TraderAssort RequireMechanicAssort(TradersTable traders)
    {
        ArgumentNullException.ThrowIfNull(traders);
        if (!traders.TryGetValue(Traders.MECHANIC, out var mechanic) || mechanic?.Assort is null)
        {
            throw new InvalidOperationException("Mechanic is missing from the trader database.");
        }

        return mechanic.Assort;
    }

    /// <summary>
    /// Computes the handbook/registration price required so that a trader who pays
    /// handbookPrice * (100 - buyPriceCoefficientPercent) / 100 for an item ends up paying at least
    /// <paramref name="sellPriceTarget"/> roubles. Rounds up so the realized sell price never falls short of
    /// the target due to integer rounding on the client side.
    /// </summary>
    public static long ComputeHandbookPriceForTraderSellTarget(
        long sellPriceTarget,
        double buyPriceCoefficientPercent)
    {
        if (sellPriceTarget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sellPriceTarget), "Sell price target must be positive.");
        }

        if (!double.IsFinite(buyPriceCoefficientPercent) ||
            buyPriceCoefficientPercent < 0d ||
            buyPriceCoefficientPercent >= 100d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(buyPriceCoefficientPercent),
                "Buy price coefficient must be a finite percentage in [0, 100).");
        }

        var retainedFraction = (100d - buyPriceCoefficientPercent) / 100d;
        var requiredHandbookPrice = Math.Ceiling(sellPriceTarget / retainedFraction);
        if (requiredHandbookPrice > MaximumExactDoubleInteger)
        {
            throw new InvalidOperationException(
                "The computed handbook price required to hit the requested trader sell target is too large.");
        }

        return checked((long)requiredHandbookPrice);
    }

    /// <summary>
    /// Grants Therapist explicit permission to buy the case/key even though their handbook category isn't
    /// one of the categories she normally accepts (confirmed against her live base.json: neither
    /// <see cref="CaseHandbookParentId"/> nor <see cref="KeyHandbookParentId"/> appear in her "items_buy"
    /// category list). Without this, configuring a Therapist sell price has no visible effect in-game because
    /// her sell screen would never offer to buy the item in the first place.
    /// All cases are sellable at the native handbook-based rate by default.
    /// The key remains find-only and uses its existing optional sell setting.
    /// </summary>
    public static void EnsureTherapistBuysConfiguredItems(TradersTable traders, ModConfig config)
    {
        ArgumentNullException.ThrowIfNull(traders);
        ArgumentNullException.ThrowIfNull(config);
        if (!traders.TryGetValue(Traders.THERAPIST, out var therapist) || therapist?.Base?.ItemsBuy is null)
        {
            throw new InvalidOperationException("Therapist is missing from the trader database.");
        }

        foreach (var template in CaseContracts.Templates)
        {
            if (!therapist.Base.ItemsBuy.IdList.Contains((MongoId)template))
                therapist.Base.ItemsBuy.IdList.Add((MongoId)template);
        }

        if (config.TherapistSellPriceKey is not null)
        {
            therapist.Base.ItemsBuy.IdList.Add((MongoId)ModConstants.KeyTemplateId);
        }
    }

    /// <summary>
    /// Patches the case's finalized Mechanic purchase price, handbook price, and credits price once the
    /// reward-pack catalog snapshot is available. Runs at the very end of server startup (see
    /// CatalogStartupBarrier), unlike <see cref="ApplyKeyRegistrationPrice"/>, because the case's
    /// non-fixed price depends on the catalog's expected value. The key no longer goes through this path at
    /// all -- it isn't sold by Mechanic any more.
    /// </summary>
    public static void ApplyFinalizedPrices(
        TemplateTable templates,
        TraderAssort mechanicAssort,
        TicketPrices prices,
        ModConfig config)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(mechanicAssort);
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(config);

        ValidatePriceStructure(prices);
        var casePrice = ToExactDoublePrice(prices.CasePrice, "case");
        var sinks = PreflightCasePriceSinks(templates, mechanicAssort);
        ArgumentNullException.ThrowIfNull(templates.Prices);

        sinks.CaseProperties.CreditsPrice = casePrice;

        // The handbook price is normally identical to the Mechanic purchase price (what the player pays to
        // buy the case). When a Therapist sell price is configured, the handbook price instead needs to be
        // whatever makes Therapist's own buy-price coefficient resolve to that target, which is generally a
        // different (larger) number. This is how vanilla items work too: a trader's payout is always some
        // fraction of handbook value, never the whole thing.
        var caseHandbookPrice = config.TherapistSellPriceCase is long caseTarget
            ? ComputeHandbookPriceForTraderSellTarget(caseTarget, TherapistBuyPriceCoefficientPercent)
            : prices.CasePrice;
        sinks.CaseHandbook.Price = ToExactDoublePrice(caseHandbookPrice, "case handbook");

        sinks.CaseBarter.Count = casePrice;
        // CustomItemService registered a provisional flea value before the
        // catalog existed. Publish the real value alongside the other sinks.
        templates.Prices[(MongoId)ModConstants.CaseTemplateId] = casePrice;
    }

    /// <summary>
    /// Overrides the key's registered handbook/credits price to hit <see cref="ModConfig.TherapistSellPriceKey"/>,
    /// when configured. Unlike the case, this needs no catalog snapshot and no deferral to the final startup
    /// barrier -- it can run immediately after the key is registered, from <c>ContrabandContentLoader</c>. A
    /// no-op when <see cref="ModConfig.TherapistSellPriceKey"/> is unset, since <see cref="CreateKeyCloneDetails"/>
    /// already registered the key with its correct fixed internal price in that case.
    /// </summary>
    public static void ApplyKeyRegistrationPrice(TemplateTable templates, ModConfig config)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(config);

        if (config.TherapistSellPriceKey is not long keyTarget)
        {
            return;
        }

        var keyProperties = RequireTemplateProperties(templates, ModConstants.KeyTemplateId, KeyParentId, "key");
        var keyHandbook = RequireHandbookEntry(templates, ModConstants.KeyTemplateId, KeyHandbookParentId, "key");

        var keyHandbookPrice = ComputeHandbookPriceForTraderSellTarget(keyTarget, TherapistBuyPriceCoefficientPercent);
        var exactPrice = ToExactDoublePrice(keyHandbookPrice, "key handbook");
        keyProperties.CreditsPrice = exactPrice;
        keyHandbook.Price = exactPrice;
    }

    public static void EnsureNativeRandomLootRouteUnavailable(
        IReadOnlyDictionary<MongoId, RewardDetails> randomLootContainers)
    {
        ArgumentNullException.ThrowIfNull(randomLootContainers);
        if (CaseContracts.Templates.Any(template => randomLootContainers.ContainsKey((MongoId)template)))
        {
            throw new InvalidOperationException("BR-12 Relay Case must not be registered as a native random loot container.");
        }
    }

    private static TemplateItemProperties CommonProperties(
        string name,
        string shortName,
        string description,
        string prefabPath,
        long handbookPrice,
        int width,
        int height) =>
        new()
        {
            Name = name,
            ShortName = shortName,
            Description = description,
            Prefab = new Prefab { Path = prefabPath },
            Width = width,
            Height = height,
            StackMaxSize = 1,
            StackObjectsCount = 1,
            CreditsPrice = PositivePrice(handbookPrice),
            Grids = [],
            Slots = [],
            StackSlots = [],
            ForbidNonEmptyContainers = true,
            QuestItem = false,
            ExaminedByDefault = true,
            CanSellOnRagfair = false,
            CanRequireOnRagfair = false,
            IsUngivable = false,
            IsUndiscardable = false
        };

    private static double PositivePrice(long price)
    {
        if (price <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(price));
        }

        return ToExactDoublePrice(price, "registration");
    }

    private static TicketPrices FixedPrices(long casePrice)
    {
        if (casePrice <= 0)
        {
            throw new InvalidOperationException("The fixed case price must be positive.");
        }

        return new TicketPrices(casePrice);
    }

    private sealed record CasePriceSinks(
        TemplateItemProperties CaseProperties,
        HandbookItem CaseHandbook,
        BarterScheme CaseBarter);

    private static CasePriceSinks PreflightCasePriceSinks(
        TemplateTable templates,
        TraderAssort mechanicAssort)
    {
        if (templates.Items is null)
        {
            throw new InvalidOperationException("The finalized SPT item template table is unavailable.");
        }

        var caseProperties = RequireTemplateProperties(
            templates,
            ModConstants.CaseTemplateId,
            CaseParentId,
            "case");
        var caseHandbook = RequireHandbookEntry(
            templates,
            ModConstants.CaseTemplateId,
            CaseHandbookParentId,
            "case");
        var caseBarter = RequireMechanicRoubleBarter(
            mechanicAssort,
            ModConstants.MechanicCaseAssortRootId,
            ModConstants.CaseTemplateId,
            "case");

        return new CasePriceSinks(caseProperties, caseHandbook, caseBarter);
    }

    private static TemplateItemProperties RequireTemplateProperties(
        TemplateTable templates,
        string templateId,
        string expectedParentId,
        string label)
    {
        var id = (MongoId)templateId;
        var expectedParent = (MongoId)expectedParentId;
        var matches = templates.Items
            .Where(entry => entry.Value is not null && entry.Value.Id == id)
            .Take(2)
            .ToArray();
        if (matches.Length != 1 ||
            matches[0].Key != id ||
            matches[0].Value.Parent != expectedParent)
        {
            throw new InvalidOperationException(
                $"The registered Contraband Cases {label} template must exist exactly once and be well formed.");
        }

        var properties = matches[0].Value.Properties ??
            throw new InvalidOperationException(
                $"The registered Contraband Cases {label} template has no properties.");
        RequirePositiveFinitePrice(properties.CreditsPrice, $"{label} template CreditsPrice");
        return properties;
    }

    private static HandbookItem RequireHandbookEntry(
        TemplateTable templates,
        string templateId,
        string expectedParentId,
        string label)
    {
        var entries = templates.Handbook?.Items;
        if (entries is null)
        {
            throw new InvalidOperationException("The finalized SPT handbook is unavailable.");
        }

        var id = (MongoId)templateId;
        var matches = entries
            .Where(entry => entry is not null && entry.Id == id)
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"The registered Contraband Cases {label} handbook entry must exist exactly once.");
        }

        if (matches[0].ParentId != (MongoId)expectedParentId)
        {
            throw new InvalidOperationException(
                $"The registered Contraband Cases {label} handbook entry has the wrong parent.");
        }

        RequirePositiveFinitePrice(matches[0].Price, $"{label} handbook price");
        return matches[0];
    }

    private static BarterScheme RequireMechanicRoubleBarter(
        TraderAssort mechanicAssort,
        string assortId,
        string templateId,
        string label)
    {
        if (mechanicAssort.Items is null ||
            mechanicAssort.BarterScheme is null ||
            mechanicAssort.LoyalLevelItems is null)
        {
            throw new InvalidOperationException("Mechanic's finalized assort is unavailable.");
        }

        var id = (MongoId)assortId;
        var expectedTemplate = (MongoId)templateId;
        var roots = mechanicAssort.Items
            .Where(item => item is not null && item.Id == id)
            .Take(2)
            .ToArray();
        if (roots.Length != 1 ||
            roots[0].Template != expectedTemplate ||
            !string.Equals(roots[0].ParentId, "hideout", StringComparison.Ordinal) ||
            !string.Equals(roots[0].SlotId, "hideout", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The registered Contraband Cases Mechanic {label} offer root is missing or malformed.");
        }

        var update = roots[0].Upd;
        var stock = update?.StackObjectsCount;
        var buyRestriction = update?.BuyRestrictionMax;
        if (update is null ||
            update.UnlimitedCount != false ||
            stock is null ||
            !double.IsFinite(stock.Value) ||
            stock.Value <= 0d ||
            buyRestriction is null ||
            buyRestriction.Value <= 0 ||
            stock.Value != buyRestriction.Value ||
            update.BuyRestrictionCurrent != 0 ||
            update.SpawnedInSession != false)
        {
            throw new InvalidOperationException(
                $"The registered Contraband Cases Mechanic {label} offer update data is malformed.");
        }

        if (!mechanicAssort.LoyalLevelItems.TryGetValue(id, out var loyaltyLevel) || loyaltyLevel != 1)
        {
            throw new InvalidOperationException(
                $"The registered Contraband Cases Mechanic {label} offer must require loyalty level 1.");
        }

        if (!mechanicAssort.BarterScheme.TryGetValue(id, out var options) ||
            options is null ||
            options.Count != 1 ||
            options[0] is null ||
            options[0].Count != 1 ||
            options[0][0] is null ||
            options[0][0].Template != Money.ROUBLES)
        {
            throw new InvalidOperationException(
                $"The registered Contraband Cases Mechanic {label} offer must have exactly one rouble price.");
        }

        RequirePositiveFinitePrice(options[0][0].Count, $"Mechanic {label} barter price");
        return options[0][0];
    }

    private static void ValidatePriceStructure(TicketPrices prices)
    {
        if (prices.CasePrice <= 0)
        {
            throw new InvalidOperationException("Finalized case price must be positive.");
        }
    }

    private static double ToExactDoublePrice(long price, string label)
    {
        if (price <= 0 || price > MaximumExactDoubleInteger)
        {
            throw new InvalidOperationException(
                $"The finalized {label} price cannot be represented exactly by SPT's price fields.");
        }

        return price;
    }

    private static void RequirePositiveFinitePrice(double? value, string label)
    {
        if (value is null ||
            !double.IsFinite(value.Value) ||
            value.Value <= 0d ||
            value.Value > MaximumExactDoubleInteger ||
            Math.Truncate(value.Value) != value.Value)
        {
            throw new InvalidOperationException($"The registered {label} is missing or invalid.");
        }
    }
}
