using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Xunit;

namespace ContrabandCases.Tests.Catalog;

public sealed class CargoLotMaterializerTests
{
    private const string ParentTemplateId = "54009119af1c881c07000029";
    private const string RootTemplateId = "100000000000000000000001";
    private const string HorizontalTemplateId = "100000000000000000000002";
    private const string VerticalTemplateId = "100000000000000000000003";
    private const string StackTemplateId = "100000000000000000000004";
    private const string MedTemplateId = "100000000000000000000005";
    private const string RepairTemplateId = "100000000000000000000006";
    private const string FoodTemplateId = "100000000000000000000007";
    private const string GenericTemplateId = "100000000000000000000008";

    [Fact]
    public void Materialize_preserves_canonical_multiroot_topology_state_and_both_rotations()
    {
        var templates = new[]
        {
            Template(RootTemplateId, maximumDurability: 100),
            Template(HorizontalTemplateId),
            Template(VerticalTemplateId),
            Template(StackTemplateId, stackMax: 10)
        };
        var forest = RewardForest.Create(
        [
            Node("root-b", "root-b", StackTemplateId, null, null, stackCount: 7),
            Node(
                "root-a",
                "root-a/vertical",
                VerticalTemplateId,
                "root-a/horizontal",
                "mod_vertical",
                new CanonicalInternalLocation(3, 4, CanonicalRotation.Vertical)),
            Node("root-a", "root-a", RootTemplateId, null, null, stableState: new(75m, 100m)),
            Node(
                "root-a",
                "root-a/horizontal",
                HorizontalTemplateId,
                "root-a",
                "mod_horizontal",
                new CanonicalInternalLocation(1, 2, CanonicalRotation.Horizontal))
        ]);

        var result = Materializer(templates).Materialize(forest, []);

        Assert.Equal(
            [RootTemplateId, HorizontalTemplateId, VerticalTemplateId, StackTemplateId],
            result.Items.Select(item => item.Template.ToString()));
        Assert.Equal([result.Items[0].Id, result.Items[3].Id], result.CanonicalRootIds);
        Assert.Null(result.Items[0].ParentId);
        Assert.Null(result.Items[0].SlotId);
        Assert.Null(result.Items[0].Location);
        Assert.Equal(result.Items[0].Id.ToString(), result.Items[1].ParentId);
        Assert.Equal("mod_horizontal", result.Items[1].SlotId);
        Assert.Equal(result.Items[1].Id.ToString(), result.Items[2].ParentId);
        Assert.Equal("mod_vertical", result.Items[2].SlotId);
        Assert.Null(result.Items[3].ParentId);

        AssertLocation(result.Items[1], 1, 2, ItemRotation.Horizontal);
        AssertLocation(result.Items[2], 3, 4, ItemRotation.Vertical);
        Assert.Equal(1d, result.Items[0].Upd?.StackObjectsCount);
        Assert.Equal(75d, result.Items[0].Upd?.Repairable?.Durability);
        Assert.Equal(100d, result.Items[0].Upd?.Repairable?.MaxDurability);
        Assert.Equal(7d, result.Items[3].Upd?.StackObjectsCount);
    }

    [Fact]
    public void Materialize_allocates_unique_ids_and_retries_supplied_and_in_batch_collisions()
    {
        var forest = RewardForest.Create(
        [
            Node("root-a", "root-a", RootTemplateId, null, null),
            Node("root-b", "root-b", StackTemplateId, null, null)
        ]);
        MongoId occupied = "aaaaaaaaaaaaaaaaaaaaaaaa";
        MongoId first = "bbbbbbbbbbbbbbbbbbbbbbbb";
        MongoId second = "cccccccccccccccccccccccc";
        var candidates = new Queue<MongoId>([occupied, first, first, second]);
        var existing = new HashSet<MongoId> { occupied };
        var materializer = new CargoLotMaterializer(
            id => id switch
            {
                RootTemplateId => Template(RootTemplateId),
                StackTemplateId => Template(StackTemplateId),
                _ => null
            },
            () => candidates.Dequeue());

        var result = materializer.Materialize(forest, existing);

        Assert.Equal([first, second], result.Items.Select(item => item.Id));
        Assert.Equal(2, result.Items.Select(item => item.Id).Distinct().Count());
        Assert.Equal([first, second], result.CanonicalRootIds);
        Assert.Equal([occupied], existing);
        Assert.Empty(candidates);
    }

