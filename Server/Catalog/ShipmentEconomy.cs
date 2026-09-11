using ContrabandCases.Shared.Catalog;

namespace ContrabandCases.Server.Catalog;

/// <summary>
/// Immutable economy generation: authored shipment recipes coexist with their
/// original paid lots. Never resize a saved reward or change native item values.
/// </summary>
internal static class ShipmentEconomy
{
    internal const string Suffix = ".shipment-v1";
    internal const string CompactSuffix = ".compact-v1";
    internal const string CuratedSuffix = ".compact-v2";

    internal static bool IsCurated(string lotId) => JackpotPayouts.IsJackpot(lotId) || lotId.EndsWith(CuratedSuffix, StringComparison.Ordinal);
    internal static bool IsCompact(string lotId) => IsCurated(lotId) || lotId.EndsWith(CompactSuffix, StringComparison.Ordinal);
    internal static int Generation(string lotId) => JackpotPayouts.IsJackpot(lotId) ? 4 : IsCurated(lotId) ? 3 : IsCompact(lotId) ? 2 : IsShipment(lotId) ? 1 : 0;

    internal static bool IsShipment(string lotId) => lotId.EndsWith(Suffix, StringComparison.Ordinal);

    internal static string BaseId(string lotId) => JackpotPayouts.IsJackpot(lotId) ? BaseId(lotId[..^JackpotPayouts.Suffix.Length]) :
        IsCurated(lotId) ? BaseId(lotId[..^CuratedSuffix.Length]) :
        IsCompact(lotId) ? BaseId(lotId[..^CompactSuffix.Length]) :
        IsShipment(lotId) ? lotId[..^Suffix.Length] : lotId;

    internal static RewardRarity Grade(string lotId, long useValue)
    {
        if (Generation(lotId) == 0) return CargoGradeBands.Assign(useValue);
        if (useValue <= 0) throw new CargoCatalogValidationException("Shipment use value must be positive.");
        if (JackpotPayouts.IsJackpot(lotId)) return RewardRarity.BlackLabel;
        if (IsCurated(lotId)) return useValue switch
        {
            < 400_000 => RewardRarity.ScavGrade,
            < 800_000 => RewardRarity.Uncommon,
            < 1_500_000 => RewardRarity.Contractor,
            < 2_400_000 => RewardRarity.Restricted,
            _ => RewardRarity.BlackLabel
        };
        return useValue switch
        {
            < 240_000 => RewardRarity.ScavGrade,
            < 450_000 => RewardRarity.Uncommon,
            < 900_000 => RewardRarity.Contractor,
            < 1_800_000 => RewardRarity.Restricted,
            _ => RewardRarity.BlackLabel
        };
    }

    internal static IEnumerable<ResolvedCargoLot> CurrentLots(IReadOnlyList<ResolvedCargoLot> lots)
    {
        var replaced = lots.Where(lot => Generation(lot.Identity.LotId) > 0)
            .Select(lot => (lot.Identity.ProviderId, lot.Identity.PackVersion,
                Id: JackpotPayouts.IsJackpot(lot.Identity.LotId) ? lot.Identity.LotId[..^JackpotPayouts.Suffix.Length] :
                    IsCurated(lot.Identity.LotId) ? lot.Identity.LotId[..^CuratedSuffix.Length] + CompactSuffix :
                    IsCompact(lot.Identity.LotId) ? lot.Identity.LotId[..^CompactSuffix.Length] : BaseId(lot.Identity.LotId)))
            .ToHashSet();
        return lots.Where(lot => !replaced.Contains((lot.Identity.ProviderId, lot.Identity.PackVersion, lot.Identity.LotId)));
    }
}
