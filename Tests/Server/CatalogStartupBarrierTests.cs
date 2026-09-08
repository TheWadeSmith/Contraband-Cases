using System.Text.Json;
using ContrabandCases.Client.Opening;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Configuration;
using ContrabandCases.Server.Content;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Economy;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class CatalogStartupBarrierTests
{
    [Fact]
    public void Finalize_startup_opens_gate_freezes_once_and_updates_all_three_case_price_sinks()
    {
        var catalog = Catalog(85_000);
        var freezeCount = 0;
        var coordinator = new CatalogSnapshotCoordinator(() =>
        {
            freezeCount++;
            return catalog;
        });
        var templates = RegisteredTemplates(700);
        var assort = RegisteredAssort(ModConfig.Parse("{}"));

        Assert.False(coordinator.IsStartupComplete);
        Assert.False(coordinator.IsFrozen);
        Assert.Throws<InvalidOperationException>(() => coordinator.GetSnapshot());

        var prices = CatalogStartupBarrier.FinalizeStartup(
            coordinator,
            ModConfig.Parse("{}"),
            templates,
            assort);

        Assert.Equal(2_000L, prices.CasePrice);
        Assert.True(coordinator.IsStartupComplete);
        Assert.True(coordinator.IsFrozen);
        Assert.Same(catalog, coordinator.GetSnapshot());
        Assert.Same(catalog, coordinator.GetSnapshot());
        Assert.Equal(1, freezeCount);
        Assert.Equal([2_000d, 2_000d, 2_000d], CapturePrices(templates, assort));
        Assert.Equal(2_000d, templates.Prices[(MongoId)ModConstants.CaseTemplateId]);
        var published = coordinator.GetCaseSnapshot(ModConstants.CaseTemplateId);
        Assert.Equal(2_000L, ManifestOpeningOdds.Create(published).CasePrice);
        Assert.Same(published, coordinator.GetCaseSnapshot(ModConstants.CaseTemplateId));
        Assert.NotEqual(catalog.SnapshotId, published.SnapshotId);
        Assert.Equal(catalog.Lots, published.Lots);
        Assert.Null(catalog.CasePrice);
        var response = ManifestSnapshotRouter.CreateCurrentState(new CaseOpeningJournal(), published, null);
        var client = ContrabandCases.Client.Opening.ManifestSnapshotEnvelope.ParseCurrent(
            JsonSerializer.Serialize(new { err = 0, errmsg = (string?)null, data = response }));
        Assert.Equal(2_000L, client.OpeningOdds!.CasePrice);
        Assert.Contains("₽2,000", ManifestPresentationPolicy.OpeningSummaryText(client.OpeningOdds));
    }

    [Fact]
    public void Finalize_startup_preserves_fixed_price_override_in_every_sink()
    {
        var coordinator = new CatalogSnapshotCoordinator(() => Catalog(1_000_000));
        var templates = RegisteredTemplates(80_000);
        var config = ModConfig.Parse("""{ "fixedCasePrice": 80000 }""");
        var assort = RegisteredAssort(config);

        var prices = CatalogStartupBarrier.FinalizeStartup(
            coordinator,
            config,
            templates,
            assort);

        Assert.Equal(80_000L, prices.CasePrice);
        Assert.Equal([80_000d, 80_000d, 80_000d], CapturePrices(templates, assort));
        Assert.Equal(80_000L, ManifestOpeningOdds.Create(coordinator.GetCaseSnapshot(ModConstants.CaseTemplateId)).CasePrice);
    }

    [Fact]
    public void Finalize_startup_decouples_handbook_price_from_mechanic_price_for_configured_therapist_target()
    {
        var coordinator = new CatalogSnapshotCoordinator(() => Catalog(1_000_000));
        var templates = RegisteredTemplates(150_000);
        var config = ModConfig.Parse("""
            { "fixedCasePrice": 150000, "therapistSellPriceCase": 150000 }
            """);
        var assort = RegisteredAssort(config);

        CatalogStartupBarrier.FinalizeStartup(coordinator, config, templates, assort);

        var captured = CapturePrices(templates, assort);
        // CreditsPrice/Barter stay at the Mechanic purchase price (150000), but the handbook price is
        // scaled up so Therapist's live 37% buy coefficient still resolves to (at least) 150000.
        Assert.Equal(150_000d, captured[0]); // case CreditsPrice
        Assert.Equal(238_096d, captured[1]); // case handbook price
        Assert.Equal(150_000d, captured[2]); // case Mechanic barter price
        Assert.Equal(150_000L, ManifestOpeningOdds.Create(coordinator.GetCaseSnapshot(ModConstants.CaseTemplateId)).CasePrice);
    }

    [Fact]
    public void Recovery_only_two_family_catalogue_starts_and_finalizes_prices_without_enabling_openings()
    {
        var catalog = Catalog(85_000, familyCount: 2);
        var coordinator = new CatalogSnapshotCoordinator(() => catalog);
        var templates = RegisteredTemplates(700);
        var assort = RegisteredAssort(ModConfig.Parse("{}"));

        var prices = CatalogStartupBarrier.FinalizeStartup(
            coordinator,
            ModConfig.Parse("{}"),
            templates,
            assort);

        Assert.False(catalog.OpeningEnabled);
        Assert.NotNull(catalog.OpeningDisabledReason);
        Assert.True(coordinator.IsStartupComplete);
        Assert.True(coordinator.IsFrozen);
        Assert.Equal(100_000L, prices.CasePrice);
        Assert.Equal([100_000d, 100_000d, 100_000d], CapturePrices(templates, assort));
        var published = coordinator.GetCaseSnapshot(ModConstants.CaseTemplateId);
        Assert.False(published.OpeningEnabled);
        Assert.Equal(100_000L, published.CasePrice);
        Assert.Equal(catalog.Lots, published.Lots);
    }

    [Fact]
    public void Mixed_price_change_changes_opening_authority_without_rewriting_saved_lots()
    {
        var source = Catalog(85_000);
        CargoCatalogSnapshot Published(long price)
        {
            var config = ModConfig.Parse($"{{\"fixedCasePrice\":{price}}}");
            var coordinator = new CatalogSnapshotCoordinator(() => source);
            CatalogStartupBarrier.FinalizeStartup(coordinator, config, RegisteredTemplates(700), RegisteredAssort(config));
            return coordinator.GetCaseSnapshot(ModConstants.CaseTemplateId);
        }

        var first = Published(80_000);
        var samePrice = Published(80_000);
        var changed = Published(90_000);
        Assert.Equal(first.SnapshotId, samePrice.SnapshotId);
        Assert.NotEqual(first.SnapshotId, changed.SnapshotId);
        var saved = source.Lots[0];
        Assert.Same(saved, changed.ResolveExact(saved.Evaluation.Grade, saved.Identity, saved.Forest, saved.Fingerprint));
    }

    [Fact]
    public void Malformed_barter_fails_during_preflight_before_any_sink_changes()
    {
        var coordinator = new CatalogSnapshotCoordinator(() => Catalog(85_000));
        var templates = RegisteredTemplates(700);
        var assort = RegisteredAssort(ModConfig.Parse("{}"));
        var caseBarter = Barter(assort, ModConstants.MechanicCaseAssortRootId);
        caseBarter.Template = ModConstants.KeyTemplateId; // No longer a rouble barter -- malformed.
        var before = CapturePrices(templates, assort);

        Assert.Throws<InvalidOperationException>(() =>
            CatalogStartupBarrier.FinalizeStartup(
                coordinator,
                ModConfig.Parse("{}"),
                templates,
                assort));

        Assert.Equal(before, CapturePrices(templates, assort));
    }

    [Fact]
    public void Non_exact_spt_double_price_fails_before_any_sink_changes()
    {
        var templates = RegisteredTemplates(700);
        var assort = RegisteredAssort(ModConfig.Parse("{}"));
        var before = CapturePrices(templates, assort);
        const long tooLarge = 9_007_199_254_740_993;

        Assert.Throws<InvalidOperationException>(() =>
            ContrabandContentDefinitions.ApplyFinalizedPrices(
                templates,
                assort,
                new TicketPrices(tooLarge),
                ModConfig.Parse("{}")));

        Assert.Equal(before, CapturePrices(templates, assort));
    }

    [Fact]
    public void Content_state_is_initialized_exactly_once()
    {
        var state = new ContrabandContentState();
        var config = ModConfig.Parse("{}");

        Assert.Throws<InvalidOperationException>(() => state.RequireConfig());
        state.Initialize(config);

        Assert.Same(config, state.RequireConfig());
        Assert.Throws<InvalidOperationException>(() => state.Initialize(ModConfig.Parse("{}")));
        Assert.Same(config, state.RequireConfig());
    }

    private static CargoCatalogSnapshot Catalog(long handbookValue, int familyCount = 3)
    {
        var lots = Enumerable.Range(0, familyCount)
            .Select(index => Lot(
                $"provider-{index}",
                $"lot-{index}",
                $"family-{index}",
                handbookValue))
            .ToArray();
        return new CargoCatalogSnapshot(
            new string('c', 64),
            lots,
            [],
            lots.ToDictionary(
                lot => lot.Identity.ProviderId,
                _ => 1d,
                StringComparer.Ordinal));
    }

    private static ResolvedCargoLot Lot(
        string providerId,
        string lotId,
        string familyId,
        long handbookValue)
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
            1d,
            new RaidRole("testing"),
            [new TemplateLine(templateId, 1, 1)]);
        var forest = RewardForest.Create(
            [new RewardForestNode("root", "root", templateId, null, null, null, 1)]);
        var fingerprint = RewardForestFingerprintV2.Compute(providerId, lotId, forest);
        return new ResolvedCargoLot(
            definition,
            forest,
            fingerprint,
            CargoLotIdentitySnapshot.Capture(definition, fingerprint),
            new CargoLotEvaluation(
                handbookValue,
                handbookValue,
                1,
                CargoGradeBands.Assign(handbookValue)));
    }

    private static TemplateTable RegisteredTemplates(double casePrice)
    {
        return new TemplateTable
        {
            Character = [],
            CustomisationStorage = [],
            Items = new Dictionary<MongoId, TemplateItem>
            {
                [(MongoId)ModConstants.CaseTemplateId] = RegisteredTemplate(
                    ModConstants.CaseTemplateId,
                    ContrabandContentDefinitions.CaseParentId,
                    casePrice)
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
                        Id = ModConstants.CaseTemplateId,
                        ParentId = ContrabandContentDefinitions.CaseHandbookParentId,
                        Price = casePrice
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
    }

    private static TemplateItem RegisteredTemplate(string id, string parentId, double price) => new()
    {
        Id = id,
        Parent = parentId,
        Properties = new TemplateItemProperties { CreditsPrice = price }
    };

    private static TraderAssort RegisteredAssort(ModConfig config)
    {
        var assort = new TraderAssort
        {
            Items = [],
            BarterScheme = [],
            LoyalLevelItems = []
        };
        var prices = ContrabandContentDefinitions.CalculateRegistrationPrices(config);
        ContrabandContentDefinitions.ApplyMechanicOffers(
            assort,
            ContrabandContentDefinitions.CreateMechanicOffers(prices, config));
        return assort;
    }

    private static double?[] CapturePrices(TemplateTable templates, TraderAssort assort) =>
    [
        templates.Items[(MongoId)ModConstants.CaseTemplateId].Properties!.CreditsPrice,
        Assert.Single(
            templates.Handbook.Items,
            entry => entry.Id == (MongoId)ModConstants.CaseTemplateId).Price,
        Barter(assort, ModConstants.MechanicCaseAssortRootId).Count
    ];

    private static BarterScheme Barter(TraderAssort assort, string assortId) =>
        Assert.Single(Assert.Single(assort.BarterScheme[(MongoId)assortId]));
}
