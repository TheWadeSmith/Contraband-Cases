using ContrabandCases.Shared.Catalog;

namespace ContrabandCases.Server.Catalog;

/// <summary>
/// Immutable economy generation: authored shipment recipes coexist with their
/// original paid lots. Never resize a saved reward or change native item values.
/// </summary>
internal static class ShipmentEconomy
{
    internal const string Suffix = ".shipment-v1";

    internal static bool IsShipment(string lotId) => lotId.EndsWith(Suffix, StringComparison.Ordinal);

    internal static string BaseId(string lotId) => IsShipment(lotId) ? lotId[..^Suffix.Length] : lotId;

    internal static RewardRarity Grade(string lotId, long useValue)
    {
        if (!IsShipment(lotId)) return CargoGradeBands.Assign(useValue);
        if (useValue <= 0) throw new CargoCatalogValidationException("Shipment use value must be positive.");
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
        var replaced = lots.Where(lot => IsShipment(lot.Identity.LotId))
            .Select(lot => (lot.Identity.ProviderId, lot.Identity.PackVersion, Id: BaseId(lot.Identity.LotId)))
            .ToHashSet();
        return lots.Where(lot => !replaced.Contains((lot.Identity.ProviderId, lot.Identity.PackVersion, lot.Identity.LotId)));
    }
}
