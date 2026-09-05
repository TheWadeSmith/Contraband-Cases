using System.Collections.ObjectModel;
using System.Text;

namespace ContrabandCases.Shared.Catalog;

public enum CanonicalRotation
{
    Horizontal,
    Vertical
}

public enum RewardResourceKind
{
    MedKit,
    RepairKit,
    FoodDrink,
    Generic
}

public sealed class CanonicalInternalLocation : IEquatable<CanonicalInternalLocation>
{
    public CanonicalInternalLocation(int x, int y, CanonicalRotation rotation)
    {
        if (x < 0)
        {
            throw new CargoCatalogValidationException("Internal location X cannot be negative.");
        }
        if (y < 0)
        {
            throw new CargoCatalogValidationException("Internal location Y cannot be negative.");
        }
        if (!Enum.IsDefined(typeof(CanonicalRotation), rotation))
        {
            throw new CargoCatalogValidationException("Internal location rotation is unknown.");
        }

        X = x;
        Y = y;
        Rotation = rotation;
    }

    public int X { get; }

    public int Y { get; }

    public CanonicalRotation Rotation { get; }

    public bool Equals(CanonicalInternalLocation? other) =>
        other is not null && X == other.X && Y == other.Y && Rotation == other.Rotation;

    public override bool Equals(object? obj) => Equals(obj as CanonicalInternalLocation);

    public override int GetHashCode() => HashCode.Combine(X, Y, Rotation);
}

public sealed class RewardStableState : IEquatable<RewardStableState>
{
    public RewardStableState(
        decimal? durability = null,
        decimal? maximumDurability = null,
        decimal? resourceValue = null,
        decimal? maximumResourceValue = null,
        RewardResourceKind? resourceKind = null)
    {
        CargoDomainValidator.RequireOptionalNonNegative(durability, nameof(durability));
        CargoDomainValidator.RequireOptionalNonNegative(maximumDurability, nameof(maximumDurability));
        CargoDomainValidator.RequireOptionalNonNegative(resourceValue, nameof(resourceValue));
        CargoDomainValidator.RequireOptionalNonNegative(maximumResourceValue, nameof(maximumResourceValue));
        CargoDomainValidator.RequireNotGreaterThan(durability, maximumDurability, "Durability");
        CargoDomainValidator.RequireNotGreaterThan(resourceValue, maximumResourceValue, "Resource value");
        if (resourceKind is not null &&
            !Enum.IsDefined(typeof(RewardResourceKind), resourceKind.Value))
        {
            throw new CargoCatalogValidationException("Resource kind is unknown.");
        }
        if (resourceKind is null &&
            (resourceValue is not null || maximumResourceValue is not null))
        {
            throw new CargoCatalogValidationException(
                "Resource state requires an exact resource kind.");
        }
        if (resourceKind is not null &&
            (resourceValue is null || maximumResourceValue is null))
        {
            throw new CargoCatalogValidationException(
                "Resource state requires both its value and finalized maximum.");
        }
        if (durability is null && maximumDurability is null && resourceValue is null && maximumResourceValue is null)
        {
            throw new CargoCatalogValidationException("Stable state must contain at least one allowlisted value.");
        }

        Durability = durability;
        MaximumDurability = maximumDurability;
        ResourceValue = resourceValue;
        MaximumResourceValue = maximumResourceValue;
        ResourceKind = resourceKind;
    }

    public decimal? Durability { get; }

    public decimal? MaximumDurability { get; }

    public decimal? ResourceValue { get; }

    public decimal? MaximumResourceValue { get; }

    public RewardResourceKind? ResourceKind { get; }

    public bool Equals(RewardStableState? other) =>
        other is not null &&
        Durability == other.Durability &&
        MaximumDurability == other.MaximumDurability &&
        ResourceValue == other.ResourceValue &&
        MaximumResourceValue == other.MaximumResourceValue &&
        ResourceKind == other.ResourceKind;

    public override bool Equals(object? obj) => Equals(obj as RewardStableState);

    public override int GetHashCode() =>
        HashCode.Combine(
            Durability,
            MaximumDurability,
            ResourceValue,
            MaximumResourceValue,
            ResourceKind);
}

