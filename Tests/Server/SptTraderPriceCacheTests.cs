using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Configuration;
using ContrabandCases.Server.Content;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Tests.Loot;
using Microsoft.Extensions.Logging;
using Color = Spectre.Console.Color;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Ragfair;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Ragfair;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class SptTraderPriceCacheTests
{
    private const string Unrelated = "eeeeeeeeeeeeeeeeeeeeeeee";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_flea_validation_honors_early_owned_exclusions_after_price_mod_reenables_items(bool bsgListEnabled)
    {
        var fixture = new Fixture();
        // Replay native startup order: preload exclusions, native price load,
        // then a pricing-mod postfix restoring flags from its provisional baseline.
        // RagfairCallbacks immediately generates offers before the final catalog hook.
        ContrabandFleaPolicy.RegisterExclusions(fixture.FleaConfig);
        fixture.Ragfair.Load();
        fixture.FleaConfig.Dynamic.Blacklist.EnableBsgList = bsgListEnabled;
        foreach (var item in fixture.Templates.Items.Values)
        {
            item.Properties!.CanSellOnRagfair = true;
            fixture.Templates.Prices[item.Id] = fixture.Entry(item.Id).Price!.Value * 1.2;
        }

        foreach (var id in CaseContracts.Templates.Append(ModConstants.KeyTemplateId))
        {
            Assert.False(fixture.FleaHelper.IsItemValidRagfairItem(new(true, fixture.Templates.Items[id])));
            Assert.Contains((MongoId)id, fixture.FleaConfig.Dynamic.Barter.ItemTplBlacklist);
        }
        Assert.True(fixture.FleaHelper.IsItemValidRagfairItem(new(true, fixture.Templates.Items[Unrelated])));

        await fixture.Start();

        Assert.Equal(bsgListEnabled, fixture.FleaConfig.Dynamic.Blacklist.EnableBsgList);
        Assert.All(CaseContracts.Templates, id =>
            Assert.Equal((double)fixture.Coordinator.GetCaseSnapshot(id).CasePrice!, fixture.Templates.Prices[id]));
        Assert.Equal(14_400d, fixture.Templates.Prices[Unrelated]);
    }

    [Fact]
    public void Repeated_native_exclusion_registration_preserves_existing_sets_and_unrelated_entries()
    {
        var fixture = new Fixture();
        var custom = fixture.FleaConfig.Dynamic.Blacklist.Custom;
        var barter = fixture.FleaConfig.Dynamic.Barter.ItemTplBlacklist;
        custom.Add(Unrelated);
        barter.Add(Unrelated);

        ContrabandFleaPolicy.RegisterExclusions(fixture.FleaConfig);
        ContrabandFleaPolicy.RegisterExclusions(fixture.FleaConfig);

        Assert.Same(custom, fixture.FleaConfig.Dynamic.Blacklist.Custom);
        Assert.Same(barter, fixture.FleaConfig.Dynamic.Barter.ItemTplBlacklist);
        Assert.Equal(7, custom.Count);
        Assert.Equal(7, barter.Count);
        Assert.Contains((MongoId)Unrelated, custom);
        Assert.Contains((MongoId)Unrelated, barter);
    }

    [Theory]
    [InlineData("dynamic")]
    [InlineData("custom")]
    [InlineData("barter")]
    [InlineData("barter-list")]
    public void Missing_native_config_fails_before_mutating_an_existing_exclusion_set(string defect)
    {
        var fixture = new Fixture();
        var custom = fixture.FleaConfig.Dynamic.Blacklist.Custom;
        var barter = fixture.FleaConfig.Dynamic.Barter.ItemTplBlacklist;
        switch (defect)
        {
            case "dynamic": fixture.FleaConfig.Dynamic = null!; break;
            case "custom": fixture.FleaConfig.Dynamic.Blacklist.Custom = null!; break;
            case "barter": fixture.FleaConfig.Dynamic.Barter = null!; break;
            case "barter-list": fixture.FleaConfig.Dynamic.Barter.ItemTplBlacklist = null!; break;
        }

        Assert.Throws<InvalidOperationException>(() => ContrabandFleaPolicy.RegisterExclusions(fixture.FleaConfig));

        Assert.Empty(custom);
        Assert.Empty(barter);
    }

    [Fact]
    public void Null_native_config_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() => ContrabandFleaPolicy.RegisterExclusions(null!));

    [Theory]
    [InlineData("packed")]
    [InlineData("extra-child")]
    [InlineData("missing-assort")]
    [InlineData("missing-barter")]
    [InlineData("empty-barter")]
    [InlineData("foreign-currency")]
    [InlineData("null-price")]
    [InlineData("nan-price")]
    [InlineData("zero-price")]
    public void Malformed_owned_Mechanic_offer_fails_before_repricing_or_removing_any_offer(string defect)
    {
        var fixture = new Fixture();
        var fake = fixture.AddOffer(CaseContracts.CashCache, OfferCreator.FakePlayer);
        var offer = fixture.AddOffer(ModConstants.CaseTemplateId, OfferCreator.Trader,
            Traders.MECHANIC, ModConstants.MechanicCaseAssortRootId);
        var scheme = fixture.Assort.BarterScheme[ModConstants.MechanicCaseAssortRootId];
        switch (defect)
        {
            case "packed": offer.SellInOnePiece = true; break;
            case "extra-child": offer.Items!.Add(new Item { Id = new MongoId(), Template = Unrelated }); break;
            case "missing-assort": fixture.Assort.Items.Clear(); break;
            case "missing-barter": fixture.Assort.BarterScheme.Clear(); break;
            case "empty-barter": scheme.Clear(); break;
            case "foreign-currency": scheme[0][0].Template = Money.DOLLARS; break;
            case "null-price": scheme[0][0].Count = null; break;
            case "nan-price": scheme[0][0].Count = double.NaN; break;
            case "zero-price": scheme[0][0].Count = 0; break;
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ContrabandFleaPolicy.FinalizeStartup(fixture.Templates, fixture.Traders, fixture.FleaConfig, fixture.Offers));

        Assert.Contains("cached owned Mechanic", exception.Message);
        Assert.Same(fake, fixture.Offers.GetOfferById(fake.Id));
        Assert.Equal(1_000d, Assert.Single(offer.Requirements!).Count);
        Assert.Empty(fixture.FleaConfig.Dynamic.Blacklist.Custom);
    }

    [Fact]
    public void Startup_cleanup_is_repeatable_and_preserves_unknown_creator_offers()
    {
        var fixture = new Fixture();
        var unknown = fixture.AddOffer(CaseContracts.CashCache, OfferCreator.Player);
        unknown.CreatedBy = null; // No proof this is a generated fake seller: do not remove it.
        var fake = fixture.AddOffer(ModConstants.KeyTemplateId, OfferCreator.FakePlayer);

        Assert.Equal(1, ContrabandFleaPolicy.FinalizeStartup(fixture.Templates, fixture.Traders, fixture.FleaConfig, fixture.Offers));
        Assert.Equal(0, ContrabandFleaPolicy.FinalizeStartup(fixture.Templates, fixture.Traders, fixture.FleaConfig, fixture.Offers));

        Assert.Same(unknown, Assert.Single(fixture.Offers.GetOffers()));
        Assert.Null(fixture.Offers.GetOfferById(fake.Id));
    }

    [Fact]
    public void Startup_cleanup_handles_large_native_offer_cache_without_changing_unrelated_offers()
    {
        var fixture = new Fixture();
        fixture.FleaConfig.Dynamic.OfferItemCount["default"] = new MinMax<int> { Min = 20_000, Max = 20_000 };
        var unrelated = Enumerable.Range(0, 10_000).Select(_ => fixture.AddOffer(Unrelated, OfferCreator.FakePlayer)).ToArray();
        var key = fixture.AddOffer(ModConstants.KeyTemplateId, OfferCreator.FakePlayer);

        Assert.Equal(1, ContrabandFleaPolicy.FinalizeStartup(fixture.Templates, fixture.Traders, fixture.FleaConfig, fixture.Offers));

        Assert.Null(fixture.Offers.GetOfferById(key.Id));
        Assert.Equal(10_000, fixture.Offers.GetOffers().Count);
        Assert.All(unrelated, offer => Assert.Same(offer, fixture.Offers.GetOfferById(offer.Id)));
    }

    [Fact]
    public async Task Startup_restores_owned_flea_exclusions_without_changing_global_blacklist_policy()
    {
        var fixture = new Fixture();
        fixture.FleaConfig.Dynamic.Blacklist.Custom.Add(Unrelated);
        foreach (var item in fixture.Templates.Items.Values)
        {
            item.Properties!.CanSellOnRagfair = true;
            item.Properties.CanRequireOnRagfair = true;
        }

        await fixture.Start();

        Assert.False(fixture.FleaConfig.Dynamic.Blacklist.EnableBsgList);
        Assert.Contains((MongoId)Unrelated, fixture.FleaConfig.Dynamic.Blacklist.Custom);
        foreach (var id in CaseContracts.Templates.Append(ModConstants.KeyTemplateId))
        {
            Assert.False(fixture.Templates.Items[id].Properties!.CanSellOnRagfair);
            Assert.False(fixture.Templates.Items[id].Properties!.CanRequireOnRagfair);
            Assert.Contains((MongoId)id, fixture.FleaConfig.Dynamic.Blacklist.Custom);
            Assert.Contains((MongoId)id, fixture.FleaConfig.Dynamic.Barter.ItemTplBlacklist);
        }
        Assert.True(fixture.Templates.Items[Unrelated].Properties!.CanSellOnRagfair);
        Assert.True(fixture.Templates.Items[Unrelated].Properties!.CanRequireOnRagfair);
    }

    [Fact]
    public async Task Startup_removes_owned_fake_and_expired_listings_but_preserves_player_and_other_trader_offers()
    {
        var fixture = new Fixture();
        var fake = fixture.AddOffer(CaseContracts.CashCache, OfferCreator.FakePlayer);
        fixture.Offers.FlagOfferAsExpired(fake.Id);
        var key = fixture.AddOffer(ModConstants.KeyTemplateId, OfferCreator.FakePlayer);
        var barter = fixture.AddOffer(Unrelated, OfferCreator.FakePlayer);
        barter.Requirements = [new OfferRequirement { TemplateId = ModConstants.KeyTemplateId, Count = 1 }];
        var player = fixture.AddOffer(CaseContracts.Operations, OfferCreator.Player);
        var trader = fixture.AddOffer(CaseContracts.BlackSite, OfferCreator.Trader);

        await fixture.Start();

        Assert.Null(fixture.Offers.GetOfferById(fake.Id));
        Assert.Null(fixture.Offers.GetOfferById(key.Id));
        Assert.Null(fixture.Offers.GetOfferById(barter.Id));
        Assert.Empty(fixture.Offers.GetStaleOfferIds());
        Assert.Empty(fixture.Offers.GetExpiredOfferItems());
        Assert.Same(player, fixture.Offers.GetOfferById(player.Id));
        Assert.Same(trader, fixture.Offers.GetOfferById(trader.Id));
        Assert.Equal(1_000d, trader.SummaryCost);
    }

    [Fact]
    public async Task Startup_reprices_cached_Mechanic_offer_without_replacing_identity_stock_or_other_listings()
    {
        var fixture = new Fixture("""{ "fixedCasePrice": 1000000, "therapistSellPriceCase": 500000 }""");
        var offer = fixture.AddOffer(ModConstants.CaseTemplateId, OfferCreator.Trader, Traders.MECHANIC,
            ModConstants.MechanicCaseAssortRootId);
        var other = fixture.AddOffer(Unrelated, OfferCreator.Trader, Traders.MECHANIC);
        var initialId = offer.Id;
        var initialQuantity = offer.Quantity;

        await fixture.Start();

        Assert.Same(offer, fixture.Offers.GetOfferById(initialId));
        Assert.Equal(initialQuantity, offer.Quantity);
        Assert.Equal(1_000_000d, Assert.Single(offer.Requirements!).Count);
        Assert.Equal(Money.ROUBLES, Assert.Single(offer.Requirements!).TemplateId);
        Assert.Equal(1_000_000d, offer.RequirementsCost);
        Assert.Equal(1_000_000d, offer.SummaryCost);
        Assert.Equal(fixture.Handbook.GetTemplatePrice(ModConstants.CaseTemplateId), offer.ItemsCost);
        Assert.Same(other, fixture.Offers.GetOfferById(other.Id));
        Assert.Equal(1_000d, other.SummaryCost);
    }

    [Fact]
    public async Task Startup_updates_warm_trader_and_handbook_caches_for_every_case()
    {
        var fixture = new Fixture();
        var originalCache = fixture.Ragfair.GetAllStaticPrices();
        Assert.All(CaseContracts.Templates, id => Assert.Equal(1_000d, originalCache[id]));

        await fixture.Start();

        Assert.All(CaseContracts.Templates, id => Assert.Contains((MongoId)id, fixture.RaidLoot.Pmc.GlobalLootBlacklist));
        Assert.Equal(0, fixture.RaidLoot.LoadCount);
        Assert.Equal(5, fixture.RaidLoot.Load().Values.Single().ItemDistribution.Count(e => CaseContracts.IsCase(e.Tpl.ToString())));
        Assert.Same(originalCache, fixture.Ragfair.GetAllStaticPrices());
        foreach (var id in CaseContracts.Templates)
        {
            var price = (double)fixture.Coordinator.GetCaseSnapshot(id).CasePrice!;
            Assert.True(price > 1_000d);
            Assert.Equal(price, fixture.Handbook.GetTemplatePrice(id));
            Assert.Equal(price, originalCache[id]);
        }
        fixture.Ragfair.RefreshStaticPrices();
        Assert.All(CaseContracts.Templates, id =>
            Assert.Equal(fixture.Entry(id).Price, fixture.Ragfair.GetAllStaticPrices()[id]));
        Assert.Equal(65_000d, fixture.Ragfair.GetAllStaticPrices()[ModConstants.KeyTemplateId]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_also_supports_cold_trader_cache(bool warmHandbook)
    {
        var fixture = new Fixture();
        if (warmHandbook) Assert.Equal(1_000d, fixture.Handbook.GetTemplatePrice(ModConstants.CaseTemplateId));

        await fixture.Start();

        Assert.All(CaseContracts.Templates.Append(ModConstants.KeyTemplateId), id =>
        {
            Assert.Equal(fixture.Entry(id).Price, fixture.Handbook.GetTemplatePrice(id));
            Assert.Equal(fixture.Entry(id).Price, fixture.Ragfair.GetAllStaticPrices()[id]);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_preserves_configured_sell_targets_separately_from_purchase_prices(bool warm)
    {
        var fixture = new Fixture("""
            { "fixedCasePrice": 150000, "therapistSellPriceCase": 120000, "therapistSellPriceKey": 50000 }
            """);
        if (warm) _ = fixture.Ragfair.GetAllStaticPrices();
        // The key override is applied during registration, potentially after cache warmup.
        ContrabandContentDefinitions.ApplyKeyRegistrationPrice(fixture.Templates, fixture.Config);

        await fixture.Start();

        var caseBase = ContrabandContentDefinitions.ComputeHandbookPriceForTraderSellTarget(120_000, 37);
        var keyBase = ContrabandContentDefinitions.ComputeHandbookPriceForTraderSellTarget(50_000, 37);
        foreach (var (id, price) in new[] { (ModConstants.CaseTemplateId, caseBase), (ModConstants.KeyTemplateId, keyBase) })
        {
            Assert.Equal((double)price, fixture.Handbook.GetTemplatePrice(id));
            Assert.Equal((double)price, fixture.Ragfair.GetAllStaticPrices()[id]);
        }
        Assert.Equal(150_000d, fixture.Templates.Prices[ModConstants.CaseTemplateId]);
        Assert.Equal(150_000d, fixture.Assort.BarterScheme[ModConstants.MechanicCaseAssortRootId][0][0].Count);
        Assert.All(CaseContracts.Templates.Where(id => id != ModConstants.CaseTemplateId), id =>
            Assert.Equal((double)fixture.Coordinator.GetCaseSnapshot(id).CasePrice!, fixture.Handbook.GetTemplatePrice(id)));
        fixture.Ragfair.RefreshStaticPrices();
        Assert.Equal((double)keyBase, fixture.Ragfair.GetAllStaticPrices()[ModConstants.KeyTemplateId]);
        Assert.Equal((double)caseBase, fixture.Ragfair.GetAllStaticPrices()[ModConstants.CaseTemplateId]);
    }

    [Fact]
    public async Task Startup_preserves_unrelated_cached_prices_and_dictionary_identity()
    {
        var fixture = new Fixture();
        var traderCache = fixture.Ragfair.GetAllStaticPrices();
        traderCache[Unrelated] = 87_654d; // A different mod has deliberately customized this cache.
        fixture.Entry(Unrelated).Price = 34_567d; // Do not copy this unrelated table edit to either cache.

        await fixture.Start();

        Assert.Same(traderCache, fixture.Ragfair.GetAllStaticPrices());
        Assert.Equal(87_654d, traderCache[Unrelated]);
        Assert.Equal(12_000d, fixture.Handbook.GetTemplatePrice(Unrelated));
        Assert.Equal(34_567d, fixture.Entry(Unrelated).Price);
    }

    [Fact]
    public async Task Cold_native_global_overrides_apply_before_catalog_freezes_and_are_not_reapplied()
    {
        var fixture = new Fixture();
        fixture.ItemConfig.HandbookPriceOverride[Unrelated] = new HandbookPriceOverride
        {
            ParentId = ContrabandContentDefinitions.CaseHandbookParentId, Price = 43_210d
        };
        fixture.ItemConfig.HandbookPriceOverride[ModConstants.CaseTemplateId] = new HandbookPriceOverride
        {
            ParentId = ContrabandContentDefinitions.CaseHandbookParentId, Price = 987d
        };
        double? observedAtFreeze = null;
        fixture.OnFreeze = () => observedAtFreeze = fixture.Entry(Unrelated).Price;

        await fixture.Start();

        Assert.Equal(43_210d, observedAtFreeze);
        Assert.Equal(43_210d, fixture.Handbook.GetTemplatePrice(Unrelated));
        fixture.Ragfair.RefreshStaticPrices();
        Assert.Equal(fixture.Entry(ModConstants.CaseTemplateId).Price,
            fixture.Ragfair.GetAllStaticPrices()[ModConstants.CaseTemplateId]);
        Assert.NotEqual(987d, fixture.Handbook.GetTemplatePrice(ModConstants.CaseTemplateId));
    }

    [Theory]
    [InlineData("missing-handbook")]
    [InlineData("duplicate-handbook")]
    [InlineData("wrong-parent")]
    [InlineData("missing-template")]
    [InlineData("mismatched-template-id")]
    [InlineData("null-price")]
    [InlineData("zero-price")]
    [InlineData("negative-price")]
    [InlineData("nan-price")]
    [InlineData("infinite-price")]
    [InlineData("fractional-price")]
    [InlineData("too-large-price")]
    public void Invalid_finalized_entry_fails_before_either_cache_is_changed(string defect)
    {
        var fixture = new Fixture();
        var caches = SptTraderPriceCaches.Bind(fixture.Handbook, fixture.Ragfair);
        var before = fixture.Ragfair.GetAllStaticPrices().ToDictionary(entry => entry.Key, entry => entry.Value);
        fixture.Entry(ModConstants.CaseTemplateId).Price = 157_000d;
        var entry = fixture.Entry(ModConstants.KeyTemplateId); // Last entry, to catch partial publication.
        switch (defect)
        {
            case "missing-handbook": fixture.Templates.Handbook.Items.Remove(entry); break;
            case "duplicate-handbook": fixture.Templates.Handbook.Items.Add(entry); break;
            case "wrong-parent": entry.ParentId = ContrabandContentDefinitions.CaseHandbookParentId; break;
            case "missing-template": fixture.Templates.Items.Remove(ModConstants.KeyTemplateId); break;
            case "mismatched-template-id": fixture.Templates.Items[ModConstants.KeyTemplateId].Id = Unrelated; break;
            case "null-price": entry.Price = null; break;
            case "zero-price": entry.Price = 0; break;
            case "negative-price": entry.Price = -1; break;
            case "nan-price": entry.Price = double.NaN; break;
            case "infinite-price": entry.Price = double.PositiveInfinity; break;
            case "fractional-price": entry.Price = 1.5; break;
            case "too-large-price": entry.Price = 9_007_199_254_740_994d; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }

        Assert.Throws<InvalidOperationException>(() => caches.Publish(fixture.Templates));

        Assert.All(before, pair =>
        {
            Assert.Equal(pair.Value, fixture.Ragfair.GetAllStaticPrices()[pair.Key]);
            Assert.Equal(pair.Value, fixture.Handbook.GetTemplatePrice(pair.Key));
        });
    }

    [Fact]
    public void Publish_is_repeatable_and_repairs_missing_owned_cache_entries()
    {
        var fixture = new Fixture();
        var caches = SptTraderPriceCaches.Bind(fixture.Handbook, fixture.Ragfair);
        fixture.Ragfair.GetAllStaticPrices().Remove(CaseContracts.CashCache);
        fixture.Entry(ModConstants.CaseTemplateId).Price = 157_000d;

        caches.Publish(fixture.Templates);
        caches.Publish(fixture.Templates);

        Assert.Equal(157_000d, fixture.Handbook.GetTemplatePrice(ModConstants.CaseTemplateId));
        Assert.Equal(157_000d, fixture.Ragfair.GetAllStaticPrices()[ModConstants.CaseTemplateId]);
        Assert.Equal(fixture.Entry(CaseContracts.CashCache).Price, fixture.Ragfair.GetAllStaticPrices()[CaseContracts.CashCache]);
    }

    private sealed class Fixture
    {
        public TemplateTable Templates { get; } = new()
        {
            Items = [], Prices = [], Handbook = new HandbookBase { Items = [], Categories = [] },
            Character = [], CustomisationStorage = [], Prestige = null!, Quests = [],
            RepeatableQuests = null!, Customization = [], Dialogue = null!, Profiles = [],
            DefaultEquipmentPresets = [], Achievements = [], CustomAchievements = [], LocationServices = null!
        };
        public ModConfig Config { get; }
        public ItemConfig ItemConfig { get; } = new()
        {
            HandbookPriceOverride = [], Blacklist = [], LootableItemBlacklist = [],
            RewardItemBlacklist = [], RewardItemTypeBlacklist = [], BossItems = [], CustomItemGlobalPresets = []
        };
        public Action? OnFreeze { get; set; }
        public HandbookHelper Handbook { get; }
        public RagfairPriceService Ragfair { get; }
        public CatalogSnapshotCoordinator Coordinator { get; }
        public TraderAssort Assort { get; } = new() { Items = [], BarterScheme = [], LoyalLevelItems = [] };
        public RagfairConfig FleaConfig { get; } = new()
        {
            Traders = [], Sell = null!, TieredFlea = null!, RunIntervalValues = null!,
            Dynamic = new Dynamic
            {
                Blacklist = new RagfairBlacklist { Custom = [], CustomItemCategoryList = [], ArmorPlate = null! },
                Barter = new BarterDetails { ItemTplBlacklist = [], ItemTypeBlacklist = [] },
                Pack = null!, OfferAdjustment = null!, OfferItemCount = new() { ["default"] = new MinMax<int> { Min = 10, Max = 10 } },
                PriceRanges = null!, IgnoreQualityPriceVarianceBlacklist = [], EndTimeSeconds = null!,
                Condition = [], StackablePercent = null!, NonStackableCount = null!, Rating = null!,
                Armor = null!, OfferCurrencyChangePercent = [], ShowAsSingleStack = [],
                UnreasonableModPrices = [], ItemPriceOverrideRouble = [], GenerateBaseFleaPrices = new()
            }
        };
        public RagfairOfferHolder Offers { get; }
        public RagfairServerHelper FleaHelper { get; }
        public TradersTable Traders { get; }

        public Fixture(string configJson = "{}")
        {
            Config = ModConfig.Parse(configJson);
            foreach (var id in CaseContracts.Templates.Append(ModConstants.KeyTemplateId).Append(Unrelated))
            {
                var price = id == ModConstants.KeyTemplateId ? 65_000 : id == Unrelated ? 12_000 : 1_000;
                var parent = id == ModConstants.KeyTemplateId
                    ? ContrabandContentDefinitions.KeyParentId : ContrabandContentDefinitions.CaseParentId;
                var handbookParent = id == ModConstants.KeyTemplateId
                    ? ContrabandContentDefinitions.KeyHandbookParentId : ContrabandContentDefinitions.CaseHandbookParentId;
                Templates.Items.Add(id, new TemplateItem
                {
                    Id = id, Type = "Item", Parent = parent,
                    Properties = new TemplateItemProperties { CreditsPrice = price }
                });
                Templates.Handbook.Items.Add(new HandbookItem { Id = id, ParentId = handbookParent, Price = price });
            }
            Handbook = new HandbookHelper(new QuietLogger<HandbookHelper>(), Templates,
                ItemConfig, new HandbookCloner());
            Ragfair = new RagfairPriceService(null!, Templates, null!, null!, Handbook,
                null!, null!, null!, null!, FleaConfig);
            var itemHelper = new ItemHelper(new QuietLogger<ItemHelper>(), Templates, null!, Handbook,
                new ItemBaseClassService(new QuietLogger<ItemBaseClassService>(), Templates, null!),
                new ItemFilterService(ItemConfig), null!, null!, null!);
            FleaHelper = new RagfairServerHelper(null!, new TradersTable(), new RandomUtil(null!, null!), null!, itemHelper,
                null!, null!, null!, FleaConfig, null!);
            Offers = new RagfairOfferHolder(new QuietLogger<RagfairOfferHolder>(), FleaHelper, null!, itemHelper);
            var catalog = CaseCatalogTests.Snapshot(new[]
            {
                CaseCatalogTests.Lot("core", "arsenal", "rifle"),
                CaseCatalogTests.Lot("core", "operator", "recon"),
                CaseCatalogTests.Lot("core", "field-supply", "medical"),
                CaseCatalogTests.Lot("vault", "arsenal", "vault"),
                CaseCatalogTests.Lot("core", "operator", "night"),
                CaseCatalogTests.Lot("core", "field-supply", "ordnance"),
                CaseCatalogTests.Lot("krackasourus.anime-cards", "field-supply", "anime-cards", collection: true),
                CaseCatalogTests.Lot("krackasourus.pokemon-cards", "field-supply", "pokemon-cards", collection: true),
                CaseCatalogTests.Lot("krackasourus.yugioh-cards", "field-supply", "yugioh-cards", collection: true)
            });
            Coordinator = new CatalogSnapshotCoordinator(() => { OnFreeze?.Invoke(); return catalog; },
                () => CashCacheTests.Catalog());
            ContrabandContentDefinitions.ApplyMechanicOffers(Assort,
                ContrabandContentDefinitions.CreateMechanicOffers(
                    ContrabandContentDefinitions.CalculateRegistrationPrices(Config), Config));
            Traders = new TradersTable
            {
                [SPTarkov.Server.Core.Models.Enums.Traders.MECHANIC] = new Trader
                    { Assort = Assort, Base = null!, Dialogue = [], QuestAssort = null! }
            };
        }

        public HandbookItem Entry(string id) => Templates.Handbook.Items.Single(item => item.Id == (MongoId)id);

        public RagfairOffer AddOffer(string template, OfferCreator creator, MongoId? userId = null, MongoId? rootId = null)
        {
            var root = rootId ?? new MongoId();
            var offer = new RagfairOffer
            {
                Id = new MongoId(), Root = root, CreatedBy = creator,
                User = new RagfairOfferUser { Id = userId ?? new MongoId(),
                    MemberType = creator == OfferCreator.Trader ? MemberCategory.Trader : MemberCategory.Default },
                Items = [new Item { Id = root, Template = template, Upd = new Upd { StackObjectsCount = 3 } }],
                Requirements = [new OfferRequirement { TemplateId = Money.ROUBLES, Count = 1_000 }],
                ItemsCost = 1_000, RequirementsCost = 1_000, SummaryCost = 1_000, Quantity = 3
            };
            Offers.AddOffer(offer);
            return offer;
        }

        public Task Start()
        {
            var state = new ContrabandContentState();
            state.Initialize(Config);
            return new CatalogStartupBarrier(Coordinator, state, Templates, Traders, Handbook, Ragfair,
                FleaConfig, Offers,
                RaidLoot.Locations, RaidLoot.Bots, RaidLoot.Pmc,
                new QuietLogger<CatalogStartupBarrier>()).OnLoadAsync(CancellationToken.None);
        }

        public ContrabandCaseLootInjectorTests.Fixture RaidLoot { get; } = new(new()
        {
            ["578f87ad245977356274f2cc"] = new StaticLootDetails
            {
                ItemDistribution = [new ItemDistribution { Tpl = "5449016a4bdc2d6f028b456f", RelativeProbability = 1000 }]
            }
        });
    }

    private sealed class HandbookCloner : ICloner
    {
        public T? Clone<T>(T? value) => value is HandbookBase handbook
            ? (T)(object)new HandbookBase
            {
                Items = handbook.Items.Select(item => new HandbookItem
                    { Id = item.Id, ParentId = item.ParentId, Price = item.Price }).ToList(),
                Categories = handbook.Categories.ToList()
            }
            : throw new NotSupportedException("This fixture only clones the handbook.");
    }

    private sealed class QuietLogger<T> : ISptLogger<T>
    {
        public void LogWithColor(string data, Color? textColor = null, Color? backgroundColor = null, Exception? ex = null) { }
        public void Success(string data, Exception? ex = null) { }
        public void Error(string data, Exception? ex = null) { }
        public void Warning(string data, Exception? ex = null) { }
        public void Info(string data, Exception? ex = null) { }
        public void Debug(string data, Exception? ex = null) { }
        public void Critical(string data, Exception? ex = null) { }
        public void Log(LogLevel level, string data, Color? textColor = null, Color? backgroundColor = null, Exception? ex = null) { }
        public bool IsLogEnabled(LogLevel level) => false;
    }
}
