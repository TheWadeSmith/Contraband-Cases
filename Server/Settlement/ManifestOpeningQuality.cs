using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Manifest;

namespace ContrabandCases.Server.Settlement;

/// <summary>Immutable tier draw, saved with the ticket before input consumption.</summary>
public sealed record ManifestOpeningQuality
{
    public ManifestOpeningQuality(ManifestOpeningTier tier, long draw, bool epicAvailable,
        bool legendaryAvailable, bool forcedTest, bool singlePrize = false)
    {
        if (!Enum.IsDefined(tier)) throw new ArgumentOutOfRangeException(nameof(tier));
        if (singlePrize && tier != ManifestOpeningTier.Legendary)
            throw new ArgumentException("Only Legendary openings support an automatic single prize.", nameof(singlePrize));
        var natural = ManifestOpeningTierRules.Select(draw, epicAvailable, legendaryAvailable);
        if (!forcedTest && tier != natural ||
            tier == ManifestOpeningTier.Epic && !epicAvailable ||
            tier == ManifestOpeningTier.Legendary && !legendaryAvailable)
            throw new ArgumentException("Opening quality contradicts its saved draw or available pools.");
        Tier = tier;
        Draw = draw;
        EpicAvailable = epicAvailable;
        LegendaryAvailable = legendaryAvailable;
        ForcedTest = forcedTest;
        SinglePrize = singlePrize;
    }

    public ManifestOpeningTier Tier { get; }
    public long Draw { get; }
    public bool EpicAvailable { get; }
    public bool LegendaryAvailable { get; }
    public bool ForcedTest { get; }
    // Missing in historical journals: retain their already-saved three-way choice.
    public bool SinglePrize { get; }
    public bool IsPremium => Tier != ManifestOpeningTier.Normal;

    internal void ValidateOffers(IEnumerable<RewardRarity> grades)
    {
        if (IsPremium && grades.Any(grade => Tier == ManifestOpeningTier.Legendary
                ? grade != RewardRarity.BlackLabel
                : grade is not (RewardRarity.Restricted or RewardRarity.BlackLabel)))
            throw new ArgumentException("Premium opening contains a package below its guaranteed grade.");
    }
}
