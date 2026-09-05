using System.Text.Json;

namespace ContrabandCases.Server.Configuration;

public sealed class ModConfig
{
    public int CaseStock { get; private set; } = 5;

    public int TraderStock => CaseStock;

    public double AnimationDurationSeconds { get; private set; } = 4.5d;

    public bool ReducedMotionDefault { get; private set; }

    public bool DebugLogging { get; private set; }

    public bool TestingInventoryGrantsEnabled { get; private set; }

    public long? FixedCasePrice { get; private set; }

    /// <summary>
    /// Guaranteed rouble amount the case is worth when sold to the Therapist specifically. When set, the
    /// case's registered handbook price is computed so that Therapist's live buy price coefficient resolves
    /// to (at least) this amount, decoupled from <see cref="FixedCasePrice"/> (which only ever drives what
    /// the Mechanic charges to sell the case to the player). Null disables the feature entirely.
    /// </summary>
    public long? TherapistSellPriceCase { get; private set; }

    /// <summary>
    /// Same as <see cref="TherapistSellPriceCase"/>, but for the key. Independent of the case setting. The
    /// key is find-only and never sold, so this is the only way its price is ever surfaced to a player.
    /// </summary>
    public long? TherapistSellPriceKey { get; private set; }

    /// <summary>
    /// How aggressively the BR-12 Relay Key is added to raid loot, expressed as a percentage of the
    /// existing weight already present in each targeted static loot container or bot loot pool (see
    /// ContrabandCases.Server.Loot.ContrabandKeyLootInjector). The key is only ever added to a container or
    /// bot pool that already places at least one vanilla key-category item, at an additional weight equal
    /// to (sum of that pool's existing weights) * (this percentage / 100). Scaling relative to each pool's
    /// own weight, rather than a single flat number, keeps the key's relative rarity roughly consistent
    /// across pools that use wildly different weight scales. Must be positive and finite.
    /// </summary>
    public double KeyLootWeightPercent { get; private set; } = 2d;

    /// <summary>
    /// Combined added weight for all available case types in verified raid crates, as a percentage
    /// of each pool's non-case weight. Not a probability per crate or raid. Zero disables case drops.
    /// </summary>
    public double CaseLootWeightPercent { get; private set; } = 1d;

    public static ModConfig Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Contraband Cases config is empty.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Contraband Cases config must be a JSON object.");
            }

            var config = new ModConfig();
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            var caseStockWasSet = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    throw new InvalidOperationException(
                        $"Contraband Cases config contains duplicate property '{property.Name}'.");
                }

                switch (property.Name)
                {
                    case "traderStock":
                        if (caseStockWasSet)
                        {
                            throw new InvalidOperationException("traderStock and caseStock cannot both be set.");
                        }
                        config.CaseStock = ReadInt32(property);
                        caseStockWasSet = true;
                        break;
                    case "caseStock":
                        if (caseStockWasSet)
                        {
                            throw new InvalidOperationException("traderStock and caseStock cannot both be set.");
                        }
                        config.CaseStock = ReadInt32(property);
                        caseStockWasSet = true;
                        break;
                    case "animationDurationSeconds":
                        config.AnimationDurationSeconds = ReadDouble(property);
                        break;
                    case "reducedMotionDefault":
                        config.ReducedMotionDefault = ReadBoolean(property);
                        break;
                    case "debugLogging":
                        config.DebugLogging = ReadBoolean(property);
                        break;
                    case "testingInventoryGrantsEnabled":
                        config.TestingInventoryGrantsEnabled = ReadBoolean(property);
                        break;
                    case "fixedCasePrice":
                        config.FixedCasePrice = ReadNullableInt64(property);
                        break;
                    case "therapistSellPriceCase":
                        config.TherapistSellPriceCase = ReadNullableInt64(property);
                        break;
                    case "therapistSellPriceKey":
                        config.TherapistSellPriceKey = ReadNullableInt64(property);
                        break;
                    case "keyLootWeightPercent":
                        config.KeyLootWeightPercent = ReadDouble(property);
                        break;
                    case "caseLootWeightPercent":
                        config.CaseLootWeightPercent = ReadDouble(property);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Contraband Cases config contains unknown property '{property.Name}'.");
                }
            }

            config.Validate();
            return config;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Contraband Cases config is not valid JSONC.", exception);
        }
    }

    private static int ReadInt32(JsonProperty property)
    {
        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value))
        {
            return value;
        }

        throw InvalidPropertyType(property, "an integer");
    }

    private static long? ReadNullableInt64(JsonProperty property)
    {
        if (property.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var value))
        {
            return value;
        }

        throw InvalidPropertyType(property, "an integer or null");
    }

    private static double ReadDouble(JsonProperty property)
    {
        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var value))
        {
            return value;
        }

        throw InvalidPropertyType(property, "a number");
    }

    private static bool ReadBoolean(JsonProperty property)
    {
        if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return property.Value.GetBoolean();
        }

        throw InvalidPropertyType(property, "a boolean");
    }

    private static InvalidOperationException InvalidPropertyType(JsonProperty property, string expectedType) =>
        new($"Contraband Cases config property '{property.Name}' must be {expectedType}.");

    private void Validate()
    {
        if (CaseStock <= 0)
        {
            throw new InvalidOperationException("caseStock must be positive.");
        }

        if (!double.IsFinite(AnimationDurationSeconds) || AnimationDurationSeconds <= 0d)
        {
            throw new InvalidOperationException("animationDurationSeconds must be positive and finite.");
        }

        if (FixedCasePrice is <= 0)
        {
            throw new InvalidOperationException("Fixed case price must be positive when set.");
        }

        if (TherapistSellPriceCase is <= 0)
        {
            throw new InvalidOperationException("therapistSellPriceCase must be positive when set.");
        }

        if (TherapistSellPriceKey is <= 0)
        {
            throw new InvalidOperationException("therapistSellPriceKey must be positive when set.");
        }

        if (!double.IsFinite(KeyLootWeightPercent) || KeyLootWeightPercent <= 0d)
        {
            throw new InvalidOperationException("keyLootWeightPercent must be positive and finite.");
        }

        if (!double.IsFinite(CaseLootWeightPercent) || CaseLootWeightPercent < 0d)
        {
            throw new InvalidOperationException("caseLootWeightPercent must be non-negative and finite.");
        }
    }
}
