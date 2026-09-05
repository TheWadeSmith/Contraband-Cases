using System.Text.Json;
using ContrabandCases.Client.Opening;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Tests.Server;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class ManifestLibraryTests
{
    [Fact]
    public void Library_roundtrip_links_real_cases_to_only_their_public_packages()
    {
        var a = CaseCatalogTests.Lot("core", "field-supply", "meds");
        var b = CaseCatalogTests.Lot("cards", "collections", "cards", collection: true);
        var catalog = CaseCatalogTests.Snapshot([a, b]);
        var cases = new Dictionary<string, CargoCatalogSnapshot?>
        {
            [CaseContracts.Operations] = CaseCatalogTests.Snapshot([a]),
            [CaseContracts.Relics] = CaseCatalogTests.Snapshot([b])
        };
        var data = ManifestLibraryProjection.Create(new CaseOpeningJournal(), catalog, new Dictionary<string, string>(), caseCatalogs: cases);
        var library = ManifestSnapshotParser.ParseLibrary(JsonSerializer.Serialize(new { err = 0, errmsg = (string?)null, data }));
        Assert.Equal("meds", Assert.Single(BrokerLibraryFilter.Lots(library, CaseContracts.Operations, "")).LotId);
        Assert.Equal("cards", Assert.Single(BrokerLibraryFilter.Lots(library, CaseContracts.Relics, "")).LotId);
    }

    [Fact]
    public void Case_membership_never_references_a_package_absent_from_public_projection()
    {
        var a = CaseCatalogTests.Lot("core", "field-supply", "meds");
        var b = CaseCatalogTests.Lot("core", "field-supply", "other");
        var data = ManifestLibraryProjection.Create(new CaseOpeningJournal(), CaseCatalogTests.Snapshot([a]),
            new Dictionary<string, string>(), caseCatalogs: new Dictionary<string, CargoCatalogSnapshot?>
            { [CaseContracts.Operations] = CaseCatalogTests.Snapshot([a, b]) });
        Assert.Equal(new[] { "core/meds" }, Assert.Single(data.Cases).LotIds);
        _ = ManifestSnapshotParser.ParseLibrary(JsonSerializer.Serialize(new { err = 0, errmsg = (string?)null, data }));
    }

    [Theory]
    [InlineData("unknown-case")]
    [InlineData("duplicate-case")]
    [InlineData("unknown-lot")]
    [InlineData("duplicate-lot")]
    [InlineData("null-lot")]
    [InlineData("extra-property")]
    [InlineData("non-string-reference")]
    public void Library_rejects_malformed_case_links(string mutation)
    {
        var catalog = CaseCatalogTests.Snapshot([CaseCatalogTests.Lot("core", "field-supply", "meds")]);
        var data = ManifestLibraryProjection.Create(new CaseOpeningJournal(), catalog, new Dictionary<string, string>(),
            caseCatalogs: new Dictionary<string, CargoCatalogSnapshot?> { [CaseContracts.Operations] = catalog });
        var json = JObject.Parse(JsonSerializer.Serialize(new { err = 0, errmsg = (string?)null, data }));
        var root = (JObject)json["data"]!;
        var cases = (JArray)root["cases"]!;
        var first = (JObject)cases[0]!;
        var ids = (JArray)first["lotIds"]!;
        switch (mutation)
        {
            case "unknown-case": first["templateId"] = "unknown"; break;
            case "duplicate-case": cases.Add(first.DeepClone()); break;
            case "unknown-lot": ids[0] = "core/unknown"; break;
            case "duplicate-lot": ids.Add(ids[0].DeepClone()); break;
            case "null-lot": ((JArray)root["lots"]!).Add(JValue.CreateNull()); break;
            case "extra-property": first["futureOffers"] = new JArray(); break;
            case "non-string-reference": ids[0] = 123; break;
        }
        Assert.Throws<ManifestSnapshotException>(() => ManifestSnapshotParser.ParseLibrary(json.ToString()));
    }

    [Fact]
    public void Old_library_protocol_keeps_pending_state_unknown_instead_of_inventing_none()
    {
        var library = ManifestSnapshotParser.ParseLibrary("{\"err\":0,\"errmsg\":null,\"data\":{\"protocolVersion\":1,\"dossier\":\"History\",\"lots\":[]}}");
        Assert.Null(library.HasPending);
        Assert.Empty(library.Cases);
        Assert.Contains("updated server", library.Status);
    }

    [Fact]
    public void Dossier_roundtrips_with_saved_favor_even_when_catalog_is_missing()
    {
        var journal = new CaseOpeningJournal(recoveryMeter: 2);
        var data = ManifestLibraryProjection.Create(journal, null, new Dictionary<string, string>());
        var json = JsonSerializer.Serialize(new { err = 0, errmsg = (string?)null, data });
        var parsed = ManifestSnapshotParser.ParseLibrary(json);
        Assert.Contains("BROKER FAVOR 2/3", parsed.Dossier);
        Assert.Contains("No settled manifests", parsed.Dossier);
        Assert.Empty(parsed.Lots);
        Assert.Equal(2, journal.BrokerFavor);
        Assert.Null(journal.ActiveManifest);
        Assert.False(parsed.HasPending);
        Assert.Contains("SERVER VERSION", parsed.Status);
    }

    [Theory]
    [InlineData("unknown", 1)]
    [InlineData("protocolVersion", 9)]
    public void Library_rejects_unknown_or_unsupported_protocol_fields(string field, int value)
    {
        var envelope = JObject.Parse("{\"err\":0,\"errmsg\":null,\"data\":{\"protocolVersion\":1,\"dossier\":\"hello\",\"lots\":[]}}");
        envelope["data"]![field] = value;
        Assert.Throws<ManifestSnapshotException>(() => ManifestSnapshotParser.ParseLibrary(envelope.ToString()));
    }

    [Fact]
    public void Gallery_filters_by_real_grade_and_retains_exact_item_contents()
    {
        var lot = Lot(RewardRarity.Uncommon);
        Assert.True(ManifestGallery.Matches(lot, "core/meds", GalleryRarity.Uncommon));
        Assert.False(ManifestGallery.Matches(lot, "", GalleryRarity.Legendary));
        Assert.False(ManifestGallery.Matches(lot, "absent", GalleryRarity.Any));
        var preview = ManifestGallery.Create(lot, GalleryState.Offer2, longName: true);
        Assert.Equal(lot.Contents, preview.CurrentLot!.Contents);
        Assert.Equal(lot.Fingerprint, preview.CurrentLot.Fingerprint);
        Assert.Equal(2, preview.CurrentOrdinal);
        Assert.NotEqual(lot.DisplayName, preview.CurrentLot.DisplayName);
    }

    [Fact]
    public void Every_gallery_state_handles_every_supported_rarity_without_regrading_lots()
    {
        foreach (var grade in Enum.GetValues<RewardRarity>())
            foreach (var state in Enum.GetValues<GalleryState>())
            {
                var preview = ManifestGallery.Create(Lot(grade), state, false);
                if (preview.CurrentLot is not null) Assert.Equal(grade, preview.CurrentLot.Grade);
                if (grade == RewardRarity.BlackLabel) Assert.False(preview.AvailableActions.CanRelay);
                if (state == GalleryState.Confiscated) Assert.Equal(ManifestPhase.Confiscated, preview.Phase);
                if (state == GalleryState.Replacement) Assert.True(preview.RelayTerminal);
            }
    }

    [Fact]
    public void Terminal_gallery_retains_decorated_display_lot_without_changing_snapshot_shape()
    {
        foreach (var state in new[] { GalleryState.Claimed, GalleryState.Confiscated })
        {
            var original = Lot(RewardRarity.Restricted);
            var display = ManifestGallery.DisplayLot(original, longName: true);
            var preview = ManifestGallery.Create(display, state, longName: false);
            Assert.Null(preview.CurrentLot);
            Assert.True(display.DisplayName.Length > original.DisplayName.Length);
            Assert.Equal(original.Fingerprint, display.Fingerprint);
            Assert.Equal(original.Contents, display.Contents);
        }
    }

    private static ManifestLotSnapshot Lot(RewardRarity grade) => new("core", "Base Game", "meds", "Medical kit",
        "Test", "field-supply", "medical", grade, new string('a', 24), new string('b', 64),
        50_000, 50_000, 2, [new ManifestLotContentSnapshot(new string('a', 24), "Bandage", 2)]);

    [Fact]
    public void Readonly_browser_filters_by_authoritative_case_membership_and_category()
    {
        var lot = Lot(RewardRarity.Uncommon);
        var library = new ManifestLibrarySnapshot("", [lot], false, "", new Dictionary<string, IReadOnlyList<string>>
        {
            [CaseContracts.Operations] = ["core/meds"], [CaseContracts.Relics] = []
        });
        Assert.Same(lot, Assert.Single(BrokerLibraryFilter.Lots(library, CaseContracts.Operations, "field-supply")));
        Assert.Empty(BrokerLibraryFilter.Lots(library, CaseContracts.Relics, ""));
        Assert.Empty(BrokerLibraryFilter.Lots(library, CaseContracts.Operations, "arsenal"));
        Assert.Empty(BrokerLibraryFilter.Lots(library, "unknown", ""));
    }

    [Theory]
    [InlineData("hasPending", "false")]
    [InlineData("status", 123)]
    public void Library_v2_rejects_malformed_status(string field, object invalid)
    {
        var data = ManifestLibraryProjection.Create(new CaseOpeningJournal(), null, new Dictionary<string, string>());
        var json = JObject.Parse(JsonSerializer.Serialize(new { err = 0, errmsg = (string?)null, data }));
        json["data"]![field] = JToken.FromObject(invalid);
        Assert.Throws<ManifestSnapshotException>(() => ManifestSnapshotParser.ParseLibrary(json.ToString()));
    }
}
