using ContrabandCases.Client.Opening;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class ManifestClientProtocolTests
{
    private const string ManifestId = "manifest-client-protocol";
    private const string RecoveryCaseItemId = "aaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(CaseContracts.Operations)]
    [InlineData(CaseContracts.Relics)]
    [InlineData(CaseContracts.BlackSite)]
    public void Themed_opening_odds_require_a_positive_published_price(string template)
    {
        var odds = OpeningOdds();
        odds["caseTemplateId"] = template;
        odds["casePrice"] = null;
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, odds)));
        odds["casePrice"] = 0;
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, odds)));
        odds["casePrice"] = 123_000;
        var parsed = ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, odds)).OpeningOdds!;
        Assert.Equal(template, parsed.CaseTemplateId);
        Assert.Equal(123_000, parsed.CasePrice);
    }

    [Fact]
    public void Snapshot_accepts_sanitized_authoritative_lot_without_a_local_catalog()
    {
        var snapshot = ManifestSnapshotEnvelope.Parse(
            Envelope(OfferSnapshot()),
            ManifestId);

        Assert.Equal(ManifestId, snapshot.ManifestId);
        Assert.Equal(ManifestPhase.Offer1, snapshot.Phase);
        Assert.Equal(ManifestRiskBand.Mixed, snapshot.FamilySeals[0].RiskBand);
        Assert.Equal("Field Cache", snapshot.CurrentLot!.DisplayName);
        Assert.Equal(RewardRarity.ScavGrade, snapshot.CurrentLot.Grade);
        Assert.Equal(2, snapshot.CurrentLot.Contents.Count);
        Assert.True(snapshot.AvailableActions.CanLock);
        Assert.True(snapshot.AvailableActions.CanBurn);
    }

    [Fact]
    public void Snapshot_authenticates_the_exact_requested_manifest_id()
    {
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(OfferSnapshot()), "manifest-other"));
    }

    [Fact]
    public void Current_discovery_accepts_authoritative_odds_but_required_snapshot_fetch_does_not()
    {
        var odds = OpeningOdds();
        var current = ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, odds));

        Assert.Null(current.Snapshot);
        Assert.NotNull(current.OpeningOdds);
        Assert.Equal("catalog-a", current.OpeningOdds!.CatalogSnapshotId);
        Assert.Equal(3, current.OpeningOdds.FamilyCount);
        Assert.Equal("33.33%", current.OpeningOdds.Families[0].PerSlotPercent);
        Assert.Equal("50.00%", current.OpeningOdds.Families[0].Lots[0].ConditionalPercent);
        Assert.Equal("anchor-template", current.OpeningOdds.Families[0].Lots[0].AnchorTemplateId);
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(null), ManifestId));
    }

    [Fact]
    public void Opening_odds_lot_requires_an_anchor_template_id_and_fails_closed_without_one()
    {
        var missing = OpeningOdds();
        FirstLot(missing).Remove("anchorTemplateId");
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, missing)));

        var blank = OpeningOdds();
        FirstLot(blank)["anchorTemplateId"] = string.Empty;
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, blank)));

        var unknown = OpeningOdds();
        FirstLot(unknown)["extra"] = "unexpected";
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, unknown)));
    }

    [Fact]
    public void Current_discovery_accepts_an_active_snapshot_and_enforces_exact_xor()
    {
        var active = ManifestSnapshotEnvelope.ParseCurrent(
            CurrentEnvelope(OfferSnapshot(), null));

        Assert.NotNull(active.Snapshot);
        Assert.Null(active.OpeningOdds);
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, null)));
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(
                CurrentEnvelope(OfferSnapshot(), OpeningOdds())));
    }

    [Fact]
    public void Current_discovery_rejects_duplicate_unknown_and_unsupported_odds_fields()
    {
        var duplicate = CurrentEnvelope(null, OpeningOdds()).Replace(
            "\"familyCount\":3",
            "\"familyCount\":3,\"familyCount\":3",
            StringComparison.Ordinal);
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(duplicate));

        var unknown = OpeningOdds();
        ((JObject)((JArray)((JObject)((JArray)unknown["families"]!)[0]!)["lots"]!)[0]!)["weight"] = 1;
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, unknown)));

        var unsupported = OpeningOdds();
        unsupported["protocolVersion"] = ManifestOpeningOddsSnapshot.CurrentProtocolVersion + 1;
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, unsupported)));
    }

    [Fact]
    public void Opening_odds_require_bounded_reduced_canonical_rationals_and_percentages()
    {
        Assert.Equal(128, ManifestOpeningOddsSnapshot.MaximumFamilies);
        Assert.Equal(512, ManifestOpeningOddsSnapshot.MaximumLotsPerFamily);
        Assert.Equal(2_048, ManifestOpeningOddsSnapshot.MaximumTotalLots);
        Assert.Equal(1_024, ManifestOpeningOddsSnapshot.MaximumRationalDigits);

        var leadingZero = OpeningOdds();
        FirstFamily(leadingZero)["perSlotNumerator"] = "01";
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, leadingZero)));

        var nonReduced = OpeningOdds();
        FirstLot(nonReduced)["conditionalNumerator"] = "2";
        FirstLot(nonReduced)["conditionalDenominator"] = "4";
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, nonReduced)));

        var numericToken = OpeningOdds();
        FirstFamily(numericToken)["inclusionNumerator"] = 1;
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, numericToken)));

        var oversized = OpeningOdds();
        FirstLot(oversized)["conditionalNumerator"] =
            new string('1', ManifestOpeningOddsSnapshot.MaximumRationalDigits + 1);
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, oversized)));

        var malformedPercent = OpeningOdds();
        FirstLot(malformedPercent)["conditionalPercent"] = "50%";
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, malformedPercent)));

        var excessiveFamilies = OpeningOdds();
        excessiveFamilies["familyCount"] = ManifestOpeningOddsSnapshot.MaximumFamilies + 1;
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, excessiveFamilies)));
    }

    [Fact]
    public void Opening_odds_reject_each_percentage_when_it_disagrees_with_its_rational()
    {
        var wrongPerSlot = OpeningOdds();
        FirstFamily(wrongPerSlot)["perSlotPercent"] = "33.34%";
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, wrongPerSlot)));

        var wrongInclusion = OpeningOdds();
        FirstFamily(wrongInclusion)["inclusionPercent"] = "99.99%";
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, wrongInclusion)));

        var wrongConditional = OpeningOdds();
        FirstLot(wrongConditional)["conditionalPercent"] = "50.01%";
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, wrongConditional)));
    }

    [Theory]
    [InlineData("1", "20000", "0.01%", "19999", "20000", "100.00%")]
    [InlineData("1", "40000", "0.00%", "39999", "40000", "100.00%")]
    [InlineData("1", "32", "3.13%", "31", "32", "96.88%")]
    public void Opening_odds_percentage_validation_uses_invariant_half_up_hundredths(
        string firstNumerator,
        string firstDenominator,
        string firstPercent,
        string secondNumerator,
        string secondDenominator,
        string secondPercent)
    {
        var odds = OpeningOdds();
        var lots = (JArray)FirstFamily(odds)["lots"]!;
        SetConditionalOdds(
            (JObject)lots[0]!,
            firstNumerator,
            firstDenominator,
            firstPercent);
        SetConditionalOdds(
            (JObject)lots[1]!,
            secondNumerator,
            secondDenominator,
            secondPercent);

        var current = ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, odds));

        var parsedLots = current.OpeningOdds!.Families[0].Lots;
        Assert.Equal(firstPercent, parsedLots[0].ConditionalPercent);
        Assert.Equal(secondPercent, parsedLots[1].ConditionalPercent);
    }

    [Fact]
    public void Opening_odds_require_uniform_family_math_unit_sums_and_canonical_order()
    {
        var wrongFamilyMath = OpeningOdds();
        FirstFamily(wrongFamilyMath)["perSlotNumerator"] = "1";
        FirstFamily(wrongFamilyMath)["perSlotDenominator"] = "2";
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, wrongFamilyMath)));

        var wrongSum = OpeningOdds();
        FirstLot(wrongSum)["conditionalNumerator"] = "1";
        FirstLot(wrongSum)["conditionalDenominator"] = "3";
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, wrongSum)));

        var familyOrder = OpeningOdds();
        var families = (JArray)familyOrder["families"]!;
        (families[0], families[1]) = (families[1], families[0]);
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, familyOrder)));

        var lotOrder = OpeningOdds();
        var lots = (JArray)FirstFamily(lotOrder)["lots"]!;
        (lots[0], lots[1]) = (lots[1], lots[0]);
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, lotOrder)));
    }

    [Fact]
    public void Opening_odds_enforce_per_family_and_total_lot_bounds()
    {
        var tooManyInFamily = OpeningOdds();
        FirstFamily(tooManyInFamily)["lots"] = OddsLots(
            "arsenal",
            ManifestOpeningOddsSnapshot.MaximumLotsPerFamily + 1);
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, tooManyInFamily)));

        var overTotal = OpeningOdds();
        overTotal["familyCount"] = 5;
        overTotal["families"] = new JArray(
            Enumerable.Range(0, 5).Select(index => OddsFamilyWithLotCount(
                $"family-{index:D2}",
                index == 4 ? 1 : ManifestOpeningOddsSnapshot.MaximumLotsPerFamily,
                familyCount: 5)));
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(null, overTotal)));
    }

    [Fact]
    public void Parser_rejects_duplicate_unknown_and_case_variant_properties_at_every_boundary()
    {
        var duplicate = Envelope(OfferSnapshot()).Replace(
            $"\"manifestId\":\"{ManifestId}\"",
            $"\"manifestId\":\"{ManifestId}\",\"manifestId\":\"manifest-other\"",
            StringComparison.Ordinal);
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(duplicate, ManifestId));

        var unknownNested = OfferSnapshot();
        ((JObject)((JArray)((JObject)unknownNested["currentLot"]!)["contents"]!)[0]!)["presetId"] = "hidden";
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(unknownNested), ManifestId));

        var wrongCase = OfferSnapshot();
        wrongCase["ManifestId"] = wrongCase["manifestId"];
        wrongCase.Remove("manifestId");
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(wrongCase), ManifestId));

        var unknownEnvelope = JObject.Parse(Envelope(OfferSnapshot()));
        unknownEnvelope["status"] = "ok";
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(unknownEnvelope.ToString(Formatting.None), ManifestId));
    }

    [Fact]
    public void Parser_rejects_noncanonical_enums_hashes_numbers_and_lists()
    {
        var wrongPhase = OfferSnapshot();
        wrongPhase["phase"] = "offer1";
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(wrongPhase), ManifestId));

        var leakingRiskBand = OfferSnapshot();
        ((JObject)((JArray)leakingRiskBand["familySeals"]!)[0]!)["riskBand"] = "High";
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(leakingRiskBand), ManifestId));

        var uppercaseHash = OfferSnapshot();
        ((JObject)uppercaseHash["currentLot"]!)["fingerprint"] = new string('A', 64);
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(uppercaseHash), ManifestId));

        var fractionalStage = OfferSnapshot();
        fractionalStage["relayStage"] = 1.0d;
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(fractionalStage), ManifestId));

        var fourSeals = OfferSnapshot();
        ((JArray)fourSeals["familySeals"]!).Add(Seal(4, "family-d", false, false, false));
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(fourSeals), ManifestId));
    }

    [Fact]
    public void Active_and_terminal_snapshot_fields_follow_their_phase_boundaries()
    {
        var activeWithoutCatalog = OfferSnapshot();
        activeWithoutCatalog["catalogSnapshotId"] = JValue.CreateNull();
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(activeWithoutCatalog), ManifestId));

        var terminal = ManifestSnapshotEnvelope.Parse(
            Envelope(TerminalSnapshot()),
            ManifestId);
        Assert.Equal(ManifestPhase.Granted, terminal.Phase);
        Assert.Null(terminal.CatalogSnapshotId);
        Assert.Null(terminal.CurrentLot);
        Assert.True(terminal.TicketCommitted);
        Assert.True(terminal.RelayTerminal);
    }

    [Fact]
    public void Ticket_recovery_requires_only_its_exact_case_identity()
    {
        var ticket = ManifestSnapshotEnvelope.Parse(
            Envelope(TicketPreparedSnapshot()),
            ManifestId);
        Assert.Equal(RecoveryCaseItemId, ticket.RecoveryCaseItemId);
        Assert.False(ticket.TicketCommitted);
        Assert.Null(ticket.CurrentLot);

        var missingRecoveryIdentity = TicketPreparedSnapshot();
        missingRecoveryIdentity["recoveryCaseItemId"] = JValue.CreateNull();
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(missingRecoveryIdentity), ManifestId));

        var leakedRecoveryIdentity = OfferSnapshot();
        leakedRecoveryIdentity["recoveryCaseItemId"] = RecoveryCaseItemId;
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(leakedRecoveryIdentity), ManifestId));
    }

    [Fact]
    public void Entitlement_relay_discloses_exact_odds_and_one_point_favor_transition()
    {
        var snapshot = ManifestSnapshotEnvelope.Parse(
            Envelope(EntitlementSnapshot()),
            ManifestId);

        Assert.NotNull(snapshot.Relay);
        Assert.Equal(55, snapshot.Relay!.UpgradePercent);
        Assert.Equal(2, snapshot.Relay.FavorAfterOnLoss);
        Assert.Equal(RewardRarity.Uncommon, snapshot.Relay.UpgradeGrade);
        Assert.True(snapshot.Relay.SidegradeEndsChain);

        var stageWeightedFavor = EntitlementSnapshot();
        ((JObject)stageWeightedFavor["relay"]!)["favorAfterOnLoss"] = 3;
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(stageWeightedFavor), ManifestId));
    }

    [Fact]
    public void State_echoes_and_action_flags_must_match_the_authoritative_snapshot()
    {
        var staleOrdinal = OfferSnapshot();
        ((JObject)staleOrdinal["availableActions"]!)["expectedOrdinal"] = 2;
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(staleOrdinal), ManifestId));

        var unexpectedClaim = OfferSnapshot();
        ((JObject)unexpectedClaim["availableActions"]!)["canClaim"] = true;
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(unexpectedClaim), ManifestId));

        var lockOnly = OfferSnapshot();
        ((JObject)lockOnly["availableActions"]!)["canBurn"] = false;
        var parsed = ManifestSnapshotEnvelope.Parse(Envelope(lockOnly), ManifestId);
        Assert.True(parsed.AvailableActions.CanLock);
        Assert.False(parsed.AvailableActions.CanBurn);
    }

    [Fact]
    public void Missing_content_is_the_only_reason_lot_values_may_be_absent()
    {
        var absentValues = OfferSnapshot();
        var lot = (JObject)absentValues["currentLot"]!;
        lot["liquidationValue"] = JValue.CreateNull();
        lot["useValue"] = JValue.CreateNull();
        lot["footprintCells"] = JValue.CreateNull();
        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(absentValues), ManifestId));

        absentValues["missingContentBlocked"] = true;
        absentValues["availableActions"] = Actions(
            false,
            false,
            false,
            false,
            true,
            1,
            ManifestPhase.Offer1,
            1);
        var blocked = ManifestSnapshotEnvelope.Parse(Envelope(absentValues), ManifestId);
        Assert.True(blocked.MissingContentBlocked);
        Assert.True(blocked.AvailableActions.CanForfeit);
    }

    [Theory]
    [InlineData(ManifestPhase.ClaimPrepared, true, false)]
    [InlineData(ManifestPhase.RewardOwed, true, false)]
    [InlineData(ManifestPhase.RelayPrepared, false, true)]
    public void Prepared_recovery_accepts_frozen_lot_without_live_valuation(
        ManifestPhase phase,
        bool canClaim,
        bool canRelay)
    {
        var prepared = PreparedRecoverySnapshot(phase, canClaim, canRelay);

        var snapshot = ManifestSnapshotEnvelope.Parse(Envelope(prepared), ManifestId);

        Assert.Equal(phase, snapshot.Phase);
        Assert.False(snapshot.MissingContentBlocked);
        Assert.Equal(canClaim, snapshot.AvailableActions.CanClaim);
        Assert.Equal(canRelay, snapshot.AvailableActions.CanRelay);
    }

    [Theory]
    [InlineData(ManifestPhase.Entitlement)]
    [InlineData(ManifestPhase.ClaimPrepared)]
    [InlineData(ManifestPhase.RewardOwed)]
    [InlineData(ManifestPhase.RelayPrepared)]
    public void Current_state_accepts_cross_family_lot_authenticated_by_successful_relay_receipt(
        ManifestPhase phase)
    {
        var current = ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(
            CrossFamilyPostRelaySnapshot(phase),
            null));

        Assert.Equal(phase, current.Snapshot!.Phase);
        Assert.Equal("relay-family", current.Snapshot.CurrentLot!.FamilyId);
        Assert.Equal("Cross-Family Upgrade", current.Snapshot.LatestReceipt!.OutputDisplayName);
        Assert.Equal(RewardRarity.Uncommon, current.Snapshot.LatestReceipt.OutputGrade);
    }

    [Fact]
    public void Current_state_accepts_cross_family_sidegrade_authenticated_by_terminal_receipt()
    {
        var current = ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(
            CrossFamilyPostSidegradeSnapshot(),
            null));

        Assert.True(current.Snapshot!.RelayTerminal);
        Assert.Equal("relay-family", current.Snapshot.CurrentLot!.FamilyId);
        Assert.Equal(ManifestRelayResult.Sidegrade, current.Snapshot.LatestReceipt!.Outcome);
        Assert.Equal("Cross-Family Sidegrade", current.Snapshot.LatestReceipt.OutputDisplayName);
    }

    [Fact]
    public void First_relay_preparation_without_receipt_still_requires_original_offer_family()
    {
        var prepared = PreparedRecoverySnapshot(
            ManifestPhase.RelayPrepared,
            canClaim: false,
            canRelay: true);
        ((JObject)prepared["currentLot"]!)["familyId"] = "relay-family";

        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(prepared, null)));
    }

    [Fact]
    public void Cross_family_receipt_must_match_current_lot_name_and_grade()
    {
        var wrongName = CrossFamilyPostRelaySnapshot(ManifestPhase.Entitlement);
        ((JObject)wrongName["latestReceipt"]!)["outputDisplayName"] = "Unrelated Output";
        var nameException = Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(wrongName, null)));
        Assert.Contains("does not authenticate the current lot", nameException.Message, StringComparison.Ordinal);

        var wrongGrade = CrossFamilyPostRelaySnapshot(ManifestPhase.Entitlement);
        var wrongGradeReceipt = (JObject)wrongGrade["latestReceipt"]!;
        wrongGradeReceipt["inputGrade"] = RewardRarity.Contractor.ToString();
        wrongGradeReceipt["outputGrade"] = RewardRarity.Restricted.ToString();
        var gradeException = Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(wrongGrade, null)));
        Assert.Contains("does not authenticate the current lot", gradeException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Active_post_relay_state_requires_receipt_even_when_current_family_matches_offer()
    {
        var snapshot = CrossFamilyPostRelaySnapshot(ManifestPhase.Entitlement);
        ((JObject)snapshot["currentLot"]!)["familyId"] = "family-a";
        snapshot["latestReceipt"] = JValue.CreateNull();

        var exception = Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(snapshot, null)));
        Assert.Contains("committed Relay evidence", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Active_post_relay_receipt_must_match_current_lot_even_when_family_matches_offer()
    {
        var snapshot = CrossFamilyPostRelaySnapshot(ManifestPhase.Entitlement);
        ((JObject)snapshot["currentLot"]!)["familyId"] = "family-a";
        ((JObject)snapshot["latestReceipt"]!)["outputDisplayName"] = "Unrelated Output";

        var exception = Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(snapshot, null)));
        Assert.Contains("does not authenticate the current lot", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Relay_receipt_stage_and_active_terminal_marker_must_match_current_state()
    {
        var wrongStage = CrossFamilyPostRelaySnapshot(ManifestPhase.Entitlement);
        ((JObject)wrongStage["latestReceipt"]!)["stage"] = 2;
        var stageException = Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(wrongStage, null)));
        Assert.Contains("current Relay stage", stageException.Message, StringComparison.Ordinal);

        var wrongTerminal = CrossFamilyPostRelaySnapshot(ManifestPhase.Entitlement);
        wrongTerminal["relayTerminal"] = true;
        var terminalException = Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(wrongTerminal, null)));
        Assert.Contains("terminal marker", terminalException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Offer_phase_rejects_relay_receipt_before_entitlement_exists()
    {
        var snapshot = OfferSnapshot();
        snapshot["latestReceipt"] = RelayReceipt(
            ManifestRelayResult.Sidegrade,
            "Field Cache",
            RewardRarity.ScavGrade,
            "Field Cache",
            RewardRarity.ScavGrade,
            brokerFavorBefore: 0,
            brokerFavorAfter: 0);

        var exception = Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(snapshot, null)));
        Assert.Contains("before an entitlement", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Confiscated_receipt_cannot_authenticate_a_cross_family_current_lot()
    {
        var snapshot = CrossFamilyPostRelaySnapshot(ManifestPhase.Entitlement);
        snapshot["brokerFavor"] = 2;
        var relay = (JObject)snapshot["relay"]!;
        relay["favorBefore"] = 2;
        relay["favorAfterOnLoss"] = 3;
        var receipt = (JObject)snapshot["latestReceipt"]!;
        receipt["stage"] = 2;
        receipt["outcome"] = ManifestRelayResult.Confiscated.ToString();
        receipt["outputDisplayName"] = JValue.CreateNull();
        receipt["outputGrade"] = JValue.CreateNull();
        receipt["brokerFavorBefore"] = 1;
        receipt["brokerFavorAfter"] = 2;

        var exception = Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.ParseCurrent(CurrentEnvelope(snapshot, null)));
        Assert.Contains("outcome contradicts", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ManifestPhase.ClaimPrepared)]
    [InlineData(ManifestPhase.RewardOwed)]
    [InlineData(ManifestPhase.RelayPrepared)]
    public void Missing_content_block_is_rejected_during_prepared_recovery(ManifestPhase phase)
    {
        var prepared = PreparedRecoverySnapshot(
            phase,
            canClaim: phase != ManifestPhase.RelayPrepared,
            canRelay: phase == ManifestPhase.RelayPrepared);
        prepared["missingContentBlocked"] = true;
        prepared["availableActions"] = Actions(
            false,
            false,
            false,
            false,
            false,
            1,
            phase,
            1);

        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(prepared), ManifestId));
    }

    [Fact]
    public void Missing_content_block_is_rejected_after_terminal_settlement()
    {
        var terminal = TerminalSnapshot();
        terminal["missingContentBlocked"] = true;

        Assert.Throws<ManifestSnapshotException>(() =>
            ManifestSnapshotEnvelope.Parse(Envelope(terminal), ManifestId));
    }

    [Fact]
    public void Mutation_requests_serialize_only_action_identity_and_expected_state()
    {
        var lockRequest = JObject.FromObject(new ManifestDecisionOperationParams(
            ModConstants.ManifestLockAction,
            ManifestId,
            1));
        AssertExactProperties(lockRequest, "Action", "manifestId", "expectedOrdinal");
        Assert.Equal(ModConstants.ManifestLockAction, lockRequest.Value<string>("Action"));

        var claimRequest = JObject.FromObject(new ManifestClaimOperationParams(
            ManifestId,
            ManifestPhase.RewardOwed));
        AssertExactProperties(claimRequest, "Action", "manifestId", "expectedPhase");
        Assert.Equal("RewardOwed", claimRequest.Value<string>("expectedPhase"));

        var relayRequest = JObject.FromObject(new ManifestRelayOperationParams(
            ManifestId,
            ManifestPhase.RelayPrepared,
            2));
        AssertExactProperties(
            relayRequest,
            "Action",
            "manifestId",
            "expectedPhase",
            "expectedRelayStage");
        Assert.Equal(2, relayRequest.Value<int>("expectedRelayStage"));
    }

    [Fact]
    public void Mutation_request_constructors_reject_wrong_actions_phases_and_stages()
    {
        Assert.Throws<ArgumentException>(() => new ManifestDecisionOperationParams(
            ModConstants.OpenAction,
            ManifestId,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManifestDecisionOperationParams(
            ModConstants.ManifestBurnAction,
            ManifestId,
            3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManifestClaimOperationParams(
            ManifestId,
            ManifestPhase.Offer1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManifestRelayOperationParams(
            ManifestId,
            ManifestPhase.ClaimPrepared,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManifestRelayOperationParams(
            ManifestId,
            ManifestPhase.Entitlement,
            4));
    }

    [Fact]
    public void Snapshot_transport_body_contains_only_the_authenticated_manifest_id()
    {
        var request = JObject.Parse(ManifestSnapshotTransport.CreateSnapshotRequestJson(ManifestId));

        AssertExactProperties(request, "manifestId");
        Assert.Equal(ManifestId, request.Value<string>("manifestId"));
        Assert.Equal("/contrabandcases/manifest/current", ModConstants.ManifestCurrentRoute);
        Assert.Equal("/contrabandcases/manifest/snapshot", ModConstants.ManifestSnapshotRoute);
    }

    private static JObject OfferSnapshot() => Snapshot(
        ManifestPhase.Offer1,
        currentOrdinal: 1,
        lockedOrdinal: null,
        relayTerminal: false,
        currentLot: Lot(),
        availableActions: Actions(
            true,
            true,
            false,
            false,
            false,
            1,
            ManifestPhase.Offer1,
            1),
        familySeals: Seals(currentOrdinal: 1, lockedOrdinal: null));

    private static JObject EntitlementSnapshot() => Snapshot(
        ManifestPhase.Entitlement,
        currentOrdinal: 1,
        lockedOrdinal: 1,
        relayTerminal: false,
        currentLot: Lot(),
        availableActions: Actions(
            false,
            false,
            true,
            true,
            false,
            1,
            ManifestPhase.Entitlement,
            1),
        familySeals: Seals(currentOrdinal: 1, lockedOrdinal: 1),
        relay: new JObject
        {
            ["stage"] = 1,
            ["upgradePercent"] = 55,
            ["sidegradePercent"] = 30,
            ["confiscatePercent"] = 15,
            ["favorBefore"] = 1,
            ["favorAfterOnLoss"] = 2,
            ["guaranteeActive"] = false,
            ["keyCost"] = 13_500,
            ["upgradeGrade"] = "Uncommon",
            ["candidateValueMin"] = 40_000,
            ["candidateValueMax"] = 74_999,
            ["sidegradeEndsChain"] = true,
            ["terminalReason"] = JValue.CreateNull()
        },
        brokerFavor: 1);

    private static JObject PreparedRecoverySnapshot(
        ManifestPhase phase,
        bool canClaim,
        bool canRelay)
    {
        var prepared = EntitlementSnapshot();
        prepared["phase"] = phase.ToString();
        prepared["availableActions"] = Actions(
            false,
            false,
            canClaim,
            canRelay,
            false,
            1,
            phase,
            1);
        var lot = (JObject)prepared["currentLot"]!;
        lot["liquidationValue"] = JValue.CreateNull();
        lot["useValue"] = JValue.CreateNull();
        lot["footprintCells"] = JValue.CreateNull();
        if (phase != ManifestPhase.RelayPrepared)
        {
            prepared["relay"] = JValue.CreateNull();
        }
        else
        {
            var relay = (JObject)prepared["relay"]!;
            relay["candidateValueMin"] = JValue.CreateNull();
            relay["candidateValueMax"] = JValue.CreateNull();
        }

        return prepared;
    }

    private static JObject CrossFamilyPostRelaySnapshot(ManifestPhase phase)
    {
        var snapshot = EntitlementSnapshot();
        snapshot["phase"] = phase.ToString();
        snapshot["relayStage"] = 2;
        snapshot["availableActions"] = Actions(
            false,
            false,
            phase is ManifestPhase.Entitlement or ManifestPhase.ClaimPrepared or ManifestPhase.RewardOwed,
            phase is ManifestPhase.Entitlement or ManifestPhase.RelayPrepared,
            false,
            1,
            phase,
            2);

        var lot = (JObject)snapshot["currentLot"]!;
        lot["lotId"] = "cross-family-upgrade";
        lot["displayName"] = "Cross-Family Upgrade";
        lot["familyId"] = "relay-family";
        lot["grade"] = RewardRarity.Uncommon.ToString();
        lot["fingerprint"] = new string('b', 64);

        snapshot["latestReceipt"] = RelayReceipt(
            ManifestRelayResult.Upgrade,
            "Field Cache",
            RewardRarity.ScavGrade,
            "Cross-Family Upgrade",
            RewardRarity.Uncommon,
            brokerFavorBefore: 1,
            brokerFavorAfter: 1);

        if (phase is ManifestPhase.Entitlement or ManifestPhase.RelayPrepared)
        {
            var relay = (JObject)snapshot["relay"]!;
            relay["stage"] = 2;
            relay["upgradePercent"] = 45;
            relay["sidegradePercent"] = 25;
            relay["confiscatePercent"] = 30;
            relay["upgradeGrade"] = RewardRarity.Contractor.ToString();
        }
        else
        {
            snapshot["relay"] = JValue.CreateNull();
        }

        return snapshot;
    }

    private static JObject CrossFamilyPostSidegradeSnapshot()
    {
        var snapshot = EntitlementSnapshot();
        snapshot["relayTerminal"] = true;
        snapshot["availableActions"] = Actions(
            false,
            false,
            true,
            false,
            false,
            1,
            ManifestPhase.Entitlement,
            1);

        var lot = (JObject)snapshot["currentLot"]!;
        lot["lotId"] = "cross-family-sidegrade";
        lot["displayName"] = "Cross-Family Sidegrade";
        lot["familyId"] = "relay-family";
        lot["fingerprint"] = new string('c', 64);

        var relay = (JObject)snapshot["relay"]!;
        relay["upgradeGrade"] = JValue.CreateNull();
        relay["candidateValueMin"] = JValue.CreateNull();
        relay["candidateValueMax"] = JValue.CreateNull();
        relay["terminalReason"] = "A sidegrade settles the Relay chain.";
        snapshot["latestReceipt"] = RelayReceipt(
            ManifestRelayResult.Sidegrade,
            "Field Cache",
            RewardRarity.ScavGrade,
            "Cross-Family Sidegrade",
            RewardRarity.ScavGrade,
            brokerFavorBefore: 1,
            brokerFavorAfter: 1);
        return snapshot;
    }

    private static JObject RelayReceipt(
        ManifestRelayResult outcome,
        string inputDisplayName,
        RewardRarity inputGrade,
        string? outputDisplayName,
        RewardRarity? outputGrade,
        int brokerFavorBefore,
        int brokerFavorAfter) => new()
        {
            ["stage"] = 1,
            ["outcome"] = outcome.ToString(),
            ["inputDisplayName"] = inputDisplayName,
            ["inputGrade"] = inputGrade.ToString(),
            ["outputDisplayName"] = outputDisplayName is null
                ? JValue.CreateNull()
                : outputDisplayName,
            ["outputGrade"] = outputGrade is null
                ? JValue.CreateNull()
                : outputGrade.Value.ToString(),
            ["brokerFavorBefore"] = brokerFavorBefore,
            ["brokerFavorAfter"] = brokerFavorAfter
        };

    private static JObject TerminalSnapshot() => Snapshot(
        ManifestPhase.Granted,
        currentOrdinal: 1,
        lockedOrdinal: 1,
        relayTerminal: true,
        currentLot: null,
        availableActions: Actions(
            false,
            false,
            false,
            false,
            false,
            1,
            ManifestPhase.Granted,
            1),
        familySeals: Seals(currentOrdinal: 1, lockedOrdinal: 1),
        catalogSnapshotId: null);

    private static JObject TicketPreparedSnapshot() => Snapshot(
        ManifestPhase.TicketPrepared,
        currentOrdinal: 1,
        lockedOrdinal: null,
        relayTerminal: false,
        currentLot: null,
        availableActions: Actions(
            false,
            false,
            false,
            false,
            false,
            1,
            ManifestPhase.TicketPrepared,
            1),
        familySeals: Seals(currentOrdinal: 0, lockedOrdinal: null),
        ticketCommitted: false,
        recoveryCaseItemId: RecoveryCaseItemId);

    private static JObject Snapshot(
        ManifestPhase phase,
        int currentOrdinal,
        int? lockedOrdinal,
        bool relayTerminal,
        JObject? currentLot,
        JObject availableActions,
        JArray familySeals,
        JObject? relay = null,
        JObject? latestReceipt = null,
        string? catalogSnapshotId = "catalog-client-protocol",
        bool ticketCommitted = true,
        int brokerFavor = 0,
        string? recoveryCaseItemId = null) => new()
        {
            ["protocolVersion"] = ManifestSnapshot.CurrentProtocolVersion,
            ["caseTemplateId"] = ModConstants.CaseTemplateId,
            ["manifestId"] = ManifestId,
            ["phase"] = phase.ToString(),
            ["currentOrdinal"] = currentOrdinal,
            ["lockedOrdinal"] = lockedOrdinal is int value ? value : JValue.CreateNull(),
            ["relayStage"] = 1,
            ["relayTerminal"] = relayTerminal,
            ["rarityLadderVersion"] = "FiveTier",
            ["catalogSnapshotId"] = catalogSnapshotId is null
                ? JValue.CreateNull()
                : catalogSnapshotId,
            ["ticketCommitted"] = ticketCommitted,
            ["brokerFavor"] = brokerFavor,
            ["brokerFavorMaximum"] = 3,
            ["familySeals"] = familySeals,
            ["currentLot"] = currentLot is null ? JValue.CreateNull() : currentLot,
            ["availableActions"] = availableActions,
            ["relay"] = relay is null ? JValue.CreateNull() : relay,
            ["latestReceipt"] = latestReceipt is null ? JValue.CreateNull() : latestReceipt,
            ["missingContentBlocked"] = false,
            ["recoveryCaseItemId"] = recoveryCaseItemId is null
                ? JValue.CreateNull()
                : recoveryCaseItemId
        };

    private static JObject Lot() => new()
    {
        ["providerId"] = "core",
        ["providerLabel"] = "Contraband Broker",
        ["lotId"] = "field-cache",
        ["displayName"] = "Field Cache",
        ["purpose"] = "A compact raid sustain kit.",
        ["familyId"] = "family-a",
        ["trackId"] = "field-sustain",
        ["grade"] = "ScavGrade",
        ["anchorTemplateId"] = "anchor-template",
        ["fingerprint"] = new string('a', 64),
        ["liquidationValue"] = 50_000,
        ["useValue"] = 70_000,
        ["footprintCells"] = 4,
        ["contents"] = new JArray
        {
            new JObject
            {
                ["templateId"] = "anchor-template",
                ["displayName"] = "Medical Bag",
                ["quantity"] = 1
            },
            new JObject
            {
                ["templateId"] = "supply-template",
                ["displayName"] = "Field Supply",
                ["quantity"] = 3
            }
        }
    };

    private static JArray Seals(int currentOrdinal, int? lockedOrdinal) => new()
    {
        Seal(
            1,
            "family-a",
            currentOrdinal >= 1,
            currentOrdinal > 1,
            lockedOrdinal == 1),
        Seal(
            2,
            "family-b",
            currentOrdinal >= 2,
            currentOrdinal > 2,
            lockedOrdinal == 2),
        Seal(
            3,
            "family-c",
            currentOrdinal >= 3,
            false,
            lockedOrdinal == 3)
    };

    private static JObject Seal(
        int ordinal,
        string familyId,
        bool revealed,
        bool burned,
        bool locked) => new()
        {
            ["ordinal"] = ordinal,
            ["familyId"] = familyId,
            ["familyLabel"] = $"Family {ordinal}",
            ["riskBand"] = "Mixed",
            ["revealed"] = revealed,
            ["burned"] = burned,
            ["locked"] = locked
        };

    private static JObject Actions(
        bool canLock,
        bool canBurn,
        bool canClaim,
        bool canRelay,
        bool canForfeit,
        int expectedOrdinal,
        ManifestPhase expectedPhase,
        int expectedRelayStage) => new()
        {
            ["canLock"] = canLock,
            ["canBurn"] = canBurn,
            ["canClaim"] = canClaim,
            ["canRelay"] = canRelay,
            ["canForfeit"] = canForfeit,
            ["expectedOrdinal"] = expectedOrdinal,
            ["expectedPhase"] = expectedPhase.ToString(),
            ["expectedRelayStage"] = expectedRelayStage
        };

    private static JObject OpeningOdds(string catalogSnapshotId = "catalog-a") => new()
    {
        ["protocolVersion"] = ManifestOpeningOddsSnapshot.CurrentProtocolVersion,
        ["caseTemplateId"] = ModConstants.CaseTemplateId,
        ["casePrice"] = JValue.CreateNull(),
        ["catalogSnapshotId"] = catalogSnapshotId,
        ["offerCount"] = ManifestOpeningOddsSnapshot.RequiredOfferCount,
        ["familyCount"] = 3,
        ["selectionRule"] = ManifestOpeningOddsSnapshot.CanonicalSelectionRule,
        ["families"] = new JArray
        {
            OddsFamily("arsenal", "Arsenal", RewardRarity.Restricted),
            OddsFamily("field-supply", "Field Supply", RewardRarity.ScavGrade),
            OddsFamily("operator", "Operator", RewardRarity.Contractor)
        }
    };

    private static JObject OddsFamily(
        string familyId,
        string familyLabel,
        RewardRarity grade) => new()
        {
            ["familyId"] = familyId,
            ["familyLabel"] = familyLabel,
            ["perSlotNumerator"] = "1",
            ["perSlotDenominator"] = "3",
            ["perSlotPercent"] = "33.33%",
            ["inclusionNumerator"] = "1",
            ["inclusionDenominator"] = "1",
            ["inclusionPercent"] = "100.00%",
            ["lots"] = new JArray
            {
                OddsLot("core", "Base Game", $"{familyId}-a", $"{familyLabel} A", grade),
                OddsLot("core", "Base Game", $"{familyId}-b", $"{familyLabel} B", grade)
            }
        };

    private static JObject OddsLot(
        string providerId,
        string providerLabel,
        string lotId,
        string displayName,
        RewardRarity grade,
        string conditionalNumerator = "1",
        string conditionalDenominator = "2",
        string conditionalPercent = "50.00%",
        string anchorTemplateId = "anchor-template") => new()
        {
            ["providerId"] = providerId,
            ["providerLabel"] = providerLabel,
            ["lotId"] = lotId,
            ["displayName"] = displayName,
            ["grade"] = grade.ToString(),
            ["anchorTemplateId"] = anchorTemplateId,
            ["conditionalNumerator"] = conditionalNumerator,
            ["conditionalDenominator"] = conditionalDenominator,
            ["conditionalPercent"] = conditionalPercent
        };

    private static JObject OddsFamilyWithLotCount(
        string familyId,
        int lotCount,
        int familyCount) => new()
        {
            ["familyId"] = familyId,
            ["familyLabel"] = familyId,
            ["perSlotNumerator"] = "1",
            ["perSlotDenominator"] = familyCount.ToString(),
            ["perSlotPercent"] = "20.00%",
            ["inclusionNumerator"] = "3",
            ["inclusionDenominator"] = familyCount.ToString(),
            ["inclusionPercent"] = "60.00%",
            ["lots"] = OddsLots(familyId, lotCount)
        };

    private static JArray OddsLots(string familyId, int count) => new(
        Enumerable.Range(0, count).Select(index => OddsLot(
            "core",
            "Base Game",
            $"{familyId}-lot-{index:D4}",
            $"Lot {index:D4}",
            RewardRarity.ScavGrade,
            "1",
            count.ToString(),
            "0.20%")));

    private static JObject FirstFamily(JObject odds) =>
        (JObject)((JArray)odds["families"]!)[0]!;

    private static JObject FirstLot(JObject odds) =>
        (JObject)((JArray)FirstFamily(odds)["lots"]!)[0]!;

    private static void SetConditionalOdds(
        JObject lot,
        string numerator,
        string denominator,
        string percent)
    {
        lot["conditionalNumerator"] = numerator;
        lot["conditionalDenominator"] = denominator;
        lot["conditionalPercent"] = percent;
    }

    private static string Envelope(JObject? snapshot) => new JObject
    {
        ["err"] = 0,
        ["errmsg"] = JValue.CreateNull(),
        ["data"] = new JObject
        {
            ["snapshot"] = snapshot is null ? JValue.CreateNull() : snapshot
        }
    }.ToString(Formatting.None);

    private static string CurrentEnvelope(JObject? snapshot, JObject? openingOdds) => new JObject
    {
        ["err"] = 0,
        ["errmsg"] = JValue.CreateNull(),
        ["data"] = new JObject
        {
            ["snapshot"] = snapshot is null ? JValue.CreateNull() : snapshot,
            ["openingOdds"] = openingOdds is null ? JValue.CreateNull() : openingOdds
        }
    }.ToString(Formatting.None);

    private static void AssertExactProperties(JObject source, params string[] expected)
    {
        Assert.Equal(
            expected.OrderBy(value => value, StringComparer.Ordinal),
            source.Properties().Select(property => property.Name).OrderBy(value => value, StringComparer.Ordinal));
    }
}
