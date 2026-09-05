using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Xunit;

namespace ContrabandCases.Tests.Catalog;

public sealed class CargoLotResolverResourceTests
{
    private const string ParentTemplateId = "54009119af1c881c07000029";
    private const string MedTemplateId = "100000000000000000000001";
    private const string RepairTemplateId = "100000000000000000000002";
    private const string FoodTemplateId = "100000000000000000000003";
    private const string GenericTemplateId = "100000000000000000000004";
    private const string PresetId = "200000000000000000000001";
    private const string SourceId = "300000000000000000000001";

    [Fact]
    public void Preset_resolution_preserves_each_exact_resource_upd_representation()
    {
        var cases = new[]
        {
            new ResourceCase(
                MedTemplateId,
                RewardResourceKind.MedKit,
                4m,
                12m,
                Template(MedTemplateId, maxHpResource: 12),
                new Upd { MedKit = new UpdMedKit { HpResource = 4 } }),
            new ResourceCase(
                RepairTemplateId,
                RewardResourceKind.RepairKit,
                5m,
                13m,
                Template(RepairTemplateId, maxRepairResource: 13),
                new Upd { RepairKit = new UpdRepairKit { Resource = 5 } }),
            new ResourceCase(
                FoodTemplateId,
                RewardResourceKind.FoodDrink,
                6m,
                14m,
                Template(FoodTemplateId, maxResource: 14),
                new Upd { FoodDrink = new UpdFoodDrink { HpPercent = 6 } }),
            new ResourceCase(
                GenericTemplateId,
                RewardResourceKind.Generic,
                7m,
                15m,
                Template(GenericTemplateId, maxResource: 15, resource: 9),
                new Upd { Resource = new UpdResource { Value = 7 } })
        };

        foreach (var testCase in cases)
        {
            var state = ResolvePresetState(testCase.Template, testCase.Upd);

            Assert.Equal(testCase.Kind, state.ResourceKind);
            Assert.Equal(testCase.Value, state.ResourceValue);
            Assert.Equal(testCase.Maximum, state.MaximumResourceValue);
        }
    }

    [Fact]
    public void Template_resolution_derives_only_one_finalized_resource_representation()
    {
        var templates = new[]
        {
            Template(MedTemplateId, maxHpResource: 12),
            Template(RepairTemplateId, maxRepairResource: 13),
            Template(FoodTemplateId, maxResource: 14),
            Template(GenericTemplateId, maxResource: 15, resource: 9)
        };
        var resolver = Resolver(templates);
        var resolved = resolver.Resolve(Lot(
            MedTemplateId,
            [
                new TemplateLine(MedTemplateId, 1, 1),
                new TemplateLine(RepairTemplateId, 1, 1),
                new TemplateLine(FoodTemplateId, 1, 1),
                new TemplateLine(GenericTemplateId, 1, 1)
            ]));
        var byTemplate = resolved.Forest.Nodes.ToDictionary(node => node.TemplateId);

        Assert.Equal(RewardResourceKind.MedKit, byTemplate[MedTemplateId].StableState?.ResourceKind);
        Assert.Equal(12m, byTemplate[MedTemplateId].StableState?.ResourceValue);
        Assert.Equal(RewardResourceKind.RepairKit, byTemplate[RepairTemplateId].StableState?.ResourceKind);
        Assert.Equal(13m, byTemplate[RepairTemplateId].StableState?.ResourceValue);
        Assert.Equal(RewardResourceKind.FoodDrink, byTemplate[FoodTemplateId].StableState?.ResourceKind);
        Assert.Equal(14m, byTemplate[FoodTemplateId].StableState?.ResourceValue);
        Assert.Equal(RewardResourceKind.Generic, byTemplate[GenericTemplateId].StableState?.ResourceKind);
        Assert.Equal(9m, byTemplate[GenericTemplateId].StableState?.ResourceValue);
        Assert.Equal(15m, byTemplate[GenericTemplateId].StableState?.MaximumResourceValue);
    }

