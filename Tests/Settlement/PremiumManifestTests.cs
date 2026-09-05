using System.Text.Json;
using ContrabandCases.Client.Opening;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Tests.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class PremiumManifestTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ManifestOpeningTier.Epic, 1)]
    [InlineData(ManifestOpeningTier.Epic, 2)]
    [InlineData(ManifestOpeningTier.Epic, 3)]
    [InlineData(ManifestOpeningTier.Legendary, 1)]
    [InlineData(ManifestOpeningTier.Legendary, 2)]
    [InlineData(ManifestOpeningTier.Legendary, 3)]
    public void Save_reconnect_choose_and_terminal_receipt_preserve_all_choices(ManifestOpeningTier tier, int ordinal)
    {
        var catalog = Catalog();
        var prepared = Prepare(catalog, tier);
        var journal = RoundTrip(new CaseOpeningJournal(activeManifest: prepared));
        Assert.Equal(tier, journal.ActiveManifest!.Ticket.OpeningQuality!.Tier);
        Assert.Empty(Parse(ManifestSnapshotProjection.FromActive(journal.ActiveManifest, catalog, null)).PremiumChoices);
        journal.ReplaceActiveManifest(journal.ActiveManifest.BeginTicketProfileCommit());
        journal.ReplaceActiveManifest(journal.ActiveManifest.ActivateTicket(Now.AddSeconds(1)));
        journal = RoundTrip(journal);
        var active = journal.ActiveManifest!;
        var snapshot = Parse(ManifestSnapshotProjection.FromActive(active, catalog, null));
        Assert.Equal(3, snapshot.PremiumChoices.Count);
        Assert.All(snapshot.FamilySeals, seal => { Assert.True(seal.Revealed); Assert.False(seal.Burned); });
        Assert.True(snapshot.AvailableActions.CanLock);
        Assert.False(snapshot.AvailableActions.CanBurn);
        Assert.Equal(prepared.Offers.Select(o => o.Fingerprint), active.Offers.Select(o => o.Fingerprint));
        Assert.Throws<InvalidOperationException>(() => active.DecideOffer(ManifestOfferDecision.Burn, Now.AddSeconds(2)));
        journal.ReplaceActiveManifest(active.ChoosePremiumOffer(ordinal, Now.AddSeconds(2)));
        journal = RoundTrip(journal);
        active = journal.ActiveManifest!;
        Assert.Equal(ManifestOfferDecision.Choose, Assert.Single(active.Decisions).Decision);
        Assert.Equal(ordinal, active.FlowState.LockedOrdinal);
        Assert.Equal(prepared.Offers[ordinal - 1].Fingerprint, active.Entitlement!.Fingerprint);
        var chosen = Parse(ManifestSnapshotProjection.FromActive(active, catalog, null));
        Assert.Empty(chosen.PremiumChoices);
        Assert.False(ManifestPresentationPolicy.ShouldRevealAfter(snapshot, chosen, false));
        journal.ReplaceActiveManifest(active.ForfeitMissingContent(Now.AddSeconds(3)));
        journal.FinishActiveManifest();
        var receipt = Assert.Single(RoundTrip(journal).ManifestReceipts);
        Assert.Equal(tier, receipt.OpeningQuality!.Tier);
        Assert.Equal(ordinal, Parse(ManifestSnapshotProjection.FromTerminal(receipt)).LockedOrdinal);
    }

    [Fact]
    public void Premium_pool_respects_value_floor_distinctness_and_chase_cap()
    {
        var catalog = Catalog();
        Assert.True(ManifestPremiumPool.IsAvailable(catalog, ManifestOpeningTier.Legendary));
        for (var i = 0; i < 200; i++)
        {
            var random = new Random(i);
            var selector = new ManifestCatalogSelector(() => random.NextInt64(CanonicalRngEvidence.UnitDenominator));
            var offers = selector.CreatePremiumOffers(catalog, ManifestOpeningTier.Legendary);
            Assert.Equal(3, offers.Select(o => o.Fingerprint).Distinct().Count());
            Assert.All(offers, o => Assert.Equal(RewardRarity.BlackLabel, o.Rarity));
        }
        var pool = ManifestOpeningPool.Create(catalog, ManifestPremiumPool.Candidates(catalog, ManifestOpeningTier.Legendary));
        var chase = pool.Lots.Select((lot, i) => ManifestOpeningPool.IsChase(lot) ? pool.Weights[i] : 0).Aggregate((a, b) => a + b);
        Assert.True(chase * 400 <= pool.Weights.Total);
    }

    [Fact]
    public void Insufficient_premium_alternatives_fail_before_drawing_and_publish_zero_odds()
    {
        var catalog = Catalog(premiumCount: 2);
        Assert.False(ManifestPremiumPool.IsAvailable(catalog, ManifestOpeningTier.Legendary));
        Assert.Throws<InvalidOperationException>(() => new ManifestCatalogSelector(() => throw new Exception("Must not draw"))
            .CreatePremiumOffers(catalog, ManifestOpeningTier.Legendary));
        var odds = ManifestOpeningOdds.Create(catalog);
        Assert.Equal(0, odds.PremiumOdds!.Legendary.ChanceBasisPoints);
        Assert.Empty(odds.PremiumOdds.Legendary.Lots);
    }

    [Fact]
    public void Missing_unchosen_package_blocks_choices_without_rerolling()
    {
        var catalog = Catalog();
        var active = Prepare(catalog, ManifestOpeningTier.Legendary).BeginTicketProfileCommit().ActivateTicket(Now.AddSeconds(1));
        var missing = new CargoCatalogSnapshot(new string('c', 64),
            catalog.Lots.Where(l => !l.Fingerprint.Equals(active.Offers[2].Fingerprint)), [], catalog.ProviderWeights);
        var projected = ManifestSnapshotProjection.FromActive(active, missing, null);
        var parsed = Parse(projected);
        Assert.True(parsed.MissingContentBlocked);
        Assert.False(parsed.AvailableActions.CanLock);
        Assert.Equal(3, parsed.PremiumChoices.Count);
        Assert.True(ManifestSnapshotProjection.IsMissingCurrentContent(active, missing));
    }

    [Fact]
    public void Journal_cannot_rewrite_quality_and_parser_rejects_below_grade_choice()
    {
        var catalog = Catalog();
        var prepared = Prepare(catalog, ManifestOpeningTier.Legendary);
        var journal = new CaseOpeningJournal(activeManifest: prepared);
        var document = SptCaseJournal.ToDocument(journal);
        document.ActiveManifest!.Ticket!.OpeningQuality = new ManifestOpeningQuality(ManifestOpeningTier.Epic, 0, true, true, true);
        Assert.Throws<InvalidOperationException>(() => journal.ReplaceActiveManifest(SptCaseJournal.FromDocument(document).ActiveManifest!));
        var active = prepared.BeginTicketProfileCommit().ActivateTicket(Now.AddSeconds(1));
        var json = JObject.Parse(Envelope(ManifestSnapshotProjection.FromActive(active, catalog, null)));
        json["data"]!["snapshot"]!["premiumChoices"]![1]!["grade"] = "ScavGrade";
        Assert.Throws<ManifestSnapshotException>(() => ManifestSnapshotParser.Parse(json.ToString(), active.ManifestId, true));
    }

    [Fact]
    public void Premium_odds_roundtrip_and_reveal_show_only_saved_premium_packages()
    {
        var catalog = Catalog();
        var data = ManifestOpeningOdds.Create(catalog);
        var odds = ManifestSnapshotParser.ParseCurrent(JsonSerializer.Serialize(new
        { err = 0, errmsg = (string?)null, data = ManifestCurrentStateEnvelope.FromOpeningOdds(data) })).OpeningOdds!;
        Assert.Equal(150, odds.PremiumOdds!.Epic.ChanceBasisPoints);
        Assert.Equal(20, odds.PremiumOdds.Legendary.ChanceBasisPoints);
        Assert.Contains("98.3%", ManifestPresentationPolicy.PremiumOddsText(odds, true));
        var snapshot = Parse(ManifestSnapshotProjection.FromActive(Prepare(catalog, ManifestOpeningTier.Legendary)
            .BeginTicketProfileCommit().ActivateTicket(Now.AddSeconds(1)), catalog, null));
        var reveal = ManifestPresentationPolicy.CreateReveal(snapshot, false, 1, 40, 32, 900, 120, 8,
            publishedCatalog: odds);
        Assert.Equal(3, reveal.Tiles.Count);
        Assert.All(reveal.Tiles.Values, tile => Assert.Equal(RewardRarity.BlackLabel, tile.Grade));
        Assert.Contains("CHOOSE ONE", reveal.Header);
    }

    internal static CargoCatalogSnapshot Catalog(int premiumCount = 3) => CaseCatalogTests.Snapshot(
        // Deliberately oppose family order and lot-ID order within one provider.
        // Premium draws cross families; published rows must still obey the wire order.
        Enumerable.Range(0, premiumCount).Select(i => ManifestOpeningPoolTests.Lot("premium" + i, 400_000,
            family: "family-" + (premiumCount - i)))
            .Concat([ManifestOpeningPoolTests.Lot("chase", 1_000_000),
                ManifestOpeningPoolTests.Lot("b", 50_000, family: "family-b"),
                ManifestOpeningPoolTests.Lot("c", 50_000, family: "family-c")]));

    private static ManifestRecord Prepare(CargoCatalogSnapshot catalog, ManifestOpeningTier tier)
    {
        var offers = new ManifestCatalogSelector(() => 0).CreatePremiumOffers(catalog, tier);
        var ticket = new ManifestTicketPayload("000000000000000000000001", "000000000000000000000002", Now,
            false, false, null, openingQuality: new ManifestOpeningQuality(tier, 0, true, true, true))
            .WithCommitPlan(1, ManifestInputCommitWitness.GenesisHash);
        return new ManifestRecord("premium-test", catalog.SnapshotId,
            ManifestCommitmentEvidence.CreateWithRandomNonce("premium-test", catalog.SnapshotId, offers),
            ManifestFlowState.PrepareTicket(), ticket, offers, null, null, null, null, 0);
    }

    private static CaseOpeningJournal RoundTrip(CaseOpeningJournal journal) => SptCaseJournal.FromDocument(
        JsonSerializer.Deserialize<SptCaseJournalDocument>(JsonSerializer.Serialize(SptCaseJournal.ToDocument(journal)))!);
    private static string Envelope(ManifestSnapshotData data) => JsonSerializer.Serialize(new
    { err = 0, errmsg = (string?)null, data = new ContrabandCases.Server.Settlement.ManifestSnapshotEnvelope { Snapshot = data } });
    private static ManifestSnapshot Parse(ManifestSnapshotData data) => ManifestSnapshotParser.Parse(Envelope(data), data.ManifestId, true)!;
}