public sealed class RewardForestNode
{
    public RewardForestNode(
        string treeRootPath,
        string logicalPath,
        string templateId,
        string? parentLogicalPath,
        string? slotId,
        CanonicalInternalLocation? internalLocation,
        int stackCount,
        RewardStableState? stableState = null)
    {
        TreeRootPath = CargoDomainValidator.RequireIdentifier(treeRootPath, nameof(treeRootPath));
        LogicalPath = CargoDomainValidator.RequireIdentifier(logicalPath, nameof(logicalPath));
        TemplateId = CargoDomainValidator.RequireIdentifier(templateId, nameof(templateId));
        ParentLogicalPath = CargoDomainValidator.OptionalIdentifier(parentLogicalPath, nameof(parentLogicalPath));
        SlotId = CargoDomainValidator.OptionalIdentifier(slotId, nameof(slotId));
        StackCount = CargoDomainValidator.RequirePositive(stackCount, nameof(stackCount));

        if (ParentLogicalPath is null)
        {
            if (SlotId is not null || internalLocation is not null)
            {
                throw new CargoCatalogValidationException("A forest root cannot have an internal slot or location.");
            }
        }
        else if (SlotId is null)
        {
            throw new CargoCatalogValidationException("A child node requires a slot ID.");
        }

        InternalLocation = internalLocation;
        StableState = stableState;
    }

    public string TreeRootPath { get; }

    public string LogicalPath { get; }

    public string TemplateId { get; }

    public string? ParentLogicalPath { get; }

    public string? SlotId { get; }

    public CanonicalInternalLocation? InternalLocation { get; }

    public int StackCount { get; }

    public RewardStableState? StableState { get; }
}

public sealed class RewardForest
{
    public const int MaxRootCount = 64;
    public const int MaxNodeCount = 512;
    public const int MaxDepth = 32;
    public const int MaxCanonicalSizeBytes = 131_072;

    private RewardForest(
        IReadOnlyList<RewardForestNode> nodes,
        IReadOnlyList<RewardForestNode> roots)
    {
        Nodes = nodes;
        Roots = roots;
    }

    public IReadOnlyList<RewardForestNode> Nodes { get; }

    public IReadOnlyList<RewardForestNode> Roots { get; }

    public static RewardForest Create(IEnumerable<RewardForestNode> nodes)
    {
        return CargoDomainValidator.ValidateForest(nodes);
    }

    internal static RewardForest CreateValidated(
        RewardForestNode[] nodes,
        RewardForestNode[] roots) =>
        new(
            new ReadOnlyCollection<RewardForestNode>(nodes),
            new ReadOnlyCollection<RewardForestNode>(roots));
}

internal static class CargoDomainValidator
{
    internal const int MaxIdentifierSizeBytes = 512;
    internal const int MaxTextSizeBytes = 4_096;

    internal static string RequireIdentifier(string? value, string propertyName) =>
        RequireBoundedText(value, propertyName, MaxIdentifierSizeBytes);

    internal static string RequireText(string? value, string propertyName) =>
        RequireBoundedText(value, propertyName, MaxTextSizeBytes);

    internal static string? OptionalIdentifier(string? value, string propertyName)
    {
        return value is null ? null : RequireIdentifier(value, propertyName);
    }

    internal static int RequirePositive(int value, string propertyName)
    {
        if (value <= 0)
        {
            throw new CargoCatalogValidationException($"{propertyName} must be positive.");
        }

        return value;
    }

    internal static double RequirePositiveFinite(double value, string propertyName)
    {
        if (!double.IsFinite(value) || value <= 0d)
        {
            throw new CargoCatalogValidationException($"{propertyName} must be positive and finite.");
        }

        return value;
    }

    internal static void RequireOptionalNonNegative(decimal? value, string propertyName)
    {
        if (value < 0m)
        {
            throw new CargoCatalogValidationException($"{propertyName} cannot be negative.");
        }
    }

    internal static void RequireNotGreaterThan(decimal? value, decimal? maximum, string propertyName)
    {
        if (value is not null && maximum is not null && value > maximum)
        {
            throw new CargoCatalogValidationException($"{propertyName} cannot exceed its maximum.");
        }
    }

