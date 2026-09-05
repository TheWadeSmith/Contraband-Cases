using System.Collections.ObjectModel;
using ContrabandCases.Shared.Catalog;

namespace ContrabandCases.Server.Catalog;

/// <summary>
/// Projects the frozen v0.2 weapon catalog into the v0.3 cargo-lot boundary.
/// The adapter is deliberately additive: the legacy catalog remains the source
/// of truth until the manifest coordinator is wired in a later slice.
/// </summary>
public static class LegacyWeaponPresetProvider
{
    public const string ProviderId = "contrabandcases.legacy-weapons";
    public const string PackVersion = "0.2.0";

    private static readonly FamilyId ArsenalFamily = new("arsenal");
    private static readonly TrackId LegacyWeaponTrack = new("legacy-weapon");
    private static readonly RaidRole LegacyWeaponRole = new("legacy-complete-weapon");

    public static IReadOnlyList<CargoLotDefinition> Adapt(IEnumerable<ValidatedReward> rewards)
    {
        ArgumentNullException.ThrowIfNull(rewards);

        var source = rewards.ToArray();
        if (source.Length == 0)
        {
            throw new CargoCatalogValidationException("The legacy reward catalog cannot be empty.");
        }

        var lots = new CargoLotDefinition[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            var reward = source[index]
                ?? throw new CargoCatalogValidationException("The legacy reward catalog contains a null reward.");

            lots[index] = new CargoLotDefinition(
                ProviderId,
                PackVersion,
                reward.Id,
                reward.DisplayName,
                "A complete weapon preset from the original BR-12 reward catalog.",
                ArsenalFamily,
                LegacyWeaponTrack,
                reward.WeaponTemplateId,
                reward.Weight,
                LegacyWeaponRole,
                [new PresetLine(reward.PresetId)]);
        }

        return new ReadOnlyCollection<CargoLotDefinition>(lots);
    }
}
