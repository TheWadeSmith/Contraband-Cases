using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using ContrabandCases.Shared.Relay;
using Xunit;

namespace ContrabandCases.Tests.Manifest;

public sealed class ManifestStateMachineTests
{
    [Fact]
    public void Ticket_must_be_prepared_before_offer_one_becomes_active()
    {
        var prepared = ManifestFlowState.PrepareTicket();
        Assert.Equal(ManifestPhase.TicketPrepared, prepared.Phase);
        Assert.Null(prepared.LockedOrdinal);

        var state = ManifestFlowState.ActivateTicket(prepared);

        Assert.Equal(ManifestPhase.Offer1, state.Phase);
        Assert.Equal(1, state.CurrentOrdinal);
        Assert.Null(state.LockedOrdinal);
        Assert.Equal(1, state.RelayStage);
        Assert.False(state.RelayTerminal);
        Assert.False(state.IsTerminal);

        Assert.Throws<InvalidOperationException>(() =>
            ManifestStateMachine.DecideOffer(prepared, ManifestOfferDecision.Lock));
        Assert.Throws<InvalidOperationException>(() => ManifestFlowState.ActivateTicket(state));
    }

    [Fact]
    public void Lock_stops_on_the_current_offer()
    {
        var first = ManifestStateMachine.DecideOffer(
            ActiveTicket(),
            ManifestOfferDecision.Lock);
        var second = ManifestStateMachine.DecideOffer(
            ManifestStateMachine.DecideOffer(
                ActiveTicket(),
                ManifestOfferDecision.Burn),
            ManifestOfferDecision.Lock);

        AssertEntitlement(first, 1);
        AssertEntitlement(second, 2);
    }

    [Fact]
    public void Second_burn_forces_and_locks_offer_three()
    {
        var offerTwo = ManifestStateMachine.DecideOffer(
            ActiveTicket(),
            ManifestOfferDecision.Burn);

        Assert.Equal(ManifestPhase.Offer2, offerTwo.Phase);
        Assert.Equal(2, offerTwo.CurrentOrdinal);
        Assert.Null(offerTwo.LockedOrdinal);

        var forced = ManifestStateMachine.DecideOffer(offerTwo, ManifestOfferDecision.Burn);
        AssertEntitlement(forced, 3);
        Assert.Throws<InvalidOperationException>(() =>
            ManifestStateMachine.DecideOffer(forced, ManifestOfferDecision.Burn));
    }

    [Fact]
    public void Claim_preparation_and_recovery_have_explicit_phases()
    {
        var entitlement = LockFirst();
        var prepared = ManifestStateMachine.PrepareClaim(entitlement);
        var owed = ManifestStateMachine.MarkRewardOwed(prepared);

        Assert.Equal(ManifestPhase.ClaimPrepared, prepared.Phase);
        Assert.Equal(ManifestPhase.RewardOwed, owed.Phase);
        Assert.Equal(ManifestPhase.Granted, ManifestStateMachine.CompleteClaim(prepared).Phase);
        Assert.Equal(ManifestPhase.Granted, ManifestStateMachine.CompleteClaim(owed).Phase);
        Assert.True(ManifestStateMachine.CompleteClaim(owed).IsTerminal);
    }

    [Fact]
    public void Sidegrade_is_a_terminal_entitlement_that_can_still_be_claimed()
    {
        var prepared = ManifestStateMachine.PrepareRelay(LockFirst(), relayEligible: true);
        var sidegrade = ManifestStateMachine.CompleteRelay(
            prepared,
            ManifestRelayResult.Sidegrade,
            RewardRarity.ScavGrade);

        Assert.Equal(ManifestPhase.Entitlement, sidegrade.Phase);
        Assert.True(sidegrade.RelayTerminal);
        Assert.Throws<InvalidOperationException>(() =>
            ManifestStateMachine.PrepareRelay(sidegrade, relayEligible: true));
        Assert.Equal(ManifestPhase.ClaimPrepared, ManifestStateMachine.PrepareClaim(sidegrade).Phase);
    }

    [Fact]
    public void Upgrade_advances_until_maximum_stage_then_becomes_terminal()
    {
        var state = LockFirst();
        for (var expectedStage = 2; expectedStage <= RelayRules.MaximumStage; expectedStage++)
        {
            state = ManifestStateMachine.CompleteRelay(
                ManifestStateMachine.PrepareRelay(state, relayEligible: true),
                ManifestRelayResult.Upgrade,
                RewardRarity.Contractor);
            Assert.Equal(expectedStage, state.RelayStage);
            Assert.False(state.RelayTerminal);
        }

        state = ManifestStateMachine.CompleteRelay(
            ManifestStateMachine.PrepareRelay(state, relayEligible: true),
            ManifestRelayResult.Upgrade,
            RewardRarity.Contractor);

        Assert.Equal(RelayRules.MaximumStage, state.RelayStage);
        Assert.True(state.RelayTerminal);
        Assert.Throws<InvalidOperationException>(() =>
            ManifestStateMachine.PrepareRelay(state, relayEligible: true));
    }

