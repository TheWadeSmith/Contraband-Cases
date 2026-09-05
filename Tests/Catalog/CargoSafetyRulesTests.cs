using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Xunit;

namespace ContrabandCases.Tests.Catalog;

public sealed class CargoSafetyRulesTests
{
    private const string ItemRootId = "54009119af1c881c07000029";
    private const string AllowedCategoryId = "700000000000000000000001";
    private const string ParentTemplateId = "700000000000000000000002";
    private const string ChildTemplateId = "700000000000000000000003";
    private const string SecondChildTemplateId = "700000000000000000000004";
    private const string PresetId = "700000000000000000000005";
    private const string RootSourceId = "700000000000000000000006";
    private const string ChildSourceId = "700000000000000000000007";
    private const string SecondChildSourceId = "700000000000000000000008";

    [Fact]
    public void RewardForest_stops_after_the_bounded_overflow_element()
    {
        var produced = 0;

        Assert.Throws<CargoCatalogValidationException>(() =>
            RewardForest.Create(UnboundedNodes()));
        Assert.Equal(RewardForest.MaxNodeCount + 1, produced);

        IEnumerable<RewardForestNode> UnboundedNodes()
        {
            while (true)
            {
                var path = $"root-{produced:D4}";
                produced++;
                yield return Node(path, path, ParentTemplateId);
            }
        }
    }

    [Fact]
    public void Materializer_stops_after_the_bounded_existing_id_overflow_element()
    {
        var forest = RewardForest.Create(
            [Node("root", "root", ParentTemplateId)]);
        var template = Template(ParentTemplateId);
        var enumerated = 0;

        Assert.Throws<CargoCatalogValidationException>(() =>
            Materializer([template]).Materialize(forest, UnboundedExistingIds()));
        Assert.Equal(CargoLotMaterializer.MaxExistingIdCount + 1, enumerated);

        IEnumerable<MongoId> UnboundedExistingIds()
        {
            while (true)
            {
                enumerated++;
                yield return "aaaaaaaaaaaaaaaaaaaaaaaa";
            }
        }
    }

    [Fact]
    public void Preset_rejects_every_representative_noncanonical_upd_family()
    {
        var unsupported = new Upd[]
        {
            new() { Sight = new UpdSight() },
            new() { Map = new UpdMap() },
            new() { FireMode = new UpdFireMode() },
            new() { Foldable = new UpdFoldable() }
        };

        foreach (var upd in unsupported)
        {
            Assert.Throws<CargoCatalogValidationException>(() =>
                ResolveSinglePreset(Template(ParentTemplateId), upd));
        }
    }

    [Fact]
    public void Preset_rejects_incomplete_or_unmodelled_nested_upd_state()
    {
        Assert.Throws<CargoCatalogValidationException>(() =>
            ResolveSinglePreset(
                Template(ParentTemplateId, maximumDurability: 100),
                new Upd { Repairable = new UpdRepairable() }));
        Assert.Throws<CargoCatalogValidationException>(() =>
            ResolveSinglePreset(
                Template(ParentTemplateId, maxResource: 10, resource: 10),
                new Upd
                {
                    Resource = new UpdResource { Value = 5, UnitsConsumed = 0 }
                }));
    }

    [Fact]
    public void Durability_instance_state_requires_a_positive_finalized_maximum()
    {
        Assert.Throws<CargoCatalogValidationException>(() =>
            ResolveSinglePreset(
                Template(ParentTemplateId),
                new Upd
                {
                    Repairable = new UpdRepairable
                    {
                        Durability = 5,
                        MaxDurability = 10
                    }
                }));

        var forest = RewardForest.Create(
        [
            Node(
                "root",
                "root",
                ParentTemplateId,
                stableState: new RewardStableState(5m, 10m))
        ]);
        Assert.Throws<CargoCatalogValidationException>(() =>
            Materializer([Template(ParentTemplateId)]).Materialize(forest, []));
    }

