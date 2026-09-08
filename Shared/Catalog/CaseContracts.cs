namespace ContrabandCases.Shared.Catalog;

/// <summary>Fixed case identities and grouping rules; never rewrites saved lot identities.</summary>
public static class CaseContracts
{
    public const string Operations = "66d000000000000000000011";
    public const string Relics = "66d000000000000000000012";
    public const string BlackSite = "66d000000000000000000013";
    public const string CashCache = "66d000000000000000000014";
    public const string SelectionVersion = "case-contracts-v3-typical-price";

    public static IReadOnlyList<string> Templates { get; } = Array.AsReadOnly(new[]
        { ModConstants.CaseTemplateId, Operations, Relics, BlackSite, CashCache });

    public static bool IsCase(string? template) => template is
        ModConstants.CaseTemplateId or Operations or Relics or BlackSite or CashCache;

    public static int OfferCount(string template) => Require(template) == CashCache ? 1 : 3;

    public static string Require(string template) => IsCase(template) ? template :
        throw new ArgumentException("Unknown Contraband case template.", nameof(template));

    public static string Name(string template) => Require(template) switch
    {
        Operations => "BR-12 Operations Case",
        Relics => "BR-12 Relics Case",
        BlackSite => "BR-12 Black Site Case",
        CashCache => "BR-12 Cash Cache",
        _ => "BR-12 Mixed Case"
    };

    public static string ShortName(string template) => Require(template) switch
    {
        Operations => "Operations",
        Relics => "Relics",
        BlackSite => "Black Site",
        CashCache => "Cash Cache",
        _ => "BR-12 Case"
    };

    public static string AssortId(string template) => Require(template) switch
    {
        Operations => "66d000000000000000000021",
        Relics => "66d000000000000000000022",
        BlackSite => "66d000000000000000000023",
        CashCache => "66d000000000000000000024",
        _ => ModConstants.MechanicCaseAssortRootId
    };

    public static string Description(string template) => Require(template) switch
    {
        Operations => "Practical raid packages: ammunition, medical supplies, equipment and loadouts, with rare high-value jackpots. Theme does not guarantee reward rarity or value.",
        Relics => "Collectible cards, arcane curios and historical relics from installed reward packs. Normal openings offer three distinct collection or relic tracks; rare surprise openings offer three premium packages to choose from.",
        BlackSite => "Specialist raid packages from night operations, ordnance, vault equipment, optics and experimental supplies. Normal openings offer three distinct tracks with no guaranteed rarity or value; rare surprise openings offer three premium packages to choose from.",
        CashCache => "One universal key, one payout: roubles, dollars, euros, GP Coins or rare physical Bitcoin. No discard, Relay or Favor. Read the exact payout odds before opening; foreign currency and GP barter values are not guaranteed rouble cash-outs.",
        _ => "Mixed packages from all available reward packs."
    };

    public static FamilyId SelectionFamily(string template, CargoLotIdentitySnapshot identity) =>
        UsesTrackGroups(template) ? new FamilyId(identity.TrackId.Value) :
            CargoFamilies.SelectionFamily(identity.FamilyId);

    public static bool UsesTrackGroups(string template) => Require(template) is Relics or BlackSite;

    public static string GroupLabel(string template, string group) => UsesTrackGroups(template)
        ? group switch
        {
            "anime-cards" => "Anime Cards",
            "pokemon-cards" => "Pokemon Cards",
            "yugioh-cards" => "Yu-Gi-Oh Cards",
            "eco-ww2-relic-cache" => "Historical Relics",
            "amonya-arcane-cache" => "Arcane Curios",
            "vault" => "Vault Equipment",
            "night" => "Night Operations",
            "ordnance" => "Ordnance",
            "chemistry" => "Combat Chemistry",
            "cooler-stims" => "Experimental Stims",
            "eco-elite-optics" => "Specialist Optics",
            "relic-expedition" => "Relic Expeditions",
            "ammo" => "Ammunition",
            "recon" => "Recon Equipment",
            "rifle" => "Precision Weapons",
            _ => CargoFamilies.Label(group)
        }
        : CargoFamilies.Label(group);
}
