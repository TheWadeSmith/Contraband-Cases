using System.Globalization;
using System.Text;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace ContrabandCases.Server.Catalog;

public sealed class CargoLotResolverDependencies
{
    public CargoLotResolverDependencies(
        Func<string, TemplateItem?> findTemplate,
        Func<string, Preset?> findPreset,
        Func<string, TemplateItem, string?>? validateCustomItemData = null)
    {
        FindTemplate = findTemplate ?? throw new ArgumentNullException(nameof(findTemplate));
        FindPreset = findPreset ?? throw new ArgumentNullException(nameof(findPreset));
        ValidateCustomItemData = validateCustomItemData;
    }

    public Func<string, TemplateItem?> FindTemplate { get; }

    public Func<string, Preset?> FindPreset { get; }

    public Func<string, TemplateItem, string?>? ValidateCustomItemData { get; }
}

public sealed class ResolvedCargoLot
{
    private readonly CargoLotEvaluation? _evaluation;

    internal ResolvedCargoLot(
        CargoLotDefinition definition,
        RewardForest forest,
        RewardForestFingerprintV2 fingerprint,
        CargoLotIdentitySnapshot identity,
        CargoLotEvaluation? evaluation = null,
        bool isRetired = false)
    {
        Definition = definition;
        Forest = forest;
        Fingerprint = fingerprint;
        Identity = identity;
        _evaluation = evaluation;
        IsRetired = isRetired;
    }

    public CargoLotDefinition Definition { get; }

    public RewardForest Forest { get; }

    public RewardForestFingerprintV2 Fingerprint { get; }

    public CargoLotIdentitySnapshot Identity { get; }

    internal bool IsRetired { get; }

    public CargoLotEvaluation Evaluation =>
        _evaluation ?? throw new InvalidOperationException(
            "The cargo lot has not been evaluated against finalized SPT economy data.");

    internal CargoLotEvaluation? EvaluationOrNull => _evaluation;

    internal ResolvedCargoLot WithEvaluation(CargoLotEvaluation evaluation) =>
        new(Definition, Forest, Fingerprint, Identity, evaluation, IsRetired);

    internal ResolvedCargoLot AsRetired() => new(Definition, Forest, Fingerprint, Identity, _evaluation, true);
}

/// <summary>
/// Resolves the closed recipe language against finalized SPT data. Output uses
/// logical paths exclusively; source Mongo IDs never cross this boundary.
/// </summary>
public sealed class CargoLotResolver
{
    private static readonly HashSet<string> AllowedUpdProperties = new(StringComparer.Ordinal)
    {
        nameof(Upd.StackObjectsCount),
        nameof(Upd.Repairable),
        nameof(Upd.MedKit),
        nameof(Upd.RepairKit),
        nameof(Upd.FoodDrink),
        nameof(Upd.Resource)
    };

    private readonly CargoLotResolverDependencies _dependencies;

    public CargoLotResolver(CargoLotResolverDependencies dependencies)
    {
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
    }

    public ResolvedCargoLot Resolve(CargoLotDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var nodes = new List<RewardForestNode>();
        var rootIndex = 0;
        var templateCache = new Dictionary<string, TemplateItem>(StringComparer.Ordinal);
        foreach (var line in definition.RecipeLines)
        {
            switch (line)
            {
                case TemplateLine templateLine:
                    ResolveTemplateLine(templateLine, nodes, ref rootIndex, templateCache);
                    break;
                case PresetLine presetLine:
                    ResolvePresetLine(presetLine, nodes, ref rootIndex, templateCache);
                    break;
                default:
                    throw new CargoCatalogValidationException(
                        $"Lot '{definition.LotId}' contains an unsupported recipe line.");
            }
        }

        if (!nodes.Any(node => string.Equals(
                node.TemplateId,
                definition.AnchorTemplateId,
                StringComparison.Ordinal)))
        {
            throw new CargoCatalogValidationException(
                $"Lot '{definition.LotId}' anchor template is absent from its resolved forest.");
        }

        var forest = RewardForest.Create(nodes);
        CargoTemplateRules.ValidateForestTopologyAndEligibility(
            forest,
            _dependencies.FindTemplate,
            _dependencies.ValidateCustomItemData,
            templateCache);
        var fingerprint = RewardForestFingerprintV2.Compute(
            definition.ProviderId,
            definition.LotId,
            forest);
        return new ResolvedCargoLot(
            definition,
            forest,
            fingerprint,
            CargoLotIdentitySnapshot.Capture(definition, fingerprint));
    }

