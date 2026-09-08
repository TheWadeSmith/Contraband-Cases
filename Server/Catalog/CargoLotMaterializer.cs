using System.Collections.ObjectModel;
using System.Security.Cryptography;
using ContrabandCases.Shared.Catalog;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace ContrabandCases.Server.Catalog;

public sealed class MaterializedCargoLot
{
    internal MaterializedCargoLot(
        IReadOnlyList<Item> items,
        IReadOnlyList<MongoId> canonicalRootIds)
    {
        Items = items;
        CanonicalRootIds = canonicalRootIds;
    }

    public IReadOnlyList<Item> Items { get; }

    public IReadOnlyList<MongoId> CanonicalRootIds { get; }
}

/// <summary>
/// Materializes a verified semantic forest against the current finalized item
/// templates. No inventory placement or profile mutation occurs here.
/// </summary>
public sealed class CargoLotMaterializer
{
    internal const int MaxExistingIdCount = 65_536;

    private readonly Func<string, TemplateItem?> _findTemplate;
    private readonly Func<string, TemplateItem, string?>? _validateCustomItemData;
    private readonly Func<MongoId> _createId;

    public CargoLotMaterializer(
        Func<string, TemplateItem?> findTemplate,
        Func<string, TemplateItem, string?>? validateCustomItemData = null)
        : this(findTemplate, CreateSecureMongoId, validateCustomItemData)
    {
    }

    internal CargoLotMaterializer(
        Func<string, TemplateItem?> findTemplate,
        Func<MongoId> createId)
        : this(findTemplate, createId, null)
    {
    }

    private CargoLotMaterializer(
        Func<string, TemplateItem?> findTemplate,
        Func<MongoId> createId,
        Func<string, TemplateItem, string?>? validateCustomItemData)
    {
        _findTemplate = findTemplate ?? throw new ArgumentNullException(nameof(findTemplate));
        _createId = createId ?? throw new ArgumentNullException(nameof(createId));
        _validateCustomItemData = validateCustomItemData;
    }

    public MaterializedCargoLot Materialize(
        RewardForest forest,
        IEnumerable<MongoId> existingIds)
        => MaterializeCore(forest, existingIds, false);

    internal MaterializedCargoLot MaterializeCashPayout(RewardForest forest, IEnumerable<MongoId> existingIds)
        => MaterializeCore(forest, existingIds, true);

    private MaterializedCargoLot MaterializeCore(RewardForest forest, IEnumerable<MongoId> existingIds, bool cashPayout)
    {
        ArgumentNullException.ThrowIfNull(forest);
        ArgumentNullException.ThrowIfNull(existingIds);

        var templates = ValidateForestAgainstLiveTemplates(forest, cashPayout);
        var occupiedIds = ReadExistingIds(existingIds);
        var idByLogicalPath = new Dictionary<string, MongoId>(StringComparer.Ordinal);
        foreach (var node in forest.Nodes)
        {
            idByLogicalPath.Add(node.LogicalPath, AllocateUniqueId(occupiedIds));
        }

        var items = new Item[forest.Nodes.Count];
        for (var index = 0; index < forest.Nodes.Count; index++)
        {
            var node = forest.Nodes[index];
            items[index] = new Item
            {
                Id = idByLogicalPath[node.LogicalPath],
                Template = templates[node.TemplateId].Id,
                ParentId = node.ParentLogicalPath is null
                    ? null
                    : idByLogicalPath[node.ParentLogicalPath].ToString(),
                SlotId = node.SlotId,
                Location = ToPhysicalLocation(node.InternalLocation),
                Upd = ToPhysicalUpd(node)
            };
        }

        var rootIds = forest.Roots
            .Select(root => idByLogicalPath[root.LogicalPath])
            .ToArray();
        return new MaterializedCargoLot(
            new ReadOnlyCollection<Item>(items),
            new ReadOnlyCollection<MongoId>(rootIds));
    }

    public void Validate(RewardForest forest)
    {
        ArgumentNullException.ThrowIfNull(forest);
        _ = ValidateForestAgainstLiveTemplates(forest);
    }

    internal void ValidateCashPayout(RewardForest forest)
    {
        ArgumentNullException.ThrowIfNull(forest);
        _ = ValidateForestAgainstLiveTemplates(forest, true);
    }

