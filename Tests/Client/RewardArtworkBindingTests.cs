using ContrabandCases.Client.Opening;
using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class RewardArtworkBindingTests
{
    [Fact]
    public void Contents_can_replace_seal_and_late_anchor_wins_without_failure_erasing_art()
    {
        var art = new RewardArtworkBinding("anchor", RewardRarity.Uncommon, ["content:a", "content:b"]);
        Assert.False(art.TryAccept("anchor", false));
        Assert.False(art.TryAccept("unrelated", true));
        Assert.True(art.TryAccept("content:a", true));
        Assert.False(art.TryAccept("content:b", true));
        Assert.True(art.TryAccept("anchor", true));
        Assert.False(art.TryAccept("content:b", true));
        Assert.False(art.TryAccept("anchor", false));
    }

    [Fact]
    public void Fresh_screen_starts_with_its_own_grade_and_no_previous_selection()
    {
        var previous = new RewardArtworkBinding("old", RewardRarity.BlackLabel, ["old-content"]);
        Assert.True(previous.TryAccept("old", true));
        var next = new RewardArtworkBinding("new", RewardRarity.ScavGrade, ["new-content"]);
        Assert.Equal(RewardRarity.ScavGrade, next.Grade);
        Assert.False(next.TryAccept("old", true));
        Assert.False(next.TryAccept("old-content", true));
        Assert.True(next.TryAccept("new-content", true));
    }
}