    [Fact]
    public void Preset_rejects_missing_slots_filters_duplicate_occupancy_and_mandatory_gaps()
    {
        var child = Template(ChildTemplateId);
        var missingSlotParent = Template(ParentTemplateId);
        Assert.Throws<CargoCatalogValidationException>(() =>
            ResolvePreset(
                [missingSlotParent, child],
                PresetWithChildren(
                    new Item
                    {
                        Id = ChildSourceId,
                        Template = ChildTemplateId,
                        ParentId = RootSourceId,
                        SlotId = "mod"
                    })));

        var rejectingParent = Template(
            ParentTemplateId,
            slots: [SlotTarget("mod", SecondChildTemplateId)]);
        Assert.Throws<CargoCatalogValidationException>(() =>
            ResolvePreset(
                [rejectingParent, child],
                PresetWithChildren(
                    new Item
                    {
                        Id = ChildSourceId,
                        Template = ChildTemplateId,
                        ParentId = RootSourceId,
                        SlotId = "mod"
                    })));

        var acceptingParent = Template(
            ParentTemplateId,
            slots: [SlotTarget("mod", ChildTemplateId)]);
        Assert.Throws<CargoCatalogValidationException>(() =>
            ResolvePreset(
                [acceptingParent, child],
                PresetWithChildren(
                    new Item
                    {
                        Id = ChildSourceId,
                        Template = ChildTemplateId,
                        ParentId = RootSourceId,
                        SlotId = "mod"
                    },
                    new Item
                    {
                        Id = SecondChildSourceId,
                        Template = ChildTemplateId,
                        ParentId = RootSourceId,
                        SlotId = "mod"
                    })));

        var requiredParent = Template(
            ParentTemplateId,
            slots: [SlotTarget("mandatory", ChildTemplateId, required: true)]);
        Assert.Throws<CargoCatalogValidationException>(() =>
            ResolvePreset([requiredParent], PresetWithChildren()));
    }

    [Fact]
    public void Preset_validates_grid_rotation_bounds_and_overlap()
    {
        var parent = Template(
            ParentTemplateId,
            grids: [GridTarget("main", 3, 2, ChildTemplateId)]);
        var child = Template(ChildTemplateId, width: 2, height: 1);
        var vertical = ResolvePreset(
            [parent, child],
            PresetWithChildren(
                GridChild(ChildSourceId, 2, 0, ItemRotation.Vertical)));
        Assert.Equal(
            CanonicalRotation.Vertical,
            vertical.Forest.Nodes.Single(node => node.TemplateId == ChildTemplateId)
                .InternalLocation?.Rotation);

        Assert.Throws<CargoCatalogValidationException>(() =>
            ResolvePreset(
                [parent, child],
                PresetWithChildren(
                    GridChild(ChildSourceId, 2, 0, ItemRotation.Horizontal))));

        Assert.Throws<CargoCatalogValidationException>(() =>
            ResolvePreset(
                [parent, child],
                PresetWithChildren(
                    GridChild(ChildSourceId, 0, 0, ItemRotation.Horizontal),
                    GridChild(SecondChildSourceId, 1, 0, ItemRotation.Horizontal))));
    }

    [Fact]
    public void Preset_accepts_current_only_internal_rotation()
    {
        var resolved = ResolveGridPreset(
            new ItemLocation { X = 0, Y = 0, R = ItemRotation.Vertical });

        Assert.Equal(
            CanonicalRotation.Vertical,
            resolved.Forest.Nodes.Single(node => node.TemplateId == ChildTemplateId)
                .InternalLocation?.Rotation);
    }

    [Fact]
    public void Preset_accepts_legacy_only_internal_rotation()
    {
        var resolved = ResolveGridPreset(
            new ItemLocation { X = 0, Y = 0, Rotation = true });

        Assert.Equal(
            CanonicalRotation.Vertical,
            resolved.Forest.Nodes.Single(node => node.TemplateId == ChildTemplateId)
                .InternalLocation?.Rotation);
    }

    [Fact]
    public void Preset_accepts_equal_current_and_legacy_internal_rotations()
    {
        var resolved = ResolveGridPreset(
            new ItemLocation
            {
                X = 0,
                Y = 0,
                R = ItemRotation.Vertical,
                Rotation = true
            });

        Assert.Equal(
            CanonicalRotation.Vertical,
            resolved.Forest.Nodes.Single(node => node.TemplateId == ChildTemplateId)
                .InternalLocation?.Rotation);
    }

