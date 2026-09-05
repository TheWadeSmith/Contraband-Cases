using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;

namespace ContrabandCases.Server.Catalog;

/// <summary>
/// Finite-horizon reference-value analysis, not a payout promise. Uses the actual
/// category/provider/lot distribution, visible next-category clues, and bounded
/// Relay pools. The player may stop after any offer or eligible upgrade.
/// </summary>
public sealed class ManifestEconomyAnalysis
{
    private readonly ResolvedCargoLot[] _lots;
    private readonly Family[] _families;
    private readonly Candidate[][][] _upgrades;
    private readonly Candidate[][][] _replacements;

    public ManifestEconomyAnalysis(CargoCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.OpeningEnabled) throw new InvalidOperationException("Analysis requires an opening-enabled catalog.");
        _lots = catalog.Lots.ToArray();
        var odds = ManifestOpeningOdds.Create(catalog);
        _families = odds.Families.Select(family => new Family(
            CargoFamilies.Signal(family.FamilyId),
            family.Lots.Select(row => new Candidate(
                Array.FindIndex(_lots, lot => lot.Identity.ProviderId == row.ProviderId &&
                    lot.Identity.LotId == row.LotId &&
                    catalog.SelectionFamily(lot).Value == family.FamilyId),
                Probability(System.Numerics.BigInteger.Parse(row.ConditionalNumerator),
                    System.Numerics.BigInteger.Parse(row.ConditionalDenominator)))).ToArray())).ToArray();

