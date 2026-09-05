using ContrabandCases.Client.Opening;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using Newtonsoft.Json;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class RelayClientTests
{
    private const string StakeRootId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RewardRootId = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string KeyId = "cccccccccccccccccccccccc";

    [Fact]
    public void Relay_cycle_can_repeat_without_reopening_or_reusing_a_callback_claim()
    {
        var phase = new OpeningPhaseMachine();
        var callbacks = new OperationCallbackGate();

        Assert.True(phase.TryOpen(out var run));
        Assert.True(phase.TryConfirm(run));
        var opening = callbacks.Begin();
        Assert.True(callbacks.TryClaim(opening));
        Assert.False(callbacks.TryClaim(opening));
        Assert.True(phase.TryCommit(run));
        Assert.True(phase.TryComplete(run));

        Assert.True(phase.TryContinue(run));
        var relay = callbacks.Begin();
        Assert.True(relay > opening);
        Assert.False(callbacks.TryClaim(opening));
        Assert.True(callbacks.TryClaim(relay));
        Assert.True(phase.TryCommit(run));
        Assert.True(phase.TryComplete(run));
        Assert.Equal(OpeningPhase.Result, phase.Phase);
    }

    [Fact]
    public void Altered_tree_rejection_returns_to_decision_with_secure_enabled_and_relay_disabled()
    {
        var phase = new OpeningPhaseMachine();
        Assert.True(phase.TryOpen(out var run));
        Assert.True(phase.TryConfirm(run));
        Assert.True(phase.TryCommit(run));
        Assert.True(phase.TryComplete(run));
        Assert.True(phase.TryContinue(run));

        Assert.True(phase.TryResolvePending(run));
        var availability = RelayDecisionAvailability.Disabled(
            "The staked weapon no longer matches its awarded tree.");

        Assert.Equal(OpeningPhase.Result, phase.Phase);
        Assert.True(availability.SecureEnabled);
        Assert.False(availability.RelayEnabled);
        Assert.Contains("awarded tree", availability.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Synchronous_dispatch_throw_cancels_generation_and_restores_decision_for_retry()
    {
        var phase = new OpeningPhaseMachine();
        var callbacks = new OperationCallbackGate();
        Assert.True(phase.TryOpen(out var run));
        Assert.True(phase.TryConfirm(run));
        Assert.True(phase.TryCommit(run));
        Assert.True(phase.TryComplete(run));
        Assert.True(phase.TryContinue(run));
        var operation = callbacks.Begin();

        Assert.True(callbacks.TryCancel(operation));
        Assert.False(callbacks.IsPending);
        Assert.False(callbacks.TryClaim(operation));
        Assert.True(phase.TryResolvePending(run));
        Assert.Equal(OpeningPhase.Result, phase.Phase);
    }

    [Theory]
    [InlineData(true, false, RelayDispatchFailureRecovery.RestoreVerifiedDecision)]
    [InlineData(true, true, RelayDispatchFailureRecovery.RestoreVerifiedDecision)]
    [InlineData(false, false, RelayDispatchFailureRecovery.OfferDecisionSnapshotRetry)]
    [InlineData(false, true, RelayDispatchFailureRecovery.ReleaseDetachedRun)]
    public void Dispatch_failure_without_a_predecision_snapshot_never_attempts_to_restore_one(
        bool hasVerifiedDecisionSnapshot,
        bool presentationDetached,
        RelayDispatchFailureRecovery expected)
    {
        Assert.Equal(
            expected,
            RelayDispatchFailurePolicy.Decide(
                hasVerifiedDecisionSnapshot,
                presentationDetached));
    }

    [Fact]
    public void Lost_snapshot_gets_one_automatic_read_only_retry_then_retains_pending_recovery()
    {
        var retries = new VerificationRetryBudget(maximumAutomaticRetries: 1);

        Assert.True(retries.TryTakeAutomaticRetry());
        Assert.False(retries.TryTakeAutomaticRetry());
        Assert.Equal(1, retries.AutomaticRetries);

        retries.Reset();
        Assert.True(retries.TryTakeAutomaticRetry());
    }

    [Fact]
    public void Completing_recovered_action_clears_original_snapshot_before_followup_auto_secure()
    {
        var recovery = new RelayPreparedActionRecovery();
        var preparedSnapshot = Snapshot(
            settlementPending: true,
            pendingAction: "Relay");
        RelaySnapshot? activeSnapshot = preparedSnapshot;
        var firstRevealSnapshot = Assert.IsType<RelaySnapshot>(activeSnapshot);

        recovery.Begin();
        Assert.True(recovery.IsPending);

        RouletteController.CompletePreparedActionRecovery(
            recovery,
            ref activeSnapshot);

        Assert.False(recovery.IsPending);
        Assert.Null(activeSnapshot);
        Assert.Same(preparedSnapshot, firstRevealSnapshot);
        Assert.Equal(StakeRootId, firstRevealSnapshot.Status.StakeRootId);
        Assert.Equal(
            RelayDispatchFailureRecovery.OfferDecisionSnapshotRetry,
            RelayDispatchFailurePolicy.Decide(
                hasVerifiedDecisionSnapshot: activeSnapshot is not null,
                presentationDetached: false));
        Assert.Throws<InvalidOperationException>(() =>
            RouletteController.CompletePreparedActionRecovery(
                recovery,
                ref activeSnapshot));
        recovery.Begin();
        Assert.Throws<InvalidOperationException>(() => recovery.Begin());
    }

    [Fact]
    public void Completing_recovered_action_without_its_snapshot_preserves_pending_recovery()
    {
        var recovery = new RelayPreparedActionRecovery();
        RelaySnapshot? missingSnapshot = null;

        recovery.Begin();

        Assert.Throws<InvalidOperationException>(() =>
            RouletteController.CompletePreparedActionRecovery(
                recovery,
                ref missingSnapshot));
        Assert.True(recovery.IsPending);
        Assert.Null(missingSnapshot);
    }

    [Fact]
    public void Automatic_secure_promotes_only_the_verified_next_decision_for_dispatch_retry()
    {
        var oldSnapshot = Snapshot(stakeRootId: StakeRootId);
        var freshSnapshot = Snapshot(stakeRootId: RewardRootId);
        RelaySnapshot? decisionSnapshot = oldSnapshot;
        RelaySnapshot? nextDecisionSnapshot = freshSnapshot;

        RouletteController.PrepareAutomaticSecureSnapshots(
            resumePrepared: false,
            showPresentation: false,
            Receipt(RelayOutcome.RarityUpgrade, "upgrade", RewardRootId, RewardRarity.Uncommon),
            RewardRootId,
            ref decisionSnapshot,
            ref nextDecisionSnapshot);

        Assert.Same(freshSnapshot, decisionSnapshot);
        Assert.NotSame(oldSnapshot, decisionSnapshot);
        Assert.Null(nextDecisionSnapshot);
        Assert.Equal(
            RelayDispatchFailureRecovery.RestoreVerifiedDecision,
            RelayDispatchFailurePolicy.Decide(
                hasVerifiedDecisionSnapshot: decisionSnapshot is not null,
                presentationDetached: false));
    }

    [Fact]
    public void Automatic_secure_without_a_verified_next_decision_clears_the_old_retry_snapshot()
    {
        RelaySnapshot? decisionSnapshot = Snapshot(stakeRootId: StakeRootId);
        RelaySnapshot? nextDecisionSnapshot = null;

        RouletteController.PrepareAutomaticSecureSnapshots(
            resumePrepared: false,
            showPresentation: false,
            Receipt(RelayOutcome.RarityUpgrade, "upgrade", RewardRootId, RewardRarity.Uncommon),
            RewardRootId,
            ref decisionSnapshot,
            ref nextDecisionSnapshot);

        Assert.Null(decisionSnapshot);
        Assert.Null(nextDecisionSnapshot);
        Assert.Equal(
            RelayDispatchFailureRecovery.OfferDecisionSnapshotRetry,
            RelayDispatchFailurePolicy.Decide(
                hasVerifiedDecisionSnapshot: decisionSnapshot is not null,
                presentationDetached: false));
    }

    [Theory]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    public void Automatic_secure_preserves_snapshots_until_the_controller_freshness_guard_is_met(
        bool resumePrepared,
        bool showPresentation,
        bool hasReceipt,
        bool currentSnapshotMatchesCommittedRoot)
    {
        var oldSnapshot = Snapshot(
            stakeRootId: currentSnapshotMatchesCommittedRoot
                ? RewardRootId
                : StakeRootId);
        var freshSnapshot = Snapshot(stakeRootId: RewardRootId);
        RelaySnapshot? decisionSnapshot = oldSnapshot;
        RelaySnapshot? nextDecisionSnapshot = freshSnapshot;
        var receipt = hasReceipt
            ? Receipt(RelayOutcome.RarityUpgrade, "upgrade", RewardRootId, RewardRarity.Uncommon)
            : null;

        RouletteController.PrepareAutomaticSecureSnapshots(
            resumePrepared,
            showPresentation,
            receipt,
            RewardRootId,
            ref decisionSnapshot,
            ref nextDecisionSnapshot);

        Assert.Same(oldSnapshot, decisionSnapshot);
        Assert.Same(freshSnapshot, nextDecisionSnapshot);
    }

    [Fact]
    public void Snapshot_observer_timeout_is_bounded_by_unscaled_elapsed_time()
    {
        var timeout = new RelaySnapshotTimeoutBudget(timeoutSeconds: 15d);

        Assert.False(timeout.Advance(14.9d));
        Assert.Equal(14.9d, timeout.ElapsedSeconds, precision: 8);
        Assert.True(timeout.Advance(0.1d));
        Assert.Equal(15d, timeout.ElapsedSeconds);
        Assert.True(timeout.Advance(100d));
        Assert.Equal(15d, timeout.ElapsedSeconds);
    }

    [Fact]
    public void Snapshot_observer_start_failure_clears_ownership_and_returns_pending_phase_to_result_once()
    {
        var phase = new OpeningPhaseMachine();
        Assert.True(phase.TryOpen(out var run));
        Assert.True(phase.TryConfirm(run));
        Assert.True(phase.TryCommit(run));
        Assert.True(phase.TryComplete(run));
        Assert.True(phase.TryContinue(run));
        object? observation = null;
        var failureCount = 0;

        RouletteController.StartSnapshotObservation(
            ref observation,
            () => throw new InvalidOperationException("observer start failed"),
            _ =>
            {
                failureCount++;
                Assert.True(phase.TryResolvePending(run));
            });

        Assert.Null(observation);
        Assert.Equal(1, failureCount);
        Assert.Equal(OpeningPhase.Result, phase.Phase);
    }

    [Fact]
    public void Snapshot_observer_start_failure_does_not_clobber_retry_observer_installed_by_failure_callback()
    {
        object? observation = null;
        var retryObservation = new object();
        var failureCount = 0;

        RouletteController.StartSnapshotObservation(
            ref observation,
            () => throw new InvalidOperationException("first observer start failed"),
            _ =>
            {
                failureCount++;
                RouletteController.StartSnapshotObservation(
                    ref observation,
                    () => retryObservation,
                    _ => throw new Xunit.Sdk.XunitException("The retry observer should start."));
            });

        Assert.Same(retryObservation, observation);
        Assert.Equal(1, failureCount);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.001d)]
    public void Snapshot_observer_timeout_rejects_invalid_frame_deltas(double delta)
    {
        var timeout = new RelaySnapshotTimeoutBudget();

        Assert.Throws<ArgumentOutOfRangeException>(() => timeout.Advance(delta));
    }

    [Fact]
    public void Cancelled_or_claimed_generation_cannot_be_accepted_by_a_duplicate_callback()
    {
        var callbacks = new OperationCallbackGate();
        var cancelled = callbacks.Begin();
        Assert.True(callbacks.TryCancel(cancelled));
        Assert.False(callbacks.TryClaim(cancelled));

        var accepted = callbacks.Begin();
        Assert.True(callbacks.TryClaim(accepted));
        Assert.False(callbacks.TryClaim(accepted));
        Assert.False(callbacks.TryCancel(accepted));
    }

    [Fact]
    public void Synchronous_reveal_setup_failure_returns_committed_reward_to_secure_path()
    {
        var phase = new OpeningPhaseMachine();
        Assert.True(phase.TryOpen(out var run));
        Assert.True(phase.TryConfirm(run));
        Assert.True(phase.TryCommit(run));

        Assert.True(phase.TryAbortRevealToResult(run));
        Assert.True(phase.TryContinue(run));

        Assert.Equal(OpeningPhase.Pending, phase.Phase);
    }

    [Fact]
    public void Relay_hold_confirms_once_only_after_the_full_threshold()
    {
        var hold = new RelayHoldConfirmation(1.2d);

        Assert.True(hold.Begin());
        Assert.False(hold.Advance(1.199d));
        Assert.Equal(1.199d / 1.2d, hold.Progress, precision: 8);
        Assert.True(hold.Advance(0.001d));
        Assert.True(hold.IsConfirmed);
        Assert.False(hold.Advance(10d));
        Assert.False(hold.Begin());
    }

    [Fact]
    public void Released_hold_resets_and_cannot_confirm_from_accumulated_time()
    {
        var hold = new RelayHoldConfirmation(1.2d);
        Assert.True(hold.Begin());
        Assert.False(hold.Advance(0.9d));

        hold.Cancel();

        Assert.Equal(0d, hold.Progress);
        Assert.True(hold.Begin());
        Assert.False(hold.Advance(0.31d));
        Assert.False(hold.IsConfirmed);
    }

    [Fact]
    public void Snapshot_envelope_accepts_only_the_requested_authoritative_chain()
    {
        var json = Envelope(Snapshot());

        var snapshot = RelaySnapshotEnvelope.Parse(json, StakeRootId);

        Assert.Equal(StakeRootId, snapshot.Status.StakeRootId);
        Assert.Equal(3, snapshot.PublishedLadder.Count);
        Assert.Equal(55, snapshot.PublishedLadder[0].UpgradePercent);
        Assert.Equal("upgrade", Assert.Single(snapshot.UpgradeCandidates).RewardId);
        Assert.Equal("sidegrade", Assert.Single(snapshot.SidegradeCandidates).RewardId);
    }

    [Fact]
    public void Snapshot_envelope_rejects_server_errors_mismatched_roots_and_false_odds()
    {
        Assert.Throws<RelaySnapshotException>(() => RelaySnapshotEnvelope.Parse(
            "{\"err\":1,\"errmsg\":\"rejected\",\"data\":null}",
            StakeRootId));

        var mismatched = Snapshot(stakeRootId: RewardRootId);
        Assert.Throws<RelaySnapshotException>(() =>
            RelaySnapshotEnvelope.Parse(Envelope(mismatched), StakeRootId));

        var falseOdds = Snapshot(stageOneUpgrade: 99);
        Assert.Throws<RelaySnapshotException>(() =>
            RelaySnapshotEnvelope.Parse(Envelope(falseOdds), StakeRootId));
    }

    [Fact]
    public void Candidate_catalog_identity_accepts_server_prices_but_rejects_identity_changes()
    {
        var local = Reward("upgrade", RewardRarity.Uncommon, "new-root");
        var candidate = new RelayCandidate
        {
            RewardId = local.Id,
            DisplayName = local.DisplayName,
            Rarity = local.Rarity.ToString(),
            HandbookValue = 123_456
        };

        RelayCandidateCatalogValidator.Validate([candidate], [local]);

        var altered = new RelayCandidate
        {
            RewardId = local.Id,
            DisplayName = "Altered",
            Rarity = local.Rarity.ToString(),
            HandbookValue = candidate.HandbookValue
        };
        Assert.Throws<RelaySnapshotException>(() =>
            RelayCandidateCatalogValidator.Validate([altered], [local]));
    }

    [Theory]
    [InlineData(RelayOutcome.RarityUpgrade, false, RelayClientDestination.NextDecision)]
    [InlineData(RelayOutcome.RarityUpgrade, true, RelayClientDestination.Terminal)]
    [InlineData(RelayOutcome.SameRaritySidegrade, true, RelayClientDestination.Terminal)]
    [InlineData(RelayOutcome.Confiscated, true, RelayClientDestination.Terminal)]
    [InlineData(RelayOutcome.Secured, true, RelayClientDestination.Terminal)]
    public void Only_a_nonterminal_rarity_upgrade_can_reach_another_decision(
        RelayOutcome outcome,
        bool terminal,
        RelayClientDestination expected)
    {
        Assert.Equal(expected, RelayTerminalPolicy.After(outcome, terminal));
    }

    [Theory]
    [InlineData("Secure", RelayPendingRecovery.ResumeSecure)]
    [InlineData("Relay", RelayPendingRecovery.ResumeRelay)]
    public void Pending_snapshot_can_only_resume_the_exact_persisted_action(
        string pendingAction,
        RelayPendingRecovery expected)
    {
        var snapshot = Snapshot(settlementPending: true, pendingAction: pendingAction);

        Assert.Equal(expected, RelaySnapshotRecovery.Decide(snapshot));
    }

    [Fact]
    public void Pending_discovery_requires_an_explicit_authenticated_pending_snapshot_field()
    {
        var none = RelayPendingDiscoveryEnvelope.Parse(PendingEnvelope(null));
        var pending = RelayPendingDiscoveryEnvelope.Parse(PendingEnvelope(
            Snapshot(settlementPending: true, pendingAction: "Relay")));

        Assert.Null(none.PendingSnapshot);
        Assert.Equal(
            RelayPendingRecovery.ResumeRelay,
            RelaySnapshotRecovery.Decide(pending.PendingSnapshot!));
        Assert.Throws<RelaySnapshotException>(() =>
            RelayPendingDiscoveryEnvelope.Parse(
                "{\"err\":0,\"errmsg\":null,\"data\":{}}"));
        Assert.Throws<RelaySnapshotException>(() =>
            RelayPendingDiscoveryEnvelope.Parse(PendingEnvelope(Snapshot())));
    }

    [Fact]
    public void Restart_recovery_secure_requires_the_exact_stake_tree_to_remain()
    {
        var current = Inventory(
            Node(StakeRootId, "old-root"),
            Node("dddddddddddddddddddddddd", "old-mod", StakeRootId));
        var catalog = new[]
        {
            Reward("current", RewardRarity.ScavGrade, "old-root", "old-mod")
        };
        var pending = Snapshot(settlementPending: true, pendingAction: "Secure");

        var recovered = RelayInventoryReconciler.ReconcileRecovered(
            "profile",
            current,
            catalog,
            pending,
            SecureReceipt());

        Assert.Equal(StakeRootId, recovered!.RootItemId);
        Assert.Equal("current", recovered.Reward.Id);
        Assert.Throws<InventorySnapshotException>(() =>
            RelayInventoryReconciler.ReconcileRecovered(
                "profile",
                Inventory(),
                catalog,
                pending,
                SecureReceipt()));
    }

    [Fact]
    public void Restart_recovery_upgrade_accepts_only_receipt_identified_catalog_output()
    {
        var current = Inventory(
            Node(RewardRootId, "new-root"),
            Node("ffffffffffffffffffffffff", "new-mod", RewardRootId));
        var catalog = new[]
        {
            Reward("current", RewardRarity.ScavGrade, "old-root"),
            Reward("upgrade", RewardRarity.Uncommon, "new-root", "new-mod")
        };
        var pending = Snapshot(settlementPending: true, pendingAction: "Relay");
        var receipt = Receipt(
            RelayOutcome.RarityUpgrade,
            "upgrade",
            RewardRootId,
            RewardRarity.Uncommon);

        var recovered = RelayInventoryReconciler.ReconcileRecovered(
            "profile",
            current,
            catalog,
            pending,
            receipt);

        Assert.Equal("upgrade", recovered!.Reward.Id);
        Assert.Throws<InventorySnapshotException>(() =>
            RelayInventoryReconciler.ReconcileRecovered(
                "profile",
                Inventory(
                    Node(StakeRootId, "old-root"),
                    Node(RewardRootId, "new-root"),
                    Node("ffffffffffffffffffffffff", "new-mod", RewardRootId)),
                catalog,
                pending,
                receipt));
    }

    [Fact]
    public void Restart_recovery_confiscation_requires_absent_stake_and_no_output()
    {
        var pending = Snapshot(settlementPending: true, pendingAction: "Relay");
        var receipt = Receipt(RelayOutcome.Confiscated, null, null, null);

        Assert.Null(RelayInventoryReconciler.ReconcileRecovered(
            "profile",
            Inventory(Node("eeeeeeeeeeeeeeeeeeeeeeee", "unrelated")),
            Array.Empty<ValidatedReward>(),
            pending,
            receipt));
        Assert.Throws<InventorySnapshotException>(() =>
            RelayInventoryReconciler.ReconcileRecovered(
                "profile",
                Inventory(Node(StakeRootId, "old-root")),
                Array.Empty<ValidatedReward>(),
                pending,
                receipt));
    }

    [Fact]
    public void Cosmetic_preview_origin_is_structurally_denied_economic_dispatch()
    {
        Assert.False(RelayInteractionPolicy.CanDispatch(RelayInteractionOrigin.CosmeticPreview));
        Assert.True(RelayInteractionPolicy.CanDispatch(RelayInteractionOrigin.RealOpening));
    }

    [Fact]
    public void HasKey_is_false_when_no_BR12_Relay_Key_is_present_anywhere_in_the_local_stash()
    {
        var snapshot = Inventory(
            Node(StakeRootId, "not-a-key"),
            Node("eeeeeeeeeeeeeeeeeeeeeeee", "unrelated"));

        Assert.False(RelayInventoryBaseline.HasKey(snapshot));
    }

    [Fact]
    public void HasKey_is_true_when_a_BR12_Relay_Key_is_present_anywhere_in_the_local_stash()
    {
        var snapshot = Inventory(
            Node(StakeRootId, "not-a-key"),
            Node(KeyId, ModConstants.KeyTemplateId));

        Assert.True(RelayInventoryBaseline.HasKey(snapshot));
    }

    [Theory]
    [InlineData(CosmeticOutcomeSelection.RelayUpgradePreview, CosmeticRelayPreviewKind.Upgrade)]
    [InlineData(CosmeticOutcomeSelection.RelaySidegradePreview, CosmeticRelayPreviewKind.Sidegrade)]
    [InlineData(CosmeticOutcomeSelection.RelayConfiscationPreview, CosmeticRelayPreviewKind.Confiscation)]
    public void Every_cosmetic_relay_path_produces_only_a_presentation_plan_with_no_dispatch_authority(
        CosmeticOutcomeSelection selection,
        CosmeticRelayPreviewKind expected)
    {
        Assert.True(CosmeticPreviewPlanner.TryCreateRelay(selection, out var plan));
        Assert.NotNull(plan);
        Assert.Equal(expected, plan!.Kind);
        Assert.Equal(RelayInteractionOrigin.CosmeticPreview, plan.Origin);
        Assert.False(RelayInteractionPolicy.CanDispatch(plan.Origin));
        Assert.False(RelayInteractionPolicy.CanFetchSnapshot(plan.Origin));
    }

    [Fact]
    public void Upgrade_reconciliation_requires_exact_stake_tree_key_and_receipt_verified_output()
    {
        var before = Inventory(
            Node(StakeRootId, "old-root"),
            Node("dddddddddddddddddddddddd", "old-mod", StakeRootId),
            Node(KeyId, ModConstants.KeyTemplateId),
            Node("eeeeeeeeeeeeeeeeeeeeeeee", "unrelated"));
        var baseline = RelayInventoryBaseline.Capture(before, StakeRootId);
        var after = Inventory(
            Node(RewardRootId, "new-root"),
            Node("ffffffffffffffffffffffff", "new-mod", RewardRootId),
            Node("eeeeeeeeeeeeeeeeeeeeeeee", "unrelated"));
        var catalog = new[]
        {
            Reward("upgrade", RewardRarity.Uncommon, "new-root", "new-mod"),
            Reward("sidegrade", RewardRarity.ScavGrade, "other-root")
        };
        var snapshot = Snapshot();
        var receipt = Receipt(RelayOutcome.RarityUpgrade, "upgrade", RewardRootId, RewardRarity.Uncommon);

        var result = RelayInventoryReconciler.Reconcile(baseline, after, catalog, snapshot, receipt);

        Assert.Equal("upgrade", result!.Reward.Id);
        Assert.Equal(RewardRootId, result.RootItemId);
    }

    [Fact]
    public void Confiscation_reconciliation_accepts_no_output_and_rejects_any_unrelated_change()
    {
        var before = Inventory(
            Node(StakeRootId, "old-root"),
            Node(KeyId, ModConstants.KeyTemplateId),
            Node("eeeeeeeeeeeeeeeeeeeeeeee", "unrelated"));
        var baseline = RelayInventoryBaseline.Capture(before, StakeRootId);
        var after = Inventory(Node("eeeeeeeeeeeeeeeeeeeeeeee", "unrelated"));
        var receipt = Receipt(RelayOutcome.Confiscated, null, null, null);

        Assert.Null(RelayInventoryReconciler.Reconcile(
            baseline,
            after,
            Array.Empty<ValidatedReward>(),
            Snapshot(),
            receipt));

        var changed = Inventory(Node("eeeeeeeeeeeeeeeeeeeeeeee", "changed"));
        Assert.Throws<InventorySnapshotException>(() => RelayInventoryReconciler.Reconcile(
            baseline,
            changed,
            Array.Empty<ValidatedReward>(),
            Snapshot(),
            receipt));
    }

    private static RelaySnapshot Snapshot(
        string stakeRootId = StakeRootId,
        int stageOneUpgrade = 55,
        bool settlementPending = false,
        string? pendingAction = null) => new()
        {
            Status = new RelayStatus
            {
                StakeRootId = stakeRootId,
                RewardId = "current",
                Rarity = RewardRarity.ScavGrade.ToString(),
                RarityLadderVersion = RarityLadderVersion.FiveTier.ToString(),
                Stage = 1,
                RecoveryMeter = 1,
                RecoveryMeterMaximum = 3,
                RelayEligible = !settlementPending,
                SettlementPending = settlementPending,
                PendingAction = pendingAction,
                Terminal = settlementPending
            },
            PublishedLadder =
        [
            Stage(1, stageOneUpgrade, 30, 15),
            Stage(2, 45, 25, 30),
            Stage(3, 35, 20, 45)
        ],
            UpgradeCandidates = settlementPending
                ? Array.Empty<RelayCandidate>()
                :
        [
            new RelayCandidate
            {
                RewardId = "upgrade",
                DisplayName = "Upgrade",
                Rarity = RewardRarity.Uncommon.ToString(),
                HandbookValue = 100
            }
        ],
            SidegradeCandidates = settlementPending
                ? Array.Empty<RelayCandidate>()
                :
        [
            new RelayCandidate
            {
                RewardId = "sidegrade",
                DisplayName = "Sidegrade",
                Rarity = RewardRarity.ScavGrade.ToString(),
                HandbookValue = 50
            }
        ]
        };

    private static RelayReceipt SecureReceipt() => new()
    {
        Action = "Secure",
        Outcome = RelayOutcome.Secured.ToString(),
        StakeRootId = StakeRootId,
        Stage = 1,
        PreviousRewardId = "current",
        PreviousRarity = RewardRarity.ScavGrade.ToString(),
        RarityLadderVersion = RarityLadderVersion.FiveTier.ToString(),
        RecoveryMeter = 1,
        RecoveryMeterMaximum = 3,
        Terminal = true
    };

    private static RelayPublishedStage Stage(int stage, int upgrade, int sidegrade, int confiscate) => new()
    {
        Stage = stage,
        UpgradePercent = upgrade,
        SidegradePercent = sidegrade,
        ConfiscatePercent = confiscate
    };

    private static RelayReceipt Receipt(
        RelayOutcome outcome,
        string? rewardId,
        string? rewardRootId,
        RewardRarity? rarity) => new()
        {
            Action = "Relay",
            Outcome = outcome.ToString(),
            StakeRootId = StakeRootId,
            Stage = 1,
            PreviousRewardId = "current",
            PreviousRarity = RewardRarity.ScavGrade.ToString(),
            RarityLadderVersion = RarityLadderVersion.FiveTier.ToString(),
            RewardId = rewardId,
            RewardRootId = rewardRootId,
            Rarity = rarity?.ToString(),
            RecoveryMeter = outcome == RelayOutcome.Confiscated ? 2 : 1,
            RecoveryMeterMaximum = 3,
            Terminal = outcome != RelayOutcome.RarityUpgrade
        };

    private static string Envelope(RelaySnapshot snapshot) => JsonConvert.SerializeObject(new
    {
        err = 0,
        errmsg = (string?)null,
        data = snapshot
    });

    private static string PendingEnvelope(RelaySnapshot? snapshot) => JsonConvert.SerializeObject(new
    {
        err = 0,
        errmsg = (string?)null,
        data = new
        {
            pendingSnapshot = snapshot
        }
    });

    private static InventorySnapshot Inventory(params InventorySnapshotNode[] nodes) =>
        new("profile", nodes);

    private static InventorySnapshotNode Node(string id, string template, string? parent = null) =>
        new(id, template, parent, 1);

    private static ValidatedReward Reward(
        string id,
        RewardRarity rarity,
        params string[] templates)
    {
        var definitions = RewardCatalog.Create([
            new RewardDefinition(id, id, templates[0], $"preset-{id}", rarity, 1d)
        ]);
        return Assert.Single(definitions.Validate(new PresetResolver(templates)));
    }

    private sealed class PresetResolver(string[] templates) : IRewardPresetResolver
    {
        public RewardPresetTree? Resolve(string presetId) => new(
            templates.Select((template, index) => new RewardPresetItem(
                index == 0 ? "root" : $"child-{index}",
                template,
                index == 0 ? null : "root")));
    }
}
