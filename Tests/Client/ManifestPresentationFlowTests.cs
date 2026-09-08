using ContrabandCases.Client.Opening;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using Newtonsoft.Json;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class ManifestPresentationFlowTests
{
    [Fact]
    public void Case_independent_recovery_with_no_pending_manifest_never_confirms_a_new_case()
    {
        Assert.Equal(ManifestResumeKind.NoPending, ManifestPresentationPolicy.Resume(null, recoveryOnly: true));
        Assert.Throws<InvalidOperationException>(() => ManifestPresentationPolicy.RequireDispatchAuthority(
            libraryOnly: false, recoveryOnly: true, ManifestEconomicAction.OpenTicket, before: null));
    }

    [Theory]
    [InlineData(ManifestPhase.Entitlement, ManifestResumeKind.ShowEntitlement)]
    [InlineData(ManifestPhase.RewardOwed, ManifestResumeKind.ResumeClaim)]
    [InlineData(ManifestPhase.ClaimPrepared, ManifestResumeKind.ResumeClaim)]
    [InlineData(ManifestPhase.TicketPrepared, ManifestResumeKind.ResumeTicket)]
    [InlineData(ManifestPhase.RelayPrepared, ManifestResumeKind.ResumeRelay)]
    public void Case_independent_recovery_retains_authoritative_phase_and_permits_saved_actions(
        ManifestPhase phase, ManifestResumeKind kind)
    {
        var snapshot = Snapshot(phase);
        Assert.Equal(kind, ManifestPresentationPolicy.Resume(snapshot, recoveryOnly: true));
        var action = phase == ManifestPhase.TicketPrepared ? ManifestEconomicAction.OpenTicket :
            phase == ManifestPhase.RelayPrepared ? ManifestEconomicAction.Relay : ManifestEconomicAction.Claim;
        ManifestPresentationPolicy.RequireDispatchAuthority(false, true, action, snapshot);
        Assert.False(ManifestPresentationPolicy.RequiresFreshOpeningKey(action, snapshot));
    }

    [Fact]
    public void Gallery_and_dossier_runs_remain_read_only_even_with_an_active_snapshot()
    {
        foreach (var action in Enum.GetValues<ManifestEconomicAction>())
        {
            Assert.Throws<InvalidOperationException>(() => ManifestPresentationPolicy.RequireDispatchAuthority(
                true, false, action, Snapshot(ManifestPhase.Entitlement)));
        }
    }

    [Fact]
    public void Only_fresh_openings_require_a_local_key_before_dispatch_not_ticket_recovery()
    {
        Assert.True(ManifestPresentationPolicy.RequiresFreshOpeningKey(ManifestEconomicAction.OpenTicket, null));
        Assert.False(ManifestPresentationPolicy.RequiresFreshOpeningKey(ManifestEconomicAction.OpenTicket,
            Snapshot(ManifestPhase.TicketPrepared)));
        Assert.False(ManifestPresentationPolicy.RequiresFreshOpeningKey(ManifestEconomicAction.Relay,
            Snapshot(ManifestPhase.RelayPrepared)));
    }

    [Theory]
    [InlineData(ManifestPhase.TicketPrepared, ManifestResumeKind.ResumeTicket)]
    [InlineData(ManifestPhase.Offer1, ManifestResumeKind.ShowOffer)]
    [InlineData(ManifestPhase.Offer2, ManifestResumeKind.ShowOffer)]
    [InlineData(ManifestPhase.Entitlement, ManifestResumeKind.ShowEntitlement)]
    [InlineData(ManifestPhase.ClaimPrepared, ManifestResumeKind.ResumeClaim)]
    [InlineData(ManifestPhase.RewardOwed, ManifestResumeKind.ResumeClaim)]
    [InlineData(ManifestPhase.RelayPrepared, ManifestResumeKind.ResumeRelay)]
    [InlineData(ManifestPhase.Granted, ManifestResumeKind.ShowTerminal)]
    [InlineData(ManifestPhase.Confiscated, ManifestResumeKind.ShowTerminal)]
    [InlineData(ManifestPhase.Forfeited, ManifestResumeKind.ShowTerminal)]
    public void Recovery_resumes_each_authoritative_phase_without_inventing_a_choice(
        ManifestPhase phase,
        ManifestResumeKind expected)
    {
        Assert.Equal(expected, ManifestPresentationPolicy.Resume(Snapshot(phase)));
    }

    [Fact]
    public void No_active_manifest_is_the_only_state_that_allows_new_case_confirmation()
    {
        Assert.Equal(ManifestResumeKind.ConfirmNew, ManifestPresentationPolicy.Resume(null));
    }

    [Fact]
    public void Reveal_lands_on_the_server_lot_anchor_and_uses_only_disclosed_names_or_seals()
    {
        var snapshot = Snapshot(ManifestPhase.Offer1);

        var reveal = ManifestPresentationPolicy.CreateReveal(
            snapshot,
            reducedMotion: false,
            seed: 123,
            tileCount: 36,
            landingIndex: 29,
            viewportWidth: 1200,
            tileWidth: 176,
            tileSpacing: 12);

        var landingId = reveal.Motion.Strip[reveal.Motion.LandingIndex];
        var landing = reveal.Tiles[landingId];
        Assert.Equal($"lot:{snapshot.CurrentLot!.Fingerprint}", landingId);
        Assert.Equal(snapshot.CurrentLot.AnchorTemplateId, landing.TemplateId);
        Assert.Equal(snapshot.CurrentLot.DisplayName, landing.DisplayName);
        Assert.Equal("OFFER 1 OF 3", reveal.Header);

        var disclosed = snapshot.CurrentLot.Contents
            .Select(content => content.DisplayName)
            .Append(snapshot.CurrentLot.DisplayName)
            .Concat(snapshot.FamilySeals.Select(seal => seal.FamilyLabel))
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(reveal.Tiles.Values, tile =>
            Assert.True(
                disclosed.Contains(tile.DisplayName) || tile.DisplayName.StartsWith("SEALED FAMILY ", StringComparison.Ordinal),
                $"Undisclosed tile text was fabricated: {tile.DisplayName}"));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 902)]
    [InlineData(true, 43)]
    public void Published_catalog_cards_never_change_the_committed_landing(bool reducedMotion, int seed)
    {
        var snapshot = Snapshot(ManifestPhase.Offer1);
        var catalog = OpeningOdds(Hex('c'));
        var reveal = ManifestPresentationPolicy.CreateReveal(
            snapshot, reducedMotion, seed, 36, 29, 1200, 176, 12,
            publishedCatalog: catalog);

        var landing = reveal.Tiles[reveal.Motion.Strip[reveal.Motion.LandingIndex]];
        Assert.Equal(snapshot.CurrentLot!.DisplayName, landing.DisplayName);
        Assert.Equal(snapshot.CurrentLot.AnchorTemplateId, landing.TemplateId);
        Assert.Contains(reveal.Tiles.Values, tile => tile.DisplayName == "Server Lot");
        Assert.DoesNotContain(reveal.Tiles.Keys, id => id.StartsWith("content:") || id.StartsWith("seal:"));
        Assert.All(reveal.Tiles.Values, tile => Assert.True(
            tile.DisplayName == snapshot.CurrentLot.DisplayName ||
            catalog.Families.SelectMany(f => f.Lots).Any(lot => lot.DisplayName == tile.DisplayName)));
    }

    [Fact]
    public void Reveal_tiles_carry_the_lot_provider_id_for_cosmetic_accenting_but_seals_do_not()
    {
        var snapshot = Snapshot(ManifestPhase.Offer1);

        var reveal = ManifestPresentationPolicy.CreateReveal(
            snapshot,
            reducedMotion: false,
            seed: 123,
            tileCount: 36,
            landingIndex: 29,
            viewportWidth: 1200,
            tileWidth: 176,
            tileSpacing: 12);

        var landingId = reveal.Motion.Strip[reveal.Motion.LandingIndex];
        Assert.Equal(snapshot.CurrentLot!.ProviderId, reveal.Tiles[landingId].ProviderId);
        Assert.DoesNotContain(reveal.Tiles.Keys, id => id.StartsWith("content:", StringComparison.Ordinal));
        Assert.All(snapshot.FamilySeals, seal =>
        {
            var tile = reveal.Tiles[$"seal:{seal.Ordinal}"];
            Assert.Null(tile.ProviderId);
            Assert.Null(tile.TemplateId);
            Assert.Equal("CONTENTS UNDISCLOSED", tile.Detail);
            Assert.Equal(RewardRarity.ScavGrade, tile.Grade);
        });
    }

    [Fact]
    public void Fallback_does_not_leak_winning_contents_through_forced_near_misses()
    {
        var snapshot = Snapshot(ManifestPhase.Offer1);

        var reveal = ManifestPresentationPolicy.CreateReveal(
            snapshot,
            reducedMotion: false,
            seed: 123,
            tileCount: 36,
            landingIndex: 29,
            viewportWidth: 1200,
            tileWidth: 176,
            tileSpacing: 12,
            nearMissChancePercent: 100);

        var neighborId = reveal.Motion.Strip[reveal.Motion.LandingIndex - 1];
        Assert.StartsWith("seal:", neighborId);
        Assert.Equal("CONTENTS UNDISCLOSED", reveal.Tiles[neighborId].Detail);
    }

    [Fact]
    public void A_new_offer_or_relay_output_animates_but_locking_the_same_lot_does_not()
    {
        var offer = Snapshot(ManifestPhase.Offer1, fingerprint: Hex('a'));
        var locked = Snapshot(ManifestPhase.Entitlement, fingerprint: Hex('a'));
        var relayOutput = Snapshot(ManifestPhase.Entitlement, fingerprint: Hex('b'));

        Assert.False(ManifestPresentationPolicy.ShouldRevealAfter(offer, locked, recovering: false));
        Assert.True(ManifestPresentationPolicy.ShouldRevealAfter(locked, relayOutput, recovering: false));
        Assert.False(ManifestPresentationPolicy.ShouldRevealAfter(null, offer, recovering: true));
        Assert.False(ManifestPresentationPolicy.ShouldRevealAfter(null, locked, recovering: true));
    }

    [Theory]
    [InlineData(ManifestPhase.Offer1)]
    [InlineData(ManifestPhase.Offer2)]
    [InlineData(ManifestPhase.Entitlement)]
    public void Reopening_a_saved_package_never_replays_the_spinner_but_fresh_results_still_animate(ManifestPhase phase)
    {
        var saved = Snapshot(phase);
        Assert.False(ManifestPresentationPolicy.ShouldRevealAfter(null, saved, recovering: true));
        Assert.True(ManifestPresentationPolicy.ShouldRevealAfter(null, saved, recovering: false));
    }

    [Fact]
    public void Revalidation_requires_the_same_manifest_phase_ordinal_stage_lot_and_published_action()
    {
        var expected = Snapshot(ManifestPhase.Entitlement, canClaim: true, canRelay: true);

        Assert.True(ManifestPresentationPolicy.MatchesActionPrecondition(
            expected,
            expected,
            ManifestEconomicAction.Relay));
        Assert.False(ManifestPresentationPolicy.MatchesActionPrecondition(
            expected,
            Snapshot(ManifestPhase.Entitlement, canClaim: true, canRelay: true, fingerprint: Hex('b')),
            ManifestEconomicAction.Relay));
        Assert.False(ManifestPresentationPolicy.MatchesActionPrecondition(
            expected,
            Snapshot(ManifestPhase.Entitlement, canClaim: true, canRelay: false),
            ManifestEconomicAction.Relay));
    }

    [Fact]
    public void Unchanged_entitlement_after_claim_is_explicitly_a_no_space_retry()
    {
        var before = Snapshot(ManifestPhase.Entitlement, canClaim: true);
        var after = Snapshot(ManifestPhase.Entitlement, canClaim: true);

        Assert.True(ManifestPresentationPolicy.IsClaimRetry(
            ManifestEconomicAction.Claim,
            before,
            after));
        Assert.False(ManifestPresentationPolicy.IsClaimRetry(
            ManifestEconomicAction.Relay,
            before,
            after));
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, true, false, true, true)]
    [InlineData(false, false, true, true, true)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, false)]
    public void Teardown_keeps_the_operation_alive_across_the_callback_claim_frame(
        bool callbackPending,
        bool operationPendingStage,
        bool verifyingStage,
        bool observationPending,
        bool expected)
    {
        Assert.Equal(
            expected,
            ManifestPresentationPolicy.KeepDetachedObservationAlive(
                callbackPending,
                operationPendingStage,
                verifyingStage,
                observationPending));
    }

    [Theory]
    [InlineData(ManifestPhase.TicketPrepared, ManifestEconomicAction.OpenTicket)]
    [InlineData(ManifestPhase.ClaimPrepared, ManifestEconomicAction.Claim)]
    [InlineData(ManifestPhase.RewardOwed, ManifestEconomicAction.Claim)]
    [InlineData(ManifestPhase.RelayPrepared, ManifestEconomicAction.Relay)]
    public void Manual_prepared_recovery_uses_the_endpoint_owned_by_authoritative_phase(
        ManifestPhase phase,
        ManifestEconomicAction expected)
    {
        Assert.Equal(
            expected,
            ManifestPresentationPolicy.PreparedRecoveryAction(Snapshot(phase)));
    }

    [Fact]
    public void Successful_ticket_recovery_that_remains_prepared_requires_a_manual_next_attempt()
    {
        var before = Snapshot(ManifestPhase.TicketPrepared);
        var afterSuccessfulOperation = Snapshot(ManifestPhase.TicketPrepared);

        Assert.Equal(before.ManifestId, afterSuccessfulOperation.ManifestId);
        Assert.Equal(
            ManifestPostOperationRoute.ManualPreparedRecovery,
            ManifestPresentationPolicy.AfterOperation(afterSuccessfulOperation));
        Assert.Equal(
            ManifestEconomicAction.OpenTicket,
            ManifestPresentationPolicy.PreparedRecoveryAction(afterSuccessfulOperation));
    }

    [Fact]
    public void Relay_disclosure_uses_exact_server_odds_and_makes_favor_guarantee_visible()
    {
        var ordinary = Snapshot(
            ManifestPhase.Entitlement,
            canClaim: true,
            canRelay: true,
            favor: 1,
            guarantee: false);
        var guaranteed = Snapshot(
            ManifestPhase.Entitlement,
            canClaim: true,
            canRelay: true,
            favor: 3,
            guarantee: true);

        Assert.Contains("UPGRADE 45%", ManifestPresentationPolicy.RelayOdds(ordinary));
        Assert.Contains("REPLACE 35%", ManifestPresentationPolicy.RelayOdds(ordinary));
        Assert.Contains("LOSE IT 20%", ManifestPresentationPolicy.RelayOdds(ordinary));
        Assert.Contains("GUARANTEED UPGRADE", ManifestPresentationPolicy.RelayOdds(guaranteed), StringComparison.Ordinal);
    }

    [Fact]
    public void New_manifest_preflight_dispatches_reconfirms_or_resumes_without_guessing()
    {
        var displayed = OpeningOdds("catalog-a");

        Assert.Equal(
            ManifestOpeningPreflightDecision.Dispatch,
            ManifestPresentationPolicy.OpeningPreflight(
                displayed,
                new ManifestCurrentState(null, OpeningOdds("catalog-a"))));
        Assert.Equal(
            ManifestOpeningPreflightDecision.Reconfirm,
            ManifestPresentationPolicy.OpeningPreflight(
                displayed,
                new ManifestCurrentState(null, OpeningOdds("catalog-b"))));
        Assert.Equal(
            ManifestOpeningPreflightDecision.ResumeActive,
            ManifestPresentationPolicy.OpeningPreflight(
                displayed,
                new ManifestCurrentState(Snapshot(ManifestPhase.Offer1), null)));
    }

    [Fact]
    public void Opening_confirmation_summary_leads_with_rules_and_server_percentage_text_verbatim()
    {
        var text = ManifestPresentationPolicy.OpeningSummaryText(OpeningOdds("catalog-authority"));

        Assert.Contains("HOW THIS MANIFEST WORKS", text, StringComparison.Ordinal);
        Assert.Contains("server commits 3 different families", text, StringComparison.Ordinal);
        Assert.Contains("LOCK makes the displayed lot claimable", text, StringComparison.Ordinal);
        Assert.Contains("DISCARD permanently burns that lot", text, StringComparison.Ordinal);
        Assert.Contains("Server Family", text, StringComparison.Ordinal);
        Assert.Contains("SLOT 33.33%", text, StringComparison.Ordinal);
        Assert.Contains("INCLUDED 100.00%", text, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog-authority", text, StringComparison.Ordinal);
        Assert.Contains("before any case or key is consumed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Opening_audit_disclosure_preserves_every_server_label_percentage_and_exact_ratio()
    {
        var text = ManifestPresentationPolicy.OpeningOddsText(OpeningOdds("catalog-authority"));

        Assert.Contains("FULL SERVER ODDS & AUDIT DETAILS", text, StringComparison.Ordinal);
        Assert.Contains("CATALOG SNAPSHOT", text, StringComparison.Ordinal);
        Assert.Contains("catalog-authority", text, StringComparison.Ordinal);
        Assert.Contains("Server Family", text, StringComparison.Ordinal);
        Assert.Contains("Server Provider", text, StringComparison.Ordinal);
        Assert.Contains("Server Lot", text, StringComparison.Ordinal);
        Assert.Contains("Within family: 12.34%", text, StringComparison.Ordinal);
        Assert.Contains("Slot chance: 33.33%", text, StringComparison.Ordinal);
        Assert.Contains("Included chance: 100.00%", text, StringComparison.Ordinal);
        Assert.Contains("Exact slot numerator: 1", text, StringComparison.Ordinal);
        Assert.Contains("Exact slot denominator: 3", text, StringComparison.Ordinal);
        Assert.Contains("Exact inclusion numerator: 1", text, StringComparison.Ordinal);
        Assert.Contains("Exact inclusion denominator: 1", text, StringComparison.Ordinal);
        Assert.Contains("Exact numerator: 1", text, StringComparison.Ordinal);
        Assert.Contains("Exact denominator: 1", text, StringComparison.Ordinal);
        Assert.Contains("confirmation is required again", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Offer_copy_makes_lock_and_irreversible_discard_consequences_explicit()
    {
        var first = Snapshot(ManifestPhase.Offer1);
        var second = Snapshot(ManifestPhase.Offer2);
        var blocked = Snapshot(ManifestPhase.Offer1, canBurn: false);

        Assert.Contains("Keep this reward for Messenger delivery or Relay", ManifestPresentationPolicy.OfferDecisionPrompt(first));
        Assert.Contains("Discard gives it up permanently", ManifestPresentationPolicy.OfferDecisionPrompt(first));
        Assert.Equal("DISCARD & REVEAL NEXT", ManifestPresentationPolicy.OfferDiscardActionLabel(first));
        Assert.Contains("final offer. You cannot come back", ManifestPresentationPolicy.OfferDecisionPrompt(second));
        Assert.Equal("DISCARD & REVEAL FINAL", ManifestPresentationPolicy.OfferDiscardActionLabel(second));
        Assert.Contains("no discard action will be sent", ManifestPresentationPolicy.OfferDecisionPrompt(blocked));
        Assert.Equal("DISCARD UNAVAILABLE", ManifestPresentationPolicy.OfferDiscardActionLabel(blocked));
    }

    [Fact]
    public void Unrevealed_family_labels_are_defensively_replaced_even_if_payload_leaks()
    {
        var seal = new ManifestFamilySealSnapshot(
            2,
            "future-real-family",
            "LEAKED FUTURE FAMILY",
            ManifestRiskBand.Mixed,
            revealed: false,
            burned: false,
            locked: false);

        Assert.Equal("SEALED FAMILY 2", ManifestPresentationPolicy.FamilySealLabel(seal));
    }

    [Fact]
    public void Forfeit_request_carries_only_manifest_id_and_exact_expected_phase()
    {
        var request = new ManifestForfeitOperationParams(Hex('1')[..24], ManifestPhase.Offer2);
        var json = JsonConvert.SerializeObject(request);

        Assert.Equal(ModConstants.ManifestForfeitAction, request.Action);
        Assert.Equal(
            $"{{\"Action\":\"{ModConstants.ManifestForfeitAction}\",\"manifestId\":\"{Hex('1')[..24]}\",\"expectedPhase\":\"Offer2\"}}",
            json);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ManifestForfeitOperationParams(Hex('1')[..24], ManifestPhase.ClaimPrepared));
    }

    private static ManifestSnapshot Snapshot(
        ManifestPhase phase,
        bool canClaim = false,
        bool canRelay = false,
        string? fingerprint = null,
        int favor = 0,
        bool guarantee = false,
        bool canBurn = true)
    {
        var terminal = phase is ManifestPhase.Granted or ManifestPhase.Confiscated or ManifestPhase.Forfeited;
        var ticket = phase == ManifestPhase.TicketPrepared;
        var offer = phase is ManifestPhase.Offer1 or ManifestPhase.Offer2;
        var ordinal = phase == ManifestPhase.Offer2 ? 2 : 1;
        int? lockedOrdinal = ticket || offer || phase == ManifestPhase.Forfeited ? null : ordinal;
        var lot = ticket || terminal
            ? null
            : new ManifestLotSnapshot(
                "pack.alpha",
                "Alpha Pack",
                "field-kit",
                "Field Recovery Cache",
                "A mixed medical and utility package",
                ordinal == 1 ? "operator" : "field-supply",
                "survival",
                RewardRarity.Contractor,
                "aaaaaaaaaaaaaaaaaaaaaaaa",
                fingerprint ?? Hex('a'),
                125_000,
                180_000,
                12,
                [
                    new ManifestLotContentSnapshot("aaaaaaaaaaaaaaaaaaaaaaaa", "Portable Defibrillator", 1),
                    new ManifestLotContentSnapshot("bbbbbbbbbbbbbbbbbbbbbbbb", "Surgical Kit", 2)
                ]);
        var relay = canRelay
            ? new ManifestRelayPropositionSnapshot(
                1,
                45,
                35,
                20,
                favor,
                Math.Min(3, favor + 1),
                guarantee,
                1,
                RewardRarity.Restricted,
                200_000,
                300_000,
                true,
                null)
            : null;
        return new ManifestSnapshot(
            ManifestSnapshot.CurrentProtocolVersion,
            "111111111111111111111111",
            phase,
            ordinal,
            lockedOrdinal,
            1,
            terminal,
            RarityLadderVersion.FiveTier,
            terminal ? null : Hex('c'),
            !ticket,
            favor,
            3,
            [
                new ManifestFamilySealSnapshot(1, "operator", "Operator", ManifestRiskBand.Mixed, !ticket, false, lockedOrdinal == 1),
                new ManifestFamilySealSnapshot(2, "field-supply", "Field Supply", ManifestRiskBand.Mixed, ordinal >= 2 && !ticket, false, lockedOrdinal == 2),
                new ManifestFamilySealSnapshot(3, "arsenal", "Arsenal", ManifestRiskBand.Mixed, false, false, lockedOrdinal == 3)
            ],
            lot,
            new ManifestAvailableActionsSnapshot(
                offer,
                offer && canBurn,
                canClaim,
                canRelay,
                false,
                ordinal,
                phase,
                1),
            relay,
            null,
            false,
            ticket ? "222222222222222222222222" : null);
    }

    private static ManifestOpeningOddsSnapshot OpeningOdds(string catalogSnapshotId)
    {
        var lots = new[]
        {
            new ManifestOpeningLotOddsSnapshot(
                "server.provider",
                "Server Provider",
                "server-lot",
                "Server Lot",
                RewardRarity.Restricted,
                "server-lot-anchor-template",
                "1",
                "1",
                "12.34%")
        };
        var families = new[]
        {
            new ManifestOpeningFamilyOddsSnapshot(
                "family-a",
                "Server Family",
                "1",
                "3",
                "33.33%",
                "1",
                "1",
                "100.00%",
                lots),
            new ManifestOpeningFamilyOddsSnapshot(
                "family-b",
                "Second Family",
                "1",
                "3",
                "33.33%",
                "1",
                "1",
                "100.00%",
                lots),
            new ManifestOpeningFamilyOddsSnapshot(
                "family-c",
                "Third Family",
                "1",
                "3",
                "33.33%",
                "1",
                "1",
                "100.00%",
                lots)
        };
        return new ManifestOpeningOddsSnapshot(
            ManifestOpeningOddsSnapshot.CurrentProtocolVersion,
            catalogSnapshotId,
            ManifestOpeningOddsSnapshot.RequiredOfferCount,
            families.Length,
            ManifestOpeningOddsSnapshot.CanonicalSelectionRule,
            families);
    }

    private static string Hex(char value) => new(value, 64);
}
