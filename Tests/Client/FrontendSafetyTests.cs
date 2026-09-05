using ContrabandCases.Client.Opening;
using ContrabandCases.Client.Configuration;
using ContrabandCases.Client.Audio;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class FrontendSafetyTests
{
    [Theory]
    [InlineData(RewardRarity.ScavGrade, "Common")]
    [InlineData(RewardRarity.Uncommon, "Uncommon")]
    [InlineData(RewardRarity.Contractor, "Rare")]
    [InlineData(RewardRarity.Restricted, "Epic")]
    [InlineData(RewardRarity.BlackLabel, "Legendary")]
    public void Package_headings_use_plain_rarity_names_without_roman_numerals(RewardRarity grade, string label)
    {
        var lot = new ManifestLotSnapshot("core", "Base game", "kit", "Medical kit",
            "Medical supplies", "field-supply", "medical", grade, "item", new string('a', 64),
            50, 100, 1, [new ManifestLotContentSnapshot("item", "Medical kit", 1)]);

        var heading = ManifestPresentationPolicy.LotHeading(lot).Split('\n')[0];

        Assert.Equal($"<b>Medical kit</b>  •  {label}", heading);
    }

    [Fact]
    public void Readable_odds_keep_category_condition_and_do_not_imply_claim_chance()
    {
        var text = BrokerPresentation.ReadableOdds(Odds());
        Assert.Contains("25% • Included among saved offers: 75%", text);
        Assert.Contains("10% within this category", text);
        Assert.Contains("not final-claim odds", text);
        Assert.Contains("Medical ‹b›supplies‹/b›", text);
        Assert.DoesNotContain("Medical <b>supplies</b>", text);
    }

    [Fact]
    public void Missing_premium_disclosure_never_mislabels_a_cargo_case_as_cash()
    {
        Assert.DoesNotContain("CASH", BrokerPresentation.TierRates(Odds()));
        Assert.Contains("unavailable", BrokerPresentation.TierRates(Odds()));
    }

    [Fact]
    public void Cash_odds_describe_a_single_payout_not_category_selection()
    {
        var text = BrokerPresentation.ReadableOdds(Odds(CaseContracts.CashCache));
        Assert.Contains("10% per opening", text);
        Assert.DoesNotContain("Category per offer slot", text);
        Assert.Contains("NO PREMIUM TIER", text);
        var overview = BrokerPresentation.Overview(Odds(CaseContracts.CashCache));
        Assert.Contains("No discard or Relay", overview);
        Assert.Contains("No additional roubles charged", overview);
    }

    [Fact]
    public void Premium_odds_state_first_draw_condition_and_unavailable_tiers()
    {
        var lot = Odds().Families[0].Lots[0];
        var odds = Odds(premium: new ManifestPremiumOddsSnapshot(
            new ManifestPremiumTierOddsSnapshot(150, 350_000, [lot]),
            new ManifestPremiumTierOddsSnapshot(0, 1_000_000, [])));
        var text = BrokerPresentation.ReadableOdds(odds);
        Assert.Contains("Normal 98.5%", text);
        Assert.Contains("10% on the first package draw", text);
        Assert.Contains("Later draws exclude earlier choices", text);
        Assert.Contains("not final-claim probabilities", text);
        Assert.Contains("Unavailable with the installed catalog; not rolled", text);
    }

    [Fact]
    public void Full_package_details_keep_quantities_names_purpose_and_source_without_rich_text_injection()
    {
        var name = new string('z', 220);
        var lot = new ManifestLotSnapshot("core", "<color=red>Mod</color>", "kit", name,
            "Hideout and crafting", "field-supply", "medical", RewardRarity.Uncommon, "item", new string('a', 64),
            50, 100, 1, [new ManifestLotContentSnapshot("item", name, 17)]);
        var text = BrokerPresentation.PackageDetails(lot);
        Assert.Contains("17 × " + name, text);
        Assert.Contains("Hideout and crafting", text);
        Assert.Contains("‹color=red›Mod‹/color›", text);
        Assert.DoesNotContain("<color=red>", text);
        Assert.Contains("not cash/resale", text);
    }

    private static ManifestOpeningOddsSnapshot Odds(string template = CaseContracts.Operations,
        ManifestPremiumOddsSnapshot? premium = null) => new(3, "catalog-v2/" + new string('a', 64),
        template == CaseContracts.CashCache ? 1 : 3, 1, ManifestOpeningOddsSnapshot.CanonicalSelectionRule,
        [new ManifestOpeningFamilyOddsSnapshot("field-supply", "Medical <b>supplies</b>", "1", "4", "25%",
            "3", "4", "75%", [new ManifestOpeningLotOddsSnapshot("core", "Base game", "kit", "Medical kit",
                RewardRarity.Uncommon, "item", "1", "10", "10%")])], template, 100_000, premium);

    [Fact]
    public void Original_ui_foley_is_bounded_deterministic_and_fades_at_both_edges()
    {
        foreach (var cue in Enum.GetValues<MechanicalCue>())
        {
            var samples = MechanicalUiSound.Create(cue);
            Assert.Equal(samples, MechanicalUiSound.Create(cue));
            Assert.All(samples, value => { Assert.True(float.IsFinite(value)); Assert.InRange(value, -0.85f, 0.85f); });
            Assert.Equal(0, samples[0]);
            Assert.Equal(0, samples[^1]);
            Assert.Contains(samples, value => Math.Abs(value) > 0.01);
        }
    }
    [Fact]
    public void Only_primary_pointer_can_begin_a_hold()
    {
        var input = new RelayHoldInput();
        input.PointerDown(primary: false, focused: true);
        Assert.False(input.Advance(2, true, false, false));
        Assert.Equal(0, input.Progress);
        input.PointerDown(primary: true, focused: true);
        Assert.True(input.Advance(1.2, true, false, false));
        Assert.False(input.Advance(2, true, false, false));
    }

    [Fact]
    public void Focus_loss_discards_progress_and_requires_a_new_press()
    {
        var input = new RelayHoldInput();
        input.PointerDown(true, true);
        Assert.False(input.Advance(0.8, true, false, false));
        input.Interrupt();
        Assert.False(input.Advance(2, false, false, false));
        Assert.False(input.Advance(2, true, true, true));
        Assert.False(input.Advance(0, true, true, false));
        Assert.False(input.Advance(0.8, true, true, true));
        Assert.True(input.Advance(0.4, true, true, true));
    }

    [Fact]
    public void A_key_already_held_when_control_appears_cannot_confirm()
    {
        var input = new RelayHoldInput();
        Assert.False(input.Advance(2, true, true, true));
        Assert.False(input.Advance(0, true, true, false));
        Assert.True(input.Advance(1.2, true, true, true));
    }

    [Fact]
    public void Secondary_pointer_release_does_not_complete_or_cancel_primary_hold()
    {
        var input = new RelayHoldInput();
        input.PointerDown(true, true);
        input.PointerUp(false);
        Assert.True(input.Advance(1.2, true, false, false));
    }

    [Fact]
    public void Case_and_tier_selectors_cover_all_real_case_and_premium_combinations()
    {
        foreach (var theme in Enum.GetValues<TestingCaseTheme>())
        foreach (var tier in Enum.GetValues<TestingOpeningTier>())
        {
            if (theme == TestingCaseTheme.CashCache && tier != TestingOpeningTier.Natural)
            {
                Assert.Throws<ArgumentException>(() => TestingCaseSelection.Resolve(theme, tier));
                continue;
            }
            var resolved = TestingCaseSelection.Resolve(theme, tier);
            Assert.True(CaseContracts.IsCase(TestingCrateTypeCodec.CaseTemplate(resolved)));
            Assert.Equal(tier == TestingOpeningTier.Natural ? null :
                tier == TestingOpeningTier.Epic ? ManifestOpeningTier.Epic : ManifestOpeningTier.Legendary,
                TestingCrateTypeCodec.PremiumTier(resolved));
            Assert.Equal(resolved, TestingCrateTypeCodec.Parse(TestingCrateTypeCodec.ToWireValue(resolved)));
        }
        Assert.Throws<ArgumentException>(() => TestingCaseSelection.Resolve((TestingCaseTheme)999, TestingOpeningTier.Natural));
    }

    [Fact]
    public void Premium_plan_loads_all_three_choice_images_and_only_selected_package_contents()
    {
        var lots = Enumerable.Range(0, 3).Select(i => new ManifestLotSnapshot("core", "Base Game", $"lot{i}", $"Package {i}",
            "Medical", "field-supply", "meds", RewardRarity.Restricted, $"anchor{i}", new string((char)('a' + i), 64),
            10, 100, 1, [new ManifestLotContentSnapshot($"item{i}", $"Item {i}", 1)])).ToArray();
        var plan = ManifestSpritePlan.ForPremiumChoices(lots, 1);
        var ids = plan.Requests.SelectMany(r => r.TileIds).ToArray();
        Assert.Contains("premium:0", ids);
        Assert.Contains("premium:1", ids);
        Assert.Contains("premium:2", ids);
        Assert.Contains("content:item1", ids);
        Assert.DoesNotContain("content:item0", ids);
        Assert.DoesNotContain("content:item2", ids);
    }
}
