namespace ContrabandCases.Shared;

/// <summary>
/// Selects actual case templates or forced mixed-case reward pools for the
/// server-opt-in MCM inventory grant. It never changes purchased cases.
/// </summary>
public enum TestingCrateType
{
    /// <summary>The normal full blended catalog. No forcing occurs.</summary>
    TrueRandom = 0,

    /// <summary>Forces the "vault" provider (the existing rare/legendary pool).</summary>
    Vault = 1,

    /// <summary>Forces the existing non-card themed gear packs (SJX, Vultify).</summary>
    Themed = 2,

    /// <summary>Forces the existing Krackasourus collectible-card packs.</summary>
    Cards = 3,

    /// <summary>
    /// A broad, common-feeling mix: core plus the everyday-tier curated
    /// modded packs. High pick weight, low individual rarity.
    /// </summary>
    Scrap = 4,

    /// <summary>
    /// A rare, high-value mix: the vault plus the elite-tier curated modded
    /// packs. Low pick weight, meant to feel special.
    /// </summary>
    Mega = 5,

    /// <summary>Grants the actual purchasable themed case, without debug pool forcing.</summary>
    OperationsCase = 6,
    RelicsCase = 7,
    BlackSiteCase = 8,
    CashCache = 9,
    EpicMixed = 10,
    LegendaryMixed = 11,
    EpicOperations = 12,
    LegendaryOperations = 13,
    EpicRelics = 14,
    LegendaryRelics = 15,
    EpicBlackSite = 16,
    LegendaryBlackSite = 17
}

/// <summary>
/// Stable wire encoding for <see cref="TestingCrateType"/> shared by the client
/// request and the server request handler. Decoding never throws -- an
/// unrecognized, missing, or stale value always falls back to
/// <see cref="TestingCrateType.TrueRandom"/> so a forced-pool feature failure
/// can never block or corrupt a testing grant.
/// </summary>
public static class TestingCrateTypeCodec
{
    public static string ToWireValue(TestingCrateType crateType) => crateType switch
    {
        TestingCrateType.TrueRandom => "trueRandom",
        TestingCrateType.Vault => "vault",
        TestingCrateType.Themed => "themed",
        TestingCrateType.Cards => "cards",
        TestingCrateType.Scrap => "scrap",
        TestingCrateType.Mega => "mega",
        TestingCrateType.OperationsCase => "operationsCase",
        TestingCrateType.RelicsCase => "relicsCase",
        TestingCrateType.BlackSiteCase => "blackSiteCase",
        TestingCrateType.CashCache => "cashCache",
        TestingCrateType.EpicMixed => "epicMixed",
        TestingCrateType.LegendaryMixed => "legendaryMixed",
        TestingCrateType.EpicOperations => "epicOperations",
        TestingCrateType.LegendaryOperations => "legendaryOperations",
        TestingCrateType.EpicRelics => "epicRelics",
        TestingCrateType.LegendaryRelics => "legendaryRelics",
        TestingCrateType.EpicBlackSite => "epicBlackSite",
        TestingCrateType.LegendaryBlackSite => "legendaryBlackSite",
        _ => "trueRandom"
    };

    public static TestingCrateType Parse(string? value) => value switch
    {
        "vault" => TestingCrateType.Vault,
        "themed" => TestingCrateType.Themed,
        "cards" => TestingCrateType.Cards,
        "scrap" => TestingCrateType.Scrap,
        "mega" => TestingCrateType.Mega,
        "operationsCase" => TestingCrateType.OperationsCase,
        "relicsCase" => TestingCrateType.RelicsCase,
        "blackSiteCase" => TestingCrateType.BlackSiteCase,
        "cashCache" => TestingCrateType.CashCache,
        "epicMixed" => TestingCrateType.EpicMixed,
        "legendaryMixed" => TestingCrateType.LegendaryMixed,
        "epicOperations" => TestingCrateType.EpicOperations,
        "legendaryOperations" => TestingCrateType.LegendaryOperations,
        "epicRelics" => TestingCrateType.EpicRelics,
        "legendaryRelics" => TestingCrateType.LegendaryRelics,
        "epicBlackSite" => TestingCrateType.EpicBlackSite,
        "legendaryBlackSite" => TestingCrateType.LegendaryBlackSite,
        _ => TestingCrateType.TrueRandom
    };

    public static string CaseTemplate(TestingCrateType type) => type switch
    {
        TestingCrateType.OperationsCase or TestingCrateType.EpicOperations or TestingCrateType.LegendaryOperations => Catalog.CaseContracts.Operations,
        TestingCrateType.RelicsCase or TestingCrateType.EpicRelics or TestingCrateType.LegendaryRelics => Catalog.CaseContracts.Relics,
        TestingCrateType.BlackSiteCase or TestingCrateType.EpicBlackSite or TestingCrateType.LegendaryBlackSite => Catalog.CaseContracts.BlackSite,
        TestingCrateType.CashCache => Catalog.CaseContracts.CashCache,
        _ => ModConstants.CaseTemplateId
    };

    public static Manifest.ManifestOpeningTier? PremiumTier(TestingCrateType type) => type switch
    {
        TestingCrateType.EpicMixed or TestingCrateType.EpicOperations or TestingCrateType.EpicRelics or TestingCrateType.EpicBlackSite
            => Manifest.ManifestOpeningTier.Epic,
        TestingCrateType.LegendaryMixed or TestingCrateType.LegendaryOperations or TestingCrateType.LegendaryRelics or TestingCrateType.LegendaryBlackSite
            => Manifest.ManifestOpeningTier.Legendary,
        _ => null
    };
}
