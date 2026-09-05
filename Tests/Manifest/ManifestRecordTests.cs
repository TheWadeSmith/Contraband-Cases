using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Xunit;

namespace ContrabandCases.Tests.Manifest;

public sealed class ManifestRecordTests
{
    private static readonly DateTimeOffset PreparedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
    private static readonly DateTimeOffset CommittedAt = DateTimeOffset.UnixEpoch.AddMinutes(2);

    [Fact]
    public void Historical_three_offer_commitment_digest_is_unchanged()
    {
        // Captured with installed Server/Shared 0.4.2.0, before Cash support.
        // manifest-1 / catalog-1 / nonce 32 bytes of 0xaa / default Offers().
        Assert.Equal("5583e5673592525d85e26e1f0c6284939877593e4195e3b4a899588229aa4ce5",
            Commitment(Offers()).CommitmentSha256Hex);
    }

    [Fact]
    public void Ticket_prepared_requires_exactly_three_ordered_verified_offers()
    {
        var offers = Offers();
        var record = Record(
            ManifestFlowState.PrepareTicket(),
            offers,
            ticket: Ticket(committed: false));

        Assert.Equal("manifest-1", record.ManifestId);
        Assert.Equal([1, 2, 3], record.Offers.Select(offer => offer.Ordinal));
        Assert.True(record.RequiresExclusiveProfileMutation);
        Assert.False(record.IsTerminal);
        Assert.Null(record.ClaimPrepared);
        Assert.Null(record.RelayPrepared);
        Assert.Throws<ArgumentException>(() => Record(
            ManifestFlowState.PrepareTicket(),
            offers.Take(2),
            ticket: Ticket(committed: false)));
    }

    [Fact]
    public void Ticket_prepared_persists_a_versioned_commitment_and_rejects_every_bound_field_mutation()
    {
        var offers = Offers();
        var evidence = Commitment(offers);
        var record = Record(
            ManifestFlowState.PrepareTicket(),
            offers,
            ticket: Ticket(committed: false),
            commitment: evidence);

        Assert.Same(evidence, record.Commitment);
        Assert.Equal(ManifestCommitmentEvidence.CurrentVersion, record.Commitment.Version);
        Assert.Equal(64, record.Commitment.CommitmentSha256Hex.Length);
        Assert.True(record.Commitment.VerifyReveal(record.ManifestId, record.CatalogSnapshotId, record.Offers));

        Assert.Throws<ArgumentException>(() => Record(
            ManifestFlowState.PrepareTicket(),
            offers,
            ticket: Ticket(committed: false),
            commitment: evidence,
            manifestId: "manifest-mutated"));
        Assert.Throws<ArgumentException>(() => Record(
            ManifestFlowState.PrepareTicket(),
            offers,
            ticket: Ticket(committed: false),
            commitment: evidence,
            catalogSnapshotId: "catalog-mutated"));
        Assert.Throws<ArgumentException>(() => Record(
            ManifestFlowState.PrepareTicket(),
            offers,
            ticket: Ticket(committed: false),
            commitment: new ManifestCommitmentEvidence(
                evidence.Version,
                evidence.CommitmentSha256Hex,
                new string('b', ManifestCommitmentEvidence.NonceByteCount * 2))));
        Assert.Throws<ArgumentException>(() => Record(
            ManifestFlowState.PrepareTicket(),
            offers,
            ticket: Ticket(committed: false),
            commitment: new ManifestCommitmentEvidence(
                evidence.Version,
                "0" + evidence.CommitmentSha256Hex[1..],
                evidence.NonceHex)));

        var mutations = new[]
        {
            ReplaceOffer(offers, 0, Reidentify(offers[0], familyId: "family-mutated")),
            ReplaceOffer(offers, 0, Reidentify(offers[0], providerId: "provider-mutated")),
            ReplaceOffer(offers, 0, Reidentify(offers[0], lotId: "lot-mutated")),
            ReplaceOffer(offers, 0, Reidentify(offers[0], forestSeed: 91)),
            ReplaceOffer(offers, 0, Reidentify(offers[0], rarity: RewardRarity.BlackLabel)),
            ReplaceOffer(offers, 0, Reidentify(
                offers[0],
                rngEvidence: new CanonicalRngEvidence(ManifestRngPurpose.OfferSelection, 1, 99)))
        };
        foreach (var mutatedOffers in mutations)
        {
            Assert.Throws<ArgumentException>(() => Record(
                ManifestFlowState.PrepareTicket(),
                mutatedOffers,
                ticket: Ticket(committed: false),
                commitment: evidence));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new ManifestCommitmentEvidence(
            ManifestCommitmentEvidence.CurrentVersion + 1,
            evidence.CommitmentSha256Hex,
            evidence.NonceHex));
        Assert.Throws<ArgumentException>(() => new ManifestCommitmentEvidence(
            evidence.Version,
            evidence.CommitmentSha256Hex.ToUpperInvariant(),
            evidence.NonceHex));
    }

