using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Xunit;
using IoPath = System.IO.Path;

namespace ContrabandCases.Tests.Server;

/// <summary>
/// Covers the six curated-mod reward packs added in the second wave of the
/// "safe real IDs, not guesses" round: WTT-ContentBackport and
/// Eco-Attachment-Emporium each get a field/elite pair, and Amonya and
/// Eco-WW2-Pack each get one elite pack. Every template ID referenced by
/// these packs is a real, statically-declared MongoID copied directly out of
/// the source mod's own shipped JSON (and, for WTT-ContentBackport, cross-
/// checked against a real server log line naming the resolved item), never
/// invented -- see the docs/ investigation notes for the per-mod evidence.
///
/// Like <see cref="NewRewardPackTests"/>, these must load with the exact
/// schema/scale used by the rest of the project, resolve completely when
/// their source mod's templates are present, and fail closed (skipped, never
/// a crash, never a corrupted pool) when their source mod is not installed.
/// </summary>
public sealed class ExpandedModRewardPackTests
{
    private const string ParentTemplateId = "54009119af1c881c07000029";
    private const string CoreTemplateId = "900000000000000000000001";

    public static readonly TheoryData<string, string, double, int> PackFiles = new()
    {
        { "wtt-contentbackport.field-resupply.json", "wtt-contentbackport.field-resupply", 0.20, 3 },
        { "wtt-contentbackport.elite-optics.json", "wtt-contentbackport.elite-optics", 0.07, 3 },
        { "eco-attachment.field-cache.json", "eco-attachment.field-cache", 0.16, 4 },
        { "eco-attachment.elite-optics.json", "eco-attachment.elite-optics", 0.06, 4 },
        { "amonya.arcane-cache.json", "amonya.arcane-cache", 0.05, 3 },
        { "eco-ww2.relic-cache.json", "eco-ww2.relic-cache", 0.05, 2 }
    };

    [Theory]
    [MemberData(nameof(PackFiles))]
    public void New_pack_loads_with_expected_identity_scale_and_lot_count(
        string fileName,
        string expectedProviderId,
        double expectedProviderWeight,
        int expectedLotCount)
    {
        var pack = new JsonRewardPackLoader().LoadFile(RewardPackPath(fileName));

        Assert.Equal(expectedProviderId, pack.ProviderId);
        Assert.Equal(expectedProviderWeight, pack.ProviderWeight, 12);
        Assert.Equal(expectedLotCount, pack.Lots.Count);
        Assert.Empty(pack.RequiredPresetIds);
        Assert.Empty(pack.RequiredBundleKeys);
        Assert.NotEmpty(pack.RequiredTemplateIds);

        // Every template a lot's recipe references must be declared in
        // requiredTemplateIds, exactly like the audited existing packs --
        // this is what lets CargoPackRequirementValidator fail closed
        // correctly instead of silently loading a partial/corrupt pool.
        var recipeTemplateIds = pack.Lots
            .SelectMany(lot => lot.RecipeLines)
            .Select(line => Assert.IsType<TemplateLine>(line).TemplateId)
            .Distinct(StringComparer.Ordinal);
        Assert.Equal(
            pack.RequiredTemplateIds.OrderBy(id => id, StringComparer.Ordinal),
            recipeTemplateIds.OrderBy(id => id, StringComparer.Ordinal));

        // The project's existing rarity scale: core = 1.0, vault = 0.05,
        // themed packs ~0.12-0.35. New "everyday" packs land in that themed
        // range; new "elite" packs land near vault's rarity.
        Assert.InRange(expectedProviderWeight, 0.01, 1.0);
    }

    [Fact]
    public void Everyday_packs_use_a_common_scale_and_elite_packs_use_a_rare_vault_adjacent_scale()
    {
        var loader = new JsonRewardPackLoader();
        var everyday = new[]
            {
                "wtt-contentbackport.field-resupply.json",
                "eco-attachment.field-cache.json"
            }
            .Select(fileName => loader.LoadFile(RewardPackPath(fileName)).ProviderWeight)
            .ToArray();
        var elite = new[]
            {
                "wtt-contentbackport.elite-optics.json",
                "eco-attachment.elite-optics.json",
                "amonya.arcane-cache.json",
                "eco-ww2.relic-cache.json"
            }
            .Select(fileName => loader.LoadFile(RewardPackPath(fileName)).ProviderWeight)
            .ToArray();

        Assert.All(everyday, weight => Assert.InRange(weight, 0.12, 0.35));
        Assert.All(elite, weight => Assert.InRange(weight, 0.04, 0.10));
    }

