using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace ContrabandCases.Server.Settlement;

public readonly record struct RelayInventoryEvidence(
    RewardPresence InputPresence,
    bool KeyPresent,
    RewardPresence OutputPresence);

public enum RelayDecision
{
    ApplyPrepared,
    RecoverCommitted,
    ReplayCommitted,
    RejectContradiction
}

public static class RelayStateMachine
{
    public static RelayDecision Decide(RelaySettlementRecord record, RelayInventoryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Action != RelayRecordAction.Relay)
        {
            throw new ArgumentException("Inventory state decisions apply only to Relay mutations.", nameof(record));
        }

        if (record.Status == RelayRecordStatus.Committed)
        {
            return RelayDecision.ReplayCommitted;
        }

        var expectedOutput = record.Outcome == RelayOutcome.Confiscated
            ? RewardPresence.Absent
            : RewardPresence.Complete;
        if (evidence == new RelayInventoryEvidence(RewardPresence.Complete, true, RewardPresence.Absent))
        {
            return RelayDecision.ApplyPrepared;
        }
        if (record.ProfileCommitStarted &&
            evidence == new RelayInventoryEvidence(RewardPresence.Absent, false, expectedOutput))
        {
            return RelayDecision.RecoverCommitted;
        }

        return RelayDecision.RejectContradiction;
    }
}

public sealed record RelayStake(
    MongoId OriginCaseId,
    MongoId RootId,
    string RewardId,
    RewardRarity Rarity,
    RarityLadderVersion RarityLadderVersion,
    int Stage,
    bool Terminal,
    RelayOutcome? TerminalOutcome,
    IReadOnlyList<Item> ExpectedItems);

public static class RelayChainResolver
{
    public static RelayStake Resolve(
        CaseOpeningJournal journal,
        MongoId stakeRootId,
        Func<string, ValidatedReward> findReward)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(findReward);

        var opening = journal.Records.SingleOrDefault(record =>
            record.Status == OpeningRecordStatus.Committed && record.RewardRootId == stakeRootId);
        if (opening is not null)
        {
            var reward = findReward(opening.RewardId);
            var consumer = journal.FindRelay(stakeRootId);
            return new RelayStake(
                opening.CaseId,
                stakeRootId,
                reward.Id,
                reward.Rarity,
                opening.RarityLadderVersion,
                1,
                consumer is not null || journal.IsLegacySecured(stakeRootId) || reward.Rarity == RewardRarity.BlackLabel,
                consumer?.Outcome ?? (journal.IsLegacySecured(stakeRootId) ? RelayOutcome.Secured : null),
                opening.RewardItems);
        }

        var producer = journal.RelayRecords.SingleOrDefault(record =>
            record.Status == RelayRecordStatus.Committed && record.OutputRootId == stakeRootId);
        if (producer is null || producer.OutputRewardId is null)
        {
            throw new InvalidOperationException("The requested stake is not a committed Contraband Cases chain tip.");
        }

        var outputReward = findReward(producer.OutputRewardId);
        var nextStage = producer.Outcome == RelayOutcome.RarityUpgrade
            ? checked(producer.Stage + 1)
            : producer.Stage;
        var consumerRecord = journal.FindRelay(stakeRootId);
        var terminal =
            producer.Outcome != RelayOutcome.RarityUpgrade ||
            producer.Stage >= RelayRules.MaximumStage ||
            outputReward.Rarity == RewardRarity.BlackLabel ||
            consumerRecord is not null;
        return new RelayStake(
            producer.OriginCaseId,
            stakeRootId,
            outputReward.Id,
            outputReward.Rarity,
            producer.RarityLadderVersion,
            nextStage,
            terminal,
            consumerRecord?.Outcome ?? (terminal ? producer.Outcome : null),
            producer.OutputItems);
    }
}