    [Fact]
    public void Preset_rejects_conflicting_current_and_legacy_internal_rotations()
    {
        Assert.Throws<CargoCatalogValidationException>(() =>
            ResolveGridPreset(
                new ItemLocation
                {
                    X = 0,
                    Y = 0,
                    R = ItemRotation.Vertical,
                    Rotation = false
                }));
    }

    [Theory]
    [InlineData(SlotTargetKind.Slot)]
    [InlineData(SlotTargetKind.Chamber)]
    [InlineData(SlotTargetKind.Cartridge)]
    [InlineData(SlotTargetKind.StackSlot)]
    public void Preset_enforces_finalized_max_count_for_every_slot_target_kind(
        SlotTargetKind targetKind)
    {
        var parent = ParentWithTarget(targetKind, maximumCount: 1);
        var child = Template(ChildTemplateId, stackMax: 10);

        Assert.Throws<CargoCatalogValidationException>(() =>
            ResolvePreset(
                [parent, child],
                PresetWithChildren(SlottedChild(stackCount: 2))));
    }

    [Theory]
    [InlineData(SlotTargetKind.Slot)]
    [InlineData(SlotTargetKind.Chamber)]
    [InlineData(SlotTargetKind.Cartridge)]
    [InlineData(SlotTargetKind.StackSlot)]
    public void Preset_treats_zero_max_count_as_no_local_slot_bound(
        SlotTargetKind targetKind)
    {
        var parent = ParentWithTarget(targetKind, maximumCount: 0);
        var child = Template(ChildTemplateId, stackMax: 10);

        var resolved = ResolvePreset(
            [parent, child],
            PresetWithChildren(SlottedChild(stackCount: 2)));

        Assert.Equal(
            2,
            resolved.Forest.Nodes.Single(node => node.TemplateId == ChildTemplateId).StackCount);
    }

    [Theory]
    [InlineData(SlotTargetKind.Slot)]
    [InlineData(SlotTargetKind.Chamber)]
    [InlineData(SlotTargetKind.Cartridge)]
    public void Preset_rejects_conflicting_slot_max_count_representations(
        SlotTargetKind targetKind)
    {
        var parent = ParentWithTarget(
            targetKind,
            maximumCount: 2,
            legacyMaximumStackCount: 3);
        var child = Template(ChildTemplateId, stackMax: 10);

        Assert.Throws<CargoCatalogValidationException>(() =>
            ResolvePreset(
                [parent, child],
                PresetWithChildren(SlottedChild(stackCount: 1))));
    }

    [Theory]
    [InlineData(SlotTargetKind.Slot)]
    [InlineData(SlotTargetKind.Chamber)]
    [InlineData(SlotTargetKind.Cartridge)]
    public void Preset_accepts_equal_slot_max_count_representations(
        SlotTargetKind targetKind)
    {
        var parent = ParentWithTarget(
            targetKind,
            maximumCount: 2,
            legacyMaximumStackCount: 2);
        var child = Template(ChildTemplateId, stackMax: 10);

        var resolved = ResolvePreset(
            [parent, child],
            PresetWithChildren(SlottedChild(stackCount: 2)));

        Assert.Equal(
            2,
            resolved.Forest.Nodes.Single(node => node.TemplateId == ChildTemplateId).StackCount);
    }

    [Theory]
    [InlineData("543be5dd4bdc2deb348b4569")]
    [InlineData("543be5e94bdc2df1348b4568")]
    [InlineData("5c164d2286f774194c5e69fa")]
    [InlineData("5448bf274bdc2dfc2f8b456a")]
    [InlineData("5795f317245977243854e041")]
    [InlineData("566abbb64bdc2d144c8b457d")]
    public void Resolver_rejects_explicit_excluded_ancestry(string excludedCategoryId)
    {
        var excludedCategory = Category(excludedCategoryId);
        var intermediate = Category(AllowedCategoryId, excludedCategoryId);
        var template = Template(ParentTemplateId, AllowedCategoryId);

        Assert.Throws<CargoCatalogValidationException>(() =>
            Resolver([excludedCategory, intermediate, template]).Resolve(
                Lot([new TemplateLine(ParentTemplateId, 1, 1)])));
    }

