using ContrabandCases.Server.Settlement;
using SPTarkov.Server.Core.Models.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace ContrabandCases.Tests.Settlement;

public sealed class OpeningStateMachineTests
{
    [Fact]
    public void Missing_record_with_case_is_new()
    {
        Assert.Equal(OpeningDecision.PrepareNew, OpeningStateMachine.Decide(null, new InventoryEvidence(true, true, RewardPresence.Absent)));
    }

    [Fact]
    public void Missing_record_without_case_is_rejected()
    {
        Assert.Equal(OpeningDecision.RejectContradiction, OpeningStateMachine.Decide(null, new InventoryEvidence(false, true, RewardPresence.Absent)));
    }

    [Theory]
    [InlineData(true, true, RewardPresence.Absent, OpeningDecision.ApplyPrepared)]
    [InlineData(true, true, RewardPresence.Complete, OpeningDecision.RejectContradiction)]
    [InlineData(true, true, RewardPresence.Partial, OpeningDecision.RejectContradiction)]
    [InlineData(true, false, RewardPresence.Absent, OpeningDecision.RejectContradiction)]
    [InlineData(true, false, RewardPresence.Complete, OpeningDecision.RejectContradiction)]
    [InlineData(true, false, RewardPresence.Partial, OpeningDecision.RejectContradiction)]
    [InlineData(false, true, RewardPresence.Absent, OpeningDecision.RejectContradiction)]
    [InlineData(false, true, RewardPresence.Complete, OpeningDecision.RejectContradiction)]
    [InlineData(false, true, RewardPresence.Partial, OpeningDecision.RejectContradiction)]
    [InlineData(false, false, RewardPresence.Absent, OpeningDecision.RejectContradiction)]
    [InlineData(false, false, RewardPresence.Complete, OpeningDecision.RecoverCommitted)]
    [InlineData(false, false, RewardPresence.Partial, OpeningDecision.RejectContradiction)]
    public void Prepared_evidence_rows_are_exhaustive_and_fail_closed(bool casePresent, bool keyPresent, RewardPresence rewardPresence, OpeningDecision expected)
    {
        Assert.Equal(expected, OpeningStateMachine.Decide(Record(OpeningRecordStatus.Prepared), new InventoryEvidence(casePresent, keyPresent, rewardPresence)));
    }

    [Fact]
    public void Committed_record_is_replayed_regardless_of_inventory_evidence()
    {
        Assert.Equal(OpeningDecision.ReplayCommitted, OpeningStateMachine.Decide(Record(OpeningRecordStatus.Committed), new InventoryEvidence(false, false, RewardPresence.Partial)));
    }

    [Fact]
    public void Malformed_persisted_status_is_rejected_fail_closed()
    {
        var record = (CaseOpeningRecord)RuntimeHelpers.GetUninitializedObject(typeof(CaseOpeningRecord));
        var statusField = typeof(CaseOpeningRecord).GetField("<Status>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(statusField);
        statusField!.SetValue(record, (OpeningRecordStatus)91);

        Assert.Equal(OpeningDecision.RejectContradiction, OpeningStateMachine.Decide(record, new InventoryEvidence(true, true, RewardPresence.Absent)));
    }

    private static CaseOpeningRecord Record(OpeningRecordStatus status) => new(
        new MongoId(), new MongoId(), "reward", new(), DateTimeOffset.UtcNow, status,
        status == OpeningRecordStatus.Committed ? DateTimeOffset.UtcNow : null);
}
