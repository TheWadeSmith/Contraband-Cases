using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Utils;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class ManifestJournalIntegrationTests
{
    private static readonly DateTimeOffset PreparedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
    private static readonly DateTimeOffset CommittedAt = DateTimeOffset.UnixEpoch.AddMinutes(2);

    [Fact]
    public void Long_valid_receipt_titles_cannot_overflow_the_read_only_dossier()
    {
        var source = Offers();
        var longTitle = new string('x', 4096);
        var original = source[0];
        var identity = original.Identity;
        var longIdentity = new CargoLotIdentitySnapshot(identity.ProviderId, identity.PackVersion, identity.LotId,
            longTitle, identity.Purpose, identity.FamilyId, identity.TrackId, identity.AnchorTemplateId,
            identity.Weight, identity.UsePath, original.Fingerprint);
        source[0] = new ManifestOfferSnapshot(1, original.Rarity, longIdentity, original.Forest,
            original.Fingerprint, original.RngEvidence);
        var receipts = Enumerable.Range(0, 50).Select(i => CreateForfeitReceipt($"long-{i}", CommittedAt.AddSeconds(i),
            0, source, LockFirstDecision())).ToArray();
        var journal = new CaseOpeningJournal(manifestReceipts: receipts);
        var library = ManifestLibraryProjection.Create(journal, null, new Dictionary<string, string>());
        Assert.InRange(library.Dossier.Length, 1, 65536);
        Assert.Contains(new string('x', 239) + "…", library.Dossier);
        Assert.All(journal.ManifestReceipts, r => Assert.Equal(longTitle, r.Entitlement!.Identity.DisplayName));
    }

    [Fact]
    public void Read_only_library_does_not_disclose_active_offers_or_rng()
    {
        var active = CreateManifest(ManifestPhase.Offer1);
        var journal = new CaseOpeningJournal(activeManifest: active);
        var library = ManifestLibraryProjection.Create(journal, null, new Dictionary<string, string>());
        var json = System.Text.Json.JsonSerializer.Serialize(library);
        Assert.Contains("unfinished manifest", library.Dossier);
        Assert.DoesNotContain(active.ManifestId, json);
        foreach (var offer in active.Offers)
        {
            Assert.DoesNotContain(offer.Identity.DisplayName, json);
            Assert.DoesNotContain(offer.Fingerprint.Sha256Hex, json);
        }
        Assert.Same(active, journal.ActiveManifest);
    }

    [Theory]
    [InlineData(ManifestPhase.TicketPrepared)]
    [InlineData(ManifestPhase.ClaimPrepared)]
    [InlineData(ManifestPhase.RelayPrepared)]
    [InlineData(ManifestPhase.RewardOwed)]
    public void Prepared_manifest_actions_conflict_with_legacy_prepared_actions_in_both_directions(
        ManifestPhase phase)
    {
        var manifest = CreateManifest(phase);
        var preparedOpening = PreparedOpening();

        Assert.Throws<ArgumentException>(() =>
            new CaseOpeningJournal([preparedOpening], activeManifest: manifest));

        var activeJournal = new CaseOpeningJournal(activeManifest: manifest);
        Assert.Throws<InvalidOperationException>(() => activeJournal.Add(PreparedOpening()));
        Assert.Throws<InvalidOperationException>(() => activeJournal.AddRelay(PreparedRelay()));
    }

    [Fact]
    public void Starting_a_fresh_manifest_conflicts_with_an_existing_legacy_prepared_action()
    {
        var journal = new CaseOpeningJournal([PreparedOpening()]);

        Assert.Throws<InvalidOperationException>(() =>
            journal.SetActiveManifest(CreateManifest(ManifestPhase.TicketPrepared)));
    }

    [Fact]
    public void Nonprepared_active_manifest_can_coexist_with_one_legacy_prepared_action()
    {
        var active = CreateManifest(ManifestPhase.Offer1);
        var journal = new CaseOpeningJournal([PreparedOpening()], activeManifest: active);
        var relayJournal = new CaseOpeningJournal(
            relayRecords: [PreparedRelay()],
            activeManifest: active);

        Assert.Same(active, journal.ActiveManifest);
        Assert.NotNull(journal.PreparedOpening);
        Assert.Same(active, relayJournal.ActiveManifest);
        Assert.NotNull(relayJournal.PreparedRelay);
    }

    [Theory]
    [InlineData(ManifestPhase.ClaimPrepared, true, false)]
    [InlineData(ManifestPhase.RewardOwed, true, false)]
    [InlineData(ManifestPhase.RelayPrepared, false, true)]
    public void Prepared_recovery_remains_available_when_the_catalog_is_missing(
        ManifestPhase phase,
        bool canClaim,
        bool canRelay)
    {
        var snapshot = ManifestSnapshotProjection.FromActive(
            CreateManifest(phase),
            catalog: null,
            locale: null);

        Assert.False(snapshot.MissingContentBlocked);
        Assert.Equal(canClaim, snapshot.AvailableActions.CanClaim);
        Assert.Equal(canRelay, snapshot.AvailableActions.CanRelay);
        Assert.False(snapshot.AvailableActions.CanForfeit);
        Assert.NotNull(snapshot.CurrentLot);
        Assert.Null(snapshot.CurrentLot.LiquidationValue);
        Assert.Null(snapshot.CurrentLot.UseValue);
        Assert.Null(snapshot.CurrentLot.FootprintCells);

        var responseJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            err = 0,
            errmsg = (string?)null,
            data = new ManifestSnapshotEnvelope { Snapshot = snapshot }
        });
        var parsed = global::ContrabandCases.Client.Opening.ManifestSnapshotEnvelope.Parse(
            responseJson,
            snapshot.ManifestId);
        Assert.Equal(phase, parsed.Phase);
        Assert.Equal(canClaim, parsed.AvailableActions.CanClaim);
        Assert.Equal(canRelay, parsed.AvailableActions.CanRelay);
    }

    [Fact]
    public void Missing_catalog_blocks_an_unprepared_entitlement_with_forfeit_only()
    {
        var snapshot = ManifestSnapshotProjection.FromActive(
            CreateManifest(ManifestPhase.Entitlement),
            catalog: null,
            locale: null);

        Assert.True(snapshot.MissingContentBlocked);
        Assert.False(snapshot.AvailableActions.CanClaim);
        Assert.False(snapshot.AvailableActions.CanRelay);
        Assert.True(snapshot.AvailableActions.CanForfeit);
    }

    [Fact]
    public void Current_state_is_snapshot_only_when_a_manifest_is_active()
    {
        var state = ManifestSnapshotRouter.CreateCurrentState(
            new CaseOpeningJournal(activeManifest: CreateManifest(ManifestPhase.Offer1)),
            catalog: null,
            locale: null);

        Assert.NotNull(state.Snapshot);
        Assert.Null(state.OpeningOdds);
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var properties = document.RootElement.EnumerateObject().ToArray();
        Assert.Equal(["snapshot", "openingOdds"], properties.Select(property => property.Name));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, properties[0].Value.ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, properties[1].Value.ValueKind);
    }

    [Fact]
    public void Current_offer_response_preserves_null_locked_ordinal_for_the_strict_client()
    {
        var state = ManifestSnapshotRouter.CreateCurrentState(
            new CaseOpeningJournal(activeManifest: CreateManifest(ManifestPhase.Offer1)),
            catalog: null,
            locale: null);
        var jsonUtil = new JsonUtil([]);
        var responseJson = new HttpResponseUtil(jsonUtil, null!).GetBody(state);

        using var document = System.Text.Json.JsonDocument.Parse(responseJson);
        var snapshot = document.RootElement.GetProperty("data").GetProperty("snapshot");
        Assert.True(snapshot.TryGetProperty("lockedOrdinal", out var lockedOrdinal));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, lockedOrdinal.ValueKind);

        var parsed = global::ContrabandCases.Client.Opening.ManifestSnapshotEnvelope.ParseCurrent(
            responseJson);
        Assert.Equal(ManifestPhase.Offer1, parsed.Snapshot!.Phase);
    }

    [Fact]
    public void Snapshot_projection_hides_every_unrevealed_family_identity()
    {
        var snapshot = ManifestSnapshotProjection.FromActive(
            CreateManifest(ManifestPhase.Offer1),
            catalog: null,
            locale: null);

        Assert.Collection(
            snapshot.FamilySeals,
            seal =>
            {
                Assert.True(seal.Revealed);
                Assert.Equal("family-a", seal.FamilyId);
                Assert.Equal("Family A", seal.FamilyLabel);
            },
            seal =>
            {
                Assert.False(seal.Revealed);
                Assert.Equal("sealed-2", seal.FamilyId);
                Assert.Equal("Sealed Family 2", seal.FamilyLabel);
            },
            seal =>
            {
                Assert.False(seal.Revealed);
                Assert.Equal("sealed-3", seal.FamilyId);
                Assert.Equal("Sealed Family 3", seal.FamilyLabel);
            });
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("family-b", json, StringComparison.Ordinal);
        Assert.DoesNotContain("family-c", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Journal_allows_only_one_active_manifest_and_replacement_requires_the_same_identity()
    {
        var original = CreateManifest(ManifestPhase.TicketPrepared, "manifest-a");
        var commitStarted = CreateTicketPreparedManifest(
            "manifest-a",
            Offers(),
            profileCommitStarted: true);
        var replacement = CreateManifest(ManifestPhase.Offer1, "manifest-a");
        var journal = new CaseOpeningJournal();

        journal.SetActiveManifest(original);

        Assert.Throws<InvalidOperationException>(() =>
            journal.SetActiveManifest(CreateManifest(ManifestPhase.TicketPrepared, "manifest-b")));
        Assert.Throws<InvalidOperationException>(() =>
            journal.ReplaceActiveManifest(CreateManifest(ManifestPhase.Offer1, "manifest-b")));

        journal.ReplaceActiveManifest(commitStarted);
        journal.ReplaceActiveManifest(replacement);

        Assert.Same(replacement, journal.ActiveManifest);
    }

    [Fact]
    public void Ticket_must_persist_profile_commit_started_before_advancing()
    {
        const string manifestId = "manifest-ticket-marker";
        var offers = Offers();
        var prepared = CreateTicketPreparedManifest(manifestId, offers);
        var commitStarted = CreateTicketPreparedManifest(
            manifestId,
            offers,
            profileCommitStarted: true);
        var activated = CreateManifest(ManifestPhase.Offer1, manifestId);
        var journal = new CaseOpeningJournal(activeManifest: prepared);

        Assert.Throws<InvalidOperationException>(() => journal.ReplaceActiveManifest(activated));

        journal.ReplaceActiveManifest(commitStarted);
        journal.ReplaceActiveManifest(activated);

        Assert.Same(activated, journal.ActiveManifest);
    }

    [Fact]
    public void Replacement_rejects_ticket_commit_chain_rewrites()
    {
        const string manifestId = "manifest-ticket-chain";
        var offers = Offers();
        var original = CreateTicketPreparedManifest(
            manifestId,
            offers,
            commitGeneration: 1,
            commitPredecessorHash: ManifestInputCommitWitness.GenesisHash);
        var rewrittenGeneration = CreateTicketPreparedManifest(
            manifestId,
            offers,
            commitGeneration: 2,
            commitPredecessorHash: ManifestInputCommitWitness.GenesisHash);
        var rewrittenPredecessor = CreateTicketPreparedManifest(
            manifestId,
            offers,
            commitGeneration: 1,
            commitPredecessorHash: new string('1', 64));
        var journal = new CaseOpeningJournal(activeManifest: original);

        Assert.Throws<InvalidOperationException>(() =>
            journal.ReplaceActiveManifest(rewrittenGeneration));
        Assert.Throws<InvalidOperationException>(() =>
            journal.ReplaceActiveManifest(rewrittenPredecessor));
        Assert.Same(original, journal.ActiveManifest);
    }

    [Fact]
    public void Claim_must_persist_profile_commit_started_before_advancing()
    {
        const string manifestId = "manifest-claim-forward-marker";
        var prepared = CreateClaimPreparedManifest(manifestId, Id(600), profileCommitStarted: false);
        var commitStarted = CreateClaimPreparedManifest(manifestId, Id(600), profileCommitStarted: true);
        var rewardOwed = CreateManifest(ManifestPhase.RewardOwed, manifestId);
        var journal = new CaseOpeningJournal(activeManifest: prepared);

        Assert.Throws<InvalidOperationException>(() => journal.ReplaceActiveManifest(rewardOwed));

        journal.ReplaceActiveManifest(commitStarted);
        journal.ReplaceActiveManifest(rewardOwed);

        Assert.Same(rewardOwed, journal.ActiveManifest);
    }

    [Fact]
    public void Relay_must_persist_profile_commit_started_before_advancing()
    {
        const string manifestId = "manifest-relay-forward-marker";
        var (prepared, confiscated) = CreateConfiscationTransition(
            manifestId,
            profileCommitStarted: false);
        var (commitStarted, _) = CreateConfiscationTransition(
            manifestId,
            profileCommitStarted: true);
        var journal = new CaseOpeningJournal(activeManifest: prepared);

        Assert.Throws<InvalidOperationException>(() => journal.ReplaceActiveManifest(confiscated));

        journal.ReplaceActiveManifest(commitStarted);
        journal.ReplaceActiveManifest(confiscated);

        Assert.Same(confiscated, journal.ActiveManifest);
    }

    [Fact]
    public void Set_accepts_only_a_fresh_ticket_prepared_manifest()
    {
        var journal = new CaseOpeningJournal();

        Assert.Throws<InvalidOperationException>(() =>
            journal.SetActiveManifest(CreateManifest(ManifestPhase.Offer1)));
        Assert.Null(journal.ActiveManifest);
    }

    [Fact]
    public void Finishing_a_terminal_manifest_moves_its_exact_embedded_receipt_and_clears_active_state()
    {
        var terminal = CreateForfeitedManifest("manifest-terminal", CommittedAt, brokerFavor: 2);
        var embeddedReceipt = terminal.TerminalReceipt!;
        var journal = new CaseOpeningJournal(recoveryMeter: 2, activeManifest: terminal);

        journal.FinishActiveManifest();

        Assert.Null(journal.ActiveManifest);
        Assert.Same(embeddedReceipt, Assert.Single(journal.ManifestReceipts));
        Assert.Equal(2, journal.BrokerFavor);
    }

    [Fact]
    public void Granted_manifest_finishes_atomically_with_exact_replayable_claim_evidence()
    {
        var (prepared, terminal, grant) = CreateGrantedManifest(
            "manifest-granted",
            CommittedAt,
            preparedRootId: Id(900));
        var journal = new CaseOpeningJournal(activeManifest: prepared);
        var swappedPhysicalGrant = new ManifestClaimGrantRecord(
            "manifest-granted",
            prepared.Entitlement!,
            Claim(prepared.Offers[0], profileCommitStarted: true, Id(901)),
            CommittedAt);

        Assert.Throws<InvalidOperationException>(() => journal.FinishActiveManifest());
        Assert.Throws<InvalidOperationException>(() => journal.ReplaceActiveManifest(terminal));
        Assert.Throws<InvalidOperationException>(() =>
            journal.FinishGrantedActiveManifest(terminal, swappedPhysicalGrant));
        Assert.Same(prepared, journal.ActiveManifest);
        Assert.Empty(journal.ManifestReceipts);
        Assert.Null(journal.FindManifestClaimGrant("manifest-granted"));

        journal.FinishGrantedActiveManifest(terminal, grant);

        Assert.Null(journal.ActiveManifest);
        Assert.Same(terminal.TerminalReceipt, Assert.Single(journal.ManifestReceipts));
        var replay = Assert.IsType<ManifestClaimGrantRecord>(
            journal.FindManifestClaimGrant("manifest-granted"));
        Assert.Equal([Id(900)], replay.ClaimPayload.RootIds);
        Assert.Equal([Id(900)], replay.ClaimPayload.ExactItemIds);

        replay.ClaimPayload.Items[0].Upd!.StackObjectsCount = 99;
        Assert.Equal(
            1d,
            journal.FindManifestClaimGrant("manifest-granted")!.ClaimPayload.Items[0].Upd!.StackObjectsCount);
    }

    [Fact]
    public void Claim_grant_fails_closed_for_uncommitted_mismatched_or_extended_payloads()
    {
        var offers = Offers();
        var entitlement = Entitlement(offers[0]);

        Assert.Throws<ArgumentException>(() => new ManifestClaimGrantRecord(
            "manifest-uncommitted",
            entitlement,
            Claim(offers[0], profileCommitStarted: false),
            CommittedAt));
        Assert.Throws<ArgumentException>(() => new ManifestClaimGrantRecord(
            "manifest-mismatched",
            entitlement,
            Claim(offers[1], profileCommitStarted: true),
            CommittedAt));

        var extendedItems = Claim(offers[0], profileCommitStarted: true).Items.ToArray();
        extendedItems[0].ExtensionData!["unsupported"] = "value";
        var extendedPayload = new ManifestClaimPreparedPayload(
            extendedItems,
            [extendedItems[0].Id],
            profileCommitStarted: true,
            PreparedAt,
            commitGeneration: 1,
            commitPredecessorHash: ManifestClaimCommitWitness.GenesisHash);
        Assert.Throws<ArgumentException>(() => new ManifestClaimGrantRecord(
            "manifest-extended",
            entitlement,
            extendedPayload,
            CommittedAt));
        Assert.Throws<ArgumentException>(() => new ManifestClaimGrantRecord(
            "manifest-early",
            entitlement,
            Claim(offers[0], profileCommitStarted: true),
            PreparedAt.AddTicks(-1)));
    }

    [Fact]
    public void Journal_rejects_missing_mismatched_or_duplicate_claim_grant_pairs()
    {
        var (prepared, terminal, grant) = CreateGrantedManifest("manifest-pair", CommittedAt);
        var receipt = terminal.TerminalReceipt!;
        var wrongIdGrant = new ManifestClaimGrantRecord(
            "manifest-other",
            grant.Entitlement,
            grant.ClaimPayload,
            grant.CommittedAtUtc);
        var wrongSemanticLot = Lot(
            "provider-other",
            "lot-other",
            "family-other",
            "track-a",
            RewardRarity.ScavGrade,
            seed: 1);
        var wrongSemanticGrant = new ManifestClaimGrantRecord(
            "manifest-pair",
            new ManifestEntitlementSnapshot(
                wrongSemanticLot.Rarity,
                wrongSemanticLot.Identity,
                wrongSemanticLot.Forest,
                wrongSemanticLot.Fingerprint),
            grant.ClaimPayload,
            grant.CommittedAtUtc);

        Assert.Throws<ArgumentException>(() =>
            new CaseOpeningJournal(manifestReceipts: [receipt]));
        Assert.Throws<ArgumentException>(() =>
            new CaseOpeningJournal(manifestClaimGrants: [grant]));
        Assert.Throws<ArgumentException>(() => new CaseOpeningJournal(
            manifestReceipts: [receipt],
            manifestClaimGrants: [wrongIdGrant]));
        Assert.Throws<ArgumentException>(() => new CaseOpeningJournal(
            manifestReceipts: [receipt],
            manifestClaimGrants: [wrongSemanticGrant]));
        Assert.Throws<ArgumentException>(() => new CaseOpeningJournal(
            manifestReceipts: [receipt],
            manifestClaimGrants: [grant, grant]));
        Assert.Throws<ArgumentException>(() => new CaseOpeningJournal(
            manifestReceipts: [CreateForfeitReceipt("manifest-pair", CommittedAt, 0)],
            manifestClaimGrants: [grant]));

        var journal = new CaseOpeningJournal(activeManifest: prepared);
        Assert.Throws<InvalidOperationException>(() =>
            journal.FinishGrantedActiveManifest(terminal, wrongIdGrant));
        Assert.Same(prepared, journal.ActiveManifest);
        Assert.Empty(journal.ManifestReceipts);
    }

    [Fact]
    public void Finish_rejects_nonterminal_state_and_receipt_ids_must_be_unique()
    {
        var active = CreateManifest(ManifestPhase.Offer1);
        var journal = new CaseOpeningJournal(activeManifest: active);
        var receipt = CreateForfeitReceipt("duplicate", CommittedAt, 0);

        Assert.Throws<InvalidOperationException>(() => journal.FinishActiveManifest());
        Assert.Same(active, journal.ActiveManifest);
        Assert.Empty(journal.ManifestReceipts);
        Assert.Throws<ArgumentException>(() =>
            new CaseOpeningJournal(manifestReceipts: [receipt, receipt]));
    }

    [Fact]
    public void Receipt_pruning_never_removes_an_active_reward_debt()
    {
        var debt = CreateManifest(ManifestPhase.RewardOwed, "manifest-debt");
        var receipts = Enumerable.Range(0, 300)
            .Select(index => CreateForfeitReceipt(
                $"manifest-history-{index:D3}",
                CommittedAt.AddSeconds(index),
                0))
            .ToArray();

        var journal = new CaseOpeningJournal(activeManifest: debt, manifestReceipts: receipts);

        Assert.Same(debt, journal.ActiveManifest);
        Assert.Equal(ManifestPhase.RewardOwed, journal.ActiveManifest!.FlowState.Phase);
        Assert.Equal(256, journal.ManifestReceipts.Count);
    }

    [Theory]
    [InlineData(ManifestPhase.ClaimPrepared)]
    [InlineData(ManifestPhase.RewardOwed)]
    public void Claim_grant_pruning_is_deterministic_and_preserves_active_reward_debt(
        ManifestPhase debtPhase)
    {
        var debt = CreateManifest(debtPhase, "manifest-grant-debt");
        var evidence = Enumerable.Range(0, 260)
            .Select(index => CreateGrantedManifest(
                $"manifest-grant-history-{index:D3}",
                CommittedAt,
                preparedRootId: Id(1_000 + index)))
            .ToArray();
        var expectedIds = evidence
            .Select(pair => pair.Terminal.TerminalReceipt!)
            .Take(CaseOpeningJournal.RetainedManifestReceiptCount)
            .Select(receipt => receipt.ManifestId)
            .ToArray();

        var journal = new CaseOpeningJournal(
            activeManifest: debt,
            manifestReceipts: evidence.Select(pair => pair.Terminal.TerminalReceipt!),
            manifestClaimGrants: evidence.Select(pair => pair.Grant));

        Assert.Same(debt, journal.ActiveManifest);
        Assert.Equal(debtPhase, journal.ActiveManifest!.FlowState.Phase);
        Assert.Equal(expectedIds, journal.ManifestReceipts.Select(receipt => receipt.ManifestId));
        Assert.Equal(expectedIds, journal.ManifestClaimGrants.Select(grant => grant.ManifestId));
        Assert.All(expectedIds, manifestId => Assert.NotNull(journal.FindManifestClaimGrant(manifestId)));
        Assert.Null(journal.FindManifestClaimGrant("manifest-grant-history-259"));
    }

    [Fact]
    public void Terminal_receipts_are_immutable_and_bounded_by_durable_insertion_order()
    {
        var receipts = Enumerable.Range(0, 260)
            .Select(index => CreateForfeitReceipt($"manifest-{index:D3}", CommittedAt, 0))
            .ToArray();
        var expectedIds = receipts
            .Take(CaseOpeningJournal.RetainedManifestReceiptCount)
            .Select(receipt => receipt.ManifestId)
            .ToArray();

        var journal = new CaseOpeningJournal(manifestReceipts: receipts);

        Assert.Equal(expectedIds, journal.ManifestReceipts.Select(receipt => receipt.ManifestId));
        var projection = Assert.IsAssignableFrom<IList<ManifestTerminalReceipt>>(journal.ManifestReceipts);
        Assert.True(projection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => projection.Clear());
    }

    [Fact]
    public void Backdated_new_claim_grant_survives_pruning_a_full_history()
    {
        var history = Enumerable.Range(0, CaseOpeningJournal.RetainedManifestReceiptCount)
            .Select(index => CreateGrantedManifest(
                $"manifest-full-history-{index:D3}",
                CommittedAt.AddMinutes(index + 10),
                preparedRootId: Id(2_000 + index)))
            .ToArray();
        var (prepared, terminal, grant) = CreateGrantedManifest(
            "manifest-backdated-new",
            CommittedAt,
            preparedRootId: Id(3_000));
        var journal = new CaseOpeningJournal(
            activeManifest: prepared,
            manifestReceipts: history.Select(pair => pair.Terminal.TerminalReceipt!),
            manifestClaimGrants: history.Select(pair => pair.Grant));

        journal.FinishGrantedActiveManifest(terminal, grant);

        Assert.Equal(CaseOpeningJournal.RetainedManifestReceiptCount, journal.ManifestReceipts.Count);
        Assert.Equal("manifest-backdated-new", journal.ManifestReceipts[0].ManifestId);
        Assert.NotNull(journal.FindManifestClaimGrant("manifest-backdated-new"));
        Assert.Null(journal.FindManifestClaimGrant("manifest-full-history-255"));
    }

    [Fact]
    public void Broker_favor_alias_and_active_manifest_must_share_one_value()
    {
        var active = CreateManifest(ManifestPhase.TicketPrepared, brokerFavor: 2);
        var journal = new CaseOpeningJournal(recoveryMeter: 2, activeManifest: active);

        Assert.Equal(journal.RecoveryMeter, journal.BrokerFavor);
        Assert.Equal(active.BrokerFavor, journal.BrokerFavor);
        Assert.Throws<ArgumentException>(() =>
            new CaseOpeningJournal(recoveryMeter: 1, activeManifest: active));

        var empty = new CaseOpeningJournal(recoveryMeter: 1);
        Assert.Throws<InvalidOperationException>(() => empty.SetActiveManifest(active));
        Assert.Null(empty.ActiveManifest);
        Assert.Equal(1, empty.BrokerFavor);
    }

    [Fact]
    public void Replacement_cannot_swap_a_frozen_offer_or_rewrite_a_prior_decision()
    {
        var originalOffers = Offers();
        var swappedOffers = Offers();
        var replacementLot = Lot(
            "provider-swapped",
            "lot-swapped",
            "family-swapped",
            "track-swapped",
            RewardRarity.Restricted,
            99);
        swappedOffers[2] = new ManifestOfferSnapshot(
            3,
            replacementLot.Rarity,
            replacementLot.Identity,
            replacementLot.Forest,
            replacementLot.Fingerprint,
            new CanonicalRngEvidence(ManifestRngPurpose.OfferSelection, 3, 3));
        var original = CreateTicketPreparedManifest("manifest-offers", originalOffers);
        var swapped = CreateTicketPreparedManifest("manifest-offers", swappedOffers);
        var offerJournal = new CaseOpeningJournal(activeManifest: original);

        Assert.Throws<InvalidOperationException>(() => offerJournal.ReplaceActiveManifest(swapped));
        Assert.Same(original, offerJournal.ActiveManifest);

        var firstDecision = CreateOfferTwoManifest("manifest-decisions", CommittedAt);
        var rewrittenDecision = CreateOfferTwoManifest(
            "manifest-decisions",
            CommittedAt.AddSeconds(1));
        var decisionJournal = new CaseOpeningJournal(activeManifest: firstDecision);

        Assert.Throws<InvalidOperationException>(() =>
            decisionJournal.ReplaceActiveManifest(rewrittenDecision));
        Assert.Same(firstDecision, decisionJournal.ActiveManifest);
    }

    [Fact]
    public void Replacement_cannot_recommit_the_same_offer_transcript_with_a_new_nonce()
    {
        var offers = Offers();
        var original = CreateTicketPreparedManifest("manifest-commitment", offers, nonceCharacter: 'a');
        var recommitted = CreateTicketPreparedManifest("manifest-commitment", offers, nonceCharacter: 'b');
        var journal = new CaseOpeningJournal(activeManifest: original);

        Assert.Throws<InvalidOperationException>(() => journal.ReplaceActiveManifest(recommitted));
        Assert.Same(original, journal.ActiveManifest);
    }

    [Fact]
    public void Replacing_a_manifest_advances_favor_only_with_one_matching_committed_relay_receipt()
    {
        var (prepared, confiscated) = CreateConfiscationTransition("manifest-relay");
        var journal = new CaseOpeningJournal(activeManifest: prepared);

        journal.ReplaceActiveManifest(confiscated);

        Assert.Same(confiscated, journal.ActiveManifest);
        Assert.Equal(1, journal.RecoveryMeter);
        Assert.Equal(1, journal.BrokerFavor);

        journal.FinishActiveManifest();
        Assert.Equal(1, Assert.Single(journal.ManifestReceipts).BrokerFavorAfter);

        var unchanged = CreateManifest(ManifestPhase.Offer1, "manifest-invalid", 0);
        var unprovenFavor = CreateManifest(ManifestPhase.Offer1, "manifest-invalid", 1);
        var rejectedJournal = new CaseOpeningJournal(activeManifest: unchanged);

        Assert.Throws<InvalidOperationException>(() =>
            rejectedJournal.ReplaceActiveManifest(unprovenFavor));
        Assert.Same(unchanged, rejectedJournal.ActiveManifest);
        Assert.Equal(0, rejectedJournal.BrokerFavor);
    }

    [Fact]
    public void Legacy_meter_application_is_rejected_while_any_manifest_is_active()
    {
        var active = CreateManifest(ManifestPhase.Offer1);
        var journal = new CaseOpeningJournal(activeManifest: active);
        var committedRelay = CommittedLegacyRelay();

        Assert.Throws<InvalidOperationException>(() => journal.ApplyCommittedMeter(committedRelay));
        Assert.Same(active, journal.ActiveManifest);
        Assert.Equal(0, journal.BrokerFavor);
    }

    [Fact]
    public void Replacement_rejects_phase_rollback_and_terminal_receipt_erasure_or_rewrite()
    {
        var rewardOwed = CreateManifest(ManifestPhase.RewardOwed, "manifest-rollback");
        var earlierClaim = CreateManifest(ManifestPhase.ClaimPrepared, "manifest-rollback");
        var rollbackJournal = new CaseOpeningJournal(activeManifest: rewardOwed);

        Assert.Throws<InvalidOperationException>(() =>
            rollbackJournal.ReplaceActiveManifest(earlierClaim));
        Assert.Same(rewardOwed, rollbackJournal.ActiveManifest);

        var terminal = CreateForfeitedManifest("manifest-terminal-immutable", CommittedAt, 0);
        var erased = CreateManifest(ManifestPhase.Entitlement, "manifest-terminal-immutable");
        var rewritten = CreateForfeitedManifest(
            "manifest-terminal-immutable",
            CommittedAt.AddSeconds(1),
            0);
        var terminalJournal = new CaseOpeningJournal(activeManifest: terminal);

        Assert.Throws<InvalidOperationException>(() => terminalJournal.ReplaceActiveManifest(erased));
        Assert.Throws<InvalidOperationException>(() => terminalJournal.ReplaceActiveManifest(rewritten));
        Assert.Same(terminal, terminalJournal.ActiveManifest);
    }

    [Fact]
    public void Replacement_rejects_claim_payload_swap_and_profile_marker_regression()
    {
        var original = CreateClaimPreparedManifest("manifest-claim-immutable", Id(800), false);
        var swappedItems = CreateClaimPreparedManifest("manifest-claim-immutable", Id(801), false);
        var swapJournal = new CaseOpeningJournal(activeManifest: original);

        Assert.Throws<InvalidOperationException>(() => swapJournal.ReplaceActiveManifest(swappedItems));
        Assert.Same(original, swapJournal.ActiveManifest);

        var commitStarted = CreateClaimPreparedManifest("manifest-claim-marker", Id(802), true);
        var markerRollback = CreateClaimPreparedManifest("manifest-claim-marker", Id(802), false);
        var markerJournal = new CaseOpeningJournal(activeManifest: commitStarted);

        Assert.Throws<InvalidOperationException>(() => markerJournal.ReplaceActiveManifest(markerRollback));
        Assert.Same(commitStarted, markerJournal.ActiveManifest);

        var rewrittenGeneration = CreateClaimPreparedManifest(
            "manifest-claim-chain",
            Id(804),
            profileCommitStarted: false,
            commitGeneration: 2);
        var rewrittenPredecessor = CreateClaimPreparedManifest(
            "manifest-claim-chain",
            Id(804),
            profileCommitStarted: false,
            commitPredecessorHash: new string('1', 64));
        var chainJournal = new CaseOpeningJournal(activeManifest:
            CreateClaimPreparedManifest("manifest-claim-chain", Id(804), profileCommitStarted: false));

        Assert.Throws<InvalidOperationException>(() =>
            chainJournal.ReplaceActiveManifest(rewrittenGeneration));
        Assert.Throws<InvalidOperationException>(() =>
            chainJournal.ReplaceActiveManifest(rewrittenPredecessor));
    }

    [Fact]
    public void Claim_root_placement_reconciles_only_after_the_profile_marker_advances()
    {
        const string manifestId = "manifest-claim-placement";
        var rootId = Id(803);
        var prepared = CreateClaimPreparedManifest(
            manifestId,
            rootId,
            profileCommitStarted: false,
            parentId: "stash-a",
            slotId: "hideout",
            location: new ItemLocation { X = 1, Y = 2, R = ItemRotation.Horizontal });
        var rewrittenPreflight = CreateClaimPreparedManifest(
            manifestId,
            rootId,
            profileCommitStarted: false,
            parentId: "sorting-table",
            slotId: null,
            location: new ItemLocation { X = 3, Y = 4, R = ItemRotation.Vertical });
        var live = CreateClaimPreparedManifest(
            manifestId,
            rootId,
            profileCommitStarted: true,
            parentId: "sorting-table",
            slotId: null,
            location: new ItemLocation { X = 3, Y = 4, R = ItemRotation.Vertical });
        var recovered = CreateClaimPreparedManifest(
            manifestId,
            rootId,
            profileCommitStarted: true,
            parentId: "stash-b",
            slotId: "hideout",
            location: new ItemLocation { X = 5, Y = 6, R = ItemRotation.Horizontal });
        var journal = new CaseOpeningJournal(activeManifest: prepared);

        Assert.Throws<InvalidOperationException>(() =>
            journal.ReplaceActiveManifest(rewrittenPreflight));

        journal.ReplaceActiveManifest(live);
        journal.ReplaceActiveManifest(recovered);

        Assert.Same(recovered, journal.ActiveManifest);
        Assert.True(journal.ActiveManifest!.ClaimPrepared!.ProfileCommitStarted);
    }

    [Fact]
    public void Relay_prepared_evidence_is_immutable_and_its_profile_marker_advances_only_forward()
    {
        var original = CreateRelayPreparedManifest(
            "manifest-relay-evidence",
            Id(810),
            profileCommitStarted: false,
            ManifestRelayResult.Upgrade);
        var swappedKey = CreateRelayPreparedManifest(
            "manifest-relay-evidence",
            Id(811),
            profileCommitStarted: false,
            ManifestRelayResult.Upgrade);
        var evidenceJournal = new CaseOpeningJournal(activeManifest: original);

        Assert.Throws<InvalidOperationException>(() => evidenceJournal.ReplaceActiveManifest(swappedKey));
        Assert.Same(original, evidenceJournal.ActiveManifest);

        var swappedNextPool = CreateRelayPreparedManifest(
            "manifest-relay-evidence",
            Id(810),
            profileCommitStarted: false,
            ManifestRelayResult.Upgrade,
            nextCandidateSeed: 42);
        Assert.Throws<InvalidOperationException>(() =>
            evidenceJournal.ReplaceActiveManifest(swappedNextPool));
        Assert.Same(original, evidenceJournal.ActiveManifest);

        var swappedNextPoolAndStarted = CreateRelayPreparedManifest(
            "manifest-relay-evidence",
            Id(810),
            profileCommitStarted: true,
            ManifestRelayResult.Upgrade,
            nextCandidateSeed: 42);
        Assert.Throws<InvalidOperationException>(() =>
            evidenceJournal.ReplaceActiveManifest(swappedNextPoolAndStarted));
        Assert.Same(original, evidenceJournal.ActiveManifest);

        var commitStarted = CreateRelayPreparedManifest(
            "manifest-relay-marker",
            Id(812),
            profileCommitStarted: true,
            ManifestRelayResult.Upgrade);
        var markerRollback = CreateRelayPreparedManifest(
            "manifest-relay-marker",
            Id(812),
            profileCommitStarted: false,
            ManifestRelayResult.Upgrade);
        var markerJournal = new CaseOpeningJournal(activeManifest: commitStarted);

        Assert.Throws<InvalidOperationException>(() => markerJournal.ReplaceActiveManifest(markerRollback));

        var forwardJournal = new CaseOpeningJournal(activeManifest: markerRollback);
        forwardJournal.ReplaceActiveManifest(commitStarted);
        Assert.Same(commitStarted, forwardJournal.ActiveManifest);
    }

    [Fact]
    public void Replacement_rejects_relay_key_commit_chain_rewrites()
    {
        const string manifestId = "manifest-relay-chain";
        var original = CreateRelayPreparedManifest(
            manifestId,
            Id(813),
            profileCommitStarted: false,
            ManifestRelayResult.Upgrade,
            commitGeneration: 1,
            commitPredecessorHash: ManifestInputCommitWitness.GenesisHash);
        var rewrittenGeneration = CreateRelayPreparedManifest(
            manifestId,
            Id(813),
            profileCommitStarted: false,
            ManifestRelayResult.Upgrade,
            commitGeneration: 2,
            commitPredecessorHash: ManifestInputCommitWitness.GenesisHash);
        var rewrittenPredecessor = CreateRelayPreparedManifest(
            manifestId,
            Id(813),
            profileCommitStarted: false,
            ManifestRelayResult.Upgrade,
            commitGeneration: 1,
            commitPredecessorHash: new string('1', 64));
        var journal = new CaseOpeningJournal(activeManifest: original);

        Assert.Throws<InvalidOperationException>(() =>
            journal.ReplaceActiveManifest(rewrittenGeneration));
        Assert.Throws<InvalidOperationException>(() =>
            journal.ReplaceActiveManifest(rewrittenPredecessor));
        Assert.Same(original, journal.ActiveManifest);
    }

    [Fact]
    public void Entitlement_candidates_cannot_be_swapped_without_a_committed_relay_receipt()
    {
        var original = CreateEntitlementManifest("manifest-candidates", candidateSeed: 820);
        var swapped = CreateEntitlementManifest("manifest-candidates", candidateSeed: 830);
        var journal = new CaseOpeningJournal(activeManifest: original);

        Assert.Throws<InvalidOperationException>(() => journal.ReplaceActiveManifest(swapped));
        Assert.Same(original, journal.ActiveManifest);
    }

    [Fact]
    public void Relay_upgrade_completion_carries_the_exact_prepared_next_stage_pool()
    {
        var prepared = CreateRelayPreparedManifest(
            "manifest-relay-next-pool",
            Id(835),
            profileCommitStarted: true,
            ManifestRelayResult.Upgrade);
        var completed = prepared.CompleteRelay(CommittedAt);
        var droppedPool = WithRelayCandidates(completed, []);
        var rewrittenPool = WithRelayCandidates(
            completed,
            [
                Candidate(
                    ManifestRelayResult.Upgrade,
                    RewardRarity.Contractor,
                    "rewritten-next-upgrade",
                    "track-a",
                    50),
                Candidate(
                    ManifestRelayResult.Sidegrade,
                    RewardRarity.Uncommon,
                    "rewritten-next-sidegrade",
                    "track-a",
                    51)
            ]);
        var journal = new CaseOpeningJournal(activeManifest: prepared);

        Assert.Throws<InvalidOperationException>(() =>
            journal.ReplaceActiveManifest(droppedPool));
        Assert.Throws<InvalidOperationException>(() =>
            journal.ReplaceActiveManifest(rewrittenPool));
        Assert.Same(prepared, journal.ActiveManifest);

        journal.ReplaceActiveManifest(completed);
        Assert.Same(completed, journal.ActiveManifest);
    }

    [Fact]
    public void Relay_completion_receipt_must_match_the_prepared_outcome()
    {
        var preparedUpgrade = CreateRelayPreparedManifest(
            "manifest-relay-mismatch",
            Id(840),
            profileCommitStarted: true,
            ManifestRelayResult.Upgrade);
        var (_, confiscated) = CreateConfiscationTransition("manifest-relay-mismatch");
        var journal = new CaseOpeningJournal(activeManifest: preparedUpgrade);

        Assert.Throws<InvalidOperationException>(() => journal.ReplaceActiveManifest(confiscated));
        Assert.Same(preparedUpgrade, journal.ActiveManifest);
        Assert.Equal(0, journal.BrokerFavor);
    }

    private static ManifestRecord CreateManifest(
        ManifestPhase phase,
        string manifestId = "manifest-1",
        int brokerFavor = 0)
    {
        var offers = Offers();
        var preparedTicket = ManifestFlowState.PrepareTicket();
        var activeTicket = ManifestFlowState.ActivateTicket(preparedTicket);
        ManifestFlowState state;
        IEnumerable<ManifestDecisionRecord>? decisions = null;
        ManifestEntitlementSnapshot? entitlement = null;
        IEnumerable<ManifestRelayCandidateSnapshot>? candidates = null;
        ManifestClaimPreparedPayload? claimPrepared = null;
        ManifestRelayPreparedPayload? relayPrepared = null;

        switch (phase)
        {
            case ManifestPhase.TicketPrepared:
                state = preparedTicket;
                break;
            case ManifestPhase.Offer1:
                state = activeTicket;
                break;
            case ManifestPhase.Entitlement:
                state = ManifestStateMachine.DecideOffer(activeTicket, ManifestOfferDecision.Lock);
                decisions = LockFirstDecision();
                entitlement = Entitlement(offers[0]);
                break;
            case ManifestPhase.ClaimPrepared:
                state = ManifestStateMachine.PrepareClaim(
                    ManifestStateMachine.DecideOffer(activeTicket, ManifestOfferDecision.Lock));
                decisions = LockFirstDecision();
                entitlement = Entitlement(offers[0]);
                claimPrepared = Claim(offers[0], profileCommitStarted: false);
                break;
            case ManifestPhase.RewardOwed:
                state = ManifestStateMachine.MarkRewardOwed(ManifestStateMachine.PrepareClaim(
                    ManifestStateMachine.DecideOffer(activeTicket, ManifestOfferDecision.Lock)));
                decisions = LockFirstDecision();
                entitlement = Entitlement(offers[0]);
                claimPrepared = Claim(offers[0], profileCommitStarted: true);
                break;
            case ManifestPhase.RelayPrepared:
                var entitlementState = ManifestStateMachine.DecideOffer(
                    activeTicket,
                    ManifestOfferDecision.Lock);
                state = ManifestStateMachine.PrepareRelay(entitlementState, relayEligible: true);
                decisions = LockFirstDecision();
                entitlement = Entitlement(offers[0]);
                var upgrade = Candidate(
                    ManifestRelayResult.Upgrade,
                    RewardRarity.Uncommon,
                    "relay-upgrade",
                    "track-a",
                    20);
                var sidegrade = Candidate(
                    ManifestRelayResult.Sidegrade,
                    RewardRarity.ScavGrade,
                    "relay-sidegrade",
                    "track-a",
                    21);
                var nextUpgrade = Candidate(
                    ManifestRelayResult.Upgrade,
                    RewardRarity.Contractor,
                    "relay-next-upgrade",
                    "track-a",
                    22);
                var nextSidegrade = Candidate(
                    ManifestRelayResult.Sidegrade,
                    RewardRarity.Uncommon,
                    "relay-next-sidegrade",
                    "track-a",
                    23);
                candidates = [upgrade, sidegrade];
                relayPrepared = new ManifestRelayPreparedPayload(
                    Id(700),
                    ManifestRelayResult.Upgrade,
                    Entitlement(upgrade),
                    RelayRules.GetOdds(1),
                    OutcomeRng(0),
                    TargetRng(0),
                    brokerFavor,
                    brokerFavor,
                    false,
                    PreparedAt,
                    [nextUpgrade, nextSidegrade]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(phase), phase, null);
        }

        return new ManifestRecord(
            manifestId,
            "catalog-1",
            Commitment(manifestId, offers),
            state,
            Ticket(committed: phase != ManifestPhase.TicketPrepared),
            offers,
            decisions,
            entitlement,
            candidates,
            relayHistory: null,
            brokerFavor,
            claimPrepared,
            relayPrepared);
    }

    private static ManifestRecord CreateTicketPreparedManifest(
        string manifestId,
        IReadOnlyList<ManifestOfferSnapshot> offers,
        char nonceCharacter = 'a',
        bool profileCommitStarted = false,
        long? commitGeneration = null,
        string? commitPredecessorHash = null) => new(
        manifestId,
        "catalog-1",
        Commitment(manifestId, offers, nonceCharacter),
        ManifestFlowState.PrepareTicket(),
        Ticket(
            committed: false,
            profileCommitStarted,
            commitGeneration,
            commitPredecessorHash),
        offers,
        decisions: null,
        entitlement: null,
        relayCandidates: null,
        relayHistory: null,
        brokerFavor: 0);

    private static ManifestRecord CreateClaimPreparedManifest(
        string manifestId,
        MongoId rootId,
        bool profileCommitStarted,
        string parentId = "stash",
        string? slotId = "hideout",
        ItemLocation? location = null,
        long commitGeneration = 1,
        string? commitPredecessorHash = null)
    {
        var offers = Offers();
        var entitlementState = ManifestStateMachine.DecideOffer(
            ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket()),
            ManifestOfferDecision.Lock);
        var state = ManifestStateMachine.PrepareClaim(entitlementState);
        return new ManifestRecord(
            manifestId,
            "catalog-1",
            Commitment(manifestId, offers),
            state,
            Ticket(committed: true),
            offers,
            LockFirstDecision(),
            Entitlement(offers[0]),
            relayCandidates: null,
            relayHistory: null,
            brokerFavor: 0,
            claimPrepared: Claim(
                offers[0],
                profileCommitStarted,
                rootId,
                parentId,
                slotId,
                location,
                commitGeneration,
                commitPredecessorHash));
    }

    private static ManifestRecord CreateRelayPreparedManifest(
        string manifestId,
        MongoId keyId,
        bool profileCommitStarted,
        ManifestRelayResult outcome,
        long? commitGeneration = null,
        string? commitPredecessorHash = null,
        int nextCandidateSeed = 32)
    {
        var offers = Offers();
        var entitlementState = ManifestStateMachine.DecideOffer(
            ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket()),
            ManifestOfferDecision.Lock);
        var state = ManifestStateMachine.PrepareRelay(entitlementState, relayEligible: true);
        var upgrade = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Uncommon,
            "relay-upgrade",
            "track-a",
            30);
        var sidegrade = Candidate(
            ManifestRelayResult.Sidegrade,
            RewardRarity.ScavGrade,
            "relay-sidegrade",
            "track-a",
            31);
        var confiscated = outcome == ManifestRelayResult.Confiscated;
        var nextCandidates = outcome == ManifestRelayResult.Upgrade
            ? new[]
            {
                Candidate(
                    ManifestRelayResult.Upgrade,
                    RewardRarity.Contractor,
                    "relay-next-upgrade",
                    "track-a",
                    nextCandidateSeed),
                Candidate(
                    ManifestRelayResult.Sidegrade,
                    RewardRarity.Uncommon,
                    "relay-next-sidegrade",
                    "track-a",
                    nextCandidateSeed + 1)
            }
            : [];
        var payload = new ManifestRelayPreparedPayload(
            keyId,
            outcome,
            confiscated ? null : Entitlement(outcome == ManifestRelayResult.Upgrade ? upgrade : sidegrade),
            RelayRules.GetOdds(1),
            OutcomeRng(confiscated ? CanonicalRngEvidence.UnitDenominator - 1 : 0),
            confiscated ? null : TargetRng(0),
            brokerFavorBefore: 0,
            brokerFavorAfter: confiscated ? 1 : 0,
            profileCommitStarted,
            PreparedAt,
            nextCandidates,
            commitGeneration,
            commitPredecessorHash);
        return new ManifestRecord(
            manifestId,
            "catalog-1",
            Commitment(manifestId, offers),
            state,
            Ticket(committed: true),
            offers,
            LockFirstDecision(),
            Entitlement(offers[0]),
            [upgrade, sidegrade],
            relayHistory: null,
            brokerFavor: 0,
            relayPrepared: payload);
    }

    private static ManifestRecord CreateEntitlementManifest(string manifestId, int candidateSeed)
    {
        var offers = Offers();
        var state = ManifestStateMachine.DecideOffer(
            ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket()),
            ManifestOfferDecision.Lock);
        var upgrade = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Uncommon,
            $"upgrade-{candidateSeed}",
            "track-a",
            candidateSeed);
        var sidegrade = Candidate(
            ManifestRelayResult.Sidegrade,
            RewardRarity.ScavGrade,
            $"sidegrade-{candidateSeed}",
            "track-a",
            candidateSeed + 1);
        return new ManifestRecord(
            manifestId,
            "catalog-1",
            Commitment(manifestId, offers),
            state,
            Ticket(committed: true),
            offers,
            LockFirstDecision(),
            Entitlement(offers[0]),
            [upgrade, sidegrade],
            relayHistory: null,
            brokerFavor: 0);
    }

    private static ManifestRecord WithRelayCandidates(
        ManifestRecord source,
        IEnumerable<ManifestRelayCandidateSnapshot> candidates) => new(
        source.ManifestId,
        source.CatalogSnapshotId,
        source.Commitment,
        source.FlowState,
        source.Ticket,
        source.Offers,
        source.Decisions,
        source.Entitlement,
        candidates,
        source.RelayHistory,
        source.BrokerFavor,
        source.ClaimPrepared,
        source.RelayPrepared,
        source.TerminalReceipt);

    private static ManifestRecord CreateOfferTwoManifest(
        string manifestId,
        DateTimeOffset decidedAtUtc)
    {
        var offers = Offers();
        var state = ManifestStateMachine.DecideOffer(
            ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket()),
            ManifestOfferDecision.Burn);
        return new ManifestRecord(
            manifestId,
            "catalog-1",
            Commitment(manifestId, offers),
            state,
            Ticket(committed: true),
            offers,
            [new ManifestDecisionRecord(1, ManifestOfferDecision.Burn, decidedAtUtc)],
            entitlement: null,
            relayCandidates: null,
            relayHistory: null,
            brokerFavor: 0);
    }

    private static (
        ManifestRecord Prepared,
        ManifestRecord Terminal,
        ManifestClaimGrantRecord Grant) CreateGrantedManifest(
        string manifestId,
        DateTimeOffset completedAtUtc,
        int brokerFavor = 0,
        MongoId? preparedRootId = null)
    {
        var offers = Offers();
        var entitlementState = ManifestStateMachine.DecideOffer(
            ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket()),
            ManifestOfferDecision.Lock);
        var claimState = ManifestStateMachine.PrepareClaim(entitlementState);
        var terminalState = ManifestStateMachine.CompleteClaim(claimState);
        var decisions = LockFirstDecision();
        var entitlement = Entitlement(offers[0]);
        var claim = Claim(offers[0], profileCommitStarted: true, preparedRootId);
        var prepared = new ManifestRecord(
            manifestId,
            "catalog-1",
            Commitment(manifestId, offers),
            claimState,
            Ticket(committed: true),
            offers,
            decisions,
            entitlement,
            relayCandidates: null,
            relayHistory: null,
            brokerFavor,
            claimPrepared: claim);
        var receipt = new ManifestTerminalReceipt(
            manifestId,
            ManifestPhase.Granted,
            offers.Select(Receipt),
            Receipt(offers[0]),
            decisions,
            relayHistory: null,
            brokerFavor,
            brokerFavor,
            completedAtUtc);
        var manifest = new ManifestRecord(
            manifestId,
            "catalog-1",
            Commitment(manifestId, offers),
            terminalState,
            Ticket(committed: true),
            offers,
            decisions,
            entitlement: null,
            relayCandidates: null,
            relayHistory: null,
            brokerFavor,
            terminalReceipt: receipt);
        var grant = new ManifestClaimGrantRecord(
            manifestId,
            entitlement,
            claim,
            completedAtUtc);
        return (prepared, manifest, grant);
    }

    private static ManifestRecord CreateForfeitedManifest(
        string manifestId,
        DateTimeOffset completedAtUtc,
        int brokerFavor)
    {
        var offers = Offers();
        var activeTicket = ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket());
        var entitlementState = ManifestStateMachine.DecideOffer(activeTicket, ManifestOfferDecision.Lock);
        var terminalState = ManifestStateMachine.ForfeitMissingContent(
            entitlementState,
            missingContentBlocked: true);
        var decisions = LockFirstDecision();
        var receipt = CreateForfeitReceipt(manifestId, completedAtUtc, brokerFavor, offers, decisions);
        return new ManifestRecord(
            manifestId,
            "catalog-1",
            Commitment(manifestId, offers),
            terminalState,
            Ticket(committed: true),
            offers,
            decisions,
            entitlement: null,
            relayCandidates: null,
            relayHistory: null,
            brokerFavor,
            terminalReceipt: receipt);
    }

    private static (ManifestRecord Prepared, ManifestRecord Confiscated) CreateConfiscationTransition(
        string manifestId,
        bool profileCommitStarted = true)
    {
        var offers = Offers();
        var activeTicket = ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket());
        var entitlementState = ManifestStateMachine.DecideOffer(activeTicket, ManifestOfferDecision.Lock);
        var relayState = ManifestStateMachine.PrepareRelay(entitlementState, relayEligible: true);
        var decisions = LockFirstDecision();
        var entitlement = Entitlement(offers[0]);
        var upgrade = Candidate(
            ManifestRelayResult.Upgrade,
            RewardRarity.Uncommon,
            "relay-upgrade",
            "track-a",
            30);
        var sidegrade = Candidate(
            ManifestRelayResult.Sidegrade,
            RewardRarity.ScavGrade,
            "relay-sidegrade",
            "track-a",
            31);
        var outcomeRng = OutcomeRng(CanonicalRngEvidence.UnitDenominator - 1);
        var preparedPayload = new ManifestRelayPreparedPayload(
            Id(701),
            ManifestRelayResult.Confiscated,
            output: null,
            RelayRules.GetOdds(1),
            outcomeRng,
            targetRng: null,
            brokerFavorBefore: 0,
            brokerFavorAfter: 1,
            profileCommitStarted,
            PreparedAt);
        var prepared = new ManifestRecord(
            manifestId,
            "catalog-1",
            Commitment(manifestId, offers),
            relayState,
            Ticket(committed: true),
            offers,
            decisions,
            entitlement,
            [upgrade, sidegrade],
            relayHistory: null,
            brokerFavor: 0,
            relayPrepared: preparedPayload);

        var committedReceipt = new ManifestRelayReceipt(
            1,
            Receipt(offers[0]),
            ManifestRelayResult.Confiscated,
            output: null,
            RelayRules.GetOdds(1),
            outcomeRng,
            targetRng: null,
            eligibleTargets: null,
            brokerFavorBefore: 0,
            brokerFavorAfter: 1,
            CommittedAt);
        var terminalState = ManifestStateMachine.CompleteRelay(
            relayState,
            ManifestRelayResult.Confiscated,
            outputRarity: null);
        var terminalReceipt = new ManifestTerminalReceipt(
            manifestId,
            ManifestPhase.Confiscated,
            offers.Select(Receipt),
            Receipt(offers[0]),
            decisions,
            [committedReceipt],
            brokerFavorBefore: 0,
            brokerFavorAfter: 1,
            CommittedAt);
        var confiscated = new ManifestRecord(
            manifestId,
            "catalog-1",
            Commitment(manifestId, offers),
            terminalState,
            Ticket(committed: true),
            offers,
            decisions,
            entitlement: null,
            relayCandidates: null,
            relayHistory: [committedReceipt],
            brokerFavor: 1,
            terminalReceipt: terminalReceipt);
        return (prepared, confiscated);
    }

    private static ManifestTerminalReceipt CreateForfeitReceipt(
        string manifestId,
        DateTimeOffset completedAtUtc,
        int brokerFavor)
    {
        var offers = Offers();
        return CreateForfeitReceipt(
            manifestId,
            completedAtUtc,
            brokerFavor,
            offers,
            LockFirstDecision());
    }

    private static ManifestTerminalReceipt CreateForfeitReceipt(
        string manifestId,
        DateTimeOffset completedAtUtc,
        int brokerFavor,
        IReadOnlyList<ManifestOfferSnapshot> offers,
        IReadOnlyList<ManifestDecisionRecord> decisions) => new(
        manifestId,
        ManifestPhase.Forfeited,
        offers.Select(Receipt),
        Receipt(offers[0]),
        decisions,
        relayHistory: null,
        brokerFavor,
        brokerFavor,
        completedAtUtc);

    private static ManifestClaimPreparedPayload Claim(
        ManifestOfferSnapshot offer,
        bool profileCommitStarted,
        MongoId? preparedRootId = null,
        string parentId = "stash",
        string? slotId = "hideout",
        ItemLocation? location = null,
        long commitGeneration = 1,
        string? commitPredecessorHash = null)
    {
        var rootId = preparedRootId ?? Id(600);
        var item = new Item
        {
            Id = rootId,
            Template = (MongoId)offer.Forest.Nodes[0].TemplateId,
            ParentId = parentId,
            SlotId = slotId,
            Location = location,
            Upd = new Upd { StackObjectsCount = 1 }
        };
        return new ManifestClaimPreparedPayload(
            [item],
            [rootId],
            profileCommitStarted,
            PreparedAt,
            commitGeneration,
            commitPredecessorHash ?? ManifestClaimCommitWitness.GenesisHash);
    }

    private static ManifestTicketPayload Ticket(
        bool committed,
        bool? profileCommitStarted = null,
        long? commitGeneration = null,
        string? commitPredecessorHash = null) => new(
        Id(500),
        Id(501),
        PreparedAt,
        profileCommitStarted ?? committed,
        committed,
        committed ? CommittedAt : null,
        commitGeneration,
        commitPredecessorHash);

    private static ManifestDecisionRecord[] LockFirstDecision() =>
        [new(1, ManifestOfferDecision.Lock, CommittedAt)];

    private static ManifestOfferSnapshot[] Offers()
    {
        var lots = new[]
        {
            Lot("provider-a", "lot-a", "family-a", "track-a", RewardRarity.ScavGrade, 1),
            Lot("provider-b", "lot-b", "family-b", "track-b", RewardRarity.Contractor, 2),
            Lot("provider-c", "lot-c", "family-c", "track-c", RewardRarity.Restricted, 3)
        };
        return lots.Select((lot, index) => new ManifestOfferSnapshot(
            index + 1,
            lot.Rarity,
            lot.Identity,
            lot.Forest,
            lot.Fingerprint,
            new CanonicalRngEvidence(ManifestRngPurpose.OfferSelection, index + 1, index + 1)))
            .ToArray();
    }

    private static ManifestRelayCandidateSnapshot Candidate(
        ManifestRelayResult result,
        RewardRarity rarity,
        string lotId,
        string trackId,
        int seed)
    {
        var lot = Lot("provider-" + lotId, lotId, "family-relay", trackId, rarity, seed);
        return new ManifestRelayCandidateSnapshot(result, rarity, lot.Identity, lot.Forest, lot.Fingerprint);
    }

    private static LotData Lot(
        string providerId,
        string lotId,
        string familyId,
        string trackId,
        RewardRarity rarity,
        int seed)
    {
        var forest = RewardForest.Create(
            [new RewardForestNode("root", "root", Id(seed).ToString(), null, null, null, 1)]);
        var fingerprint = RewardForestFingerprintV2.Compute(providerId, lotId, forest);
        var identity = new CargoLotIdentitySnapshot(
            providerId,
            "1.0.0",
            lotId,
            "Display " + lotId,
            "Purpose " + lotId,
            new FamilyId(familyId),
            new TrackId(trackId),
            forest.Nodes[0].TemplateId,
            1d,
            new RaidRole("role-" + lotId),
            fingerprint);
        return new LotData(rarity, identity, forest, fingerprint);
    }

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

    private static CanonicalRngEvidence OutcomeRng(long numerator) => new(
        ManifestRngPurpose.RelayOutcome,
        1,
        numerator);

    private static CanonicalRngEvidence TargetRng(long numerator) => new(
        ManifestRngPurpose.RelayTargetSelection,
        1,
        numerator);

    private static ManifestCommitmentEvidence Commitment(
        string manifestId,
        IEnumerable<ManifestOfferSnapshot> offers,
        char nonceCharacter = 'a') =>
        ManifestCommitmentEvidence.Create(
            manifestId,
            "catalog-1",
            new string(nonceCharacter, ManifestCommitmentEvidence.NonceByteCount * 2),
            offers);

    private static CaseOpeningRecord PreparedOpening() => new(
        new MongoId(),
        new MongoId(),
        "reward",
        [],
        PreparedAt,
        OpeningRecordStatus.Prepared,
        committedAtUtc: null);

    private static RelaySettlementRecord PreparedRelay()
    {
        var root = new MongoId();
        return new RelaySettlementRecord(
            new MongoId(),
            root,
            [root],
            "reward",
            RewardRarity.ScavGrade,
            1,
            RelayRecordAction.Secure,
            keyId: null,
            RelayOutcome.Secured,
            outputRewardId: null,
            outputItems: [],
            meterBefore: 0,
            meterAfter: 0,
            guaranteedUpgrade: false,
            profileCommitStarted: false,
            PreparedAt,
            RelayRecordStatus.Prepared,
            committedAtUtc: null);
    }

    private static RelaySettlementRecord CommittedLegacyRelay()
    {
        var root = new MongoId();
        return new RelaySettlementRecord(
            new MongoId(),
            root,
            [root],
            "reward",
            RewardRarity.ScavGrade,
            1,
            RelayRecordAction.Relay,
            new MongoId(),
            RelayOutcome.Confiscated,
            outputRewardId: null,
            outputItems: [],
            meterBefore: 0,
            meterAfter: 1,
            guaranteedUpgrade: false,
            profileCommitStarted: true,
            PreparedAt,
            RelayRecordStatus.Committed,
            CommittedAt);
    }

    private static MongoId Id(int value) => (MongoId)value.ToString("x24");

    private sealed record LotData(
        RewardRarity Rarity,
        CargoLotIdentitySnapshot Identity,
        RewardForest Forest,
        RewardForestFingerprintV2 Fingerprint);
}
