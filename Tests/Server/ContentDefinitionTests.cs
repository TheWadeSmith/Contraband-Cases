using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Configuration;
using ContrabandCases.Server.Content;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class ContentDefinitionTests
{
    [Fact]
    public void Themed_cases_publish_only_viable_pools_with_resolved_prices_and_preserve_mixed_settings()
    {
        var templates = KeyOnlyTemplates(65_000);
        foreach (var template in CaseContracts.Templates)
        {
            var details = ContrabandContentDefinitions.CreateCaseCloneDetails(1_000, template);
            templates.Items.Add(template, new TemplateItem
            {
                Id = template,
                Parent = details.ParentId,
                Properties = details.OverrideProperties
            });
            templates.Handbook.Items.Add(new HandbookItem
            {
                Id = template,
                ParentId = ContrabandContentDefinitions.CaseHandbookParentId,
                Price = 1_000
            });
        }
        var mixed = templates.Items[ModConstants.CaseTemplateId].Properties!;
        mixed.CreditsPrice = 87_000;
        var config = ModConfig.Parse("{}");
        var assort = EmptyAssort();
        ContrabandContentDefinitions.ApplyMechanicOffers(assort,
            [new TraderOfferDefinition(ModConstants.MechanicCaseAssortRootId, ModConstants.CaseTemplateId,
                87_000, 5, 5, 1, false)]);
        var catalog = CaseCatalogTests.Snapshot(new[] { "arsenal", "operator", "field-supply" }
            .Select(f => CaseCatalogTests.Lot("core", f, f)));
        var coordinator = new CatalogSnapshotCoordinator(() => catalog);
        coordinator.MarkStartupComplete();

        var published = ContrabandContentDefinitions.PublishThemedCases(templates, assort, config, coordinator);

        var offer = Assert.Single(published);
        Assert.Equal(CaseContracts.Operations, offer.TemplateId.ToString());
        var view = coordinator.GetCaseSnapshot(CaseContracts.Operations);
        Assert.Same(view, coordinator.GetCaseSnapshot(CaseContracts.Operations));
        Assert.Equal(view.CasePrice, offer.PriceRoubles);
        Assert.Equal((double)view.CasePrice!, templates.Items[CaseContracts.Operations].Properties!.CreditsPrice);
        Assert.Equal((double)view.CasePrice!, templates.Prices[CaseContracts.Operations]);
        Assert.Equal((double)view.CasePrice!, templates.Handbook.Items.Single(item => item.Id == (MongoId)CaseContracts.Operations).Price);
        Assert.Equal(87_000, mixed.CreditsPrice);
        Assert.Equal(87_000, assort.BarterScheme[ModConstants.MechanicCaseAssortRootId][0][0].Count);
        Assert.Equal(2, assort.Items.Count);
        Assert.DoesNotContain(assort.Items, item => item.Template == (MongoId)CaseContracts.Relics ||
            item.Template == (MongoId)CaseContracts.BlackSite || item.Template == (MongoId)ModConstants.KeyTemplateId);
        Assert.Throws<InvalidOperationException>(() =>
            ContrabandContentDefinitions.PublishThemedCases(templates, assort, config, coordinator));
        Assert.Equal(2, assort.Items.Count);
    }

    [Theory]
    [InlineData(CaseContracts.Operations, "green")]
    [InlineData(CaseContracts.Relics, "violet")]
    [InlineData(CaseContracts.BlackSite, "red")]
    public void Themed_cases_share_the_bundle_but_have_distinct_labels_and_inventory_colors(string template, string color)
    {
        var details = ContrabandContentDefinitions.CreateCaseCloneDetails(10_000, template);
        Assert.Equal(CaseContracts.Name(template), details.Locales!["en"].Name);
        Assert.Equal(color, details.OverrideProperties!.BackgroundColor);
        Assert.Equal(ModConstants.CaseBundleKey, details.OverrideProperties.Prefab!.Path);
        Assert.Contains(CaseContracts.Description(template), details.Locales["en"].Description!);
    }

    [Fact]
    public void Metadata_uses_the_shared_identity_and_has_no_invented_url_or_prepatcher()
    {
        var metadata = new ContrabandCases.Server.ModMetadata();

        Assert.Equal(ModConstants.ModId, metadata.ModGuid);
        Assert.Equal(ModConstants.ModName, metadata.Name);
        Assert.Equal(ModConstants.ModVersion, metadata.Version.ToString());
        Assert.Equal("Wade", metadata.Author);
        Assert.Equal("~4.1.0", metadata.SptVersion.ToString());
        Assert.Equal("MIT", metadata.License);
        Assert.Null(metadata.Url);
        Assert.False(metadata.HasPrepatcher);
    }

    [Fact]
    public void Fixed_ids_are_unique_lowercase_mongo_ids()
    {
        var ids = new[]
        {
            ModConstants.CaseTemplateId,
            ModConstants.KeyTemplateId,
            ModConstants.MechanicCaseAssortRootId
        };

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches("^[0-9a-f]{24}$", id));
    }

    [Fact]
    public void Item_descriptions_explain_distinct_opening_and_relay_key_costs()
    {
        const string expectedCaseDescription =
            "A sealed Northline Transit escrow case. Opening consumes this case and one BR-12 Relay Key, " +
            "then presents three sequential offers with Lock/Burn decisions on the first two. The selected " +
            "entitlement can be Claimed or Relayed; Relay consumes one additional BR-12 Relay Key.";
        const string expectedKeyDescription =
            "Each key is universal and single-use: one opens any BR-12 case, while a separate key stakes its " +
            "selected entitlement in Relay. Found in raid only -- Mechanic no longer sells it. It has no " +
            "real-money value.";
        var caseDetails = ContrabandContentDefinitions.CreateCaseCloneDetails(70_000);
        var keyDetails = ContrabandContentDefinitions.CreateKeyCloneDetails();
        var caseDescription = Assert.IsType<string>(caseDetails.Locales!["en"].Description);
        var keyDescription = Assert.IsType<string>(keyDetails.Locales!["en"].Description);

        Assert.Equal(expectedCaseDescription, ContrabandContentDefinitions.CaseDescription);
        Assert.Equal(expectedCaseDescription, caseDescription);
        Assert.Equal(expectedCaseDescription, caseDetails.OverrideProperties!.Description);
        Assert.Contains("Opening consumes this case and one BR-12 Relay Key", caseDescription);
        Assert.Contains("three sequential offers with Lock/Burn decisions on the first two", caseDescription);
        Assert.Contains("can be Claimed or Relayed", caseDescription);
        Assert.Contains("Relay consumes one additional BR-12 Relay Key", caseDescription);

        Assert.Equal(expectedKeyDescription, keyDescription);
        Assert.Equal(expectedKeyDescription, keyDetails.OverrideProperties!.Description);
        Assert.Contains("one opens any BR-12 case", keyDescription);
        Assert.Contains("a separate key stakes its selected entitlement in Relay", keyDescription);
        Assert.Contains("Found in raid only", keyDescription);
    }

    [Fact]
    public void Case_and_key_clone_definitions_are_explicit_and_fail_closed()
    {
        var caseDetails = ContrabandContentDefinitions.CreateCaseCloneDetails(70_000);
        var keyDetails = ContrabandContentDefinitions.CreateKeyCloneDetails();

        Assert.Equal("64897ffc3656831810043165", caseDetails.ItemTplToClone.ToString());
        Assert.Equal("62f109593b54472778797866", caseDetails.ParentId.ToString());
        Assert.Equal(ModConstants.CaseTemplateId, caseDetails.NewId.ToString());
        Assert.Equal("5b5f6fa186f77409407a7eb7", caseDetails.HandbookParentId);
        Assert.Equal(ModConstants.CaseBundleKey, caseDetails.OverrideProperties!.Prefab!.Path);
        Assert.Equal(3, caseDetails.OverrideProperties.Width);
        Assert.Equal(2, caseDetails.OverrideProperties.Height);
        Assert.Equal(1, caseDetails.OverrideProperties.StackMaxSize);
        Assert.Equal(70_000, caseDetails.OverrideProperties.CreditsPrice);
        Assert.Equal(
            ContrabandContentDefinitions.CaseDescription,
            caseDetails.Locales!["en"].Description);
        Assert.Equal(
            ContrabandContentDefinitions.CaseDescription,
            caseDetails.OverrideProperties.Description);

        Assert.Equal("593962ca86f774068014d9af", keyDetails.ItemTplToClone.ToString());
        Assert.Equal("5c99f98d86f7745c314214b3", keyDetails.ParentId.ToString());
        Assert.Equal(ModConstants.KeyTemplateId, keyDetails.NewId.ToString());
        Assert.Equal("5c518ec986f7743b68682ce2", keyDetails.HandbookParentId);
        Assert.Equal(ModConstants.KeyBundleKey, keyDetails.OverrideProperties!.Prefab!.Path);
        Assert.Equal(1, keyDetails.OverrideProperties.Width);
        Assert.Equal(1, keyDetails.OverrideProperties.Height);
        Assert.Equal(1, keyDetails.OverrideProperties.StackMaxSize);
        Assert.Equal(1, keyDetails.OverrideProperties.MaxUsages);
        // The key's registration price is now a fixed internal constant (it's find-only, no
        // longer sold, so nothing external drives its price) -- matching its long-standing
        // pre-0.3.18 effective worth so nothing about its implied value actually changed.
        Assert.Equal(65_000, keyDetails.OverrideProperties.CreditsPrice);

        foreach (var details in new[] { caseDetails, keyDetails })
        {
            Assert.True(details.AddToHandbook);
            Assert.True(details.AddToFleaPriceDb);
            Assert.False(details.AddToWeaponShelf);
            Assert.Equal(details.OverrideProperties!.CreditsPrice, details.FleaPriceRoubles);
            Assert.False(details.OverrideProperties!.CanSellOnRagfair);
            Assert.False(details.OverrideProperties.CanRequireOnRagfair);
            Assert.False(details.OverrideProperties.QuestItem);
            Assert.True(details.OverrideProperties.ExaminedByDefault);
            Assert.True(details.OverrideProperties.ForbidNonEmptyContainers);
            Assert.Empty(details.OverrideProperties.Grids!);
            Assert.Empty(details.OverrideProperties.Slots!);
            Assert.Empty(details.OverrideProperties.StackSlots!);
        }
    }

    [Fact]
    public void Mechanic_ll1_offer_uses_default_stock_and_the_full_computed_ticket_price()
    {
        var catalog = Catalog(
            Lot("provider-a", "lot-a", "family-a", 85_000),
            Lot("provider-b", "lot-b", "family-b", 85_000),
            Lot("provider-c", "lot-c", "family-c", 85_000));
        // Uses an isolated config (no fixedCasePrice) rather than the live shipped config.jsonc,
        // so this test always exercises the dynamic computation regardless of whatever fixed
        // price the live config currently ships.
        var config = ModConfig.Parse("{}");

        var prices = ContrabandContentDefinitions.CalculatePrices(catalog, config);
        var offers = ContrabandContentDefinitions.CreateMechanicOffers(prices, config);

        // The case is the only priced item now (the key is find-only), so it receives the full
        // computed ticket total rather than a 70% share of it.
        Assert.Equal(56_000, prices.CasePrice);
        Assert.Single(offers);
        Assert.Collection(
            offers,
            offer => AssertOffer(offer, ModConstants.MechanicCaseAssortRootId, ModConstants.CaseTemplateId, 56_000, 5));

        var assort = EmptyAssort();
        ContrabandContentDefinitions.EnsureMechanicOfferIdsAvailable(assort, offers);
        ContrabandContentDefinitions.ApplyMechanicOffers(assort, offers);

        Assert.Collection(
            assort.Items,
            item => AssertAssortItem(item, ModConstants.MechanicCaseAssortRootId, ModConstants.CaseTemplateId, 5));
        Assert.Equal(56_000, assort.BarterScheme[(MongoId)ModConstants.MechanicCaseAssortRootId][0][0].Count);
        Assert.All(assort.BarterScheme.Values, scheme => Assert.Equal(Money.ROUBLES, Assert.Single(Assert.Single(scheme)).Template));
        Assert.All(assort.LoyalLevelItems.Values, level => Assert.Equal(1, level));
    }

    [Fact]
    public void Typical_reward_price_is_rounded_up_to_one_thousand()
    {
        var catalog = Catalog(
            Lot("provider-a", "lot-a1", "family-a", 850, 0.9d),
            Lot("provider-a", "lot-a2", "family-a", 851, 0.1d),
            Lot("provider-b", "lot-b", "family-b", 850),
            Lot("provider-c", "lot-c", "family-c", 850));

        var prices = ContrabandContentDefinitions.CalculatePrices(catalog, ModConfig.Parse("{}"));

        Assert.Equal(1_000, prices.CasePrice);
    }

    [Fact]
    public void Fixed_case_price_overrides_the_finalized_manifest_value()
    {
        var catalog = Catalog(
            Lot("provider-a", "lot-a", "family-a", 1_000_000),
            Lot("provider-b", "lot-b", "family-b", 1_000_000),
            Lot("provider-c", "lot-c", "family-c", 1_000_000));
        var config = ModConfig.Parse("""{ "fixedCasePrice": 123456 }""");

        var prices = ContrabandContentDefinitions.CalculatePrices(catalog, config);

        Assert.Equal(123_456, prices.CasePrice);
    }

    [Fact]
    public void Registration_prices_are_positive_and_preserve_fixed_overrides()
    {
        var automatic = ContrabandContentDefinitions.CalculateRegistrationPrices(ModConfig.Parse("{}"));
        var configured = ContrabandContentDefinitions.CalculateRegistrationPrices(ModConfig.Parse("""
            { "fixedCasePrice": 80000 }
            """));

        Assert.Equal(1_000L, automatic.CasePrice);
        Assert.Equal(80_000L, configured.CasePrice);
    }

    [Fact]
    public void Fixed_case_price_must_be_positive_when_set()
    {
        Assert.Throws<InvalidOperationException>(() => ModConfig.Parse("""
            { "traderStock":5, "animationDurationSeconds":4.5, "fixedCasePrice":0 }
            """));
    }

    [Fact]
    public void Apply_key_registration_price_is_a_noop_without_a_therapist_target()
    {
        var templates = KeyOnlyTemplates(65_000);

        ContrabandContentDefinitions.ApplyKeyRegistrationPrice(templates, ModConfig.Parse("{}"));

        Assert.Equal(65_000d, templates.Items[(MongoId)ModConstants.KeyTemplateId].Properties!.CreditsPrice);
        Assert.Equal(
            65_000d,
            Assert.Single(templates.Handbook.Items, entry => entry.Id == (MongoId)ModConstants.KeyTemplateId).Price);
    }

    [Fact]
    public void Apply_key_registration_price_overrides_credits_and_handbook_price_for_configured_therapist_target()
    {
        var templates = KeyOnlyTemplates(65_000);
        var config = ModConfig.Parse("""{ "therapistSellPriceKey": 65000 }""");

        ContrabandContentDefinitions.ApplyKeyRegistrationPrice(templates, config);

        var properties = templates.Items[(MongoId)ModConstants.KeyTemplateId].Properties!;
        var handbook = Assert.Single(
            templates.Handbook.Items,
            entry => entry.Id == (MongoId)ModConstants.KeyTemplateId);
        // ceil(65000 / 0.63), the same Therapist 37% coefficient used elsewhere in this file.
        Assert.Equal(103_175d, properties.CreditsPrice);
        Assert.Equal(103_175d, handbook.Price);
    }

    [Fact]
    public void Native_random_container_registration_is_rejected()
    {
        var nativeRewards = new Dictionary<MongoId, RewardDetails>();
        ContrabandContentDefinitions.EnsureNativeRandomLootRouteUnavailable(nativeRewards);

        nativeRewards[(MongoId)ModConstants.CaseTemplateId] = new RewardDetails();

        Assert.Throws<InvalidOperationException>(() =>
            ContrabandContentDefinitions.EnsureNativeRandomLootRouteUnavailable(nativeRewards));
    }

    [Fact]
    public void Router_request_validation_accepts_only_the_custom_action_and_nonempty_case_id()
    {
        var valid = new OpenRandomLootContainerRequestData
        {
            Action = ModConstants.OpenAction,
            Item = "aaaaaaaaaaaaaaaaaaaaaaaa"
        };

        ContrabandCaseRouter.ValidateRequest(ModConstants.OpenAction, valid);
        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateRequest("OpenRandomLootContainer", valid));
        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateRequest(ModConstants.OpenAction, valid with { Action = "OpenRandomLootContainer" }));
        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateRequest(ModConstants.OpenAction, valid with { Item = default }));
    }

    [Theory]
    [InlineData(ModConstants.RelaySecureAction)]
    [InlineData(ModConstants.RelayAction)]
    public void Relay_router_accepts_only_matching_action_and_nonempty_authoritative_stake_root(string action)
    {
        var valid = new OpenRandomLootContainerRequestData
        {
            Action = action,
            Item = "aaaaaaaaaaaaaaaaaaaaaaaa"
        };

        ContrabandCaseRouter.ValidateRelayRequest(action, valid);
        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateRelayRequest(action, valid with { Action = ModConstants.OpenAction }));
        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateRelayRequest(ModConstants.OpenAction, valid));
        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateRelayRequest(action, valid with { Item = default }));
    }

    [Fact]
    public void Testing_grant_router_accepts_only_the_exact_action_and_bounded_counts()
    {
        var valid = new TestingInventoryGrantRequestData
        {
            Action = ModConstants.TestingInventoryGrantAction,
            CaseCount = 5,
            KeyCount = 10
        };

        ContrabandCaseRouter.ValidateTestingInventoryGrantRequest(
            ModConstants.TestingInventoryGrantAction,
            valid);
        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateTestingInventoryGrantRequest(ModConstants.OpenAction, valid));
        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateTestingInventoryGrantRequest(
                ModConstants.TestingInventoryGrantAction,
                new TestingInventoryGrantRequestData
                {
                    Action = ModConstants.OpenAction,
                    CaseCount = 5,
                    KeyCount = 10
                }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContrabandCaseRouter.ValidateTestingInventoryGrantRequest(
                ModConstants.TestingInventoryGrantAction,
                new TestingInventoryGrantRequestData
                {
                    Action = ModConstants.TestingInventoryGrantAction,
                    CaseCount = 10,
                    KeyCount = 40
                }));
    }

    private static void AssertOffer(
        TraderOfferDefinition offer,
        string expectedAssortId,
        string expectedTemplateId,
        long expectedPrice,
        int expectedStock)
    {
        Assert.Equal(expectedAssortId, offer.AssortId.ToString());
        Assert.Equal(expectedTemplateId, offer.TemplateId.ToString());
        Assert.Equal(expectedPrice, offer.PriceRoubles);
        Assert.Equal(expectedStock, offer.Stock);
        Assert.Equal(expectedStock, offer.BuyRestriction);
        Assert.Equal(1, offer.LoyaltyLevel);
        Assert.False(offer.FoundInRaid);
    }

    private static void AssertAssortItem(Item item, string expectedId, string expectedTemplate, int expectedStock)
    {
        Assert.Equal(expectedId, item.Id.ToString());
        Assert.Equal(expectedTemplate, item.Template.ToString());
        Assert.Equal("hideout", item.ParentId);
        Assert.Equal("hideout", item.SlotId);
        Assert.NotNull(item.Upd);
        Assert.Equal(expectedStock, item.Upd!.StackObjectsCount);
        Assert.Equal(expectedStock, item.Upd.BuyRestrictionMax);
        Assert.Equal(0, item.Upd.BuyRestrictionCurrent);
        Assert.False(item.Upd.UnlimitedCount);
        Assert.False(item.Upd.SpawnedInSession);
    }

    private static TraderAssort EmptyAssort() => new()
    {
        Items = [],
        BarterScheme = [],
        LoyalLevelItems = []
    };

    private static TemplateTable KeyOnlyTemplates(double keyPrice) => new()
    {
        Character = [],
        CustomisationStorage = [],
        Items = new Dictionary<MongoId, TemplateItem>
        {
            [(MongoId)ModConstants.KeyTemplateId] = new TemplateItem
            {
                Id = ModConstants.KeyTemplateId,
                Parent = ContrabandContentDefinitions.KeyParentId,
                Properties = new TemplateItemProperties { CreditsPrice = keyPrice }
            }
        },
        Prestige = null!,
        Quests = [],
        RepeatableQuests = null!,
        Handbook = new HandbookBase
        {
            Categories = [],
            Items =
            [
                new HandbookItem
                {
                    Id = ModConstants.KeyTemplateId,
                    ParentId = ContrabandContentDefinitions.KeyHandbookParentId,
                    Price = keyPrice
                }
            ]
        },
        Customization = [],
        Dialogue = null!,
        Profiles = [],
        Prices = [],
        DefaultEquipmentPresets = [],
        Achievements = [],
        CustomAchievements = [],
        LocationServices = null!
    };

    private static string ConfigPath(string fileName) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../config", fileName));

    private static CargoCatalogSnapshot Catalog(params ResolvedCargoLot[] lots) =>
        new(
            new string('a', 64),
            lots,
            [],
            lots
                .Select(lot => lot.Identity.ProviderId)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(providerId => providerId, _ => 1d, StringComparer.Ordinal));

    private static ResolvedCargoLot Lot(
        string providerId,
        string lotId,
        string familyId,
        long handbookValue,
        double weight = 1d)
    {
        var templateId = string.Concat("template-", lotId);
        var definition = new CargoLotDefinition(
            providerId,
            "1.0.0",
            lotId,
            string.Concat("Display ", lotId),
            string.Concat("Purpose ", lotId),
            new FamilyId(familyId),
            new TrackId(string.Concat("track-", lotId)),
            templateId,
            weight,
            new RaidRole("testing"),
            [new TemplateLine(templateId, 1, 1)]);
        var forest = RewardForest.Create(
            [new RewardForestNode("root", "root", templateId, null, null, null, 1)]);
        var fingerprint = RewardForestFingerprintV2.Compute(providerId, lotId, forest);
        var identity = CargoLotIdentitySnapshot.Capture(definition, fingerprint);
        return new ResolvedCargoLot(
            definition,
            forest,
            fingerprint,
            identity,
            new CargoLotEvaluation(
                handbookValue,
                handbookValue,
                1,
                CargoGradeBands.Assign(handbookValue)));
    }
}
