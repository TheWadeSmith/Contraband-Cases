using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Cloners;

namespace ContrabandCases.Server.Catalog;

public interface IRelayRewardCatalog
{
    IReadOnlyList<ValidatedReward> Rewards { get; }
    ValidatedReward FindReward(string rewardId);
}

internal sealed class SptRewardPresetResolver : IRewardPresetResolver
{
    private readonly Func<MongoId, bool> _isPreset;
    private readonly Func<MongoId, Preset?> _getPreset;
    private readonly Func<IEnumerable<Item>, double> _getHandbookValue;
    private readonly Dictionary<string, ResolvedPreset> _resolved = new(StringComparer.Ordinal);

    public SptRewardPresetResolver(PresetHelper presetHelper, HandbookHelper handbookHelper)
        : this(presetHelper.IsPreset, presetHelper.GetPreset, handbookHelper.GetTemplatePriceForItems)
    {
    }

    internal SptRewardPresetResolver(
        Func<MongoId, bool> isPreset,
        Func<MongoId, Preset?> getPreset,
        Func<IEnumerable<Item>, double> getHandbookValue)
    {
        _isPreset = isPreset ?? throw new ArgumentNullException(nameof(isPreset));
        _getPreset = getPreset ?? throw new ArgumentNullException(nameof(getPreset));
        _getHandbookValue = getHandbookValue ?? throw new ArgumentNullException(nameof(getHandbookValue));
    }

    public RewardPresetTree? Resolve(string presetId)
    {
        if (!IsMongoId(presetId))
        {
            return null;
        }

        if (_resolved.TryGetValue(presetId, out var cached))
        {
            return cached.Tree;
        }

        var mongoId = (MongoId)presetId;
        if (!_isPreset(mongoId))
        {
            return null;
        }

        Preset preset;
        try
        {
            preset = _getPreset(mongoId)
                ?? throw new RewardCatalogValidationException($"Preset '{presetId}' resolved to null.");
        }
        catch (RewardCatalogValidationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RewardCatalogValidationException($"Preset '{presetId}' could not be loaded.", exception);
        }

        if (preset.Items is null || preset.Items.Count == 0)
        {
            throw new RewardCatalogValidationException($"Preset '{presetId}' contains no items.");
        }

        var roots = preset.Items.Where(item => item.Id == preset.Parent).ToArray();
        if (roots.Length != 1)
        {
            throw new RewardCatalogValidationException($"Preset '{presetId}' does not declare exactly one root item.");
        }

        var rawValue = _getHandbookValue(preset.Items);
        if (!double.IsFinite(rawValue) || rawValue < 0d || rawValue > long.MaxValue)
        {
            throw new RewardCatalogValidationException($"Preset '{presetId}' has an invalid handbook value.");
        }

        var handbookValue = checked((long)Math.Round(rawValue, MidpointRounding.AwayFromZero));
        var normalizedItems = preset.Items
            .OrderByDescending(item => item.Id == preset.Parent)
            .ToList();
        normalizedItems[0].ParentId = null;
        var tree = new RewardPresetTree(
            normalizedItems.Select(item => new RewardPresetItem(
                item.Id.ToString(),
                item.Template.ToString(),
                item.ParentId)),
            handbookValue);
        _resolved.Add(presetId, new ResolvedPreset(tree, normalizedItems));
        return tree;
    }

    public IReadOnlyList<Item> GetResolvedItems(string presetId)
    {
        if (!_resolved.TryGetValue(presetId, out var preset))
        {
            throw new InvalidOperationException($"Preset '{presetId}' was not validated before use.");
        }

        return preset.Items;
    }

    private static bool IsMongoId(string? value) =>
        value is { Length: 24 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private sealed record ResolvedPreset(RewardPresetTree Tree, IReadOnlyList<Item> Items);
}

[Injectable(InjectionType.Singleton)]
public sealed class ServerRewardCatalog : IRelayRewardCatalog
{
    private static readonly string[] RootPropertyNames = ["rewards"];
    private static readonly string[] RewardPropertyNames =
        ["id", "displayName", "weaponTemplateId", "presetId", "rarity", "weight"];

    private readonly SptRewardPresetResolver _resolver;
    private readonly ICloner _cloner;
    private IReadOnlyList<ValidatedReward>? _rewards;
    private IReadOnlyDictionary<string, IReadOnlyList<Item>>? _presetItemsByRewardId;

    public ServerRewardCatalog(PresetHelper presetHelper, HandbookHelper handbookHelper, ICloner cloner)
    {
        _resolver = new SptRewardPresetResolver(presetHelper, handbookHelper);
        _cloner = cloner ?? throw new ArgumentNullException(nameof(cloner));
    }

    public IReadOnlyList<ValidatedReward> Rewards =>
        _rewards ?? throw new InvalidOperationException("The server reward catalog has not been loaded.");

    public void Load(string json)
    {
        if (_rewards is not null)
        {
            throw new InvalidOperationException("The server reward catalog can only be loaded once.");
        }

        var validated = Parse(json).Validate(_resolver);
        EnsureFirstBuildCatalog(validated);

        var presets = new Dictionary<string, IReadOnlyList<Item>>(StringComparer.Ordinal);
        foreach (var reward in validated)
        {
            var items = _resolver.GetResolvedItems(reward.PresetId);
            presets.Add(reward.Id, new ReadOnlyCollection<Item>(items.ToArray()));
        }

        _rewards = validated;
        _presetItemsByRewardId = new ReadOnlyDictionary<string, IReadOnlyList<Item>>(presets);
    }