    [Fact]
    public void Materialize_emits_the_exact_upd_subtype_for_each_resource_kind()
    {
        var templates = new[]
        {
            Template(MedTemplateId, maxHpResource: 20),
            Template(RepairTemplateId, maxRepairResource: 30),
            Template(FoodTemplateId, maxResource: 40),
            Template(GenericTemplateId, maxResource: 50, resource: 50)
        };
        var forest = RewardForest.Create(
        [
            ResourceNode("root-med", MedTemplateId, RewardResourceKind.MedKit, 12m, 20m),
            ResourceNode("root-repair", RepairTemplateId, RewardResourceKind.RepairKit, 13m, 30m),
            ResourceNode("root-food", FoodTemplateId, RewardResourceKind.FoodDrink, 14m, 40m),
            ResourceNode("root-generic", GenericTemplateId, RewardResourceKind.Generic, 15m, 50m)
        ]);

        var result = Materializer(templates).Materialize(forest, []);
        var byTemplate = result.Items.ToDictionary(item => item.Template.ToString());

        AssertResource(byTemplate[MedTemplateId].Upd!, RewardResourceKind.MedKit, 12d);
        AssertResource(byTemplate[RepairTemplateId].Upd!, RewardResourceKind.RepairKit, 13d);
        AssertResource(byTemplate[FoodTemplateId].Upd!, RewardResourceKind.FoodDrink, 14d);
        AssertResource(byTemplate[GenericTemplateId].Upd!, RewardResourceKind.Generic, 15d);
    }

    [Fact]
    public void Materialize_rejects_missing_drifted_ambiguous_and_out_of_bounds_templates()
    {
        var plainForest = RewardForest.Create(
            [Node("root", "root", RootTemplateId, null, null)]);
        Assert.Throws<CargoCatalogValidationException>(() =>
            Materializer([]).Materialize(plainForest, []));
        Assert.Throws<CargoCatalogValidationException>(() =>
            new CargoLotMaterializer(_ => Template(StackTemplateId))
                .Materialize(plainForest, []));

        var ambiguous = Template(RootTemplateId, maxHpResource: 10, maxRepairResource: 10);
        Assert.Throws<CargoCatalogValidationException>(() =>
            Materializer([ambiguous]).Materialize(plainForest, []));

        var driftedResourceForest = RewardForest.Create(
            [ResourceNode("root", FoodTemplateId, RewardResourceKind.FoodDrink, 4m, 10m)]);
        Assert.Throws<CargoCatalogValidationException>(() =>
            Materializer([Template(FoodTemplateId, maxResource: 11)])
                .Materialize(driftedResourceForest, []));

        var stackForest = RewardForest.Create(
            [Node("root", "root", StackTemplateId, null, null, stackCount: 2)]);
        Assert.Throws<CargoCatalogValidationException>(() =>
            Materializer([Template(StackTemplateId, stackMax: 1)])
                .Materialize(stackForest, []));

        var durabilityForest = RewardForest.Create(
            [Node("root", "root", RootTemplateId, null, null, stableState: new(90m, 110m))]);
        Assert.Throws<CargoCatalogValidationException>(() =>
            Materializer([Template(RootTemplateId, maximumDurability: 100)])
                .Materialize(durabilityForest, []));
    }

    [Fact]
    public void Materialize_does_not_mutate_inputs_and_semantic_output_is_id_independent()
    {
        var template = Template(GenericTemplateId, stackMax: 5, maxResource: 20, resource: 20);
        var properties = Assert.IsType<TemplateItemProperties>(template.Properties);
        var forest = RewardForest.Create(
        [
            ResourceNode("root", GenericTemplateId, RewardResourceKind.Generic, 11m, 20m),
            Node(
                "root",
                "root/child",
                StackTemplateId,
                "root",
                "main",
                new CanonicalInternalLocation(2, 1, CanonicalRotation.Vertical),
                3)
        ]);
        var stackTemplate = Template(StackTemplateId, stackMax: 5);
        var firstIds = new Queue<MongoId>(
        [
            "111111111111111111111111",
            "222222222222222222222222"
        ]);
        var secondIds = new Queue<MongoId>(
        [
            "333333333333333333333333",
            "444444444444444444444444"
        ]);
        var templates = new[] { template, stackTemplate };

        var first = Materializer(templates, () => firstIds.Dequeue()).Materialize(forest, []);
        var second = Materializer(templates, () => secondIds.Dequeue()).Materialize(forest, []);

        Assert.Equal(SemanticShapes(first.Items), SemanticShapes(second.Items));
        Assert.Same(properties, template.Properties);
        Assert.Equal(5, properties.StackMaxSize);
        Assert.Equal(20, properties.MaxResource);
        Assert.Equal(20d, properties.Resource);
        Assert.Equal(
            ["root", "root/child"],
            forest.Nodes.Select(node => node.LogicalPath));
        Assert.Equal(11m, forest.Nodes[0].StableState?.ResourceValue);
    }