    private IReadOnlyDictionary<string, TemplateItem> ValidateForestAgainstLiveTemplates(
        RewardForest forest, bool cashPayout = false)
    {
        if (cashPayout) CashPayoutCatalog.ValidateShape(forest);
        var templates = new Dictionary<string, TemplateItem>(StringComparer.Ordinal);
        foreach (var node in forest.Nodes)
        {
            if (!templates.TryGetValue(node.TemplateId, out var template))
            {
                template = CargoTemplateRules.ResolveTemplate(
                    node.TemplateId,
                    _findTemplate,
                    _validateCustomItemData);
                templates.Add(node.TemplateId, template);
            }

            CargoTemplateRules.ValidateStackCount(node.TemplateId, node.StackCount, template);
            ValidateStableState(node, template);
        }

        CargoTemplateRules.ValidateForestTopologyAndEligibility(
            forest,
            _findTemplate,
            _validateCustomItemData,
            templates,
            cashPayout);
        return templates;
    }

    private static void ValidateStableState(
        RewardForestNode node,
        TemplateItem template)
    {
        var properties = template.Properties
            ?? throw new CargoCatalogValidationException(
                $"Template '{node.TemplateId}' has no item properties.");
        var state = node.StableState;
        var finalizedDurabilityMaximum =
            CargoTemplateRules.GetFinalizedDurabilityMaximum(properties);
        var hasDurability =
            state?.Durability is not null ||
            state?.MaximumDurability is not null;
        if (!hasDurability && finalizedDurabilityMaximum is not null)
        {
            throw new CargoCatalogValidationException(
                $"Template '{node.TemplateId}' durability state drifted after catalog finalization.");
        }
        if (hasDurability && finalizedDurabilityMaximum is null)
        {
            throw new CargoCatalogValidationException(
                $"Template '{node.TemplateId}' does not support durability instance state.");
        }
        if (hasDurability &&
            (state!.Durability is null || state.MaximumDurability is null))
        {
            throw new CargoCatalogValidationException(
                $"Template '{node.TemplateId}' has incomplete durability instance state.");
        }
        if (hasDurability && finalizedDurabilityMaximum is not null)
        {
            var finalizedMaximum = CargoTemplateRules.ToCanonicalDecimal(
                node.TemplateId,
                "maximum durability",
                finalizedDurabilityMaximum.Value);
            if (state!.Durability > finalizedMaximum ||
                state.MaximumDurability > finalizedMaximum)
            {
                throw new CargoCatalogValidationException(
                    $"Template '{node.TemplateId}' durability state is outside its finalized bounds.");
            }
        }

        if (state?.ResourceKind is not RewardResourceKind kind)
        {
            if (CargoTemplateRules.ResolveDefaultResource(node.TemplateId, properties) is not null)
            {
                throw new CargoCatalogValidationException(
                    $"Template '{node.TemplateId}' resource state drifted after catalog finalization.");
            }
            return;
        }

        var finalizedResourceMaximum =
            CargoTemplateRules.GetResourceMaximumForKind(node.TemplateId, properties, kind);
        var canonicalMaximum = CargoTemplateRules.ToCanonicalDecimal(
            node.TemplateId,
            "maximum resource",
            finalizedResourceMaximum);
        if (state.MaximumResourceValue != canonicalMaximum)
        {
            throw new CargoCatalogValidationException(
                $"Template '{node.TemplateId}' resource bounds drifted after catalog finalization.");
        }
    }