    [Theory]
    [InlineData("59faff1d86f7746c51718c9c")]
    [InlineData("6656560053eaaa7a23349c86")]
    public void Resolver_rejects_explicit_nonmoney_token_templates(string templateId)
    {
        Assert.Throws<CargoCatalogValidationException>(() =>
            Resolver(
            [
                Category(AllowedCategoryId),
                Template(templateId, AllowedCategoryId)
            ]).Resolve(Lot([new TemplateLine(templateId, 1, 1)], templateId)));
    }

    [Fact]
    public void Resolver_and_materializer_reject_quest_dogtag_and_excluded_container_items()
    {
        foreach (var properties in new[]
                 {
                     new TemplateFlags(QuestItem: true, Dogtag: false),
                     new TemplateFlags(QuestItem: false, Dogtag: true)
                 })
        {
            var template = Template(
                ParentTemplateId,
                questItem: properties.QuestItem,
                dogtag: properties.Dogtag);
            Assert.Throws<CargoCatalogValidationException>(() =>
                Resolver([template]).Resolve(
                    Lot([new TemplateLine(ParentTemplateId, 1, 1)])));
        }

        const string containerCategoryId = "5795f317245977243854e041";
        var container = Template(ParentTemplateId, containerCategoryId);
        var forest = RewardForest.Create(
            [Node("root", "root", ParentTemplateId)]);
        Assert.Throws<CargoCatalogValidationException>(() =>
            Materializer([Category(containerCategoryId), container])
                .Materialize(forest, []));
    }

    private static ResolvedCargoLot ResolveSinglePreset(
        TemplateItem template,
        Upd upd) =>
        ResolvePreset(
            [template],
            new Preset
            {
                Id = PresetId,
                Parent = RootSourceId,
                Items =
                [
                    new Item
                    {
                        Id = RootSourceId,
                        Template = template.Id,
                        Upd = upd
                    }
                ]
            });

    private static ResolvedCargoLot ResolveGridPreset(ItemLocation location)
    {
        var parent = Template(
            ParentTemplateId,
            grids: [GridTarget("main", 3, 2, ChildTemplateId)]);
        var child = Template(ChildTemplateId, width: 2, height: 1);
        return ResolvePreset(
            [parent, child],
            PresetWithChildren(
                new Item
                {
                    Id = ChildSourceId,
                    Template = ChildTemplateId,
                    ParentId = RootSourceId,
                    SlotId = "main",
                    Location = location
                }));
    }

    private static TemplateItem ParentWithTarget(
        SlotTargetKind targetKind,
        double? maximumCount,
        double? legacyMaximumStackCount = null)
    {
        var slot = SlotTarget(
            "contents",
            ChildTemplateId,
            maximumCount: maximumCount,
            legacyMaximumStackCount: legacyMaximumStackCount);
        return targetKind switch
        {
            SlotTargetKind.Slot => Template(ParentTemplateId, slots: [slot]),
            SlotTargetKind.Chamber => Template(ParentTemplateId, chambers: [slot]),
            SlotTargetKind.Cartridge => Template(ParentTemplateId, cartridges: [slot]),
            SlotTargetKind.StackSlot => Template(
                ParentTemplateId,
                stackSlots:
                [
                    StackSlotTarget("contents", ChildTemplateId, maximumCount)
                ]),
            _ => throw new ArgumentOutOfRangeException(nameof(targetKind))
        };
    }

    private static Item SlottedChild(int stackCount) =>
        new()
        {
            Id = ChildSourceId,
            Template = ChildTemplateId,
            ParentId = RootSourceId,
            SlotId = "contents",
            Upd = new Upd { StackObjectsCount = stackCount }
        };

    private static ResolvedCargoLot ResolvePreset(
        IEnumerable<TemplateItem> templates,
        Preset preset) =>
        Resolver(templates, [preset]).Resolve(
            Lot([new PresetLine(PresetId)]));

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

    private static CargoLotMaterializer Materializer(
        IEnumerable<TemplateItem> templates)
    {
        var templateMap = templates.ToDictionary(
            template => template.Id.ToString(),
            StringComparer.Ordinal);
        return new CargoLotMaterializer(id => templateMap.GetValueOrDefault(id));
    }

