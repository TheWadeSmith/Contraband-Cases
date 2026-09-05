using ContrabandCases.Shared.Manifest;
using Xunit;

namespace ContrabandCases.Tests.Shared;

public sealed class ManifestOpeningTierTests
{
    [Fact]
    public void PublishedIntervals_HaveExactRareTierBoundaries()
    {
        var denominator = ManifestOpeningTierRules.DrawDenominator;
        var legendaryEnd = denominator * 20m / 10_000;
        var epicEnd = denominator * 170m / 10_000;
        Assert.Equal(ManifestOpeningTier.Legendary, ManifestOpeningTierRules.Select(0, true, true));
        Assert.Equal(ManifestOpeningTier.Legendary, ManifestOpeningTierRules.Select((long)legendaryEnd, true, true));
        Assert.Equal(ManifestOpeningTier.Epic, ManifestOpeningTierRules.Select((long)legendaryEnd + 1, true, true));
        Assert.Equal(ManifestOpeningTier.Epic, ManifestOpeningTierRules.Select((long)epicEnd, true, true));
        Assert.Equal(ManifestOpeningTier.Normal, ManifestOpeningTierRules.Select((long)epicEnd + 1, true, true));
        Assert.Equal(ManifestOpeningTier.Normal, ManifestOpeningTierRules.Select(denominator - 1, true, true));
    }

    [Fact]
    public void UnavailableTier_IsDisabledBeforeOpening_NotReassignedToAnotherPremiumTier()
    {
        Assert.Equal(ManifestOpeningTier.Normal, ManifestOpeningTierRules.Select(0, true, false));
        Assert.Equal(ManifestOpeningTier.Normal,
            ManifestOpeningTierRules.Select(ManifestOpeningTierRules.DrawDenominator / 100, false, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ManifestOpeningTierRules.Select(-1, true, true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ChoosePremium_IsOneTransitionWithoutBurning(int ordinal)
    {
        var state = ManifestFlowState.ActivateTicket(ManifestFlowState.PrepareTicket());
        var chosen = ManifestStateMachine.ChoosePremiumOffer(state, ordinal);
        Assert.Equal(ManifestPhase.Entitlement, chosen.Phase);
        Assert.Equal(ordinal, chosen.LockedOrdinal);
        Assert.Throws<InvalidOperationException>(() => ManifestStateMachine.ChoosePremiumOffer(chosen, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ManifestStateMachine.DecideOffer(state, ManifestOfferDecision.Choose));
    }
}
