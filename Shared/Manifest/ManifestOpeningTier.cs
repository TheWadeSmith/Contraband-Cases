namespace ContrabandCases.Shared.Manifest;

public enum ManifestOpeningTier
{
    Normal,
    Epic,
    Legendary
}

public static class ManifestOpeningTierRules
{
    public const long DrawDenominator = 9_007_199_254_740_992L;
    public const int EpicBasisPoints = 150;
    public const int LegendaryBasisPoints = 20;

    public static ManifestOpeningTier Select(long draw, bool epicAvailable, bool legendaryAvailable)
    {
        if (draw < 0 || draw >= DrawDenominator)
            throw new ArgumentOutOfRangeException(nameof(draw));
        // Decimal multiplication avoids overflow and retains exact boundary comparisons.
        var point = (decimal)draw * 10_000;
        if (point < (decimal)DrawDenominator * LegendaryBasisPoints)
            return legendaryAvailable ? ManifestOpeningTier.Legendary : ManifestOpeningTier.Normal;
        if (point < (decimal)DrawDenominator * (LegendaryBasisPoints + EpicBasisPoints))
            return epicAvailable ? ManifestOpeningTier.Epic : ManifestOpeningTier.Normal;
        return ManifestOpeningTier.Normal;
    }
}
