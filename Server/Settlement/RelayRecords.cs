using System.Collections.ObjectModel;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace ContrabandCases.Server.Settlement;

public enum RelayRecordAction
{
    Secure,
    Relay
}

public enum RelayRecordStatus
{
    Prepared,
    Committed
}

public sealed class RelaySettlementRecord
{
    private readonly IReadOnlyList<MongoId> _inputItemIds;
    private readonly IReadOnlyList<Item> _inputItems;
    private readonly IReadOnlyList<Item> _outputItems;
    private readonly IReadOnlyList<MongoId> _exactOutputIds;

    public RelaySettlementRecord(
        MongoId originCaseId,
        MongoId stakeRootId,
        IEnumerable<MongoId> inputItemIds,
        string inputRewardId,
        RewardRarity inputRarity,
        int stage,
        RelayRecordAction action,
        MongoId? keyId,
        RelayOutcome outcome,
        string? outputRewardId,
        IEnumerable<Item>? outputItems,
        int meterBefore,
        int meterAfter,
        bool guaranteedUpgrade,
        bool profileCommitStarted,
        DateTimeOffset preparedAtUtc,
        RelayRecordStatus status,
        DateTimeOffset? committedAtUtc,
        IEnumerable<Item>? inputItems = null,
        RarityLadderVersion rarityLadderVersion = RarityLadderVersion.FiveTier)
    {
        if (originCaseId.IsEmpty || stakeRootId.IsEmpty)
        {
            throw new ArgumentException("Relay origin and stake IDs are required.");
        }
        if (string.IsNullOrWhiteSpace(inputRewardId))
        {
            throw new ArgumentException("A Relay input reward ID is required.", nameof(inputRewardId));
        }
        if (!Enum.IsDefined(inputRarity) || !Enum.IsDefined(action) || !Enum.IsDefined(outcome) || !Enum.IsDefined(status))
        {
            throw new ArgumentException("Relay record contains an unrecognized enum value.");
        }
        RelayRules.ValidateLadderVersion(rarityLadderVersion, nameof(rarityLadderVersion));
        if (!RelayRules.IsSupportedRarity(inputRarity, rarityLadderVersion))
        {
            throw new ArgumentException("Relay record input rarity is not supported by its rarity ladder.");
        }
        _ = RelayRules.GetOdds(stage);
        if (meterBefore is < 0 or > RelayRules.MaximumRecoveryMeter ||
            meterAfter is < 0 or > RelayRules.MaximumRecoveryMeter)
        {
            throw new ArgumentOutOfRangeException(nameof(meterBefore), "Relay meter must be between zero and three.");
        }
        if (preparedAtUtc.Offset != TimeSpan.Zero || committedAtUtc is { Offset: var offset } && offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Relay timestamps must be UTC.");
        }
        if ((status == RelayRecordStatus.Committed) != (committedAtUtc is not null))
        {
            throw new ArgumentException("Relay committed status and timestamp must agree.", nameof(committedAtUtc));
        }

        var inputs = inputItemIds?.ToArray() ?? throw new ArgumentNullException(nameof(inputItemIds));
        if (inputs.Length == 0 || inputs.Distinct().Count() != inputs.Length || !inputs.Contains(stakeRootId))
        {
            throw new ArgumentException("Relay input IDs must be unique and contain the stake root.", nameof(inputItemIds));
        }
        var inputSnapshot = (inputItems ?? []).Select(CaseOpeningRecord.CloneItem).ToArray();
        if (inputSnapshot.Length > 0 &&
            (inputSnapshot.Select(item => item.Id).Distinct().Count() != inputSnapshot.Length ||
             !inputSnapshot.Select(item => item.Id).ToHashSet().SetEquals(inputs) ||
             SettlementItemTrees.FindRootId(inputSnapshot) != stakeRootId))
        {
            throw new ArgumentException("Relay input snapshot must exactly describe the consumed stake tree.", nameof(inputItems));
        }

        var outputs = (outputItems ?? []).Select(CaseOpeningRecord.CloneItem).ToArray();
        if (outputs.Select(item => item.Id).Distinct().Count() != outputs.Length || outputs.Any(item => inputs.Contains(item.Id)))
        {
            throw new ArgumentException("Relay output item IDs must be unique and distinct from the stake.", nameof(outputItems));
        }

        if (action == RelayRecordAction.Secure)
        {
            if (keyId is not null || outcome != RelayOutcome.Secured || outputRewardId is not null || outputs.Length != 0 ||
                meterAfter != meterBefore || guaranteedUpgrade || profileCommitStarted)
            {
                throw new ArgumentException("Secure records cannot consume a key, mutate inventory, or change the recovery meter.");
            }
        }
        else
        {
            if (keyId is null || keyId.Value.IsEmpty || keyId.Value == stakeRootId || inputs.Contains(keyId.Value) ||
                outcome == RelayOutcome.Secured)
            {
                throw new ArgumentException("Relay records require a distinct key and economic outcome.");
            }
            if (status == RelayRecordStatus.Committed && !profileCommitStarted)
            {
                throw new ArgumentException("Committed Relay records require a profile-commit marker.");
            }
            if (guaranteedUpgrade != (meterBefore == RelayRules.MaximumRecoveryMeter))
            {
                throw new ArgumentException("Guaranteed-upgrade marker must match the prepared recovery meter.");
            }
            if (meterAfter != RelayRules.MeterAfterOutcome(meterBefore, stage, outcome))
            {
                throw new ArgumentException("Relay recovery meter does not match the planned outcome.");
            }
            if (outcome == RelayOutcome.Confiscated)
            {
                if (outputRewardId is not null || outputs.Length != 0)
                {
                    throw new ArgumentException("Confiscation records cannot contain an output reward.");
                }
            }
            else if (string.IsNullOrWhiteSpace(outputRewardId) || outputs.Length == 0)
            {
                throw new ArgumentException("Upgrade and sidegrade records require an output reward tree.");
            }
        }

        OriginCaseId = originCaseId;
        StakeRootId = stakeRootId;
        _inputItemIds = new ReadOnlyCollection<MongoId>(inputs);
        _inputItems = new ReadOnlyCollection<Item>(inputSnapshot);
        InputRewardId = inputRewardId;
        InputRarity = inputRarity;
        Stage = stage;
        Action = action;
        KeyId = keyId;
        Outcome = outcome;
        OutputRewardId = outputRewardId;
        _outputItems = new ReadOnlyCollection<Item>(outputs);
        _exactOutputIds = new ReadOnlyCollection<MongoId>(outputs.Select(item => item.Id).ToArray());
        MeterBefore = meterBefore;
        MeterAfter = meterAfter;
        GuaranteedUpgrade = guaranteedUpgrade;
        ProfileCommitStarted = profileCommitStarted;
        PreparedAtUtc = preparedAtUtc;
        Status = status;
        CommittedAtUtc = committedAtUtc;
        RarityLadderVersion = rarityLadderVersion;
    }

