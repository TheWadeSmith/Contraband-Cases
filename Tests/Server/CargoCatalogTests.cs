using System.Text.Json;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class CargoCatalogTests
{
    private const string ParentTemplateId = "54009119af1c881c07000029";
    private const string TemplateA = "100000000000000000000001";
    private const string TemplateB = "100000000000000000000002";
    private const string TemplateC = "100000000000000000000003";
    private const string TemplateD = "100000000000000000000004";
    private const string PresetId = "200000000000000000000001";
    private const string UlachTemplateId = "5b40e1525acfc4771e1c6611";
    private const string UlachPresetId = "657120b36fe59548840cb542";
    private const string UlachTopPlateId = "657112234269e9a568089eac";
    private const string UlachBackPlateId = "657112a4818110db4600aa66";
    private const string UlachEarsPlateId = "657112ce22996eaf110881fb";

    [Fact]
    public void Legacy_provider_adapts_all_twelve_rewards_without_changing_identity_or_weight()
    {
        var json = File.ReadAllText(ConfigPath("rewards.json"));
        var catalog = ServerRewardCatalog.Parse(json);
        var roots = catalog.Rewards.ToDictionary(
            reward => reward.PresetId,
            reward => reward.WeaponTemplateId,
            StringComparer.Ordinal);
        var rewards = catalog.Validate(new LegacyPresetResolver(roots));

        var lots = LegacyWeaponPresetProvider.Adapt(rewards);

        Assert.Equal(12, lots.Count);
        Assert.Equal(rewards.Select(reward => reward.Id), lots.Select(lot => lot.LotId));
        Assert.Equal(rewards.Select(reward => reward.Weight), lots.Select(lot => lot.Weight));
        Assert.Equal(
            rewards.Select(reward => reward.WeaponTemplateId),
            lots.Select(lot => lot.AnchorTemplateId));
        Assert.Equal(
            rewards.Select(reward => reward.PresetId),
            lots.Select(lot => Assert.IsType<PresetLine>(Assert.Single(lot.RecipeLines)).PresetId));
        Assert.All(lots, lot =>
        {
            Assert.Equal(LegacyWeaponPresetProvider.ProviderId, lot.ProviderId);
            Assert.Equal(LegacyWeaponPresetProvider.PackVersion, lot.PackVersion);
        });
    }

    [Fact]
    public void Template_line_creates_independent_canonical_roots()
    {
        var resolver = Resolver([Template(TemplateA, stackMax: 10)]);
        var definition = Lot(
            "core",
            "stacked-a",
            TemplateA,
            [new TemplateLine(TemplateA, instanceCount: 3, stackCountPerInstance: 5)]);

        var resolved = resolver.Resolve(definition);

        Assert.Equal(3, resolved.Forest.Roots.Count);
        Assert.Equal(3, resolved.Forest.Nodes.Count);
        Assert.Equal(3, resolved.Forest.Roots.Select(root => root.LogicalPath).Distinct().Count());
        Assert.All(resolved.Forest.Roots, root =>
        {
            Assert.Null(root.ParentLogicalPath);
            Assert.Equal(root.LogicalPath, root.TreeRootPath);
            Assert.Equal(TemplateA, root.TemplateId);
            Assert.Equal(5, root.StackCount);
        });
    }

    [Fact]
    public void Preset_resolution_preserves_semantic_topology_location_and_allowlisted_state_without_source_ids()
    {
        var templates = new[]
        {
            Template(TemplateA, maximumDurability: 100),
            Template(TemplateB),
            Template(TemplateC),
            Template(TemplateD, maximumResource: 10)
        };
        var firstPreset = TopologyPreset(alternateIdsAndOrder: false);
        var secondPreset = TopologyPreset(alternateIdsAndOrder: true);
        var definition = Lot(
            "core",
            "preset-tree",
            TemplateA,
            [new PresetLine(PresetId)]);

        var first = Resolver(templates, [firstPreset]).Resolve(definition);
        var second = Resolver(templates, [secondPreset]).Resolve(definition);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Single(first.Forest.Roots);
        Assert.Equal(4, first.Forest.Nodes.Count);

        var root = Assert.Single(first.Forest.Roots);
        Assert.Equal(TemplateA, root.TemplateId);
        Assert.Null(root.InternalLocation);
        Assert.Equal(75m, root.StableState?.Durability);
        Assert.Equal(100m, root.StableState?.MaximumDurability);

        var scope = first.Forest.Nodes.Single(node => node.TemplateId == TemplateB);
        Assert.Equal(root.LogicalPath, scope.ParentLogicalPath);
        Assert.Equal("mod_scope", scope.SlotId);
        Assert.Equal(2, scope.InternalLocation?.X);
        Assert.Equal(3, scope.InternalLocation?.Y);
        Assert.Equal(CanonicalRotation.Vertical, scope.InternalLocation?.Rotation);

        var mount = first.Forest.Nodes.Single(node => node.TemplateId == TemplateC);
        Assert.Equal(scope.LogicalPath, mount.ParentLogicalPath);
        Assert.Equal("mod_mount", mount.SlotId);

        var resource = first.Forest.Nodes.Single(node => node.TemplateId == TemplateD);
        Assert.Equal(root.LogicalPath, resource.ParentLogicalPath);
        Assert.Equal(7m, resource.StableState?.ResourceValue);
        Assert.Equal(10m, resource.StableState?.MaximumResourceValue);

        var serialized = JsonSerializer.Serialize(first.Forest);
        foreach (var sourceId in FirstSourceIds.Concat(SecondSourceIds))
        {
            Assert.DoesNotContain(sourceId.ToString(), serialized, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Resolver_rejects_missing_templates_stack_overflow_incomplete_trees_and_custom_item_failures()
    {
        var validTemplate = Template(TemplateA, stackMax: 2);
        var missing = Lot(
            "core",
            "missing",
            TemplateB,
            [new TemplateLine(TemplateB, 1, 1)]);
        Assert.Throws<CargoCatalogValidationException>(() =>
            Resolver([validTemplate]).Resolve(missing));

        var overflow = Lot(
            "core",
            "overflow",
            TemplateA,
            [new TemplateLine(TemplateA, 1, 3)]);
        Assert.Throws<CargoCatalogValidationException>(() =>
            Resolver([validTemplate]).Resolve(overflow));

        var negativeResourceTemplate = Template(TemplateB, maximumResource: -1);
        var malformedResource = Lot(
            "core",
            "negative-resource",
            TemplateB,
            [new TemplateLine(TemplateB, 1, 1)]);
        Assert.Throws<CargoCatalogValidationException>(() =>
            Resolver([negativeResourceTemplate]).Resolve(malformedResource));

        var nonFiniteTemplate = Template(TemplateB, maximumDurability: double.PositiveInfinity);
        Assert.Throws<CargoCatalogValidationException>(() =>
            Resolver([nonFiniteTemplate]).Resolve(malformedResource));

        var orphanPreset = new Preset
        {
            Id = PresetId,
            Parent = FirstSourceIds[0],
            Items =
            [
                new Item { Id = FirstSourceIds[0], Template = TemplateA },
                new Item
                {
                    Id = FirstSourceIds[1],
                    Template = TemplateB,
                    ParentId = "ffffffffffffffffffffffff",
                    SlotId = "mod"
                }
            ]
        };
        var presetLot = Lot(
            "core",
            "orphan",
            TemplateA,
            [new PresetLine(PresetId)]);
        Assert.Throws<CargoCatalogValidationException>(() =>
            Resolver([validTemplate, Template(TemplateB)], [orphanPreset]).Resolve(presetLot));

        var rejected = new CargoLotResolver(new CargoLotResolverDependencies(
            _ => validTemplate,
            _ => null,
            (_, _) => "bundle evidence is missing"));
        Assert.Throws<CargoCatalogValidationException>(() => rejected.Resolve(WithStack(overflow, 1)));
    }

    [Fact]
    public void Core_night_patrol_uses_the_complete_ulach_preset_required_by_spt()
    {
        var core = new JsonRewardPackLoader().LoadFile(
            ConfigPath(System.IO.Path.Combine("reward-packs", "core.json")));
        var nightPatrol = Assert.Single(
            core.Lots,
            lot => string.Equals(lot.LotId, "night-patrol", StringComparison.Ordinal));
        var ulachLine = Assert.Single(nightPatrol.RecipeLines.OfType<PresetLine>());

        Assert.Equal(UlachPresetId, ulachLine.PresetId);
        Assert.Contains(UlachPresetId, core.RequiredPresetIds);

        var headsetTemplateId = Assert.IsType<TemplateLine>(nightPatrol.RecipeLines[0]).TemplateId;
        var medicalTemplateId = Assert.IsType<TemplateLine>(nightPatrol.RecipeLines[2]).TemplateId;
        var templates = new[]
        {
            Template(headsetTemplateId),
            Template(
                UlachTemplateId,
                slots:
                [
                    SlotTarget("Helmet_top", UlachTopPlateId, required: true),
                    SlotTarget("Helmet_back", UlachBackPlateId, required: true),
                    SlotTarget("Helmet_ears", UlachEarsPlateId, required: true)
                ]),
            Template(UlachTopPlateId),
            Template(UlachBackPlateId),
            Template(UlachEarsPlateId),
            Template(medicalTemplateId)
        };
        var rootId = (MongoId)"300000000000000000000001";
        var preset = new Preset
        {
            Id = UlachPresetId,
            Parent = rootId,
            Items =
            [
                new Item { Id = rootId, Template = UlachTemplateId },
                new Item
                {
                    Id = "300000000000000000000002",
                    Template = UlachTopPlateId,
                    ParentId = rootId.ToString(),
                    SlotId = "Helmet_top"
                },
                new Item
                {
                    Id = "300000000000000000000003",
                    Template = UlachBackPlateId,
                    ParentId = rootId.ToString(),
                    SlotId = "Helmet_back"
                },
                new Item
                {
                    Id = "300000000000000000000004",
                    Template = UlachEarsPlateId,
                    ParentId = rootId.ToString(),
                    SlotId = "Helmet_ears"
                }
            ]
        };

        var resolved = Resolver(templates, [preset]).Resolve(nightPatrol);

        Assert.Equal(6, resolved.Forest.Nodes.Count);
        Assert.Equal(
            ["Helmet_back", "Helmet_ears", "Helmet_top"],
            resolved.Forest.Nodes
                .Where(node => node.ParentLogicalPath is not null)
                .Select(node => node.SlotId)
                .OrderBy(slot => slot, StringComparer.Ordinal));
    }

    [Fact]
    public void Snapshot_is_deterministic_immutable_and_sorted_ordinally()
    {
        var builder = Builder([Template(TemplateA), Template(TemplateB), Template(TemplateC)]);
        var lotZ = Lot("core", "z-lot", TemplateA, [new TemplateLine(TemplateA, 1, 1)]);
        var lotA = Lot("core", "a-lot", TemplateB, [new TemplateLine(TemplateB, 1, 1)]);
        var modLot = Lot("mod.items", "b-lot", TemplateC, [new TemplateLine(TemplateC, 1, 1)]);
        var source = new List<CargoLotDefinition> { lotZ, lotA };
        var firstCore = new CargoLotPack("core", "1.0.0", source);
        source.Clear();

        var first = builder.Build(
            firstCore,
            [new CargoLotPack("mod.items", "1.0.0", [modLot])]);
        var second = builder.Build(
            new CargoLotPack("core", "1.0.0", [lotA, lotZ]),
            [new CargoLotPack("mod.items", "1.0.0", [modLot])]);

        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(first.Sha256Hex, second.Sha256Hex);
        Assert.Equal(64, first.Sha256Hex.Length);
        Assert.Equal(
            ["core/a-lot", "core/z-lot", "mod.items/b-lot"],
            first.Lots.Select(lot => $"{lot.Identity.ProviderId}/{lot.Identity.LotId}"));
        Assert.Equal(2, firstCore.Lots.Count);
    }

    [Fact]
    public void Invalid_optional_pack_is_skipped_wholly_while_invalid_core_fails()
    {
        var builder = Builder([Template(TemplateA), Template(TemplateB)]);
        var core = new CargoLotPack(
            "core",
            "1.0.0",
            [Lot("core", "core-a", TemplateA, [new TemplateLine(TemplateA, 1, 1)])]);
        var optional = new CargoLotPack(
            "optional",
            "1.0.0",
            [
                Lot("optional", "valid-b", TemplateB, [new TemplateLine(TemplateB, 1, 1)]),
                Lot(
                    "optional",
                    "missing-c",
                    TemplateC,
                    [new TemplateLine(TemplateC, 1, 1)])
            ]);

        var snapshot = builder.Build(core, [optional]);

        Assert.Single(snapshot.Lots);
        Assert.Equal("core-a", snapshot.Lots[0].Identity.LotId);
        var skipped = Assert.Single(snapshot.SkippedPacks);
        Assert.Equal("optional", skipped.ProviderId);
        Assert.DoesNotContain(snapshot.Lots, lot => lot.Identity.ProviderId == "optional");

        var invalidCore = new CargoLotPack(
            "core",
            "1.0.0",
            [Lot("core", "missing", TemplateC, [new TemplateLine(TemplateC, 1, 1)])]);
        Assert.Throws<CargoCatalogValidationException>(() => builder.Build(invalidCore));
    }

    [Fact]
    public void Duplicate_optional_provider_identity_is_rejected_deterministically_as_a_whole()
    {
        var builder = Builder([Template(TemplateA), Template(TemplateB)]);
        var core = new CargoLotPack(
            "core",
            "1.0.0",
            [Lot("core", "core-a", TemplateA, [new TemplateLine(TemplateA, 1, 1)])]);
        var firstDuplicate = new CargoLotPack(
            "duplicate",
            "1.0.0",
            [Lot("duplicate", "lot-a", TemplateA, [new TemplateLine(TemplateA, 1, 1)])]);
        var secondDuplicate = new CargoLotPack(
            "duplicate",
            "1.0.0",
            [Lot("duplicate", "lot-b", TemplateB, [new TemplateLine(TemplateB, 1, 1)])]);

        var first = builder.Build(core, [firstDuplicate, secondDuplicate]);
        var reversed = builder.Build(core, [secondDuplicate, firstDuplicate]);

        Assert.Equal(first.SnapshotId, reversed.SnapshotId);
        Assert.Equal(2, first.SkippedPacks.Count);
        Assert.Equal(2, reversed.SkippedPacks.Count);
        Assert.DoesNotContain(first.Lots, lot => lot.Identity.ProviderId == "duplicate");
        Assert.DoesNotContain(reversed.Lots, lot => lot.Identity.ProviderId == "duplicate");
    }

    private static CargoLotDefinition WithStack(
        CargoLotDefinition definition,
        int stackCount)
    {
        var source = Assert.IsType<TemplateLine>(Assert.Single(definition.RecipeLines));
        return Lot(
            definition.ProviderId,
            definition.LotId,
            definition.AnchorTemplateId,
            [new TemplateLine(source.TemplateId, source.InstanceCount, stackCount)]);
    }

    private static CargoLotDefinition Lot(
        string providerId,
        string lotId,
        string anchorTemplateId,
        IEnumerable<RewardRecipeLine> lines) =>
        new(
            providerId,
            "1.0.0",
            lotId,
            lotId,
            "A focused test cargo lot.",
            new FamilyId("field-supply"),
            new TrackId("testing"),
            anchorTemplateId,
            1d,
            new RaidRole("testing"),
            lines);

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
            templateId => templateMap.GetValueOrDefault(templateId),
            presetId => presetMap.GetValueOrDefault(presetId)));
    }

    private static CargoCatalogSnapshotBuilder Builder(
        IEnumerable<TemplateItem> templates,
        IEnumerable<Preset>? presets = null)
    {
        var templateSnapshot = templates.ToArray();
        var templateMap = templateSnapshot.ToDictionary(
            template => template.Id.ToString(),
            StringComparer.Ordinal);
        return new CargoCatalogSnapshotBuilder(
            Resolver(templateSnapshot, presets),
            new CargoLotEvaluator(
                templateId => templateMap.GetValueOrDefault(templateId),
                _ => 1_000d));
    }

    private static TemplateItem Template(
        string id,
        int stackMax = 1,
        double? maximumDurability = null,
        int? maximumResource = null,
        IEnumerable<Slot>? slots = null) =>
        new()
        {
            Id = id,
            Parent = ParentTemplateId,
            Name = id,
            Type = "Item",
            Properties = new TemplateItemProperties
            {
                StackMaxSize = stackMax,
                Width = 1,
                Height = 1,
                MaxDurability = maximumDurability,
                MaxResource = maximumResource,
                Slots = slots?.ToArray() ?? (id == TemplateB
                    ? [SlotTarget("mod_mount", TemplateC)]
                    : []),
                Grids = id == TemplateA
                    ?
                    [
                        GridTarget("mod_scope", TemplateB),
                        GridTarget("main", TemplateD)
                    ]
                    : [],
                Prefab = new Prefab { Path = $"test/{id}.bundle" }
            }
        };

    private static Slot SlotTarget(
        string name,
        string allowedTemplateId,
        bool required = false) =>
        new()
        {
            Name = name,
            Required = required,
            Properties = new SlotProperties
            {
                Filters =
                [
                    new SlotFilter { Filter = [(MongoId)allowedTemplateId] }
                ]
            }
        };

    private static Grid GridTarget(string name, string allowedTemplateId) =>
        new()
        {
            Name = name,
            Properties = new GridProperties
            {
                CellsH = 8,
                CellsV = 8,
                Filters =
                [
                    new GridFilter { Filter = [(MongoId)allowedTemplateId] }
                ]
            }
        };

    private static Preset TopologyPreset(bool alternateIdsAndOrder)
    {
        var ids = alternateIdsAndOrder ? SecondSourceIds : FirstSourceIds;
        var items = new List<Item>
        {
            new()
            {
                Id = ids[0],
                Template = TemplateA,
                ParentId = "preset-shell",
                Location = new ItemLocation { X = 99, Y = 99 },
                Upd = new Upd
                {
                    Repairable = new UpdRepairable { Durability = 75, MaxDurability = 100 }
                }
            },
            new()
            {
                Id = ids[1],
                Template = TemplateB,
                ParentId = ids[0].ToString(),
                SlotId = "mod_scope",
                Location = new ItemLocation { X = 2, Y = 3, R = ItemRotation.Vertical }
            },
            new()
            {
                Id = ids[2],
                Template = TemplateC,
                ParentId = ids[1].ToString(),
                SlotId = "mod_mount"
            },
            new()
            {
                Id = ids[3],
                Template = TemplateD,
                ParentId = ids[0].ToString(),
                SlotId = "main",
                Location = new ItemLocation { X = 0, Y = 1 },
                Upd = new Upd { Resource = new UpdResource { Value = 7 } }
            }
        };
        if (alternateIdsAndOrder)
        {
            items.Reverse();
        }

        return new Preset
        {
            Id = PresetId,
            Parent = ids[0],
            Items = items
        };
    }

    private static readonly MongoId[] FirstSourceIds =
    [
        "aaaaaaaaaaaaaaaaaaaaaaaa",
        "bbbbbbbbbbbbbbbbbbbbbbbb",
        "cccccccccccccccccccccccc",
        "dddddddddddddddddddddddd"
    ];

    private static readonly MongoId[] SecondSourceIds =
    [
        "111111111111111111111111",
        "222222222222222222222222",
        "333333333333333333333333",
        "444444444444444444444444"
    ];

    private sealed class LegacyPresetResolver(
        IReadOnlyDictionary<string, string> roots) : IRewardPresetResolver
    {
        public RewardPresetTree? Resolve(string presetId)
        {
            return roots.TryGetValue(presetId, out var rootTemplateId)
                ? new RewardPresetTree(
                    [new RewardPresetItem($"root-{presetId}", rootTemplateId, null)],
                    1_000)
                : null;
        }
    }

    private static string ConfigPath(string fileName) =>
        System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../config", fileName));
}
