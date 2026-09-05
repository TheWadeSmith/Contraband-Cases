using ContrabandCases.Shared.Catalog;
using System.Text;
using Xunit;

namespace ContrabandCases.Tests.Catalog;

public sealed class RewardForestFingerprintV2Tests
{
    [Fact]
    public void Create_canonicalizes_node_order_and_exposes_roots()
    {
        var child = Node("a", "a/mod", "optic", "a", "mod_scope");
        var rootB = Node("b", "b", "medical", null, null);
        var rootA = Node("a", "a", "weapon", null, null);

        var forest = RewardForest.Create([child, rootB, rootA]);

        Assert.Equal(new[] { "a", "a/mod", "b" }, forest.Nodes.Select(node => node.LogicalPath));
        Assert.Equal(new[] { "a", "b" }, forest.Roots.Select(node => node.LogicalPath));
    }

    [Fact]
    public void Fingerprint_is_sha256_and_is_insensitive_to_enumeration_order()
    {
        var nodes = ValidNodes();

        var first = RewardForestFingerprintV2.Compute("provider", "lot", RewardForest.Create(nodes));
        var second = RewardForestFingerprintV2.Compute("provider", "lot", RewardForest.Create(nodes.Reverse()));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Matches("^[0-9a-f]{64}$", first.Sha256Hex);
        Assert.Equal("reward-forest-v2", RewardForestFingerprintV2.Domain);
    }

    [Fact]
    public void Fingerprint_matches_the_documented_v2_golden_vector()
    {
        // Pins the v2 domain, field order, presence markers, little-endian scalars,
        // invariant decimals, UTF-8 encoding, and canonical node ordering.
        var fingerprint = RewardForestFingerprintV2.Compute(
            "provider-\ud83d\ude80",
            "lot-\u00e9",
            RewardForest.Create(ValidNodes().Reverse()));

        Assert.Equal("57795a025a5d5d74d61167f9a91e2a468ac0960e1691aa6d205a0b2cc55f280e", fingerprint.Sha256Hex);
    }

    [Fact]
    public void Fingerprint_rejects_the_lone_surrogate_pair_that_previously_collided()
    {
        const string loneHighSurrogate = "provider-\ud800";
        const string loneLowSurrogate = "provider-\udc00";
        var replacementFallback = new UTF8Encoding(false, false);

        Assert.Equal(
            replacementFallback.GetBytes(loneHighSurrogate),
            replacementFallback.GetBytes(loneLowSurrogate));
        Assert.Throws<CargoCatalogValidationException>(() =>
            RewardForestFingerprintV2.Compute(loneHighSurrogate, "lot", RewardForest.Create(ValidNodes())));
        Assert.Throws<CargoCatalogValidationException>(() =>
            RewardForestFingerprintV2.Compute(loneLowSurrogate, "lot", RewardForest.Create(ValidNodes())));

        Assert.Matches(
            "^[0-9a-f]{64}$",
            RewardForestFingerprintV2.Compute("provider-\ud83d\ude80", "lot", RewardForest.Create(ValidNodes())).Sha256Hex);
    }

