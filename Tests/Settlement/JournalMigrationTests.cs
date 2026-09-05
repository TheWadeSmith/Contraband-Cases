using System.Text.Json;
using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Utils.Json.Converters;
using Xunit;
using CargoCollection = ContrabandCases.Shared.Catalog.Collection;

namespace ContrabandCases.Tests.Settlement;

public sealed class JournalMigrationTests
{
    private static readonly DateTimeOffset PreparedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
    private static readonly DateTimeOffset CommittedAt = DateTimeOffset.UnixEpoch.AddMinutes(2);
    private static readonly string ManifestNonce = new('a', ManifestCommitmentEvidence.NonceByteCount * 2);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Legacy_schema_preserves_prepared_opening_meter_and_secured_roots_without_manifest_state(
        int schemaVersion)
    {
        var preparedRoot = Id(10);
        var committedRoot = Id(20);
        var explicitSecuredRoot = committedRoot;
        var document = new SptCaseJournalDocument
        {
            SchemaVersion = schemaVersion,
            RecoveryMeter = 2,
            Records =
            [
                new SptCaseOpeningDocument
                {
                    CaseId = Id(1),
                    KeyId = Id(2),
                    RewardId = "prepared-reward",
                    RewardItems = [Physical(preparedRoot, 11)],
                    PreparedAtUtc = PreparedAt,
                    Status = OpeningRecordStatus.Prepared
                },
                new SptCaseOpeningDocument
                {
                    CaseId = Id(3),
                    KeyId = Id(4),
                    RewardId = "committed-reward",
                    RewardItems = [Physical(committedRoot, 21)],
                    PreparedAtUtc = PreparedAt,
                    Status = OpeningRecordStatus.Committed,
                    CommittedAtUtc = CommittedAt
                }
            ],
            RelayRecords = [],
            LegacySecuredStakeRoots = [explicitSecuredRoot],
            ActiveManifest = null,
            ManifestReceipts = null
        };

        var journal = SptCaseJournal.FromDocument(document);

        Assert.Equal(2, journal.RecoveryMeter);
        Assert.Equal(preparedRoot, Assert.Single(journal.PreparedOpening!.ExactRewardIds));
        Assert.Equal((MongoId)Id(11), Assert.Single(journal.PreparedOpening.RewardItems).Template);
        Assert.Null(journal.ActiveManifest);
        Assert.Empty(journal.ManifestReceipts);
        var expectedSecuredRoot = schemaVersion <= 1 ? committedRoot : explicitSecuredRoot;
        Assert.True(journal.IsLegacySecured(expectedSecuredRoot));
        Assert.Single(journal.LegacySecuredStakeRoots);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Legacy_schema_preserves_prepared_relay_input_and_profile_commit_evidence(
        int schemaVersion)
    {
        var stakeRoot = Id(40);
        var input = Physical(stakeRoot, 41);
        input.Upd = new Upd { StackObjectsCount = 7 };
        var document = new SptCaseJournalDocument
        {
            SchemaVersion = schemaVersion,
            RecoveryMeter = 2,
            Records = [],
            RelayRecords =
            [
                new SptRelaySettlementDocument
                {
                    OriginCaseId = Id(42),
                    StakeRootId = stakeRoot,
                    InputItemIds = [stakeRoot],
                    InputItems = [input],
                    InputRewardId = "relay-input",
                    InputRarity = RewardRarity.ScavGrade,
                    Stage = 1,
                    Action = RelayRecordAction.Relay,
                    KeyId = Id(43),
                    Outcome = RelayOutcome.Confiscated,
                    OutputItems = [],
                    MeterBefore = 2,
                    MeterAfter = 3,
                    GuaranteedUpgrade = false,
                    ProfileCommitStarted = true,
                    PreparedAtUtc = PreparedAt,
                    Status = RelayRecordStatus.Prepared
                }
            ],
            LegacySecuredStakeRoots = [],
            ActiveManifest = null,
            ManifestReceipts = null
        };

        var journal = SptCaseJournal.FromDocument(document);

        var relay = Assert.Single(journal.RelayRecords);
        Assert.Same(journal.PreparedRelay, relay);
        Assert.Equal(stakeRoot, Assert.Single(relay.InputItemIds));
        Assert.Equal(7d, Assert.Single(relay.InputItems).Upd!.StackObjectsCount);
        Assert.True(relay.ProfileCommitStarted);
        Assert.Equal(2, journal.RecoveryMeter);
        Assert.Null(journal.ActiveManifest);
        Assert.Empty(journal.ManifestReceipts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Legacy_schema_rejects_manifest_fields_instead_of_discarding_them(int schemaVersion)
    {
        var activeManifest = new SptCaseJournalDocument
        {
            SchemaVersion = schemaVersion,
            Records = [],
            ActiveManifest = new SptManifestRecordDocument(),
            ManifestReceipts = []
        };
        var terminalReceipt = new SptCaseJournalDocument
        {
            SchemaVersion = schemaVersion,
            Records = [],
            ActiveManifest = null,
            ManifestReceipts = [new SptManifestTerminalReceiptDocument()]
        };
        var claimGrant = new SptCaseJournalDocument
        {
            SchemaVersion = schemaVersion,
            Records = [],
            ManifestClaimGrants = [new SptManifestClaimGrantDocument()]
        };

        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(activeManifest));
        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(terminalReceipt));
        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(claimGrant));
    }