    private MongoId AllocateUniqueId(ISet<MongoId> occupiedIds)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = _createId();
            if (!candidate.IsEmpty && occupiedIds.Add(candidate))
            {
                return candidate;
            }
        }

        throw new CargoCatalogValidationException(
            "Could not allocate a unique physical item ID.");
    }

    private static HashSet<MongoId> ReadExistingIds(IEnumerable<MongoId> existingIds)
    {
        var occupiedIds = new HashSet<MongoId>();
        var count = 0;
        foreach (var existingId in existingIds)
        {
            count++;
            if (count > MaxExistingIdCount)
            {
                throw new CargoCatalogValidationException(
                    "Existing item ID count exceeds the supported limit.");
            }
            if (existingId.IsEmpty)
            {
                throw new CargoCatalogValidationException(
                    "Existing item IDs cannot contain an empty ID.");
            }

            occupiedIds.Add(existingId);
        }

        return occupiedIds;
    }

    private static ItemLocation? ToPhysicalLocation(CanonicalInternalLocation? location)
    {
        if (location is null)
        {
            return null;
        }

        return new ItemLocation
        {
            X = location.X,
            Y = location.Y,
            R = location.Rotation == CanonicalRotation.Vertical
                ? ItemRotation.Vertical
                : ItemRotation.Horizontal,
            Rotation = null
        };
    }

    private static Upd ToPhysicalUpd(RewardForestNode node)
    {
        var upd = new Upd { StackObjectsCount = node.StackCount };
        var state = node.StableState;
        if (state is null)
        {
            return upd;
        }

        if (state.Durability is not null || state.MaximumDurability is not null)
        {
            upd.Repairable = new UpdRepairable
            {
                Durability = ToPhysicalDouble(state.Durability),
                MaxDurability = ToPhysicalDouble(state.MaximumDurability)
            };
        }

        var resourceValue = ToPhysicalDouble(state.ResourceValue);
        switch (state.ResourceKind)
        {
            case null:
                break;
            case RewardResourceKind.MedKit:
                upd.MedKit = new UpdMedKit { HpResource = resourceValue };
                break;
            case RewardResourceKind.RepairKit:
                upd.RepairKit = new UpdRepairKit { Resource = resourceValue };
                break;
            case RewardResourceKind.FoodDrink:
                upd.FoodDrink = new UpdFoodDrink { HpPercent = resourceValue };
                break;
            case RewardResourceKind.Generic:
                upd.Resource = new UpdResource { Value = resourceValue };
                break;
            default:
                throw new CargoCatalogValidationException("Resource kind is unknown.");
        }

        return upd;
    }

    private static double? ToPhysicalDouble(decimal? value)
    {
        if (value is null)
        {
            return null;
        }

        var result = (double)value.Value;
        if (!double.IsFinite(result))
        {
            throw new CargoCatalogValidationException(
                "Stable state cannot be represented by a physical SPT item.");
        }

        return result;
    }

    private static MongoId CreateSecureMongoId() =>
        (MongoId)Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
}

internal sealed record CargoTemplateResourceState(
    RewardResourceKind Kind,
    double Value,
    double Maximum);

internal static class CargoTemplateRules
{
    private const string ItemRootTemplateId = "54009119af1c881c07000029";
    private const int MaxAncestryDepth = 64;
    private const int MaxTemplateTargets = 256;
    private const int MaxTargetFilters = 64;
    private const string SimpleStorageClass = "5795f317245977243854e041";
    private const string CompoundItemClass = "566162e44bdc2d3f298b4573";

    // Useful, ordinary player equipment may be won empty. This is not permission
    // to pack rewards into arbitrary storage, secure containers or profile grids.
    private static readonly HashSet<string> EmptyStoragePrizes = new(StringComparer.Ordinal)
    {
        "619cbf7d23893217ec30b689", // Injector case
        "5d235bb686f77443f4331278", // SICC pouch
        "590c60fc86f77412b13fddcf", // Documents case
        "59fafd4b86f7745ca07e1232", // Key tool
        "5aafbcd986f7745e590fff23", // Medicine case
        "59fb023c86f7746d0d4b423c", // Weapon case
        "5b6d9ce188a4501afc1b2b25", // THICC weapon case
        "59fb042886f7746c5005a7b2", // Item case
        "5c0a840b86f7742ffa4f2482", // THICC item case (chase prize)
        "5e2af55f86f7746d4159f07c", // Grenade case
        "67600929bd0a0549d70993f6"  // Ballistic plate case
    };

    private static readonly HashSet<string> ExcludedAncestryIds = new(StringComparer.Ordinal)
    {
        // Currency, keys, and keycards.
        "543be5dd4bdc2deb348b4569",
        "543be5e94bdc2df1348b4568",
        "5c99f98d86f7745c314214b3",
        "5c164d2286f774194c5e69fa",

        // Secure, storage, loot, profile, and stash containers.
        "5448bf274bdc2dfc2f8b456a",
        "5795f317245977243854e041",
        "5671435f4bdc2d96058b4569",
        "566965d44bdc2d814c8b4571",
        "62f109593b54472778797866",
        "567583764bdc2d98058b456e",
        "566abbb64bdc2d144c8b457d",
        "63da6da4784a55176c018dba",
        "55d720f24bdc2d88028b456d",
        "557596e64bdc2dc2118b4571",
        "6050cac987d3f925bf016837"
    };

    private static readonly HashSet<string> ExcludedExactTemplateIds = new(StringComparer.Ordinal)
    {
        // Non-money service/wealth tokens explicitly excluded by the blueprint.
        "59faff1d86f7746c51718c9c", // Physical Bitcoin
        "6656560053eaaa7a23349c86"  // Lega Medal
    };

