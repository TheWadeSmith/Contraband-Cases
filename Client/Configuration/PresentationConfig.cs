using Newtonsoft.Json;
using BepInEx.Configuration;

namespace ContrabandCases.Client.Configuration;

internal sealed class PresentationConfig
{
    private ConfigEntry<bool>? _motionSetting;
    private ConfigEntry<float>? _volumeSetting;
    private bool _reducedMotionDefault;

    [JsonProperty("animationDurationSeconds")]
    public double AnimationDurationSeconds { get; private set; } = 4.5d;

    [JsonProperty("reducedMotionDefault")]
    public bool ReducedMotionDefault
    {
        get => _motionSetting?.Value ?? _reducedMotionDefault;
        private set => _reducedMotionDefault = value;
    }

    [JsonIgnore]
    public float Volume => _volumeSetting?.Value ?? 0.7f;

    public void BindPlayerSettings(ConfigFile config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        _motionSetting = config.Bind("Presentation", "Reduced Motion", _reducedMotionDefault,
            McmSettings.Option(McmSettings.General, "Reduced motion", 30,
                "Use a brief fade instead of the spinner, flashes and confetti. Applies on the next reveal; rewards never change."));
        _volumeSetting = config.Bind("Presentation", "Effects Volume", 0.7f,
            McmSettings.Option(McmSettings.General, "Sound volume", 40,
                "Case ticks, tension and result sounds. Zero mutes them. Also respects the game's UI volume. Open Broker > Status to test the sound.",
                new AcceptableValueRange<float>(0f, 1f)));
    }

    [JsonProperty("debugLogging")]
    public bool DebugLogging { get; private set; }

    public static PresentationConfig Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Contraband Cases presentation config is empty.");
        }

        PresentationConfig config;
        try
        {
            config = JsonConvert.DeserializeObject<PresentationConfig>(json)
                ?? throw new InvalidOperationException("Contraband Cases presentation config deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Contraband Cases presentation config is not valid JSONC.", exception);
        }

        if (double.IsNaN(config.AnimationDurationSeconds) ||
            double.IsInfinity(config.AnimationDurationSeconds) ||
            config.AnimationDurationSeconds <= 0d)
        {
            throw new InvalidOperationException("animationDurationSeconds must be positive and finite.");
        }

        return config;
    }
}
