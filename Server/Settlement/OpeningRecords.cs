using System.Collections.ObjectModel;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Utils.Json.Converters;

namespace ContrabandCases.Server.Settlement;

public enum OpeningRecordStatus
{
    Prepared,
    Committed
}

public sealed class CaseOpeningRecord
{
    private static readonly JsonSerializerOptions CloneSerializerOptions = CreateCloneSerializerOptions();
    private readonly IReadOnlyList<Item> _rewardItems;
    private readonly IReadOnlyList<MongoId> _exactRewardIds;

    public CaseOpeningRecord(
        MongoId caseId,
        MongoId keyId,
        string rewardId,
        List<Item> rewardItems,
        DateTimeOffset preparedAtUtc,
        OpeningRecordStatus status,
        DateTimeOffset? committedAtUtc,
        RarityLadderVersion rarityLadderVersion = RarityLadderVersion.FiveTier,
        ManifestClaimPreparedPayload? mailDelivery = null)
    {
        if (string.IsNullOrWhiteSpace(rewardId))
        {
            throw new ArgumentException("A catalog reward ID is required.", nameof(rewardId));
        }
        ArgumentNullException.ThrowIfNull(rewardItems);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Opening record status is not recognized.");
        }
        RelayRules.ValidateLadderVersion(rarityLadderVersion, nameof(rarityLadderVersion));
        if (preparedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Prepared timestamp must be UTC.", nameof(preparedAtUtc));
        }
        if (committedAtUtc is { Offset: var committedOffset } && committedOffset != TimeSpan.Zero)
        {
            throw new ArgumentException("Committed timestamp must be UTC.", nameof(committedAtUtc));
        }
        if (status == OpeningRecordStatus.Committed && committedAtUtc is null)
        {
            throw new ArgumentException("Committed records require a committed timestamp.", nameof(committedAtUtc));
        }
        if (status == OpeningRecordStatus.Prepared && committedAtUtc is not null)
        {
            throw new ArgumentException("Prepared records cannot have a committed timestamp.", nameof(committedAtUtc));
        }

        CaseId = caseId;
        KeyId = keyId;
        RewardId = rewardId;
        var clonedRewardItems = rewardItems.Select(CloneItem).ToArray();
        if (caseId == keyId)
        {
            throw new ArgumentException("Case and key IDs must be distinct.", nameof(keyId));
        }
        if (clonedRewardItems.Select(item => item.Id).Distinct().Count() != clonedRewardItems.Length)
        {
            throw new ArgumentException("Reward item IDs must be unique.", nameof(rewardItems));
        }
        if (clonedRewardItems.Any(item => item.Id == caseId || item.Id == keyId))
        {
            throw new ArgumentException("Reward item IDs must be distinct from the case and key IDs.", nameof(rewardItems));
        }

        _rewardItems = new ReadOnlyCollection<Item>(clonedRewardItems);
        _exactRewardIds = new ReadOnlyCollection<MongoId>(clonedRewardItems.Select(item => item.Id).ToArray());
        PreparedAtUtc = preparedAtUtc;
        Status = status;
        CommittedAtUtc = committedAtUtc;
        RarityLadderVersion = rarityLadderVersion;
        LegacyRewardMail.Validate(mailDelivery, _rewardItems, preparedAtUtc, status == OpeningRecordStatus.Committed);
        MailDelivery = mailDelivery;
    }

    public MongoId CaseId { get; }
    public MongoId KeyId { get; }
    public string RewardId { get; }
    public IReadOnlyList<Item> RewardItems => new ReadOnlyCollection<Item>(_rewardItems.Select(CloneItem).ToArray());
    public IReadOnlyList<MongoId> ExactRewardIds => _exactRewardIds;
    public MongoId? RewardRootId => SettlementItemTrees.FindRootId(_rewardItems);
    public DateTimeOffset PreparedAtUtc { get; }
    public DateTimeOffset? CommittedAtUtc { get; }
    public OpeningRecordStatus Status { get; }
    public RarityLadderVersion RarityLadderVersion { get; }
    public ManifestClaimPreparedPayload? MailDelivery { get; }

    public CaseOpeningRecord WithMailDelivery(ManifestClaimPreparedPayload mailDelivery) => new(
        CaseId, KeyId, RewardId, RewardItems.ToList(), PreparedAtUtc, Status, CommittedAtUtc,
        RarityLadderVersion, mailDelivery);

    public CaseOpeningRecord Commit(DateTimeOffset committedAtUtc) =>
        new(
            CaseId,
            KeyId,
            RewardId,
            RewardItems.ToList(),
            PreparedAtUtc,
            OpeningRecordStatus.Committed,
            committedAtUtc,
            RarityLadderVersion,
            MailDelivery);

    internal static Item CloneItem(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var context = new CloneContext();
#pragma warning disable CS8619
        return item with
        {
            Location = CloneValue(item.Location, context),
            Upd = CloneReference(item.Upd, context),
            ExtensionData = CloneExtensionData(item.ExtensionData, context)
        };
#pragma warning restore CS8619
    }

#pragma warning disable CS8714
    private static Dictionary<string, object?> CloneExtensionData(Dictionary<string?, object?>? source, CloneContext context)
    {
        var clone = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (source is null)
        {
            return clone;
        }

        if (context.TryGetCompleted(source, out var existing))
        {
            return (Dictionary<string, object?>)existing;
        }

        context.Enter(source);
        try
        {
            foreach (var pair in source)
            {
                clone[pair.Key ?? string.Empty] = CloneValue(pair.Value, context);
            }

            context.Complete(source, clone);
            return clone;
        }
        finally
        {
            context.Abandon(source);
        }
    }
