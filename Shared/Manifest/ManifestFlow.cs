using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;

namespace ContrabandCases.Shared.Manifest;

public enum ManifestPhase
{
    TicketPrepared,
    Offer1,
    Offer2,
    Entitlement,
    ClaimPrepared,
    RelayPrepared,
    RewardOwed,
    Granted,
    Confiscated,
    Forfeited
}

public enum ManifestOfferDecision
{
    Lock,
    Burn,
    Choose
}

public enum ManifestRelayResult
{
    Upgrade,
    Sidegrade,
    Confiscated
}

public sealed class ManifestFlowState
{
    internal ManifestFlowState(
        ManifestPhase phase,
        int currentOrdinal,
        int? lockedOrdinal,
        int relayStage,
        bool relayTerminal)
    {
        if (!Enum.IsDefined(typeof(ManifestPhase), phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }
        if (currentOrdinal is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(currentOrdinal));
        }
        if (relayStage is < 1 or > RelayRules.MaximumStage)
        {
            throw new ArgumentOutOfRangeException(nameof(relayStage));
        }

        var unlockedPhase = phase is ManifestPhase.TicketPrepared or ManifestPhase.Offer1 or ManifestPhase.Offer2;
        if (unlockedPhase && lockedOrdinal is not null ||
            !unlockedPhase && phase != ManifestPhase.Forfeited && lockedOrdinal is null)
        {
            throw new ArgumentException("Only unrevealed offer states may omit a locked ordinal.", nameof(lockedOrdinal));
        }
        if (phase == ManifestPhase.Offer1 && currentOrdinal != 1 ||
            phase == ManifestPhase.Offer2 && currentOrdinal != 2)
        {
            throw new ArgumentException("Offer phase and ordinal contradict each other.", nameof(currentOrdinal));
        }
        if (lockedOrdinal is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(lockedOrdinal));
        }
        if (lockedOrdinal is not null && currentOrdinal != lockedOrdinal)
        {
            throw new ArgumentException("The current ordinal must identify the locked offer.", nameof(currentOrdinal));
        }

        Phase = phase;
        CurrentOrdinal = currentOrdinal;
        LockedOrdinal = lockedOrdinal;
        RelayStage = relayStage;
        RelayTerminal = relayTerminal;
    }

    public ManifestPhase Phase { get; }

    public int CurrentOrdinal { get; }

    public int? LockedOrdinal { get; }

    public int RelayStage { get; }

    public bool RelayTerminal { get; }

    public bool IsTerminal => Phase is ManifestPhase.Granted or
        ManifestPhase.Confiscated or ManifestPhase.Forfeited;

    public static ManifestFlowState PrepareTicket() =>
        new(ManifestPhase.TicketPrepared, 1, null, 1, false);

    public static ManifestFlowState Restore(
        ManifestPhase phase,
        int currentOrdinal,
        int? lockedOrdinal,
        int relayStage,
        bool relayTerminal)
    {
        var state = new ManifestFlowState(
            phase,
            currentOrdinal,
            lockedOrdinal,
            relayStage,
            relayTerminal);
        var reachable = phase switch
        {
            ManifestPhase.TicketPrepared or ManifestPhase.Offer1 =>
                currentOrdinal == 1 && relayStage == 1 && !relayTerminal,
            ManifestPhase.Offer2 =>
                currentOrdinal == 2 && relayStage == 1 && !relayTerminal,
            ManifestPhase.Entitlement or
            ManifestPhase.ClaimPrepared or
            ManifestPhase.RewardOwed => true,
            ManifestPhase.RelayPrepared => !relayTerminal,
            ManifestPhase.Granted or ManifestPhase.Confiscated => relayTerminal,
            ManifestPhase.Forfeited =>
                relayTerminal &&
                (lockedOrdinal is not null ||
                    currentOrdinal is 1 or 2 && relayStage == 1),
            _ => false
        };

        if (!reachable)
        {
            throw new ArgumentException(
                "The persisted manifest state is not reachable.",
                nameof(phase));
        }

        return state;
    }

    public static ManifestFlowState ActivateTicket(ManifestFlowState state, bool singlePayout = false)
    {
        state = ManifestStateMachine.RequireState(state);
        if (state.Phase != ManifestPhase.TicketPrepared)
        {
            throw new InvalidOperationException($"Cannot activate a ticket while the manifest is in phase '{state.Phase}'.");
        }

        return singlePayout
            ? new ManifestFlowState(ManifestPhase.Entitlement, 1, 1, 1, false)
            : new ManifestFlowState(ManifestPhase.Offer1, 1, null, 1, false);
    }

}

public static class ManifestStateMachine
{
    public static ManifestFlowState ChoosePremiumOffer(ManifestFlowState state, int ordinal)
    {
        RequirePhase(state, ManifestPhase.Offer1, "choose a premium package");
        if (ordinal is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(ordinal));
        return Locked(ordinal, state.RelayStage, relayTerminal: false);
    }

    public static ManifestFlowState DecideOffer(
        ManifestFlowState state,
        ManifestOfferDecision decision)
    {
        state = RequireState(state);
        if (decision is not (ManifestOfferDecision.Lock or ManifestOfferDecision.Burn))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }
        if (state.Phase is not (ManifestPhase.Offer1 or ManifestPhase.Offer2))
        {
            throw InvalidTransition(state, decision.ToString());
        }

