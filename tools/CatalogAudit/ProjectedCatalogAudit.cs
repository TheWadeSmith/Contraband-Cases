using System.Text.Json;
using System.Text.Json.Serialization;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
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
            DecisionWeightedCases = new[] { catalog }.Concat(CaseContracts.Templates.Where(t =>
                    t != catalog.CaseTemplateId && t != CaseContracts.CashCache).Select(t => CaseCatalogs.ForCase(catalog, t)))
                .Where(view => view.OpeningEnabled).Select(DescribeDecisions).ToArray(),
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

    internal static object DescribeDecisions(CargoCatalogSnapshot catalog)
    {
        var price = ManifestCatalogEconomy.CalculateAutomaticPrices(catalog).CasePrice;
        var analysis = new ManifestEconomyAnalysis(catalog);
        var selector = new ManifestCatalogSelector();
        int Wagers(ResolvedCargoLot lot, int stage)
        {
            if (stage > RelayRules.MaximumStage || lot.Evaluation.Grade == RewardRarity.BlackLabel) return 0;
            var candidates = selector.CreateRelayCandidatesForStage(catalog,
                new ManifestEntitlementSnapshot(lot.Evaluation.Grade, lot.Identity, lot.Forest, lot.Fingerprint), stage);
            if (candidates.Count == 0) return 0;
            return 1 + candidates.Where(c => c.TargetResult == ManifestRelayResult.Upgrade)
                .Select(c => Wagers(catalog.ResolveExact(c.Rarity, c.Identity, c.Forest, c.Fingerprint)!, stage + 1))
                .DefaultIfEmpty(0).Max();
        }
        decimal Percent(IEnumerable<OpeningClaimProbability> claims) => decimal.Round(claims.Sum(c => c.Probability) * 100m, 4);
        return new
        {
            Name = CaseContracts.Name(catalog.CaseTemplateId), CasePrice = price,
            ReachableWagers = catalog.FreshOpeningLots.GroupBy(l => Wagers(l, 1))
                .ToDictionary(g => g.Key, g => g.Count()),
            PremiumEpicAvailable = ManifestPremiumPool.IsAvailable(CaseCatalogs.WithPrice(catalog, price), ManifestOpeningTier.Epic),
            PremiumLegendaryAvailable = ManifestPremiumPool.IsAvailable(CaseCatalogs.WithPrice(catalog, price), ManifestOpeningTier.Legendary),
            Strategies = Enum.GetValues<OpeningChoicePolicy>().Select(policy =>
            {
                var claims = analysis.OpeningClaims(price, policy);
                var known = claims.Where(c => c.Lot.Evaluation.TraderResaleEstimate is not null).ToArray();
                var unknown = claims.Where(c => c.Lot.Evaluation.TraderResaleEstimate is null).ToArray();
                return new
                {
                    Policy = policy.ToString(),
                    ProbabilitySum = claims.Sum(c => c.Probability),
                    RarityPercent = claims.GroupBy(c => c.Lot.Evaluation.Grade)
                        .ToDictionary(g => RewardRarities.GetInfo(g.Key).DisplayName, g => Percent(g)),
                    RepeatedRoots = claims.SelectMany(c => c.Lot.Forest.Roots.Select(r => r.TemplateId).Distinct()
                            .Select(id => new { TemplateId = id, c.Probability }))
                        .GroupBy(r => r.TemplateId).Select(g => new { TemplateId = g.Key, ClaimPercent = decimal.Round(g.Sum(c => c.Probability) * 100m, 4) })
                        .OrderByDescending(r => r.ClaimPercent).Take(15).ToArray(),
                    ResaleUnknownPercent = Percent(unknown),
                    // A contribution, not the whole payout EV: unknown resale is never reported as zero.
                    KnownResaleContribution = decimal.Round(known.Sum(c => c.Probability * c.Lot.Evaluation.TraderResaleEstimate!.Value), 2),
                    Scenarios = new[] { ("Bought", (decimal)price), ("Found, foregone ordinary Therapist sale", price * 0.63m) }
                        .SelectMany(origin => ManifestCatalogEconomy.KeyOpportunityCostScenarios.Select(key =>
                        {
                            var cost = origin.Item2 + key;
                            return new
                            {
                                Origin = origin.Item1, KeyOpportunityCost = key, TotalEconomicCost = cost,
                                ReferenceLossPercent = Percent(claims.Where(c => c.Lot.Evaluation.UseValue < cost * 0.9m)),
                                ReferenceNearEvenPercent = Percent(claims.Where(c => c.Lot.Evaluation.UseValue >= cost * 0.9m && c.Lot.Evaluation.UseValue <= cost * 1.1m)),
                                ReferenceWinPercent = Percent(claims.Where(c => c.Lot.Evaluation.UseValue > cost * 1.1m)),
                                KnownResaleLossPercent = Percent(known.Where(c => c.Lot.Evaluation.TraderResaleEstimate < cost * 0.9m)),
                                KnownResaleNearEvenPercent = Percent(known.Where(c => c.Lot.Evaluation.TraderResaleEstimate >= cost * 0.9m && c.Lot.Evaluation.TraderResaleEstimate <= cost * 1.1m)),
                                KnownResaleWinPercent = Percent(known.Where(c => c.Lot.Evaluation.TraderResaleEstimate > cost * 1.1m))
                            };
                        })).ToArray()
                };
            }).ToArray()
        };
    }
}