        var selector = new ManifestCatalogSelector();
        _upgrades = new Candidate[RelayRules.MaximumStage][][];
        _replacements = new Candidate[RelayRules.MaximumStage][][];
        for (var stage = 1; stage <= RelayRules.MaximumStage; stage++)
        {
            _upgrades[stage - 1] = new Candidate[_lots.Length][];
            _replacements[stage - 1] = new Candidate[_lots.Length][];
            for (var index = 0; index < _lots.Length; index++)
            {
                var lot = _lots[index];
                var candidates = selector.CreateRelayCandidatesForStage(catalog,
                    new ManifestEntitlementSnapshot(lot.Evaluation.Grade, lot.Identity, lot.Forest, lot.Fingerprint), stage);
                Candidate[] Pool(ManifestRelayResult result)
                {
                    var pool = candidates.Where(c => c.TargetResult == result).ToArray();
                    if (pool.Length == 0) return [];
                    var weights = ManifestSelectionMath.CreateExactWeights(pool, c => c.Identity.Weight);
                    return pool.Select((c, i) => new Candidate(
                        Array.IndexOf(_lots, catalog.ResolveExact(c.Rarity, c.Identity, c.Forest, c.Fingerprint)!),
                        Probability(weights[i], weights.Total))).ToArray();
                }
                _upgrades[stage - 1][index] = Pool(ManifestRelayResult.Upgrade);
                _replacements[stage - 1][index] = Pool(ManifestRelayResult.Sidegrade);
            }
        }
    }

    public decimal OptimalKeepDiscardUseValue() => OpeningValue(_lots.Select(lot => (decimal)lot.Evaluation.UseValue).ToArray());

    /// <summary>Outcomes of optimal keep/discard play, before any Relay or Favor.</summary>
    public OpeningRewardSummary SummarizeOpening(decimal casePrice,
        OpeningChoicePolicy policy = OpeningChoicePolicy.OptimalReferenceValue)
    {
        if (casePrice <= 0) throw new ArgumentOutOfRangeException(nameof(casePrice));
        var values = _lots.Select(lot => (decimal)lot.Evaluation.UseValue).ToArray();
        var probabilities = new decimal[_lots.Length];
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        var expected = OpeningValue(values, probabilities, policy,
            casePrice + ManifestCatalogEconomy.OpeningKeyAllowance);
        var outcomes = Enumerable.Range(0, _lots.Length).Where(i => probabilities[i] > 0)
            .OrderBy(i => values[i]).ToArray();
        var total = probabilities.Sum();
        long Quantile(decimal fraction)
        {
            decimal cumulative = 0;
            foreach (var i in outcomes)
            {
                cumulative += probabilities[i];
                // Diagnostic decimal arithmetic can straddle an exact boundary
                // such as 1/2. This rounding never participates in selection.
                if (decimal.Round(cumulative / total, 12) >= fraction) return (long)values[i];
            }
            return (long)values[outcomes[^1]];
        }
        decimal Percent(Func<int, bool> predicate) => decimal.Round(100m *
            outcomes.Where(predicate).Sum(i => probabilities[i]) / total, 2);
        return new OpeningRewardSummary(expected, (long)values[outcomes[0]],
            Quantile(0.1m), Quantile(0.5m), Quantile(0.9m), (long)values[outcomes[^1]],
            Percent(i => ManifestOpeningPool.IsChase(_lots[i])),
            Array.AsReadOnly(new[] { 0m, 25_000m, 65_000m }.Select(keyCost =>
                new OpeningCostScenario(keyCost, casePrice + keyCost,
                    decimal.Round(expected - casePrice - keyCost, 2),
                    Percent(i => values[i] < casePrice + keyCost),
                    Percent(i => values[i] < (casePrice + keyCost) * 0.9m),
                    Percent(i => values[i] >= (casePrice + keyCost) * 0.9m && values[i] <= (casePrice + keyCost) * 1.1m),
                    Percent(i => values[i] > (casePrice + keyCost) * 1.1m),
                    Percent(i => values[i] >= (casePrice + keyCost) * 2m))).ToArray()));
    }

    public IReadOnlyList<EconomyScenario> Analyze(int caseCount, decimal keyOpportunityCost)
    {
        if (caseCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(caseCount));
        if (keyOpportunityCost < 0) throw new ArgumentOutOfRangeException(nameof(keyOpportunityCost));
        // No terminal salvage value for unused Favor: its benefit comes only from
        // actual future eligible Relay attempts within the requested horizon.
        var future = new decimal[4];
        var current = new decimal[4];
        for (var run = 0; run < caseCount; run++)
        {
            var memo = new Dictionary<(int Lot, int Stage, int Favor), decimal>();
            decimal StakeValue(int lot, int stage, int favor)
            {
                var keep = _lots[lot].Evaluation.UseValue + future[favor];
                if (stage > RelayRules.MaximumStage || _upgrades[stage - 1][lot].Length == 0 || _replacements[stage - 1][lot].Length == 0)
                    return keep;
                var key = (lot, stage, favor);
                if (memo.TryGetValue(key, out var cached)) return cached;
                var upgradeFavor = favor == 3 ? 0 : favor;
                var upgrade = _upgrades[stage - 1][lot].Sum(c => c.Probability * StakeValue(c.Lot, stage + 1, upgradeFavor));
                var replacement = _replacements[stage - 1][lot].Sum(c => c.Probability *
                    (_lots[c.Lot].Evaluation.UseValue + future[favor]));
                var odds = RelayRules.GetOdds(stage);
                var relay = -keyOpportunityCost + (favor == 3 ? upgrade :
                    (upgrade * odds.UpgradePercent + replacement * odds.SidegradePercent +
                     future[Math.Min(3, favor + 1)] * odds.ConfiscatePercent) / 100m);
                return memo[key] = Math.Max(keep, relay);
            }

            for (var favor = 0; favor <= 3; favor++)
                current[favor] = OpeningValue(Enumerable.Range(0, _lots.Length)
                    .Select(lot => StakeValue(lot, 1, favor)).ToArray()) - keyOpportunityCost;
            future = (decimal[])current.Clone();
        }
        return Enumerable.Range(0, 4).Select(favor => new EconomyScenario(
            caseCount, favor, keyOpportunityCost, decimal.Round(current[favor], 2),
            decimal.Round(current[favor] / caseCount, 2))).ToArray();
    }

    private decimal OpeningValue(decimal[] values, decimal[]? outcomes = null,
        OpeningChoicePolicy policy = OpeningChoicePolicy.OptimalReferenceValue, decimal openingCost = 0)
    {
        bool Keep(decimal value, decimal continuation) => policy switch
        {
            OpeningChoicePolicy.KeepFirst => true,
            OpeningChoicePolicy.KeepAtOpeningCost => value >= openingCost,
            _ => value >= continuation
        };
        decimal Mean(int family, decimal? continuation = null) => _families[family].Lots.Sum(c =>
            c.Probability * (continuation.HasValue && !Keep(values[c.Lot], continuation.Value)
                ? continuation.Value : values[c.Lot]));
        var means = Enumerable.Range(0, _families.Length).Select(i => Mean(i)).ToArray();
        // With only three offers, explicit backward induction is clearer than a
        // general game-state solver. Unknown clues remain grouped: no clairvoyance.
        decimal Second(int first, int second, decimal pathProbability = 0)
        {
            var thirds = Enumerable.Range(0, _families.Length).Where(i => i != first && i != second).ToArray();
            return thirds.GroupBy(i => _families[i].Signal).Sum(group =>
            {
                var groupProbability = (decimal)group.Count() / thirds.Length;
                var continuation = group.Average(i => means[i]);
                if (outcomes is not null && pathProbability > 0)
                {
                    foreach (var candidate in _families[second].Lots)
                    {
                        var probability = pathProbability * groupProbability * candidate.Probability;
                        if (Keep(values[candidate.Lot], continuation)) outcomes[candidate.Lot] += probability;
                        else foreach (var third in group)
                            foreach (var final in _families[third].Lots)
                                outcomes[final.Lot] += probability / group.Count() * final.Probability;
                    }
                }
                return groupProbability * Mean(second, continuation);
            });
        }
        return Enumerable.Range(0, _families.Length).Average(first =>
        {
            var seconds = Enumerable.Range(0, _families.Length).Where(i => i != first).ToArray();
            return seconds.GroupBy(i => _families[i].Signal).Sum(group =>
            {
                var groupProbability = (decimal)group.Count() / seconds.Length;
                var continuation = group.Average(second => Second(first, second));
                if (outcomes is not null)
                {
                    foreach (var candidate in _families[first].Lots)
                    {
                        var probability = groupProbability / _families.Length * candidate.Probability;
                        if (Keep(values[candidate.Lot], continuation)) outcomes[candidate.Lot] += probability;
                        else foreach (var second in group)
                            Second(first, second, probability / group.Count());
                    }
                }
                return groupProbability * Mean(first, continuation);
            });
        });
    }

    // Bounded decimal approximation for diagnostics only; selection and the
    // published odds retain their exact rational arithmetic.
    private static decimal Probability(System.Numerics.BigInteger numerator, System.Numerics.BigInteger denominator)
    {
        return new ExactProbability(numerator, denominator).ApproximateDecimal;
    }

    private sealed record Family(string? Signal, Candidate[] Lots);
    private sealed record Candidate(int Lot, decimal Probability);
}

public enum OpeningChoicePolicy
{
    OptimalReferenceValue,
    KeepFirst,
    KeepAtOpeningCost
}

public sealed record EconomyScenario(int CaseCount, int InitialFavor, decimal KeyOpportunityCost,
    decimal ExpectedTotalUseValueAfterKeysBeforeCasePrices, decimal BreakEvenCasePrice);

public sealed record OpeningRewardSummary(decimal ExpectedUseValue, long MinimumUseValue,
    long P10UseValue, long MedianUseValue, long P90UseValue, long MaximumUseValue,
    decimal ClaimedChasePercent,
    IReadOnlyList<OpeningCostScenario> KeyCostScenarios);

public sealed record OpeningCostScenario(decimal KeyOpportunityCost, decimal TotalCost,
    decimal ExpectedUseValueMinusTotalCost, decimal BelowTotalCostPercent,
    decimal LossBelow90PercentOfCost, decimal NearBreakEvenWithin10Percent,
    decimal WinAbove110PercentOfCost, decimal BigWinAtLeastDoubleCostPercent);
