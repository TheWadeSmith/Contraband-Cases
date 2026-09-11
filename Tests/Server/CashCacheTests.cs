using System.Text.Json;
using ContrabandCases.Client.Opening;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Newtonsoft.Json.Linq;
using Xunit;
using ManifestSnapshotEnvelope = ContrabandCases.Client.Opening.ManifestSnapshotEnvelope;

namespace ContrabandCases.Tests.Server;

public sealed class CashCacheTests
{
    [Theory]
    [InlineData(1, 50)]
    [InlineData(7, 8)]
    [InlineData(100, 1)]
    public void Bitcoin_jackpot_publishes_and_materializes_fifty_coins_at_one_in_a_thousand(
        int stackLimit, int expectedStacks)
    {
        TemplateItem? Templates(string id)
        {
            var template = FindTemplate(id);
            if (id == CashPayouts.Bitcoin) template!.Properties!.StackMaxSize = stackLimit;
            return template;
        }
        var catalog = CashPayoutCatalog.Build(Templates, 130, 150, 500_000, 7_500);
        var jackpot = Assert.Single(catalog.FreshOpeningLots, lot => lot.Evaluation.Grade == RewardRarity.BlackLabel);
        Assert.Equal(50, jackpot.Forest.Nodes.Sum(node => node.StackCount));
        Assert.Equal("50 × Physical Bitcoin", jackpot.Identity.DisplayName);
        Assert.Equal(25_000_000, jackpot.Evaluation.UseValue);
        Assert.Equal(15, catalog.FreshOpeningLots.Count);
        var odds = Assert.Single(ManifestOpeningOdds.Create(catalog).Families).Lots;
        Assert.Equal("0.10%", Assert.Single(odds, row => row.LotId == jackpot.Identity.LotId).ConditionalPercent);
        Assert.Equal("0.90%", Assert.Single(odds, row => row.LotId == "btc-1.shipment-v1").ConditionalPercent);
        var items = new CargoLotMaterializer(Templates).MaterializeCashPayout(jackpot.Forest, []).Items;
        Assert.Equal(expectedStacks, items.Count);
        Assert.Equal(50, items.Sum(item => item.Upd!.StackObjectsCount));
        Assert.Equal(items.Count, items.Select(item => item.Id).Distinct().Count());
        Assert.All(items, item => Assert.InRange(item.Upd!.StackObjectsCount!.Value, 1, stackLimit));
    }

    [Fact]
    public void Previous_ten_bitcoin_jackpot_stays_recoverable_but_is_not_in_new_draws()
    {
        var catalog = Catalog();
        var old = Assert.Single(catalog.Lots, lot => lot.Identity.LotId == "btc-2.shipment-v1");
        Assert.Equal(10, old.Forest.Nodes.Sum(node => node.StackCount));
        Assert.Equal("10 × Physical Bitcoin", old.Identity.DisplayName);
        Assert.Equal("f3f68f03002c5dac129a83bb9456b8864475d225afadef7265b5a426ec310e03", old.Fingerprint.Sha256Hex);
        Assert.DoesNotContain(old, catalog.FreshOpeningLots);
        foreach (var current in new[] { catalog, CashPayoutCatalog.Disabled("No quotes", FindTemplate) })
        {
            var recovered = current.ResolveExact(old.Evaluation.Grade, old.Identity, old.Forest, old.Fingerprint);
            Assert.NotNull(recovered);
            Assert.Equal(10, new CargoLotMaterializer(FindTemplate).MaterializeCashPayout(recovered.Forest, [])
                .Items.Sum(item => item.Upd!.StackObjectsCount));
        }
        var jackpot = Assert.Single(catalog.FreshOpeningLots, lot => lot.Evaluation.Grade == RewardRarity.BlackLabel);
        Assert.Null(catalog.ResolveExact(old.Evaluation.Grade, old.Identity, jackpot.Forest, jackpot.Fingerprint));
    }

