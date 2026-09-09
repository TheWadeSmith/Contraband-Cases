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

    internal TestingCrateType ResolveSelection() => CaseCount.Value == 0 ? TestingCrateType.TrueRandom : SelectionMode.Value switch
    {
        TestingSelectionMode.CaseAndTier => TestingCaseSelection.Resolve(CaseTheme.Value, OpeningTier.Value),
        TestingSelectionMode.LegacyProviderPool => CrateType.Value,
        _ => throw new ArgumentException("Select CaseAndTier or LegacyProviderPool.")
    };

    internal string? GrantBlockReason()
    {
        if (!Enabled.Value) return "Enable spawning first";
        try { TestingInventoryGrantPolicy.Validate(CaseCount.Value, KeyCount.Value); }
        catch (ArgumentException) { return "Choose 1–40 items total"; }
        try { _ = ResolveSelection(); }
        catch (ArgumentException) { return "Choose a valid case/tier (Cash: Natural)"; }
        return null;
    }

    public static TestingInventoryGrantConfig Bind(ConfigFile config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }
        TestingInventoryGrantConfig? settings = null;
        settings = new TestingInventoryGrantConfig(
            config.Bind(
                Section,
                "Enable Inventory Grant Controls",
                false,
                McmSettings.Option(McmSettings.Spawning, "Enable item spawning", 10,
                    "Adds REAL items to your stash. Server config must also have testingInventoryGrantsEnabled=true. Outside raids only; does not enable animation previews.")),
            config.Bind(
                Section,
                "Cases to Grant",
                5,
                McmSettings.Option(McmSettings.Spawning, "Case quantity", 50,
                    "0–10 cases of the selected type. Set to 0 for keys only. Cases plus keys may not exceed 40.",
                    new AcceptableValueRange<int>(0, TestingInventoryGrantPolicy.MaximumCaseCount))),
            config.Bind(
                Section,
                "Keys to Grant",
                10,
                McmSettings.Option(McmSettings.Spawning, "Key quantity", 60,
                    "0–40 universal, single-use keys. One opens any case; Relay costs another. Cases plus keys may not exceed 40.",
                    new AcceptableValueRange<int>(0, TestingInventoryGrantPolicy.MaximumKeyCount))),
            config.Bind(
                Section,
                "Forced Crate Pool",
                TestingCrateType.TrueRandom,
                McmSettings.Option(McmSettings.Advanced, "Legacy spawn pool", 10,
                    "Used only when Spawn selection is Legacy provider pool. Preserves older saved tests. Vault/Themed/Cards/Scrap/Mega select providers, not guaranteed rarities.", advanced: true)),
            McmSettings.BindAction(config,
                Section,
                "Grant Items to Stash Now",
                McmSettings.Spawning, "Spawn selected items", 70,
                "Request these real items once. Server permission and stash space are required. Close other case windows first. No request is replayed on game launch.",
                () => settings?.GrantBlockReason()));
        settings.CaseTheme = config.Bind(Section, "Case Theme", TestingCaseTheme.Mixed,
            McmSettings.Option(McmSettings.Spawning, "Case type", 30,
                "Choose Mixed, Operations, Relics, Black Site or Cash Cache. Used by Case and tier selection; ignored for keys-only grants."));
        settings.OpeningTier = config.Bind(Section, "Opening Tier", TestingOpeningTier.Natural,
            McmSettings.Option(McmSettings.Spawning, "Opening quality", 40,
                "Natural keeps real odds. Epic offers three packages to choose from; Legendary awards one rolled prize. Forced tiers apply only to newly granted TEST cases. Cash Cache requires Natural. Open before a server restart; unopened test tags expire. Existing/purchased cases are unchanged."));
        settings.SelectionMode = config.Bind(Section, "Selection Mode", TestingSelectionMode.CaseAndTier,
            McmSettings.Option(McmSettings.Spawning, "Spawn selection", 20,
                "Normally use Case and tier. Legacy provider pool ignores the two selectors below and uses Legacy spawn pool under Advanced tests (enable MCM's advanced settings)."));
        return settings;
    }
}