    [Fact]
    public void Fingerprint_is_sensitive_to_identity_and_every_semantic_node_field()
    {
        var baseline = Fingerprint(ValidNodes());
        var changes = new[]
        {
            RewardForestFingerprintV2.Compute("other-provider", "lot", RewardForest.Create(ValidNodes())),
            RewardForestFingerprintV2.Compute("provider", "other-lot", RewardForest.Create(ValidNodes())),
            Fingerprint([Node("different-root", "different-root", "weapon", null, null), Node("different-root", "child", "optic", "different-root", "mod_scope", new(1, 2, CanonicalRotation.Vertical), 30, Stable())]),
            Fingerprint([Node("root", "root", "different-template", null, null), Child()]),
            Fingerprint([Root(), Node("root", "child-renamed", "optic", "root", "mod_scope", new(1, 2, CanonicalRotation.Vertical), 30, Stable())]),
            Fingerprint([Root(), Node("root", "child", "optic", "root", "different-slot", new(1, 2, CanonicalRotation.Vertical), 30, Stable())]),
            Fingerprint([Root(), Node("root", "child", "optic", "root", "mod_scope", new(2, 2, CanonicalRotation.Vertical), 30, Stable())]),
            Fingerprint([Root(), Node("root", "child", "optic", "root", "mod_scope", new(1, 3, CanonicalRotation.Vertical), 30, Stable())]),
            Fingerprint([Root(), Node("root", "child", "optic", "root", "mod_scope", new(1, 2, CanonicalRotation.Horizontal), 30, Stable())]),
            Fingerprint([Root(), Node("root", "child", "optic", "root", "mod_scope", new(1, 2, CanonicalRotation.Vertical), 31, Stable())]),
            Fingerprint([Root(), Node("root", "child", "optic", "root", "mod_scope", null, 30, Stable())]),
            Fingerprint([Root(), Node("root", "child", "optic", "root", "mod_scope", new(1, 2, CanonicalRotation.Vertical), 30, null)]),
            Fingerprint([Root(), Node("root", "child", "optic", "root", "mod_scope", new(1, 2, CanonicalRotation.Vertical), 30, new RewardStableState(79m, 90m, 10m, 20m, RewardResourceKind.Generic))]),
            Fingerprint([Root(), Node("root", "child", "optic", "root", "mod_scope", new(1, 2, CanonicalRotation.Vertical), 30, new RewardStableState(80m, 91m, 10m, 20m, RewardResourceKind.Generic))]),
            Fingerprint([Root(), Node("root", "child", "optic", "root", "mod_scope", new(1, 2, CanonicalRotation.Vertical), 30, new RewardStableState(80m, 90m, 11m, 20m, RewardResourceKind.Generic))]),
            Fingerprint([Root(), Node("root", "child", "optic", "root", "mod_scope", new(1, 2, CanonicalRotation.Vertical), 30, new RewardStableState(80m, 90m, 10m, 21m, RewardResourceKind.Generic))]),
            Fingerprint([Root(), Node("root", "child", "optic", "root", "mod_scope", new(1, 2, CanonicalRotation.Vertical), 30, new RewardStableState(null, 90m, 10m, 20m, RewardResourceKind.Generic))]),
            Fingerprint([Root(), Node("root", "child", "optic", "root", "mod_scope", new(1, 2, CanonicalRotation.Vertical), 30, new RewardStableState(80m, null, 10m, 20m, RewardResourceKind.Generic))]),
            Fingerprint([Root(), Node("root", "child", "optic", "root", "mod_scope", new(1, 2, CanonicalRotation.Vertical), 30, new RewardStableState(80m, 90m, 10m, 20m, RewardResourceKind.FoodDrink))])
        };

        Assert.All(changes, changed => Assert.NotEqual(baseline, changed));
    }

    [Fact]
    public void Fingerprint_is_sensitive_to_parent_topology()
    {
        var nested = Fingerprint([
            Root(),
            Node("root", "carrier", "carrier-template", "root", "slot"),
            Node("root", "child", "optic", "carrier", "slot")
        ]);
        var flat = Fingerprint([
            Root(),
            Node("root", "carrier", "carrier-template", "root", "slot"),
            Node("root", "child", "optic", "root", "slot")
        ]);

        Assert.NotEqual(nested, flat);
    }

    [Fact]
    public void Stable_state_value_identity_includes_the_closed_resource_kind()
    {
        var generic = new RewardStableState(
            resourceValue: 10m,
            maximumResourceValue: 20m,
            resourceKind: RewardResourceKind.Generic);
        var food = new RewardStableState(
            resourceValue: 10m,
            maximumResourceValue: 20m,
            resourceKind: RewardResourceKind.FoodDrink);

        Assert.NotEqual(generic, food);
        Assert.NotEqual(generic.GetHashCode(), food.GetHashCode());
    }

    [Fact]
    public void Create_rejects_invalid_graphs()
    {
        Assert.Throws<CargoCatalogValidationException>(() => RewardForest.Create([]));
        Assert.Throws<CargoCatalogValidationException>(() => RewardForest.Create([Root(), Root()]));
        Assert.Throws<CargoCatalogValidationException>(() => RewardForest.Create([
            Root(), Node("root", "orphan", "optic", "missing", "slot")
        ]));
        Assert.Throws<CargoCatalogValidationException>(() => RewardForest.Create([
            Root(),
            Node("root", "cycle-a", "a", "cycle-b", "slot"),
            Node("root", "cycle-b", "b", "cycle-a", "slot")
        ]));
        Assert.Throws<CargoCatalogValidationException>(() => RewardForest.Create([
            Root(), Node("other", "other", "other", null, null),
            Node("root", "crossed", "optic", "other", "slot")
        ]));
        Assert.Throws<CargoCatalogValidationException>(() => RewardForest.Create([
            Node("declared-root", "actual-root", "weapon", null, null)
        ]));
    }