    internal static RewardForest ValidateForest(IEnumerable<RewardForestNode> source)
    {
        if (source is null)
        {
            throw new CargoCatalogValidationException("Forest nodes are required.");
        }

        var nodes = source
            .Take(RewardForest.MaxNodeCount + 1)
            .ToArray();
        if (nodes.Length == 0)
        {
            throw new CargoCatalogValidationException("A reward forest cannot be empty.");
        }
        if (nodes.Length > RewardForest.MaxNodeCount)
        {
            throw new CargoCatalogValidationException("Reward forest node count exceeds the supported limit.");
        }
        if (nodes.Any(node => node is null))
        {
            throw new CargoCatalogValidationException("A reward forest cannot contain a null node.");
        }

        var byPath = new Dictionary<string, RewardForestNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!byPath.TryAdd(node.LogicalPath, node))
            {
                throw new CargoCatalogValidationException($"Duplicate logical path '{node.LogicalPath}'.");
            }
        }

        var roots = nodes.Where(node => node.ParentLogicalPath is null).ToArray();
        if (roots.Length == 0)
        {
            throw new CargoCatalogValidationException("A reward forest requires at least one root.");
        }
        if (roots.Length > RewardForest.MaxRootCount)
        {
            throw new CargoCatalogValidationException("Reward forest root count exceeds the supported limit.");
        }

        foreach (var root in roots)
        {
            if (!string.Equals(root.TreeRootPath, root.LogicalPath, StringComparison.Ordinal))
            {
                throw new CargoCatalogValidationException("A root's tree root path must equal its logical path.");
            }
        }

        var depthByPath = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var cursor = node;
            var depth = 0;
            while (cursor.ParentLogicalPath is not null)
            {
                if (!visited.Add(cursor.LogicalPath))
                {
                    throw new CargoCatalogValidationException("Reward forest contains a cycle.");
                }
                if (!byPath.TryGetValue(cursor.ParentLogicalPath, out var parent))
                {
                    throw new CargoCatalogValidationException($"Node '{cursor.LogicalPath}' has a missing parent.");
                }
                if (!string.Equals(cursor.TreeRootPath, parent.TreeRootPath, StringComparison.Ordinal))
                {
                    throw new CargoCatalogValidationException("A child cannot reference a parent in another logical tree.");
                }

                cursor = parent;
                depth++;
                if (depth > RewardForest.MaxDepth)
                {
                    throw new CargoCatalogValidationException("Reward forest depth exceeds the supported limit.");
                }
            }

            if (!string.Equals(cursor.LogicalPath, node.TreeRootPath, StringComparison.Ordinal))
            {
                throw new CargoCatalogValidationException($"Node '{node.LogicalPath}' is disconnected from its declared root.");
            }

            depthByPath[node.LogicalPath] = depth;
        }

        Array.Sort(nodes, (left, right) =>
        {
            var comparison = StringComparer.Ordinal.Compare(left.TreeRootPath, right.TreeRootPath);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = depthByPath[left.LogicalPath].CompareTo(depthByPath[right.LogicalPath]);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.LogicalPath, right.LogicalPath);
        });
        Array.Sort(roots, (left, right) => StringComparer.Ordinal.Compare(left.LogicalPath, right.LogicalPath));

        if (RewardForestCanonicalizer.GetForestCanonicalSize(nodes) > RewardForest.MaxCanonicalSizeBytes)
        {
            throw new CargoCatalogValidationException("Reward forest canonical size exceeds the supported limit.");
        }

        return RewardForest.CreateValidated(nodes, roots);
    }

    private static string RequireBoundedText(string? value, string propertyName, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CargoCatalogValidationException($"{propertyName} is required.");
        }
        if (CargoUtf8.GetByteCount(value, propertyName) > maxBytes)
        {
            throw new CargoCatalogValidationException($"{propertyName} exceeds the supported size.");
        }

        return value;
    }
}

internal static class CargoUtf8
{
    internal static UTF8Encoding StrictEncoding { get; } = new(false, true);

    internal static int GetByteCount(string value, string propertyName)
    {
        try
        {
            return StrictEncoding.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw InvalidUnicode(propertyName);
        }
    }

    internal static byte[] GetBytes(string value, string propertyName)
    {
        try
        {
            return StrictEncoding.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw InvalidUnicode(propertyName);
        }
    }

    private static CargoCatalogValidationException InvalidUnicode(string propertyName) =>
        new($"{propertyName} must contain valid Unicode scalar text.");
}
