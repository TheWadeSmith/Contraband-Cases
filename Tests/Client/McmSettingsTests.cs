using BepInEx.Configuration;
using ContrabandCases.Client.Configuration;
using ContrabandCases.Client.Opening;
using ContrabandCases.Shared;
using UnityEngine;
using Xunit;

namespace ContrabandCases.Tests.Client;

public sealed class McmSettingsTests
{
    private const string Grants = "Testing (Inventory Grants — Server Opt-In)";
    private const string Preview = "Testing (Cosmetic Only)";
    private static readonly (string Section, string Key)[] Actions =
    [
        (Grants, "Grant Items to Stash Now"), (Preview, "Run Cosmetic Self-Test"),
        ("Broker Dossier", "Open Dossier"), ("Testing (Catalog Gallery)", "Open Gallery")
    ];

    [Fact]
    public void Broker_starts_hidden_with_a_rebindable_F5_shortcut_and_an_MCM_action()
    {
        using var fixture = new ConfigFixture();
        var settings = new LibraryConfig(fixture.Load());
        Assert.False(settings.ShowBrokerButton.Value);
        Assert.Equal(KeyCode.F5, settings.OpenBrokerShortcut.Value.MainKey);
        Assert.Empty(settings.OpenBrokerShortcut.Value.Modifiers);
        Assert.False(settings.OpenDossier.Value);
        Assert.NotNull(Field<Delegate>(Assert.Single(settings.OpenDossier.Description.Tags), "CustomDrawer"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Old_button_setting_does_not_reenable_the_retired_overlay_on_upgrade(bool oldValue)
    {
        using var fixture = new ConfigFixture();
        var old = fixture.Load();
        old.Bind("Broker Dossier", "Show Broker Button", true).Value = oldValue;
        old.Save();

        var config = fixture.Load();
        var settings = new LibraryConfig(config);
        Assert.False(settings.ShowBrokerButton.Value);
        config.Save();
        Assert.Equal(oldValue, fixture.Load().Bind("Broker Dossier", "Show Broker Button", true).Value);
    }

    [Theory]
    [InlineData(KeyCode.F4, KeyCode.LeftControl)]
    [InlineData(KeyCode.None, KeyCode.None)]
    public void Explicit_shortcut_and_optional_button_preferences_survive_restart(KeyCode key, KeyCode modifier)
    {
        using var fixture = new ConfigFixture();
        var config = fixture.Load();
        var settings = new LibraryConfig(config);
        settings.OpenBrokerShortcut.Value = modifier == KeyCode.None
            ? new KeyboardShortcut(key) : new KeyboardShortcut(key, modifier);
        settings.ShowBrokerButton.Value = true;
        config.Save();

        var reloaded = new LibraryConfig(fixture.Load());
        Assert.Equal(settings.OpenBrokerShortcut.Value.Serialize(), reloaded.OpenBrokerShortcut.Value.Serialize());
        Assert.True(reloaded.ShowBrokerButton.Value);
    }

    [Fact]
    public void Installed_MCM_exposes_the_optional_visible_window_guard_contract()
    {
        var property = typeof(ConfigurationManager.ConfigurationManager).GetProperty("DisplayingWindow");
        Assert.NotNull(property);
        Assert.Equal(typeof(bool), property.PropertyType);
        Assert.True(property.CanRead);
    }

    [Fact]
    public void Saved_action_flags_are_cleared_at_bind_without_resetting_user_preferences()
    {
        using var fixture = new ConfigFixture();
        var seed = fixture.Load();
        foreach (var (section, key) in Actions) seed.Bind(section, key, false).Value = true;
        seed.Bind(Grants, "Enable Inventory Grant Controls", false).Value = true;
        seed.Bind(Grants, "Cases to Grant", 5).Value = 2;
        seed.Bind(Grants, "Keys to Grant", 10).Value = 7;
        seed.Bind(Grants, "Case Theme", TestingCaseTheme.Mixed).Value = TestingCaseTheme.Relics;
        seed.Bind(Grants, "Opening Tier", TestingOpeningTier.Natural).Value = TestingOpeningTier.Legendary;
        seed.Bind("Presentation", "Effects Volume", 0.7f).Value = 0.25f;
        seed.Save();

        var config = fixture.Load();
        var settings = BindAll(config);

        foreach (var (section, key) in Actions) Assert.False((bool)config[section, key].BoxedValue);
        Assert.True(settings.Enabled.Value);
        Assert.Equal(2, settings.CaseCount.Value);
        Assert.Equal(7, settings.KeyCount.Value);
        Assert.Equal(TestingCrateType.LegendaryRelics, settings.ResolveSelection());
        Assert.Equal(0.25f, config["Presentation", "Effects Volume"].BoxedValue);
    }

    [Fact]
    public void Every_control_has_display_metadata_and_actions_are_buttons_without_reset_controls()
    {
        using var fixture = new ConfigFixture();
        var config = fixture.Load();
        BindAll(config);

        foreach (var entry in config.Select(pair => pair.Value))
        {
            var tag = Assert.Single(entry.Description.Tags);
            Assert.Equal("ConfigurationManagerAttributes", tag.GetType().Name);
            Assert.False(string.IsNullOrWhiteSpace(Field<string>(tag, "Category")));
            Assert.False(string.IsNullOrWhiteSpace(Field<string>(tag, "DispName")));
        }
        foreach (var (section, key) in Actions)
        {
            var tag = Assert.Single(config[section, key].Description.Tags);
            Assert.NotNull(Field<Delegate>(tag, "CustomDrawer"));
            Assert.True(Field<bool>(tag, "HideDefaultButton"));
        }
        Assert.True(Field<bool>(Assert.Single(config[Grants, "Forced Crate Pool"].Description.Tags), "IsAdvanced"));
    }

    [Fact]
    public void Keys_only_grants_do_not_require_a_valid_case_tier_combination()
    {
        using var fixture = new ConfigFixture();
        var settings = TestingInventoryGrantConfig.Bind(fixture.Load());
        settings.CaseCount.Value = 0;
        settings.KeyCount.Value = 10;
        settings.CaseTheme.Value = TestingCaseTheme.CashCache;
        settings.OpeningTier.Value = TestingOpeningTier.Legendary;

        Assert.Equal(TestingCrateType.TrueRandom, settings.ResolveSelection());
    }

    [Fact]
    public void Installed_MCM_imports_labels_categories_order_advanced_flags_and_button_drawers()
    {
        using var fixture = new ConfigFixture();
        var config = fixture.Load();
        BindAll(config);
        var adapterType = typeof(ConfigurationManager.SettingEntryBase).Assembly
            .GetType("ConfigurationManager.ConfigSettingEntry", throwOnError: true)!;
        foreach (var entry in config.Select(pair => pair.Value))
        {
            var tag = Assert.Single(entry.Description.Tags);
            var native = (ConfigurationManager.SettingEntryBase)Activator.CreateInstance(adapterType, entry, null)!;
            Assert.Equal(Field<string>(tag, "Category"), native.Category);
            Assert.Equal(Field<string>(tag, "DispName"), native.DispName);
            Assert.Equal(Field<int>(tag, "Order"), native.Order);
            Assert.Equal(Field<bool>(tag, "IsAdvanced"), native.IsAdvanced);
            Assert.Equal(Field<bool>(tag, "HideDefaultButton"), native.HideDefaultButton);
            Assert.Same(tag.GetType().GetField("CustomDrawer")!.GetValue(tag), native.CustomDrawer);
        }
        var spawnRows = config.Select(pair => pair.Value.Description.Tags.Single())
            .Where(tag => Field<string>(tag, "Category") == McmSettings.Spawning)
            .OrderByDescending(tag => Field<int>(tag, "Order"))
            .Select(tag => Field<string>(tag, "DispName")).ToArray();
        Assert.Equal(new[] { "Enable item spawning", "Spawn selection", "Case type", "Opening quality",
            "Case quantity", "Key quantity", "Spawn selected items" }, spawnRows);
    }

    [Theory]
    [InlineData(false, 1, 1, TestingCaseTheme.Mixed, TestingOpeningTier.Natural, "Enable spawning first")]
    [InlineData(true, 0, 0, TestingCaseTheme.Mixed, TestingOpeningTier.Natural, "Choose 1–40 items total")]
    [InlineData(true, 10, 40, TestingCaseTheme.Mixed, TestingOpeningTier.Natural, "Choose 1–40 items total")]
    [InlineData(true, 1, 1, TestingCaseTheme.CashCache, TestingOpeningTier.Legendary, "Choose a valid case/tier (Cash: Natural)")]
    [InlineData(true, 1, 1, TestingCaseTheme.CashCache, TestingOpeningTier.Natural, null)]
    [InlineData(true, 0, 40, TestingCaseTheme.CashCache, TestingOpeningTier.Legendary, null)]
    [InlineData(true, 2, 7, TestingCaseTheme.Relics, TestingOpeningTier.Legendary, null)]
    public void Spawn_button_explains_invalid_choices_without_changing_them(bool enabled, int cases, int keys,
        object theme, object tier, string? expected)
    {
        using var fixture = new ConfigFixture();
        var settings = TestingInventoryGrantConfig.Bind(fixture.Load());
        settings.Enabled.Value = enabled;
        settings.CaseCount.Value = cases;
        settings.KeyCount.Value = keys;
        settings.CaseTheme.Value = (TestingCaseTheme)theme;
        settings.OpeningTier.Value = (TestingOpeningTier)tier;
        Assert.Equal(expected, settings.GrantBlockReason());
        Assert.Equal(tier, settings.OpeningTier.Value);
        Assert.False(settings.GrantNow.Value);
    }

    [Fact]
    public void Legacy_pool_and_real_case_modes_keep_their_saved_values_and_independent_authority()
    {
        using var fixture = new ConfigFixture();
        var seed = fixture.Load();
        seed.Bind(Grants, "Selection Mode", TestingSelectionMode.CaseAndTier).Value = TestingSelectionMode.LegacyProviderPool;
        seed.Bind(Grants, "Forced Crate Pool", TestingCrateType.TrueRandom).Value = TestingCrateType.Cards;
        seed.Save();
        var settings = TestingInventoryGrantConfig.Bind(fixture.Load());
        Assert.Equal(TestingCrateType.Cards, settings.ResolveSelection());
        settings.CaseTheme.Value = TestingCaseTheme.BlackSite;
        settings.OpeningTier.Value = TestingOpeningTier.Epic;
        Assert.Equal(TestingCrateType.Cards, settings.ResolveSelection());
        settings.SelectionMode.Value = TestingSelectionMode.CaseAndTier;
        Assert.Equal(TestingCrateType.EpicBlackSite, settings.ResolveSelection());
        Assert.Equal(TestingCrateType.Cards, settings.CrateType.Value);
    }

    private static TestingInventoryGrantConfig BindAll(ConfigFile config)
    {
        PresentationConfig.Parse("{}").BindPlayerSettings(config);
        var grants = TestingInventoryGrantConfig.Bind(config);
        TestingModeConfig.Bind(config);
        _ = new LibraryConfig(config);
        return grants;
    }

    private static T Field<T>(object tag, string name) =>
        Assert.IsAssignableFrom<T>(tag.GetType().GetField(name)!.GetValue(tag));

    private sealed class ConfigFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "contraband-mcm-test-" + Guid.NewGuid().ToString("N"));
        public ConfigFixture() => Directory.CreateDirectory(_directory);
        public ConfigFile Load() => new(Path.Combine(_directory, "settings.cfg"), false) { SaveOnConfigSet = false };
        public void Dispose()
        {
            File.Delete(Path.Combine(_directory, "settings.cfg"));
            Directory.Delete(_directory);
        }
    }
}