    internal static TemplateItem ResolveTemplate(
        string templateId,
        Func<string, TemplateItem?> findTemplate,
        Func<string, TemplateItem, string?>? validateCustomItemData)
    {
        TemplateItem template;
        try
        {
            template = findTemplate(templateId)
                ?? throw new CargoCatalogValidationException(
                    $"Template '{templateId}' does not exist.");
        }
        catch (CargoCatalogValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' could not be read from finalized SPT data.");
        }

        ValidateTemplateData(templateId, template);
        ValidateCustomItemData(templateId, template, validateCustomItemData);
        return template;
    }

    internal static void ValidateForestTopologyAndEligibility(
        RewardForest forest,
        Func<string, TemplateItem?> findTemplate,
        Func<string, TemplateItem, string?>? validateCustomItemData,
        IDictionary<string, TemplateItem> templateCache,
        bool cashPayout = false)
    {
        if (cashPayout) CashPayoutCatalog.ValidateShape(forest);
        var ancestryByTemplate = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var node in forest.Nodes)
        {
            if (!templateCache.TryGetValue(node.TemplateId, out var template))
            {
                template = ResolveTemplate(
                    node.TemplateId,
                    findTemplate,
                    validateCustomItemData);
                templateCache.Add(node.TemplateId, template);
            }

            if (EmptyStoragePrizes.Contains(node.TemplateId) &&
                (node.ParentLogicalPath is not null || node.StackCount != 1 ||
                 forest.Nodes.Any(child => child.ParentLogicalPath == node.LogicalPath)))
                throw new CargoCatalogValidationException("Storage prizes must be empty, singleton roots; rewards are never packed inside them.");

            ValidateRewardEligibility(
                node.TemplateId,
                template,
                findTemplate,
                templateCache,
                ancestryByTemplate,
                cashPayout);
        }

        var childrenByParent = forest.Nodes
            .Where(node => node.ParentLogicalPath is not null)
            .GroupBy(node => node.ParentLogicalPath!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        foreach (var parent in forest.Nodes)
        {
            childrenByParent.TryGetValue(parent.LogicalPath, out var children);
            ValidateParentTopology(
                parent,
                children ?? [],
                templateCache,
                ancestryByTemplate);
        }
    }

    private static void ValidateRewardEligibility(
        string templateId,
        TemplateItem template,
        Func<string, TemplateItem?> findTemplate,
        IDictionary<string, TemplateItem> templateCache,
        IDictionary<string, HashSet<string>> ancestryByTemplate,
        bool cashPayout)
    {
        var properties = template.Properties
            ?? throw new CargoCatalogValidationException(
                $"Template '{templateId}' has no item properties.");
        if (properties.QuestItem is true || properties.DogTagQualities is true)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' is a quest item or dogtag and cannot be cargo.");
        }

