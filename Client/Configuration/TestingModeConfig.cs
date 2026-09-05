using BepInEx.Configuration;
using ContrabandCases.Client.Opening;

namespace ContrabandCases.Client.Configuration;

internal sealed class TestingModeConfig
{
    private const string Section = "Testing (Cosmetic Only)";

    private TestingModeConfig(
        ConfigEntry<bool> enabled,
        ConfigEntry<bool> runSelfTest,
        ConfigEntry<CosmeticOutcomeSelection> outcome,
        ConfigEntry<float> animationSeconds,
        ConfigEntry<bool> lifecycleDiagnostics)
    {
        Enabled = enabled;
        RunSelfTest = runSelfTest;
        Outcome = outcome;
        AnimationSeconds = animationSeconds;
        LifecycleDiagnostics = lifecycleDiagnostics;
    }

    public ConfigEntry<bool> Enabled { get; }

    public ConfigEntry<bool> RunSelfTest { get; }

    public ConfigEntry<CosmeticOutcomeSelection> Outcome { get; }

    public ConfigEntry<float> AnimationSeconds { get; }

    public ConfigEntry<bool> LifecycleDiagnostics { get; }

    public static TestingModeConfig Bind(ConfigFile config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        return new TestingModeConfig(
            config.Bind(
                Section,
                "Testing Mode",
                false,
                McmSettings.Option(McmSettings.Preview, "Enable previews", 10,
                    "Unlock animation and catalog previews. These never spend or grant items or change real odds. Catalog previews read the server; animation previews stay local.")),
            McmSettings.BindAction(config,
                Section,
                "Run Cosmetic Self-Test",
                McmSettings.Preview, "Play animation preview", 40,
                "Play the selected case or Relay animation once. No case, key or reward is spent or granted.",
                () => config[Section, "Testing Mode"].BoxedValue is true ? null : "Enable previews first"),
            config.Bind(
                Section,
                "Cosmetic Outcome",
                CosmeticOutcomeSelection.CycleRewards,
                McmSettings.Option(McmSettings.Preview, "Preview result", 20,
                    "Select the animation to inspect, not a real reward. To receive actual testing cases, use Spawn test items.")),
            config.Bind(
                Section,
                "Self-Test Animation Seconds",
                1f,
                McmSettings.Option(McmSettings.Preview, "Preview duration (seconds)", 30,
                    "Animation preview only. Does not change normal case openings.",
                    new AcceptableValueRange<float>(
                        (float)CosmeticSelfTestPolicy.MinimumDurationSeconds,
                        (float)CosmeticSelfTestPolicy.MaximumDurationSeconds))),
            config.Bind(
                Section,
                "Extra Lifecycle Diagnostics",
                false,
                McmSettings.Option(McmSettings.Advanced, "Extra UI logging", 200,
                    "Log window creation, cleanup and scene changes for troubleshooting. Normally leave off.", advanced: true)));
    }

}
