using System.Collections.ObjectModel;

namespace ContrabandCases.Shared.Catalog;

public sealed class RewardFingerprint : IEquatable<RewardFingerprint>
{
    public RewardFingerprint(string rootTemplateId, IEnumerable<string> childTemplateIds)
    {
        if (string.IsNullOrWhiteSpace(rootTemplateId))
        {
            throw new ArgumentException("A root template ID is required.", nameof(rootTemplateId));
        }

        if (childTemplateIds is null)
        {
            throw new ArgumentNullException(nameof(childTemplateIds));
        }
        var children = childTemplateIds.ToArray();
        if (children.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Child template IDs cannot be empty.", nameof(childTemplateIds));
        }

        Array.Sort(children, StringComparer.Ordinal);
        RootTemplateId = rootTemplateId;
        ChildTemplateIds = new ReadOnlyCollection<string>(children);
    }

    public string RootTemplateId { get; }

    public IReadOnlyList<string> ChildTemplateIds { get; }

    public static RewardFingerprint FromPreset(RewardPresetTree preset)
    {
        if (preset is null)
        {
            throw new ArgumentNullException(nameof(preset));
        }
        if (preset.Items.Count == 0)
        {
            throw new RewardCatalogValidationException("Reward preset is empty.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var byId = new Dictionary<string, RewardPresetItem>(StringComparer.Ordinal);
        foreach (var item in preset.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.TemplateId) || !ids.Add(item.Id))
            {
                throw new RewardCatalogValidationException("Reward preset contains an invalid or duplicate item ID.");
            }

            byId.Add(item.Id, item);
        }

        var roots = preset.Items.Where(item => item.ParentId is null).ToArray();
        if (roots.Length != 1)
        {
            throw new RewardCatalogValidationException("Reward preset must have exactly one root.");
        }

        foreach (var item in preset.Items)
        {
            if (item.ParentId is not null && !byId.ContainsKey(item.ParentId))
            {
                throw new RewardCatalogValidationException("Reward preset contains an orphaned item.");
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var cursor = item;
            while (cursor.ParentId is not null)
            {
                if (!visited.Add(cursor.Id) || !byId.TryGetValue(cursor.ParentId, out cursor))
                {
                    throw new RewardCatalogValidationException("Reward preset contains a cycle or orphaned item.");
                }
            }

            if (!string.Equals(cursor.Id, roots[0].Id, StringComparison.Ordinal))
            {
                throw new RewardCatalogValidationException("Reward preset is disconnected from its root.");
            }
        }

        return new RewardFingerprint(roots[0].TemplateId, preset.Items
            .Where(item => !string.Equals(item.Id, roots[0].Id, StringComparison.Ordinal))
            .Select(item => item.TemplateId));
    }

    public bool Equals(RewardFingerprint? other)
    {
        return other is not null &&
            string.Equals(RootTemplateId, other.RootTemplateId, StringComparison.Ordinal) &&
            ChildTemplateIds.SequenceEqual(other.ChildTemplateIds, StringComparer.Ordinal);
    }

    public override bool Equals(object? obj) => Equals(obj as RewardFingerprint);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RootTemplateId, StringComparer.Ordinal);
        foreach (var childTemplateId in ChildTemplateIds)
        {
            hash.Add(childTemplateId, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => RootTemplateId + ":" + string.Join(",", ChildTemplateIds);
}
