using System.Text.Json;
using ContrabandCases.Server.Catalog;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Tests.Server;
using Xunit;
using ClientEnvelope = ContrabandCases.Client.Opening.ManifestSnapshotEnvelope;

namespace ContrabandCases.Tests.Settlement;

public sealed class ThemedManifestTests
{
    private static readonly DateTimeOffset PreparedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlackSite_accepts_and_displays_both_legacy_families_and_new_track_sequences(bool legacy)
    {
        var lots = Enumerable.Range(0, 3).Select(i => ManifestOpeningPoolTests.Lot("lot-" + i, 100_000,
            family: legacy ? "family-" + i : "field-supply", track: legacy ? "same-track" : "track-" + i, provider: "vault")).ToArray();
        var catalog = CaseCatalogTests.Snapshot(lots);
        var offers = lots.Select((lot, i) => new ManifestOfferSnapshot(i + 1, lot.Evaluation.Grade,
            lot.Identity, lot.Forest, lot.Fingerprint, new CanonicalRngEvidence(ManifestRngPurpose.OfferSelection, i + 1, 0))).ToArray();
        var ticket = new ManifestTicketPayload("000000000000000000000001", "000000000000000000000002",
            PreparedAt, false, false, null, caseTemplateId: CaseContracts.BlackSite)
            .WithCommitPlan(1, ManifestInputCommitWitness.GenesisHash);
        var active = new ManifestRecord("blacksite-recovery", catalog.SnapshotId,
            ManifestCommitmentEvidence.CreateWithRandomNonce("blacksite-recovery", catalog.SnapshotId, offers),
            ManifestFlowState.PrepareTicket(), ticket, offers, null, null, null, null, 0)
            .BeginTicketProfileCommit().ActivateTicket(PreparedAt.AddMinutes(1));
        for (var ordinal = 1; ordinal <= 3; ordinal++)
        {
            active = RoundTrip(new CaseOpeningJournal(activeManifest: active)).ActiveManifest!;
            var parsed = Parse(ManifestSnapshotProjection.FromActive(active, catalog, null));
            Assert.Equal(legacy ? "family-" + (ordinal - 1) : "track-" + (ordinal - 1), parsed.FamilySeals[ordinal - 1].FamilyId);
            if (ordinal < 3) active = active.DecideOffer(ManifestOfferDecision.Burn, PreparedAt.AddMinutes(ordinal + 1));
        }
        var receipt = active.ForfeitMissingContent(PreparedAt.AddMinutes(5)).TerminalReceipt!;
        Assert.Equal(3, Parse(ManifestSnapshotProjection.FromTerminal(receipt)).FamilySeals.Select(s => s.FamilyId).Distinct().Count());
    }

    [Theory]
    [InlineData(ModConstants.CaseTemplateId)]
    [InlineData(CaseContracts.Operations)]
    [InlineData(CaseContracts.Relics)]
    [InlineData(CaseContracts.BlackSite)]
    public void Ticket_journal_decisions_and_terminal_receipt_preserve_case_identity(string template)
    {
        var (prepared, catalog) = Prepare(template);
        var journal = RoundTrip(new CaseOpeningJournal(activeManifest: prepared));
        Assert.Equal(template, journal.ActiveManifest!.Ticket.CaseTemplateId);
        var active = journal.ActiveManifest.BeginTicketProfileCommit();
        journal.ReplaceActiveManifest(active);
        active = active.ActivateTicket(PreparedAt.AddMinutes(1));
        journal.ReplaceActiveManifest(active);

        for (var ordinal = 1; ordinal <= 3; ordinal++)
        {
            journal = RoundTrip(journal);
            active = journal.ActiveManifest!;
            Assert.Equal(template, active.Ticket.CaseTemplateId);
            var projection = ManifestSnapshotProjection.FromActive(active, catalog, null);
            var parsed = Parse(projection);
            Assert.Equal(ordinal, parsed.CurrentOrdinal);
            Assert.Equal(template, parsed.CaseTemplateId);
            Assert.Equal(ordinal, parsed.FamilySeals.Count(s => s.Revealed));
            if (template == CaseContracts.Relics)
                Assert.Equal(parsed.CurrentLot!.TrackId, parsed.FamilySeals[ordinal - 1].FamilyId);
            if (ordinal < 3)
                journal.ReplaceActiveManifest(active.DecideOffer(ManifestOfferDecision.Burn,
                    PreparedAt.AddMinutes(ordinal + 1)));
        }

        var terminal = active.ForfeitMissingContent(PreparedAt.AddMinutes(5));
        journal.ReplaceActiveManifest(terminal);
        journal.FinishActiveManifest();
        journal = RoundTrip(journal);
        Assert.Null(journal.ActiveManifest);
        var receipt = Assert.Single(journal.ManifestReceipts);
        Assert.Equal(template, receipt.CaseTemplateId);
        Assert.Equal(template, Parse(ManifestSnapshotProjection.FromTerminal(receipt)).CaseTemplateId);
        Assert.Equal(prepared.Offers.Select(o => o.Fingerprint), receipt.Offers.Select(o => o.Fingerprint));
    }

