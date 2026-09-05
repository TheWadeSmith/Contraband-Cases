using ContrabandCases.Server.Settlement;
using ContrabandCases.Shared.Catalog;
using ContrabandCases.Shared.Relay;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class RelayStateMachineTests
{
    [Fact]
    public void Outputless_confiscation_recovers_only_after_durable_commit_marker()
    {
        var prepared = Record(RelayOutcome.Confiscated, profileCommitStarted: false);
        var absent = new RelayInventoryEvidence(RewardPresence.Absent, false, RewardPresence.Absent);

        Assert.Equal(RelayDecision.RejectContradiction, RelayStateMachine.Decide(prepared, absent));
        Assert.Equal(RelayDecision.RecoverCommitted, RelayStateMachine.Decide(prepared.BeginProfileCommit(), absent));
    }

    [Fact]
    public void Prepared_upgrade_applies_from_inputs_and_recovers_from_complete_output()
    {
        var prepared = Record(RelayOutcome.RarityUpgrade, profileCommitStarted: false);

        Assert.Equal(RelayDecision.ApplyPrepared, RelayStateMachine.Decide(
            prepared,
            new RelayInventoryEvidence(RewardPresence.Complete, true, RewardPresence.Absent)));
        Assert.Equal(RelayDecision.RecoverCommitted, RelayStateMachine.Decide(
            prepared.BeginProfileCommit(prepared.OutputItems),
            new RelayInventoryEvidence(RewardPresence.Absent, false, RewardPresence.Complete)));
    }

    [Fact]
    public void Committed_records_replay_without_considering_live_inventory()
    {
        var committed = Record(RelayOutcome.Confiscated, true).Commit(DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal(RelayDecision.ReplayCommitted, RelayStateMachine.Decide(
            committed,
            new RelayInventoryEvidence(RewardPresence.Partial, true, RewardPresence.Partial)));
    }

    private static RelaySettlementRecord Record(RelayOutcome outcome, bool profileCommitStarted)
    {
        var root = new MongoId();
        var output = outcome == RelayOutcome.Confiscated
            ? Array.Empty<Item>()
            : [new Item { Id = new MongoId(), Template = new MongoId() }];
        return new RelaySettlementRecord(
            new MongoId(), root, [root], "input", RewardRarity.ScavGrade, 1,
            RelayRecordAction.Relay, new MongoId(), outcome,
            outcome == RelayOutcome.Confiscated ? null : "output", output,
            0, outcome == RelayOutcome.Confiscated ? 1 : 0, false,
            profileCommitStarted, DateTimeOffset.UnixEpoch, RelayRecordStatus.Prepared, null);
    }
}
