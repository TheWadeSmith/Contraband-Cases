using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Configuration;
using ContrabandCases.Server.Content;
using ContrabandCases.Server.Loot;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Tests.Server;
using Microsoft.Extensions.Logging;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Json;
using Xunit;
using Color = Spectre.Console.Color;

namespace ContrabandCases.Tests.Loot;

public sealed class ContrabandCaseLootInjectorTests
{
    private const string Crate = "578f87ad245977356274f2cc";
    private const string VanillaKey = ContrabandContentDefinitions.KeyCloneTemplateId;
    private const string OtherItem = "5449016a4bdc2d6f028b456f";

    // Verified against the SPT 4.1.3 container templates, not a broad container-parent filter.
    [Theory]
    [InlineData(Crate)]
    [InlineData("5909d36d86f774660f0bb900")]
    [InlineData("5909d45286f77465a8136dc6")]
    [InlineData("5909d5ef86f77467974efbd8")]
    [InlineData("5909d76c86f77471e53d2adf")]
    [InlineData("5909d7cf86f77470ee57d75a")]
    [InlineData("5909d89086f77472591234a0")]
    [InlineData("5d6fd13186f77424ad2a8c69")]
    [InlineData("5d6fd45b86f774317075ed43")]
    [InlineData("5d6fe50986f77449d97f7463")]
    [InlineData("67adf4b81c58bd68b2002fec")]
    [InlineData("67adf4db515e3dd542077a1d")]
    [InlineData("67adf4eb110ba15da90c6413")]
    [InlineData("67adf5f7adc1f43b0702b826")]
    public void Verified_crates_receive_one_shared_weight_split_between_five_ordinary_cases(string container)
    {
        var original = Pool(800, 200);
        var counts = original.ItemCountDistribution;
        var fixture = new Fixture(new() { [container] = original });
        fixture.Register();

        var result = fixture.Load()[container];
        Assert.Same(counts, result.ItemCountDistribution);
        Assert.Equal(800f, result.ItemDistribution.Single(e => e.Tpl == (MongoId)OtherItem).RelativeProbability);
        Assert.Equal(200f, result.ItemDistribution.Single(e => e.Tpl == (MongoId)VanillaKey).RelativeProbability);
        var cases = Cases(result);
        Assert.Equal(CaseContracts.Templates.Order(), cases.Select(e => e.Tpl.ToString()).Order());
        Assert.All(cases, entry => Assert.Equal(2.2f, entry.RelativeProbability));
        Assert.Equal(11f, cases.Sum(e => e.RelativeProbability));
    }

    [Theory]
    [InlineData("578f8778245977358849a9b5")] // Jacket
    [InlineData("578f8782245977354405a1e3")] // Safe
    [InlineData("578f87a3245977356274f2cb")] // Duffel
    [InlineData("578f87b7245977356274f2cd")] // Drawers
    [InlineData("5909d50c86f774659e6aaebe")] // Toolbox
    [InlineData("5909e4b686f7747f5b744fa4")] // Dead scav
    [InlineData("5d6d2b5486f774785c2ba8ea")] // Ground cache
    [InlineData("5d6d2bb386f774785b07a77a")] // Barrel cache
    [InlineData("6582e6bb0c3b9823fe6d1840")] // Flare scav corpse
    [InlineData("6582e6c6edf14c4c6023adf2")] // Laborant corpse
    [InlineData("6582e6d7b14c3f72eb071420")] // PMC corpse
    [InlineData("658420d8085fea07e674cdb6")] // Civilian corpse
    [InlineData("67adf5752fc5ee84020a9940")] // Labyrinth scav corpse
    [InlineData("eeeeeeeeeeeeeeeeeeeeeeee")] // Unknown/modded container
    public void Non_crates_cannot_spawn_cases_even_when_a_pool_already_contains_one(string container)
    {
        var pool = Pool(800, 200);
        pool.ItemDistribution = pool.ItemDistribution.Concat(CaseContracts.Templates.Select(id => Entry(id, 100)));
        var fixture = new Fixture(new() { [container] = pool });
        fixture.Register();

        var result = fixture.Load()[container];
        Assert.Empty(Cases(result));
        Assert.Equal(new[] { OtherItem, VanillaKey }, result.ItemDistribution.Select(e => e.Tpl.ToString()));
    }

