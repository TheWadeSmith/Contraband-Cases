namespace ContrabandCases.Server.Settlement;

public enum RewardPresence
{
    Absent,
    Complete,
    Partial
}

public readonly record struct InventoryEvidence(bool CasePresent, bool KeyPresent, RewardPresence RewardPresence);

public enum OpeningDecision
{
    PrepareNew,
    ApplyPrepared,
    RecoverCommitted,
    ReplayCommitted,
    RejectContradiction
}

public static class OpeningStateMachine
{
    public static OpeningDecision Decide(CaseOpeningRecord? record, InventoryEvidence evidence)
    {
        if (record is null)
        {
            return evidence.CasePresent ? OpeningDecision.PrepareNew : OpeningDecision.RejectContradiction;
        }

        return record.Status switch
        {
            OpeningRecordStatus.Prepared => (evidence.CasePresent, evidence.KeyPresent, evidence.RewardPresence) switch
            {
                (true, true, RewardPresence.Absent) => OpeningDecision.ApplyPrepared,
                (false, false, RewardPresence.Complete) => OpeningDecision.RecoverCommitted,
                _ => OpeningDecision.RejectContradiction
            },
            OpeningRecordStatus.Committed => OpeningDecision.ReplayCommitted,
            _ => OpeningDecision.RejectContradiction
        };
    }
}
