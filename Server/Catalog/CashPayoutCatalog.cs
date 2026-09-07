using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace ContrabandCases.Server.Catalog;

/// <summary>A closed, versioned single-draw payout table, separate from cargo recipes.</summary>
internal static class CashPayoutCatalog
{
    internal const string Version = "cash-v1";
    internal const string SelectionVersion = "cash-opening-v2";
    internal sealed record Payout(string Id, string Template, int Amount, int Weight, RewardRarity Grade);

    internal static IReadOnlyList<Payout> Payouts { get; } = Array.AsReadOnly(new[]
    {
        new Payout("rub-25000", CashPayouts.Roubles, 25_000, 1500, RewardRarity.ScavGrade),
        new Payout("rub-60000", CashPayouts.Roubles, 60_000, 1600, RewardRarity.Uncommon),
        new Payout("rub-100000", CashPayouts.Roubles, 100_000, 1000, RewardRarity.Contractor),
        new Payout("rub-150000", CashPayouts.Roubles, 150_000, 1700, RewardRarity.Contractor),
        new Payout("rub-175000", CashPayouts.Roubles, 175_000, 1000, RewardRarity.Restricted),
        new Payout("rub-220000", CashPayouts.Roubles, 220_000, 1000, RewardRarity.Restricted),
        new Payout("rub-300000", CashPayouts.Roubles, 300_000, 300, RewardRarity.Restricted),
        new Payout("usd-1000", CashPayouts.Dollars, 1_000, 700, RewardRarity.Contractor),
        new Payout("usd-1500", CashPayouts.Dollars, 1_500, 600, RewardRarity.Restricted),
        new Payout("eur-1000", CashPayouts.Euros, 1_000, 400, RewardRarity.Contractor),
        new Payout("eur-2000", CashPayouts.Euros, 2_000, 100, RewardRarity.Restricted),
        new Payout("gp-10", CashPayouts.GpCoin, 10, 300, RewardRarity.Uncommon),
        new Payout("gp-25", CashPayouts.GpCoin, 25, 100, RewardRarity.Restricted),
        new Payout("btc-1", CashPayouts.Bitcoin, 1, 90, RewardRarity.Restricted),
        new Payout("btc-2", CashPayouts.Bitcoin, 2, 10, RewardRarity.BlackLabel)
    });

    // Original weights are part of immutable cash-v1 claim identities. Reweight
    // new draws here, shared by selection, displayed odds, pricing and reports.
    internal static int OpeningWeight(string lotId) => lotId switch
    {
        "rub-60000" => 1300,
        "rub-175000" => 900,
        _ => Payouts.Single(payout => payout.Id == lotId).Weight
    };

