using System.Globalization;
using System.Text;
using System.Text.Json;
using ContrabandCases.Shared.Catalog;

namespace ContrabandCases.Server.Catalog;

public sealed class JsonRewardPackLoader
{
    internal const int MaximumPackSizeBytes = 1_048_576;
    // Includes immutable historical generations; fresh delivery has its own
    // much smaller item budget. Do not delete paid recipes to fit this bound.
    private const int MaximumLots = 256;
    private const int MaximumRecipeLines = 64;
    private const int SchemaVersion = 1;

    private static readonly string[] RootProperties =
    [
        "schemaVersion",
        "providerId",
        "packVersion",
        "displayLabel",
        "providerWeight",
        "requiredTemplateIds",
        "requiredPresetIds",
        "requiredBundleKeys",
        "retiredLotIds",
        "lots"
    ];

    private static readonly string[] LotProperties =
    [
        "lotId",
        "displayName",
        "purpose",
        "familyId",
        "trackId",
        "anchorTemplateId",
        "weight",
        "usePath",
        "recipe",
        "roubleBonus"
    ];

    public CargoLotPack LoadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A reward-pack path is required.", nameof(path));
        }
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new CargoCatalogValidationException("Reward packs must use the .json extension.");
        }

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Reward pack is missing.", path);
        }
        if (file.Length is <= 0 or > MaximumPackSizeBytes)
        {
            throw new CargoCatalogValidationException(
                $"Reward pack '{file.Name}' is empty or exceeds the size limit.");
        }

        string json;
        try
        {
            json = File.ReadAllText(path, new UTF8Encoding(false, true));
        }
        catch (DecoderFallbackException)
        {
            throw new CargoCatalogValidationException(
                $"Reward pack '{file.Name}' is not valid UTF-8.");
        }

        return Load(json, file.Name);
    }

    public CargoLotPack Load(string json, string sourceName = "reward pack")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new CargoCatalogValidationException($"{sourceName} JSON is required.");
        }
        if (Encoding.UTF8.GetByteCount(json) > MaximumPackSizeBytes)
        {
            throw new CargoCatalogValidationException($"{sourceName} exceeds the size limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            var root = RequireObject(document.RootElement, sourceName);
            EnsureKnownProperties(root, RootProperties, sourceName);
            if (RequiredInt32(root, "schemaVersion", sourceName) != SchemaVersion)
            {
                throw new CargoCatalogValidationException(
                    $"{sourceName} uses an unsupported schema version.");
            }

            var providerId = RequiredString(root, "providerId", sourceName);
            ValidateNormalizedId(providerId, "providerId", sourceName);
            var packVersion = RequiredString(root, "packVersion", sourceName);
            ValidateVersion(packVersion, sourceName);
            var displayLabel = RequiredString(root, "displayLabel", sourceName);
            var providerWeight = RequiredDouble(root, "providerWeight", sourceName);
            var templateIds = RequiredStringArray(
                root,
                "requiredTemplateIds",
                sourceName,
                maximum: 256);
            var presetIds = RequiredStringArray(
                root,
                "requiredPresetIds",
                sourceName,
                maximum: 256);
            var bundleKeys = RequiredStringArray(
                root,
                "requiredBundleKeys",
                sourceName,
                maximum: 256);
            var lotsElement = RequiredArray(root, "lots", sourceName);
            var lots = lotsElement.EnumerateArray()
                .Take(MaximumLots + 1)
                .Select((element, index) => ParseLot(
                    element,
                    providerId,
                    packVersion,
                    $"{sourceName} lot {index}"))
                .ToArray();
            if (lots.Length is 0 or > MaximumLots)
            {
                throw new CargoCatalogValidationException(
                    $"{sourceName} must contain between 1 and {MaximumLots} lots.");
            }

            return new CargoLotPack(
                providerId,
                packVersion,
                displayLabel,
                providerWeight,
                templateIds,
                presetIds,
                bundleKeys,
                lots,
                root.TryGetProperty("retiredLotIds", out _)
                    ? RequiredStringArray(root, "retiredLotIds", sourceName, maximum: MaximumLots)
                    : []);
        }
        catch (CargoCatalogValidationException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new CargoCatalogValidationException($"{sourceName} is not strict valid JSON.");
        }
        catch (FormatException)
        {
            throw new CargoCatalogValidationException($"{sourceName} contains invalid numeric data.");
        }
        catch (OverflowException)
        {
            throw new CargoCatalogValidationException($"{sourceName} contains out-of-range numeric data.");
        }
    }

    private static CargoLotDefinition ParseLot(
        JsonElement element,
        string providerId,
        string packVersion,
        string context)
    {
        var lot = RequireObject(element, context);
        EnsureKnownProperties(lot, LotProperties, context);
        var lotId = RequiredString(lot, "lotId", context);
        ValidateNormalizedId(lotId, "lotId", context);
        var displayName = RequiredString(lot, "displayName", context);
        var purpose = RequiredString(lot, "purpose", context);
        var familyId = RequiredString(lot, "familyId", context);
        ValidateNormalizedId(familyId, "familyId", context);
        var trackId = RequiredString(lot, "trackId", context);
        ValidateNormalizedId(trackId, "trackId", context);
        var anchorTemplateId = RequiredString(lot, "anchorTemplateId", context);
        var weight = RequiredDouble(lot, "weight", context);
        var usePath = ParseUsePath(RequiredObject(lot, "usePath", context), context);
        var recipeElement = RequiredArray(lot, "recipe", context);
        var recipe = recipeElement.EnumerateArray()
            .Take(MaximumRecipeLines + 1)
            .Select((line, index) => ParseRecipeLine(line, $"{context} recipe {index}"))
            .ToArray();
        if (recipe.Length is 0 or > MaximumRecipeLines)
        {
            throw new CargoCatalogValidationException(
                $"{context} must contain between 1 and {MaximumRecipeLines} recipe lines.");
        }

        return new CargoLotDefinition(
            providerId,
            packVersion,
            lotId,
            displayName,
            purpose,
            new FamilyId(familyId),
            new TrackId(trackId),
            anchorTemplateId,
            weight,
            usePath,
            recipe,
            lot.TryGetProperty("roubleBonus", out _) ? RequiredInt32(lot, "roubleBonus", context) : 0);
    }

    private static UsePath ParseUsePath(JsonElement element, string context)
    {
        var kind = RequiredString(element, "kind", context);
        return kind switch
        {
            "raidRole" => ParseRaidRole(element, context),
            "collection" => ParseCollection(element, context),
            "craft" => ParseCraft(element, context),
            "barter" => ParseBarter(element, context),
            _ => throw new CargoCatalogValidationException(
                $"{context} contains unsupported use-path kind '{kind}'.")
        };
    }

    private static RaidRole ParseRaidRole(JsonElement element, string context)
    {
        EnsureKnownProperties(element, ["kind", "roleId"], $"{context} usePath");
        return new RaidRole(RequiredString(element, "roleId", context));
    }

    private static Collection ParseCollection(JsonElement element, string context)
    {
        EnsureKnownProperties(
            element,
            ["kind", "containerTemplateId", "collectionId"],
            $"{context} usePath");
        return new Collection(
            RequiredString(element, "containerTemplateId", context),
            RequiredString(element, "collectionId", context));
    }

    private static Craft ParseCraft(JsonElement element, string context)
    {
        EnsureKnownProperties(element, ["kind", "productionId"], $"{context} usePath");
        return new Craft(RequiredString(element, "productionId", context));
    }

    private static Barter ParseBarter(JsonElement element, string context)
    {
        EnsureKnownProperties(
            element,
            ["kind", "traderId", "assortId"],
            $"{context} usePath");
        return new Barter(
            RequiredString(element, "traderId", context),
            RequiredString(element, "assortId", context));
    }

    private static RewardRecipeLine ParseRecipeLine(JsonElement element, string context)
    {
        var line = RequireObject(element, context);
        var kind = RequiredString(line, "kind", context);
        return kind switch
        {
            "template" => ParseTemplateLine(line, context),
            "preset" => ParsePresetLine(line, context),
            _ => throw new CargoCatalogValidationException(
                $"{context} contains unsupported recipe kind '{kind}'.")
        };
    }

    private static TemplateLine ParseTemplateLine(JsonElement element, string context)
    {
        EnsureKnownProperties(
            element,
            ["kind", "templateId", "instanceCount", "stackCountPerInstance"],
            context);
        return new TemplateLine(
            RequiredString(element, "templateId", context),
            RequiredInt32(element, "instanceCount", context),
            RequiredInt32(element, "stackCountPerInstance", context));
    }

    private static PresetLine ParsePresetLine(JsonElement element, string context)
    {
        EnsureKnownProperties(element, ["kind", "presetId", "omitRootSlots"], context);
        return new PresetLine(RequiredString(element, "presetId", context),
            element.TryGetProperty("omitRootSlots", out _)
                ? RequiredStringArray(element, "omitRootSlots", context, maximum: 32) : []);
    }

    private static JsonElement RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new CargoCatalogValidationException($"{context} must be a JSON object.");
        }

        return element;
    }

    private static JsonElement RequiredObject(
        JsonElement parent,
        string propertyName,
        string context)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            throw new CargoCatalogValidationException(
                $"{context} is missing '{propertyName}'.");
        }

        return RequireObject(value, $"{context} {propertyName}");
    }

    private static JsonElement RequiredArray(
        JsonElement parent,
        string propertyName,
        string context)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new CargoCatalogValidationException(
                $"{context} is missing array '{propertyName}'.");
        }

        return value;
    }

    private static string RequiredString(
        JsonElement parent,
        string propertyName,
        string context)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new CargoCatalogValidationException(
                $"{context} is missing string '{propertyName}'.");
        }

        return value.GetString()!;
    }

    private static int RequiredInt32(
        JsonElement parent,
        string propertyName,
        string context)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
        {
            throw new CargoCatalogValidationException(
                $"{context} is missing integer '{propertyName}'.");
        }

        return result;
    }

    private static double RequiredDouble(
        JsonElement parent,
        string propertyName,
        string context)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var result) ||
            !double.IsFinite(result))
        {
            throw new CargoCatalogValidationException(
                $"{context} is missing finite number '{propertyName}'.");
        }

        return result;
    }

    private static IReadOnlyList<string> RequiredStringArray(
        JsonElement parent,
        string propertyName,
        string context,
        int maximum)
    {
        var array = RequiredArray(parent, propertyName, context);
        var result = array.EnumerateArray()
            .Take(maximum + 1)
            .Select(value =>
            {
                if (value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(value.GetString()))
                {
                    throw new CargoCatalogValidationException(
                        $"{context} array '{propertyName}' contains a non-string or blank value.");
                }

                return value.GetString()!;
            })
            .ToArray();
        if (result.Length > maximum)
        {
            throw new CargoCatalogValidationException(
                $"{context} array '{propertyName}' is excessive.");
        }

        return result;
    }

    private static void EnsureKnownProperties(
        JsonElement element,
        IReadOnlyCollection<string> known,
        string context)
    {
        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!encountered.Add(property.Name))
            {
                throw new CargoCatalogValidationException(
                    $"{context} contains duplicate property '{property.Name}'.");
            }
            if (!known.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new CargoCatalogValidationException(
                    $"{context} contains unknown property '{property.Name}'.");
            }
        }
    }

    private static void ValidateNormalizedId(string value, string propertyName, string context)
    {
        if (value.Length > 64 ||
            value[0] is '.' or '-' ||
            value[^1] is '.' or '-' ||
            value.Any(character => character is not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and not '.' and not '-'))
        {
            throw new CargoCatalogValidationException(
                $"{context} has non-normalized {propertyName} '{value}'.");
        }
    }

    private static void ValidateVersion(string value, string context)
    {
        var components = value.Split('.');
        if (components.Length != 3 ||
            components.Any(component =>
                component.Length == 0 ||
                !int.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            throw new CargoCatalogValidationException(
                $"{context} has invalid packVersion '{value}'.");
        }
    }
}
