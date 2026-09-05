using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;

namespace ContrabandCases.Server.Catalog;

/// <summary>Diagnostic calculations over exactly the catalog used for settlement.</summary>
public static class ManifestEconomyReport
{
    public static object Create(CargoCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var lots = catalog.Lots.ToArray();
        var selector = new ManifestCatalogSelector();
        var rows = lots.Select(lot =>
        {
            var entitlement = new ManifestEntitlementSnapshot(
                lot.Evaluation.Grade, lot.Identity, lot.Forest, lot.Fingerprint);
            decimal Mean(ManifestRelayCandidateSnapshot[] pool)
            {
                if (pool.Length == 0) return 0;
                var weights = ManifestSelectionMath.CreateExactWeights(pool, c => c.Identity.Weight);
                return pool.Select((c, index) => weights.ProbabilityAt(index).ApproximateDecimal *
                    catalog.ResolveExact(c.Rarity, c.Identity, c.Forest, c.Fingerprint)!.Evaluation.UseValue).Sum();
            }
            var eligible = selector.CreateRelayCandidatesForStage(catalog, entitlement, 1).Count > 0;
            return new
            {
                lot.Identity.ProviderId,
                lot.Identity.LotId,
                lot.Identity.DisplayName,
                Category = CargoFamilies.SelectionFamily(lot.Identity.FamilyId).Value,
                Theme = lot.Identity.FamilyId.Value,
                Track = lot.Identity.TrackId.Value,
                Grade = RewardRarities.GetInfo(lot.Evaluation.Grade).DisplayName,
                lot.Evaluation.HandbookValue,
                lot.Evaluation.UseValue,
                OpeningOnlyChase = ManifestOpeningPool.IsChase(lot),
                AvailableForFreshOpening = ManifestOpeningPool.IsFreshEligible(lot),
                RelayEligible = eligible,
                Relay = Enumerable.Range(1, RelayRules.MaximumStage).SelectMany(stage =>
                    Enumerable.Range(0, 4).Select(favor =>
                    {
                        var candidates = selector.CreateRelayCandidatesForStage(catalog, entitlement, stage);
                        var upgradeValue = Mean(candidates.Where(c => c.TargetResult == ContrabandCases.Shared.Manifest.ManifestRelayResult.Upgrade).ToArray());
                        var sidegradeValue = Mean(candidates.Where(c => c.TargetResult == ContrabandCases.Shared.Manifest.ManifestRelayResult.Sidegrade).ToArray());
                        var odds = RelayRules.GetOdds(stage);
                        var expected = favor == 3 ? upgradeValue :
                            upgradeValue * odds.UpgradePercent / 100m + sidegradeValue * odds.SidegradePercent / 100m;
                        return new
                        {
                            Stage = stage,
                            Favor = favor,
                            Guaranteed = favor == 3,
                            Eligible = candidates.Count > 0,
                            ExpectedOutputUseValue = decimal.Round(expected, 2),
                            NetBeforeKeyOpportunityCost = decimal.Round(expected - lot.Evaluation.UseValue, 2),
                            NetWith25000KeyOpportunityCost = decimal.Round(expected - lot.Evaluation.UseValue - 25000, 2),
                            NetWith65000KeyOpportunityCost = decimal.Round(expected - lot.Evaluation.UseValue - 65000, 2),
                            FavorAfterLoss = Math.Min(3, favor + 1)
                        };
                    })).ToArray()
            };
        }).ToArray();
        var nonLegendary = rows.Count(row => row.Grade != "Legendary");
        var analysis = catalog.OpeningEnabled ? new ManifestEconomyAnalysis(catalog) : null;
        var automaticPrice = analysis is null ? (long?)null : ManifestCatalogEconomy.CalculateAutomaticPrices(catalog).CasePrice;
        return new
        {
            catalog.SnapshotId,
            SelectionCategories = CargoFamilies.SelectionVersion,
            LotCount = lots.Length,
            FreshOpeningLotCount = catalog.FreshOpeningLots.Count,
            ProviderCount = catalog.ProviderWeights.Count,
            catalog.OpeningEnabled,
            ExpectedSingleOfferHandbookValue = ManifestCatalogEconomy.CalculateExpectedHandbookValue(catalog),
            OptimalKeepDiscardUseValue = analysis?.OptimalKeepDiscardUseValue(),
            SuggestedAutomaticCasePrice = automaticPrice,
            OpeningChoiceAtAutomaticPrice = automaticPrice is long price ? analysis!.SummarizeOpening(price) : null,
            OpeningPlayerStrategies = automaticPrice is long strategyPrice
                ? Enum.GetValues<OpeningChoicePolicy>().Select(policy => new
                {
                    Policy = policy.ToString(),
                    Outcomes = analysis!.SummarizeOpening(strategyPrice, policy)
                }).ToArray() : [],
            OpeningChoiceCaveat = "Before Relay/Favor. Automatic price is 95% of the lesser of optimal mean/median reference value minus a 25,000 RUB key allowance, rounded up to 1,000 RUB with a 1,000 minimum. The cost-threshold strategy keeps the first offer worth case price plus 25,000; first-offer takes no discard advantage. Loss/near-even/win bands are +/-10% diagnostics, never quotas. Values are not trader cash. Mixed uses automatic price, not a fixed override. Key costs are scenarios, not shop prices. Claimed chase odds depend on policy.",
            OptimalKeepDiscardAndRelayScenarios = analysis is null ? [] :
                new[] { 0m, 25_000m, 65_000m }.SelectMany(cost =>
                    new[] { 1, 20 }.SelectMany(count => analysis.Analyze(count, cost))).ToArray(),
            RelayEligibleNonLegendaryPercent = nonLegendary == 0 ? 0 : 100m * rows.Count(row => row.RelayEligible) / nonLegendary,
            Caveat = "Reference values, not trader cash payouts. Per-lot Relay rows are one-step comparisons. Optimal scenarios use risk-neutral keep/discard and Relay decisions, visible category clues, persistent Favor, every consumed key, and a finite horizon with no salvage value for unused Favor. Break-even case price is a reference-value ceiling, not a recommended sale price or guaranteed profit. Keys are find-only; costs are scenarios. Diagnostic reporting never changes exact selection odds or saved entitlements.",
            Families = lots.GroupBy(lot => CargoFamilies.SelectionFamily(lot.Identity.FamilyId)).Select(group => new
            {
                Id = group.Key.Value,
                LotCount = group.Count(),
                Rarities = group.GroupBy(lot => RewardRarities.GetInfo(lot.Evaluation.Grade).DisplayName)
                    .ToDictionary(g => g.Key, g => g.Count())
            }).ToArray(),
            OpeningOdds = catalog.OpeningEnabled ? ManifestOpeningOdds.Create(catalog) : null,
            Lots = rows,
            SkippedPacks = catalog.SkippedPacks,
            ThemedCases = CaseContracts.Templates.Where(t => catalog.CaseTemplateId == ModConstants.CaseTemplateId &&
                t != ModConstants.CaseTemplateId && t != CaseContracts.CashCache).Select(t =>
            {
                var view = CaseCatalogs.ForCase(catalog, t);
                var eligible = view.Lots.Count(lot => lot.Evaluation.Grade != RewardRarity.BlackLabel &&
                    selector.CreateRelayCandidatesForStage(view, new ManifestEntitlementSnapshot(
                        lot.Evaluation.Grade, lot.Identity, lot.Forest, lot.Fingerprint), 1).Count > 0);
                var nonLegendaryCount = view.Lots.Count(lot => lot.Evaluation.Grade != RewardRarity.BlackLabel);
                return new
                {
                    view.CaseTemplateId,
                    Name = CaseContracts.Name(t),
                    view.SnapshotId,
                    view.CasePrice,
                    view.OpeningEnabled,
                    view.OpeningDisabledReason,
                    OpeningChoiceAtCasePrice = view.OpeningEnabled
                        ? new ManifestEconomyAnalysis(view).SummarizeOpening(view.CasePrice!.Value) : null,
                    LotCount = view.Lots.Count,
                    FreshOpeningLotCount = view.FreshOpeningLots.Count,
                    OpeningPlayerStrategies = view.OpeningEnabled
                        ? Enum.GetValues<OpeningChoicePolicy>().Select(policy => new
                        {
                            Policy = policy.ToString(),
                            Outcomes = new ManifestEconomyAnalysis(view).SummarizeOpening(view.CasePrice!.Value, policy)
                        }).ToArray() : [],
                    Groups = view.FreshOpeningLots.GroupBy(view.SelectionFamily).Select(group => new
                    {
                        Id = group.Key.Value,
                        LotCount = group.Count()
                    }).ToArray(),
                    RelayEligibleNonLegendaryPercent = nonLegendaryCount == 0 ? 0 : 100m * eligible / nonLegendaryCount,
                    OpeningOdds = view.OpeningEnabled ? ManifestOpeningOdds.Create(view) : null
                };
            }).ToArray()
        };
    }
}