    private void ResolveTemplateLine(
        TemplateLine line,
        ICollection<RewardForestNode> output,
        ref int rootIndex,
        IDictionary<string, TemplateItem> templateCache)
    {
        if (line.InstanceCount > RewardForest.MaxRootCount)
        {
            throw new CargoCatalogValidationException(
                $"Template line '{line.TemplateId}' exceeds the root-count limit.");
        }

        var template = ResolveTemplate(line.TemplateId, templateCache);
        CargoTemplateRules.ValidateStackCount(
            line.TemplateId,
            line.StackCountPerInstance,
            template);
        var stableState = NormalizeStableState(line.TemplateId, template, null);
        for (var instance = 0; instance < line.InstanceCount; instance++)
        {
            EnsureForestCapacity(output.Count + 1, rootIndex + 1);
            var rootPath = RootPath(rootIndex++);
            output.Add(new RewardForestNode(
                rootPath,
                rootPath,
                line.TemplateId,
                null,
                null,
                null,
                line.StackCountPerInstance,
                stableState));
        }
    }

    private void ResolvePresetLine(
        PresetLine line,
        ICollection<RewardForestNode> output,
        ref int rootIndex,
        IDictionary<string, TemplateItem> templateCache)
    {
        var preset = FindPreset(line.PresetId);
        if (!string.Equals(preset.Id.ToString(), line.PresetId, StringComparison.Ordinal))
        {
            throw new CargoCatalogValidationException(
                $"Preset lookup '{line.PresetId}' returned mismatched custom-item data.");
        }
        if (preset.Items is null || preset.Items.Count == 0)
        {
            throw new CargoCatalogValidationException($"Preset '{line.PresetId}' contains no items.");
        }
        if (preset.Items.Count > RewardForest.MaxNodeCount)
        {
            throw new CargoCatalogValidationException(
                $"Preset '{line.PresetId}' exceeds the node-count limit.");
        }

        var byId = new Dictionary<string, PresetNode>(StringComparer.Ordinal);
        foreach (var item in preset.Items)
        {
            if (item is null)
            {
                throw new CargoCatalogValidationException(
                    $"Preset '{line.PresetId}' contains a null item.");
            }

            var sourceId = item.Id.ToString();
            if (string.IsNullOrWhiteSpace(sourceId) || byId.ContainsKey(sourceId))
            {
                throw new CargoCatalogValidationException(
                    $"Preset '{line.PresetId}' contains a missing or duplicate item ID.");
            }

            EnsureNoUnmodeledInstanceData(line.PresetId, item);
            var templateId = item.Template.ToString();
            var template = ResolveTemplate(templateId, templateCache);
            byId.Add(sourceId, new PresetNode(
                sourceId,
                templateId,
                item.ParentId,
                item.SlotId,
                item.Location,
                ResolvePresetStackCount(line.PresetId, item, template),
                NormalizeStableState(templateId, template, item)));
        }

        var rootId = preset.Parent.ToString();
        if (!byId.TryGetValue(rootId, out var root))
        {
            throw new CargoCatalogValidationException(
                $"Preset '{line.PresetId}' does not identify a root in its item tree.");
        }

        foreach (var node in byId.Values)
        {
            if (ReferenceEquals(node, root))
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(node.ParentSourceId) ||
                !byId.TryGetValue(node.ParentSourceId, out var parent))
            {
                throw new CargoCatalogValidationException(
                    $"Preset '{line.PresetId}' contains an orphaned item.");
            }
            if (string.IsNullOrWhiteSpace(node.SlotId))
            {
                throw new CargoCatalogValidationException(
                    $"Preset '{line.PresetId}' contains a child without a slot ID.");
            }

            parent.Children.Add(node);
        }