    public MongoId OriginCaseId { get; }
    public MongoId StakeRootId { get; }
    public IReadOnlyList<MongoId> InputItemIds => _inputItemIds;
    public IReadOnlyList<Item> InputItems => new ReadOnlyCollection<Item>(_inputItems.Select(CaseOpeningRecord.CloneItem).ToArray());
    public string InputRewardId { get; }
    public RewardRarity InputRarity { get; }
    public int Stage { get; }
    public RelayRecordAction Action { get; }
    public MongoId? KeyId { get; }
    public RelayOutcome Outcome { get; }
    public string? OutputRewardId { get; }
    public IReadOnlyList<Item> OutputItems => new ReadOnlyCollection<Item>(_outputItems.Select(CaseOpeningRecord.CloneItem).ToArray());
    public IReadOnlyList<MongoId> ExactOutputIds => _exactOutputIds;
    public MongoId? OutputRootId => SettlementItemTrees.FindRootId(_outputItems);
    public int MeterBefore { get; }
    public int MeterAfter { get; }
    public bool GuaranteedUpgrade { get; }
    public bool ProfileCommitStarted { get; }
    public DateTimeOffset PreparedAtUtc { get; }
    public RelayRecordStatus Status { get; }
    public DateTimeOffset? CommittedAtUtc { get; }
    public RarityLadderVersion RarityLadderVersion { get; }

    public RelaySettlementRecord BeginProfileCommit(IEnumerable<Item>? locatedOutputItems = null) => Copy(
        locatedOutputItems ?? OutputItems,
        profileCommitStarted: true,
        RelayRecordStatus.Prepared,
        null);

    public RelaySettlementRecord Commit(DateTimeOffset committedAtUtc) => Copy(
        OutputItems,
        ProfileCommitStarted,
        RelayRecordStatus.Committed,
        committedAtUtc);

    private RelaySettlementRecord Copy(
        IEnumerable<Item> outputItems,
        bool profileCommitStarted,
        RelayRecordStatus status,
        DateTimeOffset? committedAtUtc) => new(
            OriginCaseId,
            StakeRootId,
            InputItemIds,
            InputRewardId,
            InputRarity,
            Stage,
            Action,
            KeyId,
            Outcome,
            OutputRewardId,
            outputItems,
            MeterBefore,
            MeterAfter,
            GuaranteedUpgrade,
            profileCommitStarted,
            PreparedAtUtc,
            status,
            committedAtUtc,
            InputItems,
            RarityLadderVersion);
}

internal static class SettlementItemTrees
{
    public static MongoId? FindRootId(IReadOnlyCollection<Item> items)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var ids = items.Select(item => item.Id.ToString()).ToHashSet(StringComparer.Ordinal);
        var roots = items.Where(item => item.ParentId is null || !ids.Contains(item.ParentId)).ToArray();
        if (roots.Length != 1)
        {
            throw new InvalidOperationException("A settlement reward tree must contain exactly one root.");
        }

        return roots[0].Id;
    }
}
