using ContrabandCases.Client.Opening;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class ManifestRecoveryCopyTests
{
    [Theory]
    [InlineData("Epic")]
    [InlineData("Legendary")]
    public void Unavailable_premium_test_opening_explains_missing_packages_and_preserved_inputs(string tier)
    {
        var message = ManifestPresentationPolicy.RejectedActionMessage(ManifestEconomicAction.OpenTicket,
            $"{tier} testing is unavailable for this case: not enough qualifying packages are installed. " +
            "Your case and key were not consumed. Spawn a different testing case or restore its reward packs.");
        Assert.Contains("not enough qualifying packages", message);
        Assert.Contains("case and key were not consumed", message);
        Assert.DoesNotContain("saved opening", message);
    }

    [Fact]
    public void Rejected_fresh_opening_does_not_claim_a_saved_reward_exists()
    {
        var message = ManifestPresentationPolicy.RejectedActionMessage(ManifestEconomicAction.OpenTicket,
            "<b>internal server failure</b>");
        Assert.Contains("No saved opening", message);
        Assert.DoesNotContain("internal server failure", message);
    }

    [Fact]
    public void Missing_relay_key_explains_the_requirement_and_keeps_collection_available()
    {
        var message = ManifestPresentationPolicy.RejectedActionMessage(
            ManifestEconomicAction.Relay,
            "BR-12 Relay Key required. Place a usable key in your stash and try again. No items were consumed.");
        Assert.Contains("another BR-12 Relay Key", message);
        Assert.Contains("collect it", message);
    }

    [Theory]
    [InlineData(ManifestEconomicAction.Relay, "does not spend a key or roll again")]
    [InlineData(ManifestEconomicAction.Claim, "Mechanic in Messenger")]
    [InlineData(ManifestEconomicAction.Burn, "does not spend items or repeat your choice")]
    public void Rejected_actions_explain_the_safe_next_step_without_leaking_server_details(
        ManifestEconomicAction action, string expected)
    {
        var message = ManifestPresentationPolicy.RejectedActionMessage(action, "<b>internal stack trace</b>");
        Assert.Contains(expected, message);
        Assert.DoesNotContain("internal stack trace", message);
        Assert.DoesNotContain("authoritative", message);
        Assert.DoesNotContain("economic action", message);
    }
}
