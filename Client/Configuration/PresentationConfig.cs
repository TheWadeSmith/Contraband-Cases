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
            "Replaces scrolling with a brief fade and disables flashes, pulsing, shimmer and confetti. Takes full effect on the next reveal. Never changes results.");
        _volumeSetting = config.Bind("Presentation", "Effects Volume", 0.7f,
            new ConfigDescription("Volume of case ticks, tension and result tones. Set to zero to mute. Also respects the game's UI audio mixer.",
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