    internal static RewardCatalog Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new RewardCatalogValidationException("Reward catalog JSON is required.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new RewardCatalogValidationException("Reward catalog must be a JSON object.");
            }

            EnsureKnownProperties(root, RootPropertyNames, "Reward catalog");
            if (!root.TryGetProperty("rewards", out var rewardsElement) ||
                rewardsElement.ValueKind != JsonValueKind.Array)
            {
                throw new RewardCatalogValidationException("Reward catalog must contain a rewards array.");
            }

            var definitions = new List<RewardDefinition>();
            foreach (var rewardElement in rewardsElement.EnumerateArray())
            {
                definitions.Add(ParseReward(rewardElement));
            }

            return RewardCatalog.Create(definitions);
        }
        catch (RewardCatalogValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new RewardCatalogValidationException("Reward catalog is not valid JSON.", exception);
        }
    }

    internal static void EnsureFirstBuildCatalog(IReadOnlyList<ValidatedReward> rewards)
    {
        ArgumentNullException.ThrowIfNull(rewards);
        if (rewards.Count != 12)
        {
            throw new RewardCatalogValidationException("The first-build server catalog must contain exactly twelve rewards.");
        }
    }

    public List<Item> ClonePresetItems(string rewardId)
    {
        if (_presetItemsByRewardId is null || !_presetItemsByRewardId.TryGetValue(rewardId, out var items))
        {
            throw new InvalidOperationException($"Reward '{rewardId}' is not in the loaded server catalog.");
        }

        return _cloner.Clone(items.ToList())
            ?? throw new InvalidOperationException($"Reward '{rewardId}' preset could not be cloned.");
    }

    public ValidatedReward FindReward(string rewardId)
    {
        if (string.IsNullOrWhiteSpace(rewardId))
        {
            throw new ArgumentException("A reward ID is required.", nameof(rewardId));
        }

        return Rewards.SingleOrDefault(reward => string.Equals(reward.Id, rewardId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Reward '{rewardId}' is not in the loaded server catalog.");
    }

    public ValidatedReward SelectRelayTarget(
        string inputRewardId,
        RelayOutcome outcome,
        double unitValue,
        RarityLadderVersion rarityLadderVersion = RarityLadderVersion.FiveTier)
    {
        var input = FindReward(inputRewardId);
        var candidates = outcome switch
        {
            RelayOutcome.RarityUpgrade => Rewards
                .Where(reward => reward.Rarity == RelayRules.GetUpgradeRarity(
                    input.Rarity,
                    rarityLadderVersion))
                .ToArray(),
            RelayOutcome.SameRaritySidegrade => Rewards
                .Where(reward => reward.Rarity == input.Rarity &&
                    !string.Equals(reward.Id, input.Id, StringComparison.Ordinal))
                .ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Only reward-producing Relay outcomes select a target.")
        };
        if (candidates.Length == 0)
        {
            throw new InvalidOperationException($"Reward '{inputRewardId}' has no target for Relay outcome '{outcome}'.");
        }

        return SelectNormalized(candidates, unitValue);
    }

    internal static ValidatedReward SelectNormalized(IReadOnlyList<ValidatedReward> rewards, double unitValue)
    {
        if (rewards is null || rewards.Count == 0)
        {
            throw new ArgumentException("At least one Relay target is required.", nameof(rewards));
        }
        if (!double.IsFinite(unitValue) || unitValue < 0d || unitValue >= 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(unitValue));
        }

        var totalWeight = rewards.Sum(reward => reward.Weight);
        var threshold = unitValue * totalWeight;
        var cumulative = 0d;
        foreach (var reward in rewards)
        {
            cumulative += reward.Weight;
            if (threshold < cumulative)
            {
                return reward;
            }
        }

        return rewards[^1];
    }

    private static RewardDefinition ParseReward(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new RewardCatalogValidationException("Each reward must be an object.");
        }

        EnsureKnownProperties(element, RewardPropertyNames, "Reward");
        var id = RequiredString(element, "id");
        var displayName = RequiredString(element, "displayName");
        var weaponTemplateId = RequiredString(element, "weaponTemplateId");
        var presetId = RequiredString(element, "presetId");
        var rarityText = RequiredString(element, "rarity");
        if (!Enum.TryParse<RewardRarity>(rarityText, false, out var rarity) ||
            !string.Equals(rarity.ToString(), rarityText, StringComparison.Ordinal))
        {
            throw new RewardCatalogValidationException($"Reward '{id}' has an unknown rarity.");
        }

        if (!element.TryGetProperty("weight", out var weightElement))
        {
            throw new RewardCatalogValidationException($"Reward '{id}' is missing weight.");
        }

        return new RewardDefinition(
            id,
            displayName,
            weaponTemplateId,
            presetId,
            rarity,
            ParseWeight(weightElement, id));
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new RewardCatalogValidationException($"Reward is missing {propertyName}.");
        }

        return property.GetString()!;
    }

    private static double ParseWeight(JsonElement element, string rewardId)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var numericWeight))
        {
            return numericWeight;
        }

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var stringWeight))
        {
            return stringWeight;
        }

        throw new RewardCatalogValidationException($"Reward '{rewardId}' has an invalid weight.");
    }

    private static void EnsureKnownProperties(
        JsonElement element,
        IReadOnlyCollection<string> knownPropertyNames,
        string objectName)
    {
        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!encountered.Add(property.Name))
            {
                throw new RewardCatalogValidationException(
                    $"{objectName} contains duplicate property '{property.Name}'.");
            }

            if (!knownPropertyNames.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new RewardCatalogValidationException(
                    $"{objectName} contains unknown property '{property.Name}'.");
            }
        }
    }
}
