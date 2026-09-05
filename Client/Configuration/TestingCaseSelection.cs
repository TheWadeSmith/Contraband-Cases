using ContrabandCases.Shared;
using System.ComponentModel;

namespace ContrabandCases.Client.Configuration;

internal enum TestingCaseTheme { Mixed, Operations, Relics, BlackSite, CashCache }
internal enum TestingOpeningTier { [Description("Natural — real odds")] Natural, Epic, Legendary }
internal enum TestingSelectionMode { [Description("Case and tier")] CaseAndTier, [Description("Legacy provider pool")] LegacyProviderPool }

internal static class TestingCaseSelection
{
    internal static TestingCrateType Resolve(TestingCaseTheme theme, TestingOpeningTier tier)
    {
        if (!Enum.IsDefined(typeof(TestingCaseTheme), theme) || !Enum.IsDefined(typeof(TestingOpeningTier), tier))
            throw new ArgumentException("Choose a supported testing case and opening tier.");
        if (theme == TestingCaseTheme.CashCache)
            return tier == TestingOpeningTier.Natural ? TestingCrateType.CashCache
                : throw new ArgumentException("Cash Cache has no Epic/Legendary opening tier. Select Natural.");
        return (theme, tier) switch
        {
            (TestingCaseTheme.Mixed, TestingOpeningTier.Natural) => TestingCrateType.TrueRandom,
            (TestingCaseTheme.Mixed, TestingOpeningTier.Epic) => TestingCrateType.EpicMixed,
            (TestingCaseTheme.Mixed, _) => TestingCrateType.LegendaryMixed,
            (TestingCaseTheme.Operations, TestingOpeningTier.Natural) => TestingCrateType.OperationsCase,
            (TestingCaseTheme.Operations, TestingOpeningTier.Epic) => TestingCrateType.EpicOperations,
            (TestingCaseTheme.Operations, _) => TestingCrateType.LegendaryOperations,
            (TestingCaseTheme.Relics, TestingOpeningTier.Natural) => TestingCrateType.RelicsCase,
            (TestingCaseTheme.Relics, TestingOpeningTier.Epic) => TestingCrateType.EpicRelics,
            (TestingCaseTheme.Relics, _) => TestingCrateType.LegendaryRelics,
            (TestingCaseTheme.BlackSite, TestingOpeningTier.Natural) => TestingCrateType.BlackSiteCase,
            (TestingCaseTheme.BlackSite, TestingOpeningTier.Epic) => TestingCrateType.EpicBlackSite,
            _ => TestingCrateType.LegendaryBlackSite
        };
    }
}
