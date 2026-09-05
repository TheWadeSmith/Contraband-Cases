using System.Collections.ObjectModel;

namespace ContrabandCases.Shared.Catalog;

public sealed class CargoCatalogValidationException : InvalidOperationException
{
    public CargoCatalogValidationException(string message)
        : base(message)
    {
    }
}

public sealed class FamilyId : IEquatable<FamilyId>, IComparable<FamilyId>
{
    public FamilyId(string value)
    {
        Value = CargoDomainValidator.RequireIdentifier(value, nameof(value));
    }

    public string Value { get; }

    public bool Equals(FamilyId? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as FamilyId);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(FamilyId? other) =>
        other is null ? 1 : StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value;
}

public sealed class TrackId : IEquatable<TrackId>, IComparable<TrackId>
{
    public TrackId(string value)
    {
        Value = CargoDomainValidator.RequireIdentifier(value, nameof(value));
    }

    public string Value { get; }

    public bool Equals(TrackId? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as TrackId);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(TrackId? other) =>
        other is null ? 1 : StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value;
}

public abstract class UsePath
{
    internal UsePath()
    {
    }
}

public sealed class RaidRole : UsePath
{
    public RaidRole(string roleId)
    {
        RoleId = CargoDomainValidator.RequireIdentifier(roleId, nameof(roleId));
    }

    public string RoleId { get; }
}

public sealed class Collection : UsePath
{
    public Collection(string containerTemplateId, string collectionId)
    {
        ContainerTemplateId = CargoDomainValidator.RequireIdentifier(containerTemplateId, nameof(containerTemplateId));
        CollectionId = CargoDomainValidator.RequireIdentifier(collectionId, nameof(collectionId));
    }

    public string ContainerTemplateId { get; }

    public string CollectionId { get; }
}

public sealed class Craft : UsePath
{
    public Craft(string productionId)
    {
        ProductionId = CargoDomainValidator.RequireIdentifier(productionId, nameof(productionId));
    }

    public string ProductionId { get; }
}

public sealed class Barter : UsePath
{
    public Barter(string traderId, string assortId)
    {
        TraderId = CargoDomainValidator.RequireIdentifier(traderId, nameof(traderId));
        AssortId = CargoDomainValidator.RequireIdentifier(assortId, nameof(assortId));
    }

    public string TraderId { get; }

    public string AssortId { get; }
}

public abstract class RewardRecipeLine
{
    internal RewardRecipeLine()
    {
    }
}

public sealed class TemplateLine : RewardRecipeLine
{
    public TemplateLine(string templateId, int instanceCount, int stackCountPerInstance)
    {
        TemplateId = CargoDomainValidator.RequireIdentifier(templateId, nameof(templateId));
        InstanceCount = CargoDomainValidator.RequirePositive(instanceCount, nameof(instanceCount));
        StackCountPerInstance = CargoDomainValidator.RequirePositive(stackCountPerInstance, nameof(stackCountPerInstance));
    }

    public string TemplateId { get; }

    public int InstanceCount { get; }

    public int StackCountPerInstance { get; }
}

public sealed class PresetLine : RewardRecipeLine
{
    public PresetLine(string presetId)
    {
        PresetId = CargoDomainValidator.RequireIdentifier(presetId, nameof(presetId));
    }

    public string PresetId { get; }
}

public sealed class CargoLotDefinition
{
    public CargoLotDefinition(
        string providerId,
        string packVersion,
        string lotId,
        string displayName,
        string purpose,
        FamilyId familyId,
        TrackId trackId,
        string anchorTemplateId,
        double weight,
        UsePath usePath,
        IEnumerable<RewardRecipeLine> recipeLines)
    {
        ProviderId = CargoDomainValidator.RequireIdentifier(providerId, nameof(providerId));
        PackVersion = CargoDomainValidator.RequireIdentifier(packVersion, nameof(packVersion));
        LotId = CargoDomainValidator.RequireIdentifier(lotId, nameof(lotId));
        DisplayName = CargoDomainValidator.RequireText(displayName, nameof(displayName));
        Purpose = CargoDomainValidator.RequireText(purpose, nameof(purpose));
        FamilyId = familyId ?? throw new CargoCatalogValidationException("A family ID is required.");
        TrackId = trackId ?? throw new CargoCatalogValidationException("A track ID is required.");
        AnchorTemplateId = CargoDomainValidator.RequireIdentifier(anchorTemplateId, nameof(anchorTemplateId));
        Weight = CargoDomainValidator.RequirePositiveFinite(weight, nameof(weight));
        UsePath = usePath ?? throw new CargoCatalogValidationException("A use path is required.");

        if (recipeLines is null)
        {
            throw new CargoCatalogValidationException("Recipe lines are required.");
        }

        var lines = recipeLines.ToArray();
        if (lines.Length == 0)
        {
            throw new CargoCatalogValidationException("A cargo lot must contain at least one recipe line.");
        }
        if (lines.Any(line => line is null))
        {
            throw new CargoCatalogValidationException("A cargo lot cannot contain a null recipe line.");
        }

        RecipeLines = new ReadOnlyCollection<RewardRecipeLine>(lines);
    }

    public string ProviderId { get; }

    public string PackVersion { get; }

    public string LotId { get; }

    public string DisplayName { get; }

    public string Purpose { get; }

    public FamilyId FamilyId { get; }

    public TrackId TrackId { get; }

    public string AnchorTemplateId { get; }

    public double Weight { get; }

    public UsePath UsePath { get; }

    public IReadOnlyList<RewardRecipeLine> RecipeLines { get; }
}
