using ContrabandCases.Client.Opening;
using ContrabandCases.Server.Settlement;
using Newtonsoft.Json.Linq;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Xunit;
using ClientEnvelope = ContrabandCases.Client.Opening.ManifestSnapshotEnvelope;

namespace ContrabandCases.Tests.Client;

public sealed class LegacyOpeningProtocolTests
{
    [Fact]
    public void Broker_exposes_pending_legacy_recovery_without_disclosing_its_saved_prize()
    {
        var record = Record();
        var library = ManifestLibraryProjection.Create(new CaseOpeningJournal([record]), null, new Dictionary<string, string>());
        Assert.True(library.HasPending);
        Assert.Contains("legacy", library.Dossier, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(record.CaseId.ToString(), library.Dossier);
        Assert.DoesNotContain(record.RewardId, library.Dossier);
    }

    [Fact]
    public void Legacy_recovery_authority_is_limited_to_exact_pending_case_and_not_read_only_gallery()
    {
        var id = new MongoId().ToString();
        var pending = new LegacyOpeningSnapshot(id, "old-reward", false, false);
        LegacyOpeningPresentation.RequireResumeAuthority(false, pending, id);
        Assert.Throws<InvalidOperationException>(() => LegacyOpeningPresentation.RequireResumeAuthority(true, pending, id));
        Assert.Throws<InvalidOperationException>(() => LegacyOpeningPresentation.RequireResumeAuthority(false, pending, new MongoId().ToString()));
        Assert.Throws<InvalidOperationException>(() => LegacyOpeningPresentation.RequireResumeAuthority(false, new LegacyOpeningSnapshot(id, "old-reward", true, true), id));
        Assert.Throws<InvalidOperationException>(() => LegacyOpeningPresentation.RequireResumeAuthority(false, null!, id));
        Assert.Contains("SENT TO MESSENGER", LegacyOpeningPresentation.Summary(new(id, "old-reward", true, true)));
        Assert.Contains("PREVIOUSLY DELIVERED", LegacyOpeningPresentation.Summary(new(id, "old-reward", true, false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_opening_current_response_is_explicit_not_fabricated_manifest(bool committed)
    {
        var record = Record();
        if (committed) record = record.Commit(DateTimeOffset.UnixEpoch.AddMinutes(1));
        var response = ManifestSnapshotRouter.CreateCurrentState(new CaseOpeningJournal([record]), null, null,
            requestedCaseId: record.CaseId.ToString());
        var json = System.Text.Json.JsonSerializer.Serialize(new { err = 0, errmsg = (string?)null, data = response });
        var parsed = ClientEnvelope.ParseCurrent(json);
        Assert.Null(parsed.Snapshot);
        Assert.Null(parsed.OpeningOdds);
        Assert.Equal(record.CaseId.ToString(), parsed.LegacyOpening!.CaseId);
        Assert.Equal(committed, parsed.LegacyOpening.Committed);
        Assert.False(parsed.LegacyOpening.DeliveredToMessenger);
    }

    [Theory]
    [InlineData("caseId", "\"invalid\"")]
    [InlineData("rewardId", "\"\"")]
    [InlineData("committed", "1")]
    [InlineData("deliveredToMessenger", "\"true\"")]
    public void Legacy_opening_receipt_rejects_invalid_fields(string field, string raw)
    {
        var data = new JObject { ["snapshot"] = null, ["openingOdds"] = null,
            ["legacyOpening"] = new JObject { ["caseId"] = new MongoId().ToString(), ["rewardId"] = "old-reward",
                ["committed"] = true, ["deliveredToMessenger"] = true } };
        data["legacyOpening"]![field] = JToken.Parse(raw);
        Assert.Throws<ManifestSnapshotException>(() => ClientEnvelope.ParseCurrent(new JObject { ["err"] = 0, ["errmsg"] = null, ["data"] = data }.ToString()));
    }

    private static CaseOpeningRecord Record() => new(new MongoId(), new MongoId(), "old-reward",
        [new Item { Id = new MongoId(), Template = new MongoId() }], DateTimeOffset.UnixEpoch,
        OpeningRecordStatus.Prepared, null);
}
