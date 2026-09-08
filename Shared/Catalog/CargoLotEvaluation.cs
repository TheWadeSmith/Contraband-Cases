namespace ContrabandCases.Shared.Catalog;

/// <summary>
/// Server-derived, snapshot-stable facts about one exact cargo forest.
/// Reward packs cannot provide any of these values.
/// </summary>
public sealed class CargoLotEvaluation
{
    public CargoLotEvaluation(
        long handbookValue,
        long useValue,
        int footprintCells,
        RewardRarity grade,
        long? traderResaleEstimate = null)
    {
        if (handbookValue <= 0)
        {
            throw new CargoCatalogValidationException("Cargo handbook value must be positive.");
        }
        if (useValue <= 0)
        {
            throw new CargoCatalogValidationException("Cargo use value must be positive.");
        }
        if (footprintCells <= 0)
        {
            throw new CargoCatalogValidationException("Cargo footprint must be positive.");
        }
        if (!Enum.IsDefined(typeof(RewardRarity), grade))
        {
            throw new CargoCatalogValidationException("Cargo grade is unknown.");
        }

        HandbookValue = handbookValue;
        UseValue = useValue;
        FootprintCells = footprintCells;
        Grade = grade;
        if (traderResaleEstimate < 0 || traderResaleEstimate > 10_000_000_000L)
            throw new CargoCatalogValidationException("Trader resale estimate is out of bounds.");
        TraderResaleEstimate = traderResaleEstimate;
    }

    public long HandbookValue { get; }

    public long UseValue { get; }

    public int FootprintCells { get; }

    public RewardRarity Grade { get; }

    public long? TraderResaleEstimate { get; }
}

/// <summary>
/// Stable launch bands. The server assigns the grade from the complete lot's
/// computed use value; a data pack never supplies or overrides it.
/// </summary>
public static class CargoGradeBands
{
    // These value bands do not guarantee complete Relay pools. The finalized
    // catalog report measures actual same-track replacements and upgrades.
    public const long UncommonMinimum = 40_000;
    public const long RareMinimum = 75_000;
    public const long EpicMinimum = 150_000;
    public const long LegendaryMinimum = 300_000;

    public static RewardRarity Assign(long useValue)
    {
        if (useValue <= 0)
        {
            throw new CargoCatalogValidationException("Cargo use value must be positive.");
        }

        return useValue switch
        {
            < UncommonMinimum => RewardRarity.ScavGrade,
            < RareMinimum => RewardRarity.Uncommon,
            < EpicMinimum => RewardRarity.Contractor,
            < LegendaryMinimum => RewardRarity.Restricted,
            _ => RewardRarity.BlackLabel
        };
    }
}