    [Fact]
    public void Bitcoin_jackpot_does_not_relax_other_cash_stack_or_quantity_limits()
    {
        RewardForest Coins(string template, int count, int amount) => RewardForest.Create(
            Enumerable.Range(0, count).Select(i => new RewardForestNode($"cash-{i}", $"cash-{i}",
                template, null, null, null, amount)));
        var materializer = new CargoLotMaterializer(FindTemplate);
        materializer.ValidateCashPayout(Coins(CashPayouts.Bitcoin, 50, 1));
        Assert.Throws<CargoCatalogValidationException>(() => materializer.ValidateCashPayout(Coins(CashPayouts.Bitcoin, 51, 1)));
        Assert.Throws<CargoCatalogValidationException>(() => materializer.ValidateCashPayout(Coins(CashPayouts.Bitcoin, 1, 50)));
        Assert.Throws<CargoCatalogValidationException>(() => materializer.ValidateCashPayout(Coins(CashPayouts.GpCoin, 40, 2)));
    }

    [Theory]
    [InlineData("gp-10.shipment-v1", 80, "3.00%")]
    [InlineData("gp-25.shipment-v1", 200, "1.00%")]
    public void GP_payouts_publish_exact_coins_odds_and_barter_value(string id, int quantity, string chance)
    {
        const string gp = "5d235b4d86f7742e017bc88a";
        var catalog = Catalog();
        var lot = Assert.Single(catalog.Lots, lot => lot.Identity.LotId == id);
        Assert.Equal(gp, lot.Identity.AnchorTemplateId);
        Assert.Equal($"{quantity} × GP Coins", lot.Identity.DisplayName);
        Assert.Equal(quantity * 7_500, lot.Evaluation.UseValue);
        var items = new CargoLotMaterializer(FindTemplate).MaterializeCashPayout(lot.Forest, []).Items;
        Assert.Equal(quantity, items.Sum(item => item.Upd!.StackObjectsCount));
        Assert.All(items, item => Assert.InRange(item.Upd!.StackObjectsCount!.Value, 1, 100));
        var odds = Assert.Single(ManifestOpeningOdds.Create(catalog).Families).Lots;
        Assert.Equal(chance, Assert.Single(odds, row => row.LotId == id).ConditionalPercent);
        Assert.Contains("not a rouble cash-out", CashPayouts.ValueLabel(gp));
    }

