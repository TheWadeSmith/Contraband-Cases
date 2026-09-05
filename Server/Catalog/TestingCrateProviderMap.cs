using ContrabandCases.Shared;

namespace ContrabandCases.Server.Catalog;

/// <summary>
/// Maps each debug/testing <see cref="TestingCrateType"/> selection to the set
/// of reward-pack provider IDs it forces a Manifest opening to draw from. This
/// is pure data -- it never crashes and never affects a normal, non-forced
/// opening, whose <see cref="ManifestCatalogSelector.CreateOffers"/> call
/// passes no override.
///
/// Provider weights referenced here live entirely in their own JSON pack
/// files under config/reward-packs/ and follow the project's existing scale
/// (core = 1.0, vault = 0.05, themed packs ~0.12-0.35): "Scrap" and "Mega" are
/// pure selections over those existing weights, not a new numeric scale.
/// </summary>
internal static class TestingCrateProviderMap
{
    private static readonly IReadOnlySet<string> VaultProviders =
        Set(ModConstants.VaultProviderId);

    private static readonly IReadOnlySet<string> ThemedProviders =
        Set("sjx.combat-chemistry", "vultify.cooler-stims");

    private static readonly IReadOnlySet<string> CardsProviders =
        Set("krackasourus.anime-cards", "krackasourus.pokemon-cards", "krackasourus.yugioh-cards");

    private static readonly IReadOnlySet<string> ScrapProviders =
        Set(
            "core",
            "natalya.field-gear",
            "isb-aishi.field-armory",
            "wtt-contentbackport.field-resupply",
            "eco-attachment.field-cache");

    private static readonly IReadOnlySet<string> MegaProviders =
        Set(
            ModConstants.VaultProviderId,
            "natalya.elite-armor",
            "isb-aishi.elite-armory",
            "wtt-contentbackport.elite-optics",
            "eco-attachment.elite-optics",
            "amonya.arcane-cache",
            "eco-ww2.relic-cache");

    /// <summary>
    /// Returns the allow-list of provider IDs for the given crate type, or
    /// <see langword="null"/> for <see cref="TestingCrateType.TrueRandom"/>
    /// (meaning: no restriction, the normal full blended catalog).
    /// </summary>
    public static IReadOnlySet<string>? GetAllowedProviderIds(TestingCrateType crateType) => crateType switch
    {
        TestingCrateType.TrueRandom => null,
        TestingCrateType.Vault => VaultProviders,
        TestingCrateType.Themed => ThemedProviders,
        TestingCrateType.Cards => CardsProviders,
        TestingCrateType.Scrap => ScrapProviders,
        TestingCrateType.Mega => MegaProviders,
        _ => null
    };

    private static IReadOnlySet<string> Set(params string[] providerIds) =>
        new HashSet<string>(providerIds, StringComparer.Ordinal);
}
