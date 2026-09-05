namespace ContrabandCases.Client.UI;

internal static class BrokerLauncherPlacement
{
    internal const float Width = 160;
    internal const float Height = 44;
    internal const float Gap = 12;
    internal const float Margin = 8;

    internal static bool ShouldShow(bool enabled, bool canOpen, bool mainMenuActive) =>
        enabled && canOpen && mainMenuActive;

    internal static bool TryPlace(LauncherBounds trading, LauncherBounds safeArea,
        IReadOnlyList<LauncherBounds> obstacles, out LauncherBounds placement)
    {
        placement = default;
        if (!trading.IsValid || !safeArea.IsValid) return false;
        var candidate = new LauncherBounds(trading.Right + Gap,
            trading.Bottom + (trading.Height - Height) / 2, Width, Height);
        if (!candidate.IsValid || candidate.Left < safeArea.Left + Margin ||
            candidate.Right > safeArea.Right - Margin || candidate.Bottom < safeArea.Bottom + Margin ||
            candidate.Top > safeArea.Top - Margin) return false;
        foreach (var obstacle in obstacles)
            if (!obstacle.IsValid || candidate.Overlaps(obstacle)) return false;
        placement = candidate;
        return true;
    }
}

internal readonly record struct LauncherBounds(float Left, float Bottom, float Width, float Height)
{
    internal float Right => Left + Width;
    internal float Top => Bottom + Height;
    internal bool IsValid => Width > 0 && Height > 0 &&
        Finite(Left) && Finite(Bottom) && Finite(Right) && Finite(Top);
    internal bool Overlaps(LauncherBounds other) =>
        Left < other.Right && Right > other.Left && Bottom < other.Top && Top > other.Bottom;
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