    [Fact]
    public void Legacy_absent_case_fields_default_to_mixed()
    {
        var (prepared, _) = Prepare(ModConstants.CaseTemplateId);
        var document = SptCaseJournal.ToDocument(new CaseOpeningJournal(activeManifest: prepared));
        document.ActiveManifest!.Ticket!.CaseTemplateId = null;
        Assert.Equal(ModConstants.CaseTemplateId,
            SptCaseJournal.FromDocument(document).ActiveManifest!.Ticket.CaseTemplateId);

        var receipt = prepared.BeginTicketProfileCommit().ActivateTicket(PreparedAt.AddMinutes(1))
            .ForfeitMissingContent(PreparedAt.AddMinutes(2)).TerminalReceipt!;
        document = SptCaseJournal.ToDocument(new CaseOpeningJournal(manifestReceipts: [receipt]));
        document.ManifestReceipts![0].CaseTemplateId = null;
        Assert.Equal(ModConstants.CaseTemplateId,
            Assert.Single(SptCaseJournal.FromDocument(document).ManifestReceipts).CaseTemplateId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    public void Invalid_persisted_case_identity_is_rejected(string invalid)
    {
        var (prepared, _) = Prepare(CaseContracts.Operations);
        var document = SptCaseJournal.ToDocument(new CaseOpeningJournal(activeManifest: prepared));
        document.ActiveManifest!.Ticket!.CaseTemplateId = invalid;
        Assert.ThrowsAny<ArgumentException>(() => SptCaseJournal.FromDocument(document));
        var receipt = prepared.BeginTicketProfileCommit().ActivateTicket(PreparedAt.AddMinutes(1))
            .ForfeitMissingContent(PreparedAt.AddMinutes(2)).TerminalReceipt!;
        document = SptCaseJournal.ToDocument(new CaseOpeningJournal(manifestReceipts: [receipt]));
        document.ManifestReceipts![0].CaseTemplateId = invalid;
        Assert.ThrowsAny<ArgumentException>(() => SptCaseJournal.FromDocument(document));
    }

    [Fact]
    public void Journal_replacement_cannot_substitute_the_case_type()
    {
        var (prepared, _) = Prepare(CaseContracts.Operations);
        var journal = new CaseOpeningJournal(activeManifest: prepared);
        var document = SptCaseJournal.ToDocument(journal);
        document.ActiveManifest!.Ticket!.CaseTemplateId = CaseContracts.BlackSite;
        var rewritten = SptCaseJournal.FromDocument(document).ActiveManifest!;
        Assert.Throws<InvalidOperationException>(() => journal.ReplaceActiveManifest(rewritten));
        Assert.Same(prepared, journal.ActiveManifest);
    }

    private static CaseOpeningJournal RoundTrip(CaseOpeningJournal journal) =>
        SptCaseJournal.FromDocument(JsonSerializer.Deserialize<SptCaseJournalDocument>(
            JsonSerializer.Serialize(SptCaseJournal.ToDocument(journal)))!);

    private static ContrabandCases.Client.Opening.ManifestSnapshot Parse(ManifestSnapshotData data) =>
        ClientEnvelope.Parse(JsonSerializer.Serialize(new
        {
            err = 0, errmsg = (string?)null,
            data = new ManifestSnapshotEnvelope { Snapshot = data }
        }), data.ManifestId);

    private static (ManifestRecord, CargoCatalogSnapshot) Prepare(string template)
    {
        var relics = template == CaseContracts.Relics;
        var lots = relics
            ? new[] { "anime", "pokemon", "yugioh" }.Select(series =>
                CaseCatalogTests.Lot($"krackasourus.{series}-cards", "field-supply", $"{series}-cards", true))
            : new[] { "arsenal", "operator", "field-supply" }.Select(family =>
                CaseCatalogTests.Lot(template == CaseContracts.BlackSite ? "vault" : "core", family, family));
        var catalog = CaseCatalogs.ForCase(CaseCatalogTests.Snapshot(lots), template);
        var offers = new ManifestCatalogSelector(() => 0).CreateOffers(catalog);
        var ticket = new ManifestTicketPayload("000000000000000000000001", "000000000000000000000002",
            PreparedAt, false, false, null, caseTemplateId: template)
            .WithCommitPlan(1, ManifestInputCommitWitness.GenesisHash);
        return (new ManifestRecord("themed-test", catalog.SnapshotId,
            ManifestCommitmentEvidence.CreateWithRandomNonce("themed-test", catalog.SnapshotId, offers),
            ManifestFlowState.PrepareTicket(), ticket, offers, null, null, null, null, 0), catalog);
    }
}
