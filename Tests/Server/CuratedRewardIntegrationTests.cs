using System.Text.Json;
using System.Text.Json.Serialization;
using ContrabandCases.Client.Opening;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Json;
using Xunit;
using Path = System.IO.Path;

namespace ContrabandCases.Tests.Server;

public sealed class CuratedRewardIntegrationTests
{
    [Fact]
    public void Current_prizes_have_distinct_item_compositions_not_renamed_duplicates()
    {
        var duplicates = ReadCatalog().FreshOpeningLots.GroupBy(lot => string.Join("|", lot.Forest.Nodes
                .Select(node => $"{node.TemplateId}:{node.StackCount}:{node.SlotId}")
                .OrderBy(value => value, StringComparer.Ordinal)))
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(", ", group.Select(lot => lot.Identity.ProviderId + "/" + lot.Identity.LotId)));
        Assert.Empty(duplicates);
    }

    [Fact]
    public void All_current_packs_resolve_complete_bounded_forests_and_the_real_client_contract()
    {
        var catalog = ReadCatalog();
        Assert.Empty(catalog.SkippedPacks);
        Assert.Equal(117, catalog.FreshOpeningLots.Count);
        foreach (var lot in catalog.FreshOpeningLots)
        {
            Assert.EndsWith(".compact-v2", lot.Identity.LotId);
            Assert.InRange(lot.Forest.Roots.Count, 1, 8);
            Assert.InRange(lot.Forest.Nodes.Count, 1, 128);
            Assert.InRange(lot.Evaluation.FootprintCells, 1, 64);
        }
        var library = ManifestLibraryProjection.Create(new CaseOpeningJournal(), catalog, new Dictionary<string, string>());
        var parsed = ManifestSnapshotParser.ParseLibrary(JsonSerializer.Serialize(new { err = 0, errmsg = (string?)null, data = library }));
        Assert.Equal(catalog.FreshOpeningLots.Count, parsed.Lots.Count);
    }

    [Theory]
    [InlineData(ModConstants.CaseTemplateId)]
    [InlineData(CaseContracts.Operations)]
    [InlineData(CaseContracts.Relics)]
    [InlineData(CaseContracts.BlackSite)]
    public void Each_equipment_case_has_honest_million_scale_pricing_and_both_loss_and_win_outcomes(string template)
    {
        var mixed = ReadCatalog();
        var catalog = template == ModConstants.CaseTemplateId ? mixed : CaseCatalogs.ForCase(mixed, template);
        Assert.True(catalog.OpeningEnabled, catalog.OpeningDisabledReason);
        var price = ManifestCatalogEconomy.CalculateAutomaticPrices(catalog).CasePrice;
        Assert.InRange(price, 750_000, 1_300_000);
        Assert.Contains(catalog.FreshOpeningLots, lot => lot.Evaluation.UseValue < (price + ManifestCatalogEconomy.OpeningKeyAllowance) * 0.9m);
        Assert.Contains(catalog.FreshOpeningLots, lot => lot.Evaluation.UseValue > (price + ManifestCatalogEconomy.OpeningKeyAllowance) * 1.1m);
        Assert.Contains(catalog.FreshOpeningLots, lot => lot.Evaluation.Grade == RewardRarity.Restricted);
        Assert.Contains(catalog.FreshOpeningLots, lot => lot.Evaluation.Grade == RewardRarity.BlackLabel);
    }

    [Fact]
    public void Every_current_weapon_package_contains_compatible_ammunition()
    {
        using var doc = ReadFixture();
        var templates = doc.RootElement.GetProperty("templates");
        bool IsWeapon(string id)
        {
            for (var depth = 0; depth < 16 && templates.TryGetProperty(id, out var item); depth++)
            {
                if (id == "5422acb9af1c889c16000029") return true;
                id = item.GetProperty("_parent").GetString() ?? "";
            }
            return false;
        }
        foreach (var lot in ReadCatalog().FreshOpeningLots)
        foreach (var weapon in lot.Forest.Roots.Where(r => IsWeapon(r.TemplateId)))
        {
            var caliber = templates.GetProperty(weapon.TemplateId).GetProperty("_props").GetProperty("ammoCaliber").GetString();
            Assert.True(lot.Forest.Roots.Any(r => templates.GetProperty(r.TemplateId).GetProperty("_props")
                .TryGetProperty("Caliber", out var ammunition) && ammunition.GetString() == caliber),
                $"{lot.Identity.ProviderId}/{lot.Identity.LotId}: missing ammunition {caliber} for {weapon.TemplateId}");
            var compatibleMagazines = templates.GetProperty(weapon.TemplateId).GetProperty("_props").GetProperty("Slots")
                .EnumerateArray().Where(slot => slot.GetProperty("_name").GetString() == "mod_magazine")
                .SelectMany(slot => slot.GetProperty("_props").GetProperty("filters").EnumerateArray())
                .SelectMany(filter => filter.GetProperty("Filter").EnumerateArray()).Select(id => id.GetString()).ToHashSet();
            Assert.Contains(lot.Forest.Nodes, node => compatibleMagazines.Contains(node.TemplateId) &&
                (node.ParentLogicalPath == weapon.LogicalPath && node.SlotId == "mod_magazine" || node.ParentLogicalPath is null));
        }
    }

    [Fact]
    public void Named_chase_cards_and_T7_goggles_do_not_leak_into_ordinary_draw_pools()
    {
        var chaseCards = new[] { "6699b7fd0d1d25cf00072bf8", "669979d0c73060411f04d2a1", "669c812e721191eae609114a" };
        foreach (var lot in ReadCatalog().FreshOpeningLots)
            if (lot.Forest.Nodes.Any(n => chaseCards.Contains(n.TemplateId) || n.TemplateId == "5c110624d174af029e69734c"))
                Assert.True(ManifestOpeningPool.IsChase(lot), lot.Identity.ProviderId + "/" + lot.Identity.LotId);
    }

    [Fact]
    public void Every_case_keeps_current_Relay_paths_and_genuine_premium_surprises()
    {
        var mixed = ReadCatalog();
        var selector = new ManifestCatalogSelector();
        foreach (var template in CaseContracts.Templates.Where(t => t != CaseContracts.CashCache))
        {
            var view = template == ModConstants.CaseTemplateId ? mixed : CaseCatalogs.ForCase(mixed, template);
            view = CaseCatalogs.WithPrice(view, ManifestCatalogEconomy.CalculateAutomaticPrices(view).CasePrice);
            var current = view.FreshOpeningLots.Where(l => l.Evaluation.Grade != RewardRarity.BlackLabel).ToArray();
            var eligible = current.Count(l => selector.CreateRelayCandidatesForStage(view,
                new ManifestEntitlementSnapshot(l.Evaluation.Grade, l.Identity, l.Forest, l.Fingerprint), 1).Count > 0);
            // Distinctive equipment and collector recipes take priority over
            // padding every track with filler just to manufacture more wagers.
            Assert.True(eligible * 100 >= current.Length * 50, $"{CaseContracts.Name(template)}: {eligible}/{current.Length}");
            Assert.True(ManifestPremiumPool.IsAvailable(view, ContrabandCases.Shared.Manifest.ManifestOpeningTier.Epic), CaseContracts.Name(template) + " Epic");
            Assert.True(ManifestPremiumPool.IsAvailable(view, ContrabandCases.Shared.Manifest.ManifestOpeningTier.Legendary), CaseContracts.Name(template) + " Legendary");
        }
    }

    [Fact]
    public void Every_new_night_vision_set_has_a_real_compatible_mount_chain()
    {
        using var doc = ReadFixture();
        var templates = doc.RootElement.GetProperty("templates");
        bool Accepts(string parent, string child) => templates.GetProperty(parent).GetProperty("_props").GetProperty("Slots")
            .EnumerateArray().Any(slot => slot.GetProperty("_props").GetProperty("filters").EnumerateArray()
                .Any(filter => filter.GetProperty("Filter").EnumerateArray().Any(id => id.GetString() == child)));
        foreach (var lot in ReadCatalog().FreshOpeningLots)
        {
            var ids = lot.Forest.Nodes.Select(n => n.TemplateId).ToHashSet();
            if (ids.Contains("5c0558060db834001b735271"))
            {
                Assert.Contains("5a154d5cfcdbcb001a3b00da", ids);
                Assert.True(Accepts("5a154d5cfcdbcb001a3b00da", "5c0558060db834001b735271"));
            }
            if (ids.Contains("5c110624d174af029e69734c"))
            {
                string[] chain = ["5a154d5cfcdbcb001a3b00da", "5a16b8a9fcdbcb00165aa6ca", "5c11046cd174af02a012e42b", "5c110624d174af029e69734c"];
                Assert.All(chain, id => Assert.Contains(id, ids));
                for (var i = 0; i < chain.Length - 1; i++) Assert.True(Accepts(chain[i], chain[i + 1]));
            }
            if (ids.Contains("689b889473ebd6871805edd6"))
            {
                string[] chain = ["69cbfe34c293038df7002963", "689dbded6c7e684817080c29", "689b889473ebd6871805edd6"];
                Assert.All(chain, id => Assert.Contains(id, ids));
                for (var i = 0; i < chain.Length - 1; i++) Assert.True(Accepts(chain[i], chain[i + 1]));
            }
        }
    }

    private static JsonDocument ReadFixture() => JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../Tests/Fixtures/curated-mod-templates.json"))));

    internal static CargoCatalogSnapshot ReadCatalog(bool nativeOnly = false)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString };
        foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters()) options.Converters.Add(converter);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Tests/Fixtures/curated-mod-templates.json")));
        var templates = doc.RootElement.GetProperty("templates").Deserialize<Dictionary<string, TemplateItem>>(options)!;
        var presets = doc.RootElement.GetProperty("presets").Deserialize<Dictionary<string, Preset>>(options)!;
        var prices = doc.RootElement.GetProperty("prices").Deserialize<Dictionary<string, double>>(options)!;
        var loader = new JsonRewardPackLoader();
        var core = loader.LoadFile(Path.Combine(root, "config/reward-packs/core.json"));
        var optional = Directory.GetFiles(Path.Combine(root, "config/reward-packs"), "*.json")
            .Where(path => Path.GetFileName(path) != "core.json" &&
                (!nativeOnly || Path.GetFileName(path) == "vault.json")).Select(loader.LoadFile).ToArray();
        return new CargoCatalogSnapshotBuilder(
            new CargoLotResolver(new CargoLotResolverDependencies(templates.GetValueOrDefault, presets.GetValueOrDefault)),
            new CargoLotEvaluator(templates.GetValueOrDefault, id => prices.GetValueOrDefault(id)),
            new CargoPackRequirementValidator(templates.GetValueOrDefault, presets.GetValueOrDefault).Validate).Build(core, optional);
    }
}
