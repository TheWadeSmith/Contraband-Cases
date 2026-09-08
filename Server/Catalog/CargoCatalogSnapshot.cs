using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;

namespace ContrabandCases.Server.Catalog;

public sealed class CargoLotPack
{
    public CargoLotPack(
        string providerId,
        string packVersion,
        IEnumerable<CargoLotDefinition> lots)
        : this(
            providerId,
            packVersion,
            providerId,
            1d,
            [],
            [],
            [],
            lots)
    {
    }

    public CargoLotPack(
        string providerId,
        string packVersion,
        string displayLabel,
        double providerWeight,
        IEnumerable<string> requiredTemplateIds,
        IEnumerable<string> requiredPresetIds,
        IEnumerable<string> requiredBundleKeys,
        IEnumerable<CargoLotDefinition> lots,
        IEnumerable<string>? retiredLotIds = null)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new CargoCatalogValidationException("A pack provider ID is required.");
        }
        if (string.IsNullOrWhiteSpace(packVersion))
        {
            throw new CargoCatalogValidationException("A pack version is required.");
        }
        if (string.IsNullOrWhiteSpace(displayLabel))
        {
            throw new CargoCatalogValidationException("A pack display label is required.");
        }
        if (!double.IsFinite(providerWeight) || providerWeight is <= 0d or > 10d)
        {
            throw new CargoCatalogValidationException(
                "A pack provider weight must be positive, finite, and no greater than 10.");
        }
        if (lots is null)
        {
            throw new CargoCatalogValidationException("Pack lots are required.");
        }

        var templateRequirements = SnapshotRequirements(
            requiredTemplateIds,
            "template");
        var presetRequirements = SnapshotRequirements(
            requiredPresetIds,
            "preset");
        var bundleRequirements = SnapshotRequirements(
            requiredBundleKeys,
            "bundle");
        var snapshot = lots.ToArray();
        if (snapshot.Length == 0 || snapshot.Any(lot => lot is null))
        {
            throw new CargoCatalogValidationException(
                "A reward pack must contain at least one non-null lot.");
        }

        ProviderId = providerId;
        PackVersion = packVersion;
        DisplayLabel = displayLabel;
        ProviderWeight = providerWeight;
        RequiredTemplateIds = templateRequirements;
        RequiredPresetIds = presetRequirements;
        RequiredBundleKeys = bundleRequirements;
        Lots = new ReadOnlyCollection<CargoLotDefinition>(snapshot);
        RetiredLotIds = SnapshotRequirements(retiredLotIds ?? [], "retired lot");
        if (RetiredLotIds.Any(id => !snapshot.Any(lot => lot.LotId == id)) ||
            snapshot.All(lot => RetiredLotIds.Contains(lot.LotId)))
            throw new CargoCatalogValidationException("Retired lots must exist and leave at least one active lot.");
    }

    public string ProviderId { get; }

    public string PackVersion { get; }

    public string DisplayLabel { get; }

    public double ProviderWeight { get; }

    public IReadOnlyList<string> RequiredTemplateIds { get; }

    public IReadOnlyList<string> RequiredPresetIds { get; }

    public IReadOnlyList<string> RequiredBundleKeys { get; }

    public IReadOnlyList<CargoLotDefinition> Lots { get; }

    public IReadOnlyList<string> RetiredLotIds { get; }

    private static IReadOnlyList<string> SnapshotRequirements(
        IEnumerable<string> source,
        string kind)
    {
        if (source is null)
        {
            throw new CargoCatalogValidationException(
                $"Pack {kind} requirements are required.");
        }

        var values = source.ToArray();
        if (values.Length > 256 ||
            values.Any(string.IsNullOrWhiteSpace) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new CargoCatalogValidationException(
                $"Pack {kind} requirements are invalid, duplicate, or excessive.");
        }

        return new ReadOnlyCollection<string>(
            values.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
}

public sealed class SkippedCargoLotPack
{
    internal SkippedCargoLotPack(string providerId, string packVersion, string reason)
    {
        ProviderId = providerId;
        PackVersion = packVersion;
        Reason = reason;
    }

    public string ProviderId { get; }

    public string PackVersion { get; }

    public string Reason { get; }
}

public sealed class CargoCatalogSnapshot
{
    private const string InsufficientFamiliesReason =
        "Catalog has fewer than three distinct validated loot families.";

    internal CargoCatalogSnapshot(
        string sha256Hex,
        IEnumerable<ResolvedCargoLot> lots,
        IEnumerable<SkippedCargoLotPack> skippedPacks,
        IReadOnlyDictionary<string, double> providerWeights,
        string caseTemplateId = ModConstants.CaseTemplateId,
        long? casePrice = null,
        IEnumerable<string>? unavailableRetiredLots = null)
    {
        CaseTemplateId = CaseContracts.Require(caseTemplateId);
        CasePrice = casePrice;
        UnavailableRetiredLots = new ReadOnlyCollection<string>((unavailableRetiredLots ?? []).ToArray());
        Sha256Hex = sha256Hex;
        SnapshotId = string.Concat("catalog-v2/", sha256Hex);
        Lots = new ReadOnlyCollection<ResolvedCargoLot>(lots.ToArray());
        FreshOpeningLots = new ReadOnlyCollection<ResolvedCargoLot>(
            ShipmentEconomy.CurrentLots(Lots).Where(lot => !lot.IsRetired && ManifestOpeningPool.IsFreshEligible(lot) &&
                (caseTemplateId != CaseContracts.CashCache || casePrice.HasValue && lot.EvaluationOrNull is not null)).ToArray());
        SkippedPacks = new ReadOnlyCollection<SkippedCargoLotPack>(skippedPacks.ToArray());
        ProviderWeights = new ReadOnlyDictionary<string, double>(
            new Dictionary<string, double>(providerWeights, StringComparer.Ordinal));
        FamilyAssignments = CargoFamilyAssignmentEnumerator.Enumerate(FreshOpeningLots, caseTemplateId);
        OpeningEnabled = caseTemplateId == CaseContracts.CashCache
            ? FreshOpeningLots.Count > 0
            : FamilyAssignments.Count > 0;
        OpeningDisabledReason = OpeningEnabled ? null : caseTemplateId == CaseContracts.CashCache
            ? SkippedPacks.FirstOrDefault()?.Reason ?? "Cash Cache has no validated payouts."
            : InsufficientFamiliesReason;
    }

    public string SnapshotId { get; }

    public string CaseTemplateId { get; }

    public int OfferCount => CaseContracts.OfferCount(CaseTemplateId);

    public long? CasePrice { get; }

    public FamilyId SelectionFamily(ResolvedCargoLot lot) =>
        CaseContracts.SelectionFamily(CaseTemplateId, lot.Identity);

    public string Sha256Hex { get; }

    public IReadOnlyList<ResolvedCargoLot> Lots { get; }

    public IReadOnlyList<ResolvedCargoLot> FreshOpeningLots { get; }

    public IReadOnlyList<SkippedCargoLotPack> SkippedPacks { get; }

    public IReadOnlyList<string> UnavailableRetiredLots { get; }

    public IReadOnlyDictionary<string, double> ProviderWeights { get; }

    public IReadOnlyList<CargoFamilyAssignment> FamilyAssignments { get; }

    public bool OpeningEnabled { get; }

    public string? OpeningDisabledReason { get; }

    internal ResolvedCargoLot? ResolveExact(
        RewardRarity rarity,
        CargoLotIdentitySnapshot identity,
        RewardForest forest,
        RewardForestFingerprintV2 fingerprint,
        RarityLadderVersion rarityLadderVersion = RarityLadderVersion.FiveTier)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(forest);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var candidates = CaseTemplateId == CaseContracts.CashCache
            ? Lots.Select(lot => CashPayoutCatalog.ResolveSavedForest(lot, forest)).OfType<ResolvedCargoLot>()
            : Lots;
        var matches = candidates
            .Where(lot =>
                RelayRules.CatalogRarityForLadder(CaseTemplateId == CaseContracts.CashCache
                    ? CashPayoutCatalog.Grade(lot.Identity.LotId) : lot.Evaluation.Grade, rarityLadderVersion) == rarity &&
                IdentityEquals(lot.Identity, identity) &&
                lot.Fingerprint.Equals(fingerprint) &&
                identity.Fingerprint.Equals(fingerprint) &&
                ForestEquals(lot.Forest, forest))
            .Take(2)
            .ToArray();
        if (matches.Length > 1)
        {
            throw new CargoCatalogValidationException(
                "The frozen catalog contains duplicate exact cargo-lot identities.");
        }

        return matches.SingleOrDefault();
    }

    private static bool IdentityEquals(
        CargoLotIdentitySnapshot left,
        CargoLotIdentitySnapshot right) =>
        string.Equals(left.ProviderId, right.ProviderId, StringComparison.Ordinal) &&
        string.Equals(left.PackVersion, right.PackVersion, StringComparison.Ordinal) &&
        string.Equals(left.LotId, right.LotId, StringComparison.Ordinal) &&
        string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) &&
        string.Equals(left.Purpose, right.Purpose, StringComparison.Ordinal) &&
        left.FamilyId.Equals(right.FamilyId) &&
        left.TrackId.Equals(right.TrackId) &&
        string.Equals(left.AnchorTemplateId, right.AnchorTemplateId, StringComparison.Ordinal) &&
        BitConverter.DoubleToInt64Bits(left.Weight) == BitConverter.DoubleToInt64Bits(right.Weight) &&
        UsePathEquals(left.UsePath, right.UsePath) &&
        left.Fingerprint.Equals(right.Fingerprint);

    private static bool UsePathEquals(UsePath left, UsePath right) =>
        (left, right) switch
        {
            (RaidRole first, RaidRole second) =>
                string.Equals(first.RoleId, second.RoleId, StringComparison.Ordinal),
            (Collection first, Collection second) =>
                string.Equals(
                    first.ContainerTemplateId,
                    second.ContainerTemplateId,
                    StringComparison.Ordinal) &&
                string.Equals(first.CollectionId, second.CollectionId, StringComparison.Ordinal),
            (Craft first, Craft second) =>
                string.Equals(first.ProductionId, second.ProductionId, StringComparison.Ordinal),
            (Barter first, Barter second) =>
                string.Equals(first.TraderId, second.TraderId, StringComparison.Ordinal) &&
                string.Equals(first.AssortId, second.AssortId, StringComparison.Ordinal),
            _ => false
        };

    private static bool ForestEquals(RewardForest left, RewardForest right)
    {
        if (left.Nodes.Count != right.Nodes.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Nodes.Count; index++)
        {
            var first = left.Nodes[index];
            var second = right.Nodes[index];
            if (!string.Equals(first.TreeRootPath, second.TreeRootPath, StringComparison.Ordinal) ||
                !string.Equals(first.LogicalPath, second.LogicalPath, StringComparison.Ordinal) ||
                !string.Equals(first.TemplateId, second.TemplateId, StringComparison.Ordinal) ||
                !string.Equals(first.ParentLogicalPath, second.ParentLogicalPath, StringComparison.Ordinal) ||
                !string.Equals(first.SlotId, second.SlotId, StringComparison.Ordinal) ||
                first.StackCount != second.StackCount ||
                !Equals(first.InternalLocation, second.InternalLocation) ||
                !Equals(first.StableState, second.StableState))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Builds one immutable catalog view. Core validation is fail-fast; each
/// optional pack resolves into a temporary buffer and is admitted atomically.
/// </summary>
public sealed class CargoCatalogSnapshotBuilder
{
    private readonly CargoLotResolver _resolver;
    private readonly CargoLotEvaluator _evaluator;
    private readonly Action<CargoLotPack>? _validateRequirements;

    public CargoCatalogSnapshotBuilder(
        CargoLotResolver resolver,
        CargoLotEvaluator evaluator,
        Action<CargoLotPack>? validateRequirements = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _validateRequirements = validateRequirements;
    }

    public CargoCatalogSnapshot Build(
        CargoLotPack corePack,
        IEnumerable<CargoLotPack>? optionalPacks = null,
        IEnumerable<SkippedCargoLotPack>? initialSkippedPacks = null)
    {
        ArgumentNullException.ThrowIfNull(corePack);
        var optional = (optionalPacks ?? []).ToArray();
        if (optional.Any(pack => pack is null))
        {
            throw new CargoCatalogValidationException("Optional packs cannot contain null.");
        }

        var active = new List<ResolvedCargoLot>();
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        var providerWeights = new Dictionary<string, double>(StringComparer.Ordinal);
        var unavailableRetired = new List<string>();
        var coreLots = ResolvePack(corePack, unavailableRetired);
        AddPackAtomically(corePack, coreLots, active, occupied, providerWeights);

        var skipped = new List<SkippedCargoLotPack>(initialSkippedPacks ?? []);
        var duplicateProviders = optional
            .GroupBy(pack => pack.ProviderId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var pack in optional
                     .Where(pack =>
                         duplicateProviders.Contains(pack.ProviderId) ||
                         string.Equals(pack.ProviderId, corePack.ProviderId, StringComparison.Ordinal))
                     .OrderBy(pack => pack.ProviderId, StringComparer.Ordinal)
                     .ThenBy(pack => pack.PackVersion, StringComparer.Ordinal))
        {
            skipped.Add(new SkippedCargoLotPack(
                pack.ProviderId,
                pack.PackVersion,
                $"Provider '{pack.ProviderId}' has a duplicate active pack identity."));
        }

        foreach (var pack in optional
                     .Where(pack =>
                         !duplicateProviders.Contains(pack.ProviderId) &&
                         !string.Equals(pack.ProviderId, corePack.ProviderId, StringComparison.Ordinal))
                     .OrderBy(pack => pack.ProviderId, StringComparer.Ordinal)
                     .ThenBy(pack => pack.PackVersion, StringComparer.Ordinal))
        {
            try
            {
                var resolved = ResolvePack(pack, unavailableRetired);
                AddPackAtomically(pack, resolved, active, occupied, providerWeights);
            }
            catch (CargoCatalogValidationException exception)
            {
                skipped.Add(new SkippedCargoLotPack(
                    pack.ProviderId,
                    pack.PackVersion,
                    exception.Message));
            }
        }

        var ordered = active
            .OrderBy(lot => lot.Identity.ProviderId, StringComparer.Ordinal)
            .ThenBy(lot => lot.Identity.PackVersion, StringComparer.Ordinal)
            .ThenBy(lot => lot.Identity.LotId, StringComparer.Ordinal)
            .ToArray();
        return new CargoCatalogSnapshot(
            HashSnapshot(ordered, providerWeights),
            ordered,
            skipped
                .OrderBy(pack => pack.ProviderId, StringComparer.Ordinal)
                .ThenBy(pack => pack.PackVersion, StringComparer.Ordinal),
            providerWeights,
            unavailableRetiredLots: unavailableRetired);
    }

    private IReadOnlyList<ResolvedCargoLot> ResolvePack(CargoLotPack pack, ICollection<string> unavailableRetired)
    {
        _validateRequirements?.Invoke(pack);
        if (pack.Lots.Any(lot =>
                !string.Equals(lot.ProviderId, pack.ProviderId, StringComparison.Ordinal) ||
                !string.Equals(lot.PackVersion, pack.PackVersion, StringComparison.Ordinal)))
        {
            throw new CargoCatalogValidationException(
                $"Pack '{pack.ProviderId}' contains mismatched provider or version metadata.");
        }

        var duplicate = pack.Lots
            .GroupBy(lot => lot.LotId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
        {
            throw new CargoCatalogValidationException(
                $"Pack '{pack.ProviderId}' contains duplicate lot '{duplicate.Key}'.");
        }

        var resolved = new List<ResolvedCargoLot>();
        foreach (var definition in pack.Lots)
        {
            var retired = pack.RetiredLotIds.Contains(definition.LotId);
            try
            {
                var lot = _evaluator.Evaluate(_resolver.Resolve(definition));
                resolved.Add(retired ? lot.AsRetired() : lot);
            }
            catch (CargoCatalogValidationException exception) when (retired)
            {
                // Historical definitions are recovery-only, not current pack
                // requirements. Invalid old forests remain blocked by ResolveExact;
                // never repair, substitute or silently award a different paid reward.
                unavailableRetired.Add($"{pack.ProviderId}/{definition.LotId}: {exception.Message}");
            }
        }
        return resolved;
    }

    private static void AddPackAtomically(
        CargoLotPack pack,
        IReadOnlyList<ResolvedCargoLot> resolved,
        ICollection<ResolvedCargoLot> active,
        ISet<string> occupied,
        IDictionary<string, double> providerWeights)
    {
        var keys = resolved
            .Select(lot => IdentityKey(lot.Identity.ProviderId, lot.Identity.LotId))
            .ToArray();
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length ||
            keys.Any(occupied.Contains))
        {
            throw new CargoCatalogValidationException(
                $"Pack '{pack.ProviderId}' conflicts with an active lot identity.");
        }

        foreach (var key in keys)
        {
            occupied.Add(key);
        }
        foreach (var lot in resolved)
        {
            active.Add(lot);
        }
        providerWeights.Add(pack.ProviderId, pack.ProviderWeight);
    }

    private static string IdentityKey(string providerId, string lotId) =>
        string.Concat(providerId, "\0", lotId);

    private static string HashSnapshot(
        IReadOnlyList<ResolvedCargoLot> lots,
        IReadOnlyDictionary<string, double> providerWeights)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream,
                   new UTF8Encoding(false, true),
                   leaveOpen: true))
        {
            WriteString(writer, "contraband-cases/catalog-snapshot/v2");
            WriteString(writer, CargoFamilies.SelectionVersion);
            WriteString(writer, ManifestOpeningPool.SelectionVersion);
            WriteString(writer, "surprise-three-choice-v1-150-20bp-valuefloors");
            writer.Write(providerWeights.Count);
            foreach (var provider in providerWeights.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                WriteString(writer, provider.Key);
                WriteString(
                    writer,
                    provider.Value.ToString("R", CultureInfo.InvariantCulture));
            }
            writer.Write(lots.Count);
            foreach (var lot in lots)
            {
                var identity = lot.Identity;
                WriteString(writer, identity.ProviderId);
                WriteString(writer, identity.PackVersion);
                WriteString(writer, identity.LotId);
                WriteString(writer, identity.DisplayName);
                WriteString(writer, identity.Purpose);
                WriteString(writer, identity.FamilyId.Value);
                WriteString(writer, identity.TrackId.Value);
                WriteString(writer, identity.AnchorTemplateId);
                WriteString(writer, identity.Weight.ToString("R", CultureInfo.InvariantCulture));
                WriteUsePath(writer, identity.UsePath);
                WriteString(writer, identity.Fingerprint.Sha256Hex);
                writer.Write(lot.Evaluation.HandbookValue);
                writer.Write(lot.Evaluation.UseValue);
                writer.Write(lot.Evaluation.FootprintCells);
                writer.Write((int)lot.Evaluation.Grade);
                if (lot.IsRetired) WriteString(writer, "recovery-only");
            }
        }

        return ToLowerHex(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteUsePath(BinaryWriter writer, UsePath usePath)
    {
        switch (usePath)
        {
            case RaidRole raidRole:
                WriteString(writer, "raid-role");
                WriteString(writer, raidRole.RoleId);
                break;
            case Collection collection:
                WriteString(writer, "collection");
                WriteString(writer, collection.ContainerTemplateId);
                WriteString(writer, collection.CollectionId);
                break;
            case Craft craft:
                WriteString(writer, "craft");
                WriteString(writer, craft.ProductionId);
                break;
            case Barter barter:
                WriteString(writer, "barter");
                WriteString(writer, barter.TraderId);
                WriteString(writer, barter.AssortId);
                break;
            default:
                throw new CargoCatalogValidationException("Snapshot contains an unsupported use path.");
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ToLowerHex(byte[] bytes)
    {
        var characters = new char[bytes.Length * 2];
        const string alphabet = "0123456789abcdef";
        for (var index = 0; index < bytes.Length; index++)
        {
            characters[index * 2] = alphabet[bytes[index] >> 4];
            characters[(index * 2) + 1] = alphabet[bytes[index] & 0x0f];
        }
        return new string(characters);
    }
}
