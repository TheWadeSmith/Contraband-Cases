using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class ServerRewardCatalogTests
{
    private const string RootItemId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ChildItemId = "bbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly IReadOnlyDictionary<string, string> ExpectedRoots =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["584148f2245977598f1ad387"] = "54491c4f4bdc2db1078b4568",
            ["5a3a859786f7747e2305e8bf"] = "5a38e6bac4a2826c6e06d79b",
            ["584149c42459775a77263510"] = "57d14d2524597714373db789",
            ["58dffce486f77409f40f8162"] = "574d967124597745970e7c94",
            ["584147732459775a2b6d9f12"] = "57dc2fa62459775949412633",
            ["59ef24b986f77439987b8762"] = "59e6152586f77473dc057aa1",
            ["59b81f7386f77421ac688a0a"] = "59984ab886f7743e98271174",
            ["59411aa786f7747aeb37f9a5"] = "5926bb2186f7744b1c6c6e60",
            ["5841474424597759ba49be91"] = "5644bd2b4bdc2d3b4c8b4572",
            ["5af08cf886f774223c269184"] = "5447a9cd4bdc2dbd208b4567",
            ["5e03511086f7744ccb1fb6cf"] = "5df8ce05b11454561e39243b",
            ["5a3a85af86f774745637d46c"] = "5a367e5dc4a282000e49738f"
        };

    [Fact]
    public void Configured_fixed_presets_resolve_to_expected_weapon_roots()
    {
        var presets = ExpectedRoots.ToDictionary(
            pair => (MongoId)pair.Key,
            pair => CreatePreset(pair.Key, pair.Value));
        var resolver = new SptRewardPresetResolver(
            presetId => presets.ContainsKey(presetId),
            presetId => presets[presetId],
            items => items.Count() * 1_000d);
        var json = File.ReadAllText(ConfigPath("rewards.json"));

        var rewards = ServerRewardCatalog.Parse(json).Validate(resolver);

        Assert.Equal(12, rewards.Count);
        Assert.Equal(ExpectedRoots.Keys, rewards.Select(reward => reward.PresetId));
        Assert.Equal(ExpectedRoots.Values, rewards.Select(reward => reward.Fingerprint.RootTemplateId));
        Assert.Equal(0.60d, rewards.Where(reward => reward.Rarity == RewardRarity.ScavGrade).Sum(reward => reward.Weight), 12);
        Assert.Equal(0.24d, rewards.Where(reward => reward.Rarity == RewardRarity.Contractor).Sum(reward => reward.Weight), 12);
        Assert.Equal(0.12d, rewards.Where(reward => reward.Rarity == RewardRarity.Restricted).Sum(reward => reward.Weight), 12);
        Assert.Equal(0.04d, rewards.Where(reward => reward.Rarity == RewardRarity.BlackLabel).Sum(reward => reward.Weight), 12);
        Assert.All(rewards, reward => Assert.True(reward.HandbookValue >= 1_000));
        ServerRewardCatalog.EnsureFirstBuildCatalog(rewards);
        Assert.Throws<RewardCatalogValidationException>(() =>
            ServerRewardCatalog.EnsureFirstBuildCatalog(rewards.Take(11).ToArray()));
    }

    [Fact]
    public void Configured_catalog_rejects_an_unrecognized_preset_id()
    {
        var invalidConfig = File.ReadAllText(ConfigPath("rewards.json")).Replace(
            "584148f2245977598f1ad387",
            "not-a-configured-preset",
            StringComparison.Ordinal);
        var resolver = new SptRewardPresetResolver(_ => false, _ => null, _ => 0d);

        var catalog = ServerRewardCatalog.Parse(invalidConfig);

        Assert.Throws<RewardCatalogValidationException>(() => catalog.Validate(resolver));
    }

    [Fact]
    public void Parser_accepts_jsonc_and_invariant_string_weights()
    {
        var catalog = ServerRewardCatalog.Parse("""
            {
              // String weights remain supported for existing custom catalogs.
              "rewards": [
                {
                  "id": "one",
                  "displayName": "One",
                  "weaponTemplateId": "root",
                  "presetId": "one",
                  "rarity": "ScavGrade",
                  "weight": "1.0",
                },
              ],
            }
            """);

        var definition = Assert.Single(catalog.Rewards);
        Assert.Equal("one", definition.Id);
        Assert.Equal(1d, definition.Weight);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{ \"rewards\": {} }")]
    [InlineData("{ \"rewards\": [ true ] }")]
    [InlineData("{ \"rewards\": [] }")]
    [InlineData("{ \"rewards\": [ { \"id\":\"one\" } ] }")]
    [InlineData("{ \"rewards\": [ { \"id\":\"one\", \"displayName\":\"One\", \"weaponTemplateId\":\"root\", \"presetId\":\"one\", \"rarity\":\"Unknown\", \"weight\":1 } ] }")]
    [InlineData("{ \"rewards\": [ { \"id\":\"one\", \"displayName\":\"One\", \"weaponTemplateId\":\"root\", \"presetId\":\"one\", \"rarity\":\"ScavGrade, Contractor\", \"weight\":1 } ] }")]
    public void Parser_rejects_invalid_catalog_shapes(string json)
    {
        Assert.Throws<RewardCatalogValidationException>(() => ServerRewardCatalog.Parse(json));
    }

    [Theory]
    [InlineData("{ \"rewards\": [], \"unexpected\": true }")]
    [InlineData("{ \"rewards\": [ { \"id\":\"one\", \"displayName\":\"One\", \"weaponTemplateId\":\"root\", \"presetId\":\"one\", \"rarity\":\"ScavGrade\", \"weight\":1, \"unexpected\":true } ] }")]
    [InlineData("{ \"rewards\": [], \"rewards\": [] }")]
    public void Parser_rejects_unknown_or_duplicate_properties(string json)
    {
        Assert.Throws<RewardCatalogValidationException>(() => ServerRewardCatalog.Parse(json));
    }

    [Fact]
    public void Shared_and_server_assemblies_do_not_reference_Newtonsoft_Json()
    {
        Assert.DoesNotContain(
            typeof(RewardCatalog).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "Newtonsoft.Json", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(ServerRewardCatalog).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "Newtonsoft.Json", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolver_checks_is_preset_before_getting_a_preset()
    {
        var getCalled = false;
        var resolver = new SptRewardPresetResolver(
            _ => false,
            _ =>
            {
                getCalled = true;
                return null;
            },
            _ => 0d);

        Assert.Null(resolver.Resolve("000000000000000000000001"));
        Assert.False(getCalled);
    }

    [Fact]
    public void Resolver_normalizes_only_the_declared_preset_root_and_sums_whole_tree_value()
    {
        var preset = CreatePreset("000000000000000000000010", "000000000000000000000020");
        var resolver = new SptRewardPresetResolver(_ => true, _ => preset, items => items.Count() * 123.5d);

        var tree = resolver.Resolve(preset.Id.ToString());

        Assert.NotNull(tree);
        Assert.Equal(247, tree!.HandbookValue);
        Assert.Collection(
            tree.Items,
            root => Assert.Null(root.ParentId),
            child => Assert.Equal(RootItemId, child.ParentId));
    }

    private static Preset CreatePreset(string presetId, string rootTemplateId) => new()
    {
        Id = (MongoId)presetId,
        Parent = RootItemId,
        Items =
        [
            new Item { Id = RootItemId, Template = (MongoId)rootTemplateId, ParentId = "preset-shell" },
            new Item { Id = ChildItemId, Template = "000000000000000000000099", ParentId = RootItemId, SlotId = "mod" }
        ]
    };

    private static string ConfigPath(string fileName) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../config", fileName));
}
