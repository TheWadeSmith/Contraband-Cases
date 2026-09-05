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
                "Unlocks cosmetic presentation tests only. It never contacts the server, changes odds, consumes or grants items, or edits a profile."),
            config.Bind(
                Section,
                "Run Cosmetic Self-Test",
                false,
                "Toggle on to preview the selected opening or Relay presentation once; it resets automatically. Requires Testing Mode and never performs an economic action."),
            config.Bind(
                Section,
                "Cosmetic Outcome",
                CosmeticOutcomeSelection.CycleRewards,
                "Chooses an opening rarity landing or a cosmetic Relay upgrade, sidegrade, or confiscation result. It cannot contact the server or force a real outcome."),
            config.Bind(
                Section,
                "Self-Test Animation Seconds",
                1f,
                new ConfigDescription(
                    "Duration of the cosmetic preview. This does not change the normal opening animation.",
                    new AcceptableValueRange<float>(
                        (float)CosmeticSelfTestPolicy.MinimumDurationSeconds,
                        (float)CosmeticSelfTestPolicy.MaximumDurationSeconds))),
            config.Bind(
                Section,
                "Extra Lifecycle Diagnostics",
                false,
                "Adds overlay creation, destruction, and scene-detach details to the BepInEx log."));
    }

}