    [Fact]
    public void Semantic_snapshot_rejects_a_corrupt_persisted_fingerprint()
    {
        var lot = Lot("provider-a", "lot-a", "family-a", "track-a", RewardRarity.ScavGrade, 1);
        var corrupt = new RewardForestFingerprintV2(new string('0', 64));

        var exception = Assert.Throws<ArgumentException>(() => new ManifestOfferSnapshot(
            1,
            lot.Rarity,
            lot.Identity,
            lot.Forest,
            corrupt,
            OfferRng(1)));

        Assert.Contains("fingerprint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manifest_identifiers_reject_invalid_unicode_and_use_utf8_byte_bounds()
    {
        var state = ManifestFlowState.PrepareTicket();
        var offers = Offers();

        Assert.Throws<ArgumentException>(() => new ManifestRecord(
            "\ud800",
            "catalog-1",
            Commitment(offers),
            state,
            Ticket(committed: false),
            offers,
            decisions: null,
            entitlement: null,
            relayCandidates: null,
            relayHistory: null,
            brokerFavor: 0));
        Assert.Throws<ArgumentException>(() => new ManifestRecord(
            new string('\u00e9', 257),
            "catalog-1",
            Commitment(offers),
            state,
            Ticket(committed: false),
            offers,
            decisions: null,
            entitlement: null,
            relayCandidates: null,
            relayHistory: null,
            brokerFavor: 0));
    }

    [Fact]
    public void Later_phases_require_committed_ticket_evidence()
    {
        var offers = Offers();
        var offerOne = ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket());

        Assert.Throws<ArgumentException>(() => Record(
            offerOne,
            offers,
            ticket: Ticket(committed: false)));
        Assert.Throws<ArgumentException>(() => Record(
            ManifestFlowState.PrepareTicket(),
            offers,
            ticket: Ticket(committed: true)));
    }

    [Fact]
    public void Claim_prepared_accepts_and_isolates_an_exact_multi_root_payload()
    {
        var offers = Offers(firstForestIsMultiRoot: true);
        var entitlementState = LockFirstOffer();
        var claimState = ManifestStateMachine.PrepareClaim(entitlementState);
        var entitlement = Entitlement(offers[0]);
        var rootA = Id(101);
        var childA = Id(102);
        var rootB = Id(103);
        var items = new[]
        {
            Physical(rootA, offers[0].Forest.Nodes[0].TemplateId, "stash"),
            Physical(childA, offers[0].Forest.Nodes[1].TemplateId, rootA.ToString(), "slot-a"),
            Physical(rootB, offers[0].Forest.Nodes[2].TemplateId, "stash")
        };
        var payload = new ManifestClaimPreparedPayload(items, [rootA, rootB], false, PreparedAt);

        var record = Record(
            claimState,
            offers,
            decisions: LockFirstDecision(),
            entitlement: entitlement,
            claimPrepared: payload);

        items[0] = items[0] with { Template = Id(999) };
        var projected = record.ClaimPrepared!.Items[0];
        projected.Upd!.StackObjectsCount = 9;
        Assert.Equal([rootA, rootB], record.ClaimPrepared.RootIds);
        Assert.Equal(3, record.ClaimPrepared.ExactItemIds.Count);
        Assert.Equal(1d, record.ClaimPrepared.Items[0].Upd!.StackObjectsCount);
        Assert.Equal(offers[0].Forest.Nodes[0].TemplateId, record.ClaimPrepared.Items[0].Template.ToString());
    }

    [Fact]
    public void Phase_and_prepared_payload_must_agree()
    {
        var offers = Offers(firstForestIsMultiRoot: true);
        var entitlementState = LockFirstOffer();
        var entitlement = Entitlement(offers[0]);
        var rootA = Id(111);
        var childA = Id(112);
        var rootB = Id(113);
        var claim = new ManifestClaimPreparedPayload(
            [
                Physical(rootA, offers[0].Forest.Nodes[0].TemplateId, "stash"),
                Physical(childA, offers[0].Forest.Nodes[1].TemplateId, rootA.ToString(), "slot-a"),
                Physical(rootB, offers[0].Forest.Nodes[2].TemplateId, "stash")
            ],
            [rootA, rootB],
            false,
            PreparedAt);

        Assert.Throws<ArgumentException>(() => Record(
            entitlementState,
            offers,
            decisions: LockFirstDecision(),
            entitlement: entitlement,
            claimPrepared: claim));

        var relayState = ManifestStateMachine.PrepareRelay(entitlementState, relayEligible: true);
        Assert.Throws<ArgumentException>(() => Record(
            relayState,
            offers,
            decisions: LockFirstDecision(),
            entitlement: entitlement));
    }

    [Theory]
    [InlineData("location")]
    [InlineData("stable-state")]
    public void Claim_prepared_rejects_physical_state_that_contradicts_the_semantic_forest(string mutation)
    {
        var offers = Offers(firstForestIsMultiRoot: true);
        var entitlement = Entitlement(offers[0]);
        var rootA = Id(121);
        var childA = Id(122);
        var rootB = Id(123);
        var items = new[]
        {
            Physical(rootA, offers[0].Forest.Nodes[0].TemplateId, "stash"),
            Physical(childA, offers[0].Forest.Nodes[1].TemplateId, rootA.ToString(), "slot-a"),
            Physical(rootB, offers[0].Forest.Nodes[2].TemplateId, "stash")
        };
        if (mutation == "location")
        {
            items[1].Location = new ItemLocation { X = 1, Y = 1 };
        }
        else
        {
            items[0].Upd!.Repairable = new UpdRepairable { Durability = 10, MaxDurability = 10 };
        }
        var payload = new ManifestClaimPreparedPayload(items, [rootA, rootB], false, PreparedAt);

        Assert.Throws<ArgumentException>(() => Record(
            ManifestStateMachine.PrepareClaim(LockFirstOffer()),
            offers,
            decisions: LockFirstDecision(),
            entitlement: entitlement,
            claimPrepared: payload));
    }

    [Fact]
    public void Claim_prepared_rejects_unknown_or_conflicting_rotation_representations()
    {
        var offers = Offers(firstForestIsMultiRoot: true, firstForestHasInternalLocation: true);
        var entitlement = Entitlement(offers[0]);
        var rootA = Id(131);
        var childA = Id(132);
        var rootB = Id(133);

        ManifestClaimPreparedPayload Payload(ItemLocation location)
        {
            var child = Physical(childA, offers[0].Forest.Nodes[1].TemplateId, rootA.ToString(), "slot-a");
            child.Location = location;
            return new ManifestClaimPreparedPayload(
                [
                    Physical(rootA, offers[0].Forest.Nodes[0].TemplateId, "stash"),
                    child,
                    Physical(rootB, offers[0].Forest.Nodes[2].TemplateId, "stash")
                ],
                [rootA, rootB],
                false,
                PreparedAt);
        }

        var claimState = ManifestStateMachine.PrepareClaim(LockFirstOffer());
        var valid = Record(
            claimState,
            offers,
            decisions: LockFirstDecision(),
            entitlement: entitlement,
            claimPrepared: Payload(new ItemLocation
            {
                X = 1,
                Y = 2,
                R = ItemRotation.Horizontal,
                Rotation = false
            }));
        Assert.NotNull(valid.ClaimPrepared);

        Assert.Throws<ArgumentException>(() => Record(
            claimState,
            offers,
            decisions: LockFirstDecision(),
            entitlement: entitlement,
            claimPrepared: Payload(new ItemLocation
            {
                X = 1,
                Y = 2,
                R = ItemRotation.Horizontal,
                Rotation = true
            })));
        Assert.Throws<ArgumentException>(() => Record(
            claimState,
            offers,
            decisions: LockFirstDecision(),
            entitlement: entitlement,
            claimPrepared: Payload(new ItemLocation
            {
                X = 1,
                Y = 2,
                R = (ItemRotation)123
            })));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(501)]
    public void Claim_prepared_item_ids_cannot_reuse_consumed_ticket_inputs(int collidingId)
    {
        var offers = Offers();
        var rootId = Id(collidingId);
        var claim = new ManifestClaimPreparedPayload(
            [Physical(rootId, offers[0].Forest.Nodes[0].TemplateId, "stash")],
            [rootId],
            profileCommitStarted: true,
            PreparedAt);

        Assert.Throws<ArgumentException>(() => Record(
            ManifestStateMachine.PrepareClaim(LockFirstOffer()),
            offers,
            decisions: LockFirstDecision(),
            entitlement: Entitlement(offers[0]),
            claimPrepared: claim));
    }

    [Theory]
    [InlineData(RewardResourceKind.MedKit)]
    [InlineData(RewardResourceKind.RepairKit)]
    [InlineData(RewardResourceKind.FoodDrink)]
    [InlineData(RewardResourceKind.Generic)]
    public void Claim_payload_binds_exactly_one_physical_resource_subtype(
        RewardResourceKind resourceKind)
    {
        var entitlement = ResourceEntitlement(resourceKind);
        var rootId = Id(710);

        ManifestClaimPreparedPayload Payload(params RewardResourceKind[] physicalKinds)
        {
            var item = Physical(rootId, entitlement.Forest.Nodes[0].TemplateId, "stash");
            foreach (var physicalKind in physicalKinds)
            {
                SetPhysicalResource(item.Upd!, physicalKind, 5d);
            }
            return new ManifestClaimPreparedPayload(
                [item],
                [rootId],
                profileCommitStarted: true,
                PreparedAt);
        }

        var valid = new ManifestClaimGrantRecord(
            "manifest-resource-valid",
            entitlement,
            Payload(resourceKind),
            CommittedAt);
        Assert.Equal(resourceKind, valid.Entitlement.Forest.Nodes[0].StableState!.ResourceKind);

        var wrongKind = (RewardResourceKind)(((int)resourceKind + 1) % 4);
        Assert.Throws<ArgumentException>(() => new ManifestClaimGrantRecord(
            "manifest-resource-wrong",
            entitlement,
            Payload(wrongKind),
            CommittedAt));
        Assert.Throws<ArgumentException>(() => new ManifestClaimGrantRecord(
            "manifest-resource-extra",
            entitlement,
            Payload(resourceKind, wrongKind),
            CommittedAt));
    }

    [Fact]
    public void Frozen_relay_candidates_are_canonical_and_confined_to_track_and_grade()
    {
        var offers = Offers();
        var state = LockFirstOffer();
        var entitlement = Entitlement(offers[0]);
        var upgrade = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Uncommon,
            "upgrade",
            "track-a",
            20);
        var sidegrade = Candidate(
            ManifestRelayResult.Sidegrade,
            RewardRarity.ScavGrade,
            "sidegrade",
            "track-a",
            21);

        var record = Record(
            state,
            offers,
            decisions: LockFirstDecision(),
            entitlement: entitlement,
            relayCandidates: [sidegrade, upgrade]);

        Assert.Equal(ManifestRelayResult.Upgrade, record.RelayCandidates[0].TargetResult);
        Assert.Equal(ManifestRelayResult.Sidegrade, record.RelayCandidates[1].TargetResult);

        var crossTrack = Candidate(
            ManifestRelayResult.Sidegrade,
            RewardRarity.ScavGrade,
            "cross-track",
            "track-b",
            22);
        Assert.Throws<ArgumentException>(() => Record(
            state,
            offers,
            decisions: LockFirstDecision(),
            entitlement: entitlement,
            relayCandidates: [upgrade, crossTrack]));

        var wrongGrade = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Restricted,
            "wrong-grade",
            "track-a",
            23);
        Assert.Throws<ArgumentException>(() => Record(
            state,
            offers,
            decisions: LockFirstDecision(),
            entitlement: entitlement,
            relayCandidates: [wrongGrade, sidegrade]));
    }

    [Fact]
    public void Relay_rejects_the_staked_semantic_identity_even_when_the_candidate_grade_is_changed()
    {
        var offers = Offers();
        var stake = offers[0];
        var entitlement = Entitlement(stake);
        var sameStakeUpgrade = new ManifestRelayCandidateSnapshot(
            ManifestRelayResult.Upgrade,
            RewardRarity.Uncommon,
            stake.Identity,
            stake.Forest,
            stake.Fingerprint);
        var sidegrade = Candidate(
            ManifestRelayResult.Sidegrade,
            RewardRarity.ScavGrade,
            "stake-sidegrade",
            "track-a",
            24);

        Assert.Throws<ArgumentException>(() => Record(
            LockFirstOffer(),
            offers,
            decisions: LockFirstDecision(),
            entitlement: entitlement,
            relayCandidates: [sameStakeUpgrade, sidegrade]));

        var sameStakeReceipt = new ManifestLotReceiptSnapshot(
            RewardRarity.Uncommon,
            stake.Identity,
            stake.Fingerprint);
        Assert.Throws<ArgumentException>(() => new ManifestRelayReceipt(
            1,
            Receipt(stake),
            ManifestRelayResult.Upgrade,
            sameStakeReceipt,
            RelayRules.GetOdds(1),
            RelayOutcomeRng(0),
            RelayTargetRng(0),
            [sameStakeReceipt],
            0,
            0,
            CommittedAt));
    }

    [Fact]
    public void Relay_prepared_requires_output_from_the_frozen_same_track_pool()
    {
        var offers = Offers();
        var entitlementState = LockFirstOffer();
        var relayState = ManifestStateMachine.PrepareRelay(entitlementState, relayEligible: true);
        var entitlement = Entitlement(offers[0]);
        var upgrade = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Uncommon,
            "upgrade",
            "track-a",
            30);
        var sidegrade = Candidate(
            ManifestRelayResult.Sidegrade,
            RewardRarity.ScavGrade,
            "sidegrade",
            "track-a",
            31);
        var prepared = new ManifestRelayPreparedPayload(
            Id(400),
            ManifestRelayResult.Upgrade,
            Entitlement(upgrade),
            RelayRules.GetOdds(1),
            RelayOutcomeRng(0),
            RelayTargetRng(1),
            0,
            0,
            false,
            PreparedAt,
            NextRelayCandidates());

        var record = Record(
            relayState,
            offers,
            decisions: LockFirstDecision(),
            entitlement: entitlement,
            relayCandidates: [sidegrade, upgrade],
            relayPrepared: prepared);

        Assert.Equal(upgrade.Fingerprint, record.RelayPrepared!.Output!.Fingerprint);
        Assert.True(record.RequiresExclusiveProfileMutation);

        var foreignOutput = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Uncommon,
            "foreign",
            "track-a",
            32);
        var foreignPrepared = new ManifestRelayPreparedPayload(
            Id(401),
            ManifestRelayResult.Upgrade,
            Entitlement(foreignOutput),
            RelayRules.GetOdds(1),
            RelayOutcomeRng(0),
            RelayTargetRng(1),
            0,
            0,
            false,
            PreparedAt,
            NextRelayCandidates());
        Assert.Throws<ArgumentException>(() => Record(
            relayState,
            offers,
            decisions: LockFirstDecision(),
            entitlement: entitlement,
            relayCandidates: [sidegrade, upgrade],
            relayPrepared: foreignPrepared));
    }

    [Fact]
    public void Relay_target_draw_selects_the_canonical_weighted_candidate_at_exact_boundaries()
    {
        var offers = Offers();
        var relayState = ManifestStateMachine.PrepareRelay(LockFirstOffer(), relayEligible: true);
        var upgradeA = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Uncommon,
            "a-upgrade",
            "track-a",
            33,
            weight: 1d);
        var upgradeB = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Uncommon,
            "b-upgrade",
            "track-a",
            34,
            weight: 3d);
        var sidegrade = Candidate(
            ManifestRelayResult.Sidegrade,
            RewardRarity.ScavGrade,
            "sidegrade-boundary",
            "track-a",
            35);
        var quarter = CanonicalRngEvidence.UnitDenominator / 4;
        var cases = new[]
        {
            (Numerator: 0L, Expected: upgradeA, Altered: upgradeB),
            (Numerator: quarter - 1, Expected: upgradeA, Altered: upgradeB),
            (Numerator: quarter, Expected: upgradeB, Altered: upgradeA),
            (Numerator: CanonicalRngEvidence.UnitDenominator - 1, Expected: upgradeB, Altered: upgradeA)
        };

        foreach (var item in cases)
        {
            var prepared = RelayPrepared(item.Expected, item.Numerator);
            var record = Record(
                relayState,
                offers,
                decisions: LockFirstDecision(),
                entitlement: Entitlement(offers[0]),
                relayCandidates: [sidegrade, upgradeB, upgradeA],
                relayPrepared: prepared);
            Assert.Equal(item.Expected.Fingerprint, record.RelayPrepared!.Output!.Fingerprint);

            Assert.Throws<ArgumentException>(() => Record(
                relayState,
                offers,
                decisions: LockFirstDecision(),
                entitlement: Entitlement(offers[0]),
                relayCandidates: [upgradeA, sidegrade, upgradeB],
                relayPrepared: RelayPrepared(item.Altered, item.Numerator)));
        }

        Assert.Throws<ArgumentException>(() => Record(
            relayState,
            offers,
            decisions: LockFirstDecision(),
            entitlement: Entitlement(offers[0]),
            relayCandidates: [upgradeA, upgradeB, sidegrade],
            relayPrepared: new ManifestRelayPreparedPayload(
                Id(402),
                ManifestRelayResult.Upgrade,
                Entitlement(upgradeA),
                RelayRules.GetOdds(1),
                RelayOutcomeRng(0),
                new CanonicalRngEvidence(ManifestRngPurpose.RelayTargetSelection, 2, 0),
                0,
                0,
                false,
                PreparedAt,
                NextRelayCandidates())));
    }

    [Fact]
    public void Committed_relay_receipt_verifies_frozen_target_choices_and_rejects_an_altered_output()
    {
        var offers = Offers();
        var upgradeA = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Uncommon,
            "a-receipt",
            "track-a",
            36,
            weight: 1d);
        var upgradeB = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Uncommon,
            "b-receipt",
            "track-a",
            37,
            weight: 3d);
        var eligible = new[] { Receipt(upgradeB), Receipt(upgradeA) };
        var boundary = CanonicalRngEvidence.UnitDenominator / 4;

        var receipt = new ManifestRelayReceipt(
            1,
            Receipt(offers[0]),
            ManifestRelayResult.Upgrade,
            Receipt(upgradeB),
            RelayRules.GetOdds(1),
            RelayOutcomeRng(0),
            RelayTargetRng(boundary),
            eligible,
            0,
            0,
            CommittedAt);

        Assert.Equal("a-receipt", receipt.EligibleTargets[0].Identity.LotId);
        Assert.Equal("b-receipt", receipt.Output!.Identity.LotId);
        Assert.Throws<ArgumentException>(() => new ManifestRelayReceipt(
            1,
            Receipt(offers[0]),
            ManifestRelayResult.Upgrade,
            Receipt(upgradeA),
            RelayRules.GetOdds(1),
            RelayOutcomeRng(0),
            RelayTargetRng(boundary),
            eligible,
            0,
            0,
            CommittedAt));
        Assert.Throws<ArgumentException>(() => new ManifestRelayReceipt(
            1,
            Receipt(offers[0]),
            ManifestRelayResult.Upgrade,
            Receipt(upgradeB),
            RelayRules.GetOdds(1),
            RelayOutcomeRng(0),
            new CanonicalRngEvidence(ManifestRngPurpose.RelayTargetSelection, 2, boundary),
            eligible,
            0,
            0,
            CommittedAt));
    }

    [Fact]
    public void Completed_relay_history_preserves_auditable_semantics_and_favor()
    {
        var offers = Offers();
        var entitlementState = LockFirstOffer();
        var relayState = ManifestStateMachine.PrepareRelay(entitlementState, relayEligible: true);
        var upgradedState = ManifestStateMachine.CompleteRelay(
            relayState,
            ManifestRelayResult.Upgrade,
            RewardRarity.Uncommon);
        var output = Lot(
            "provider-output",
            "lot-output",
            "family-output",
            "track-a",
            RewardRarity.Uncommon,
            40);
        var receipt = new ManifestRelayReceipt(
            1,
            Receipt(offers[0]),
            ManifestRelayResult.Upgrade,
            Receipt(output),
            RelayRules.GetOdds(1),
            RelayOutcomeRng(0),
            RelayTargetRng(1),
            [Receipt(output)],
            0,
            0,
            CommittedAt);
        var stageTwoUpgrade = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Contractor,
            "stage-two-upgrade",
            "track-a",
            41);
        var stageTwoSidegrade = Candidate(
            ManifestRelayResult.Sidegrade,
            RewardRarity.Uncommon,
            "stage-two-sidegrade",
            "track-a",
            42);

        var record = Record(
            upgradedState,
            offers,
            decisions: LockFirstDecision(),
            entitlement: Entitlement(output),
            relayCandidates: [stageTwoSidegrade, stageTwoUpgrade],
            relayHistory: [receipt]);

        Assert.Same(receipt, Assert.Single(record.RelayHistory));
        Assert.Equal(RewardRarity.Uncommon, record.Entitlement!.Rarity);
        Assert.Equal(2, record.FlowState.RelayStage);
    }

    [Fact]
    public void Relay_terminal_marker_is_derived_from_the_latest_committed_receipt()
    {
        var offers = Offers();
        var locked = LockFirstOffer();
        var initialEntitlement = Entitlement(offers[0]);
        var forgedInitialTerminal = ManifestFlowState.Restore(
            ManifestPhase.Entitlement,
            1,
            1,
            1,
            relayTerminal: true);
        Assert.Throws<ArgumentException>(() => Record(
            forgedInitialTerminal,
            offers,
            decisions: LockFirstDecision(),
            entitlement: initialEntitlement));

        var upgrade = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Uncommon,
            "terminal-upgrade",
            "track-a",
            43);
        var upgradeReceipt = new ManifestRelayReceipt(
            1,
            Receipt(offers[0]),
            ManifestRelayResult.Upgrade,
            Receipt(upgrade),
            RelayRules.GetOdds(1),
            RelayOutcomeRng(0),
            RelayTargetRng(0),
            [Receipt(upgrade)],
            0,
            0,
            CommittedAt);
        var ordinaryUpgradeState = ManifestStateMachine.CompleteRelay(
            ManifestStateMachine.PrepareRelay(locked, relayEligible: true),
            ManifestRelayResult.Upgrade,
            RewardRarity.Uncommon);
        Assert.False(Record(
            ordinaryUpgradeState,
            offers,
            decisions: LockFirstDecision(),
            entitlement: Entitlement(upgrade),
            relayHistory: [upgradeReceipt]).FlowState.RelayTerminal);
        var forgedUpgradeTerminal = ManifestFlowState.Restore(
            ManifestPhase.Entitlement,
            1,
            1,
            2,
            relayTerminal: true);
        Assert.Throws<ArgumentException>(() => Record(
            forgedUpgradeTerminal,
            offers,
            decisions: LockFirstDecision(),
            entitlement: Entitlement(upgrade),
            relayHistory: [upgradeReceipt]));

        var sidegrade = Candidate(
            ManifestRelayResult.Sidegrade,
            RewardRarity.ScavGrade,
            "terminal-sidegrade",
            "track-a",
            44);
        var sidegradeReceipt = new ManifestRelayReceipt(
            1,
            Receipt(offers[0]),
            ManifestRelayResult.Sidegrade,
            Receipt(sidegrade),
            RelayRules.GetOdds(1),
            SidegradeOutcomeRng(1),
            RelayTargetRng(0),
            [Receipt(sidegrade)],
            0,
            0,
            CommittedAt);
        var sidegradeState = ManifestStateMachine.CompleteRelay(
            ManifestStateMachine.PrepareRelay(locked, relayEligible: true),
            ManifestRelayResult.Sidegrade,
            RewardRarity.ScavGrade);
        Assert.True(Record(
            sidegradeState,
            offers,
            decisions: LockFirstDecision(),
            entitlement: Entitlement(sidegrade),
            relayHistory: [sidegradeReceipt]).FlowState.RelayTerminal);
        var forgedSidegradeNonterminal = ManifestFlowState.Restore(
            ManifestPhase.Entitlement,
            1,
            1,
            1,
            relayTerminal: false);
        Assert.Throws<ArgumentException>(() => Record(
            forgedSidegradeNonterminal,
            offers,
            decisions: LockFirstDecision(),
            entitlement: Entitlement(sidegrade),
            relayHistory: [sidegradeReceipt]));
    }

    [Fact]
    public void Black_label_upgrade_receipt_requires_relay_terminal_state_before_maximum_stage()
    {
        var offers = Offers();
        var active = ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket());
        var offerTwo = ManifestStateMachine.DecideOffer(active, ManifestOfferDecision.Burn);
        var lockedThird = ManifestStateMachine.DecideOffer(offerTwo, ManifestOfferDecision.Burn);
        var decisions = new[]
        {
            new ManifestDecisionRecord(1, ManifestOfferDecision.Burn, PreparedAt),
            new ManifestDecisionRecord(2, ManifestOfferDecision.Burn, CommittedAt)
        };
        var blackLabel = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.BlackLabel,
            "early-black-label",
            "track-c",
            45);
        var receipt = new ManifestRelayReceipt(
            1,
            Receipt(offers[2]),
            ManifestRelayResult.Upgrade,
            Receipt(blackLabel),
            RelayRules.GetOdds(1),
            RelayOutcomeRng(0),
            RelayTargetRng(0),
            [Receipt(blackLabel)],
            0,
            0,
            CommittedAt);
        var terminalState = ManifestStateMachine.CompleteRelay(
            ManifestStateMachine.PrepareRelay(lockedThird, relayEligible: true),
            ManifestRelayResult.Upgrade,
            RewardRarity.BlackLabel);
        Assert.True(Record(
            terminalState,
            offers,
            decisions: decisions,
            entitlement: Entitlement(blackLabel),
            relayHistory: [receipt]).FlowState.RelayTerminal);

        var forgedNonterminal = ManifestFlowState.Restore(
            ManifestPhase.Entitlement,
            3,
            3,
            2,
            relayTerminal: false);
        Assert.Throws<ArgumentException>(() => Record(
            forgedNonterminal,
            offers,
            decisions: decisions,
            entitlement: Entitlement(blackLabel),
            relayHistory: [receipt]));
    }

    [Fact]
    public void Forfeit_is_terminal_without_inventory_or_favor_mutation()
    {
        var offers = Offers();
        var entitlementState = LockFirstOffer();
        var forfeitedState = ManifestStateMachine.ForfeitMissingContent(entitlementState, missingContentBlocked: true);
        var decisions = LockFirstDecision();
        var receipt = new ManifestTerminalReceipt(
            "manifest-1",
            ManifestPhase.Forfeited,
            offers.Select(Receipt),
            Receipt(offers[0]),
            decisions,
            relayHistory: [],
            brokerFavorBefore: 2,
            brokerFavorAfter: 2,
            completedAtUtc: CommittedAt);

        var record = Record(
            forfeitedState,
            offers,
            decisions: decisions,
            brokerFavor: 2,
            terminalReceipt: receipt);

        Assert.True(record.IsTerminal);
        Assert.False(record.RequiresExclusiveProfileMutation);
        Assert.Null(record.Entitlement);
        Assert.Null(record.ClaimPrepared);
        Assert.Null(record.RelayPrepared);
        Assert.Equal(2, record.TerminalReceipt!.BrokerFavorAfter);
        Assert.Empty(record.TerminalReceipt.RelayHistory);
        Assert.Throws<ArgumentException>(() => new ManifestTerminalReceipt(
            "manifest-1",
            ManifestPhase.Forfeited,
            offers.Select(Receipt),
            Receipt(offers[0]),
            decisions,
            relayHistory: [],
            brokerFavorBefore: 2,
            brokerFavorAfter: 3,
            completedAtUtc: CommittedAt));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void Broker_favor_is_bounded(int brokerFavor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Record(
            ManifestFlowState.PrepareTicket(),
            Offers(),
            ticket: Ticket(committed: false),
            brokerFavor: brokerFavor));
    }

    private static ManifestRecord Record(
        ManifestFlowState state,
        IEnumerable<ManifestOfferSnapshot> offers,
        ManifestTicketPayload? ticket = null,
        IEnumerable<ManifestDecisionRecord>? decisions = null,
        ManifestEntitlementSnapshot? entitlement = null,
        IEnumerable<ManifestRelayCandidateSnapshot>? relayCandidates = null,
        IEnumerable<ManifestRelayReceipt>? relayHistory = null,
        int brokerFavor = 0,
        ManifestClaimPreparedPayload? claimPrepared = null,
        ManifestRelayPreparedPayload? relayPrepared = null,
        ManifestTerminalReceipt? terminalReceipt = null,
        ManifestCommitmentEvidence? commitment = null,
        string manifestId = "manifest-1",
        string catalogSnapshotId = "catalog-1")
    {
        var copiedOffers = offers.ToArray();
        return new ManifestRecord(
            manifestId,
            catalogSnapshotId,
            commitment ?? Commitment(copiedOffers, manifestId, catalogSnapshotId),
            state,
            ticket ?? Ticket(committed: state.Phase != ManifestPhase.TicketPrepared),
            copiedOffers,
            decisions,
            entitlement,
            relayCandidates,
            relayHistory,
            brokerFavor,
            claimPrepared,
            relayPrepared,
            terminalReceipt);
    }

    private static ManifestTicketPayload Ticket(bool committed) => new(
        Id(500),
        Id(501),
        PreparedAt,
        profileCommitStarted: committed,
        committed,
        committed ? CommittedAt : null);

    private static ManifestCommitmentEvidence Commitment(
        IEnumerable<ManifestOfferSnapshot> offers,
        string manifestId = "manifest-1",
        string catalogSnapshotId = "catalog-1",
        string? nonceHex = null) =>
        ManifestCommitmentEvidence.Create(
            manifestId,
            catalogSnapshotId,
            nonceHex ?? new string('a', ManifestCommitmentEvidence.NonceByteCount * 2),
            offers);

    private static ManifestFlowState LockFirstOffer() => ManifestStateMachine.DecideOffer(
        ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket()),
        ManifestOfferDecision.Lock);

    private static ManifestDecisionRecord[] LockFirstDecision() =>
        [new(1, ManifestOfferDecision.Lock, CommittedAt)];

    private static ManifestOfferSnapshot[] Offers(
        bool firstForestIsMultiRoot = false,
        bool firstForestHasInternalLocation = false)
    {
        var lots = new[]
        {
            Lot(
                "provider-a",
                "lot-a",
                "family-a",
                "track-a",
                RewardRarity.ScavGrade,
                1,
                firstForestIsMultiRoot,
                childHasInternalLocation: firstForestHasInternalLocation),
            Lot("provider-b", "lot-b", "family-b", "track-b", RewardRarity.Contractor, 4),
            Lot("provider-c", "lot-c", "family-c", "track-c", RewardRarity.Restricted, 5)
        };
        return lots.Select((lot, index) => new ManifestOfferSnapshot(
            index + 1,
            lot.Rarity,
            lot.Identity,
            lot.Forest,
            lot.Fingerprint,
            OfferRng(index + 1))).ToArray();
    }

    private static ManifestOfferSnapshot[] ReplaceOffer(
        IReadOnlyList<ManifestOfferSnapshot> offers,
        int index,
        ManifestOfferSnapshot replacement)
    {
        var copy = offers.ToArray();
        copy[index] = replacement;
        return copy;
    }

    private static ManifestOfferSnapshot Reidentify(
        ManifestOfferSnapshot offer,
        string? familyId = null,
        string? providerId = null,
        string? lotId = null,
        int? forestSeed = null,
        RewardRarity? rarity = null,
        CanonicalRngEvidence? rngEvidence = null)
    {
        var provider = providerId ?? offer.Identity.ProviderId;
        var lot = lotId ?? offer.Identity.LotId;
        var forest = forestSeed is int seed
            ? RewardForest.Create([
                new RewardForestNode("root", "root", Id(seed).ToString(), null, null, null, 1)
            ])
            : offer.Forest;
        var fingerprint = RewardForestFingerprintV2.Compute(provider, lot, forest);
        var identity = new CargoLotIdentitySnapshot(
            provider,
            offer.Identity.PackVersion,
            lot,
            offer.Identity.DisplayName,
            offer.Identity.Purpose,
            new FamilyId(familyId ?? offer.Identity.FamilyId.Value),
            offer.Identity.TrackId,
            forest.Nodes[0].TemplateId,
            offer.Identity.Weight,
            offer.Identity.UsePath,
            fingerprint);
        return new ManifestOfferSnapshot(
            offer.Ordinal,
            rarity ?? offer.Rarity,
            identity,
            forest,
            fingerprint,
            rngEvidence ?? offer.RngEvidence);
    }

    private static ManifestRelayCandidateSnapshot Candidate(
        ManifestRelayResult result,
        RewardRarity rarity,
        string lotId,
        string trackId,
        int seed,
        double weight = 1d)
    {
        var lot = Lot("provider-" + lotId, lotId, "family-relay", trackId, rarity, seed, weight: weight);
        return new ManifestRelayCandidateSnapshot(result, rarity, lot.Identity, lot.Forest, lot.Fingerprint);
    }

    private static ManifestRelayPreparedPayload RelayPrepared(
        ManifestRelayCandidateSnapshot output,
        long targetNumerator) => new(
        Id(403),
        ManifestRelayResult.Upgrade,
        Entitlement(output),
        RelayRules.GetOdds(1),
        RelayOutcomeRng(0),
        RelayTargetRng(targetNumerator),
        0,
        0,
        false,
        PreparedAt,
        NextRelayCandidates());

    private static ManifestRelayCandidateSnapshot[] NextRelayCandidates() =>
    [
        Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Contractor,
            "next-upgrade",
            "track-a",
            90),
        Candidate(
            ManifestRelayResult.Sidegrade,
            RewardRarity.Uncommon,
            "next-sidegrade",
            "track-a",
            91)
    ];

    private static ManifestEntitlementSnapshot Entitlement(ManifestOfferSnapshot offer) => new(
        offer.Rarity,
        offer.Identity,
        offer.Forest,
        offer.Fingerprint);

    private static ManifestEntitlementSnapshot Entitlement(ManifestRelayCandidateSnapshot candidate) => new(
        candidate.Rarity,
        candidate.Identity,
        candidate.Forest,
        candidate.Fingerprint);

    private static ManifestEntitlementSnapshot Entitlement(LotData lot) => new(
        lot.Rarity,
        lot.Identity,
        lot.Forest,
        lot.Fingerprint);

    private static ManifestEntitlementSnapshot ResourceEntitlement(RewardResourceKind resourceKind)
    {
        const string providerId = "provider-resource";
        const string lotId = "lot-resource";
        var forest = RewardForest.Create([
            new RewardForestNode(
                "root",
                "root",
                Id(700).ToString(),
                parentLogicalPath: null,
                slotId: null,
                internalLocation: null,
                stackCount: 1,
                new RewardStableState(
                    resourceValue: 5m,
                    maximumResourceValue: 10m,
                    resourceKind: resourceKind))
        ]);
        var fingerprint = RewardForestFingerprintV2.Compute(providerId, lotId, forest);
        var identity = new CargoLotIdentitySnapshot(
            providerId,
            "1.0.0",
            lotId,
            "Resource lot",
            "Resource-kind validation",
            new FamilyId("family-resource"),
            new TrackId("track-resource"),
            forest.Nodes[0].TemplateId,
            1d,
            new RaidRole("role-resource"),
            fingerprint);
        return new ManifestEntitlementSnapshot(
            RewardRarity.ScavGrade,
            identity,
            forest,
            fingerprint);
    }

    private static void SetPhysicalResource(Upd upd, RewardResourceKind resourceKind, double value)
    {
        switch (resourceKind)
        {
            case RewardResourceKind.MedKit:
                upd.MedKit = new UpdMedKit { HpResource = value };
                break;
            case RewardResourceKind.RepairKit:
                upd.RepairKit = new UpdRepairKit { Resource = value };
                break;
            case RewardResourceKind.FoodDrink:
                upd.FoodDrink = new UpdFoodDrink { HpPercent = value };
                break;
            case RewardResourceKind.Generic:
                upd.Resource = new UpdResource { Value = value };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(resourceKind));
        }
    }

    private static ManifestLotReceiptSnapshot Receipt(ManifestOfferSnapshot offer) => new(
        offer.Rarity,
        offer.Identity,
        offer.Fingerprint);

    private static ManifestLotReceiptSnapshot Receipt(LotData lot) => new(
        lot.Rarity,
        lot.Identity,
        lot.Fingerprint);

    private static ManifestLotReceiptSnapshot Receipt(ManifestRelayCandidateSnapshot candidate) => new(
        candidate.Rarity,
        candidate.Identity,
        candidate.Fingerprint);

    private static LotData Lot(
        string providerId,
        string lotId,
        string familyId,
        string trackId,
        RewardRarity rarity,
        int seed,
        bool multiRoot = false,
        double weight = 1d,
        bool childHasInternalLocation = false)
    {
        var nodes = multiRoot
            ? new[]
            {
                new RewardForestNode("root-a", "root-a", Id(seed).ToString(), null, null, null, 1),
                new RewardForestNode(
                    "root-a",
                    "root-a/child",
                    Id(seed + 1).ToString(),
                    "root-a",
                    "slot-a",
                    childHasInternalLocation
                        ? new CanonicalInternalLocation(1, 2, CanonicalRotation.Horizontal)
                        : null,
                    1),
                new RewardForestNode("root-b", "root-b", Id(seed + 2).ToString(), null, null, null, 1)
            }
            : [new RewardForestNode("root", "root", Id(seed).ToString(), null, null, null, 1)];
        var forest = RewardForest.Create(nodes);
        var fingerprint = RewardForestFingerprintV2.Compute(providerId, lotId, forest);
        var identity = new CargoLotIdentitySnapshot(
            providerId,
            "1.0.0",
            lotId,
            "Display " + lotId,
            "Purpose " + lotId,
            new FamilyId(familyId),
            new TrackId(trackId),
            nodes[0].TemplateId,
            weight,
            new RaidRole("role-" + lotId),
            fingerprint);
        return new LotData(rarity, identity, forest, fingerprint);
    }

    private static Item Physical(MongoId id, string templateId, string parentId, string slotId = "hideout") => new()
    {
        Id = id,
        Template = (MongoId)templateId,
        ParentId = parentId,
        SlotId = slotId,
        Upd = new Upd { StackObjectsCount = 1 }
    };

    private static CanonicalRngEvidence OfferRng(int ordinal) => new(
        ManifestRngPurpose.OfferSelection,
        ordinal,
        ordinal);

    private static CanonicalRngEvidence RelayOutcomeRng(long numerator, int drawOrdinal = 1) => new(
        ManifestRngPurpose.RelayOutcome,
        drawOrdinal,
        numerator);

    private static CanonicalRngEvidence SidegradeOutcomeRng(int stage)
    {
        var odds = RelayRules.GetOdds(stage);
        var numerator = checked(
            ((long)odds.UpgradePercent * CanonicalRngEvidence.UnitDenominator + 99L) / 100L);
        return RelayOutcomeRng(numerator, stage);
    }

    private static CanonicalRngEvidence RelayTargetRng(long numerator) => new(
        ManifestRngPurpose.RelayTargetSelection,
        1,
        numerator);

    private static MongoId Id(int value) => (MongoId)value.ToString("x24");

    private sealed record LotData(
        RewardRarity Rarity,
        CargoLotIdentitySnapshot Identity,
        RewardForest Forest,
        RewardForestFingerprintV2 Fingerprint);
}