        if (decision == ManifestOfferDecision.Lock)
        {
            return Locked(state.CurrentOrdinal, state.RelayStage, relayTerminal: false);
        }

        return state.Phase == ManifestPhase.Offer1
            ? new ManifestFlowState(ManifestPhase.Offer2, 2, null, state.RelayStage, false)
            : Locked(3, state.RelayStage, relayTerminal: false);
    }

    public static ManifestFlowState PrepareClaim(ManifestFlowState state)
    {
        RequirePhase(state, ManifestPhase.Entitlement, "prepare Claim");
        return Copy(state, ManifestPhase.ClaimPrepared);
    }

    public static ManifestFlowState MarkRewardOwed(ManifestFlowState state)
    {
        RequirePhase(state, ManifestPhase.ClaimPrepared, "mark RewardOwed");
        return Copy(state, ManifestPhase.RewardOwed);
    }

    public static ManifestFlowState CompleteClaim(ManifestFlowState state)
    {
        state = RequireState(state);
        if (state.Phase is not (ManifestPhase.ClaimPrepared or ManifestPhase.RewardOwed))
        {
            throw InvalidTransition(state, "complete Claim");
        }

        return Copy(state, ManifestPhase.Granted, relayTerminal: true);
    }

    public static ManifestFlowState PrepareRelay(ManifestFlowState state, bool relayEligible)
    {
        RequirePhase(state, ManifestPhase.Entitlement, "prepare Relay");
        if (!relayEligible || state.RelayTerminal)
        {
            throw new InvalidOperationException("The current entitlement is not Relay-eligible.");
        }

        return Copy(state, ManifestPhase.RelayPrepared);
    }

    public static ManifestFlowState CompleteRelay(
        ManifestFlowState state,
        ManifestRelayResult result,
        RewardRarity? outputRarity)
    {
        RequirePhase(state, ManifestPhase.RelayPrepared, "complete Relay");
        if (!Enum.IsDefined(typeof(ManifestRelayResult), result))
        {
            throw new ArgumentOutOfRangeException(nameof(result));
        }

        ValidateRelayOutput(result, outputRarity);

        return result switch
        {
            ManifestRelayResult.Confiscated => Copy(state, ManifestPhase.Confiscated, relayTerminal: true),
            ManifestRelayResult.Sidegrade => Copy(state, ManifestPhase.Entitlement, relayTerminal: true),
            ManifestRelayResult.Upgrade => CompleteUpgrade(state, outputRarity!.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
    }

    public static ManifestFlowState ForfeitMissingContent(
        ManifestFlowState state,
        bool missingContentBlocked)
    {
        state = RequireState(state);
        if (!missingContentBlocked)
        {
            throw new InvalidOperationException("A manifest can be forfeited only while content is missing.");
        }
        if (state.Phase is not (ManifestPhase.Offer1 or ManifestPhase.Offer2 or ManifestPhase.Entitlement))
        {
            throw InvalidTransition(state, "forfeit missing content");
        }

        return new ManifestFlowState(
            ManifestPhase.Forfeited,
            state.CurrentOrdinal,
            state.LockedOrdinal,
            state.RelayStage,
            relayTerminal: true);
    }

    private static ManifestFlowState CompleteUpgrade(
        ManifestFlowState state,
        RewardRarity outputRarity)
    {
        var maximumStageReached = state.RelayStage == RelayRules.MaximumStage;
        var nextStage = maximumStageReached
            ? state.RelayStage
            : checked(state.RelayStage + 1);
        return new ManifestFlowState(
            ManifestPhase.Entitlement,
            state.CurrentOrdinal,
            state.LockedOrdinal,
            nextStage,
            outputRarity == RewardRarity.BlackLabel || maximumStageReached);
    }

    private static void ValidateRelayOutput(
        ManifestRelayResult result,
        RewardRarity? outputRarity)
    {
        if (result == ManifestRelayResult.Confiscated)
        {
            if (outputRarity is not null)
            {
                throw new ArgumentException(
                    "A confiscated Relay result cannot have an output rarity.",
                    nameof(outputRarity));
            }

            return;
        }

        if (outputRarity is null || !Enum.IsDefined(typeof(RewardRarity), outputRarity.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputRarity),
                outputRarity,
                "A non-confiscated Relay result requires a recognized output rarity.");
        }
    }

    private static ManifestFlowState Locked(int ordinal, int relayStage, bool relayTerminal) =>
        new(ManifestPhase.Entitlement, ordinal, ordinal, relayStage, relayTerminal);

    private static ManifestFlowState Copy(
        ManifestFlowState state,
        ManifestPhase phase,
        bool? relayTerminal = null) =>
        new(
            phase,
            state.CurrentOrdinal,
            state.LockedOrdinal,
            state.RelayStage,
            relayTerminal ?? state.RelayTerminal);

    private static void RequirePhase(
        ManifestFlowState? state,
        ManifestPhase required,
        string action)
    {
        state = RequireState(state);
        if (state.Phase != required)
        {
            throw InvalidTransition(state, action);
        }
    }

    private static InvalidOperationException InvalidTransition(
        ManifestFlowState state,
        string action) =>
        new($"Cannot {action} while the manifest is in phase '{state.Phase}'.");

    internal static ManifestFlowState RequireState(ManifestFlowState? state) =>
        state ?? throw new ArgumentNullException(nameof(state));
}