    [Fact]
    public void Schema_three_rejects_genuine_manifest_shape_missing_resource_kind()
    {
        var document = SptCaseJournal.ToDocument(RichRelayJournal());
        document.SchemaVersion = 3;
        document.ManifestReceipts = [];
        document.ManifestClaimGrants = null;
        document.ActiveManifest!.Offers![0].Forest!.Nodes![1].StableState!.ResourceKind = null;

        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(document));
    }

    [Fact]
    public void Schema_three_rejects_non_granted_manifest_receipt()
    {
        var document = SptCaseJournal.ToDocument(RichRelayJournal());
        document.SchemaVersion = 3;
        document.ActiveManifest = null;
        document.ManifestClaimGrants = null;

        Assert.Equal(ManifestPhase.Confiscated, Assert.Single(document.ManifestReceipts!).TerminalPhase);
        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(document));
    }

    [Fact]
    public void Schema_three_rejects_granted_receipt_without_exact_claim_grant()
    {
        var document = SptCaseJournal.ToDocument(SuccessfulRelayReceiptJournal());
        document.SchemaVersion = 3;
        document.ManifestClaimGrants = null;

        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(document));
    }

    [Fact]
    public void Schema_three_rejects_nonempty_later_schema_claim_grant_field()
    {
        var document = new SptCaseJournalDocument
        {
            SchemaVersion = 3,
            Records = [],
            RelayRecords = [],
            LegacySecuredStakeRoots = [],
            ActiveManifest = null,
            ManifestReceipts = [],
            ManifestClaimGrants = SptCaseJournal
            .ToDocument(SuccessfulRelayReceiptJournal())
            .ManifestClaimGrants
        };

        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(document));
    }

    [Fact]
    public void Schema_six_json_roundtrip_preserves_a_persisted_legacy_manifest_without_live_catalog_providers()
    {
        var journal = RichRelayJournal();
        var document = SptCaseJournal.ToDocument(journal);
        var options = JsonOptions();

        var json = JsonSerializer.Serialize(document, options);
        var deserialized = JsonSerializer.Deserialize<SptCaseJournalDocument>(json, options);
        var restored = SptCaseJournal.FromDocument(Assert.IsType<SptCaseJournalDocument>(deserialized));

        Assert.Equal(6, document.SchemaVersion);
        Assert.Equal("contraband-cases-openings-v1", SptCaseJournal.JournalKey);
        var active = Assert.IsType<ManifestRecord>(restored.ActiveManifest);
        Assert.Equal(RarityLadderVersion.LegacyFourTier, active.RarityLadderVersion);
        Assert.Equal(ManifestPhase.RelayPrepared, active.FlowState.Phase);
        Assert.Equal("catalog-snapshot-1", active.CatalogSnapshotId);
        Assert.Equal(ManifestCommitmentEvidence.CurrentVersion, active.Commitment.Version);
        Assert.Equal(ManifestNonce, active.Commitment.NonceHex);
        Assert.True(active.Commitment.VerifyReveal(active.ManifestId, active.CatalogSnapshotId, active.Offers));
        Assert.True(active.Ticket.Committed);
        Assert.Equal(7L, active.Ticket.CommitGeneration);
        Assert.Equal(new string('b', 64), active.Ticket.CommitPredecessorHash);
        Assert.Equal([1, 2, 3], active.Offers.Select(offer => offer.Ordinal));
        Assert.IsType<RaidRole>(active.Offers[0].Identity.UsePath);
        Assert.IsType<CargoCollection>(active.Offers[1].Identity.UsePath);
        Assert.IsType<Craft>(active.Offers[2].Identity.UsePath);
        Assert.All(active.RelayCandidates, candidate => Assert.IsType<Barter>(candidate.Identity.UsePath));
        var child = active.Offers[0].Forest.Nodes.Single(node => node.ParentLogicalPath is not null);
        Assert.Equal(CanonicalRotation.Vertical, child.InternalLocation!.Rotation);
        Assert.Equal(5m, child.StableState!.ResourceValue);
        Assert.Equal(10m, child.StableState.MaximumResourceValue);
        Assert.Equal(RewardResourceKind.Generic, child.StableState.ResourceKind);
        Assert.Equal(active.Offers[0].Fingerprint, active.Offers[0].Identity.Fingerprint);
        Assert.Equal(ManifestRngPurpose.OfferSelection, active.Offers[0].RngEvidence.Purpose);
        var relayPrepared = Assert.IsType<ManifestRelayPreparedPayload>(active.RelayPrepared);
        Assert.Equal(ManifestRelayResult.Upgrade, relayPrepared.Outcome);
        Assert.Equal(ManifestRngPurpose.RelayTargetSelection, relayPrepared.TargetRng!.Purpose);
        Assert.Equal(11L, relayPrepared.CommitGeneration);
        Assert.Equal(new string('c', 64), relayPrepared.CommitPredecessorHash);

        var terminal = Assert.Single(restored.ManifestReceipts);
        Assert.Equal(ManifestPhase.Confiscated, terminal.TerminalPhase);
        Assert.Equal(1, terminal.BrokerFavorAfter);
        var relayReceipt = Assert.Single(terminal.RelayHistory);
        Assert.Equal(ManifestRelayResult.Confiscated, relayReceipt.Outcome);
        Assert.Null(relayReceipt.Output);
        Assert.Null(relayReceipt.TargetRng);
        Assert.Empty(relayReceipt.EligibleTargets);
    }

    [Fact]
    public void Schema_five_json_roundtrip_preserves_claim_items_root_ids_and_commit_plan()
    {
        var journal = ClaimJournal(out var physicalRoot, out var physicalChild);
        var document = SptCaseJournal.ToDocument(journal);
        var options = JsonOptions();

        var json = JsonSerializer.Serialize(document, options);
        var deserialized = JsonSerializer.Deserialize<SptCaseJournalDocument>(json, options);
        var restored = SptCaseJournal.FromDocument(Assert.IsType<SptCaseJournalDocument>(deserialized));

        var restoredClaim = Assert.IsType<ManifestClaimPreparedPayload>(restored.ActiveManifest!.ClaimPrepared);
        Assert.Equal([physicalRoot], restoredClaim.RootIds);
        Assert.Equal([physicalRoot, physicalChild], restoredClaim.ExactItemIds);
        Assert.Equal(2d, restoredClaim.Items[1].Upd!.StackObjectsCount);
        Assert.True(restoredClaim.ProfileCommitStarted);
        Assert.Equal(1, restoredClaim.CommitGeneration);
        Assert.Equal(ManifestClaimCommitWitness.GenesisHash, restoredClaim.CommitPredecessorHash);
    }

    [Fact]
    public void Schema_four_rejects_active_manifest_claims_without_hash_chain_evidence()
    {
        var document = SptCaseJournal.ToDocument(ClaimJournal(out _, out _));
        document.SchemaVersion = 4;
        document.ActiveManifest!.ClaimPrepared!.CommitGeneration = null;
        document.ActiveManifest.ClaimPrepared.CommitPredecessorHash = null;

        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(document));
    }

    [Fact]
    public void Schema_four_restores_terminal_grants_that_predate_hash_chain_evidence()
    {
        var document = SptCaseJournal.ToDocument(SuccessfulRelayReceiptJournal());
        document.SchemaVersion = 4;
        document.ManifestClaimGrants![0].ClaimPayload!.CommitGeneration = null;
        document.ManifestClaimGrants[0].ClaimPayload!.CommitPredecessorHash = null;

        var restored = SptCaseJournal.FromDocument(document);

        var grant = Assert.IsType<ManifestClaimGrantRecord>(
            restored.FindManifestClaimGrant("manifest-successful-receipt"));
        Assert.Null(grant.ClaimPayload.CommitGeneration);
        Assert.Null(grant.ClaimPayload.CommitPredecessorHash);
    }

    [Fact]
    public void Schema_five_requires_complete_hash_chain_evidence_for_active_claims()
    {
        var missingGeneration = SptCaseJournal.ToDocument(ClaimJournal(out _, out _));
        missingGeneration.ActiveManifest!.ClaimPrepared!.CommitGeneration = null;
        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(missingGeneration));

        var missingPredecessor = SptCaseJournal.ToDocument(ClaimJournal(out _, out _));
        missingPredecessor.ActiveManifest!.ClaimPrepared!.CommitPredecessorHash = null;
        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(missingPredecessor));
    }

    [Fact]
    public void Schema_five_requires_commit_chain_evidence_for_active_ticket_and_relay_actions()
    {
        var missingTicketPlan = SptCaseJournal.ToDocument(RichRelayJournal());
        missingTicketPlan.ActiveManifest!.Ticket!.CommitGeneration = null;
        missingTicketPlan.ActiveManifest.Ticket.CommitPredecessorHash = null;
        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(missingTicketPlan));

        var missingRelayPlan = SptCaseJournal.ToDocument(RichRelayJournal());
        missingRelayPlan.ActiveManifest!.RelayPrepared!.CommitGeneration = null;
        missingRelayPlan.ActiveManifest.RelayPrepared.CommitPredecessorHash = null;
        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(missingRelayPlan));
    }

    [Fact]
    public void Schema_five_fails_closed_for_null_or_corrupt_manifest_payloads()
    {
        var nullOffers = SptCaseJournal.ToDocument(RichRelayJournal());
        nullOffers.ActiveManifest!.Offers = null;
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(nullOffers));

        var invalidUsePath = SptCaseJournal.ToDocument(RichRelayJournal());
        invalidUsePath.ActiveManifest!.Offers![0].Identity!.UsePath!.Kind = "unknown";
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(invalidUsePath));

        var invalidPhase = SptCaseJournal.ToDocument(RichRelayJournal());
        invalidPhase.ActiveManifest!.FlowState!.Phase = (ManifestPhase)999;
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(invalidPhase));

        var invalidFingerprint = SptCaseJournal.ToDocument(RichRelayJournal());
        invalidFingerprint.ActiveManifest!.Offers![0].Fingerprint!.Sha256Hex = new string('0', 64);
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(invalidFingerprint));

        var nullReceipts = SptCaseJournal.ToDocument(RichRelayJournal());
        nullReceipts.ManifestReceipts = null;
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(nullReceipts));

        var nullClaimGrants = SptCaseJournal.ToDocument(RichRelayJournal());
        nullClaimGrants.ManifestClaimGrants = null;
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(nullClaimGrants));

        var missingResourceKind = SptCaseJournal.ToDocument(RichRelayJournal());
        missingResourceKind.ActiveManifest!.Offers![0].Forest!.Nodes![1].StableState!.ResourceKind = null;
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(missingResourceKind));
    }

    [Fact]
    public void Schema_five_rejects_oversized_manifest_transcript_collections()
    {
        AssertRichDocumentRejected(document =>
            document.ActiveManifest!.Offers = Enumerable
                .Repeat(document.ActiveManifest.Offers![0], ManifestRecord.OfferCount + 1)
                .ToList());
        AssertRichDocumentRejected(document =>
            document.ActiveManifest!.Decisions = Enumerable
                .Repeat(document.ActiveManifest.Decisions![0], ManifestRecord.OfferCount)
                .ToList());
        AssertRichDocumentRejected(document =>
            document.ActiveManifest!.RelayCandidates = Enumerable
                .Repeat(
                    document.ActiveManifest.RelayCandidates![0],
                    ManifestRecord.MaximumRelayCandidateCount + 1)
                .ToList());

        var oversizedPreparedPool = SptCaseJournal.ToDocument(RichRelayJournal());
        var preparedCandidate = oversizedPreparedPool.ActiveManifest!.RelayPrepared!.NextRelayCandidates![0];
        oversizedPreparedPool.ActiveManifest.RelayPrepared.NextRelayCandidates = Enumerable
            .Repeat(preparedCandidate, ManifestRecord.MaximumRelayCandidateCount + 1)
            .ToList();
        oversizedPreparedPool.ActiveManifest.RelayPrepared.NextRelayCandidates[^1] = null!;
        var oversizedPreparedPoolError = Assert.Throws<InvalidOperationException>(() =>
            SptCaseJournal.FromDocument(oversizedPreparedPool));
        Assert.Contains("prepared next-stage Relay candidates", oversizedPreparedPoolError.Message);

        var oversizedPreparedNodes = SptCaseJournal.ToDocument(RichRelayJournal());
        preparedCandidate = oversizedPreparedNodes.ActiveManifest!.RelayPrepared!.NextRelayCandidates![0];
        preparedCandidate.Forest!.Nodes = Enumerable
            .Repeat(preparedCandidate.Forest.Nodes![0], RewardForest.MaxNodeCount)
            .ToList();
        oversizedPreparedNodes.ActiveManifest.RelayPrepared.NextRelayCandidates = Enumerable
            .Repeat(
                preparedCandidate,
                ManifestRecord.MaximumFrozenRelayNodeCount / RewardForest.MaxNodeCount + 1)
            .ToList();
        var oversizedPreparedNodesError = Assert.Throws<InvalidOperationException>(() =>
            SptCaseJournal.FromDocument(oversizedPreparedNodes));
        Assert.Contains(
            "prepared next-stage Relay candidate reward forest node count",
            oversizedPreparedNodesError.Message);

        AssertRichDocumentRejected(document =>
        {
            var candidate = document.ActiveManifest!.RelayCandidates![0];
            candidate.Forest!.Nodes = Enumerable
                .Repeat(candidate.Forest.Nodes![0], RewardForest.MaxNodeCount)
                .ToList();
            document.ActiveManifest.RelayCandidates = Enumerable
                .Repeat(
                    candidate,
                    ManifestRecord.MaximumFrozenRelayNodeCount / RewardForest.MaxNodeCount + 1)
                .ToList();
        });
        AssertRichDocumentRejected(document =>
            document.ActiveManifest!.RelayHistory = Enumerable
                .Repeat(
                    document.ManifestReceipts![0].RelayHistory![0],
                    ManifestRecord.MaximumRelayReceiptCount + 1)
                .ToList());
        AssertRichDocumentRejected(document =>
            document.ManifestReceipts = Enumerable
                .Repeat(
                    document.ManifestReceipts![0],
                    CaseOpeningJournal.RetainedManifestReceiptCount + 1)
                .ToList());

        var claimGrantDocument = SptCaseJournal.ToDocument(SuccessfulRelayReceiptJournal());
        claimGrantDocument.ManifestClaimGrants = Enumerable
            .Repeat(
                claimGrantDocument.ManifestClaimGrants![0],
                CaseOpeningJournal.RetainedManifestReceiptCount + 1)
            .ToList();
        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(claimGrantDocument));
    }

    [Fact]
    public void Schema_five_rejects_missing_mismatched_or_corrupt_claim_grant_evidence()
    {
        var missing = SptCaseJournal.ToDocument(SuccessfulRelayReceiptJournal());
        missing.ManifestClaimGrants = [];
        Assert.Throws<ArgumentException>(() => SptCaseJournal.FromDocument(missing));

        var orphan = SptCaseJournal.ToDocument(SuccessfulRelayReceiptJournal());
        orphan.ManifestReceipts = [];
        Assert.Throws<ArgumentException>(() => SptCaseJournal.FromDocument(orphan));

        var mismatchedId = SptCaseJournal.ToDocument(SuccessfulRelayReceiptJournal());
        mismatchedId.ManifestClaimGrants![0].ManifestId = "manifest-other";
        Assert.Throws<ArgumentException>(() => SptCaseJournal.FromDocument(mismatchedId));

        var mismatchedTimestamp = SptCaseJournal.ToDocument(SuccessfulRelayReceiptJournal());
        mismatchedTimestamp.ManifestClaimGrants![0].CommittedAtUtc = CommittedAt.AddTicks(1);
        Assert.Throws<ArgumentException>(() => SptCaseJournal.FromDocument(mismatchedTimestamp));

        var corruptFingerprint = SptCaseJournal.ToDocument(SuccessfulRelayReceiptJournal());
        corruptFingerprint.ManifestClaimGrants![0].Entitlement!.Fingerprint!.Sha256Hex = new string('0', 64);
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(corruptFingerprint));

        var extendedPayload = SptCaseJournal.ToDocument(SuccessfulRelayReceiptJournal());
        extendedPayload.ManifestClaimGrants![0].ClaimPayload!.Items![0].Item!
            .ExtensionData!["unsupported"] = "value";
        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(extendedPayload));

        var extendedUpd = SptCaseJournal.ToDocument(SuccessfulRelayReceiptJournal());
        extendedUpd.ManifestClaimGrants![0].ClaimPayload!.Items![0].Item!.Upd!
            .ExtensionData!["unsupported"] = "value";
        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(extendedUpd));

        var extendedLocation = SptCaseJournal.ToDocument(SuccessfulRelayReceiptJournal());
        extendedLocation.ManifestClaimGrants![0].ClaimPayload!.Items![1].Location!
            .ExtensionData!["unsupported"] = "value";
        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(extendedLocation));
    }

    [Fact]
    public void Schema_five_rejects_oversized_nested_forest_and_eligible_target_collections()
    {
        AssertRichDocumentRejected(document =>
            document.ActiveManifest!.Offers![0].Forest!.Nodes = Enumerable
                .Repeat(
                    document.ActiveManifest!.Offers![0].Forest!.Nodes![0],
                    RewardForest.MaxNodeCount + 1)
                .ToList());
        AssertRichDocumentRejected(document =>
            document.ActiveManifest!.Offers![0].Forest!.Nodes = Enumerable
                .Repeat(
                    document.ActiveManifest!.Offers![0].Forest!.Nodes![0],
                    RewardForest.MaxRootCount + 1)
                .ToList());
        AssertRichDocumentRejected(document =>
            document.ManifestReceipts![0].RelayHistory![0].EligibleTargets = Enumerable
                .Repeat(
                    document.ManifestReceipts![0].RelayHistory![0].Input!,
                    ManifestRecord.MaximumRelayCandidateCount + 1)
                .ToList());
    }

    [Fact]
    public void Schema_five_rejects_oversized_claim_item_and_root_collections()
    {
        AssertClaimDocumentRejected(document =>
            document.ActiveManifest!.ClaimPrepared!.Items = Enumerable
                .Repeat(
                    document.ActiveManifest.ClaimPrepared.Items![0],
                    RewardForest.MaxNodeCount + 1)
                .ToList());
        AssertClaimDocumentRejected(document =>
            document.ActiveManifest!.ClaimPrepared!.RootIds = Enumerable
                .Repeat(
                    document.ActiveManifest.ClaimPrepared.RootIds![0],
                    RewardForest.MaxRootCount + 1)
                .ToList());
    }

    [Fact]
    public void Schema_five_rejects_missing_corrupt_or_tampered_commitment_evidence()
    {
        var missing = SptCaseJournal.ToDocument(RichRelayJournal());
        missing.ActiveManifest!.Commitment = null;
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(missing));

        var corruptVersion = SptCaseJournal.ToDocument(RichRelayJournal());
        corruptVersion.ActiveManifest!.Commitment!.Version = ManifestCommitmentEvidence.CurrentVersion + 1;
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(corruptVersion));

        var tamperedDigest = SptCaseJournal.ToDocument(RichRelayJournal());
        tamperedDigest.ActiveManifest!.Commitment!.CommitmentSha256Hex = new string('0', 64);
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(tamperedDigest));

        var tamperedNonce = SptCaseJournal.ToDocument(RichRelayJournal());
        tamperedNonce.ActiveManifest!.Commitment!.NonceHex = new string('b', 64);
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(tamperedNonce));
    }

    [Fact]
    public void Schema_five_rejects_null_relay_receipt_eligible_target_collection()
    {
        var document = SptCaseJournal.ToDocument(RichRelayJournal());
        document.ManifestReceipts![0].RelayHistory![0].EligibleTargets = null;

        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(document));
    }

    [Fact]
    public void Successful_relay_receipt_eligible_targets_survive_true_json_roundtrip()
    {
        var document = SptCaseJournal.ToDocument(SuccessfulRelayReceiptJournal());
        var options = JsonOptions();

        var json = JsonSerializer.Serialize(document, options);
        var deserialized = JsonSerializer.Deserialize<SptCaseJournalDocument>(json, options);
        var restored = SptCaseJournal.FromDocument(Assert.IsType<SptCaseJournalDocument>(deserialized));

        var receipt = Assert.Single(Assert.Single(restored.ManifestReceipts).RelayHistory);
        Assert.Equal(ManifestRelayResult.Upgrade, receipt.Outcome);
        var eligible = Assert.Single(receipt.EligibleTargets);
        Assert.Equal(receipt.Output!.Fingerprint, eligible.Fingerprint);
        Assert.Equal(receipt.Output.Identity.LotId, eligible.Identity.LotId);
        Assert.Equal(ManifestRngPurpose.RelayTargetSelection, receipt.TargetRng!.Purpose);

        var grant = Assert.IsType<ManifestClaimGrantRecord>(
            restored.FindManifestClaimGrant("manifest-successful-receipt"));
        Assert.Equal([Id(900)], grant.ClaimPayload.RootIds);
        Assert.Equal([Id(900), Id(901)], grant.ClaimPayload.ExactItemIds);
        grant.ClaimPayload.Items[1].Upd!.StackObjectsCount = 99;
        Assert.Equal(
            2d,
            restored.FindManifestClaimGrant("manifest-successful-receipt")!
                .ClaimPayload.Items[1].Upd!.StackObjectsCount);
    }

    [Fact]
    public void Schema_five_uses_the_legacy_four_tier_transition_when_ladder_metadata_is_absent()
    {
        var document = SptCaseJournal.ToDocument(SuccessfulRelayReceiptJournal());
        document.SchemaVersion = 5;
        document.ManifestReceipts![0].RarityLadderVersion = null;
        document.ManifestReceipts[0].RelayHistory![0].RarityLadderVersion = null;

        var restored = SptCaseJournal.FromDocument(document);

        var receipt = Assert.Single(Assert.Single(restored.ManifestReceipts).RelayHistory);
        Assert.Equal(RarityLadderVersion.LegacyFourTier, receipt.RarityLadderVersion);
        Assert.Equal(RewardRarity.Contractor, receipt.Output!.Rarity);
    }

    [Fact]
    public void Schema_six_loads_the_persisted_five_tier_common_to_uncommon_transition()
    {
        var document = SptCaseJournal.ToDocument(
            SuccessfulRelayReceiptJournal(RarityLadderVersion.FiveTier));

        var restored = SptCaseJournal.FromDocument(document);

        var terminal = Assert.Single(restored.ManifestReceipts);
        var receipt = Assert.Single(terminal.RelayHistory);
        Assert.Equal(RarityLadderVersion.FiveTier, terminal.RarityLadderVersion);
        Assert.Equal(RarityLadderVersion.FiveTier, receipt.RarityLadderVersion);
        Assert.Equal(RewardRarity.Uncommon, receipt.Output!.Rarity);
    }

    [Fact]
    public void Schema_six_rejects_missing_or_mismatched_receipt_ladder_metadata()
    {
        var missing = SptCaseJournal.ToDocument(
            SuccessfulRelayReceiptJournal(RarityLadderVersion.FiveTier));
        missing.ManifestReceipts![0].RarityLadderVersion = null;
        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(missing));

        var mismatched = SptCaseJournal.ToDocument(
            SuccessfulRelayReceiptJournal(RarityLadderVersion.FiveTier));
        mismatched.ManifestReceipts![0].RelayHistory![0].RarityLadderVersion =
            RarityLadderVersion.LegacyFourTier;
        Assert.Throws<ArgumentException>(() => SptCaseJournal.FromDocument(mismatched));
    }

    [Fact]
    public void Claim_mapping_rejects_item_upd_and_location_extension_data()
    {
        var itemExtension = SptCaseJournal.ToDocument(ClaimJournal(out _, out _));
        itemExtension.ActiveManifest!.ClaimPrepared!.Items![0].Item!.ExtensionData!["probe"] = "blocked";
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(itemExtension));

        var updExtension = SptCaseJournal.ToDocument(ClaimJournal(out _, out _));
        updExtension.ActiveManifest!.ClaimPrepared!.Items![0].Item!.Upd!.ExtensionData!["probe"] = "blocked";
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(updExtension));

        var locationExtension = SptCaseJournal.ToDocument(ClaimJournal(out _, out _));
        locationExtension.ActiveManifest!.ClaimPrepared!.Items![1].Location!.ExtensionData!["probe"] = "blocked";
        Assert.ThrowsAny<Exception>(() => SptCaseJournal.FromDocument(locationExtension));
    }

    [Fact]
    public void Forward_schema_is_rejected_before_payload_migration()
    {
        var document = new SptCaseJournalDocument
        {
            SchemaVersion = SptCaseJournalDocument.CurrentSchemaVersion + 1,
            Records = null
        };

        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(document));
    }

    private static void AssertRichDocumentRejected(Action<SptCaseJournalDocument> corrupt)
    {
        var document = SptCaseJournal.ToDocument(RichRelayJournal());
        corrupt(document);

        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(document));
    }

    private static void AssertClaimDocumentRejected(Action<SptCaseJournalDocument> corrupt)
    {
        var document = SptCaseJournal.ToDocument(ClaimJournal(out _, out _));
        corrupt(document);

        Assert.Throws<InvalidOperationException>(() => SptCaseJournal.FromDocument(document));
    }

    private static CaseOpeningJournal ClaimJournal(out MongoId physicalRoot, out MongoId physicalChild)
    {
        var offers = Offers();
        var entitlementState = ManifestStateMachine.DecideOffer(
            ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket()),
            ManifestOfferDecision.Lock);
        var flowState = ManifestStateMachine.PrepareClaim(entitlementState);
        physicalRoot = Id(600);
        physicalChild = Id(601);
        var claimItems = new[]
        {
            Physical(physicalRoot, offers[0].Forest.Nodes[0].TemplateId, stackCount: 1),
            Physical(
                physicalChild,
                offers[0].Forest.Nodes[1].TemplateId,
                physicalRoot.ToString(),
                "slot-a",
                stackCount: 2)
        };
        claimItems[0].Upd!.Repairable = new UpdRepairable { Durability = 80, MaxDurability = 100 };
        claimItems[1].Location = new ItemLocation { X = 1, Y = 2, R = ItemRotation.Vertical };
        claimItems[1].Upd!.Resource = new UpdResource { Value = 5 };
        var claim = new ManifestClaimPreparedPayload(
            claimItems,
            [physicalRoot],
            profileCommitStarted: true,
            PreparedAt,
            commitGeneration: 1,
            commitPredecessorHash: ManifestClaimCommitWitness.GenesisHash);
        var manifest = new ManifestRecord(
            "manifest-claim",
            "catalog-snapshot-claim",
            ManifestCommitmentEvidence.Create(
                "manifest-claim",
                "catalog-snapshot-claim",
                ManifestNonce,
                offers),
            flowState,
            Ticket(
                commitGeneration: 1,
                commitPredecessorHash: ManifestInputCommitWitness.GenesisHash),
            offers,
            [new ManifestDecisionRecord(1, ManifestOfferDecision.Lock, CommittedAt)],
            Entitlement(offers[0]),
            relayCandidates: [],
            relayHistory: [],
            brokerFavor: 0,
            claimPrepared: claim);
        return new CaseOpeningJournal(activeManifest: manifest);
    }

    private static CaseOpeningJournal SuccessfulRelayReceiptJournal(
        RarityLadderVersion rarityLadderVersion = RarityLadderVersion.LegacyFourTier)
    {
        var offers = Offers();
        var output = Candidate(
            ManifestRelayResult.Upgrade,
            rarityLadderVersion == RarityLadderVersion.LegacyFourTier
                ? RewardRarity.Contractor
                : RewardRarity.Uncommon,
            "receipt-upgrade",
            120);
        var decision = new ManifestDecisionRecord(1, ManifestOfferDecision.Lock, CommittedAt);
        var receipt = new ManifestRelayReceipt(
            relayStage: 1,
            Receipt(offers[0]),
            ManifestRelayResult.Upgrade,
            Receipt(output),
            RelayRules.GetOdds(1),
            RelayOutcomeRng(0),
            RelayTargetRng(0),
            eligibleTargets: [Receipt(output)],
            brokerFavorBefore: 0,
            brokerFavorAfter: 0,
            CommittedAt,
            rarityLadderVersion: rarityLadderVersion);
        var terminal = new ManifestTerminalReceipt(
            "manifest-successful-receipt",
            ManifestPhase.Granted,
            offers.Select(Receipt),
            Receipt(output),
            [decision],
            [receipt],
            brokerFavorBefore: 0,
            brokerFavorAfter: 0,
            CommittedAt,
            rarityLadderVersion: rarityLadderVersion);
        var rootId = Id(900);
        var childId = Id(901);
        var claimItems = new[]
        {
            Physical(rootId, output.Forest.Nodes[0].TemplateId, stackCount: 1),
            Physical(
                childId,
                output.Forest.Nodes[1].TemplateId,
                rootId.ToString(),
                "slot-a",
                stackCount: 2)
        };
        claimItems[0].Upd!.Repairable = new UpdRepairable { Durability = 80, MaxDurability = 100 };
        claimItems[1].Location = new ItemLocation { X = 1, Y = 2, R = ItemRotation.Vertical };
        claimItems[1].Upd!.Resource = new UpdResource { Value = 5 };
        var claim = new ManifestClaimPreparedPayload(
            claimItems,
            [rootId],
            profileCommitStarted: true,
            PreparedAt,
            commitGeneration: 1,
            commitPredecessorHash: ManifestClaimCommitWitness.GenesisHash);
        var grant = new ManifestClaimGrantRecord(
            "manifest-successful-receipt",
            Entitlement(output),
            claim,
            CommittedAt);
        return new CaseOpeningJournal(
            manifestReceipts: [terminal],
            manifestClaimGrants: [grant]);
    }

    private static CaseOpeningJournal RichRelayJournal()
    {
        var offers = Offers();
        var entitlementState = ManifestStateMachine.DecideOffer(
            ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket()),
            ManifestOfferDecision.Lock);
        var flowState = ManifestStateMachine.PrepareRelay(entitlementState, relayEligible: true);
        var upgrade = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Contractor,
            "upgrade",
            100);
        var sidegrade = Candidate(
            ManifestRelayResult.Sidegrade,
            RewardRarity.ScavGrade,
            "sidegrade",
            110);
        var nextUpgrade = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Restricted,
            "next-upgrade",
            120);
        var nextSidegrade = Candidate(
            ManifestRelayResult.Sidegrade,
            RewardRarity.Contractor,
            "next-sidegrade",
            130);
        var prepared = new ManifestRelayPreparedPayload(
            Id(500),
            ManifestRelayResult.Upgrade,
            Entitlement(upgrade),
            RelayRules.GetOdds(1),
            RelayOutcomeRng(0),
            RelayTargetRng(1),
            brokerFavorBefore: 0,
            brokerFavorAfter: 0,
            profileCommitStarted: false,
            PreparedAt,
            [nextUpgrade, nextSidegrade],
            commitGeneration: 11,
            commitPredecessorHash: new string('c', 64));
        var decision = new ManifestDecisionRecord(1, ManifestOfferDecision.Lock, CommittedAt);
        var active = new ManifestRecord(
            "manifest-active",
            "catalog-snapshot-1",
            ManifestCommitmentEvidence.Create(
                "manifest-active",
                "catalog-snapshot-1",
                ManifestNonce,
                offers),
            flowState,
            Ticket(
                commitGeneration: 7,
                commitPredecessorHash: new string('b', 64)),
            offers,
            [decision],
            Entitlement(offers[0]),
            [sidegrade, upgrade],
            relayHistory: [],
            brokerFavor: 0,
            relayPrepared: prepared,
            rarityLadderVersion: RarityLadderVersion.LegacyFourTier);

        var confiscated = new ManifestRelayReceipt(
            1,
            Receipt(offers[0]),
            ManifestRelayResult.Confiscated,
            output: null,
            RelayRules.GetOdds(1),
            RelayOutcomeRng(CanonicalRngEvidence.UnitDenominator - 1),
            targetRng: null,
            eligibleTargets: [],
            brokerFavorBefore: 0,
            brokerFavorAfter: 1,
            CommittedAt,
            rarityLadderVersion: RarityLadderVersion.LegacyFourTier);
        var terminal = new ManifestTerminalReceipt(
            "manifest-receipt",
            ManifestPhase.Confiscated,
            offers.Select(Receipt),
            Receipt(offers[0]),
            [decision],
            [confiscated],
            brokerFavorBefore: 0,
            brokerFavorAfter: 1,
            CommittedAt,
            rarityLadderVersion: RarityLadderVersion.LegacyFourTier);
        return new CaseOpeningJournal(activeManifest: active, manifestReceipts: [terminal]);
    }

    private static ManifestOfferSnapshot[] Offers()
    {
        var lots = new[]
        {
            Lot("provider-a", "lot-a", "family-a", "track-a", RewardRarity.ScavGrade, new RaidRole("raid-a"), 200),
            Lot(
                "provider-b",
                "lot-b",
                "family-b",
                "track-b",
                RewardRarity.Contractor,
                new CargoCollection(Id(250).ToString(), "collection-b"),
                210),
            Lot("provider-c", "lot-c", "family-c", "track-c", RewardRarity.Restricted, new Craft("craft-c"), 220)
        };
        return lots.Select((lot, index) => new ManifestOfferSnapshot(
            index + 1,
            lot.Rarity,
            lot.Identity,
            lot.Forest,
            lot.Fingerprint,
            OfferRng(index + 1))).ToArray();
    }

    private static ManifestRelayCandidateSnapshot Candidate(
        ManifestRelayResult result,
        RewardRarity rarity,
        string lotId,
        int seed)
    {
        var lot = Lot(
            "provider-" + lotId,
            lotId,
            "family-" + lotId,
            "track-a",
            rarity,
            new Barter("trader-" + lotId, "assort-" + lotId),
            seed);
        return new ManifestRelayCandidateSnapshot(result, rarity, lot.Identity, lot.Forest, lot.Fingerprint);
    }

    private static LotData Lot(
        string providerId,
        string lotId,
        string familyId,
        string trackId,
        RewardRarity rarity,
        UsePath usePath,
        int seed)
    {
        var nodes = new[]
        {
            new RewardForestNode(
                "root",
                "root",
                Id(seed).ToString(),
                parentLogicalPath: null,
                slotId: null,
                internalLocation: null,
                stackCount: 1,
                new RewardStableState(80m, 100m)),
            new RewardForestNode(
                "root",
                "root/child",
                Id(seed + 1).ToString(),
                "root",
                "slot-a",
                new CanonicalInternalLocation(1, 2, CanonicalRotation.Vertical),
                stackCount: 2,
                new RewardStableState(
                    resourceValue: 5m,
                    maximumResourceValue: 10m,
                    resourceKind: RewardResourceKind.Generic))
        };
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
            1.25d,
            usePath,
            fingerprint);
        return new LotData(rarity, identity, forest, fingerprint);
    }

    private static ManifestTicketPayload Ticket(
        long? commitGeneration = null,
        string? commitPredecessorHash = null) => new(
        Id(510),
        Id(511),
        PreparedAt,
        profileCommitStarted: true,
        committed: true,
        CommittedAt,
        commitGeneration,
        commitPredecessorHash);

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

    private static ManifestLotReceiptSnapshot Receipt(ManifestOfferSnapshot offer) => new(
        offer.Rarity,
        offer.Identity,
        offer.Fingerprint);

    private static ManifestLotReceiptSnapshot Receipt(ManifestRelayCandidateSnapshot candidate) => new(
        candidate.Rarity,
        candidate.Identity,
        candidate.Fingerprint);

    private static CanonicalRngEvidence OfferRng(int ordinal) => new(
        ManifestRngPurpose.OfferSelection,
        ordinal,
        ordinal);

    private static CanonicalRngEvidence RelayOutcomeRng(long numerator) => new(
        ManifestRngPurpose.RelayOutcome,
        drawOrdinal: 1,
        numerator);

    private static CanonicalRngEvidence RelayTargetRng(long numerator) => new(
        ManifestRngPurpose.RelayTargetSelection,
        drawOrdinal: 1,
        numerator);

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new StringToMongoIdConverter());
        return options;
    }

    private static Item Physical(
        MongoId id,
        int templateSeed,
        string parentId = "stash",
        string slotId = "hideout",
        double stackCount = 1) =>
        Physical(id, Id(templateSeed).ToString(), parentId, slotId, stackCount);

    private static Item Physical(
        MongoId id,
        string templateId,
        string parentId = "stash",
        string slotId = "hideout",
        double stackCount = 1) => new()
        {
            Id = id,
            Template = (MongoId)templateId,
            ParentId = parentId,
            SlotId = slotId,
            Upd = new Upd { StackObjectsCount = stackCount }
        };

    private static MongoId Id(int value) => (MongoId)value.ToString("x24");

    private sealed record LotData(
        RewardRarity Rarity,
        CargoLotIdentitySnapshot Identity,
        RewardForest Forest,
        RewardForestFingerprintV2 Fingerprint);
}
