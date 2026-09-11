namespace ContrabandCases.Shared.Catalog;

/// <summary>Equipment-jackpot currency contract; separate from Cash Cache draws.</summary>
public static class JackpotPayouts
{
    public const string Suffix = ".jackpot-v2";
    public const int MaximumCashStacks = 32;

    public static bool IsJackpot(string lotId) => lotId.EndsWith(Suffix, StringComparison.Ordinal);

    public static void ValidateDefinition(string lotId, int roubleBonus)
    {
        if (roubleBonus != 0 && roubleBonus is not (3_000_000 or 5_000_000) ||
            IsJackpot(lotId) != (roubleBonus > 0))
            throw new CargoCatalogValidationException("Only a new equipment-jackpot identity may promise a 3,000,000 or 5,000,000 rouble bonus.");
    }

    public static void ValidateForest(RewardForest forest, int roubleBonus)
    {
        if (roubleBonus is not (3_000_000 or 5_000_000))
            throw new CargoCatalogValidationException("The jackpot bonus amount is invalid.");
        var cash = forest.Nodes.Where(node => node.TemplateId == CashPayouts.Roubles).ToArray();
        var equipment = forest.Nodes.Where(node => node.TemplateId != CashPayouts.Roubles).ToArray();
        if (cash.Length is < 1 or > MaximumCashStacks ||
            cash.Sum(node => (long)node.StackCount) != roubleBonus ||
            cash.Any(node => node.ParentLogicalPath is not null || node.SlotId is not null ||
                node.InternalLocation is not null || node.StableState is not null) ||
            equipment.Length is < 1 or > 128 || equipment.Count(node => node.ParentLogicalPath is null) is < 1 or > 8 ||
            equipment.Any(node => CashPayouts.IsAllowed(node.TemplateId)) ||
            equipment.Any(node => cash.Any(currency => node.ParentLogicalPath == currency.LogicalPath)))
            throw new CargoCatalogValidationException("A jackpot must contain bounded equipment and the exact plain-root rouble bonus.");
    }
}
