using BepInEx.Configuration;
using ContrabandCases.Shared;

namespace ContrabandCases.Client.Configuration;

internal sealed class TestingInventoryGrantConfig
{
    private const string Section = "Testing (Inventory Grants — Server Opt-In)";

    private TestingInventoryGrantConfig(
        ConfigEntry<bool> enabled,
        ConfigEntry<int> caseCount,
        ConfigEntry<int> keyCount,
        ConfigEntry<TestingCrateType> crateType,
        ConfigEntry<bool> grantNow)
    {
        Enabled = enabled;
        CaseCount = caseCount;
        KeyCount = keyCount;
        CrateType = crateType;
        GrantNow = grantNow;
    }

    public ConfigEntry<bool> Enabled { get; }

    public ConfigEntry<int> CaseCount { get; }

    public ConfigEntry<int> KeyCount { get; }

    /// <summary>
    /// Which reward-pack pool a granted case is forced to draw from once
    /// opened. "True Random" (the default) leaves granted cases identical to
    /// a normal Mechanic-purchased case -- no forcing occurs. Every other
    /// value only affects cases granted by this control while it is set;
    /// it has zero effect on any case already in the stash or on Mechanic's
    /// assort.
    /// </summary>
    public ConfigEntry<TestingCrateType> CrateType { get; }

    public ConfigEntry<bool> GrantNow { get; }
    public ConfigEntry<TestingCaseTheme> CaseTheme { get; private set; } = null!;
    public ConfigEntry<TestingOpeningTier> OpeningTier { get; private set; } = null!;
    public ConfigEntry<TestingSelectionMode> SelectionMode { get; private set; } = null!;

    internal TestingCrateType ResolveSelection() => SelectionMode.Value switch
    {
        TestingSelectionMode.CaseAndTier => TestingCaseSelection.Resolve(CaseTheme.Value, OpeningTier.Value),
        TestingSelectionMode.LegacyProviderPool => CrateType.Value,
        _ => throw new ArgumentException("Select CaseAndTier or LegacyProviderPool.")
    };

    public static TestingInventoryGrantConfig Bind(ConfigFile config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }
        var settings = new TestingInventoryGrantConfig(
            config.Bind(
                Section,
                "Enable Inventory Grant Controls",
                false,
                "Allows the one-shot grant control below. The server must also opt in with testingInventoryGrantsEnabled=true, and grants work only in the stash outside a raid."),
            config.Bind(
                Section,
                "Cases to Grant",
                5,
                new ConfigDescription(
                    "Number of real BR-12 Relay Cases to add to the stash.",
                    new AcceptableValueRange<int>(0, TestingInventoryGrantPolicy.MaximumCaseCount))),
            config.Bind(
                Section,
                "Keys to Grant",
                10,
                new ConfigDescription(
                    "Number of real BR-12 Relay Keys to add to the stash.",
                    new AcceptableValueRange<int>(0, TestingInventoryGrantPolicy.MaximumKeyCount))),
            config.Bind(
                Section,
                "Forced Crate Pool",
                TestingCrateType.TrueRandom,
                "OperationsCase, RelicsCase, BlackSiteCase and CashCache grant actual cases with normal odds. TrueRandom grants Mixed. Epic/Legendary options force that surprise tier for the named case (three saved choices; choose one). Requires enough qualifying packages. Test tags expire on server restart BEFORE opening; opened choices are permanently saved. Vault/Themed/Cards/Scrap/Mega are provider preferences, not rarity guarantees."),
            config.Bind(
                Section,
                "Grant Items to Stash Now",
                false,
                "Toggle on once to request the selected real items. It resets before sending and cannot run during a case opening, cosmetic preview, or raid."));
        settings.CaseTheme = config.Bind(Section, "Case Theme", TestingCaseTheme.Mixed,
            "Actual case to spawn. Mixed, Operations, Relics, BlackSite or CashCache. Used in CaseAndTier mode.");
        settings.OpeningTier = config.Bind(Section, "Opening Tier", TestingOpeningTier.Natural,
            "Natural uses real odds. Epic/Legendary force a TEST opening with three saved choices; choose ONE. CashCache supports Natural only. Does not affect purchased or existing cases.");
        settings.SelectionMode = config.Bind(Section, "Selection Mode", TestingSelectionMode.CaseAndTier,
            "CaseAndTier uses the separate theme/tier controls. LegacyProviderPool uses the old Forced Crate Pool setting instead; its value is preserved.");
        return settings;
    }
}