    [Fact]
    public void Template_resolution_rejects_ambiguous_resource_property_families()
    {
        var ambiguous = Template(
            MedTemplateId,
            maxHpResource: 10,
            maxRepairResource: 20);

        Assert.Throws<CargoCatalogValidationException>(() =>
            Resolver([ambiguous]).Resolve(Lot(
                MedTemplateId,
                [new TemplateLine(MedTemplateId, 1, 1)])));
    }

    [Fact]
    public void Explicit_preset_upd_disambiguates_conflicting_template_resource_properties()
    {
        var ambiguous = Template(
            MedTemplateId,
            maxHpResource: 10,
            maxRepairResource: 20);
        var state = ResolvePresetState(
            ambiguous,
            new Upd { MedKit = new UpdMedKit { HpResource = 6 } });

        Assert.Equal(RewardResourceKind.MedKit, state.ResourceKind);
        Assert.Equal(6m, state.ResourceValue);
        Assert.Equal(10m, state.MaximumResourceValue);
    }

    [Fact]
    public void Preset_resolution_rejects_multiple_resource_upd_representations()
    {
        var template = Template(MedTemplateId, maxHpResource: 10, maxResource: 10, resource: 10);
        var upd = new Upd
        {
            MedKit = new UpdMedKit { HpResource = 6 },
            Resource = new UpdResource { Value = 6 }
        };

        Assert.Throws<CargoCatalogValidationException>(() => ResolvePresetState(template, upd));
    }

    private static RewardStableState ResolvePresetState(TemplateItem template, Upd upd)
    {
        var preset = new Preset
        {
            Id = PresetId,
            Parent = SourceId,
            Items =
            [
                new Item
                {
                    Id = SourceId,
                    Template = template.Id,
                    Upd = upd
                }
            ]
        };
        var resolved = Resolver([template], [preset]).Resolve(
            Lot(template.Id.ToString(), [new PresetLine(PresetId)]));
        return Assert.Single(resolved.Forest.Nodes).StableState
            ?? throw new Xunit.Sdk.XunitException("Expected a canonical stable state.");
    }

    private static CargoLotResolver Resolver(
        IEnumerable<TemplateItem> templates,
        IEnumerable<Preset>? presets = null)
    {
        var templateMap = templates.ToDictionary(
            template => template.Id.ToString(),
            StringComparer.Ordinal);
        var presetMap = (presets ?? []).ToDictionary(
            preset => preset.Id.ToString(),
            StringComparer.Ordinal);
        return new CargoLotResolver(new CargoLotResolverDependencies(
            id => templateMap.GetValueOrDefault(id),
            id => presetMap.GetValueOrDefault(id)));
    }

    private static CargoLotDefinition Lot(
        string anchorTemplateId,
        IEnumerable<RewardRecipeLine> lines) =>
        new(
            "core",
            "0.3.0",
            "resource-test",
            "Resource Test",
            "Exercises canonical resource state.",
            new FamilyId("field-supply"),
            new TrackId("testing"),
            anchorTemplateId,
            1d,
            new RaidRole("testing"),
            lines);

    private static TemplateItem Template(
        string id,
        int? maxHpResource = null,
        int? maxRepairResource = null,
        int? maxResource = null,
        double? resource = null) =>
        new()
        {
            Id = id,
            Parent = ParentTemplateId,
            Name = id,
            Type = "Item",
            Properties = new TemplateItemProperties
            {
                StackMaxSize = 10,
                Width = 1,
                Height = 1,
                MaxHpResource = maxHpResource,
                MaxRepairResource = maxRepairResource,
                MaxResource = maxResource,
                Resource = resource,
                Prefab = new Prefab { Path = $"test/{id}.bundle" }
            }
        };

    private sealed record ResourceCase(
        string TemplateId,
        RewardResourceKind Kind,
        decimal Value,
        decimal Maximum,
        TemplateItem Template,
        Upd Upd);
}
