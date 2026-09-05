using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Catalog;

public sealed class RewardCatalogTests
{
    [Fact]
    public void Create_rejects_an_empty_catalog()
    {
        Assert.Throws<RewardCatalogValidationException>(() => RewardCatalog.Create([]));
    }

    [Fact]
    public void Create_rejects_duplicate_reward_ids()
    {
        Assert.Throws<RewardCatalogValidationException>(() => RewardCatalog.Create([
            Definition("same", "root-one", "root-one", RewardRarity.ScavGrade, 0.5),
            Definition("same", "root-two", "root-two", RewardRarity.Contractor, 0.5)
        ]));
    }

    [Fact]
    public void Create_rejects_missing_values_and_unknown_rarities()
    {
        var invalidDefinitions = new[]
        {
            new RewardDefinition("", "One", "root", "one", RewardRarity.ScavGrade, 1d),
            new RewardDefinition("one", "", "root", "one", RewardRarity.ScavGrade, 1d),
            new RewardDefinition("one", "One", "", "one", RewardRarity.ScavGrade, 1d),
            new RewardDefinition("one", "One", "root", "", RewardRarity.ScavGrade, 1d),
            new RewardDefinition("one", "One", "root", "one", (RewardRarity)int.MaxValue, 1d)
        };

        Assert.All(invalidDefinitions, definition =>
            Assert.Throws<RewardCatalogValidationException>(() => RewardCatalog.Create([definition])));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.01d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Validate_rejects_non_positive_or_non_finite_weights(double weight)
    {
        Assert.Throws<RewardCatalogValidationException>(() =>
            Catalog(weight).Validate(new TestPresetResolver()));
    }

    [Fact]
    public void Validate_rejects_weights_that_do_not_total_one()
    {
        var catalog = Catalog(0.99d);
        Assert.Throws<RewardCatalogValidationException>(() => catalog.Validate(new TestPresetResolver()));
    }

    [Fact]
    public void Validate_rejects_missing_and_multi_root_presets()
    {
        var catalog = Catalog(1d);
        Assert.Throws<RewardCatalogValidationException>(() => catalog.Validate(new TestPresetResolver(missingPreset: true)));
        Assert.Throws<RewardCatalogValidationException>(() => catalog.Validate(new TestPresetResolver(multiRoot: true)));
    }

    [Fact]
    public void Validate_rejects_an_orphaned_preset_tree()
    {
        var catalog = Catalog(1d);
        Assert.Throws<RewardCatalogValidationException>(() => catalog.Validate(new TestPresetResolver(orphaned: true)));
    }

    [Fact]
    public void Validate_rejects_a_root_template_mismatch()
    {
        var catalog = Catalog(1d);
        Assert.Throws<RewardCatalogValidationException>(() => catalog.Validate(new TestPresetResolver(rootMismatch: true)));
    }

    [Fact]
    public void Validate_rejects_a_cyclic_preset_tree()
    {
        var catalog = Catalog(1d);
        Assert.Throws<RewardCatalogValidationException>(() => catalog.Validate(new TestPresetResolver(cyclic: true)));
    }

    [Fact]
    public void Validate_rejects_duplicate_preset_item_ids()
    {
        var catalog = Catalog(1d);
        Assert.Throws<RewardCatalogValidationException>(() => catalog.Validate(new TestPresetResolver(duplicateItemId: true)));
    }

    [Fact]
    public void Fingerprint_sorts_child_templates_ordinally_and_preserves_multiplicity()
    {
        var fingerprint = RewardFingerprint.FromPreset(new RewardPresetTree(new[]
        {
            new RewardPresetItem("root", "root-template", null),
            new RewardPresetItem("one", "z", "root"),
            new RewardPresetItem("two", "a", "root"),
            new RewardPresetItem("three", "a", "root")
        }));

        Assert.Equal("root-template", fingerprint.RootTemplateId);
        Assert.Equal(new[] { "a", "a", "z" }, fingerprint.ChildTemplateIds);
    }

    [Fact]
    public void Validate_rejects_duplicate_canonical_fingerprints()
    {
        var catalog = RewardCatalog.Create([
            Definition("one", "root", "one", RewardRarity.ScavGrade, 0.5),
            Definition("two", "root", "two", RewardRarity.Contractor, 0.5)
        ]);

        Assert.Throws<RewardCatalogValidationException>(() => catalog.Validate(new TestPresetResolver(duplicateFingerprint: true)));
    }

    [Fact]
    public void Selector_is_boundary_deterministic_and_never_uses_external_ids()
    {
        var rewards = RewardCatalog.Create([
            Definition("safe-a", "template-a", "a", RewardRarity.ScavGrade, 0.6),
            Definition("safe-b", "template-b", "b", RewardRarity.BlackLabel, 0.4)
        ]).Validate(new TestPresetResolver());

        Assert.Equal("safe-a", WeightedRewardSelector.Select(rewards, 0d).Id);
        Assert.Equal("safe-a", WeightedRewardSelector.Select(rewards, 0.599999d).Id);
        Assert.Equal("safe-b", WeightedRewardSelector.Select(rewards, 0.6d).Id);
        Assert.Equal("safe-b", WeightedRewardSelector.Select(rewards, 0.999999d).Id);
        Assert.Throws<ArgumentOutOfRangeException>(() => WeightedRewardSelector.Select(rewards, 1d));
    }

    [Fact]
    public void Selector_validates_the_entire_list_before_selecting()
    {
        var rewards = RewardCatalog.Create([
            Definition("safe-a", "template-a", "a", RewardRarity.ScavGrade, 0.6),
            Definition("safe-b", "template-b", "b", RewardRarity.BlackLabel, 0.4)
        ]).Validate(new TestPresetResolver());

        Assert.Throws<ArgumentException>(() => WeightedRewardSelector.Select(new[] { rewards[0] }, 0d));
        Assert.Throws<ArgumentException>(() => WeightedRewardSelector.Select(new[] { rewards[0], rewards[0] }, 0d));
        Assert.Throws<ArgumentException>(() => WeightedRewardSelector.Select(new ValidatedReward[] { rewards[0], null! }, 0d));
    }

    private static RewardCatalog Catalog(double weight) => RewardCatalog.Create([
        Definition("one", "root", "one", RewardRarity.ScavGrade, weight)
    ]);

    private static RewardDefinition Definition(
        string id,
        string weaponTemplateId,
        string presetId,
        RewardRarity rarity,
        double weight) =>
        new(id, $"{id} display", weaponTemplateId, presetId, rarity, weight);

    private sealed class TestPresetResolver : IRewardPresetResolver
    {
        private readonly bool _duplicateFingerprint;
        private readonly bool _missingPreset;
        private readonly bool _multiRoot;
        private readonly bool _orphaned;
        private readonly bool _rootMismatch;
        private readonly bool _cyclic;
        private readonly bool _duplicateItemId;

        public TestPresetResolver(
            bool duplicateFingerprint = false,
            bool missingPreset = false,
            bool multiRoot = false,
            bool orphaned = false,
            bool rootMismatch = false,
            bool cyclic = false,
            bool duplicateItemId = false)
        {
            _duplicateFingerprint = duplicateFingerprint;
            _missingPreset = missingPreset;
            _multiRoot = multiRoot;
            _orphaned = orphaned;
            _rootMismatch = rootMismatch;
            _cyclic = cyclic;
            _duplicateItemId = duplicateItemId;
        }

        public RewardPresetTree? Resolve(string presetId)
        {
            if (_missingPreset)
            {
                return null;
            }

            var root = _duplicateFingerprint ? "root" : presetId switch
            {
                "a" => "template-a",
                "b" => "template-b",
                "one" => "root",
                _ => presetId
            };
            if (_rootMismatch)
            {
                root = "unexpected-root";
            }
            var nodes = new List<RewardPresetItem>
            {
                new("root-id", root, null),
                new("child-a", "child-z", "root-id"),
                new("child-b", "child-a", "root-id")
            };

            if (_multiRoot)
            {
                nodes.Add(new RewardPresetItem("another-root", "other", null));
            }

            if (_orphaned)
            {
                nodes.Add(new RewardPresetItem("orphan", "orphan-template", "unknown"));
            }

            if (_cyclic)
            {
                nodes.Add(new RewardPresetItem("cycle-a", "cycle-a-template", "cycle-b"));
                nodes.Add(new RewardPresetItem("cycle-b", "cycle-b-template", "cycle-a"));
            }

            if (_duplicateItemId)
            {
                nodes.Add(new RewardPresetItem("child-a", "duplicate-template", "root-id"));
            }

            return new RewardPresetTree(nodes);
        }
    }
}
