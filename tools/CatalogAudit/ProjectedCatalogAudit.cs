using System.Text.Json;
using System.Text.Json.Serialization;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;

internal static class ProjectedCatalogAudit
{
    internal static object Create(string fixturePath, string packRoot)
    {
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString };
        foreach (var converter in new SptJsonConverterRegistrator().GetJsonConverters()) options.Converters.Add(converter);
        using var doc = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var templates = doc.RootElement.GetProperty("templates").Deserialize<Dictionary<string, TemplateItem>>(options)!;
        var presets = doc.RootElement.GetProperty("presets").Deserialize<Dictionary<string, Preset>>(options)!;
        var prices = doc.RootElement.GetProperty("prices").Deserialize<Dictionary<string, double>>(options)!;
        var traders = doc.RootElement.GetProperty("traders").Deserialize<TraderBase[]>(options)!;
        var resale = new CargoTraderResale(templates.GetValueOrDefault, prices, traders);
        var loader = new JsonRewardPackLoader();
        var core = loader.LoadFile(Path.Combine(packRoot, "core.json"));
        var optional = Directory.GetFiles(packRoot, "*.json")
            .Where(path => Path.GetFileName(path) != "core.json").Select(loader.LoadFile).ToArray();
        var catalog = new CargoCatalogSnapshotBuilder(
            new CargoLotResolver(new CargoLotResolverDependencies(templates.GetValueOrDefault, presets.GetValueOrDefault)),
            new CargoLotEvaluator(templates.GetValueOrDefault, id => prices.GetValueOrDefault(id), resale.Estimate),
            new CargoPackRequirementValidator(templates.GetValueOrDefault, presets.GetValueOrDefault).Validate).Build(core, optional);
        var selector = new ManifestCatalogSelector();
        catalog = CaseCatalogs.WithPrice(catalog, ManifestCatalogEconomy.CalculateAutomaticPrices(catalog).CasePrice);
        return new
        {
            Scope = "Offline declared clones/overrides/native presets only. No installed mod hooks, live profiles, live flea prices, or gameplay. Resale uses default native trader coefficients; profile bonuses and modded traders are excluded.",
            Report = ManifestEconomyReport.Create(catalog),
            CurrentRelayCoverage = new[] { catalog }.Concat(CaseContracts.Templates.Where(t =>
                    t != catalog.CaseTemplateId && t != CaseContracts.CashCache).Select(t => CaseCatalogs.ForCase(catalog, t)))
                .Select(view => new
                {
                    Name = CaseContracts.Name(view.CaseTemplateId),
                    NonLegendary = view.FreshOpeningLots.Count(l => l.Evaluation.Grade != RewardRarity.BlackLabel),
                    Eligible = view.FreshOpeningLots.Count(l => l.Evaluation.Grade != RewardRarity.BlackLabel &&
                        selector.CreateRelayCandidatesForStage(view, new ManifestEntitlementSnapshot(
                            l.Evaluation.Grade, l.Identity, l.Forest, l.Fingerprint), 1).Count > 0),
                    Tiers = view.FreshOpeningLots.GroupBy(l => l.Evaluation.Grade).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                    Min = view.FreshOpeningLots.Count == 0 ? 0 : view.FreshOpeningLots.Min(l => l.Evaluation.UseValue),
                    Max = view.FreshOpeningLots.Count == 0 ? 0 : view.FreshOpeningLots.Max(l => l.Evaluation.UseValue)
                }).ToArray()
        };
    }
}
