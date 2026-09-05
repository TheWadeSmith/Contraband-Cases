using ContrabandCases.Shared.Roulette;
using Xunit;

namespace ContrabandCases.Tests.Roulette;

public sealed class RouletteStripPlannerTests
{
    [Fact]
    public void Plan_forces_the_committed_winner_at_the_landing_index()
    {
        var pool = new[] { "a", "b", "c" };
        var strip = RouletteStripPlanner.Plan(pool, "winner", 9, 4, 17);

        Assert.Equal(9, strip.Count);
        Assert.Equal("winner", strip[4]);
        Assert.All(strip.Where((_, index) => index != 4), tile => Assert.Contains(tile, pool));
    }

    [Fact]
    public void Plan_is_deterministic_for_the_same_seed()
    {
        var pool = new[] { "a", "b", "c" };
        Assert.Equal(
            RouletteStripPlanner.Plan(pool, "winner", 9, 4, 17),
            RouletteStripPlanner.Plan(pool, "winner", 9, 4, 17));
    }

    [Fact]
    public void Plan_fails_closed_for_invalid_inputs()
    {
        Assert.Throws<ArgumentException>(() => RouletteStripPlanner.Plan(Array.Empty<string>(), "winner", 9, 4, 1));
        Assert.Throws<ArgumentException>(() => RouletteStripPlanner.Plan(new[] { "a" }, "", 9, 4, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RouletteStripPlanner.Plan(new[] { "a" }, "winner", 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RouletteStripPlanner.Plan(new[] { "a" }, "winner", 9, 9, 1));
    }

    [Fact]
    public void A_hundred_percent_near_miss_chance_always_seats_a_candidate_on_both_neighbors()
    {
        var pool = new[] { "a", "b", "c" };
        var strip = RouletteStripPlanner.Plan(
            pool,
            "winner",
            tileCount: 9,
            landingIndex: 4,
            seed: 17,
            nearMissCandidates: new[] { "NEARMISS" },
            nearMissChancePercent: 100);

        Assert.Equal("winner", strip[4]);
        Assert.Equal("NEARMISS", strip[3]);
        Assert.Equal("NEARMISS", strip[5]);
    }

    [Fact]
    public void A_zero_percent_near_miss_chance_never_changes_the_strip()
    {
        var pool = new[] { "a", "b", "c" };
        var withoutBias = RouletteStripPlanner.Plan(pool, "winner", 9, 4, 17);
        var withZeroChance = RouletteStripPlanner.Plan(
            pool,
            "winner",
            tileCount: 9,
            landingIndex: 4,
            seed: 17,
            nearMissCandidates: new[] { "NEARMISS" },
            nearMissChancePercent: 0);

        Assert.Equal(withoutBias, withZeroChance);
    }

    [Fact]
    public void An_empty_near_miss_candidate_list_never_changes_the_strip()
    {
        var pool = new[] { "a", "b", "c" };
        var withoutBias = RouletteStripPlanner.Plan(pool, "winner", 9, 4, 17);
        var withEmptyCandidates = RouletteStripPlanner.Plan(
            pool,
            "winner",
            tileCount: 9,
            landingIndex: 4,
            seed: 17,
            nearMissCandidates: Array.Empty<string>(),
            nearMissChancePercent: 100);

        Assert.Equal(withoutBias, withEmptyCandidates);
    }

    [Fact]
    public void Near_miss_biasing_never_overwrites_the_committed_winner_even_at_a_strip_edge()
    {
        var pool = new[] { "a", "b", "c" };
        var strip = RouletteStripPlanner.Plan(
            pool,
            "winner",
            tileCount: 5,
            landingIndex: 0,
            seed: 5,
            nearMissCandidates: new[] { "NEARMISS" },
            nearMissChancePercent: 100);

        Assert.Equal("winner", strip[0]);
        Assert.Equal("NEARMISS", strip[1]);
    }
}