#pragma warning restore CS8714

    private static T? CloneReference<T>(T? value, CloneContext context) where T : class => (T?)CloneValue(value, context);

    private static object? CloneValue(object? value, CloneContext context)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string || value.GetType().IsValueType)
        {
            return value;
        }

        if (context.TryGetCompleted(value, out var existing))
        {
            return existing;
        }

        if (value is Array array)
        {
            return CloneArray(array, context);
        }

        if (value is IDictionary dictionary)
        {
            return CloneDictionary(dictionary, context);
        }

        if (value is IList list)
        {
            return CloneList(list, context);
        }

        var type = value.GetType();
        context.Enter(value);
        try
        {
            object clone;
            try
            {
                var json = JsonSerializer.Serialize(value, type, CloneSerializerOptions);
                clone = JsonSerializer.Deserialize(json, type, CloneSerializerOptions)
                    ?? throw new InvalidOperationException($"Could not clone settlement value of type '{type.FullName}'.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"Could not clone settlement value of type '{type.FullName}'.", exception);
            }
            catch (NotSupportedException exception)
            {
                throw new InvalidOperationException($"Could not clone settlement value of type '{type.FullName}'.", exception);
            }

            RestoreExtensionDataGraph(value, clone, new HashSet<ReferencePair>(ReferencePairComparer.Instance), context);
            context.Complete(value, clone);
            return clone;
        }
        finally
        {
            context.Abandon(value);
        }
    }

    private static Array CloneArray(Array source, CloneContext context)
    {
        if (source.Rank != 1)
        {
            throw new InvalidOperationException("Settlement extension arrays must be one-dimensional.");
        }

        var clone = Array.CreateInstance(source.GetType().GetElementType()!, source.Length);
        context.Enter(source);
        try
        {
            for (var index = 0; index < source.Length; index++)
            {
                clone.SetValue(CloneValue(source.GetValue(index), context), index);
            }

            context.Complete(source, clone);
            return clone;
        }
        finally
        {
            context.Abandon(source);
        }
    }

    private static IDictionary CloneDictionary(IDictionary source, CloneContext context)
    {
        if (context.TryGetCompleted(source, out var existing))
        {
            return (IDictionary)existing;
        }

        if (Activator.CreateInstance(source.GetType()) is not IDictionary clone)
        {
            throw new InvalidOperationException($"Could not clone settlement dictionary type '{source.GetType().FullName}'.");
        }

        context.Enter(source);
        try
        {
            foreach (DictionaryEntry entry in source)
            {
                var key = CloneValue(entry.Key, context) ?? throw new InvalidOperationException("Settlement extension dictionary keys cannot be null.");
                clone.Add(key, CloneValue(entry.Value, context));
            }

            context.Complete(source, clone);
            return clone;
        }
        finally
        {
            context.Abandon(source);
        }
    }

    private static IList CloneList(IList source, CloneContext context)
    {
        if (Activator.CreateInstance(source.GetType()) is not IList clone)
        {
            throw new InvalidOperationException($"Could not clone settlement list type '{source.GetType().FullName}'.");
        }

        context.Enter(source);
        try
        {
            foreach (var value in source)
            {
                clone.Add(CloneValue(value, context));
            }

            context.Complete(source, clone);
            return clone;
        }
        finally
        {
            context.Abandon(source);
        }
    }

    private static void RestoreExtensionDataGraph(
        object source,
        object clone,
        HashSet<ReferencePair> visited,
        CloneContext context)
    {
        if (!visited.Add(new ReferencePair(source, clone)))
        {
            return;
        }

        if (source is Array sourceArray && clone is Array cloneArray)
        {
            if (sourceArray.Rank != cloneArray.Rank || sourceArray.Length != cloneArray.Length)
            {
                throw new InvalidOperationException("Settlement clone changed an array shape.");
            }

            for (var index = 0; index < sourceArray.Length; index++)
            {
                RestoreExtensionDataValue(sourceArray.GetValue(index), cloneArray.GetValue(index), visited, context);
            }

            return;
        }

        if (source is IList sourceList && clone is IList cloneList)
        {
            if (sourceList.Count != cloneList.Count)
            {
                throw new InvalidOperationException("Settlement clone changed a list length.");
            }

            for (var index = 0; index < sourceList.Count; index++)
            {
                RestoreExtensionDataValue(sourceList[index], cloneList[index], visited, context);
            }

            return;
        }

        if (source is IDictionary sourceDictionary && clone is IDictionary cloneDictionary)
        {
            if (sourceDictionary.Count != cloneDictionary.Count)
            {
                throw new InvalidOperationException("Settlement clone changed a dictionary size.");
            }

            foreach (DictionaryEntry entry in sourceDictionary)
            {
                if (entry.Key is null || !cloneDictionary.Contains(entry.Key))
                {
                    throw new InvalidOperationException("Settlement clone changed a dictionary key.");
                }

                RestoreExtensionDataValue(entry.Value, cloneDictionary[entry.Key], visited, context);
            }

            return;
        }

        foreach (var property in source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var sourceValue = property.GetValue(source);
            if (sourceValue is null)
            {
                continue;
            }

            if (property.Name == "ExtensionData" && property.CanWrite && sourceValue is IDictionary extensionData)
            {
                property.SetValue(clone, CloneDictionary(extensionData, context));
                continue;
            }

            var cloneValue = property.GetValue(clone);
            RestoreExtensionDataValue(sourceValue, cloneValue, visited, context);
        }
    }

    private static void RestoreExtensionDataValue(
        object? sourceValue,
        object? cloneValue,
        HashSet<ReferencePair> visited,
        CloneContext context)
    {
        if (sourceValue is null)
        {
            if (cloneValue is not null)
            {
                throw new InvalidOperationException("Settlement clone changed a null value.");
            }

            return;
        }

        if (sourceValue is string || sourceValue.GetType().IsValueType)
        {
            return;
        }

        if ((sourceValue is IDictionary && cloneValue is IDictionary) ||
            (IsListOrArray(sourceValue) && IsListOrArray(cloneValue)))
        {
            RestoreExtensionDataGraph(sourceValue, cloneValue!, visited, context);
            return;
        }

        if (cloneValue is null || sourceValue.GetType() != cloneValue.GetType())
        {
            throw new InvalidOperationException($"Settlement clone changed value type '{sourceValue.GetType().FullName}'.");
        }

        RestoreExtensionDataGraph(sourceValue, cloneValue, visited, context);
    }

    private static bool IsListOrArray(object? value) => value is Array or IList;

    private sealed class CloneContext
    {
        private readonly Dictionary<object, object> _completed = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _active = new(ReferenceEqualityComparer.Instance);

        public bool TryGetCompleted(object source, out object clone)
        {
            if (_active.Contains(source))
            {
                throw new InvalidOperationException("Settlement values cannot contain reference cycles.");
            }

            return _completed.TryGetValue(source, out clone!);
        }

        public void Enter(object source)
        {
            if (!_active.Add(source))
            {
                throw new InvalidOperationException("Settlement values cannot contain reference cycles.");
            }
        }

        public void Complete(object source, object clone)
        {
            _active.Remove(source);
            _completed.Add(source, clone);
        }

        public void Abandon(object source) => _active.Remove(source);
    }

    private readonly record struct ReferencePair(object Source, object Copy);

    private sealed class ReferencePairComparer : IEqualityComparer<ReferencePair>
    {
        public static ReferencePairComparer Instance { get; } = new();

        public bool Equals(ReferencePair x, ReferencePair y) =>
            ReferenceEquals(x.Source, y.Source) && ReferenceEquals(x.Copy, y.Copy);

        public int GetHashCode(ReferencePair pair) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(pair.Source), RuntimeHelpers.GetHashCode(pair.Copy));
    }

    private static JsonSerializerOptions CreateCloneSerializerOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new StringToMongoIdConverter());
        return options;
    }
}

public sealed class CaseOpeningJournal
{
    private const int RetainedCommittedCount = 256;
    internal const int RetainedManifestReceiptCount = 256;
    private readonly List<CaseOpeningRecord> _records;
    private readonly List<RelaySettlementRecord> _relayRecords;
    private readonly List<ManifestTerminalReceipt> _manifestReceipts;
    private readonly List<ManifestClaimGrantRecord> _manifestClaimGrants;
    private readonly HashSet<MongoId> _legacySecuredStakeRoots;

