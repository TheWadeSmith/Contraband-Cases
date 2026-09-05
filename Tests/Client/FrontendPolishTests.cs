using ContrabandCases.Client.Opening;
using ContrabandCases.Shared.Catalog;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class FrontendPolishTests
{
    private static ManifestLotSnapshot Lot => new("core", "Example mod", "meds", "Medical package",
        "For crafting", "field-supply", "medical", RewardRarity.Uncommon, "bandage", new string('a', 64),
        50_000, 50_000, 2, [new("bandage", "Army <b>bandage</b>", 2), new("med", "Salewa", 1), new("splint", "Splint", 3)]);

    [Theory]
    [InlineData("MEDICAL")]
    [InlineData("  crafting  ")]
    [InlineData("example MOD")]
    [InlineData("salewa")]
    public void Search_matches_names_purpose_source_and_contents_without_escaping_filters(string query)
    {
        var library = new ManifestLibrarySnapshot("", [Lot], false, "",
            new Dictionary<string, IReadOnlyList<string>> { ["case"] = ["core/meds"], ["empty"] = [] });
        Assert.Single(BrokerLibraryFilter.Lots(library, "case", "field-supply", query));
        Assert.Empty(BrokerLibraryFilter.Lots(library, "empty", "", query));
        Assert.Empty(BrokerLibraryFilter.Lots(library, "case", "arsenal", query));
        Assert.Empty(BrokerLibraryFilter.Lots(library, "unknown", "", query));
        Assert.Empty(BrokerLibraryFilter.Lots(library, "", "", "absent"));
    }

    [Fact]
    public void Compact_preview_retains_quantities_and_reports_omitted_contents()
    {
        var text = BrokerPresentation.ContentsPreview(Lot);
        Assert.Contains("2 × Army ‹b›bandage‹/b›", text);
        Assert.Contains("1 × Salewa", text);
        Assert.Contains("+1 more item type", text);
        Assert.DoesNotContain("3 × Splint", text);
        Assert.Contains("not cash/resale", BrokerPresentation.Value(Lot));
        Assert.Contains("3 × Splint", BrokerPresentation.FullContents(Lot));
    }

    [Theory]
    [InlineData(1280, 720, 2, 4)]
    [InlineData(1920, 1080, 3, 6)]
    [InlineData(2560, 1080, 3, 6)]
    [InlineData(1280, 1024, 2, 4)]
    public void Browser_pages_match_the_adaptive_grid(int width, int height, int columns, int size)
    {
        var layout = BrokerLibraryLayout.ForScreen(width, height);
        Assert.Equal(columns, layout.Columns);
        Assert.Equal(size, layout.PageSize);
        Assert.Equal(0, layout.LastPage(0));
        Assert.Equal(0, layout.LastPage(size));
        Assert.Equal(1, layout.LastPage(size + 1));
    }

    [Theory]
    [InlineData(CashPayouts.Roubles, "Rouble payout")]
    [InlineData(CashPayouts.Dollars, "not a rouble cash-out")]
    [InlineData(CashPayouts.Bitcoin, "Therapist sale value at catalog startup")]
    public void Cash_value_keeps_the_currency_specific_basis_visible(string template, string basis)
    {
        var lot = new ManifestLotSnapshot(CashPayouts.Provider, "Cash Cache", "cash", "Cash payout",
            "Cash", "field-supply", "cash", RewardRarity.Uncommon, template, new string('c', 64),
            50_000, 50_000, 1, [new(template, "Currency", 1)]);
        Assert.Contains(basis, BrokerPresentation.Value(lot));
    }

    private static RouletteRevealPlan Plan(int seed, string winner = "winner", bool reduced = false) =>
        RouletteRevealPlan.Create(["a", "b", "c"], winner, 50, 40, seed, 1000, 200, 12, reduced,
            nearMissCandidates: null, nearMissChancePercent: 0);

    [Fact]
    public void Compact_relay_terms_keep_odds_full_stake_loss_and_replacement_rules()
    {
        var snapshot = ManifestGallery.Create(Lot, GalleryState.ReadyToClaim, false);
        var text = BrokerPresentation.RelayEssentials(snapshot);
        Assert.Contains($"{snapshot.Relay!.UpgradePercent}%", text);
        Assert.Contains($"{snapshot.Relay.SidegradePercent}%", text);
        Assert.Contains($"{snapshot.Relay.ConfiscatePercent}%", text);
        Assert.Contains("whole package + 1 key", text);
        Assert.Contains("Loss takes both", text);
        Assert.Contains("same-rarity package and ends the chain", text);
    }

    [Fact]
    public void Guaranteed_relay_terms_do_not_warn_of_a_nonexistent_loss_chance()
    {
        var text = BrokerPresentation.RelayEssentials(ManifestGallery.Create(Lot, GalleryState.GuaranteedUpgrade, false));
        Assert.Contains("guaranteed upgrade + spend 1 key", text);
        Assert.Contains("Favor resets", text);
        Assert.DoesNotContain("Loss takes both", text);
    }

    [Fact]
    public void Spin_variation_is_repeatable_winner_independent_and_preserves_exact_result()
    {
        foreach (var seed in Enumerable.Range(0, 100))
        {
            var plan = Plan(seed);
            var repeat = Plan(seed);
            var otherWinner = Plan(seed, "another");
            var fade = Plan(seed, reduced: true);
            Assert.Equal("winner", plan.Strip[plan.LandingIndex]);
            Assert.Equal(plan.Strip, repeat.Strip);
            Assert.Equal(plan.StartX, repeat.StartX);
            Assert.InRange(plan.StartX, -4 * 212, 0);
            Assert.True(plan.StartX > plan.FinalX);
            Assert.Equal(plan.StartX, plan.PositionAt(0));
            Assert.Equal(plan.FinalX, plan.PositionAt(plan.DurationSeconds));
            Assert.Equal(plan.FinalX, plan.PositionAt(plan.DurationSeconds + 10));
            Assert.Equal(plan.FinalX, fade.FinalX);
            Assert.Equal(plan.CommittedWinnerId, fade.CommittedWinnerId);
            Assert.False(fade.UsesScrolling);
            var previous = plan.StartX;
            for (var i = 1; i <= 100; i++)
            {
                var elapsed = plan.DurationSeconds * i / 100;
                var position = plan.PositionAt(elapsed);
                Assert.InRange(position, plan.FinalX, previous);
                Assert.Equal(position, repeat.PositionAt(elapsed));
                Assert.Equal(position, otherWinner.PositionAt(elapsed));
                previous = position;
            }
        }
        var plans = Enumerable.Range(0, 20).Select(seed => Plan(seed)).ToArray();
        Assert.True(plans.Select(p => p.StartX).Distinct().Count() >= 3);
        Assert.True(plans.Select(p => p.AccelerationTimeShare).Distinct().Count() >= 10);
        Assert.True(plans.Select(p => p.DecelerationPower).Distinct().Count() >= 10);
    }

    [Fact]
    public void Every_spin_profile_has_a_continuous_velocity_at_the_acceleration_join()
    {
        foreach (var seed in Enumerable.Range(0, 100))
        {
            var plan = Plan(seed);
            var join = plan.AccelerationTimeShare * plan.DurationSeconds;
            const double h = 0.00001;
            var left = (plan.PositionAt(join) - plan.PositionAt(join - h)) / h;
            var right = (plan.PositionAt(join + h) - plan.PositionAt(join)) / h;
            Assert.InRange(left / right, 0.999, 1.001);
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void Motion_is_time_based_and_still_lands_exactly_after_a_delayed_frame(int framesPerSecond)
    {
        var plan = Plan(923);
        var previous = plan.StartX;
        for (var elapsed = 0d; elapsed < plan.DurationSeconds; elapsed += 1d / framesPerSecond)
        {
            var position = plan.PositionAt(elapsed);
            Assert.InRange(position, plan.FinalX, previous);
            previous = position;
        }
        Assert.Equal(plan.FinalX, plan.PositionAt(plan.DurationSeconds + 0.25));
        Assert.Equal("winner", plan.Strip[plan.LandingIndex]);
    }
}
