using System.Text.Json;
using ContrabandCases.Client.Opening;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Server.Settlement;

internal static class CapturedOpeningAudit
{
    internal static object Create(string reportPath, string packRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
        var report = document.RootElement;
        var loader = new JsonRewardPackLoader();
        var packs = Directory.EnumerateFiles(packRoot, "*.json").Select(loader.LoadFile).ToArray();
        var definitions = packs.SelectMany(p => p.Lots).ToDictionary(d => (d.ProviderId, d.LotId));
        var captured = report.GetProperty("Lots").EnumerateArray().ToDictionary(
            row => (row.GetProperty("ProviderId").GetString()!, row.GetProperty("LotId").GetString()!));
        var capturedProviders = captured.Keys.Select(key => key.Item1).ToHashSet(StringComparer.Ordinal);
        static string LineKey(RewardRecipeLine line) => line switch
        {
            TemplateLine t => $"template:{t.TemplateId}:{t.InstanceCount}:{t.StackCountPerInstance}",
            PresetLine p => $"preset:{p.PresetId}",
            _ => throw new InvalidOperationException("Unsupported captured recipe line.")
        };
        (long Handbook, long Use) Value(CargoLotDefinition definition)
        {
            if (captured.TryGetValue((definition.ProviderId, definition.LotId), out var row))
                return (row.GetProperty("HandbookValue").GetInt64(), row.GetProperty("UseValue").GetInt64());
            // New additive caches are exact concatenations of old recipes. A
            // captured sum can project their value, but never validate topology.
            var recipe = definition.RecipeLines.Select(LineKey).ToArray();
            var bases = captured.Where(pair => pair.Key.Item1 == definition.ProviderId)
                .Select(pair => (Lines: definitions[pair.Key].RecipeLines.Select(LineKey).ToArray(),
                    Handbook: pair.Value.GetProperty("HandbookValue").GetInt64(),
                    Use: pair.Value.GetProperty("UseValue").GetInt64())).ToArray();
            var memo = new Dictionary<int, (long Handbook, long Use)?>();
            (long Handbook, long Use)? Sum(int index)
            {
                if (index == recipe.Length) return (0, 0);
                if (memo.TryGetValue(index, out var found)) return found;
                var values = new HashSet<(long Handbook, long Use)>();
                foreach (var basis in bases)
                    if (recipe.Skip(index).Take(basis.Lines.Length).SequenceEqual(basis.Lines) &&
                        Sum(index + basis.Lines.Length) is { } rest)
                        values.Add((checked(basis.Handbook + rest.Handbook), checked(basis.Use + rest.Use)));
                if (values.Count > 1) throw new InvalidOperationException($"Ambiguous captured value: {definition.LotId}");
                return memo[index] = values.Count == 1 ? values.Single() : null;
            }
            return Sum(0) ?? throw new InvalidOperationException(
                $"No captured or exact additive value for {definition.ProviderId}/{definition.LotId}; run a live audit.");
        }
        var lots = definitions.Values.Where(d => capturedProviders.Contains(d.ProviderId)).Select(definition =>
        {
            // Opening weights/grouping/prices depend on identity and captured
            // values, not node topology. This synthetic forest is NEVER used
            // for settlement, item validation or Relay-capacity verification.
            var forest = RewardForest.Create([new RewardForestNode("root", "root", definition.AnchorTemplateId,
                null, null, null, 1)]);
            var fingerprint = RewardForestFingerprintV2.Compute(definition.ProviderId, definition.LotId, forest);
            var value = Value(definition);
            return new ResolvedCargoLot(definition, forest, fingerprint,
                CargoLotIdentitySnapshot.Capture(definition, fingerprint),
                new CargoLotEvaluation(value.Handbook, value.Use, 1, CargoGradeBands.Assign(value.Use)));
        }).ToArray();
        var providers = lots.Select(l => l.Identity.ProviderId).ToHashSet(StringComparer.Ordinal);
        var snapshot = new CargoCatalogSnapshot(new string('c', 64), lots, [],
            packs.Where(p => providers.Contains(p.ProviderId)).ToDictionary(p => p.ProviderId, p => p.ProviderWeight));
        return new
        {
            Scope = "Opening-only projection using captured finalized modded values; new additive caches use exact sums of captured recipe components. Not a live catalog freeze, resale quote, item-validation pass, or Relay-capacity proof. No game profiles accessed.",
            Source = Path.GetFullPath(reportPath),
            Cases = CaseContracts.Templates.Where(template => template != CaseContracts.CashCache).Select(template =>
            {
                var view = CaseCatalogs.ForCase(snapshot, template);
                var price = ManifestCatalogEconomy.CalculateAutomaticPrices(view).CasePrice;
                view = CaseCatalogs.WithPrice(view, price);
                var odds = ManifestOpeningOdds.Create(view);
                // Exercise the actual client contract across the captured mod mix,
                // including premium distributions spanning multiple families.
                var parsed = ManifestSnapshotParser.ParseCurrent(JsonSerializer.Serialize(new
                {
                    err = 0, errmsg = (string?)null,
                    data = ManifestCurrentStateEnvelope.FromOpeningOdds(odds)
                }));
                if (parsed.OpeningOdds?.CatalogSnapshotId != view.SnapshotId)
                    throw new InvalidOperationException("Captured opening odds did not survive the client contract.");
                return new
                {
                    Name = CaseContracts.ShortName(template),
                    Price = price,
                    LotCount = view.FreshOpeningLots.Count,
                    ClientOddsContractValidated = true,
                    PremiumProjection = new[] { ManifestOpeningTier.Epic, ManifestOpeningTier.Legendary }.Select(tier =>
                    {
                        var available = ManifestPremiumPool.IsAvailable(view, tier);
                        const int sampleCount = 5_000;
                        var rng = new Random(451 + (int)tier);
                        var selector = new ManifestCatalogSelector(() => rng.NextInt64(CanonicalRngEvidence.UnitDenominator));
                        var samples = available ? Enumerable.Range(0, sampleCount).Select(_ =>
                            selector.CreatePremiumOffers(view, tier).Max(offer => view.ResolveExact(offer.Rarity,
                                offer.Identity, offer.Forest, offer.Fingerprint)!.Evaluation.UseValue)).ToArray() : [];
                        return new { Tier = tier.ToString(), Available = available,
                            Candidates = ManifestPremiumPool.Candidates(view, tier).Count,
                            MinimumReferenceValue = ManifestPremiumPool.MinimumUseValue(view, tier),
                            Sampling = "Seeded Monte Carlo; choose highest reference value; NOT actual resale or live item validation",
                            Samples = samples.Length,
                            MeanBestReferenceValue = samples.Length == 0 ? (decimal?)null : samples.Average(v => (decimal)v),
                            LossAgainstCasePlus25kPercent = samples.Length == 0 ? (decimal?)null
                                : 100m * samples.Count(value => value < price + 25_000) / samples.Length };
                    }).ToArray(),
                    Outcomes = new ManifestEconomyAnalysis(view).SummarizeOpening(price),
                    PlayerStrategies = Enum.GetValues<OpeningChoicePolicy>().Select(policy => new
                    {
                        Policy = policy.ToString(),
                        Outcomes = new ManifestEconomyAnalysis(view).SummarizeOpening(price, policy)
                    }).ToArray(),
                    Odds = odds
                };
            }).ToArray()
        };
    }
}
