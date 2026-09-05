using ContrabandCases.Client.Opening;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class BrokerShortcutInputTests
{
    [Theory]
    [InlineData(true, true, true, true, false, true)]
    [InlineData(false, true, true, true, false, false)]
    [InlineData(true, false, true, true, false, false)]
    [InlineData(true, true, false, true, false, false)]
    [InlineData(true, true, true, false, false, false)]
    [InlineData(true, true, true, true, true, false)]
    public void Shortcut_requires_a_fresh_press_and_safe_unblocked_lobby_context(
        bool pressed, bool focused, bool available, bool menuOrStash, bool editing, bool expected)
    {
        Assert.Equal(expected, BrokerShortcutInput.ShouldOpen(pressed, focused, available, menuOrStash, editing));
    }

    [Fact]
    public void Blocked_press_is_not_queued_to_open_after_a_raid_or_another_window_closes()
    {
        Assert.False(BrokerShortcutInput.ShouldOpen(true, true, false, true, false));
        Assert.False(BrokerShortcutInput.ShouldOpen(false, true, true, true, false));
        Assert.True(BrokerShortcutInput.ShouldOpen(true, true, true, true, false));
    }
}
