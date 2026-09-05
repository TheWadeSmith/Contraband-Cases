using Comfort.Common;
using ContrabandCases.Shared.Catalog;
using EFT;
using EFT.InventoryLogic;
using JsonType;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;

namespace ContrabandCases.Client.Catalog;

internal sealed class ClientRewardCatalog
{
    private static readonly string[] RootPropertyNames = ["rewards"];
    private static readonly string[] RewardPropertyNames =
        ["id", "displayName", "weaponTemplateId", "presetId", "rarity", "weight"];

    private readonly IReadOnlyDictionary<string, Item> _presetRoots;

    private ClientRewardCatalog(
        IReadOnlyList<ValidatedReward> rewards,
        IReadOnlyDictionary<string, Item> presetRoots)
    {
        Rewards = rewards;
        _presetRoots = presetRoots;
    }

    public IReadOnlyList<ValidatedReward> Rewards { get; }

    public Item GetPresetRoot(string rewardId) =>
        _presetRoots.TryGetValue(rewardId, out var root)
            ? root
            : throw new InvalidOperationException($"Reward '{rewardId}' has no validated client preset root.");

    public static ClientRewardCatalog Load(string json)
    {
        if (!Singleton<ItemFactory>.Instantiated)
        {
            throw new InvalidOperationException("Tarkov item presets are not ready yet.");
        }

        var presets = Singleton<ItemFactory>.Instance.SavedPresets;
        if (presets is null || presets.Length == 0)
        {
            throw new InvalidOperationException("Tarkov item presets are not ready yet.");
        }

        var resolver = new ClientPresetResolver(presets);
        var rewards = Parse(json).Validate(resolver);
        if (rewards.Count != 12)
        {
            throw new RewardCatalogValidationException("The first-build client catalog must contain exactly twelve rewards.");
        }

        var roots = rewards.ToDictionary(
            reward => reward.Id,
            reward => resolver.GetRoot(reward.PresetId),
            StringComparer.Ordinal);
        return new ClientRewardCatalog(rewards, roots);
    }

    private static RewardCatalog Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new RewardCatalogValidationException("Reward catalog JSON is required.");
        }

        try
        {
            var root = JToken.Parse(json, new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore,
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            });
            if (root is not JObject rootObject)
            {
                throw new RewardCatalogValidationException("Reward catalog must be a JSON object.");
            }

            EnsureKnownProperties(rootObject, RootPropertyNames, "Reward catalog");
            if (rootObject["rewards"] is not JArray rewardsElement)
            {
                throw new RewardCatalogValidationException("Reward catalog must contain a rewards array.");
            }

            var definitions = rewardsElement.Select(ParseReward).ToArray();
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

    private static RewardDefinition ParseReward(JToken element)
    {
        if (element is not JObject reward)
        {
            throw new RewardCatalogValidationException("Each reward must be an object.");
        }

        EnsureKnownProperties(reward, RewardPropertyNames, "Reward");
        var id = RequiredString(reward, "id");
        var displayName = RequiredString(reward, "displayName");
        var weaponTemplateId = RequiredString(reward, "weaponTemplateId");
        var presetId = RequiredString(reward, "presetId");
        var rarityText = RequiredString(reward, "rarity");
        if (!Enum.TryParse<RewardRarity>(rarityText, false, out var rarity) ||
            !string.Equals(rarity.ToString(), rarityText, StringComparison.Ordinal))
        {
            throw new RewardCatalogValidationException($"Reward '{id}' has an unknown rarity.");
        }

        if (reward["weight"] is not JToken weightElement)
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

    private static string RequiredString(JObject element, string propertyName)
    {
        var property = element[propertyName];
        if (property?.Type != JTokenType.String)
        {
            throw new RewardCatalogValidationException($"Reward is missing {propertyName}.");
        }

        return property.Value<string>()!;
    }

    private static double ParseWeight(JToken element, string rewardId)
    {
        if ((element.Type == JTokenType.Integer || element.Type == JTokenType.Float) &&
            double.TryParse(element.ToString(Formatting.None), NumberStyles.Float, CultureInfo.InvariantCulture, out var numericWeight))
        {
            return numericWeight;
        }

        if (element.Type == JTokenType.String &&
            double.TryParse(element.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var stringWeight))
        {
            return stringWeight;
        }

        throw new RewardCatalogValidationException($"Reward '{rewardId}' has an invalid weight.");
    }

    private static void EnsureKnownProperties(
        JObject element,
        IReadOnlyCollection<string> knownPropertyNames,
        string objectName)
    {
        foreach (var property in element.Properties())
        {
            if (!knownPropertyNames.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new RewardCatalogValidationException(
                    $"{objectName} contains unknown property '{property.Name}'.");
            }
        }
    }

    private sealed class ClientPresetResolver : IRewardPresetResolver
    {
        private readonly IReadOnlyDictionary<string, ItemPreset> _presets;
        private readonly Dictionary<string, Item> _roots = new(StringComparer.Ordinal);

        public ClientPresetResolver(IEnumerable<ItemPreset> presets)
        {
            _presets = presets
                .Where(preset => preset is not null && !string.IsNullOrWhiteSpace(preset.Id))
                .GroupBy(preset => preset.Id, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count() == 1
                        ? group.Single()
                        : throw new RewardCatalogValidationException($"Tarkov exposes duplicate preset ID '{group.Key}'."),
                    StringComparer.Ordinal);
        }

        public RewardPresetTree? Resolve(string presetId)
        {
            if (!_presets.TryGetValue(presetId, out var preset) || preset.Item is null)
            {
                return null;
            }

            var items = preset.Item.GetAllItems().ToArray();
            if (items.Length == 0)
            {
                throw new RewardCatalogValidationException($"Preset '{presetId}' contains no items.");
            }

            var ids = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var tree = new RewardPresetTree(items.Select(item =>
            {
                var parentId = item.CurrentAddress?.Container?.ParentItem?.Id;
                return new RewardPresetItem(
                    item.Id,
                    item.StringTemplateId,
                    parentId is not null && ids.Contains(parentId) ? parentId : null);
            }));

            _ = RewardFingerprint.FromPreset(tree);
            _roots[presetId] = preset.Item;
            return tree;
        }

        public Item GetRoot(string presetId) =>
            _roots.TryGetValue(presetId, out var root)
                ? root
                : throw new InvalidOperationException($"Preset '{presetId}' was not validated before use.");
    }
}
