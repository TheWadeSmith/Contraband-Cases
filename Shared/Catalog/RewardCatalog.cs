using System.Collections.ObjectModel;

namespace ContrabandCases.Shared.Catalog;

public enum RewardRarity
{
    ScavGrade,
    Contractor,
    Restricted,
    BlackLabel,
    // This is deliberately appended instead of inserted. Existing rarity
    // names and numeric values are persisted in strict client/server wire
    // records and historical journals.
    Uncommon
}

public sealed class RewardRarityInfo
{
    public RewardRarityInfo(string displayName, string displayColor)
    {
        DisplayName = displayName;
        DisplayColor = displayColor;
    }

    public string DisplayName { get; }

    public string DisplayColor { get; }
}

public static class RewardRarities
{
    public static string Rank(RewardRarity rarity) => rarity switch
    {
        RewardRarity.ScavGrade => "I",
        RewardRarity.Uncommon => "II",
        RewardRarity.Contractor => "III",
        RewardRarity.Restricted => "IV",
        RewardRarity.BlackLabel => "V",
        _ => throw new ArgumentOutOfRangeException(nameof(rarity))
    };

    public static RewardRarityInfo GetInfo(RewardRarity rarity)
    {
        return rarity switch
        {
            RewardRarity.ScavGrade => new RewardRarityInfo("Common", "gray"),
            RewardRarity.Uncommon => new RewardRarityInfo("Uncommon", "green"),
            RewardRarity.Contractor => new RewardRarityInfo("Rare", "blue"),
            RewardRarity.Restricted => new RewardRarityInfo("Epic", "violet"),
            RewardRarity.BlackLabel => new RewardRarityInfo("Legendary", "amber"),
            _ => throw new ArgumentOutOfRangeException(nameof(rarity))
        };
    }
}

public sealed class RewardCatalogValidationException : InvalidOperationException
{
    public RewardCatalogValidationException(string message)
        : base(message)
    {
    }

    public RewardCatalogValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class RewardDefinition
{
    public RewardDefinition(
        string id,
        string displayName,
        string weaponTemplateId,
        string presetId,
        RewardRarity rarity,
        double weight)
    {
        Id = id;
        DisplayName = displayName;
        WeaponTemplateId = weaponTemplateId;
        PresetId = presetId;
        Rarity = rarity;
        Weight = weight;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string WeaponTemplateId { get; }

    public string PresetId { get; }

    public RewardRarity Rarity { get; }

    public double Weight { get; }
}

public sealed class RewardPresetItem
{
    public RewardPresetItem(string id, string templateId, string? parentId)
    {
        Id = id;
        TemplateId = templateId;
        ParentId = parentId;
    }

    public string Id { get; }

    public string TemplateId { get; }

    public string? ParentId { get; }
}

public sealed class RewardPresetTree
{
    public RewardPresetTree(IEnumerable<RewardPresetItem> items, long handbookValue = 0)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }
        if (handbookValue < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(handbookValue));
        }

        Items = new ReadOnlyCollection<RewardPresetItem>(items.ToArray());
        HandbookValue = handbookValue;
    }

    public IReadOnlyList<RewardPresetItem> Items { get; }

    public long HandbookValue { get; }
}

public interface IRewardPresetResolver
{
    RewardPresetTree? Resolve(string presetId);
}

public sealed class ValidatedReward
{
    internal ValidatedReward(RewardDefinition definition, long handbookValue, RewardFingerprint fingerprint)
    {
        Id = definition.Id;
        DisplayName = definition.DisplayName;
        WeaponTemplateId = definition.WeaponTemplateId;
        Rarity = definition.Rarity;
        Weight = definition.Weight;
        HandbookValue = handbookValue;
        Fingerprint = fingerprint;
        PresetId = definition.PresetId;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string WeaponTemplateId { get; }

    public RewardRarity Rarity { get; }

    public double Weight { get; }

    public long HandbookValue { get; }

    public RewardFingerprint Fingerprint { get; }

    public string PresetId { get; }
}

public sealed class RewardCatalog
{
    private const double WeightTolerance = 1e-9;

    private RewardCatalog(IReadOnlyList<RewardDefinition> rewards)
    {
        Rewards = rewards;
    }

    public IReadOnlyList<RewardDefinition> Rewards { get; }

    public static RewardCatalog Create(IEnumerable<RewardDefinition> rewards)
    {
        if (rewards is null)
        {
            throw new ArgumentNullException(nameof(rewards));
        }

        var definitions = rewards.ToArray();
        if (definitions.Length == 0)
        {
            throw new RewardCatalogValidationException("Reward catalog cannot be empty.");
        }

        foreach (var definition in definitions)
        {
            if (definition is null)
            {
                throw new RewardCatalogValidationException("Reward catalog cannot contain a null reward.");
            }

            RequireValue(definition.Id, "id");
            RequireValue(definition.DisplayName, "displayName");
            RequireValue(definition.WeaponTemplateId, "weaponTemplateId");
            RequireValue(definition.PresetId, "presetId");
            if (!Enum.IsDefined(typeof(RewardRarity), definition.Rarity))
            {
                throw new RewardCatalogValidationException($"Reward '{definition.Id}' has an unknown rarity.");
            }
        }

        var duplicateId = definitions
            .GroupBy(definition => definition.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicateId is not null)
        {
            throw new RewardCatalogValidationException($"Duplicate reward ID '{duplicateId.Key}'.");
        }

        return new RewardCatalog(new ReadOnlyCollection<RewardDefinition>(definitions));
    }

    public IReadOnlyList<ValidatedReward> Validate(IRewardPresetResolver resolver)
    {
        if (resolver is null)
        {
            throw new ArgumentNullException(nameof(resolver));
        }

        var totalWeight = 0d;
        var validated = new List<ValidatedReward>(Rewards.Count);
        var fingerprints = new HashSet<RewardFingerprint>();

        foreach (var reward in Rewards)
        {
            if (!double.IsFinite(reward.Weight) || reward.Weight <= 0d)
            {
                throw new RewardCatalogValidationException($"Reward '{reward.Id}' has an invalid weight.");
            }

            totalWeight += reward.Weight;
            var preset = resolver.Resolve(reward.PresetId);
            if (preset is null)
            {
                throw new RewardCatalogValidationException($"Reward '{reward.Id}' references a missing preset.");
            }

            var fingerprint = RewardFingerprint.FromPreset(preset);
            if (!string.Equals(fingerprint.RootTemplateId, reward.WeaponTemplateId, StringComparison.Ordinal))
            {
                throw new RewardCatalogValidationException($"Reward '{reward.Id}' preset root does not match its weapon template.");
            }

            if (!fingerprints.Add(fingerprint))
            {
                throw new RewardCatalogValidationException($"Reward '{reward.Id}' duplicates a preset fingerprint.");
            }

            validated.Add(new ValidatedReward(reward, preset.HandbookValue, fingerprint));
        }

        if (Math.Abs(totalWeight - 1d) > WeightTolerance)
        {
            throw new RewardCatalogValidationException("Reward weights must total 1.0.");
        }

        return new ReadOnlyCollection<ValidatedReward>(validated);
    }

    private static void RequireValue(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RewardCatalogValidationException($"Reward is missing {propertyName}.");
        }
    }
}