    private static CargoLotDefinition Lot(
        IEnumerable<RewardRecipeLine> lines,
        string anchorTemplateId = ParentTemplateId) =>
        new(
            "core",
            "0.3.0",
            "safety-test",
            "Safety Test",
            "Exercises fail-closed cargo validation.",
            new FamilyId("field-supply"),
            new TrackId("testing"),
            anchorTemplateId,
            1d,
            new RaidRole("testing"),
            lines);

    private static Preset PresetWithChildren(params Item[] children) =>
        new()
        {
            Id = PresetId,
            Parent = RootSourceId,
            Items =
            [
                new Item { Id = RootSourceId, Template = ParentTemplateId },
                .. children
            ]
        };

    private static Item GridChild(
        MongoId id,
        int x,
        int y,
        ItemRotation rotation) =>
        new()
        {
            Id = id,
            Template = ChildTemplateId,
            ParentId = RootSourceId,
            SlotId = "main",
            Location = new ItemLocation { X = x, Y = y, R = rotation }
        };

    private static RewardForestNode Node(
        string treeRootPath,
        string logicalPath,
        string templateId,
        string? parentLogicalPath = null,
        string? slotId = null,
        CanonicalInternalLocation? location = null,
        RewardStableState? stableState = null) =>
        new(
            treeRootPath,
            logicalPath,
            templateId,
            parentLogicalPath,
            slotId,
            location,
            1,
            stableState);

    private static TemplateItem Template(
        string id,
        string parentId = ItemRootId,
        int width = 1,
        int height = 1,
        int stackMax = 1,
        double? maximumDurability = null,
        int? maxResource = null,
        double? resource = null,
        bool questItem = false,
        bool dogtag = false,
        IEnumerable<Slot>? slots = null,
        IEnumerable<Slot>? chambers = null,
        IEnumerable<Slot>? cartridges = null,
        IEnumerable<StackSlot>? stackSlots = null,
        IEnumerable<Grid>? grids = null) =>
        new()
        {
            Id = id,
            Parent = parentId,
            Name = id,
            Type = "Item",
            Properties = new TemplateItemProperties
            {
                StackMaxSize = stackMax,
                Width = width,
                Height = height,
                MaxDurability = maximumDurability,
                MaxResource = maxResource,
                Resource = resource,
                QuestItem = questItem,
                DogTagQualities = dogtag,
                Slots = slots ?? [],
                Chambers = chambers ?? [],
                Cartridges = cartridges ?? [],
                StackSlots = stackSlots ?? [],
                Grids = grids ?? [],
                Prefab = new Prefab { Path = $"test/{id}.bundle" }
            }
        };

    private static TemplateItem Category(
        string id,
        string parentId = ItemRootId) =>
        new()
        {
            Id = id,
            Parent = parentId,
            Name = id,
            Type = "Node"
        };

    private static Slot SlotTarget(
        string name,
        string allowedTemplateId,
        bool required = false,
        double? maximumCount = null,
        double? legacyMaximumStackCount = null) =>
        new()
        {
            Name = name,
            Required = required,
            MaxCount = maximumCount,
            Properties = new SlotProperties
            {
                MaxStackCount = legacyMaximumStackCount,
                Filters =
                [
                    new SlotFilter { Filter = [(MongoId)allowedTemplateId] }
                ]
            }
        };

    private static StackSlot StackSlotTarget(
        string name,
        string allowedTemplateId,
        double? maximumCount) =>
        new()
        {
            Name = name,
            MaxCount = maximumCount,
            Properties = new StackSlotProperties
            {
                Filters =
                [
                    new SlotFilter { Filter = [(MongoId)allowedTemplateId] }
                ]
            }
        };

    private static Grid GridTarget(
        string name,
        int width,
        int height,
        string allowedTemplateId) =>
        new()
        {
            Name = name,
            Properties = new GridProperties
            {
                CellsH = width,
                CellsV = height,
                Filters =
                [
                    new GridFilter { Filter = [(MongoId)allowedTemplateId] }
                ]
            }
        };

    private sealed record TemplateFlags(bool QuestItem, bool Dogtag);

    public enum SlotTargetKind
    {
        Slot,
        Chamber,
        Cartridge,
        StackSlot
    }
}