    [Fact]
    public void Upgrade_to_maximum_grade_is_terminal_immediately()
    {
        var upgraded = ManifestStateMachine.CompleteRelay(
            ManifestStateMachine.PrepareRelay(LockFirst(), relayEligible: true),
            ManifestRelayResult.Upgrade,
            RewardRarity.BlackLabel);

        Assert.True(upgraded.RelayTerminal);
        Assert.Equal(ManifestPhase.Entitlement, upgraded.Phase);
    }

    [Fact]
    public void Confiscation_is_terminal()
    {
        var confiscated = ManifestStateMachine.CompleteRelay(
            ManifestStateMachine.PrepareRelay(LockFirst(), relayEligible: true),
            ManifestRelayResult.Confiscated,
            outputRarity: null);

        Assert.Equal(ManifestPhase.Confiscated, confiscated.Phase);
        Assert.True(confiscated.IsTerminal);
    }

    [Fact]
    public void Relay_requires_an_eligible_nonterminal_entitlement()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ManifestStateMachine.PrepareRelay(LockFirst(), relayEligible: false));
        Assert.Throws<InvalidOperationException>(() =>
            ManifestStateMachine.PrepareRelay(ActiveTicket(), relayEligible: true));
    }

    [Theory]
    [InlineData(ManifestOfferDecision.Lock)]
    [InlineData(ManifestOfferDecision.Burn)]
    public void Offer_decisions_reject_unknown_or_settled_phases(ManifestOfferDecision decision)
    {
        var granted = ManifestStateMachine.CompleteClaim(
            ManifestStateMachine.PrepareClaim(LockFirst()));

        Assert.Throws<InvalidOperationException>(() =>
            ManifestStateMachine.DecideOffer(granted, decision));
    }

    [Fact]
    public void Missing_content_forfeit_is_irreversible_and_never_available_during_prepared_mutation()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ManifestStateMachine.ForfeitMissingContent(ActiveTicket(), missingContentBlocked: false));

        var fromOffer = ManifestStateMachine.ForfeitMissingContent(
            ActiveTicket(),
            missingContentBlocked: true);
        Assert.Equal(ManifestPhase.Forfeited, fromOffer.Phase);
        Assert.True(fromOffer.IsTerminal);
        Assert.Null(fromOffer.LockedOrdinal);

        var preparedClaim = ManifestStateMachine.PrepareClaim(LockFirst());
        Assert.Throws<InvalidOperationException>(() =>
            ManifestStateMachine.ForfeitMissingContent(preparedClaim, missingContentBlocked: true));
        Assert.Throws<InvalidOperationException>(() =>
            ManifestStateMachine.PrepareClaim(fromOffer));
    }

    [Fact]
    public void Relay_output_rarity_is_required_and_validated_by_result()
    {
        var prepared = ManifestStateMachine.PrepareRelay(LockFirst(), relayEligible: true);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ManifestStateMachine.CompleteRelay(
                prepared,
                ManifestRelayResult.Upgrade,
                outputRarity: null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ManifestStateMachine.CompleteRelay(
                prepared,
                ManifestRelayResult.Sidegrade,
                (RewardRarity)999));
        Assert.Throws<ArgumentException>(() =>
            ManifestStateMachine.CompleteRelay(
                prepared,
                ManifestRelayResult.Confiscated,
                RewardRarity.ScavGrade));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ManifestStateMachine.CompleteRelay(
                prepared,
                (ManifestRelayResult)999,
                outputRarity: null));
    }

    [Fact]
    public void Every_action_rejects_every_illegal_phase()
    {
        AssertLegalOnly(
            [ManifestPhase.TicketPrepared],
            state => ManifestFlowState.ActivateTicket(state));
        AssertLegalOnly(
            [ManifestPhase.Offer1, ManifestPhase.Offer2],
            state => ManifestStateMachine.DecideOffer(state, ManifestOfferDecision.Lock));
        AssertLegalOnly(
            [ManifestPhase.Entitlement],
            state => ManifestStateMachine.PrepareClaim(state));
        AssertLegalOnly(
            [ManifestPhase.ClaimPrepared],
            state => ManifestStateMachine.MarkRewardOwed(state));
        AssertLegalOnly(
            [ManifestPhase.ClaimPrepared, ManifestPhase.RewardOwed],
            state => ManifestStateMachine.CompleteClaim(state));
        AssertLegalOnly(
            [ManifestPhase.Entitlement],
            state => ManifestStateMachine.PrepareRelay(state, relayEligible: true));
        AssertLegalOnly(
            [ManifestPhase.RelayPrepared],
            state => ManifestStateMachine.CompleteRelay(
                state,
                ManifestRelayResult.Confiscated,
                outputRarity: null));
        AssertLegalOnly(
            [ManifestPhase.Offer1, ManifestPhase.Offer2, ManifestPhase.Entitlement],
            state => ManifestStateMachine.ForfeitMissingContent(
                state,
                missingContentBlocked: true));
    }

    [Fact]
    public void Every_action_rejects_null_state_and_unknown_decisions()
    {
        Assert.Throws<ArgumentNullException>(() => ManifestFlowState.ActivateTicket(null!));
        Assert.Throws<ArgumentNullException>(() =>
            ManifestStateMachine.DecideOffer(null!, ManifestOfferDecision.Lock));
        Assert.Throws<ArgumentNullException>(() => ManifestStateMachine.PrepareClaim(null!));
        Assert.Throws<ArgumentNullException>(() => ManifestStateMachine.MarkRewardOwed(null!));
        Assert.Throws<ArgumentNullException>(() => ManifestStateMachine.CompleteClaim(null!));
        Assert.Throws<ArgumentNullException>(() =>
            ManifestStateMachine.PrepareRelay(null!, relayEligible: true));
        Assert.Throws<ArgumentNullException>(() =>
            ManifestStateMachine.CompleteRelay(
                null!,
                ManifestRelayResult.Confiscated,
                outputRarity: null));
        Assert.Throws<ArgumentNullException>(() =>
            ManifestStateMachine.ForfeitMissingContent(null!, missingContentBlocked: true));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ManifestStateMachine.DecideOffer(ActiveTicket(), (ManifestOfferDecision)999));
    }

    [Fact]
    public void Restore_accepts_every_state_reachable_from_the_transition_graph()
    {
        var reachable = ReachableStates();
        Assert.Equal(5 + 30 * RelayRules.MaximumStage, reachable.Count);

        foreach (var expected in reachable.Keys)
        {
            var restored = ManifestFlowState.Restore(
                expected.Phase,
                expected.CurrentOrdinal,
                expected.LockedOrdinal,
                expected.RelayStage,
                expected.RelayTerminal);

            Assert.Equal(expected, StateKey.From(restored));
        }
    }

    [Fact]
    public void Restore_rejects_every_unreachable_persisted_combination()
    {
        var reachable = ReachableStates();
        var phases = Enum.GetValues<ManifestPhase>().Append((ManifestPhase)999);
        int?[] lockedOrdinals = [null, 0, 1, 2, 3, 4];

        foreach (var phase in phases)
            foreach (var currentOrdinal in Enumerable.Range(0, 5))
                foreach (var lockedOrdinal in lockedOrdinals)
                    foreach (var relayStage in Enumerable.Range(0, RelayRules.MaximumStage + 2))
                        foreach (var relayTerminal in new[] { false, true })
                        {
                            var candidate = new StateKey(
                                phase,
                                currentOrdinal,
                                lockedOrdinal,
                                relayStage,
                                relayTerminal);
                            if (reachable.ContainsKey(candidate))
                            {
                                continue;
                            }

                            var error = Record.Exception(() => ManifestFlowState.Restore(
                                phase,
                                currentOrdinal,
                                lockedOrdinal,
                                relayStage,
                                relayTerminal));

                            Assert.True(
                                error is ArgumentException,
                                $"Expected persisted state {candidate} to be rejected, but got {error?.GetType().Name ?? "success"}.");
                        }
    }

    private static ManifestFlowState LockFirst() => ManifestStateMachine.DecideOffer(
        ActiveTicket(),
        ManifestOfferDecision.Lock);

    private static ManifestFlowState ActiveTicket() =>
        ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket());

    private static IReadOnlyDictionary<ManifestPhase, ManifestFlowState> EveryPhase()
    {
        var ticketPrepared = ManifestFlowState.PrepareTicket();
        var offer1 = ManifestFlowState.ActivateTicket(ticketPrepared);
        var offer2 = ManifestStateMachine.DecideOffer(offer1, ManifestOfferDecision.Burn);
        var entitlement = ManifestStateMachine.DecideOffer(offer1, ManifestOfferDecision.Lock);
        var claimPrepared = ManifestStateMachine.PrepareClaim(entitlement);
        var relayPrepared = ManifestStateMachine.PrepareRelay(entitlement, relayEligible: true);
        var rewardOwed = ManifestStateMachine.MarkRewardOwed(claimPrepared);

        return new Dictionary<ManifestPhase, ManifestFlowState>
        {
            [ManifestPhase.TicketPrepared] = ticketPrepared,
            [ManifestPhase.Offer1] = offer1,
            [ManifestPhase.Offer2] = offer2,
            [ManifestPhase.Entitlement] = entitlement,
            [ManifestPhase.ClaimPrepared] = claimPrepared,
            [ManifestPhase.RelayPrepared] = relayPrepared,
            [ManifestPhase.RewardOwed] = rewardOwed,
            [ManifestPhase.Granted] = ManifestStateMachine.CompleteClaim(claimPrepared),
            [ManifestPhase.Confiscated] = ManifestStateMachine.CompleteRelay(
                relayPrepared,
                ManifestRelayResult.Confiscated,
                outputRarity: null),
            [ManifestPhase.Forfeited] = ManifestStateMachine.ForfeitMissingContent(
                offer1,
                missingContentBlocked: true)
        };
    }

    private static IReadOnlyDictionary<StateKey, ManifestFlowState> ReachableStates()
    {
        var reachable = new Dictionary<StateKey, ManifestFlowState>();
        var pending = new Queue<ManifestFlowState>();

        Add(ManifestFlowState.PrepareTicket());
        while (pending.Count > 0)
        {
            foreach (var next in NextStates(pending.Dequeue()))
            {
                Add(next);
            }
        }

        return reachable;

        void Add(ManifestFlowState state)
        {
            if (reachable.TryAdd(StateKey.From(state), state))
            {
                pending.Enqueue(state);
            }
        }
    }

    private static IEnumerable<ManifestFlowState> NextStates(ManifestFlowState state)
    {
        switch (state.Phase)
        {
            case ManifestPhase.TicketPrepared:
                yield return ManifestFlowState.ActivateTicket(state);
                break;
            case ManifestPhase.Offer1:
            case ManifestPhase.Offer2:
                yield return ManifestStateMachine.DecideOffer(state, ManifestOfferDecision.Lock);
                yield return ManifestStateMachine.DecideOffer(state, ManifestOfferDecision.Burn);
                yield return ManifestStateMachine.ForfeitMissingContent(state, missingContentBlocked: true);
                break;
            case ManifestPhase.Entitlement:
                yield return ManifestStateMachine.PrepareClaim(state);
                yield return ManifestStateMachine.ForfeitMissingContent(state, missingContentBlocked: true);
                if (!state.RelayTerminal)
                {
                    yield return ManifestStateMachine.PrepareRelay(state, relayEligible: true);
                }
                break;
            case ManifestPhase.ClaimPrepared:
                yield return ManifestStateMachine.MarkRewardOwed(state);
                yield return ManifestStateMachine.CompleteClaim(state);
                break;
            case ManifestPhase.RelayPrepared:
                yield return ManifestStateMachine.CompleteRelay(
                    state,
                    ManifestRelayResult.Upgrade,
                    RewardRarity.Contractor);
                yield return ManifestStateMachine.CompleteRelay(
                    state,
                    ManifestRelayResult.Upgrade,
                    RewardRarity.BlackLabel);
                yield return ManifestStateMachine.CompleteRelay(
                    state,
                    ManifestRelayResult.Sidegrade,
                    RewardRarity.ScavGrade);
                yield return ManifestStateMachine.CompleteRelay(
                    state,
                    ManifestRelayResult.Confiscated,
                    outputRarity: null);
                break;
            case ManifestPhase.RewardOwed:
                yield return ManifestStateMachine.CompleteClaim(state);
                break;
        }
    }

    private static void AssertLegalOnly(
        ManifestPhase[] legalPhases,
        Func<ManifestFlowState, ManifestFlowState> action)
    {
        foreach (var pair in EveryPhase())
        {
            if (legalPhases.Contains(pair.Key))
            {
                action(pair.Value);
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => action(pair.Value));
            }
        }
    }

    private static void AssertEntitlement(ManifestFlowState state, int ordinal)
    {
        Assert.Equal(ManifestPhase.Entitlement, state.Phase);
        Assert.Equal(ordinal, state.CurrentOrdinal);
        Assert.Equal(ordinal, state.LockedOrdinal);
        Assert.False(state.RelayTerminal);
    }

    private readonly record struct StateKey(
        ManifestPhase Phase,
        int CurrentOrdinal,
        int? LockedOrdinal,
        int RelayStage,
        bool RelayTerminal)
    {
        public static StateKey From(ManifestFlowState state) => new(
            state.Phase,
            state.CurrentOrdinal,
            state.LockedOrdinal,
            state.RelayStage,
            state.RelayTerminal);
    }
}