    [Fact]
    public void Every_pre_GP_paid_cash_claim_keeps_its_original_identity_and_remains_claimable()
    {
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,
            "../../../Fixtures/cash-v1-paid-claims.json"));
        var documents = JsonSerializer.Deserialize<SptCaseJournalDocument[]>(File.ReadAllText(path))!;
        Assert.Equal(13, documents.Length);
        foreach (var current in new[] { Catalog(), CashPayoutCatalog.Disabled("Quotes unavailable", FindTemplate) })
            foreach (var document in documents)
            {
                var active = SptCaseJournal.FromDocument(document).ActiveManifest!;
                var entitlement = active.Entitlement!;
                var recovered = current.ResolveExact(entitlement.Rarity, entitlement.Identity,
                    entitlement.Forest, entitlement.Fingerprint);
                Assert.NotNull(recovered);
                var snapshot = ManifestSnapshotProjection.FromActive(active, current, null);
                Assert.True(snapshot.AvailableActions.CanClaim);
                Assert.False(snapshot.MissingContentBlocked);
                new CargoLotMaterializer(FindTemplate).MaterializeCashPayout(recovered.Forest, []);
            }
    }

    [Fact]
    public void Cash_draw_boundaries_price_and_report_use_the_same_published_GP_distribution()
    {
        var weights = new Dictionary<string, int>
        {
            ["rub-25000"] = 1500, ["rub-60000"] = 1300, ["rub-100000"] = 1000,
            ["rub-150000"] = 1700, ["rub-175000"] = 900, ["rub-220000"] = 1000,
            ["rub-300000"] = 300, ["usd-1000"] = 700, ["usd-1500"] = 600,
            ["eur-1000"] = 400, ["eur-2000"] = 100, ["gp-10"] = 300,
            ["gp-25"] = 100, ["btc-1"] = 90, ["btc-2"] = 10
        }.ToDictionary(pair => pair.Key + ".shipment-v1", pair => pair.Value);
        weights.Remove("btc-2.shipment-v1");
        weights.Add("btc-50.jackpot-v1", 10);
        var catalog = Catalog();
        var odds = Assert.Single(ManifestOpeningOdds.Create(catalog).Families).Lots;
        var start = 0;
        foreach (var lot in catalog.FreshOpeningLots.OrderBy(lot => lot.Identity.LotId, StringComparer.Ordinal))
        {
            var weight = weights[lot.Identity.LotId];
            var published = Assert.Single(odds, row => row.LotId == lot.Identity.LotId);
            Assert.Equal(weight / 10_000m, decimal.Parse(published.ConditionalNumerator) /
                decimal.Parse(published.ConditionalDenominator));
            var first = (long)decimal.Ceiling(start * (decimal)CanonicalRngEvidence.UnitDenominator / 10_000);
            var last = (long)decimal.Ceiling((start + weight) * (decimal)CanonicalRngEvidence.UnitDenominator / 10_000) - 1;
            foreach (var draw in new[] { first, last })
                Assert.Equal(lot.Identity.LotId, Assert.Single(new ManifestCatalogSelector(() => draw)
                    .CreateOffers(catalog)).Identity.LotId);
            start += weight;
        }
        Assert.Equal(10_000, start);
        var ordinary = catalog.FreshOpeningLots.Where(lot => lot.Identity.AnchorTemplateId != CashPayouts.Bitcoin).ToArray();
        var expectedPrice = decimal.Ceiling(ordinary.Sum(lot => weights[lot.Identity.LotId] * (decimal)lot.Evaluation.UseValue) /
            9_900m / 1000m) * 1000m;
        Assert.Equal((long)expectedPrice, catalog.CasePrice);
        using var report = JsonDocument.Parse(JsonSerializer.Serialize(CashPayoutCatalog.Report(catalog)));
        Assert.Equal(catalog.FreshOpeningLots.Sum(lot => weights[lot.Identity.LotId] * (decimal)lot.Evaluation.UseValue) / 10_000m,
            report.RootElement.GetProperty("ExpectedReferencePayout").GetDecimal());
        foreach (var scenario in report.RootElement.GetProperty("KeyCostScenarios").EnumerateArray())
        {
            var cost = scenario.GetProperty("TotalReferenceCost").GetDecimal();
            decimal Share(Func<ResolvedCargoLot, bool> predicate) => catalog.FreshOpeningLots.Where(predicate)
                .Sum(lot => weights[lot.Identity.LotId]) / 100m;
            Assert.Equal(Share(lot => lot.Evaluation.UseValue < cost * 0.9m), scenario.GetProperty("MeaningfulLossPercent").GetDecimal());
            Assert.Equal(Share(lot => lot.Evaluation.UseValue >= cost * 0.9m && lot.Evaluation.UseValue <= cost * 1.1m),
                scenario.GetProperty("NearEvenPercent").GetDecimal());
            Assert.Equal(Share(lot => lot.Evaluation.UseValue > cost * 1.1m), scenario.GetProperty("WinPercent").GetDecimal());
        }
    }

    [Fact]
    public void GP_stacks_follow_native_limits_and_saved_coins_survive_quote_or_cap_increases()
    {
        TemplateItem? SmallStacks(string id)
        {
            var template = FindTemplate(id);
            if (id == CashPayouts.GpCoin) template!.Properties!.StackMaxSize = 7;
            return template;
        }
        var small = CashPayoutCatalog.Build(SmallStacks, 130, 150, 500_000, 7_500);
        var saved = small.Lots.Single(lot => lot.Identity.LotId == "gp-25");
        var materialized = new CargoLotMaterializer(SmallStacks).MaterializeCashPayout(saved.Forest, []);
        Assert.Equal(new double?[] { 7, 7, 7, 4 }, materialized.Items.Select(item => item.Upd!.StackObjectsCount));
        var changed = CashPayoutCatalog.Build(FindTemplate, 130, 150, 500_000, 10_000);
        Assert.NotEqual(Catalog().SnapshotId, changed.SnapshotId);
        Assert.NotNull(changed.ResolveExact(saved.Evaluation.Grade, saved.Identity, saved.Forest, saved.Fingerprint));
        var larger = changed.Lots.Single(lot => lot.Identity.LotId == "gp-25");
        Assert.Null(small.ResolveExact(larger.Evaluation.Grade, larger.Identity, larger.Forest, larger.Fingerprint));
        var invalid = RewardForest.Create([new RewardForestNode("root", "root", CashPayouts.GpCoin, null, null, null, 26)]);
        Assert.Throws<CargoCatalogValidationException>(() => new CargoLotMaterializer(FindTemplate).ValidateCashPayout(invalid));
        var unavailable = CashPayoutCatalog.Disabled("GP missing", id => id == CashPayouts.GpCoin ? null : FindTemplate(id));
        Assert.Equal(27, unavailable.Lots.Count);
        Assert.Empty(unavailable.FreshOpeningLots);
        Assert.Null(unavailable.ResolveExact(saved.Evaluation.Grade, saved.Identity, saved.Forest, saved.Fingerprint));
    }

    [Fact]
    public void Saved_cash_stacks_survive_a_cap_increase_but_not_an_unsafe_cap_decrease()
    {
        TemplateItem? SmallStacks(string id)
        {
            var template = FindTemplate(id);
            if (id == CashPayouts.Roubles) template!.Properties!.StackMaxSize = 100_000;
            return template;
        }
        var small = CashPayoutCatalog.Build(SmallStacks, 130, 150, 500_000, 7_500);
        var saved = small.Lots.Single(l => l.Identity.LotId == "rub-300000");
        Assert.Equal(3, saved.Forest.Nodes.Count);
        foreach (var current in new[] { Catalog(), CashPayoutCatalog.Disabled("FX unavailable", FindTemplate) })
        {
            var recovered = current.ResolveExact(saved.Evaluation.Grade, saved.Identity, saved.Forest, saved.Fingerprint);
            Assert.NotNull(recovered);
            Assert.Same(saved.Forest, recovered.Forest);
            new CargoLotMaterializer(FindTemplate).ValidateCashPayout(recovered.Forest);
        }
        var larger = Catalog().Lots.Single(l => l.Identity.LotId == "rub-300000");
        Assert.Null(small.ResolveExact(larger.Evaluation.Grade, larger.Identity, larger.Forest, larger.Fingerprint));
        var other = Catalog().Lots.Single(l => l.Identity.LotId == "rub-25000");
        Assert.Null(Catalog().ResolveExact(saved.Evaluation.Grade, saved.Identity, other.Forest, other.Fingerprint));
    }

    [Fact]
    public void Real_cash_odds_roundtrip_and_client_rejects_mismatched_rules_or_cardinality()
    {
        JObject Envelope() => JObject.Parse(JsonSerializer.Serialize(new
        {
            err = 0,
            errmsg = (string?)null,
            data = new { openingOdds = ManifestOpeningOdds.Create(Catalog()), snapshot = (object?)null }
        }));
        var parsed = ManifestSnapshotEnvelope.ParseCurrent(Envelope().ToString()).OpeningOdds!;
        Assert.Equal(1, parsed.OfferCount);
        Assert.Equal(CaseContracts.CashCache, parsed.CaseTemplateId);
        Assert.Equal(15, Assert.Single(parsed.Families).Lots.Count);
        foreach (var field in new[] { "offerCount", "familyCount", "selectionRule", "caseTemplateId" })
        {
            var malformed = Envelope();
            malformed["data"]!["openingOdds"]![field] = field switch
            {
                "selectionRule" => new JValue("wrong-rule"),
                "caseTemplateId" => new JValue(ModConstants.CaseTemplateId),
                _ => new JValue(3)
            };
            Assert.Throws<ManifestSnapshotException>(() => ManifestSnapshotEnvelope.ParseCurrent(malformed.ToString()));
        }
    }

    [Fact]
    public void Unpriced_cash_retains_valid_claims_and_is_not_offered_for_new_openings()
    {
        var cash = Catalog();
        var disabled = CashPayoutCatalog.Disabled("FX unavailable", FindTemplate);
        Assert.False(disabled.OpeningEnabled);
        Assert.Null(disabled.CasePrice);
        Assert.Empty(disabled.FreshOpeningLots);
        foreach (var lot in cash.Lots)
            Assert.NotNull(disabled.ResolveExact(lot.Evaluation.Grade, lot.Identity, lot.Forest, lot.Fingerprint));
        Assert.Throws<InvalidOperationException>(() => new ManifestCatalogSelector(() => 0).CreateOffers(disabled));
        var active = Prepare(cash).BeginTicketProfileCommit().ActivateTicket(DateTimeOffset.UnixEpoch.AddMinutes(2));
        var snapshot = ManifestSnapshotEnvelope.Parse(JsonSerializer.Serialize(new
        {
            err = 0,
            errmsg = (string?)null,
            data = new { snapshot = ManifestSnapshotProjection.FromActive(active, disabled, null) }
        }), active.ManifestId);
        Assert.True(snapshot.AvailableActions.CanClaim);
        Assert.Null(snapshot.CurrentLot!.UseValue);
        Assert.Contains("payout is unchanged", ManifestPresentationPolicy.LotDetails(snapshot));
        Assert.NotNull(JsonSerializer.Serialize(CashPayoutCatalog.Report(disabled)));
    }

    [Fact]
    public void A_missing_currency_does_not_block_other_paid_cash_payouts()
    {
        var disabled = CashPayoutCatalog.Disabled("EUR missing", id => id == CashPayouts.Euros ? null : FindTemplate(id));
        var rub = Catalog().Lots.Single(l => l.Identity.LotId == "rub-25000");
        Assert.NotNull(disabled.ResolveExact(rub.Evaluation.Grade, rub.Identity, rub.Forest, rub.Fingerprint));
        Assert.DoesNotContain(disabled.Lots, lot => lot.Identity.AnchorTemplateId == CashPayouts.Euros);
    }

    [Fact]
    public void Cash_gallery_uses_real_payouts_and_never_offers_Relay_or_discard()
    {
        var library = ManifestLibraryProjection.Create(new CaseOpeningJournal(), null, new Dictionary<string, string>(), Catalog());
        var parsed = ManifestSnapshotParser.ParseLibrary(JsonSerializer.Serialize(new { err = 0, errmsg = (string?)null, data = library }));
        Assert.Equal(15, parsed.Lots.Count);
        foreach (var lot in parsed.Lots)
            foreach (var state in Enum.GetValues<GalleryState>())
            {
                var snapshot = ManifestGallery.Create(lot, state, false);
                Assert.Equal(CaseContracts.CashCache, snapshot.CaseTemplateId);
                Assert.Single(snapshot.FamilySeals);
                var delivered = state == GalleryState.Claimed;
                Assert.Equal(!delivered, snapshot.AvailableActions.CanClaim);
                Assert.Equal(delivered, snapshot.DeliveredToMessenger);
                Assert.Equal(delivered ? ManifestPhase.Granted : ManifestPhase.Entitlement, snapshot.Phase);
                if (delivered) Assert.Null(snapshot.CurrentLot);
                else Assert.Same(lot, snapshot.CurrentLot);
                Assert.False(snapshot.AvailableActions.CanRelay);
                Assert.False(snapshot.AvailableActions.CanBurn);
                if (!delivered)
                    Assert.DoesNotContain("not a cash payout", ManifestPresentationPolicy.LotDetails(snapshot));
            }
    }

    [Fact]
    public void Cash_footprint_respects_finalized_native_dimensions()
    {
        TemplateItem? Wide(string id)
        {
            var template = FindTemplate(id);
            if (template?.Properties is { } properties) { properties.Width = 2; properties.Height = 3; }
            return template;
        }
        var catalog = CashPayoutCatalog.Build(Wide, 130, 150, 500_000, 7_500);
        Assert.Equal(12, catalog.Lots.Single(l => l.Identity.LotId == "btc-2").Evaluation.FootprintCells);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("prohibited")]
    [InlineData("not-accepted")]
    [InlineData("null-rule")]
    [InlineData("missing-fx")]
    [InlineData("null-option")]
    [InlineData("invalid-fx")]
    [InlineData("locked-therapist")]
    [InlineData("missing-gp-quote")]
    [InlineData("gp-nan")]
    [InlineData("gp-infinity")]
    [InlineData("gp-zero")]
    [InlineData("gp-negative")]
    [InlineData("gp-excessive")]
    public void Quotes_require_valid_eligibility_and_isolate_bad_external_data(string scenario)
    {
        var items = new[] { CashPayouts.Roubles, CashPayouts.Dollars, CashPayouts.Euros, CashPayouts.Bitcoin, CashPayouts.GpCoin,
            "543be5dd4bdc2deb348b4569" }.ToDictionary(id => (MongoId)id, id => FindTemplate(id)!);
        var trader = new Trader
        {
            Dialogue = [],
            QuestAssort = [],
            Base = new TraderBase
            {
                Id = Traders.THERAPIST,
                Currency = CurrencyType.RUB,
                UnlockedByDefault = true,
                ItemsBuy = new ItemBuyData { IdList = [(MongoId)CashPayouts.Bitcoin], Category = [] },
                ItemsBuyProhibited = new ItemBuyData { IdList = [], Category = [] },
                LoyaltyLevels = [new() { BuyPriceCoefficient = 40 }]
            },
            Assort = new TraderAssort { Items = [], BarterScheme = [], LoyalLevelItems = [] }
        };
        foreach (var currency in new[] { CashPayouts.Dollars, CashPayouts.Euros })
        {
            trader.Assort.Items.Add(new Item { Id = currency, Template = currency, ParentId = "hideout", SlotId = "hideout" });
            trader.Assort.BarterScheme[(MongoId)currency] = [[new BarterScheme { Template = CashPayouts.Roubles, Count = 150 }]];
        }
        var prices = new Dictionary<string, double> { [CashPayouts.Bitcoin] = 800_000, [CashPayouts.GpCoin] = 7_500 };
        switch (scenario)
        {
            case "prohibited": trader.Base.ItemsBuyProhibited.IdList.Add((MongoId)CashPayouts.Bitcoin); break;
            case "not-accepted": trader.Base.ItemsBuy.IdList.Clear(); break;
            case "null-rule": trader.Base.ItemsBuy.Category = null!; break;
            case "missing-fx": trader.Assort.BarterScheme.Remove((MongoId)CashPayouts.Euros); break;
            case "null-option": trader.Assort.BarterScheme[(MongoId)CashPayouts.Euros] = [null!]; break;
            case "invalid-fx": trader.Assort.BarterScheme[(MongoId)CashPayouts.Euros][0][0].Count = double.NaN; break;
            case "locked-therapist": trader.Base.UnlockedByDefault = false; break;
            case "missing-gp-quote": prices.Remove(CashPayouts.GpCoin); break;
            case "gp-nan": prices[CashPayouts.GpCoin] = double.NaN; break;
            case "gp-infinity": prices[CashPayouts.GpCoin] = double.PositiveInfinity; break;
            case "gp-zero": prices[CashPayouts.GpCoin] = 0; break;
            case "gp-negative": prices[CashPayouts.GpCoin] = -1; break;
            case "gp-excessive": prices[CashPayouts.GpCoin] = 100_000_001; break;
        }
        var traders = new TradersTable { [Traders.THERAPIST] = trader };
        var warnings = new List<string>();
        var cash = CatalogSnapshotCoordinator.FreezeCash(() => CashCurrencyQuotes.Freeze(items, traders, prices), FindTemplate, warnings.Add);
        Assert.Equal(scenario == "valid", cash.OpeningEnabled);
        if (scenario == "valid")
        {
            Assert.Empty(warnings);
            Assert.Equal(480_000, cash.Lots.Single(l => l.Identity.LotId == "btc-1").Evaluation.UseValue);
        }
        else
        {
            Assert.Single(warnings);
            Assert.Equal(31, cash.Lots.Count);
        }
    }

    [Fact]
    public void Cash_catalog_publishes_one_joint_draw_with_exact_Bitcoin_odds()
    {
        var catalog = Catalog();
        Assert.True(catalog.OpeningEnabled);
        Assert.Equal(1, catalog.OfferCount);
        var odds = ManifestOpeningOdds.Create(catalog);
        var family = Assert.Single(odds.Families);
        Assert.Equal(1, odds.OfferCount);
        Assert.Equal("1", family.PerSlotNumerator);
        Assert.Equal("1", family.PerSlotDenominator);
        Assert.Equal(15, family.Lots.Count);
        var bitcoin = family.Lots.Single(l => l.LotId == "btc-1.shipment-v1");
        Assert.Equal("9", bitcoin.ConditionalNumerator);
        Assert.Equal("1000", bitcoin.ConditionalDenominator);
        Assert.Equal("0.90%", bitcoin.ConditionalPercent);
        Assert.Equal("0.10%", family.Lots.Single(l => l.LotId == "btc-50.jackpot-v1").ConditionalPercent);
        Assert.Equal(10_000, CashPayoutCatalog.Payouts.Sum(p => CashPayoutCatalog.OpeningWeight(p.Id)));
    }

    [Fact]
    public void Jackpot_value_does_not_raise_entry_price_and_outliers_fail_closed()
    {
        Assert.Equal(Catalog(300_000).CasePrice, Catalog(800_000).CasePrice);
        Assert.Throws<CargoCatalogValidationException>(() => Catalog(100_000_000));
    }

    [Fact]
    public void Currency_support_does_not_weaken_ordinary_cargo_rules()
    {
        var materializer = new CargoLotMaterializer(FindTemplate);
        foreach (var lot in Catalog().Lots)
        {
            Assert.Throws<CargoCatalogValidationException>(() => materializer.Validate(lot.Forest));
            materializer.ValidateCashPayout(lot.Forest);
        }
        var invalid = RewardForest.Create([new RewardForestNode("root", "root", CashPayouts.Roubles, null, null, null, 999)]);
        Assert.Throws<CargoCatalogValidationException>(() => materializer.ValidateCashPayout(invalid));
    }

    [Fact]
    public void Bitcoin_uses_two_independent_valid_items_not_an_illegal_stack()
    {
        var lot = Catalog().Lots.Single(l => l.Identity.LotId == "btc-2");
        Assert.Equal(2, lot.Forest.Nodes.Count);
        var materialized = new CargoLotMaterializer(FindTemplate).MaterializeCashPayout(lot.Forest, []);
        Assert.Equal(2, materialized.Items.Count);
        Assert.All(materialized.Items, item => Assert.Equal(1, item.Upd!.StackObjectsCount));
        Assert.Equal(2, materialized.Items.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void Single_payout_activates_through_journal_roundtrip_with_Favor_unchanged()
    {
        var catalog = Catalog();
        var prepared = Prepare(catalog);
        var journal = RoundTrip(new CaseOpeningJournal(recoveryMeter: 2, activeManifest: prepared));
        var active = journal.ActiveManifest!.BeginTicketProfileCommit();
        journal.ReplaceActiveManifest(active);
        active = active.ActivateTicket(DateTimeOffset.UnixEpoch.AddMinutes(2));
        journal.ReplaceActiveManifest(active);
        journal = RoundTrip(journal);
        active = journal.ActiveManifest!;
        Assert.Equal(ManifestPhase.Entitlement, active.FlowState.Phase);
        Assert.Equal(1, active.FlowState.LockedOrdinal);
        Assert.Empty(active.Decisions);
        Assert.Empty(active.RelayCandidates);
        Assert.Equal(2, journal.BrokerFavor);
        Assert.Throws<InvalidOperationException>(() => active.DecideOffer(ManifestOfferDecision.Burn, DateTimeOffset.UnixEpoch.AddMinutes(3)));
        var snapshot = ManifestSnapshotEnvelope.Parse(JsonSerializer.Serialize(new
        {
            err = 0,
            errmsg = (string?)null,
            data = new { snapshot = ManifestSnapshotProjection.FromActive(active, catalog, null) }
        }), active.ManifestId);
        Assert.Single(snapshot.FamilySeals);
        Assert.True(snapshot.AvailableActions.CanClaim);
        Assert.False(snapshot.AvailableActions.CanRelay);
        Assert.Null(snapshot.Relay);
    }

    [Fact]
    public void Cash_single_offer_cannot_be_reinterpreted_as_an_ordinary_case()
    {
        var journal = new CaseOpeningJournal(recoveryMeter: 2, activeManifest: Prepare(Catalog()));
        var document = SptCaseJournal.ToDocument(journal);
        document.ActiveManifest!.Ticket!.CaseTemplateId = ModConstants.CaseTemplateId;
        Assert.Throws<ArgumentException>(() => SptCaseJournal.FromDocument(document));
    }

    [Fact]
    public void Cash_terminal_projection_roundtrips_and_malformed_entitlement_is_rejected()
    {
        var active = Prepare(Catalog()).BeginTicketProfileCommit().ActivateTicket(DateTimeOffset.UnixEpoch.AddMinutes(2));
        var terminal = active.ForfeitMissingContent(DateTimeOffset.UnixEpoch.AddMinutes(3));
        var journal = new CaseOpeningJournal(recoveryMeter: 2, activeManifest: active);
        journal.ReplaceActiveManifest(terminal);
        journal.FinishActiveManifest();
        var receipt = Assert.Single(RoundTrip(journal).ManifestReceipts);
        var snapshot = ManifestSnapshotEnvelope.Parse(JsonSerializer.Serialize(new
        {
            err = 0,
            errmsg = (string?)null,
            data = new { snapshot = ManifestSnapshotProjection.FromTerminal(receipt) }
        }), receipt.ManifestId);
        Assert.Equal(1, snapshot.LockedOrdinal);
        var document = SptCaseJournal.ToDocument(journal);
        document.ManifestReceipts![0].Entitlement = null;
        Assert.Throws<ArgumentException>(() => SptCaseJournal.FromDocument(document));
    }

    internal static CargoCatalogSnapshot Catalog(decimal bitcoin = 500_000) => CashPayoutCatalog.Build(FindTemplate, 130, 150, bitcoin, 7_500);

    internal static ManifestRecord Prepare(CargoCatalogSnapshot catalog)
    {
        var offers = new ManifestCatalogSelector(() => 0).CreateOffers(catalog);
        Assert.Single(offers);
        var ticket = new ManifestTicketPayload("000000000000000000000001", "000000000000000000000002",
            DateTimeOffset.UnixEpoch.AddMinutes(1), false, false, null, caseTemplateId: CaseContracts.CashCache)
            .WithCommitPlan(1, ManifestInputCommitWitness.GenesisHash);
        return new ManifestRecord("cash-test", catalog.SnapshotId,
            ManifestCommitmentEvidence.CreateWithRandomNonce("cash-test", catalog.SnapshotId, offers),
            ManifestFlowState.PrepareTicket(), ticket, offers, null, null, null, null, 2);
    }

    private static CaseOpeningJournal RoundTrip(CaseOpeningJournal journal) => SptCaseJournal.FromDocument(
        JsonSerializer.Deserialize<SptCaseJournalDocument>(JsonSerializer.Serialize(SptCaseJournal.ToDocument(journal)))!);

    internal static TemplateItem? FindTemplate(string id)
    {
        const string root = "54009119af1c881c07000029";
        const string money = "543be5dd4bdc2deb348b4569";
        if (id == money) return new TemplateItem { Id = id, Parent = root, Name = id, Type = "Node" };
        if (!CashPayouts.IsAllowed(id)) return null;
        return new TemplateItem
        {
            Id = id,
            Parent = id == CashPayouts.Bitcoin ? root : money,
            Name = id,
            Type = "Item",
            Properties = new TemplateItemProperties
            {
                StackMaxSize = id == CashPayouts.Bitcoin ? 1 : id == CashPayouts.GpCoin ? 100 : 500_000,
                Width = 1,
                Height = 1,
                Slots = [],
                Chambers = [],
                Cartridges = [],
                StackSlots = [],
                Grids = [],
                Prefab = new Prefab { Path = $"test/{id}.bundle" }
            }
        };
    }
}