    internal static CargoCatalogSnapshot Build(Func<string, TemplateItem?> findTemplate,
        decimal dollarPurchaseRate, decimal euroPurchaseRate, decimal bitcoinSaleValue, decimal gpReferenceValue)
    {
        var rates = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            [CashPayouts.Roubles] = 1,
            [CashPayouts.Dollars] = dollarPurchaseRate,
            [CashPayouts.Euros] = euroPurchaseRate,
            [CashPayouts.Bitcoin] = bitcoinSaleValue,
            [CashPayouts.GpCoin] = gpReferenceValue
        };
        if (rates.Values.Any(value => value <= 0 || value > 100_000_000m))
            throw new CargoCatalogValidationException("Cash Cache requires valid finalized currency and Bitcoin quotes.");
        var ordinary = Payouts.Where(p => p.Template != CashPayouts.Bitcoin).ToArray();
        var ordinaryMean = ordinary.Sum(p => OpeningWeight(p.Id) * p.Amount * rates[p.Template]) / ordinary.Sum(p => OpeningWeight(p.Id));
        // Raid-earned keys buy access to a small expected premium over the cash
        // entry price, not an unlimited buy/open loop. A modded Bitcoin outlier
        // disables new cash openings instead of making ordinary entry expensive.
        var bitcoinContribution = Payouts.Where(p => p.Template == CashPayouts.Bitcoin)
            .Sum(p => OpeningWeight(p.Id) * p.Amount * bitcoinSaleValue) / Payouts.Sum(p => OpeningWeight(p.Id));
        if (bitcoinContribution > ordinaryMean * 0.10m)
            throw new CargoCatalogValidationException("Bitcoin exceeds the Cash Cache jackpot budget; cash openings are disabled.");
        var price = checked((long)(decimal.Ceiling(ordinaryMean / 1000m) * 1000m));
        if (price is < 1000 or > 1_000_000)
            throw new CargoCatalogValidationException("Cash Cache entry price exceeds its supported economy range.");
        var lots = Payouts.Select(payout => CreateLot(payout, findTemplate, rates[payout.Template])).ToArray();
        var identity = string.Join("\n", Version, SelectionVersion, price.ToString(CultureInfo.InvariantCulture),
            string.Join("\n", lots.Select(lot => $"{lot.Identity.LotId}:{lot.Fingerprint.Sha256Hex}:{lot.Evaluation.UseValue}:{OpeningWeight(lot.Identity.LotId)}")));
        return new CargoCatalogSnapshot(Hash(identity), lots, [],
            new Dictionary<string, double> { [CashPayouts.Provider] = 1 }, CaseContracts.CashCache, price);
    }

    internal static RewardRarity Grade(string lotId) => Payouts.Single(p => p.Id == lotId).Grade;

    internal static ResolvedCargoLot? ResolveSavedForest(ResolvedCargoLot current, RewardForest saved)
    {
        try { ValidateShape(saved); }
        catch (CargoCatalogValidationException) { return null; }
        if (saved.Nodes[0].TemplateId != current.Identity.AnchorTemplateId ||
            saved.Nodes.Sum(n => (long)n.StackCount) != current.Forest.Nodes.Sum(n => (long)n.StackCount) ||
            saved.Nodes.Any(n => n.StackCount > current.Forest.Nodes.Max(node => node.StackCount))) return null;
        // A native stack-cap increase must not rewrite or strand valid smaller
        // stacks already committed. A rebuilt payout provides a conservative
        // current stack bound; all metadata and the saved fingerprint are still
        // compared by ResolveExact, and materialization revalidates native rules.
        var fingerprint = RewardForestFingerprintV2.Compute(CashPayouts.Provider, current.Identity.LotId, saved);
        var evaluation = current.EvaluationOrNull;
        if (evaluation is not null)
        {
            var cells = evaluation.FootprintCells / current.Forest.Nodes.Count * saved.Nodes.Count;
            // The opening footprint budget does not cancel an already-paid
            // forest. Native placement decides whether the original stacks fit.
            evaluation = new CargoLotEvaluation(evaluation.HandbookValue, evaluation.UseValue, cells, evaluation.Grade);
        }
        return new ResolvedCargoLot(current.Definition, saved, fingerprint,
            CargoLotIdentitySnapshot.Capture(current.Definition, fingerprint), evaluation);
    }

    internal static CargoCatalogSnapshot Disabled(string reason, Func<string, TemplateItem?>? findTemplate = null)
    {
        // Pricing gates only new purchases. Valid authored forests must remain
        // resolvable for already-paid claims, without fabricating a current quote.
        var recoverable = new List<ResolvedCargoLot>();
        var skipped = new List<SkippedCargoLotPack> { new(CashPayouts.Provider, Version, reason) };
        if (findTemplate is not null)
            foreach (var payout in Payouts)
            {
                try { recoverable.Add(CreateLot(payout, findTemplate, null)); }
                catch (CargoCatalogValidationException exception)
                {
                    skipped.Add(new SkippedCargoLotPack(CashPayouts.Provider, Version, payout.Id + ": " + exception.Message));
                }
            }
        return new CargoCatalogSnapshot(Hash(Version + "\n" + reason), recoverable, skipped,
            new Dictionary<string, double>(), CaseContracts.CashCache);
    }

    internal static object Report(CargoCatalogSnapshot catalog)
    {
        var pricedLots = catalog.FreshOpeningLots;
        var weight = pricedLots.Sum(lot => (decimal)OpeningWeight(lot.Identity.LotId));
        return new
        {
            Scope = "Single-payout cash table. USD/EUR purchase estimates, GP handbook barter references and standard Therapist Bitcoin sale estimates are not guaranteed player liquidation proceeds. No individual outcomes are adjusted.",
            SelectionVersion,
            catalog.SnapshotId,
            catalog.OpeningEnabled,
            catalog.OpeningDisabledReason,
            catalog.CasePrice,
            ExpectedReferencePayout = weight == 0 ? (decimal?)null : pricedLots.Sum(lot => (decimal)OpeningWeight(lot.Identity.LotId) * lot.Evaluation.UseValue) / weight,
            KeyCostScenarios = (weight == 0 ? Array.Empty<int>() : new[] { 0, 25_000, 65_000 }).Select(keyCost => new
            {
                AssumedKeyOpportunityCost = keyCost,
                TotalReferenceCost = catalog.CasePrice + keyCost,
                MeaningfulLossPercent = pricedLots.Where(lot => lot.Evaluation.UseValue < (catalog.CasePrice + keyCost) * 0.9m).Sum(lot => (decimal)OpeningWeight(lot.Identity.LotId)) / weight * 100,
                NearEvenPercent = pricedLots.Where(lot => lot.Evaluation.UseValue >= (catalog.CasePrice + keyCost) * 0.9m && lot.Evaluation.UseValue <= (catalog.CasePrice + keyCost) * 1.1m).Sum(lot => (decimal)OpeningWeight(lot.Identity.LotId)) / weight * 100,
                WinPercent = pricedLots.Where(lot => lot.Evaluation.UseValue > (catalog.CasePrice + keyCost) * 1.1m).Sum(lot => (decimal)OpeningWeight(lot.Identity.LotId)) / weight * 100
            }).ToArray(),
            Odds = catalog.OpeningEnabled ? ManifestOpeningOdds.Create(catalog) : null,
            Payouts = catalog.Lots.Select(lot => new
            {
                lot.Identity.LotId,
                lot.Identity.DisplayName,
                lot.Identity.AnchorTemplateId,
                Quantity = lot.Forest.Nodes.Sum(node => node.StackCount),
                UseValue = lot.EvaluationOrNull?.UseValue,
                ValueBasis = CashPayouts.ValueLabel(lot.Identity.AnchorTemplateId),
                StackCount = lot.Forest.Nodes.Count,
                lot.Fingerprint.Sha256Hex
            }).ToArray()
        };
    }

    private static ResolvedCargoLot CreateLot(Payout payout, Func<string, TemplateItem?> findTemplate, decimal? rate)
    {
        var template = findTemplate(payout.Template)
            ?? throw new CargoCatalogValidationException("A Cash Cache currency template is missing.");
        var maximum = template.Properties?.StackMaxSize;
        if (maximum is null or < 1 or > int.MaxValue)
            throw new CargoCatalogValidationException("Cash Cache currency stack bounds are invalid.");
        var width = template.Properties?.Width;
        var height = template.Properties?.Height;
        if (width is null or < 1 or > 16 || height is null or < 1 or > 16)
            throw new CargoCatalogValidationException("Cash Cache currency dimensions are invalid.");
        var stackLimit = checked((int)maximum.Value);
        var nodes = new List<RewardForestNode>();
        for (var remaining = payout.Amount; remaining > 0;)
        {
            if (nodes.Count >= 32) throw new CargoCatalogValidationException("Cash payout requires too many item stacks.");
            var path = "cash-" + nodes.Count.ToString(CultureInfo.InvariantCulture);
            var quantity = Math.Min(remaining, stackLimit);
            nodes.Add(new RewardForestNode(path, path, payout.Template, null, null, null, quantity));
            remaining -= quantity;
        }
        var forest = RewardForest.Create(nodes);
        new CargoLotMaterializer(findTemplate).ValidateCashPayout(forest);
        var name = payout.Template switch
        {
            CashPayouts.Roubles => $"₽{payout.Amount.ToString("N0", CultureInfo.InvariantCulture)}",
            CashPayouts.Dollars => $"${payout.Amount.ToString("N0", CultureInfo.InvariantCulture)} US dollars",
            CashPayouts.Euros => $"€{payout.Amount.ToString("N0", CultureInfo.InvariantCulture)} euros",
            CashPayouts.GpCoin => $"{payout.Amount} × GP Coins",
            _ => $"{payout.Amount} × Physical Bitcoin"
        };
        var definition = new CargoLotDefinition(CashPayouts.Provider, Version, payout.Id, name,
            "Cash payout. " + CashPayouts.ValueLabel(payout.Template), new FamilyId("cash"), new TrackId("cash"),
            payout.Template, payout.Weight, new RaidRole("currency"),
            nodes.Select(node => (RewardRecipeLine)new TemplateLine(node.TemplateId, 1, node.StackCount)));
        var fingerprint = RewardForestFingerprintV2.Compute(CashPayouts.Provider, payout.Id, forest);
        var value = rate.HasValue ? checked((long)decimal.Floor(payout.Amount * rate.Value)) : (long?)null;
        if (value is < 1) throw new CargoCatalogValidationException("Cash payout has no positive valuation.");
        var cells = checked((int)(nodes.Count * width.Value * height.Value));
        if (cells > CargoLotEvaluator.MaximumFootprintCells)
            throw new CargoCatalogValidationException("Cash payout exceeds the supported stash footprint.");
        return new ResolvedCargoLot(definition, forest, fingerprint,
            CargoLotIdentitySnapshot.Capture(definition, fingerprint),
            value.HasValue ? new CargoLotEvaluation(value.Value, value.Value, cells, payout.Grade) : null);
    }

    internal static void ValidateShape(RewardForest forest)
    {
        if (forest.Nodes.Count is < 1 or > 32 || forest.Nodes.Any(node =>
                !CashPayouts.IsAllowed(node.TemplateId) || node.ParentLogicalPath is not null ||
                node.SlotId is not null || node.InternalLocation is not null || node.StableState is not null) ||
            forest.Nodes.Select(node => node.TemplateId).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new CargoCatalogValidationException("A cash payout must contain only plain stacks of one allowed currency.");
        var amount = forest.Nodes.Sum(node => (long)node.StackCount);
        if (!Payouts.Any(p => p.Template == forest.Nodes[0].TemplateId && p.Amount == amount))
            throw new CargoCatalogValidationException("The cash payout quantity is not in the published table.");
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