    [Theory]
    [MemberData(nameof(PackFiles))]
    public void New_pack_is_skipped_wholly_and_without_throwing_when_its_source_mod_is_not_installed(
        string fileName,
        string expectedProviderId,
        double expectedProviderWeight,
        int expectedLotCount)
    {
        var pack = new JsonRewardPackLoader().LoadFile(RewardPackPath(fileName));
        Assert.Equal(expectedProviderWeight, pack.ProviderWeight, 12);
        Assert.Equal(expectedLotCount, pack.Lots.Count);
        var core = MinimalCorePack();
        var coreTemplate = Template(CoreTemplateId);
        var builder = new CargoCatalogSnapshotBuilder(
            Resolver([]),
            Evaluator([]),
            // Core's own template always resolves (core is always installed);
            // none of the new pack's real mod template IDs do -- this is
            // exactly "the source mod is not installed".
            new CargoPackRequirementValidator(
                id => id == CoreTemplateId ? coreTemplate : null,
                _ => null).Validate);

        var snapshot = builder.Build(core, [pack]);

        Assert.DoesNotContain(snapshot.Lots, lot => lot.Identity.ProviderId == expectedProviderId);
        var skipped = Assert.Single(snapshot.SkippedPacks, skip => skip.ProviderId == expectedProviderId);
        Assert.Contains("missing finalized", skipped.Reason, StringComparison.Ordinal);
        Assert.Single(snapshot.Lots);
        Assert.Equal("core-anchor", snapshot.Lots[0].Identity.LotId);
    }

    [Theory]
    [MemberData(nameof(PackFiles))]
    public void New_pack_resolves_completely_once_its_source_mod_templates_are_present(
        string fileName,
        string expectedProviderId,
        double _,
        int expectedLotCount)
    {
        var pack = new JsonRewardPackLoader().LoadFile(RewardPackPath(fileName));
        var templates = pack.RequiredTemplateIds
            .Select(id => Template(id))
            .ToArray();
        var core = MinimalCorePack();
        var coreTemplate = Template(CoreTemplateId);
        var builder = new CargoCatalogSnapshotBuilder(
            Resolver(templates),
            Evaluator(templates),
            new CargoPackRequirementValidator(
                id => id == CoreTemplateId
                    ? coreTemplate
                    : templates.FirstOrDefault(t => t.Id.ToString() == id),
                _ => null).Validate);

        var snapshot = builder.Build(core, [pack]);

        Assert.Empty(snapshot.SkippedPacks);
        var resolved = snapshot.Lots.Where(lot => lot.Identity.ProviderId == expectedProviderId).ToArray();
        Assert.Equal(expectedLotCount, resolved.Length);
        Assert.All(resolved, lot =>
        {
            Assert.True(lot.Evaluation.HandbookValue > 0);
            Assert.True(lot.Evaluation.UseValue > 0);
            Assert.Contains(
                lot.Forest.Nodes,
                node => string.Equals(node.TemplateId, lot.Definition.AnchorTemplateId, StringComparison.Ordinal));
        });
    }

    private static CargoLotPack MinimalCorePack() => new(
        "core",
        "0.3.3",
        "Minimal Core",
        1.0,
        [CoreTemplateId],
        [],
        [],
        [
            new CargoLotDefinition(
                "core",
                "0.3.3",
                "core-anchor",
                "Core Anchor",
                "A minimal core lot so the snapshot builder has a valid required pack.",
                new FamilyId("arsenal"),
                new TrackId("core-anchor"),
                CoreTemplateId,
                1.0,
                new RaidRole("testing"),
                [new TemplateLine(CoreTemplateId, 1, 1)])
        ]);

    private static CargoLotResolver Resolver(IEnumerable<TemplateItem> templates)
    {
        var map = templates
            .Append(Template(CoreTemplateId))
            .ToDictionary(template => template.Id.ToString(), StringComparer.Ordinal);
        return new CargoLotResolver(new CargoLotResolverDependencies(
            id => map.GetValueOrDefault(id),
            _ => null));
    }

    private static CargoLotEvaluator Evaluator(IEnumerable<TemplateItem> templates)
    {
        var map = templates
            .Append(Template(CoreTemplateId))
            .ToDictionary(template => template.Id.ToString(), StringComparer.Ordinal);
        return new CargoLotEvaluator(
            id => map.GetValueOrDefault(id),
            _ => 5_000d);
    }

    private static TemplateItem Template(string id) => new()
    {
        Id = (MongoId)id,
        Parent = ParentTemplateId,
        Name = id,
        Type = "Item",
        Properties = new TemplateItemProperties
        {
            // Generously large: some of these packs' recipes stack ammo up to
            // 20 per instance, and this synthetic template stands in for
            // whatever real stack size the source mod's real item declares.
            StackMaxSize = 999,
            Width = 1,
            Height = 1,
            Slots = [],
            Grids = [],
            Prefab = new Prefab { Path = $"test/{id}.bundle" }
        }
    };

    private static string RewardPackPath(string fileName) =>
        IoPath.GetFullPath(IoPath.Combine(
            AppContext.BaseDirectory,
            "../../../../config/reward-packs",
            fileName));
}