        var ancestry = GetTemplateAncestry(
            templateId,
            template,
            findTemplate,
            templateCache,
            ancestryByTemplate);
        var permittedCash = cashPayout && CashPayouts.IsAllowed(templateId);
        var permittedEmptyStorage = EmptyStoragePrizes.Contains(templateId) &&
            string.Equals(template.Parent.ToString(), SimpleStorageClass, StringComparison.Ordinal) &&
            ancestry.SetEquals([templateId, SimpleStorageClass, CompoundItemClass, ItemRootTemplateId]);
        if (permittedCash && ancestry.Any(id => id != "543be5dd4bdc2deb348b4569" && ExcludedAncestryIds.Contains(id)))
            throw new CargoCatalogValidationException("Cash templates cannot inherit another excluded cargo class.");
        if (!permittedCash && ((!permittedEmptyStorage && ancestry.Overlaps(ExcludedAncestryIds)) ||
            ExcludedExactTemplateIds.Contains(templateId) ||
            properties.IsRagfairCurrency is true))
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' belongs to an excluded cargo class.");
        }
    }

    private static HashSet<string> GetTemplateAncestry(
        string templateId,
        TemplateItem template,
        Func<string, TemplateItem?> findTemplate,
        IDictionary<string, TemplateItem> templateCache,
        IDictionary<string, HashSet<string>> ancestryByTemplate)
    {
        if (ancestryByTemplate.TryGetValue(templateId, out var cached))
        {
            return cached;
        }

        var ancestry = new HashSet<string>(StringComparer.Ordinal) { templateId };
        var cursor = template;
        for (var depth = 0; depth < MaxAncestryDepth; depth++)
        {
            var parentId = cursor.Parent.ToString();
            if (string.IsNullOrWhiteSpace(parentId))
            {
                throw new CargoCatalogValidationException(
                    $"Template '{templateId}' has a missing finalized parent.");
            }
            if (!ancestry.Add(parentId))
            {
                throw new CargoCatalogValidationException(
                    $"Template '{templateId}' has a cyclic finalized ancestry.");
            }
            if (string.Equals(parentId, ItemRootTemplateId, StringComparison.Ordinal))
            {
                ancestryByTemplate.Add(templateId, ancestry);
                return ancestry;
            }

            cursor = ResolveAncestorTemplate(parentId, findTemplate, templateCache);
        }

        throw new CargoCatalogValidationException(
            $"Template '{templateId}' ancestry exceeds the supported limit.");
    }

    private static TemplateItem ResolveAncestorTemplate(
        string templateId,
        Func<string, TemplateItem?> findTemplate,
        IDictionary<string, TemplateItem> templateCache)
    {
        if (templateCache.TryGetValue(templateId, out var cached))
        {
            if (!string.Equals(cached.Id.ToString(), templateId, StringComparison.Ordinal) ||
                !string.Equals(cached.Type, "Node", StringComparison.Ordinal))
            {
                throw new CargoCatalogValidationException(
                    $"Template ancestor '{templateId}' is not a matching finalized category.");
            }
            return cached;
        }

        TemplateItem template;
        try
        {
            template = findTemplate(templateId)
                ?? throw new CargoCatalogValidationException(
                    $"Template ancestor '{templateId}' does not exist.");
        }
        catch (CargoCatalogValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new CargoCatalogValidationException(
                $"Template ancestor '{templateId}' could not be read from finalized SPT data.");
        }

        if (!string.Equals(template.Id.ToString(), templateId, StringComparison.Ordinal) ||
            !string.Equals(template.Type, "Node", StringComparison.Ordinal))
        {
            throw new CargoCatalogValidationException(
                $"Template ancestor '{templateId}' is not a matching finalized category.");
        }

        templateCache.Add(templateId, template);
        return template;
    }

    private static void ValidateParentTopology(
        RewardForestNode parent,
        IReadOnlyList<RewardForestNode> children,
        IDictionary<string, TemplateItem> templates,
        IReadOnlyDictionary<string, HashSet<string>> ancestryByTemplate)
    {
        var properties = templates[parent.TemplateId].Properties
            ?? throw new CargoCatalogValidationException(
                $"Template '{parent.TemplateId}' has no item properties.");
        var slots = ReadSlotTargets(parent.TemplateId, properties);
        var grids = ReadGridTargets(parent.TemplateId, properties);
        if (slots.Keys.Intersect(grids.Keys, StringComparer.Ordinal).Any())
        {
            throw new CargoCatalogValidationException(
                $"Template '{parent.TemplateId}' has ambiguous slot and grid names.");
        }

        var occupiedSlots = new HashSet<string>(StringComparer.Ordinal);
        var occupiedGridCells = new Dictionary<string, List<GridRectangle>>(StringComparer.Ordinal);
        foreach (var child in children)
        {
            var slotId = child.SlotId
                ?? throw new CargoCatalogValidationException(
                    $"Node '{child.LogicalPath}' has no parent slot.");
            var childAncestry = ancestryByTemplate[child.TemplateId];
            if (child.InternalLocation is null)
            {
                if (!slots.TryGetValue(slotId, out var slot))
                {
                    throw new CargoCatalogValidationException(
                        $"Template '{parent.TemplateId}' has no slot '{slotId}'.");
                }
                if (!occupiedSlots.Add(slotId))
                {
                    throw new CargoCatalogValidationException(
                        $"Template '{parent.TemplateId}' has duplicate occupancy in slot '{slotId}'.");
                }
                if (!AcceptsSlotFilter(slot.Filters, childAncestry))
                {
                    throw new CargoCatalogValidationException(
                        $"Template '{child.TemplateId}' is rejected by slot '{slotId}'.");
                }
                if (slot.MaximumStackCount is > 0d &&
                    child.StackCount > slot.MaximumStackCount.Value)
                {
                    throw new CargoCatalogValidationException(
                        $"Node '{child.LogicalPath}' exceeds slot '{slotId}' stack bounds.");
                }

                continue;
            }

            if (!grids.TryGetValue(slotId, out var grid))
            {
                throw new CargoCatalogValidationException(
                    $"Template '{parent.TemplateId}' has no grid '{slotId}'.");
            }
            if (!AcceptsGridFilter(grid.Filters, childAncestry))
            {
                throw new CargoCatalogValidationException(
                    $"Template '{child.TemplateId}' is rejected by grid '{slotId}'.");
            }

            var childProperties = templates[child.TemplateId].Properties
                ?? throw new CargoCatalogValidationException(
                    $"Template '{child.TemplateId}' has no item properties.");
            var width = childProperties.Width!.Value;
            var height = childProperties.Height!.Value;
            if (child.InternalLocation.Rotation == CanonicalRotation.Vertical)
            {
                (width, height) = (height, width);
            }

            var rectangle = new GridRectangle(
                child.InternalLocation.X,
                child.InternalLocation.Y,
                (long)child.InternalLocation.X + width,
                (long)child.InternalLocation.Y + height);
            if (rectangle.Right > grid.Width || rectangle.Bottom > grid.Height)
            {
                throw new CargoCatalogValidationException(
                    $"Node '{child.LogicalPath}' is outside grid '{slotId}' bounds.");
            }

            if (!occupiedGridCells.TryGetValue(slotId, out var rectangles))
            {
                rectangles = [];
                occupiedGridCells.Add(slotId, rectangles);
            }
            if (rectangles.Any(existing => existing.Overlaps(rectangle)))
            {
                throw new CargoCatalogValidationException(
                    $"Node '{child.LogicalPath}' overlaps another item in grid '{slotId}'.");
            }
            rectangles.Add(rectangle);
        }

        foreach (var required in slots.Values.Where(slot => slot.Required))
        {
            if (!occupiedSlots.Contains(required.Name))
            {
                throw new CargoCatalogValidationException(
                    $"Template '{parent.TemplateId}' is missing mandatory slot '{required.Name}'.");
            }
        }
    }

    private static Dictionary<string, SlotTarget> ReadSlotTargets(
        string templateId,
        TemplateItemProperties properties)
    {
        var targets = new Dictionary<string, SlotTarget>(StringComparer.Ordinal);
        AddSlots(templateId, "slot", properties.Slots, targets);
        AddSlots(templateId, "chamber", properties.Chambers, targets);
        AddSlots(templateId, "cartridge", properties.Cartridges, targets);
        foreach (var stackSlot in ReadBounded(
                     properties.StackSlots,
                     templateId,
                     "stack slots",
                     MaxTemplateTargets))
        {
            AddSlotTarget(
                templateId,
                stackSlot.Name,
                required: false,
                stackSlot.MaxCount,
                stackSlot.Properties?.Filters,
                targets);
        }

        return targets;
    }

    private static void AddSlots(
        string templateId,
        string kind,
        IEnumerable<Slot>? source,
        IDictionary<string, SlotTarget> targets)
    {
        foreach (var slot in ReadBounded(
                     source,
                     templateId,
                     $"{kind}s",
                     MaxTemplateTargets))
        {
            AddSlotTarget(
                templateId,
                slot.Name,
                slot.Required is true,
                ResolveSlotMaximumStackCount(
                    templateId,
                    slot.Name,
                    slot.MaxCount,
                    slot.Properties?.MaxStackCount),
                slot.Properties?.Filters,
                targets);
        }
    }

    private static double? ResolveSlotMaximumStackCount(
        string templateId,
        string? slotName,
        double? maximumCount,
        double? legacyMaximumStackCount)
    {
        var normalizedMaximumCount = NormalizeMaximumStackCount(
            templateId,
            slotName,
            maximumCount);
        var normalizedLegacyMaximum = NormalizeMaximumStackCount(
            templateId,
            slotName,
            legacyMaximumStackCount);
        if (normalizedMaximumCount is not null &&
            normalizedLegacyMaximum is not null &&
            normalizedMaximumCount != normalizedLegacyMaximum)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' has conflicting stack bounds for slot '{slotName}'.");
        }

        return normalizedMaximumCount ?? normalizedLegacyMaximum;
    }

    private static double? NormalizeMaximumStackCount(
        string templateId,
        string? slotName,
        double? maximumStackCount)
    {
        // SPT uses zero for cartridge targets whose capacity is governed by
        // other finalized weapon data. Treat it as no local bound, not invalid.
        if (maximumStackCount is null or 0d)
        {
            return null;
        }
        if (!double.IsFinite(maximumStackCount.Value) ||
            maximumStackCount < 0d ||
            maximumStackCount != Math.Truncate(maximumStackCount.Value))
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' has invalid stack bounds for slot '{slotName}'.");
        }

        return maximumStackCount;
    }

    private static void AddSlotTarget(
        string templateId,
        string? name,
        bool required,
        double? maximumStackCount,
        IEnumerable<SlotFilter>? filters,
        IDictionary<string, SlotTarget> targets)
    {
        maximumStackCount = NormalizeMaximumStackCount(
            templateId,
            name,
            maximumStackCount);
        if (string.IsNullOrWhiteSpace(name) ||
            !targets.TryAdd(
                name ?? string.Empty,
                new SlotTarget(
                    name ?? string.Empty,
                    required,
                    maximumStackCount,
                    ReadBounded(filters, templateId, $"filters for '{name}'", MaxTargetFilters))))
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' has invalid or duplicate slot data.");
        }
    }

    private static Dictionary<string, GridTarget> ReadGridTargets(
        string templateId,
        TemplateItemProperties properties)
    {
        var targets = new Dictionary<string, GridTarget>(StringComparer.Ordinal);
        foreach (var grid in ReadBounded(
                     properties.Grids,
                     templateId,
                     "grids",
                     MaxTemplateTargets))
        {
            var gridProperties = grid.Properties;
            if (string.IsNullOrWhiteSpace(grid.Name) ||
                gridProperties?.CellsH is not > 0 ||
                gridProperties.CellsV is not > 0 ||
                gridProperties.CellsH > 256 ||
                gridProperties.CellsV > 256 ||
                !targets.TryAdd(
                    grid.Name,
                    new GridTarget(
                        grid.Name,
                        gridProperties.CellsH.Value,
                        gridProperties.CellsV.Value,
                        ReadBounded(
                            gridProperties.Filters,
                            templateId,
                            $"filters for grid '{grid.Name}'",
                            MaxTargetFilters))))
            {
                throw new CargoCatalogValidationException(
                    $"Template '{templateId}' has invalid or duplicate grid data.");
            }
        }

        return targets;
    }

    private static bool AcceptsSlotFilter(
        IReadOnlyList<SlotFilter> filters,
        IReadOnlySet<string> childAncestry) =>
        filters.Any(filter =>
            filter.Filter is { Count: > 0 } &&
            filter.Filter.Any(id => childAncestry.Contains(id.ToString())));

    private static bool AcceptsGridFilter(
        IReadOnlyList<GridFilter> filters,
        IReadOnlySet<string> childAncestry) =>
        filters.Any(filter =>
            filter.Filter is { Count: > 0 } &&
            filter.Filter.Any(id => childAncestry.Contains(id.ToString())) &&
            (filter.ExcludedFilter is null ||
             !filter.ExcludedFilter.Any(id => childAncestry.Contains(id.ToString()))));

    private static T[] ReadBounded<T>(
        IEnumerable<T>? source,
        string templateId,
        string description,
        int maximum)
        where T : class
    {
        if (source is null)
        {
            return [];
        }

        var values = source.Take(maximum + 1).ToArray();
        if (values.Length > maximum || values.Any(value => value is null))
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' has invalid or excessive {description}.");
        }

        return values;
    }

    private sealed record SlotTarget(
        string Name,
        bool Required,
        double? MaximumStackCount,
        IReadOnlyList<SlotFilter> Filters);

    private sealed record GridTarget(
        string Name,
        int Width,
        int Height,
        IReadOnlyList<GridFilter> Filters);

    private readonly record struct GridRectangle(
        long Left,
        long Top,
        long Right,
        long Bottom)
    {
        internal bool Overlaps(GridRectangle other) =>
            Left < other.Right &&
            Right > other.Left &&
            Top < other.Bottom &&
            Bottom > other.Top;
    }

    internal static void ValidateStackCount(
        string templateId,
        int stackCount,
        TemplateItem template)
    {
        var properties = template.Properties
            ?? throw new CargoCatalogValidationException(
                $"Template '{templateId}' has no item properties.");
        var maximum = properties.StackMaxSize ?? 1;
        if (maximum <= 0 || stackCount <= 0 || stackCount > maximum)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' stack count is outside its finalized bounds.");
        }
    }

    internal static double? GetFinalizedDurabilityMaximum(
        TemplateItemProperties properties) =>
        properties.MaxDurability is > 0d && double.IsFinite(properties.MaxDurability.Value)
            ? properties.MaxDurability.Value
            : null;

    internal static CargoTemplateResourceState? ResolveDefaultResource(
        string templateId,
        TemplateItemProperties properties)
    {
        var candidates = new List<CargoTemplateResourceState>(3);
        if (properties.MaxHpResource is > 0)
        {
            candidates.Add(new(
                RewardResourceKind.MedKit,
                properties.MaxHpResource.Value,
                properties.MaxHpResource.Value));
        }
        if (properties.MaxRepairResource is > 0)
        {
            candidates.Add(new(
                RewardResourceKind.RepairKit,
                properties.MaxRepairResource.Value,
                properties.MaxRepairResource.Value));
        }
        if (properties.Resource is > 0d)
        {
            var maximum = FirstPositive(properties.MaxResource, properties.Resource)!.Value;
            candidates.Add(new(
                RewardResourceKind.Generic,
                properties.Resource.Value,
                maximum));
        }
        else if (properties.MaxResource is > 0)
        {
            candidates.Add(new(
                RewardResourceKind.FoodDrink,
                properties.MaxResource.Value,
                properties.MaxResource.Value));
        }

        if (candidates.Count > 1)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' has ambiguous resource-property representations.");
        }
        if (candidates.Count == 0)
        {
            return null;
        }

        var candidate = candidates[0];
        if (candidate.Value > candidate.Maximum)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' resource exceeds its finalized maximum.");
        }

        return candidate;
    }

    internal static double GetResourceMaximumForKind(
        string templateId,
        TemplateItemProperties properties,
        RewardResourceKind kind)
    {
        double? maximum = kind switch
        {
            RewardResourceKind.MedKit => properties.MaxHpResource,
            RewardResourceKind.RepairKit => properties.MaxRepairResource,
            RewardResourceKind.FoodDrink => properties.MaxResource,
            RewardResourceKind.Generic => FirstPositive(
                properties.MaxResource,
                properties.Resource),
            _ => null
        };
        if (maximum is not > 0d)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' no longer supports {kind} resource state.");
        }

        return maximum.Value;
    }

    internal static decimal ToCanonicalDecimal(
        string templateId,
        string propertyName,
        double value)
    {
        if (!double.IsFinite(value) ||
            value < (double)decimal.MinValue ||
            value > (double)decimal.MaxValue)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' {propertyName} cannot be represented canonically.");
        }

        return checked((decimal)value);
    }

    private static void ValidateTemplateData(string templateId, TemplateItem template)
    {
        if (!string.Equals(template.Id.ToString(), templateId, StringComparison.Ordinal))
        {
            throw new CargoCatalogValidationException(
                $"Template lookup '{templateId}' returned mismatched custom-item data.");
        }
        if (!string.Equals(template.Type, "Item", StringComparison.Ordinal))
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' is not a concrete item.");
        }
        if (string.IsNullOrWhiteSpace(template.Parent.ToString()) || template.Properties is null)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' has incomplete base item data.");
        }

        var properties = template.Properties;
        if (properties.StackMaxSize is not > 0 ||
            properties.Width is not > 0 ||
            properties.Height is not > 0 ||
            properties.MaxHpResource is < 0 ||
            properties.MaxRepairResource is < 0 ||
            properties.MaxResource is < 0)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' has invalid stack or footprint data.");
        }
        ValidateOptionalFiniteNonNegative(
            templateId,
            "maximum durability",
            properties.MaxDurability);
        ValidateOptionalFiniteNonNegative(
            templateId,
            "durability",
            properties.Durability);
        ValidateOptionalFiniteNonNegative(templateId, "resource", properties.Resource);

        if (string.IsNullOrWhiteSpace(properties.Prefab?.Path) &&
            string.IsNullOrWhiteSpace(properties.UsePrefab?.Path))
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' has no renderable prefab reference.");
        }
    }

    private static void ValidateCustomItemData(
        string templateId,
        TemplateItem template,
        Func<string, TemplateItem, string?>? validateCustomItemData)
    {
        if (validateCustomItemData is null)
        {
            return;
        }

        string? rejection;
        try
        {
            rejection = validateCustomItemData(templateId, template);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' custom-item validation failed.");
        }
        if (!string.IsNullOrWhiteSpace(rejection))
        {
            throw new CargoCatalogValidationException(
                $"Template '{templateId}' failed custom-item validation: {rejection}");
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

    private static double? FirstPositive(params double?[] candidates) =>
        candidates.FirstOrDefault(candidate => candidate is > 0d);
}
