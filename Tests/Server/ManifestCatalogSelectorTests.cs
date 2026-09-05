using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class ManifestCatalogSelectorTests
{
    private const string MainTrack = "main-track";

    [Fact]
    public void CreateOffers_PersistsThreeDistinctFamiliesAndCanonicalDrawEvidence()
    {
        var lots = new[]
        {
            Lot("provider-d", "lot-d", "family-d", "track-d", RewardRarity.BlackLabel),
            Lot("provider-b", "lot-b", "family-b", "track-b", RewardRarity.Contractor),
            Lot("provider-a", "lot-a", "family-a", "track-a", RewardRarity.ScavGrade),
            Lot("provider-c", "lot-c", "family-c", "track-c", RewardRarity.Restricted)
        };
        var draws = new RecordingDrawSource(
            0,
            CanonicalRngEvidence.UnitDenominator - 1,
            CanonicalRngEvidence.UnitDenominator / 2);
        var selector = new ManifestCatalogSelector(draws.Next);

        var offers = selector.CreateOffers(Snapshot(lots));

        Assert.Equal(ManifestRecord.OfferCount, offers.Count);
        Assert.Equal([1, 2, 3], offers.Select(offer => offer.Ordinal));
        Assert.Equal(ManifestRecord.OfferCount, offers.Select(offer => offer.Identity.FamilyId).Distinct().Count());
        Assert.Equal(ManifestRecord.OfferCount, offers.Select(SemanticKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["family-a", "family-d", "family-c"], offers.Select(offer => offer.Identity.FamilyId.Value));
        Assert.Equal(
            [0, CanonicalRngEvidence.UnitDenominator - 1, CanonicalRngEvidence.UnitDenominator / 2],
            offers.Select(offer => offer.RngEvidence.UnitNumerator));
        Assert.All(offers, offer => Assert.Equal(ManifestRngPurpose.OfferSelection, offer.RngEvidence.Purpose));
        Assert.Equal([1, 2, 3], offers.Select(offer => offer.RngEvidence.DrawOrdinal));
        Assert.Equal(ManifestRecord.OfferCount, draws.CallCount);
    }

    [Fact]
    public void CreateOffers_IsStableAcrossCatalogInputOrdering()
    {
        var lots = new[]
        {
            Lot("provider-b", "lot-b2", "family-a", "track-a", RewardRarity.ScavGrade, weight: 3d),
            Lot("provider-a", "lot-a1", "family-a", "track-a", RewardRarity.ScavGrade),
            Lot("provider-c", "lot-c1", "family-b", "track-b", RewardRarity.Contractor),
            Lot("provider-d", "lot-d1", "family-c", "track-c", RewardRarity.Restricted),
            Lot("provider-e", "lot-e1", "family-d", "track-d", RewardRarity.BlackLabel)
        };
        var drawValues = new[]
        {
            CanonicalRngEvidence.UnitDenominator / 7,
            CanonicalRngEvidence.UnitDenominator * 5 / 7,
            CanonicalRngEvidence.UnitDenominator / 3
        };

        var forward = new ManifestCatalogSelector(new RecordingDrawSource(drawValues).Next)
            .CreateOffers(Snapshot(lots));
        var reverse = new ManifestCatalogSelector(new RecordingDrawSource(drawValues).Next)
            .CreateOffers(Snapshot(lots.Reverse()));

        Assert.Equal(forward.Select(SemanticKey), reverse.Select(SemanticKey));
        Assert.Equal(
            forward.Select(offer => offer.RngEvidence.UnitNumerator),
            reverse.Select(offer => offer.RngEvidence.UnitNumerator));
    }

    [Fact]
    public void CreateOffers_WeightsProvidersOnceRegardlessOfTheirLotCounts()
    {
        var lots = new[]
        {
            Lot("provider-a", "single", "family-a", "track-a", RewardRarity.ScavGrade),
            Lot("provider-b", "many-1", "family-a", "track-a", RewardRarity.ScavGrade),
            Lot("provider-b", "many-2", "family-a", "track-a", RewardRarity.ScavGrade),
            Lot("provider-b", "many-3", "family-a", "track-a", RewardRarity.ScavGrade),
            Lot("provider-b", "many-4", "family-a", "track-a", RewardRarity.ScavGrade),
            Lot("provider-c", "family-b", "family-b", "track-b", RewardRarity.Contractor),
            Lot("provider-d", "family-c", "family-c", "track-c", RewardRarity.Restricted)
        };
        var providerWeights = ProviderWeights(lots, ("provider-a", 1d), ("provider-b", 3d));
        var lastProviderADraw = CanonicalRngEvidence.UnitDenominator / 12;

        var beforeBoundary = new ManifestCatalogSelector(
                new RecordingDrawSource(lastProviderADraw, 0, 0).Next)
            .CreateOffers(Snapshot(lots, providerWeights));
        var afterBoundary = new ManifestCatalogSelector(
                new RecordingDrawSource(lastProviderADraw + 1, 0, 0).Next)
            .CreateOffers(Snapshot(lots, providerWeights));

        Assert.Equal("provider-a", beforeBoundary[0].Identity.ProviderId);
        Assert.Equal("provider-b", afterBoundary[0].Identity.ProviderId);
    }

    [Fact]
    public void CreateOffers_RespectsLotWeightsWithinTheSelectedProvider()
    {
        var lots = new[]
        {
            Lot("provider-a", "a-light", "family-a", "track-a", RewardRarity.ScavGrade),
            Lot("provider-a", "b-heavy", "family-a", "track-a", RewardRarity.ScavGrade, weight: 3d),
            Lot("provider-b", "family-b", "family-b", "track-b", RewardRarity.Contractor),
            Lot("provider-c", "family-c", "family-c", "track-c", RewardRarity.Restricted)
        };
        var lastLightDraw = CanonicalRngEvidence.UnitDenominator / 12;

        var beforeBoundary = new ManifestCatalogSelector(
                new RecordingDrawSource(lastLightDraw, 0, 0).Next)
            .CreateOffers(Snapshot(lots));
        var afterBoundary = new ManifestCatalogSelector(
                new RecordingDrawSource(lastLightDraw + 1, 0, 0).Next)
            .CreateOffers(Snapshot(lots));

        Assert.Equal("a-light", beforeBoundary[0].Identity.LotId);
        Assert.Equal("b-heavy", afterBoundary[0].Identity.LotId);
    }

    [Fact]
    public void CreateOffers_RefusesCatalogWhenOpeningIsDisabledWithoutDrawing()
    {
        var lots = new[]
        {
            Lot("provider-a", "lot-a", "family-a", "track-a", RewardRarity.ScavGrade),
            Lot("provider-b", "lot-b", "family-b", "track-b", RewardRarity.Contractor)
        };
        var draws = new RecordingDrawSource(0, 0, 0);
        var selector = new ManifestCatalogSelector(draws.Next);

        var exception = Assert.Throws<InvalidOperationException>(() => selector.CreateOffers(Snapshot(lots)));

        Assert.Equal("Catalog has fewer than three distinct validated loot families.", exception.Message);
        Assert.Equal(0, draws.CallCount);
    }

    [Fact]
    public void CreateOffers_WithNullOverrideIsIdenticalToTheOmittedDefaultParameter()
    {
        var lots = ForcedPoolTestLots();

        var omitted = new ManifestCatalogSelector(new RecordingDrawSource(0, 0, 0).Next)
            .CreateOffers(Snapshot(lots));
        var explicitNull = new ManifestCatalogSelector(new RecordingDrawSource(0, 0, 0).Next)
            .CreateOffers(Snapshot(lots), allowedProviderIds: null);

        Assert.Equal(omitted.Select(SemanticKey), explicitNull.Select(SemanticKey));
    }

    [Fact]
    public void CreateOffers_RestrictsAFamilysProviderDrawToTheAllowedSetWhenItHasMatchingContent()
    {
        // family-a has a decoy provider that sorts first alphabetically (and
        // therefore wins every unrestricted draw-zero selection) plus the
        // "vault"-style forced provider. Restricting to {"vault"} must flip
        // family-a's offer to the vault lot without touching anything else.
        var lots = ForcedPoolTestLots();
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "vault" };

        var unrestricted = new ManifestCatalogSelector(new RecordingDrawSource(0, 0, 0).Next)
            .CreateOffers(Snapshot(lots));
        var restricted = new ManifestCatalogSelector(new RecordingDrawSource(0, 0, 0).Next)
            .CreateOffers(Snapshot(lots), allowed);

        var unrestrictedFamilyA = Assert.Single(unrestricted, offer => offer.Identity.FamilyId.Value == "family-a");
        var restrictedFamilyA = Assert.Single(restricted, offer => offer.Identity.FamilyId.Value == "family-a");
        Assert.Equal("aaa-decoy", unrestrictedFamilyA.Identity.ProviderId);
        Assert.Equal("vault", restrictedFamilyA.Identity.ProviderId);

        // Families the allowed set has no lots in are completely unaffected --
        // this is the documented fallback, never a throw.
        Assert.Equal(
            unrestricted.Where(offer => offer.Identity.FamilyId.Value != "family-a").Select(SemanticKey),
            restricted.Where(offer => offer.Identity.FamilyId.Value != "family-a").Select(SemanticKey));
    }

    [Fact]
    public void CreateOffers_FallsBackToTheFullBlendedDrawWhenTheAllowedProvidersAreAbsentEntirely()
    {
        // Simulates a stale/forced tag whose provider no longer exists in the
        // frozen catalog (e.g. the mod backing it was uninstalled after the
        // tag was set). Every offer must fall back to the exact unrestricted
        // draw rather than throwing.
        var lots = ForcedPoolTestLots();
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "some-provider-nobody-installed" };

        var unrestricted = new ManifestCatalogSelector(new RecordingDrawSource(0, 0, 0).Next)
            .CreateOffers(Snapshot(lots));
        var restricted = new ManifestCatalogSelector(new RecordingDrawSource(0, 0, 0).Next)
            .CreateOffers(Snapshot(lots), allowed);

        Assert.Equal(unrestricted.Select(SemanticKey), restricted.Select(SemanticKey));
    }

    private static ResolvedCargoLot[] ForcedPoolTestLots() =>
    [
        Lot("aaa-decoy", "decoy-lot", "family-a", "track-a", RewardRarity.ScavGrade),
        Lot("vault", "vault-lot", "family-a", "track-vault", RewardRarity.BlackLabel),
        Lot("zzz-only", "lot-b", "family-b", "track-b", RewardRarity.Contractor),
        Lot("zzz-only-c", "lot-c", "family-c", "track-c", RewardRarity.Restricted)
    ];

    [Fact]
    public void CreateRelayCandidates_KeepsOnlyCompleteSameTrackGradeHalves()
    {
        var stake = Lot("provider-a", "stake", "family-a", MainTrack, RewardRarity.Contractor);
        var validSidegrade = Lot("provider-b", "sidegrade", "family-b", MainTrack, RewardRarity.Contractor);
        var validUpgrade = Lot("provider-c", "upgrade", "family-c", MainTrack, RewardRarity.Restricted);
        var lots = new[]
        {
            Lot("provider-d", "wrong-track", "family-a", "other-track", RewardRarity.Contractor),
            Lot("provider-e", "wrong-lower-grade", "family-b", MainTrack, RewardRarity.ScavGrade),
            Lot("provider-f", "wrong-higher-grade", "family-c", MainTrack, RewardRarity.BlackLabel),
            validUpgrade,
            stake,
            validSidegrade
        };
        var selector = new ManifestCatalogSelector();

        var candidates = selector.CreateRelayCandidates(Snapshot(lots), Entitlement(stake));

        Assert.Equal(2, candidates.Count);
        Assert.Collection(
            candidates,
            candidate =>
            {
                Assert.Equal(ManifestRelayResult.Upgrade, candidate.TargetResult);
                Assert.Equal("upgrade", candidate.Identity.LotId);
                Assert.Equal(RewardRarity.Restricted, candidate.Rarity);
            },
            candidate =>
            {
                Assert.Equal(ManifestRelayResult.Sidegrade, candidate.TargetResult);
                Assert.Equal("sidegrade", candidate.Identity.LotId);
                Assert.Equal(RewardRarity.Contractor, candidate.Rarity);
            });
        Assert.All(candidates, candidate => Assert.Equal(MainTrack, candidate.Identity.TrackId.Value));
        Assert.DoesNotContain(candidates, candidate => SemanticKey(candidate) == SemanticKey(stake));
    }

    [Fact]
    public void CreateRelayCandidates_ReturnsEmptyWhenEitherCandidateHalfIsUnavailable()
    {
        var stake = Lot("provider-a", "stake", "family-a", MainTrack, RewardRarity.Contractor);
        var sidegradeOnly = Snapshot(
        [
            stake,
            Lot("provider-b", "sidegrade", "family-b", MainTrack, RewardRarity.Contractor),
            Lot("provider-c", "unrelated", "family-c", "other-track", RewardRarity.Restricted)
        ]);
        var upgradeOnly = Snapshot(
        [
            stake,
            Lot("provider-b", "upgrade", "family-b", MainTrack, RewardRarity.Restricted),
            Lot("provider-c", "unrelated", "family-c", "other-track", RewardRarity.ScavGrade)
        ]);
        var selector = new ManifestCatalogSelector();

        Assert.Empty(selector.CreateRelayCandidates(sidegradeOnly, Entitlement(stake)));
        Assert.Empty(selector.CreateRelayCandidates(upgradeOnly, Entitlement(stake)));
    }

    [Fact]
    public void CreateRelayCandidates_HonorsAnExistingTrackWhenTheCurrentCatalogCannotOpenNewManifests()
    {
        var stake = Lot("provider-a", "stake", "family-a", MainTrack, RewardRarity.Contractor);
        var snapshot = Snapshot(
        [
            stake,
            Lot("provider-b", "sidegrade", "family-a", MainTrack, RewardRarity.Contractor),
            Lot("provider-c", "upgrade", "family-a", MainTrack, RewardRarity.Restricted)
        ]);
        Assert.False(snapshot.OpeningEnabled);

        var candidates = new ManifestCatalogSelector()
            .CreateRelayCandidates(snapshot, Entitlement(stake));

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, candidate => candidate.TargetResult == ManifestRelayResult.Upgrade);
        Assert.Contains(candidates, candidate => candidate.TargetResult == ManifestRelayResult.Sidegrade);
    }

    [Fact]
    public void CreateRelayCandidates_ReturnsEmptyForBlackLabelEntitlement()
    {
        var stake = Lot("provider-a", "stake", "family-a", MainTrack, RewardRarity.BlackLabel);
        var snapshot = Snapshot(
        [
            stake,
            Lot("provider-b", "same-grade", "family-b", MainTrack, RewardRarity.BlackLabel),
            Lot("provider-c", "unrelated", "family-c", MainTrack, RewardRarity.Restricted)
        ]);

        var candidates = new ManifestCatalogSelector().CreateRelayCandidates(snapshot, Entitlement(stake));

        Assert.Empty(candidates);
    }

    [Fact]
    public void CreateRelayCandidates_AppliesCountAndNodeBoundsDeterministically()
    {
        const int nodeCount = 128;
        var stake = Lot("provider-stake", "stake", "family-c", MainTrack, RewardRarity.Contractor);
        var candidates = Enumerable.Range(0, 70)
            .Select(index => Lot(
                "provider-upgrade",
                $"upgrade-{index:D3}",
                "family-a",
                MainTrack,
                RewardRarity.Restricted,
                nodeCount: nodeCount))
            .Concat(Enumerable.Range(0, 70).Select(index => Lot(
                "provider-sidegrade",
                $"sidegrade-{index:D3}",
                "family-b",
                MainTrack,
                RewardRarity.Contractor,
                nodeCount: nodeCount)))
            .ToArray();
        var lots = candidates.Append(stake).ToArray();
        var selector = new ManifestCatalogSelector();

        var forward = selector.CreateRelayCandidates(Snapshot(lots), Entitlement(stake));
        var reverse = selector.CreateRelayCandidates(Snapshot(lots.Reverse()), Entitlement(stake));

        Assert.Equal(ManifestRecord.MaximumRelayCandidateCount, forward.Count);
        Assert.Equal(ManifestRecord.MaximumFrozenRelayNodeCount, forward.Sum(candidate => candidate.Forest.Nodes.Count));
        Assert.Equal(32, forward.Count(candidate => candidate.TargetResult == ManifestRelayResult.Upgrade));
        Assert.Equal(32, forward.Count(candidate => candidate.TargetResult == ManifestRelayResult.Sidegrade));
        Assert.Equal(forward.Select(SemanticKey), reverse.Select(SemanticKey));
    }

    private static ManifestEntitlementSnapshot Entitlement(ResolvedCargoLot lot) => new(
        lot.Evaluation.Grade,
        lot.Identity,
        lot.Forest,
        lot.Fingerprint);

    private static CargoCatalogSnapshot Snapshot(
        IEnumerable<ResolvedCargoLot> lots,
        IReadOnlyDictionary<string, double>? providerWeights = null)
    {
        var snapshot = lots.ToArray();
        return new CargoCatalogSnapshot(
            new string('a', 64),
            snapshot,
            [],
            providerWeights ?? ProviderWeights(snapshot));
    }

    private static IReadOnlyDictionary<string, double> ProviderWeights(
        IEnumerable<ResolvedCargoLot> lots,
        params (string ProviderId, double Weight)[] overrides)
    {
        var weights = lots
            .Select(lot => lot.Identity.ProviderId)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(providerId => providerId, _ => 1d, StringComparer.Ordinal);
        foreach (var (providerId, weight) in overrides)
        {
            weights[providerId] = weight;
        }
        return weights;
    }

    private static ResolvedCargoLot Lot(
        string providerId,
        string lotId,
        string familyId,
        string trackId,
        RewardRarity rarity,
        double weight = 1d,
        int nodeCount = 1)
    {
        var templateId = string.Concat("template-", lotId);
        var family = new FamilyId(familyId);
        var track = new TrackId(trackId);
        var definition = new CargoLotDefinition(
            providerId,
            "1.0.0",
            lotId,
            string.Concat("Display ", lotId),
            string.Concat("Purpose ", lotId),
            family,
            track,
            templateId,
            weight,
            new RaidRole("testing"),
            [new TemplateLine(templateId, 1, 1)]);
        var forest = Forest(templateId, nodeCount);
        var fingerprint = RewardForestFingerprintV2.Compute(providerId, lotId, forest);
        var identity = CargoLotIdentitySnapshot.Capture(definition, fingerprint);
        var useValue = rarity switch
        {
            RewardRarity.ScavGrade => 50_000L,
            RewardRarity.Contractor => 100_000L,
            RewardRarity.Restricted => 200_000L,
            RewardRarity.BlackLabel => 400_000L,
            _ => throw new ArgumentOutOfRangeException(nameof(rarity))
        };
        return new ResolvedCargoLot(
            definition,
            forest,
            fingerprint,
            identity,
            new CargoLotEvaluation(useValue, useValue, nodeCount, rarity));
    }

    private static RewardForest Forest(string templateId, int nodeCount)
    {
        if (nodeCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeCount));
        }

        var nodes = new List<RewardForestNode>
        {
            new("root", "root", templateId, null, null, null, 1)
        };
        for (var index = 1; index < nodeCount; index++)
        {
            nodes.Add(new RewardForestNode(
                "root",
                $"root/node-{index:D4}",
                templateId,
                "root",
                $"slot-{index:D4}",
                null,
                1));
        }
        return RewardForest.Create(nodes);
    }

    private static string SemanticKey(ManifestOfferSnapshot offer) =>
        string.Concat(offer.Identity.ProviderId, "/", offer.Identity.LotId, "/", offer.Fingerprint.Sha256Hex);

    private static string SemanticKey(ManifestRelayCandidateSnapshot candidate) =>
        string.Concat(candidate.Identity.ProviderId, "/", candidate.Identity.LotId, "/", candidate.Fingerprint.Sha256Hex);

    private static string SemanticKey(ResolvedCargoLot lot) =>
        string.Concat(lot.Identity.ProviderId, "/", lot.Identity.LotId, "/", lot.Fingerprint.Sha256Hex);

    private sealed class RecordingDrawSource
    {
        private readonly Queue<long> _draws;

        public RecordingDrawSource(params long[] draws)
        {
            _draws = new Queue<long>(draws);
        }

        public int CallCount { get; private set; }

        public long Next()
        {
            CallCount++;
            return _draws.Dequeue();
        }
    }
}