    private static CargoLotMaterializer Materializer(
        IEnumerable<TemplateItem> templates,
        Func<MongoId>? createId = null)
    {
        var byId = templates.ToDictionary(
            template => template.Id.ToString(),
            StringComparer.Ordinal);
        return createId is null
            ? new CargoLotMaterializer(id => byId.GetValueOrDefault(id))
            : new CargoLotMaterializer(id => byId.GetValueOrDefault(id), createId);
    }

    private static IReadOnlyList<PhysicalShape> SemanticShapes(IReadOnlyList<Item> items)
    {
        var indexById = items
            .Select((item, index) => (item.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index);
        return items.Select(item =>
        {
            int? parentIndex = null;
            if (item.ParentId is not null)
            {
                parentIndex = indexById[(MongoId)item.ParentId];
            }
            var location = item.Location as ItemLocation;
            return new PhysicalShape(
                item.Template.ToString(),
                parentIndex,
                item.SlotId,
                location?.X,
                location?.Y,
                location?.R,
                location?.Rotation,
                item.Upd?.StackObjectsCount,
                item.Upd?.Repairable?.Durability,
                item.Upd?.Repairable?.MaxDurability,
                item.Upd?.Resource?.Value);
        }).ToArray();
    }

    private static void AssertLocation(
        Item item,
        int x,
        int y,
        ItemRotation rotation)
    {
        var location = Assert.IsType<ItemLocation>(item.Location);
        Assert.Equal(x, location.X);
        Assert.Equal(y, location.Y);
        Assert.Equal(rotation, location.R);
        Assert.Null(location.Rotation);
    }

    private static void AssertResource(Upd upd, RewardResourceKind kind, double value)
    {
        Assert.Equal(kind == RewardResourceKind.MedKit ? value : null, upd.MedKit?.HpResource);
        Assert.Equal(kind == RewardResourceKind.RepairKit ? value : null, upd.RepairKit?.Resource);
        Assert.Equal(kind == RewardResourceKind.FoodDrink ? value : null, upd.FoodDrink?.HpPercent);
        Assert.Equal(kind == RewardResourceKind.Generic ? value : null, upd.Resource?.Value);
    }

    private static RewardForestNode ResourceNode(
        string path,
        string templateId,
        RewardResourceKind kind,
        decimal value,
        decimal maximum) =>
        Node(
            path,
            path,
            templateId,
            null,
            null,
            stableState: new RewardStableState(
                resourceValue: value,
                maximumResourceValue: maximum,
                resourceKind: kind));

    private static RewardForestNode Node(
        string treeRootPath,
        string logicalPath,
        string templateId,
        string? parentLogicalPath,
        string? slotId,
        CanonicalInternalLocation? location = null,
        int stackCount = 1,
        RewardStableState? stableState = null) =>
        new(
            treeRootPath,
            logicalPath,
            templateId,
            parentLogicalPath,
            slotId,
            location,
            stackCount,
            stableState);

    private static TemplateItem Template(
        string id,
        int stackMax = 1,
        double? maximumDurability = null,
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
                StackMaxSize = stackMax,
                Width = 1,
                Height = 1,
                MaxDurability = maximumDurability,
                MaxHpResource = maxHpResource,
                MaxRepairResource = maxRepairResource,
                MaxResource = maxResource,
                Resource = resource,
                Grids = id switch
                {
                    RootTemplateId =>
                    [
                        GridTarget("mod_horizontal", HorizontalTemplateId)
                    ],
                    HorizontalTemplateId =>
                    [
                        GridTarget("mod_vertical", VerticalTemplateId)
                    ],
                    GenericTemplateId =>
                    [
                        GridTarget("main", StackTemplateId)
                    ],
                    _ => []
                },
                Prefab = new Prefab { Path = $"test/{id}.bundle" }
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

    private sealed record PhysicalShape(
        string TemplateId,
        int? ParentIndex,
        string? SlotId,
        int? X,
        int? Y,
        ItemRotation? Rotation,
        bool? LegacyRotation,
        double? StackCount,
        double? Durability,
        double? MaximumDurability,
        double? GenericResourceValue);
}
