using System.Text.Json;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class ManifestOpeningOddsTests
{
    [Fact]
    public void Old_common_commitments_resolve_after_the_uncommon_split_without_weakening_content_matching()
    {
        var lot = Lot("core", "old-common", "field-supply", RewardRarity.Uncommon);
        var catalog = Snapshot([lot]);
        Assert.Same(lot, catalog.ResolveExact(RewardRarity.ScavGrade, lot.Identity, lot.Forest, lot.Fingerprint,
            RarityLadderVersion.LegacyFourTier));
        Assert.Null(catalog.ResolveExact(RewardRarity.ScavGrade, lot.Identity, lot.Forest, lot.Fingerprint));
        Assert.Null(catalog.ResolveExact(RewardRarity.Contractor, lot.Identity, lot.Forest, lot.Fingerprint,
            RarityLadderVersion.LegacyFourTier));
        var other = Lot("core", "changed-content", "field-supply", RewardRarity.Uncommon);
        Assert.Null(catalog.ResolveExact(RewardRarity.ScavGrade, lot.Identity, other.Forest, other.Fingerprint,
            RarityLadderVersion.LegacyFourTier));
    }

    [Fact]
    public void Singleton_themes_compete_with_broad_category_providers()
    {
        var armor = Lot("elite", "armor", "armor", RewardRarity.BlackLabel);
        var catalog = Snapshot([
            Lot("core", "gear", "operator", RewardRarity.Contractor),
            Lot("core", "gun", "arsenal", RewardRarity.Contractor),
            Lot("core", "meds", "field-supply", RewardRarity.Contractor),
            armor
        ], new Dictionary<string, double> { ["core"] = 1, ["elite"] = 0.05 });

        var odds = ManifestOpeningOdds.Create(catalog);
        Assert.Equal(3, odds.FamilyCount);
        var equipment = odds.Families.Single(row => row.FamilyId == "operator");
        Assert.Equal("100.00%", equipment.InclusionPercent);
        Assert.Equal("4.76%", equipment.Lots.Single(lot => lot.ProviderId == "elite").ConditionalPercent);
        Assert.Equal("armor", armor.Identity.FamilyId.Value);
        Assert.Same(armor, catalog.ResolveExact(armor.Evaluation.Grade, armor.Identity, armor.Forest, armor.Fingerprint));
        var selector = new ManifestCatalogSelector(() => 0);
        Assert.Equal(3, selector.CreateOffers(catalog).Select(offer =>
            CargoFamilies.SelectionFamily(offer.Identity.FamilyId)).Distinct().Count());
    }

    [Fact]
    public void Create_PublishesExactHierarchicalOddsInCanonicalOrder()
    {
        var lots = new[]
        {
            Lot("provider-z", "z-heavy", "family-a", RewardRarity.BlackLabel, 3d),
            Lot("core", "a-core", "family-a", RewardRarity.ScavGrade),
            Lot("provider-z", "a-light", "family-a", RewardRarity.Restricted),
            Lot("core", "family-b-lot", "family-b", RewardRarity.Contractor),
            Lot("core", "family-c-lot", "family-c", RewardRarity.Restricted),
            Lot("core", "family-d-lot", "family-d", RewardRarity.BlackLabel)
        };
        var catalog = Snapshot(
            lots,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["core"] = 1d,
                ["provider-z"] = 3d
            });

        var odds = ManifestOpeningOdds.Create(catalog);

        Assert.Equal(ManifestOpeningOddsData.CurrentProtocolVersion, odds.ProtocolVersion);
        Assert.Equal(catalog.SnapshotId, odds.CatalogSnapshotId);
        Assert.Equal(ManifestRecord.OfferCount, odds.OfferCount);
        Assert.Equal(4, odds.FamilyCount);
        Assert.Equal(ManifestOpeningOddsData.SelectionRuleValue, odds.SelectionRule);
        Assert.Equal(
            ["family-a", "family-b", "family-c", "family-d"],
            odds.Families.Select(family => family.FamilyId));
        Assert.All(odds.Families, family =>
        {
            Assert.Equal("1", family.PerSlotNumerator);
            Assert.Equal("4", family.PerSlotDenominator);
            Assert.Equal("25.00%", family.PerSlotPercent);
            Assert.Equal("3", family.InclusionNumerator);
            Assert.Equal("4", family.InclusionDenominator);
            Assert.Equal("75.00%", family.InclusionPercent);
        });

        var familyA = odds.Families[0];
        Assert.Equal("Family A", familyA.FamilyLabel);
        Assert.Collection(
            familyA.Lots,
            lot => AssertLot(lot, "core", "Base Game", "a-core", "1", "4", "25.00%"),
            lot => AssertLot(lot, "provider-z", "Provider Z", "a-light", "3", "16", "18.75%"),
            lot => AssertLot(lot, "provider-z", "Provider Z", "z-heavy", "9", "16", "56.25%"));
    }

    [Fact]
    public void Create_PublishesTheSameAnchorTemplateIdAsTheManifestLotSnapshotForTheSameLot()
    {
        var lot = Lot("core", "field-cache", "family-a", RewardRarity.ScavGrade);
        var catalog = Snapshot([
            lot,
            Lot("core", "family-b-lot", "family-b", RewardRarity.Contractor),
            Lot("core", "family-c-lot", "family-c", RewardRarity.Restricted)
        ]);

        var odds = ManifestOpeningOdds.Create(catalog);

        var publishedLot = odds.Families
            .Single(family => family.FamilyId == "family-a")
            .Lots
            .Single();
        Assert.Equal(lot.Identity.AnchorTemplateId, publishedLot.AnchorTemplateId);
        Assert.Equal("template-field-cache", publishedLot.AnchorTemplateId);
    }

    [Fact]
    public void Create_UsesTheExactIeeeWeightRatiosAndReducesThem()
    {
        var lots = new[]
        {
            Lot("provider-a", "lot-a", "family-a", RewardRarity.ScavGrade),
            Lot("provider-b", "lot-b", "family-a", RewardRarity.Contractor),
            Lot("provider-a", "lot-c", "family-b", RewardRarity.Restricted),
            Lot("provider-a", "lot-d", "family-c", RewardRarity.BlackLabel)
        };
        var catalog = Snapshot(
            lots,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["provider-a"] = 0.1d,
                ["provider-b"] = 0.3d
            });

        var family = ManifestOpeningOdds.Create(catalog).Families[0];

        Assert.Collection(
            family.Lots,
            first =>
            {
                Assert.Equal("3602879701896397", first.ConditionalNumerator);
                Assert.Equal("14411518807585587", first.ConditionalDenominator);
                Assert.Equal("25.00%", first.ConditionalPercent);
            },
            second =>
            {
                Assert.Equal("10808639105689190", second.ConditionalNumerator);
                Assert.Equal("14411518807585587", second.ConditionalDenominator);
                Assert.Equal("75.00%", second.ConditionalPercent);
            });
    }

    [Fact]
    public void Create_RejectsAnUnboundedFamilyTable()
    {
        var lots = Enumerable.Range(0, ManifestOpeningOddsData.MaximumLotsPerFamily + 1)
            .Select(index => Lot(
                "provider-a",
                $"family-a-{index:D4}",
                "family-a",
                RewardRarity.ScavGrade))
            .Append(Lot("provider-a", "family-b", "family-b", RewardRarity.Contractor))
            .Append(Lot("provider-a", "family-c", "family-c", RewardRarity.Restricted));

        var exception = Assert.Throws<CargoCatalogValidationException>(() =>
            ManifestOpeningOdds.Create(Snapshot(lots)));

        Assert.Equal(
            "Opening-odds family 'family-a' exceeds the supported lot count.",
            exception.Message);
    }

    [Fact]
    public void CurrentState_IsOpeningOddsOnlyWithoutAnActiveManifest()
    {
        var catalog = Snapshot(
        [
            Lot("provider-a", "lot-a", "family-a", RewardRarity.ScavGrade),
            Lot("provider-a", "lot-b", "family-b", RewardRarity.Contractor),
            Lot("provider-a", "lot-c", "family-c", RewardRarity.Restricted)
        ]);

        var state = ManifestSnapshotRouter.CreateCurrentState(
            new CaseOpeningJournal(),
            catalog,
            locale: null);

        Assert.Null(state.Snapshot);
        Assert.NotNull(state.OpeningOdds);
        Assert.Equal(catalog.SnapshotId, state.OpeningOdds.CatalogSnapshotId);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(state));
        var properties = document.RootElement.EnumerateObject().ToArray();
        Assert.Equal(["snapshot", "openingOdds"], properties.Select(property => property.Name));
        Assert.Equal(JsonValueKind.Null, properties[0].Value.ValueKind);
        Assert.Equal(JsonValueKind.Object, properties[1].Value.ValueKind);
    }

    [Fact]
    public void CurrentState_FailsClosedWithoutAnActiveManifestOrCatalog()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ManifestSnapshotRouter.CreateCurrentState(
                new CaseOpeningJournal(),
                catalog: null,
                locale: null));

        Assert.Equal("The current Manifest catalog is unavailable.", exception.Message);
    }

    [Fact]
    public void HistoricalSnapshotEnvelope_RemainsSnapshotOnly()
    {
        var envelope = new ManifestSnapshotEnvelope
        {
            Snapshot = new ManifestSnapshotData { ManifestId = "manifest-history" }
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
        var properties = document.RootElement.EnumerateObject().ToArray();

        Assert.Single(properties);
        Assert.Equal("snapshot", properties[0].Name);
        Assert.Equal("manifest-history", properties[0].Value.GetProperty("manifestId").GetString());
        Assert.False(document.RootElement.TryGetProperty("openingOdds", out _));
    }

    [Fact]
    public void OpeningOdds_DoNotContainDrawsFingerprintsOrSelectedOffers()
    {
        var odds = ManifestOpeningOdds.Create(Snapshot(
        [
            Lot("provider-a", "lot-a", "family-a", RewardRarity.ScavGrade),
            Lot("provider-a", "lot-b", "family-b", RewardRarity.Contractor),
            Lot("provider-a", "lot-c", "family-c", RewardRarity.Restricted)
        ]));

        var json = JsonSerializer.Serialize(odds);

        Assert.DoesNotContain("fingerprint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rng", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("selected", json, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertLot(
        ManifestOpeningLotOddsData lot,
        string providerId,
        string providerLabel,
        string lotId,
        string numerator,
        string denominator,
        string percent)
    {
        Assert.Equal(providerId, lot.ProviderId);
        Assert.Equal(providerLabel, lot.ProviderLabel);
        Assert.Equal(lotId, lot.LotId);
        Assert.Equal($"template-{lotId}", lot.AnchorTemplateId);
        Assert.Equal(numerator, lot.ConditionalNumerator);
        Assert.Equal(denominator, lot.ConditionalDenominator);
        Assert.Equal(percent, lot.ConditionalPercent);
    }

    private static CargoCatalogSnapshot Snapshot(
        IEnumerable<ResolvedCargoLot> lots,
        IReadOnlyDictionary<string, double>? providerWeights = null)
    {
        var snapshot = lots.ToArray();
        return new CargoCatalogSnapshot(
            new string('a', 64),
            snapshot,
            [],
            providerWeights ?? snapshot
                .Select(lot => lot.Identity.ProviderId)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(provider => provider, _ => 1d, StringComparer.Ordinal));
    }

    private static ResolvedCargoLot Lot(
        string providerId,
        string lotId,
        string familyId,
        RewardRarity rarity,
        double weight = 1d)
    {
        var templateId = $"template-{lotId}";
        var definition = new CargoLotDefinition(
            providerId,
            "1.0.0",
            lotId,
            $"Display {lotId}",
            $"Purpose {lotId}",
            new FamilyId(familyId),
            new TrackId($"track-{familyId}"),
            templateId,
            weight,
            new RaidRole("testing"),
            [new TemplateLine(templateId, 1, 1)]);
        var forest = RewardForest.Create(
            [new RewardForestNode("root", "root", templateId, null, null, null, 1)]);
        var fingerprint = RewardForestFingerprintV2.Compute(providerId, lotId, forest);
        return new ResolvedCargoLot(
            definition,
            forest,
            fingerprint,
            CargoLotIdentitySnapshot.Capture(definition, fingerprint),
            new CargoLotEvaluation(100_000, 100_000, 1, rarity));
    }
}