    public CaseOpeningJournal(
        IEnumerable<CaseOpeningRecord>? records = null,
        IEnumerable<RelaySettlementRecord>? relayRecords = null,
        int recoveryMeter = 0,
        IEnumerable<MongoId>? legacySecuredStakeRoots = null,
        ManifestRecord? activeManifest = null,
        IEnumerable<ManifestTerminalReceipt>? manifestReceipts = null,
        IEnumerable<ManifestClaimGrantRecord>? manifestClaimGrants = null)
    {
        _records = records?.ToList() ?? new List<CaseOpeningRecord>();
        _relayRecords = relayRecords?.ToList() ?? new List<RelaySettlementRecord>();
        _manifestReceipts = manifestReceipts?.ToList() ?? new List<ManifestTerminalReceipt>();
        _manifestClaimGrants = manifestClaimGrants?.ToList() ?? new List<ManifestClaimGrantRecord>();
        _legacySecuredStakeRoots = legacySecuredStakeRoots?.ToHashSet() ?? [];
        if (_records.Any(record => record is null))
        {
            throw new ArgumentException("Journal records cannot contain null.", nameof(records));
        }
        if (_records.GroupBy(record => record.CaseId).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Journal cannot contain duplicate case IDs.", nameof(records));
        }
        if (_relayRecords.Any(record => record is null))
        {
            throw new ArgumentException("Relay journal records cannot contain null.", nameof(relayRecords));
        }
        if (_relayRecords.GroupBy(record => record.StakeRootId).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Relay journal cannot contain duplicate stake root IDs.", nameof(relayRecords));
        }
        if (_manifestReceipts.Any(receipt => receipt is null))
        {
            throw new ArgumentException("Manifest receipts cannot contain null.", nameof(manifestReceipts));
        }
        if (_manifestReceipts
            .GroupBy(receipt => receipt.ManifestId, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Manifest receipts cannot contain duplicate manifest IDs.", nameof(manifestReceipts));
        }
        if (_manifestClaimGrants.Any(grant => grant is null))
        {
            throw new ArgumentException("Manifest Claim grants cannot contain null.", nameof(manifestClaimGrants));
        }
        if (_manifestClaimGrants
            .GroupBy(grant => grant.ManifestId, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            throw new ArgumentException(
                "Manifest Claim grants cannot contain duplicate manifest IDs.",
                nameof(manifestClaimGrants));
        }
        ValidateManifestClaimGrantPairs(_manifestReceipts, _manifestClaimGrants);
        if (activeManifest is not null && _manifestReceipts.Any(receipt =>
                string.Equals(receipt.ManifestId, activeManifest.ManifestId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("An active manifest cannot already have a terminal receipt.", nameof(activeManifest));
        }
        if (_records.Count(record => record.Status == OpeningRecordStatus.Prepared) +
            _relayRecords.Count(record => record.Status == RelayRecordStatus.Prepared) +
            (activeManifest?.RequiresExclusiveProfileMutation == true ? 1 : 0) > 1)
        {
            throw new ArgumentException(
                "The journal cannot contain more than one prepared profile transaction.");
        }
        if (recoveryMeter is < 0 or > Shared.Relay.RelayRules.MaximumRecoveryMeter)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryMeter));
        }
        if (activeManifest is not null && activeManifest.BrokerFavor != recoveryMeter)
        {
            throw new ArgumentException(
                "Active manifest Broker Favor must match the journal recovery meter.",
                nameof(activeManifest));
        }
        if (activeManifest?.FlowState.Phase == Shared.Manifest.ManifestPhase.Granted)
        {
            throw new ArgumentException(
                "A Granted manifest must be finalized atomically and cannot remain active.",
                nameof(activeManifest));
        }

        RecoveryMeter = recoveryMeter;
        ActiveManifest = activeManifest;
        PruneCommitted();
        PruneManifestHistory();
    }

    public IReadOnlyList<CaseOpeningRecord> Records => _records.AsReadOnly();
    public IReadOnlyList<RelaySettlementRecord> RelayRecords => _relayRecords.AsReadOnly();
    public ManifestRecord? ActiveManifest { get; private set; }
    public IReadOnlyList<ManifestTerminalReceipt> ManifestReceipts =>
        new ReadOnlyCollection<ManifestTerminalReceipt>(_manifestReceipts.ToArray());
    internal IReadOnlyList<ManifestClaimGrantRecord> ManifestClaimGrants =>
        new ReadOnlyCollection<ManifestClaimGrantRecord>(
            _manifestClaimGrants.Select(grant => grant.Clone()).ToArray());
    public int RecoveryMeter { get; private set; }
    public int BrokerFavor => RecoveryMeter;
    public IReadOnlyCollection<MongoId> LegacySecuredStakeRoots => _legacySecuredStakeRoots;

    public CaseOpeningRecord? Find(MongoId caseId) => _records.SingleOrDefault(record => record.CaseId.Equals(caseId));
    public RelaySettlementRecord? FindRelay(MongoId stakeRootId) =>
        _relayRecords.SingleOrDefault(record => record.StakeRootId.Equals(stakeRootId));
    public ManifestClaimGrantRecord? FindManifestClaimGrant(string manifestId)
    {
        ManifestRecordValidation.RequireIdentifier(manifestId, nameof(manifestId));
        return _manifestClaimGrants.SingleOrDefault(grant =>
            string.Equals(grant.ManifestId, manifestId, StringComparison.Ordinal))?.Clone();
    }
    public CaseOpeningRecord? PreparedOpening =>
        _records.SingleOrDefault(record => record.Status == OpeningRecordStatus.Prepared);
    public RelaySettlementRecord? PreparedRelay =>
        _relayRecords.SingleOrDefault(record => record.Status == RelayRecordStatus.Prepared);
    public bool IsLegacySecured(MongoId stakeRootId) => _legacySecuredStakeRoots.Contains(stakeRootId);

    public void Add(CaseOpeningRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (Find(record.CaseId) is not null)
        {
            throw new InvalidOperationException("A journal record for this case already exists.");
        }
        if (record.Status == OpeningRecordStatus.Prepared && HasPreparedTransaction())
        {
            throw new InvalidOperationException("The profile already has a prepared settlement transaction.");
        }

        _records.Add(record);
    }

    public void Replace(CaseOpeningRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var index = _records.FindIndex(existing => existing.CaseId.Equals(record.CaseId));
        if (index < 0)
        {
            throw new InvalidOperationException("Cannot replace a journal record that does not exist.");
        }
        ValidateOpeningReplacement(_records[index], record);
        if (record.Status == OpeningRecordStatus.Prepared && HasPreparedTransaction(excludedOpeningIndex: index))
        {
            throw new InvalidOperationException("The profile already has a prepared settlement transaction.");
        }

        _records[index] = record;
    }

    public void AddRelay(RelaySettlementRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (FindRelay(record.StakeRootId) is not null)
        {
            throw new InvalidOperationException("A Relay journal record for this stake already exists.");
        }
        if (record.Status == RelayRecordStatus.Prepared && HasPreparedTransaction())
        {
            throw new InvalidOperationException("The profile already has a prepared settlement transaction.");
        }

        _relayRecords.Add(record);
    }

    public void ReplaceRelay(RelaySettlementRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var index = _relayRecords.FindIndex(existing => existing.StakeRootId.Equals(record.StakeRootId));
        if (index < 0)
        {
            throw new InvalidOperationException("Cannot replace a Relay journal record that does not exist.");
        }
        ValidateRelayReplacement(_relayRecords[index], record);
        if (record.Status == RelayRecordStatus.Prepared && HasPreparedTransaction(excludedRelayIndex: index))
        {
            throw new InvalidOperationException("The profile already has a prepared settlement transaction.");
        }

        _relayRecords[index] = record;
    }

    private static void ValidateOpeningReplacement(
        CaseOpeningRecord active,
        CaseOpeningRecord replacement)
    {
        LegacyRewardMail.ValidateReplacement(active.MailDelivery, replacement.MailDelivery,
            active.Status == OpeningRecordStatus.Committed);
        if (active.KeyId != replacement.KeyId ||
            !string.Equals(active.RewardId, replacement.RewardId, StringComparison.Ordinal) ||
            active.RarityLadderVersion != replacement.RarityLadderVersion ||
            active.PreparedAtUtc != replacement.PreparedAtUtc)
        {
            throw new InvalidOperationException("A case opening replacement cannot rewrite prepared evidence.");
        }
        var legalStatus = active.Status switch
        {
            OpeningRecordStatus.Prepared => replacement.Status is
                OpeningRecordStatus.Prepared or OpeningRecordStatus.Committed,
            OpeningRecordStatus.Committed => replacement.Status == OpeningRecordStatus.Committed &&
                replacement.CommittedAtUtc == active.CommittedAtUtc,
            _ => false
        };
        if (!legalStatus)
        {
            throw new InvalidOperationException("A case opening replacement cannot move settlement status backward.");
        }

        if (active.Status == OpeningRecordStatus.Prepared &&
            replacement.Status == OpeningRecordStatus.Prepared)
        {
            RequireExactItemEvidenceAllowingRootPlacement(
                active.RewardItems,
                replacement.RewardItems,
                "located case opening reward evidence");
            return;
        }

        RequireExactItemEvidence(
            active.RewardItems,
            replacement.RewardItems,
            "case opening reward evidence");
    }

    private static void ValidateRelayReplacement(
        RelaySettlementRecord active,
        RelaySettlementRecord replacement)
    {
        LegacyRewardMail.ValidateReplacement(active.MailDelivery, replacement.MailDelivery,
            active.Status == RelayRecordStatus.Committed);
        if (active.OriginCaseId != replacement.OriginCaseId ||
            !active.InputItemIds.SequenceEqual(replacement.InputItemIds) ||
            !string.Equals(active.InputRewardId, replacement.InputRewardId, StringComparison.Ordinal) ||
            active.InputRarity != replacement.InputRarity ||
            active.RarityLadderVersion != replacement.RarityLadderVersion ||
            active.Stage != replacement.Stage ||
            active.Action != replacement.Action ||
            active.KeyId != replacement.KeyId ||
            active.Outcome != replacement.Outcome ||
            !string.Equals(active.OutputRewardId, replacement.OutputRewardId, StringComparison.Ordinal) ||
            active.MeterBefore != replacement.MeterBefore ||
            active.MeterAfter != replacement.MeterAfter ||
            active.GuaranteedUpgrade != replacement.GuaranteedUpgrade ||
            active.PreparedAtUtc != replacement.PreparedAtUtc)
        {
            throw new InvalidOperationException("A Relay replacement cannot rewrite prepared evidence.");
        }
        RequireExactItemEvidence(
            active.InputItems,
            replacement.InputItems,
            "Relay input evidence");

        if (active.Status == RelayRecordStatus.Committed)
        {
            if (replacement.Status != RelayRecordStatus.Committed ||
                replacement.CommittedAtUtc != active.CommittedAtUtc ||
                replacement.ProfileCommitStarted != active.ProfileCommitStarted)
            {
                throw new InvalidOperationException("A committed Relay replacement must be an exact no-op.");
            }
            RequireExactItemEvidence(active.OutputItems, replacement.OutputItems, "Relay output evidence");
            return;
        }

        if (replacement.Status == RelayRecordStatus.Committed)
        {
            if (replacement.ProfileCommitStarted != active.ProfileCommitStarted)
            {
                throw new InvalidOperationException(
                    "A Relay profile-commit marker must be persisted before committing the record.");
            }
            RequireExactItemEvidence(active.OutputItems, replacement.OutputItems, "Relay output evidence");
            return;
        }

        if (active.ProfileCommitStarted && !replacement.ProfileCommitStarted)
        {
            throw new InvalidOperationException("A Relay profile-commit marker cannot move backward.");
        }
        if (active.ProfileCommitStarted == replacement.ProfileCommitStarted)
        {
            RequireExactItemEvidence(active.OutputItems, replacement.OutputItems, "Relay output evidence");
            return;
        }

        RequireExactItemEvidenceAllowingRootPlacement(
            active.OutputItems,
            replacement.OutputItems,
            "located Relay output evidence");
    }

    private static void RequireExactItemEvidence(
        IReadOnlyCollection<Item> active,
        IReadOnlyCollection<Item> replacement,
        string operation)
    {
        try
        {
            SptOpeningInventory.EnsureExactItemState(active, replacement, operation);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException($"A journal replacement changed {operation}.", exception);
        }
    }

    private static void RequireExactItemEvidenceAllowingRootPlacement(
        IReadOnlyCollection<Item> active,
        IReadOnlyCollection<Item> replacement,
        string operation)
    {
        if (active.Count == 0 && replacement.Count == 0)
        {
            return;
        }

        var activeRoot = SettlementItemTrees.FindRootId(active);
        var replacementRoot = SettlementItemTrees.FindRootId(replacement);
        if (activeRoot is null || activeRoot != replacementRoot)
        {
            throw new InvalidOperationException($"A journal replacement changed {operation}.");
        }

        var normalizedActive = active.Select(item => NormalizeRootPlacement(item, activeRoot.Value)).ToArray();
        var normalizedReplacement = replacement
            .Select(item => NormalizeRootPlacement(item, activeRoot.Value))
            .ToArray();
        RequireExactItemEvidence(normalizedActive, normalizedReplacement, operation);
    }

    private static Item NormalizeRootPlacement(Item item, MongoId rootId)
    {
        var clone = CaseOpeningRecord.CloneItem(item);
        return item.Id == rootId
            ? clone with { ParentId = null, SlotId = null, Location = null }
            : clone;
    }

    public void SetActiveManifest(ManifestRecord manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (ActiveManifest is not null)
        {
            throw new InvalidOperationException("The journal already has an active manifest.");
        }
        if (manifest.FlowState.Phase != Shared.Manifest.ManifestPhase.TicketPrepared ||
            manifest.Ticket.ProfileCommitStarted)
        {
            throw new InvalidOperationException("Only a fresh TicketPrepared manifest can become active.");
        }
        if (_manifestReceipts.Any(receipt =>
                string.Equals(receipt.ManifestId, manifest.ManifestId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("A terminal receipt already exists for this manifest.");
        }
        if (_manifestClaimGrants.Any(grant =>
                string.Equals(grant.ManifestId, manifest.ManifestId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("A durable Claim grant already exists for this manifest.");
        }
        if (manifest.BrokerFavor != RecoveryMeter)
        {
            throw new InvalidOperationException("Manifest Broker Favor must match the journal recovery meter.");
        }
        if (manifest.RequiresExclusiveProfileMutation && HasPreparedTransaction())
        {
            throw new InvalidOperationException("The profile already has a prepared settlement transaction.");
        }

        ActiveManifest = manifest;
    }

    public void ReplaceActiveManifest(ManifestRecord manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var activeManifest = ActiveManifest
            ?? throw new InvalidOperationException("Cannot replace an active manifest that does not exist.");
        if (!string.Equals(activeManifest.ManifestId, manifest.ManifestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An active manifest can only be replaced by the same manifest ID.");
        }
        if (manifest.FlowState.Phase == Shared.Manifest.ManifestPhase.Granted)
        {
            throw new InvalidOperationException(
                "A Granted manifest must use the atomic exact-Claim finish operation.");
        }
        if (manifest.RequiresExclusiveProfileMutation && HasPreparedTransaction(excludeActiveManifest: true))
        {
            throw new InvalidOperationException("The profile already has a prepared settlement transaction.");
        }

        ValidateManifestReplacement(activeManifest, manifest);
        ActiveManifest = manifest;
        RecoveryMeter = manifest.BrokerFavor;
    }

    public void FinishActiveManifest()
    {
        var activeManifest = ActiveManifest
            ?? throw new InvalidOperationException("Cannot finish an active manifest that does not exist.");
        var receipt = ValidateTerminalManifestForFinish(activeManifest);
        if (receipt.TerminalPhase == Shared.Manifest.ManifestPhase.Granted)
        {
            throw new InvalidOperationException(
                "A Granted manifest must be finished with its durable exact Claim grant.");
        }

        _manifestReceipts.Insert(0, receipt);
        ActiveManifest = null;
        PruneManifestHistory();
    }

    public void FinishGrantedActiveManifest(
        ManifestRecord terminalManifest,
        ManifestClaimGrantRecord grant)
    {
        ArgumentNullException.ThrowIfNull(terminalManifest);
        ArgumentNullException.ThrowIfNull(grant);
        var activeManifest = ActiveManifest
            ?? throw new InvalidOperationException("Cannot finish an active manifest that does not exist.");
        if (activeManifest.FlowState.Phase is not (
                Shared.Manifest.ManifestPhase.ClaimPrepared or
                Shared.Manifest.ManifestPhase.RewardOwed))
        {
            throw new InvalidOperationException(
                "An exact Claim grant can finish only an active prepared or owed Claim.");
        }

        ValidateManifestReplacement(activeManifest, terminalManifest);
        var receipt = ValidateTerminalManifestForFinish(terminalManifest);
        if (receipt.TerminalPhase != Shared.Manifest.ManifestPhase.Granted)
        {
            throw new InvalidOperationException(
                "Only a Granted active manifest can be finished with a durable Claim grant.");
        }
        if (!IsMatchingManifestClaimGrant(receipt, grant) ||
            !SameEntitlement(activeManifest.Entitlement, grant.Entitlement))
        {
            throw new InvalidOperationException(
                "The durable Claim grant does not match the terminal manifest receipt.");
        }
        ValidateExactClaimGrant(activeManifest, terminalManifest, grant);
        if (_manifestClaimGrants.Any(existing =>
                string.Equals(existing.ManifestId, grant.ManifestId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("A durable Claim grant already exists for this manifest.");
        }

        _manifestReceipts.Insert(0, receipt);
        _manifestClaimGrants.Insert(0, grant.Clone());
        RecoveryMeter = terminalManifest.BrokerFavor;
        ActiveManifest = null;
        PruneManifestHistory();
    }

    private static void ValidateExactClaimGrant(
        ManifestRecord activeManifest,
        ManifestRecord terminalManifest,
        ManifestClaimGrantRecord grant)
    {
        var prepared = activeManifest.ClaimPrepared
            ?? throw new InvalidOperationException("The active Claim has no exact prepared payload.");
        var committed = grant.ClaimPayload;
        if (!prepared.ProfileCommitStarted ||
            !committed.ProfileCommitStarted ||
            prepared.PreparedAtUtc != committed.PreparedAtUtc ||
            prepared.CommitGeneration != committed.CommitGeneration ||
            prepared.Delivery != committed.Delivery ||
            !string.Equals(
                prepared.CommitPredecessorHash,
                committed.CommitPredecessorHash,
                StringComparison.Ordinal) ||
            !prepared.RootIds.SequenceEqual(committed.RootIds))
        {
            throw new InvalidOperationException(
                "The durable Claim grant does not match its exact prepared Claim evidence.");
        }

        RequireExactItemEvidence(
            prepared.Items,
            committed.Items,
            "durable exact Claim grant evidence");
        ManifestRecordValidation.ValidateClaimItemIdsAgainstTicket(committed, terminalManifest.Ticket);
    }

    private ManifestTerminalReceipt ValidateTerminalManifestForFinish(ManifestRecord activeManifest)
    {
        if (!activeManifest.IsTerminal)
        {
            throw new InvalidOperationException("Only a terminal active manifest can be finished.");
        }

        var receipt = activeManifest.TerminalReceipt
            ?? throw new InvalidOperationException("A terminal active manifest requires its matching receipt.");
        if (!string.Equals(receipt.ManifestId, activeManifest.ManifestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The terminal receipt does not match the active manifest ID.");
        }
        if (activeManifest.BrokerFavor != RecoveryMeter || receipt.BrokerFavorAfter != RecoveryMeter)
        {
            throw new InvalidOperationException("Terminal manifest Broker Favor must match the journal recovery meter.");
        }
        if (_manifestReceipts.Any(existing =>
                string.Equals(existing.ManifestId, receipt.ManifestId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("A terminal receipt already exists for this manifest.");
        }

        return receipt;
    }

    private static void ValidateManifestClaimGrantPairs(
        IReadOnlyCollection<ManifestTerminalReceipt> receipts,
        IReadOnlyCollection<ManifestClaimGrantRecord> grants)
    {
        var grantsByManifestId = grants.ToDictionary(
            grant => grant.ManifestId,
            StringComparer.Ordinal);
        foreach (var receipt in receipts)
        {
            var hasGrant = grantsByManifestId.TryGetValue(receipt.ManifestId, out var grant);
            if (receipt.TerminalPhase == Shared.Manifest.ManifestPhase.Granted)
            {
                if (!hasGrant || !IsMatchingManifestClaimGrant(receipt, grant!))
                {
                    throw new ArgumentException(
                        "Every Granted manifest receipt requires exactly one matching durable Claim grant.");
                }
            }
            else if (hasGrant)
            {
                throw new ArgumentException(
                    "A durable Claim grant can pair only with a Granted manifest receipt.");
            }
        }

        if (grantsByManifestId.Keys.Except(
                receipts.Select(receipt => receipt.ManifestId),
                StringComparer.Ordinal).Any())
        {
            throw new ArgumentException(
                "Every durable Claim grant requires exactly one matching Granted manifest receipt.");
        }
    }

    private static bool IsMatchingManifestClaimGrant(
        ManifestTerminalReceipt receipt,
        ManifestClaimGrantRecord grant) =>
        receipt.TerminalPhase == Shared.Manifest.ManifestPhase.Granted &&
        string.Equals(receipt.ManifestId, grant.ManifestId, StringComparison.Ordinal) &&
        receipt.Entitlement is not null &&
        ManifestRecordValidation.SameReceiptLot(receipt.Entitlement, grant.Entitlement) &&
        receipt.BrokerFavorBefore == receipt.BrokerFavorAfter &&
        receipt.CompletedAtUtc == grant.CommittedAtUtc;

    private bool HasPreparedTransaction(
        int excludedOpeningIndex = -1,
        int excludedRelayIndex = -1,
        bool excludeActiveManifest = false) =>
        _records.Where((record, index) =>
                index != excludedOpeningIndex && record.Status == OpeningRecordStatus.Prepared)
            .Any() ||
        _relayRecords.Where((record, index) =>
                index != excludedRelayIndex && record.Status == RelayRecordStatus.Prepared)
            .Any() ||
        !excludeActiveManifest && ActiveManifest?.RequiresExclusiveProfileMutation == true;

    private static void ValidateManifestReplacement(
        ManifestRecord activeManifest,
        ManifestRecord replacement)
    {
        if (!string.Equals(
                activeManifest.CatalogSnapshotId,
                replacement.CatalogSnapshotId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An active manifest cannot change its catalog snapshot.");
        }
        if (activeManifest.Commitment.Version != replacement.Commitment.Version ||
            !string.Equals(
                activeManifest.Commitment.CommitmentSha256Hex,
                replacement.Commitment.CommitmentSha256Hex,
                StringComparison.Ordinal) ||
            !string.Equals(activeManifest.Commitment.NonceHex, replacement.Commitment.NonceHex, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An active manifest cannot rewrite its offer commitment evidence.");
        }
        ValidateTicketReplacement(activeManifest.Ticket, replacement.Ticket);
        for (var index = 0; index < activeManifest.Offers.Count; index++)
        {
            if (!SameOffer(activeManifest.Offers[index], replacement.Offers[index]))
            {
                throw new InvalidOperationException("An active manifest cannot rewrite its frozen offers.");
            }
        }
        if (replacement.Decisions.Count < activeManifest.Decisions.Count ||
            replacement.Decisions.Count > activeManifest.Decisions.Count + 1)
        {
            throw new InvalidOperationException("Manifest decision history can append at most one decision per replacement.");
        }
        for (var index = 0; index < activeManifest.Decisions.Count; index++)
        {
            if (!SameDecision(activeManifest.Decisions[index], replacement.Decisions[index]))
            {
                throw new InvalidOperationException("An active manifest cannot rewrite prior offer decisions.");
            }
        }

        if (replacement.RelayHistory.Count < activeManifest.RelayHistory.Count ||
            replacement.RelayHistory.Count > activeManifest.RelayHistory.Count + 1)
        {
            throw new InvalidOperationException(
                "Manifest Relay history can append at most one committed receipt per replacement.");
        }
        for (var index = 0; index < activeManifest.RelayHistory.Count; index++)
        {
            if (!ManifestRecordValidation.SameRelayReceipt(
                    activeManifest.RelayHistory[index],
                    replacement.RelayHistory[index]))
            {
                throw new InvalidOperationException(
                    "An active manifest cannot rewrite prior Relay history.");
            }
        }

        ManifestRelayReceipt? addedReceipt = null;
        if (replacement.RelayHistory.Count == activeManifest.RelayHistory.Count)
        {
            if (replacement.BrokerFavor != activeManifest.BrokerFavor)
            {
                throw new InvalidOperationException(
                    "A Broker Favor change requires one new committed Manifest Relay receipt.");
            }
        }
        else
        {
            addedReceipt = replacement.RelayHistory[^1];
            if (addedReceipt.BrokerFavorBefore != activeManifest.BrokerFavor ||
                addedReceipt.BrokerFavorAfter != replacement.BrokerFavor)
            {
                throw new InvalidOperationException(
                    "The committed Manifest Relay receipt does not prove the Broker Favor change.");
            }
        }

        var addedDecision = replacement.Decisions.Count == activeManifest.Decisions.Count + 1
            ? replacement.Decisions[^1]
            : null;
        ValidateManifestFlowTransition(activeManifest, replacement, addedDecision, addedReceipt);
        ValidateClaimPreparedReplacement(activeManifest, replacement);
        ValidateRelayPreparedReplacement(activeManifest, replacement, addedReceipt);
        ValidateManifestSemanticPayloadReplacement(activeManifest, replacement, addedReceipt);
        ValidateTerminalReceiptReplacement(activeManifest.TerminalReceipt, replacement.TerminalReceipt);
    }

    private static void ValidateManifestFlowTransition(
        ManifestRecord active,
        ManifestRecord replacement,
        ManifestDecisionRecord? addedDecision,
        ManifestRelayReceipt? addedReceipt)
    {
        var activePhase = active.FlowState.Phase;
        var replacementPhase = replacement.FlowState.Phase;
        if (activePhase == replacementPhase)
        {
            if (addedDecision is not null || addedReceipt is not null ||
                !SameFlowState(active.FlowState, replacement.FlowState))
            {
                throw new InvalidOperationException("A same-phase manifest replacement may update recovery markers only.");
            }
            return;
        }

        var activeProfileCommitStarted = activePhase switch
        {
            Shared.Manifest.ManifestPhase.TicketPrepared => active.Ticket.ProfileCommitStarted,
            Shared.Manifest.ManifestPhase.ClaimPrepared => active.ClaimPrepared?.ProfileCommitStarted == true,
            Shared.Manifest.ManifestPhase.RelayPrepared => active.RelayPrepared?.ProfileCommitStarted == true,
            _ => true
        };
        if (!activeProfileCommitStarted)
        {
            throw new InvalidOperationException(
                "A prepared manifest action must persist its profile-commit marker before advancing.");
        }

        Shared.Manifest.ManifestFlowState expected;
        var expectsDecision = false;
        var expectsReceipt = false;
        switch (activePhase)
        {
            case Shared.Manifest.ManifestPhase.TicketPrepared
                when active.Ticket.CaseTemplateId == Shared.Catalog.CaseContracts.CashCache &&
                     replacementPhase == Shared.Manifest.ManifestPhase.Entitlement:
                expected = Shared.Manifest.ManifestFlowState.ActivateTicket(active.FlowState, singlePayout: true);
                break;
            case Shared.Manifest.ManifestPhase.TicketPrepared
                when replacementPhase == Shared.Manifest.ManifestPhase.Offer1:
                expected = Shared.Manifest.ManifestFlowState.ActivateTicket(active.FlowState);
                break;
            case Shared.Manifest.ManifestPhase.Offer1
                when replacementPhase is Shared.Manifest.ManifestPhase.Offer2 or
                    Shared.Manifest.ManifestPhase.Entitlement:
            case Shared.Manifest.ManifestPhase.Offer2
                when replacementPhase == Shared.Manifest.ManifestPhase.Entitlement:
                if (addedDecision is null)
                {
                    throw new InvalidOperationException("Advancing an offer requires one appended decision.");
                }
                expectsDecision = true;
                expected = active.Ticket.OpeningQuality?.IsPremium == true &&
                    addedDecision.Decision == Shared.Manifest.ManifestOfferDecision.Choose
                    ? Shared.Manifest.ManifestStateMachine.ChoosePremiumOffer(active.FlowState, addedDecision.Ordinal)
                    : Shared.Manifest.ManifestStateMachine.DecideOffer(active.FlowState, addedDecision.Decision);
                break;
            case Shared.Manifest.ManifestPhase.Offer1 or Shared.Manifest.ManifestPhase.Offer2 or
                Shared.Manifest.ManifestPhase.Entitlement
                when replacementPhase == Shared.Manifest.ManifestPhase.Forfeited:
                expected = Shared.Manifest.ManifestStateMachine.ForfeitMissingContent(
                    active.FlowState,
                    missingContentBlocked: true);
                break;
            case Shared.Manifest.ManifestPhase.Entitlement
                when replacementPhase == Shared.Manifest.ManifestPhase.ClaimPrepared:
                expected = Shared.Manifest.ManifestStateMachine.PrepareClaim(active.FlowState);
                break;
            case Shared.Manifest.ManifestPhase.Entitlement
                when replacementPhase == Shared.Manifest.ManifestPhase.RelayPrepared:
                expected = Shared.Manifest.ManifestStateMachine.PrepareRelay(active.FlowState, relayEligible: true);
                break;
            case Shared.Manifest.ManifestPhase.ClaimPrepared
                when replacementPhase == Shared.Manifest.ManifestPhase.RewardOwed:
                expected = Shared.Manifest.ManifestStateMachine.MarkRewardOwed(active.FlowState);
                break;
            case Shared.Manifest.ManifestPhase.ClaimPrepared or Shared.Manifest.ManifestPhase.RewardOwed
                when replacementPhase == Shared.Manifest.ManifestPhase.Granted:
                expected = Shared.Manifest.ManifestStateMachine.CompleteClaim(active.FlowState);
                break;
            case Shared.Manifest.ManifestPhase.RelayPrepared
                when replacementPhase is Shared.Manifest.ManifestPhase.Entitlement or
                    Shared.Manifest.ManifestPhase.Confiscated:
                if (addedReceipt is null)
                {
                    throw new InvalidOperationException("Completing Relay requires one appended committed receipt.");
                }
                expectsReceipt = true;
                expected = Shared.Manifest.ManifestStateMachine.CompleteRelay(
                    active.FlowState,
                    addedReceipt.Outcome,
                    addedReceipt.Output?.Rarity);
                break;
            default:
                throw new InvalidOperationException(
                    $"Manifest phase cannot move from '{activePhase}' to '{replacementPhase}'.");
        }

        if (expectsDecision != (addedDecision is not null) ||
            expectsReceipt != (addedReceipt is not null) ||
            !SameFlowState(expected, replacement.FlowState))
        {
            throw new InvalidOperationException("Manifest replacement does not match a legal forward transition.");
        }
    }

    private static void ValidateClaimPreparedReplacement(ManifestRecord active, ManifestRecord replacement)
    {
        var activeClaim = active.ClaimPrepared;
        var replacementClaim = replacement.ClaimPrepared;
        if (activeClaim is null || replacementClaim is null)
        {
            if (activeClaim is not null && replacement.FlowState.Phase != Shared.Manifest.ManifestPhase.Granted)
            {
                throw new InvalidOperationException("Prepared Claim evidence cannot be removed before grant completion.");
            }
            return;
        }

        if (!activeClaim.RootIds.SequenceEqual(replacementClaim.RootIds) ||
            activeClaim.PreparedAtUtc != replacementClaim.PreparedAtUtc ||
            activeClaim.CommitGeneration != replacementClaim.CommitGeneration ||
            !string.Equals(
                activeClaim.CommitPredecessorHash,
                replacementClaim.CommitPredecessorHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Prepared Claim evidence is immutable.");
        }
        if (activeClaim.ProfileCommitStarted && !replacementClaim.ProfileCommitStarted)
        {
            throw new InvalidOperationException("A prepared Claim profile-commit marker cannot move backward.");
        }

        if (activeClaim.Delivery != replacementClaim.Delivery)
        {
            if (activeClaim.Delivery != ClaimDeliveryKind.LegacyInventory ||
                replacementClaim.Delivery != ClaimDeliveryKind.Messenger ||
                activeClaim.ProfileCommitStarted != replacementClaim.ProfileCommitStarted)
                throw new InvalidOperationException("Claim delivery cannot move backward or rewrite commit evidence.");
            RequireExactItemEvidence(activeClaim.Items, replacementClaim.Items, "migrated Claim item evidence");
            return;
        }

        if (!activeClaim.ProfileCommitStarted && !replacementClaim.ProfileCommitStarted)
        {
            RequireExactItemEvidence(activeClaim.Items, replacementClaim.Items, "prepared Claim item evidence");
            return;
        }

        RequireExactClaimItemEvidenceAllowingRootPlacement(
            activeClaim.Items,
            replacementClaim.Items,
            activeClaim.RootIds,
            "located Claim item evidence");
    }

    private static void RequireExactClaimItemEvidenceAllowingRootPlacement(
        IReadOnlyCollection<Item> active,
        IReadOnlyCollection<Item> replacement,
        IReadOnlyCollection<MongoId> rootIds,
        string operation)
    {
        var roots = rootIds.ToHashSet();
        if (roots.Count != rootIds.Count ||
            active.Count(item => roots.Contains(item.Id)) != roots.Count ||
            replacement.Count(item => roots.Contains(item.Id)) != roots.Count)
        {
            throw new InvalidOperationException($"A journal replacement changed {operation}.");
        }

        var normalizedActive = active.Select(item =>
            roots.Contains(item.Id) ? NormalizeRootPlacement(item, item.Id) : CaseOpeningRecord.CloneItem(item));
        var normalizedReplacement = replacement.Select(item =>
            roots.Contains(item.Id) ? NormalizeRootPlacement(item, item.Id) : CaseOpeningRecord.CloneItem(item));
        RequireExactItemEvidence(normalizedActive.ToArray(), normalizedReplacement.ToArray(), operation);
    }

    private static void ValidateRelayPreparedReplacement(
        ManifestRecord active,
        ManifestRecord replacement,
        ManifestRelayReceipt? addedReceipt)
    {
        var activeRelay = active.RelayPrepared;
        var replacementRelay = replacement.RelayPrepared;
        if (activeRelay is null || replacementRelay is null)
        {
            if (activeRelay is not null)
            {
                if (addedReceipt is null)
                {
                    throw new InvalidOperationException("Prepared Relay evidence cannot be removed without a committed receipt.");
                }
                ValidatePreparedRelayCompletion(active, replacement, activeRelay, addedReceipt);
            }
            return;
        }

        if (activeRelay.KeyId != replacementRelay.KeyId ||
            activeRelay.Outcome != replacementRelay.Outcome ||
            !SameEntitlement(activeRelay.Output, replacementRelay.Output) ||
            activeRelay.Odds != replacementRelay.Odds ||
            !SameRng(activeRelay.OutcomeRng, replacementRelay.OutcomeRng) ||
            !SameRng(activeRelay.TargetRng, replacementRelay.TargetRng) ||
            activeRelay.BrokerFavorBefore != replacementRelay.BrokerFavorBefore ||
            activeRelay.BrokerFavorAfter != replacementRelay.BrokerFavorAfter ||
            activeRelay.PreparedAtUtc != replacementRelay.PreparedAtUtc ||
            !SameCandidates(activeRelay.NextRelayCandidates, replacementRelay.NextRelayCandidates) ||
            activeRelay.CommitGeneration != replacementRelay.CommitGeneration ||
            !string.Equals(
                activeRelay.CommitPredecessorHash,
                replacementRelay.CommitPredecessorHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Prepared Relay evidence is immutable.");
        }
        if (activeRelay.ProfileCommitStarted && !replacementRelay.ProfileCommitStarted)
        {
            throw new InvalidOperationException("A prepared Relay profile-commit marker cannot move backward.");
        }
    }

    private static void ValidatePreparedRelayCompletion(
        ManifestRecord active,
        ManifestRecord replacement,
        ManifestRelayPreparedPayload prepared,
        ManifestRelayReceipt receipt)
    {
        var eligibleTargets = active.RelayCandidates
            .Where(candidate => candidate.TargetResult == receipt.Outcome)
            .ToArray();
        if (receipt.RelayStage != active.FlowState.RelayStage ||
            active.Entitlement is null ||
            !ManifestRecordValidation.SameReceiptLot(receipt.Input, active.Entitlement) ||
            receipt.Outcome != prepared.Outcome ||
            !SameEntitlement(receipt.Output, prepared.Output) ||
            receipt.Odds != prepared.Odds ||
            !SameRng(receipt.OutcomeRng, prepared.OutcomeRng) ||
            !SameRng(receipt.TargetRng, prepared.TargetRng) ||
            receipt.BrokerFavorBefore != prepared.BrokerFavorBefore ||
            receipt.BrokerFavorAfter != prepared.BrokerFavorAfter ||
            receipt.CompletedAtUtc < prepared.PreparedAtUtc ||
            receipt.EligibleTargets.Count != eligibleTargets.Length ||
            receipt.EligibleTargets.Where((target, index) =>
                !SameCandidateReceipt(eligibleTargets[index], target)).Any())
        {
            throw new InvalidOperationException("Committed Relay receipt does not match its prepared evidence.");
        }

        var completedState = Shared.Manifest.ManifestStateMachine.CompleteRelay(
            active.FlowState,
            prepared.Outcome,
            prepared.Output?.Rarity);
        var expectedEntitlement = prepared.Outcome == Shared.Manifest.ManifestRelayResult.Confiscated
            ? null
            : prepared.Output;
        IReadOnlyList<ManifestRelayCandidateSnapshot> expectedCandidates =
            completedState.Phase == Shared.Manifest.ManifestPhase.Entitlement &&
            !completedState.RelayTerminal
                ? prepared.NextRelayCandidates
                : [];
        if (!SameEntitlement(expectedEntitlement, replacement.Entitlement) ||
            !SameCandidates(expectedCandidates, replacement.RelayCandidates))
        {
            throw new InvalidOperationException(
                "Completed Relay state does not match its prepared semantic output.");
        }
    }

    private static void ValidateManifestSemanticPayloadReplacement(
        ManifestRecord active,
        ManifestRecord replacement,
        ManifestRelayReceipt? addedReceipt)
    {
        var offerLocked = active.FlowState.Phase is (Shared.Manifest.ManifestPhase.Offer1 or
                Shared.Manifest.ManifestPhase.Offer2) &&
            replacement.FlowState.Phase == Shared.Manifest.ManifestPhase.Entitlement;
        var terminalConsumesEntitlement = replacement.FlowState.Phase is
            Shared.Manifest.ManifestPhase.Granted or Shared.Manifest.ManifestPhase.Forfeited;
        var cashActivated = active.Ticket.CaseTemplateId == Shared.Catalog.CaseContracts.CashCache &&
            active.FlowState.Phase == Shared.Manifest.ManifestPhase.TicketPrepared &&
            replacement.FlowState.Phase == Shared.Manifest.ManifestPhase.Entitlement;
        if (!offerLocked && !cashActivated && addedReceipt is null && !terminalConsumesEntitlement &&
            (!SameEntitlement(active.Entitlement, replacement.Entitlement) ||
             !SameCandidates(active.RelayCandidates, replacement.RelayCandidates)))
        {
            throw new InvalidOperationException(
                "Manifest entitlement and frozen Relay candidates require a committed transition.");
        }
    }

    private static void ValidateTerminalReceiptReplacement(
        ManifestTerminalReceipt? active,
        ManifestTerminalReceipt? replacement)
    {
        if (active is null)
        {
            return;
        }
        if (replacement is null || !SameTerminalReceipt(active, replacement))
        {
            throw new InvalidOperationException("Terminal manifest receipt evidence is immutable.");
        }
    }

    private static bool SameFlowState(
        Shared.Manifest.ManifestFlowState left,
        Shared.Manifest.ManifestFlowState right) =>
        left.Phase == right.Phase &&
        left.CurrentOrdinal == right.CurrentOrdinal &&
        left.LockedOrdinal == right.LockedOrdinal &&
        left.RelayStage == right.RelayStage &&
        left.RelayTerminal == right.RelayTerminal;

    private static bool SameEntitlement(
        ManifestEntitlementSnapshot? left,
        ManifestEntitlementSnapshot? right) =>
        (left, right) switch
        {
            (null, null) => true,
            (not null, not null) =>
                left.Rarity == right.Rarity &&
                ManifestRecordValidation.SameIdentity(left.Identity, right.Identity) &&
                left.Fingerprint.Equals(right.Fingerprint) &&
                SameForest(left.Forest, right.Forest),
            _ => false
        };

    private static bool SameEntitlement(
        ManifestLotReceiptSnapshot? receipt,
        ManifestEntitlementSnapshot? entitlement) =>
        (receipt, entitlement) switch
        {
            (null, null) => true,
            (not null, not null) => ManifestRecordValidation.SameReceiptLot(receipt, entitlement),
            _ => false
        };

    private static bool SameCandidates(
        IReadOnlyList<ManifestRelayCandidateSnapshot> left,
        IReadOnlyList<ManifestRelayCandidateSnapshot> right) =>
        left.Count == right.Count &&
        !left.Where((candidate, index) => !SameCandidate(candidate, right[index])).Any();

    private static bool SameCandidate(
        ManifestRelayCandidateSnapshot left,
        ManifestRelayCandidateSnapshot right) =>
        left.TargetResult == right.TargetResult &&
        left.Rarity == right.Rarity &&
        ManifestRecordValidation.SameIdentity(left.Identity, right.Identity) &&
        left.Fingerprint.Equals(right.Fingerprint) &&
        SameForest(left.Forest, right.Forest);

    private static bool SameCandidateReceipt(
        ManifestRelayCandidateSnapshot candidate,
        ManifestLotReceiptSnapshot receipt) =>
        candidate.Rarity == receipt.Rarity &&
        ManifestRecordValidation.SameIdentity(candidate.Identity, receipt.Identity) &&
        candidate.Fingerprint.Equals(receipt.Fingerprint);

    private static bool SameRng(CanonicalRngEvidence? left, CanonicalRngEvidence? right) =>
        (left, right) switch
        {
            (null, null) => true,
            (not null, not null) =>
                left.Purpose == right.Purpose &&
                left.DrawOrdinal == right.DrawOrdinal &&
                left.UnitNumerator == right.UnitNumerator,
            _ => false
        };

    private static bool SameTerminalReceipt(
        ManifestTerminalReceipt left,
        ManifestTerminalReceipt right) =>
        left.CaseTemplateId == right.CaseTemplateId &&
        left.OpeningQuality == right.OpeningQuality &&
        string.Equals(left.ManifestId, right.ManifestId, StringComparison.Ordinal) &&
        left.TerminalPhase == right.TerminalPhase &&
        left.Offers.Count == right.Offers.Count &&
        !left.Offers.Where((offer, index) =>
            !ManifestRecordValidation.SameReceiptLot(offer, right.Offers[index])).Any() &&
        SameReceipt(left.Entitlement, right.Entitlement) &&
        left.Decisions.Count == right.Decisions.Count &&
        !left.Decisions.Where((decision, index) =>
            !SameDecision(decision, right.Decisions[index])).Any() &&
        left.RelayHistory.Count == right.RelayHistory.Count &&
        !left.RelayHistory.Where((receipt, index) =>
            !ManifestRecordValidation.SameRelayReceipt(receipt, right.RelayHistory[index])).Any() &&
        left.BrokerFavorBefore == right.BrokerFavorBefore &&
        left.BrokerFavorAfter == right.BrokerFavorAfter &&
        left.CompletedAtUtc == right.CompletedAtUtc;

    private static bool SameReceipt(
        ManifestLotReceiptSnapshot? left,
        ManifestLotReceiptSnapshot? right) =>
        (left, right) switch
        {
            (null, null) => true,
            (not null, not null) => ManifestRecordValidation.SameReceiptLot(left, right),
            _ => false
        };

    private static void ValidateTicketReplacement(
        ManifestTicketPayload activeTicket,
        ManifestTicketPayload replacementTicket)
    {
        if (activeTicket.OpeningQuality != replacementTicket.OpeningQuality ||
            activeTicket.CaseTemplateId != replacementTicket.CaseTemplateId ||
            activeTicket.CaseId != replacementTicket.CaseId ||
            activeTicket.KeyId != replacementTicket.KeyId ||
            activeTicket.PreparedAtUtc != replacementTicket.PreparedAtUtc ||
            activeTicket.CommitGeneration != replacementTicket.CommitGeneration ||
            !string.Equals(
                activeTicket.CommitPredecessorHash,
                replacementTicket.CommitPredecessorHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An active manifest cannot rewrite its prepared ticket identity.");
        }
        if (activeTicket.ProfileCommitStarted && !replacementTicket.ProfileCommitStarted ||
            activeTicket.Committed && !replacementTicket.Committed)
        {
            throw new InvalidOperationException("An active manifest ticket cannot move backward.");
        }
        if (activeTicket.Committed &&
            activeTicket.CommittedAtUtc != replacementTicket.CommittedAtUtc)
        {
            throw new InvalidOperationException("A committed manifest ticket timestamp is immutable.");
        }
    }

    private static bool SameOffer(ManifestOfferSnapshot left, ManifestOfferSnapshot right) =>
        left.Ordinal == right.Ordinal &&
        left.Rarity == right.Rarity &&
        ManifestRecordValidation.SameIdentity(left.Identity, right.Identity) &&
        left.Fingerprint.Equals(right.Fingerprint) &&
        SameForest(left.Forest, right.Forest) &&
        left.RngEvidence.Purpose == right.RngEvidence.Purpose &&
        left.RngEvidence.DrawOrdinal == right.RngEvidence.DrawOrdinal &&
        left.RngEvidence.UnitNumerator == right.RngEvidence.UnitNumerator;

    private static bool SameForest(
        Shared.Catalog.RewardForest left,
        Shared.Catalog.RewardForest right) =>
        left.Nodes.Count == right.Nodes.Count &&
        !left.Nodes.Where((leftNode, index) =>
            !SameForestNode(leftNode, right.Nodes[index])).Any();

    private static bool SameForestNode(
        Shared.Catalog.RewardForestNode left,
        Shared.Catalog.RewardForestNode right) =>
        string.Equals(left.TreeRootPath, right.TreeRootPath, StringComparison.Ordinal) &&
        string.Equals(left.LogicalPath, right.LogicalPath, StringComparison.Ordinal) &&
        string.Equals(left.TemplateId, right.TemplateId, StringComparison.Ordinal) &&
        string.Equals(left.ParentLogicalPath, right.ParentLogicalPath, StringComparison.Ordinal) &&
        string.Equals(left.SlotId, right.SlotId, StringComparison.Ordinal) &&
        Equals(left.InternalLocation, right.InternalLocation) &&
        left.StackCount == right.StackCount &&
        Equals(left.StableState, right.StableState);

    private static bool SameDecision(ManifestDecisionRecord left, ManifestDecisionRecord right) =>
        left.Ordinal == right.Ordinal &&
        left.Decision == right.Decision &&
        left.DecidedAtUtc == right.DecidedAtUtc;

    public void ApplyCommittedMeter(RelaySettlementRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (ActiveManifest is not null)
        {
            throw new InvalidOperationException(
                "Legacy Relay meter updates cannot run while a manifest is active.");
        }
        if (record.Status != RelayRecordStatus.Committed)
        {
            throw new InvalidOperationException("Only a committed Relay record can advance the recovery meter.");
        }
        if (RecoveryMeter != record.MeterBefore)
        {
            throw new InvalidOperationException("Relay recovery meter contradicts the committed record.");
        }

        RecoveryMeter = record.MeterAfter;
    }

    public void PruneCommitted()
    {
        var openingByRoot = new Dictionary<MongoId, CaseOpeningRecord>();
        foreach (var opening in _records.Where(record => record.RewardRootId is not null))
        {
            if (!openingByRoot.TryAdd(opening.RewardRootId!.Value, opening))
            {
                throw new InvalidOperationException("Journal contains duplicate opening reward roots.");
            }
        }

        var relayByStake = _relayRecords.ToDictionary(record => record.StakeRootId);
        var relayByOutput = new Dictionary<MongoId, RelaySettlementRecord>();
        foreach (var relay in _relayRecords.Where(record => record.OutputRootId is not null))
        {
            if (!relayByOutput.TryAdd(relay.OutputRootId!.Value, relay))
            {
                throw new InvalidOperationException("Relay journal contains duplicate output roots.");
            }
        }

        var keepOpenings = _records
            .Where(record => record.Status == OpeningRecordStatus.Prepared)
            .ToHashSet();
        var keepRelays = _relayRecords
            .Where(record => record.Status == RelayRecordStatus.Prepared)
            .ToHashSet();
        var consumedRoots = relayByStake.Keys.ToHashSet();
        consumedRoots.UnionWith(_legacySecuredStakeRoots);

        var unresolvedOpenings = _records
            .Where(record => record.Status == OpeningRecordStatus.Committed &&
                record.RewardRootId is MongoId root && !consumedRoots.Contains(root))
            .ToHashSet();
        keepOpenings.UnionWith(unresolvedOpenings);

        var retainedOpeningHistory = _records
            .Where(record => record.Status == OpeningRecordStatus.Committed && !unresolvedOpenings.Contains(record))
            .OrderByDescending(record => record.CommittedAtUtc ?? record.PreparedAtUtc)
            .ThenByDescending(record => record.CaseId.ToString(), StringComparer.Ordinal)
            .Take(RetainedCommittedCount)
            .ToArray();
        keepOpenings.UnionWith(retainedOpeningHistory);

        var activeRelayTips = _relayRecords
            .Where(record => IsActiveRelayTip(record, relayByStake))
            .ToHashSet();
        keepRelays.UnionWith(activeRelayTips);

        var retainedTerminalTips = _relayRecords
            .Where(record => record.Status == RelayRecordStatus.Committed &&
                !activeRelayTips.Contains(record) &&
                IsRelayChainTip(record, relayByStake))
            .OrderByDescending(record => record.CommittedAtUtc ?? record.PreparedAtUtc)
            .ThenByDescending(record => record.StakeRootId.ToString(), StringComparer.Ordinal)
            .Take(RetainedCommittedCount)
            .ToArray();
        keepRelays.UnionWith(retainedTerminalTips);

        foreach (var opening in retainedOpeningHistory)
        {
            if (opening.RewardRootId is MongoId root)
            {
                RetainConsumerChain(root, relayByStake, keepRelays, new HashSet<MongoId>());
            }
        }

        foreach (var relay in keepRelays.ToArray())
        {
            RetainProducerAncestry(relay, openingByRoot, relayByOutput, keepOpenings, keepRelays, new HashSet<MongoId>());
        }

        _records.RemoveAll(record =>
            record.Status == OpeningRecordStatus.Committed && !keepOpenings.Contains(record));
        _relayRecords.RemoveAll(record =>
            record.Status == RelayRecordStatus.Committed && !keepRelays.Contains(record));

        var retainedOpeningRoots = _records
            .Select(record => record.RewardRootId)
            .OfType<MongoId>()
            .ToHashSet();
        _legacySecuredStakeRoots.RemoveWhere(root => !retainedOpeningRoots.Contains(root));
    }

    private void PruneManifestHistory()
    {
        // Manifest history is persisted newest-insertion-first. Never reorder it by timestamps:
        // the host clock can move backward, but a newly completed settlement must still survive pruning.
        if (_manifestReceipts.Count > RetainedManifestReceiptCount)
        {
            _manifestReceipts.RemoveRange(
                RetainedManifestReceiptCount,
                _manifestReceipts.Count - RetainedManifestReceiptCount);
        }

        var retainedManifestIds = _manifestReceipts
            .Select(receipt => receipt.ManifestId)
            .ToHashSet(StringComparer.Ordinal);
        var grantsByManifestId = _manifestClaimGrants.ToDictionary(
            grant => grant.ManifestId,
            StringComparer.Ordinal);
        _manifestClaimGrants.Clear();
        foreach (var receipt in _manifestReceipts.Where(receipt =>
                     receipt.TerminalPhase == Shared.Manifest.ManifestPhase.Granted))
        {
            if (retainedManifestIds.Contains(receipt.ManifestId) &&
                grantsByManifestId.TryGetValue(receipt.ManifestId, out var grant))
            {
                _manifestClaimGrants.Add(grant);
            }
        }
        ValidateManifestClaimGrantPairs(_manifestReceipts, _manifestClaimGrants);
    }

    private static bool IsRelayChainTip(
        RelaySettlementRecord record,
        IReadOnlyDictionary<MongoId, RelaySettlementRecord> relayByStake) =>
        record.OutputRootId is not MongoId outputRoot || !relayByStake.ContainsKey(outputRoot);

    private static bool IsActiveRelayTip(
        RelaySettlementRecord record,
        IReadOnlyDictionary<MongoId, RelaySettlementRecord> relayByStake) =>
        record.Status == RelayRecordStatus.Committed &&
        record.Outcome == Shared.Relay.RelayOutcome.RarityUpgrade &&
        record.Stage < Shared.Relay.RelayRules.MaximumStage &&
        record.InputRarity != Shared.Catalog.RewardRarity.Restricted &&
        record.OutputRootId is MongoId outputRoot &&
        !relayByStake.ContainsKey(outputRoot);

    private static void RetainConsumerChain(
        MongoId sourceRoot,
        IReadOnlyDictionary<MongoId, RelaySettlementRecord> relayByStake,
        ISet<RelaySettlementRecord> keepRelays,
        ISet<MongoId> visitedRoots)
    {
        if (!relayByStake.TryGetValue(sourceRoot, out var consumer))
        {
            return;
        }
        if (!visitedRoots.Add(sourceRoot))
        {
            throw new InvalidOperationException("Relay journal contains a consumer cycle.");
        }

        keepRelays.Add(consumer);
        if (consumer.OutputRootId is MongoId outputRoot)
        {
            RetainConsumerChain(outputRoot, relayByStake, keepRelays, visitedRoots);
        }
    }

    private static void RetainProducerAncestry(
        RelaySettlementRecord consumer,
        IReadOnlyDictionary<MongoId, CaseOpeningRecord> openingByRoot,
        IReadOnlyDictionary<MongoId, RelaySettlementRecord> relayByOutput,
        ISet<CaseOpeningRecord> keepOpenings,
        ISet<RelaySettlementRecord> keepRelays,
        ISet<MongoId> visitedRoots)
    {
        if (!visitedRoots.Add(consumer.StakeRootId))
        {
            throw new InvalidOperationException("Relay journal contains a producer cycle.");
        }
        if (openingByRoot.TryGetValue(consumer.StakeRootId, out var opening))
        {
            keepOpenings.Add(opening);
            return;
        }
        if (!relayByOutput.TryGetValue(consumer.StakeRootId, out var producer))
        {
            return;
        }

        keepRelays.Add(producer);
        RetainProducerAncestry(
            producer,
            openingByRoot,
            relayByOutput,
            keepOpenings,
            keepRelays,
            visitedRoots);
    }
}