    [Fact]
    public void Unavailable_themes_are_excluded_and_remaining_cases_share_the_same_total_weight()
    {
        var catalog = CaseCatalogTests.Snapshot(new[]
        {
            CaseCatalogTests.Lot("core", "arsenal", "rifle"),
            CaseCatalogTests.Lot("core", "operator", "recon"),
            CaseCatalogTests.Lot("core", "field-supply", "medical")
        });
        var fixture = new Fixture(new() { [Crate] = Pool(1000) },
            new CatalogSnapshotCoordinator(() => catalog)); // Cash and optional themes unavailable.
        fixture.Register();

        var cases = Cases(fixture.Load()[Crate]);
        Assert.Equal(new[] { ModConstants.CaseTemplateId, CaseContracts.Operations }, cases.Select(e => e.Tpl.ToString()));
        Assert.All(cases, e => Assert.Equal(5.5f, e.RelativeProbability));
    }

    [Fact]
    public void Recovery_only_catalog_does_not_drop_unusable_cases()
    {
        var fixture = new Fixture(new() { [Crate] = Pool(1000) },
            new CatalogSnapshotCoordinator(() => CaseCatalogTests.Snapshot(new[]
                { CaseCatalogTests.Lot("core", "arsenal", "rifle") })));
        fixture.Register();
        Assert.Empty(Cases(fixture.Load()[Crate]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_lazy_registration_handles_preloaded_maps_reload_and_repeated_registration(bool cache)
    {
        var pool = Pool(1000);
        var fixture = new Fixture(new() { [Crate] = pool }, cache: cache);
        fixture.Load(); // May already have been cached by another startup hook.
        Assert.Equal(1, fixture.LoadCount);
        fixture.Register();
        fixture.Register();
        Assert.Equal(1, fixture.LoadCount); // No eager deserialization from registration.

        var first = Cases(fixture.Load()[Crate]).Select(e => e.RelativeProbability).ToArray();
        fixture.Lazy.Clear();
        var second = Cases(fixture.Load()[Crate]).Select(e => e.RelativeProbability).ToArray();
        Assert.Equal(new float?[] { 2.2f, 2.2f, 2.2f, 2.2f, 2.2f }, first);
        Assert.Equal(first, second); // Shared dictionary rebuild cannot accumulate case weight.
    }

    [Fact]
    public void Startup_gate_is_not_opened_or_frozen_by_loot_registration()
    {
        var fixture = new Fixture(new() { [Crate] = Pool(1000) });
        Assert.Throws<InvalidOperationException>(() => fixture.Register(startupComplete: false));
        Assert.False(fixture.Coordinator.IsStartupComplete);
        Assert.False(fixture.Coordinator.IsFrozen);
        Assert.Equal(0, fixture.LoadCount);
        Assert.Empty(Cases(fixture.Load()[Crate]));
        Assert.Empty(fixture.Pmc.GlobalLootBlacklist);
    }

    [Fact]
    public void Spawn_exclusions_remove_only_cases_from_all_bot_inventory_pools_and_pmc_auto_generation()
    {
        var fixture = new Fixture(new() { [Crate] = Pool(1000) });
        var pools = new ItemPools
        {
            Backpack = BotPool(), Pockets = BotPool(), TacticalVest = BotPool(),
            SecuredContainer = BotPool(), SpecialLoot = BotPool()
        };
        fixture.Bots.Types["assault"] = new BotType { BotInventory = new BotTypeInventory { Items = pools } };
        fixture.Bots.Types["boss"] = new BotType { BotInventory = new BotTypeInventory { Items = pools } };
        fixture.Bots.Types["empty"] = new BotType();
        fixture.Bots.Types["missing"] = null;
        fixture.Pmc.GlobalLootBlacklist.Add(OtherItem);
        fixture.Register();
        fixture.Register();

        Assert.Equal(CaseContracts.Templates.Append(OtherItem).Order(),
            fixture.Pmc.GlobalLootBlacklist.Select(id => id.ToString()).Order());
        Assert.DoesNotContain((MongoId)ModConstants.KeyTemplateId, fixture.Pmc.GlobalLootBlacklist);
        foreach (var pool in new[] { pools.Backpack, pools.Pockets, pools.TacticalVest, pools.SecuredContainer, pools.SpecialLoot })
        {
            Assert.Equal(2, pool.Count);
            Assert.Equal(100d, pool[OtherItem]);
            Assert.Equal(2d, pool[ModConstants.KeyTemplateId]);
        }
    }

    [Fact]
    public void Disabled_drops_preserve_other_loot_and_keys()
    {
        var pool = Pool(1000);
        pool.ItemDistribution = pool.ItemDistribution.Append(Entry(ModConstants.KeyTemplateId, 20));
        var fixture = new Fixture(new() { [Crate] = pool }, config: """{ "caseLootWeightPercent": 0 }""");
        fixture.Register();
        Assert.Empty(Cases(fixture.Load()[Crate]));
        Assert.Equal(20f, pool.ItemDistribution.Single(e => e.Tpl == (MongoId)ModConstants.KeyTemplateId).RelativeProbability);
    }

    [Fact]
    public async Task Existing_key_injector_keeps_its_static_and_bot_rules_when_cases_are_added()
    {
        const string jacket = "578f8778245977358849a9b5";
        const string otherCrate = "5d6fd45b86f774317075ed43";
        var fixture = new Fixture(new()
        {
            [Crate] = Pool(800, 200), [jacket] = Pool(800, 200), [otherCrate] = Pool(1000)
        });
        var eligibleBot = new ItemPools { Backpack = new() { [OtherItem] = 800, [VanillaKey] = 200 } };
        var otherBot = new ItemPools { Backpack = new() { [OtherItem] = 1000 } };
        fixture.Bots.Types["assault"] = new BotType { BotInventory = new BotTypeInventory { Items = eligibleBot } };
        fixture.Bots.Types["boss"] = new BotType { BotInventory = new BotTypeInventory { Items = otherBot } };
        var templates = new TemplateTable
        {
            Items = new() { [VanillaKey] = new TemplateItem { Id = VanillaKey, Parent = ContrabandContentDefinitions.KeyParentId } },
            Prices = [], Handbook = null!, Character = [], CustomisationStorage = [], Prestige = null!, Quests = [],
            RepeatableQuests = null!, Customization = [], Dialogue = null!, Profiles = [], DefaultEquipmentPresets = [],
            Achievements = [], CustomAchievements = [], LocationServices = null!
        };
        var state = new ContrabandContentState();
        state.Initialize(ModConfig.Parse("{}"));
        await new ContrabandKeyLootInjector(new QuietLogger<ContrabandKeyLootInjector>(),
            fixture.Locations, fixture.Bots, templates, state).OnLoadAsync(CancellationToken.None);
        Assert.False(fixture.Coordinator.IsFrozen);
        fixture.Register();

        var loot = fixture.Load();
        foreach (var id in new[] { Crate, jacket })
            Assert.Equal(22f, loot[id].ItemDistribution.Single(e => e.Tpl == (MongoId)ModConstants.KeyTemplateId).RelativeProbability);
        Assert.Empty(Cases(loot[jacket]));
        Assert.Equal(5, Cases(loot[Crate]).Length);
        Assert.Equal(11.242f, Cases(loot[Crate]).Sum(e => e.RelativeProbability)!.Value, 4);
        Assert.Equal(5, Cases(loot[otherCrate]).Length);
        Assert.DoesNotContain(loot[otherCrate].ItemDistribution, e => e.Tpl == (MongoId)ModConstants.KeyTemplateId);
        Assert.Equal(22d, eligibleBot.Backpack[ModConstants.KeyTemplateId], 10);
        Assert.Single(otherBot.Backpack);
        Assert.DoesNotContain(eligibleBot.Backpack.Keys, id => CaseContracts.IsCase(id.ToString()));
    }

    [Fact]
    public void Partially_injected_case_weights_are_replaced_not_multiplied()
    {
        var pool = Pool(1000);
        pool.ItemDistribution = pool.ItemDistribution.Concat(new[]
        {
            Entry(CaseContracts.Relics, 9000), Entry(CaseContracts.Relics, 9000)
        });
        var fixture = new Fixture(new() { [Crate] = pool });
        fixture.Register();
        var cases = Cases(fixture.Load()[Crate]);
        Assert.Equal(5, cases.Length);
        Assert.Equal(11f, cases.Sum(e => e.RelativeProbability));
    }

    [Fact]
    public void A_negative_weight_is_rejected_even_when_the_pool_total_is_positive()
    {
        var pool = Pool(1000);
        pool.ItemDistribution = pool.ItemDistribution.Append(Entry(VanillaKey, -1));
        var fixture = new Fixture(new() { [Crate] = pool });
        fixture.Register();
        Assert.Empty(Cases(fixture.Load()[Crate]));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.Epsilon)] // Per-case weights would underflow.
    public void Invalid_or_unrepresentable_weights_do_not_create_broken_loot_entries(float weight)
    {
        var fixture = new Fixture(new() { [Crate] = Pool(weight) });
        fixture.Register();
        Assert.Empty(Cases(fixture.Load()[Crate]));
    }

    [Fact]
    public void Float_overflow_is_rejected_before_any_case_is_added()
    {
        var fixture = new Fixture(new() { [Crate] = Pool(float.MaxValue) },
            config: """{ "caseLootWeightPercent": 1e100 }""");
        fixture.Register();
        Assert.Empty(Cases(fixture.Load()[Crate]));
    }

    [Fact]
    public void Empty_and_missing_static_data_are_safe()
    {
        var fixture = new Fixture(new() { [Crate] = new StaticLootDetails { ItemDistribution = [] } });
        fixture.Register();
        Assert.Empty(Cases(fixture.Load()[Crate]));
        var missing = new Fixture(new() { [Crate] = new StaticLootDetails() });
        missing.Register();
        Assert.Null(missing.Load()[Crate].ItemDistribution);
        var absent = new Fixture(null);
        absent.Register();
        Assert.Null(absent.Lazy.Value);
    }

    private static ItemDistribution Entry(string id, float weight) => new() { Tpl = id, RelativeProbability = weight };
    private static StaticLootDetails Pool(float weight, float keyWeight = 0) => new()
    {
        ItemDistribution = keyWeight > 0 ? [Entry(OtherItem, weight), Entry(VanillaKey, keyWeight)] : [Entry(OtherItem, weight)],
        ItemCountDistribution = [new ItemCountDistribution { Count = 2, RelativeProbability = 100 }]
    };
    private static ItemDistribution[] Cases(StaticLootDetails details) =>
        details.ItemDistribution.Where(e => CaseContracts.IsCase(e.Tpl.ToString())).ToArray();
    private static Dictionary<MongoId, double> BotPool() =>
        CaseContracts.Templates.ToDictionary(id => (MongoId)id, _ => 100d)
            .Concat(new Dictionary<MongoId, double> { [OtherItem] = 100, [ModConstants.KeyTemplateId] = 2 })
            .ToDictionary(pair => pair.Key, pair => pair.Value);

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

    internal sealed class Fixture
    {
        public int LoadCount { get; private set; }
        public LazyLoad<Dictionary<MongoId, StaticLootDetails>> Lazy { get; }
        public CatalogSnapshotCoordinator Coordinator { get; }
        public BotTable Bots { get; } = new() { Types = [], Base = null!, Core = null! };
        public PmcConfig Pmc { get; } = Activator.CreateInstance<PmcConfig>();
        public LocationTable Locations { get; }
        private readonly ModConfig _config;

        public Fixture(Dictionary<MongoId, StaticLootDetails>? dictionary,
            CatalogSnapshotCoordinator? coordinator = null, bool cache = false, string config = "{}")
        {
            Lazy = new LazyLoad<Dictionary<MongoId, StaticLootDetails>>(() => { LoadCount++; return dictionary!; }, cache);
            Locations = new LocationTable
            {
                Bigmap = new Location { StaticLoot = Lazy }, Base = null!, Factory4Day = null!, Factory4Night = null!,
                Interchange = null!, Laboratory = null!, Lighthouse = null!, RezervBase = null!, Shoreline = null!,
                TarkovStreets = null!, Labyrinth = null!, Woods = null!, Sandbox = null!, SandboxHigh = null!
            };
            Pmc.GlobalLootBlacklist = [];
            _config = ModConfig.Parse(config);
            Coordinator = coordinator ?? new CatalogSnapshotCoordinator(() => CaseCatalogTests.Snapshot(new[]
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
            }), () => CashCacheTests.Catalog());
        }

        public void Register(bool startupComplete = true)
        {
            if (startupComplete) Coordinator.MarkStartupComplete();
            Assert.Equal(1, ContrabandCaseLootInjector.Register(Locations, Bots, Pmc, Coordinator, _config));
        }

        public Dictionary<MongoId, StaticLootDetails> Load() => Lazy.Value!;
    }
}