        ValidateCompleteTree(line.PresetId, byId, root);
        foreach (var slot in line.OmitRootSlots)
        {
            var matches = root.Children.Where(node => node.SlotId == slot).Take(2).ToArray();
            if (matches.Length != 1 || matches[0].Children.Count != 0)
                throw new CargoCatalogValidationException($"Preset '{line.PresetId}' cannot omit non-leaf or absent root slot '{slot}'.");
            var rootTemplate = templateCache[root.TemplateId];
            var slots = rootTemplate.Properties?.Slots?.Where(s => s.Name == slot).Take(2).ToArray();
            if (slots is not { Length: 1 } || slots[0].Required != false)
                throw new CargoCatalogValidationException($"Preset '{line.PresetId}' cannot omit required or unknown root slot '{slot}'.");
            root.Children.Remove(matches[0]);
        }
        EnsureForestCapacity(output.Count + byId.Count, rootIndex + 1);
        var rootPath = RootPath(rootIndex++);
        AppendCanonicalTree(line.PresetId, root, rootPath, rootPath, null, true, output);
    }

    private Preset FindPreset(string presetId)
    {
        try
        {
            return _dependencies.FindPreset(presetId)
                ?? throw new CargoCatalogValidationException($"Preset '{presetId}' does not exist.");
        }
        catch (CargoCatalogValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new CargoCatalogValidationException(
                $"Preset '{presetId}' could not be read from finalized SPT data.");
        }
    }

    private TemplateItem ResolveTemplate(
        string templateId,
        IDictionary<string, TemplateItem> cache)
    {
        if (cache.TryGetValue(templateId, out var cached))
        {
            return cached;
        }

        var template = CargoTemplateRules.ResolveTemplate(
            templateId,
            _dependencies.FindTemplate,
            _dependencies.ValidateCustomItemData);
        cache.Add(templateId, template);
        return template;
    }

    private static void ValidateCompleteTree(
        string presetId,
        IReadOnlyDictionary<string, PresetNode> byId,
        PresetNode root)
    {
        var states = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        foreach (var node in byId.Values)
        {
            VisitForCycles(presetId, node, states);
        }

        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(PresetNode Node, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            if (!reached.Add(node.SourceId))
            {
                continue;
            }
            if (depth > RewardForest.MaxDepth)
            {
                throw new CargoCatalogValidationException(
                    $"Preset '{presetId}' exceeds the tree-depth limit.");
            }
            foreach (var child in node.Children)
            {
                pending.Push((child, depth + 1));
            }
        }

        if (reached.Count != byId.Count)
        {
            throw new CargoCatalogValidationException(
                $"Preset '{presetId}' contains nodes disconnected from its declared root.");
        }
    }

    private static void VisitForCycles(
        string presetId,
        PresetNode node,
        IDictionary<string, VisitState> states)
    {
        if (states.TryGetValue(node.SourceId, out var state))
        {
            if (state == VisitState.Visiting)
            {
                throw new CargoCatalogValidationException($"Preset '{presetId}' contains a cycle.");
            }
            return;
        }

        states[node.SourceId] = VisitState.Visiting;
        foreach (var child in node.Children)
        {
            VisitForCycles(presetId, child, states);
        }
        states[node.SourceId] = VisitState.Visited;
    }

    private static void AppendCanonicalTree(
        string presetId,
        PresetNode source,
        string rootPath,
        string logicalPath,
        string? parentPath,
        bool isRoot,
        ICollection<RewardForestNode> output)
    {
        output.Add(new RewardForestNode(
            rootPath,
            logicalPath,
            source.TemplateId,
            parentPath,
            isRoot ? null : source.SlotId,
            isRoot ? null : ResolveInternalLocation(presetId, source.Location),
            source.StackCount,
            source.StableState));

        var children = source.Children
            .OrderBy(child => BuildCanonicalSubtreeKey(presetId, child), StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < children.Length; index++)
        {
            var childPath = string.Concat(
                logicalPath,
                "/node-",
                index.ToString("D4", CultureInfo.InvariantCulture));
            AppendCanonicalTree(
                presetId,
                children[index],
                rootPath,
                childPath,
                logicalPath,
                false,
                output);
        }
    }

    private static string BuildCanonicalSubtreeKey(string presetId, PresetNode node)
    {
        var builder = new StringBuilder();
        AppendKeyPart(builder, node.TemplateId);
        AppendKeyPart(builder, node.SlotId ?? string.Empty);
        AppendKeyPart(builder, CanonicalLocationKey(presetId, node.Location));
        AppendKeyPart(builder, node.StackCount.ToString(CultureInfo.InvariantCulture));
        AppendKeyPart(builder, StableStateKey(node.StableState));
        foreach (var childKey in node.Children
                     .Select(child => BuildCanonicalSubtreeKey(presetId, child))
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            AppendKeyPart(builder, childKey);
        }
        return builder.ToString();
    }

    private static void AppendKeyPart(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
    }

    private static string StableStateKey(RewardStableState? state) => state is null
        ? string.Empty
        : string.Join(
            "/",
            DecimalKey(state.Durability),
            DecimalKey(state.MaximumDurability),
            state.ResourceKind is null
                ? "-"
                : ((int)state.ResourceKind.Value).ToString(CultureInfo.InvariantCulture),
            DecimalKey(state.ResourceValue),
            DecimalKey(state.MaximumResourceValue));

    private static string DecimalKey(decimal? value) =>
        value?.ToString("G29", CultureInfo.InvariantCulture) ?? "-";

    private static string CanonicalLocationKey(string presetId, object? value)
    {
        var location = ResolveInternalLocation(presetId, value);
        if (location is null)
        {
            return string.Empty;
        }
        return string.Concat(
            location.X.ToString(CultureInfo.InvariantCulture),
            ",",
            location.Y.ToString(CultureInfo.InvariantCulture),
            ",",
            location.Rotation == CanonicalRotation.Vertical ? "v" : "h");
    }

    private static CanonicalInternalLocation? ResolveInternalLocation(
        string presetId,
        object? value)
    {
        if (value is null)
        {
            return null;
        }
        if (value is not ItemLocation location ||
            location.ExtensionData is { Count: > 0 } ||
            location.X is null ||
            location.Y is null)
        {
            throw new CargoCatalogValidationException(
                $"Preset '{presetId}' contains unsupported custom internal-location data.");
        }
        if (!Enum.IsDefined(location.R))
        {
            throw new CargoCatalogValidationException(
                $"Preset '{presetId}' contains an unknown internal rotation.");
        }

        var rotation = ResolveRotation(presetId, location);
        return new CanonicalInternalLocation(location.X.Value, location.Y.Value, rotation);
    }

    private static CanonicalRotation ResolveRotation(string presetId, ItemLocation location)
    {
        if (location.Rotation is not bool legacy)
        {
            return location.R == ItemRotation.Vertical
                ? CanonicalRotation.Vertical
                : CanonicalRotation.Horizontal;
        }

        // R is non-nullable in SPT 4.1.3, so Horizontal is also the value on
        // legacy-only locations. Vertical plus legacy false is the only mixed
        // representation that is unambiguously contradictory.
        if (location.R == ItemRotation.Vertical && !legacy)
        {
            throw new CargoCatalogValidationException(
                $"Preset '{presetId}' contains conflicting internal rotations.");
        }

        return legacy || location.R == ItemRotation.Vertical
            ? CanonicalRotation.Vertical
            : CanonicalRotation.Horizontal;
    }

    private static int ResolvePresetStackCount(
        string presetId,
        Item item,
        TemplateItem template)
    {
        var raw = item.Upd?.StackObjectsCount ?? 1d;
        if (!double.IsFinite(raw) ||
            raw <= 0d ||
            raw != Math.Truncate(raw) ||
            raw > int.MaxValue)
        {
            throw new CargoCatalogValidationException(
                $"Preset '{presetId}' contains an invalid item stack count.");
        }

        var stackCount = checked((int)raw);
        CargoTemplateRules.ValidateStackCount(item.Template.ToString(), stackCount, template);
        return stackCount;
    }

    private static RewardStableState? NormalizeStableState(
        string templateId,
        TemplateItem template,
        Item? item)
    {
        var properties = template.Properties
            ?? throw new CargoCatalogValidationException(
                $"Template '{templateId}' has no item properties.");
        var sourceRepairable = item?.Upd?.Repairable;
        if (sourceRepairable is not null &&
            (sourceRepairable.Durability is null ||
             sourceRepairable.MaxDurability is null))
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' has incomplete durability instance state.");
        }
        var finalizedDurabilityMaximum =
            CargoTemplateRules.GetFinalizedDurabilityMaximum(properties);
        if (sourceRepairable is not null && finalizedDurabilityMaximum is null)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' does not support durability instance state.");
        }

        var maximumDurability =
            sourceRepairable?.MaxDurability ?? finalizedDurabilityMaximum;
        var durability = sourceRepairable?.Durability ?? maximumDurability;
        ValidateStatePair(templateId, "durability", durability, maximumDurability);
        if (maximumDurability > finalizedDurabilityMaximum)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' durability exceeds its finalized maximum.");
        }

        var resources = new (RewardResourceKind Kind, object? Component, double? Value)[]
        {
            (RewardResourceKind.MedKit, item?.Upd?.MedKit, item?.Upd?.MedKit?.HpResource),
            (RewardResourceKind.RepairKit, item?.Upd?.RepairKit, item?.Upd?.RepairKit?.Resource),
            (RewardResourceKind.FoodDrink, item?.Upd?.FoodDrink, item?.Upd?.FoodDrink?.HpPercent),
            (RewardResourceKind.Generic, item?.Upd?.Resource, item?.Upd?.Resource?.Value)
        };
        var populated = resources.Where(resource => resource.Component is not null).ToArray();
        if (populated.Length > 1)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' has multiple resource-state representations.");
        }

        RewardResourceKind? resourceKind;
        double? resourceValue;
        double? maximumResourceValue;
        if (populated.Length == 1)
        {
            resourceKind = populated[0].Kind;
            resourceValue = populated[0].Value;
            if (resourceValue is null)
            {
                throw new CargoCatalogValidationException(
                    $"Template '{templateId}' has incomplete {resourceKind} instance state.");
            }
            maximumResourceValue = CargoTemplateRules.GetResourceMaximumForKind(
                templateId,
                properties,
                resourceKind.Value);
        }
        else
        {
            var finalized = CargoTemplateRules.ResolveDefaultResource(templateId, properties);
            resourceKind = finalized?.Kind;
            resourceValue = finalized?.Value;
            maximumResourceValue = finalized?.Maximum;
        }
        ValidateStatePair(templateId, "resource", resourceValue, maximumResourceValue);

        if (durability is null &&
            maximumDurability is null &&
            resourceValue is null &&
            maximumResourceValue is null)
        {
            return null;
        }

        return new RewardStableState(
            ToDecimal(templateId, "durability", durability),
            ToDecimal(templateId, "maximum durability", maximumDurability),
            ToDecimal(templateId, "resource", resourceValue),
            ToDecimal(templateId, "maximum resource", maximumResourceValue),
            resourceKind);
    }

    private static void ValidateStatePair(
        string templateId,
        string stateName,
        double? value,
        double? maximum)
    {
        ValidateOptionalFiniteNonNegative(templateId, stateName, value);
        ValidateOptionalFiniteNonNegative(templateId, $"maximum {stateName}", maximum);
        if (value is not null && maximum is not null && value > maximum)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' {stateName} exceeds its finalized maximum.");
        }
    }

    private static void ValidateOptionalFiniteNonNegative(
        string templateId,
        string propertyName,
        double? value)
    {
        if (value is not null && (!double.IsFinite(value.Value) || value < 0d))
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' has invalid {propertyName} data.");
        }
    }

    private static decimal? ToDecimal(
        string templateId,
        string propertyName,
        double? value)
    {
        if (value is null)
        {
            return null;
        }
        if (!double.IsFinite(value.Value) ||
            value.Value < (double)decimal.MinValue ||
            value.Value > (double)decimal.MaxValue)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' {propertyName} cannot be represented canonically.");
        }
        return checked((decimal)value.Value);
    }

    private static void EnsureNoUnmodeledInstanceData(string presetId, Item item)
    {
        if (item.ExtensionData is { Count: > 0 })
        {
            throw new CargoCatalogValidationException(
                $"Preset '{presetId}' contains custom instance state outside the canonical allowlist.");
        }

        var upd = item.Upd;
        if (upd is null)
        {
            return;
        }

        foreach (var property in typeof(Upd).GetProperties())
        {
            var value = property.GetValue(upd);
            if (value is null || AllowedUpdProperties.Contains(property.Name))
            {
                continue;
            }
            if (property.Name == nameof(Upd.ExtensionData) &&
                upd.ExtensionData is { Count: 0 })
            {
                continue;
            }

            throw new CargoCatalogValidationException(
                $"Preset '{presetId}' contains unsupported Upd state '{property.Name}'.");
        }

        if (upd.ExtensionData is { Count: > 0 } ||
            upd.Repairable?.ExtensionData is { Count: > 0 } ||
            upd.Resource?.ExtensionData is { Count: > 0 } ||
            upd.MedKit?.ExtensionData is { Count: > 0 } ||
            upd.FoodDrink?.ExtensionData is { Count: > 0 } ||
            upd.RepairKit?.ExtensionData is { Count: > 0 } ||
            upd.Resource?.UnitsConsumed is not null)
        {
            throw new CargoCatalogValidationException(
                $"Preset '{presetId}' contains custom instance state outside the canonical allowlist.");
        }
    }

    private static void EnsureForestCapacity(int nodeCount, int rootCount)
    {
        if (nodeCount > RewardForest.MaxNodeCount)
        {
            throw new CargoCatalogValidationException(
                "Resolved lot exceeds the forest node-count limit.");
        }
        if (rootCount > RewardForest.MaxRootCount)
        {
            throw new CargoCatalogValidationException(
                "Resolved lot exceeds the forest root-count limit.");
        }
    }

    private static string RootPath(int index) =>
        string.Concat("root-", index.ToString("D4", CultureInfo.InvariantCulture));

    private sealed class PresetNode
    {
        public PresetNode(
            string sourceId,
            string templateId,
            string? parentSourceId,
            string? slotId,
            object? location,
            int stackCount,
            RewardStableState? stableState)
        {
            SourceId = sourceId;
            TemplateId = templateId;
            ParentSourceId = parentSourceId;
            SlotId = slotId;
            Location = location;
            StackCount = stackCount;
            StableState = stableState;
        }

        public string SourceId { get; }

        public string TemplateId { get; }

        public string? ParentSourceId { get; }

        public string? SlotId { get; }

        public object? Location { get; }

        public int StackCount { get; }

        public RewardStableState? StableState { get; }

        public List<PresetNode> Children { get; } = [];
    }

    private enum VisitState
    {
        Visiting,
        Visited
    }
}
