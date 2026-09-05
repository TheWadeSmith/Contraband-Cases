using ContrabandCases.Client.UI;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class BrokerLauncherPlacementTests
{
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, false)]
    public void Launcher_requires_the_main_menu_in_addition_to_an_available_broker(
        bool enabled, bool canOpen, bool mainMenuActive, bool expected)
    {
        Assert.Equal(expected, BrokerLauncherPlacement.ShouldShow(enabled, canOpen, mainMenuActive));
    }

    [Theory]
    [InlineData(0, 0, 800, 600)]
    [InlineData(-960, -540, 1920, 1080)]
    [InlineData(-1720, -720, 3440, 1440)]
    public void Placement_is_beside_trading_and_centered_in_the_menus_coordinate_space(
        float left, float bottom, float width, float height)
    {
        var trading = new LauncherBounds(left + 100, bottom + 200, 200, 60);
        Assert.True(BrokerLauncherPlacement.TryPlace(trading,
            new LauncherBounds(left, bottom, width, height), [], out var placement));

        Assert.Equal(trading.Right + 12, placement.Left);
        Assert.Equal(trading.Bottom + trading.Height / 2, placement.Bottom + placement.Height / 2);
        Assert.Equal(160, placement.Width);
        Assert.Equal(44, placement.Height);
    }

    [Theory]
    [InlineData(640, 200)]
    [InlineData(-300, 200)]
    [InlineData(100, -40)]
    [InlineData(100, 580)]
    public void No_room_at_any_screen_edge_hides_instead_of_relocating(float left, float bottom)
    {
        Assert.False(BrokerLauncherPlacement.TryPlace(new LauncherBounds(left, bottom, 100, 60),
            new LauncherBounds(0, 0, 800, 600), [], out var placement));
        Assert.Equal(default, placement);
    }

    [Theory]
    [InlineData(420, true)]
    [InlineData(421, false)]
    public void Placement_preserves_the_screen_margin(float tradingLeft, bool expected)
    {
        Assert.Equal(expected, BrokerLauncherPlacement.TryPlace(new LauncherBounds(tradingLeft, 200, 200, 60),
            new LauncherBounds(0, 0, 800, 600), [], out _));
    }

    [Theory]
    [InlineData(320, 210, 30, 30, false)]
    [InlineData(300, 200, 200, 100, false)]
    [InlineData(100, 300, 200, 60, true)]
    [InlineData(472, 208, 40, 44, true)]
    public void Occupied_slots_hide_the_launcher_without_moving_native_controls(
        float left, float bottom, float width, float height, bool expected)
    {
        Assert.Equal(expected, BrokerLauncherPlacement.TryPlace(new LauncherBounds(100, 200, 200, 60),
            new LauncherBounds(0, 0, 800, 600), [new LauncherBounds(left, bottom, width, height)], out _));
    }

    [Theory]
    [InlineData(0, 0, 0, 60)]
    [InlineData(0, 0, 100, -1)]
    [InlineData(float.NaN, 0, 100, 60)]
    [InlineData(0, float.PositiveInfinity, 100, 60)]
    [InlineData(float.MaxValue, 0, float.MaxValue, 60)]
    public void Invalid_or_unresolved_geometry_never_shows_a_launcher(
        float left, float bottom, float width, float height)
    {
        var invalid = new LauncherBounds(left, bottom, width, height);
        var trading = new LauncherBounds(100, 200, 200, 60);
        var safeArea = new LauncherBounds(0, 0, 800, 600);
        Assert.False(BrokerLauncherPlacement.TryPlace(invalid, safeArea, [], out _));
        Assert.False(BrokerLauncherPlacement.TryPlace(trading, invalid, [], out _));
        Assert.False(BrokerLauncherPlacement.TryPlace(trading, safeArea, [invalid], out _));
    }
}