    [Fact]
    public void Create_rejects_depth_root_node_and_canonical_size_limits()
    {
        var tooDeep = new List<RewardForestNode> { Root() };
        var parent = "root";
        for (var depth = 1; depth <= RewardForest.MaxDepth + 1; depth++)
        {
            var path = $"node-{depth:D2}";
            tooDeep.Add(Node("root", path, "template", parent, "slot"));
            parent = path;
        }

        var tooManyRoots = Enumerable.Range(0, RewardForest.MaxRootCount + 1)
            .Select(index => Node($"root-{index:D3}", $"root-{index:D3}", "template", null, null));
        var tooManyNodes = new List<RewardForestNode> { Root() };
        tooManyNodes.AddRange(Enumerable.Range(0, RewardForest.MaxNodeCount)
            .Select(index => Node("root", $"child-{index:D4}", "template", "root", "slot")));
        var oversized = new List<RewardForestNode> { Root() };
        oversized.AddRange(Enumerable.Range(0, RewardForest.MaxNodeCount - 1)
            .Select(index => Node(
                "root",
                $"child-{index:D4}-" + new string('x', 450),
                new string('t', 500),
                "root",
                new string('s', 500))));

        Assert.Throws<CargoCatalogValidationException>(() => RewardForest.Create(tooDeep));
        Assert.Throws<CargoCatalogValidationException>(() => RewardForest.Create(tooManyRoots));
        Assert.Throws<CargoCatalogValidationException>(() => RewardForest.Create(tooManyNodes));
        Assert.Throws<CargoCatalogValidationException>(() => RewardForest.Create(oversized));
    }

    [Fact]
    public void Nodes_reject_invalid_quantity_location_and_stable_state()
    {
        Assert.Throws<CargoCatalogValidationException>(() => Node("root", "root", "template", null, null, stackCount: 0));
        Assert.Throws<CargoCatalogValidationException>(() => new CanonicalInternalLocation(-1, 0, CanonicalRotation.Horizontal));
        Assert.Throws<CargoCatalogValidationException>(() => new CanonicalInternalLocation(0, 0, (CanonicalRotation)99));
        Assert.Throws<CargoCatalogValidationException>(() => new RewardStableState(-1m, 2m, null, null));
        Assert.Throws<CargoCatalogValidationException>(() => new RewardStableState(3m, 2m, null, null));
        Assert.Throws<CargoCatalogValidationException>(() =>
            new RewardStableState(null, null, 3m, 2m, RewardResourceKind.Generic));
        Assert.Throws<CargoCatalogValidationException>(() =>
            new RewardStableState(resourceValue: 1m, maximumResourceValue: 2m));
        Assert.Throws<CargoCatalogValidationException>(() =>
            new RewardStableState(resourceKind: RewardResourceKind.MedKit));
        Assert.Throws<CargoCatalogValidationException>(() =>
            new RewardStableState(
                resourceValue: 1m,
                resourceKind: RewardResourceKind.MedKit));
        Assert.Throws<CargoCatalogValidationException>(() =>
            new RewardStableState(
                maximumResourceValue: 2m,
                resourceKind: RewardResourceKind.MedKit));
        Assert.Throws<CargoCatalogValidationException>(() =>
            new RewardStableState(
                resourceValue: 1m,
                maximumResourceValue: 2m,
                resourceKind: (RewardResourceKind)99));
        Assert.Throws<CargoCatalogValidationException>(() =>
            Node("root", "root", "template", null, "root-cannot-have-slot"));
        Assert.Throws<CargoCatalogValidationException>(() =>
            Node("root", "child", "template", "root", null));
    }

    private static RewardForestFingerprintV2 Fingerprint(IEnumerable<RewardForestNode> nodes) =>
        RewardForestFingerprintV2.Compute("provider", "lot", RewardForest.Create(nodes));

    private static RewardForestNode[] ValidNodes() => [Root(), Child()];

    private static RewardForestNode Root() => Node("root", "root", "weapon", null, null);

    private static RewardForestNode Child() =>
        Node(
            "root",
            "child",
            "optic",
            "root",
            "mod_scope",
            new CanonicalInternalLocation(1, 2, CanonicalRotation.Vertical),
            30,
            Stable());

    private static RewardStableState Stable() =>
        new(80m, 90m, 10m, 20m, RewardResourceKind.Generic);

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
}
