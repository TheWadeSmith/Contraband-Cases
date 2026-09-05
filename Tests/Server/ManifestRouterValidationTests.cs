using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Manifest;
using SPTarkov.Server.Core.Models.Common;
using Xunit;

namespace ContrabandCases.Tests.Server;

public sealed class ManifestRouterValidationTests
{
    private const string ManifestId = "0123456789abcdef01234567";
    private static readonly MongoId CaseId = (MongoId)"111111111111111111111111";
    private static readonly MongoId KeyId = (MongoId)"222222222222222222222222";

    [Fact]
    public void Decision_routes_require_exact_action_id_and_offer_precondition()
    {
        var lockRequest = Decision(ModConstants.ManifestLockAction, ordinal: 1);
        var burnRequest = Decision(ModConstants.ManifestBurnAction, ordinal: 2);

        Assert.Equal(
            ManifestOfferDecision.Lock,
            ContrabandCaseRouter.ValidateManifestDecisionRequest(
                ModConstants.ManifestLockAction,
                lockRequest));
        Assert.Equal(
            ManifestOfferDecision.Burn,
            ContrabandCaseRouter.ValidateManifestDecisionRequest(
                ModConstants.ManifestBurnAction,
                burnRequest));

        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateManifestDecisionRequest(
                ModConstants.ManifestBurnAction,
                lockRequest));
        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateManifestDecisionRequest(
                ModConstants.ManifestLockAction,
                Decision(ModConstants.ManifestLockAction, ordinal: 3)));
        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateManifestDecisionRequest(
                ModConstants.ManifestLockAction,
                Decision(ModConstants.ManifestLockAction, ordinal: 1, manifestId: "ABCDEF0123456789ABCDEF01")));
    }

    [Theory]
    [InlineData(nameof(ManifestPhase.Entitlement))]
    [InlineData(nameof(ManifestPhase.ClaimPrepared))]
    [InlineData(nameof(ManifestPhase.RewardOwed))]
    public void Claim_route_accepts_only_exact_claim_phases(string phase)
    {
        var request = new ManifestClaimRequestData
        {
            Action = ModConstants.ManifestClaimAction,
            ManifestId = ManifestId,
            ExpectedPhase = phase
        };

        Assert.Equal(
            Enum.Parse<ManifestPhase>(phase),
            ContrabandCaseRouter.ValidateManifestClaimRequest(
                ModConstants.ManifestClaimAction,
                request));
    }

    [Fact]
    public void Claim_route_rejects_stale_action_and_noncanonical_phase_text()
    {
        var request = new ManifestClaimRequestData
        {
            Action = ModConstants.ManifestClaimAction,
            ManifestId = ManifestId,
            ExpectedPhase = "entitlement"
        };

        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateManifestClaimRequest(
                ModConstants.ManifestClaimAction,
                request));
        request.ExpectedPhase = nameof(ManifestPhase.Entitlement);
        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateManifestClaimRequest(
                ModConstants.ManifestRelayAction,
                request));
    }

    [Theory]
    [InlineData(nameof(ManifestPhase.Entitlement), 1)]
    [InlineData(nameof(ManifestPhase.RelayPrepared), 3)]
    public void Relay_route_authenticates_phase_and_stage(string phase, int stage)
    {
        var request = new ManifestRelayRequestData
        {
            Action = ModConstants.ManifestRelayAction,
            ManifestId = ManifestId,
            ExpectedPhase = phase,
            ExpectedRelayStage = stage
        };

        var validated = ContrabandCaseRouter.ValidateManifestRelayRequest(
            ModConstants.ManifestRelayAction,
            request);

        Assert.Equal(Enum.Parse<ManifestPhase>(phase), validated.ExpectedPhase);
        Assert.Equal(stage, validated.ExpectedRelayStage);
    }

    [Fact]
    public void Relay_route_rejects_out_of_range_stage()
    {
        var request = new ManifestRelayRequestData
        {
            Action = ModConstants.ManifestRelayAction,
            ManifestId = ManifestId,
            ExpectedPhase = nameof(ManifestPhase.Entitlement),
            ExpectedRelayStage = 4
        };

        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateManifestRelayRequest(
                ModConstants.ManifestRelayAction,
                request));
    }

    [Theory]
    [InlineData(nameof(ManifestPhase.Offer1))]
    [InlineData(nameof(ManifestPhase.Offer2))]
    [InlineData(nameof(ManifestPhase.Entitlement))]
    public void Forfeit_route_accepts_only_missing_content_source_phases(string phase)
    {
        var request = new ManifestForfeitRequestData
        {
            Action = ModConstants.ManifestForfeitAction,
            ManifestId = ManifestId,
            ExpectedPhase = phase
        };

        Assert.Equal(
            Enum.Parse<ManifestPhase>(phase),
            ContrabandCaseRouter.ValidateManifestForfeitRequest(
                ModConstants.ManifestForfeitAction,
                request));
    }

    [Fact]
    public void Manifest_routes_reject_unpublished_fields()
    {
        var request = Decision(ModConstants.ManifestLockAction, ordinal: 1);
        request.ExtensionData = new()
        {
            ["rewardId"] = "client-selected-reward"
        };

        Assert.Throws<InvalidOperationException>(() =>
            ContrabandCaseRouter.ValidateManifestDecisionRequest(
                ModConstants.ManifestLockAction,
                request));
    }

    [Fact]
    public void Open_dispatch_preserves_prepared_and_committed_legacy_records()
    {
        var prepared = LegacyRecord(OpeningRecordStatus.Prepared);
        var committed = LegacyRecord(OpeningRecordStatus.Committed);

        Assert.True(ContrabandCaseRouter.ShouldUseLegacyOpening(
            new CaseOpeningJournal(records: [prepared]),
            CaseId));
        Assert.True(ContrabandCaseRouter.ShouldUseLegacyOpening(
            new CaseOpeningJournal(records: [committed]),
            CaseId));
        Assert.False(ContrabandCaseRouter.ShouldUseLegacyOpening(
            new CaseOpeningJournal(),
            CaseId));
    }

    private static ManifestDecisionRequestData Decision(
        string action,
        int ordinal,
        string manifestId = ManifestId) =>
        new()
        {
            Action = action,
            ManifestId = manifestId,
            ExpectedOrdinal = ordinal
        };

    private static CaseOpeningRecord LegacyRecord(OpeningRecordStatus status) =>
        new(
            CaseId,
            KeyId,
            "legacy-reward",
            [],
            DateTimeOffset.UnixEpoch,
            status,
            status == OpeningRecordStatus.Committed
                ? DateTimeOffset.UnixEpoch.AddSeconds(1)
                : null);
}
