namespace ContrabandCases.Shared.Catalog;

/// <summary>
/// Selection categories are deliberately broader than authored themes. Keep
/// original identities intact so existing journals still resolve exact lots.
/// </summary>
public static class CargoFamilies
{
    public const string SelectionVersion = "broad-families-v1";

    public static FamilyId SelectionFamily(FamilyId theme) => new(theme.Value switch
    {
        "attachments" or "weapon-relic" => "arsenal",
        "armor" => "operator",
        "tech" or "exotic-ordnance" => "field-supply",
        _ => theme.Value
    });

    public static string Label(string family) => family switch
    {
        "arsenal" => "Weapons",
        "operator" => "Equipment",
        "field-supply" => "Supplies",
        "attachments" => "Attachments",
        "armor" => "Armor",
        "tech" => "Technology",
        "weapon-relic" => "Relics",
        "exotic-ordnance" => "Exotic Ordnance",
        _ => family
    };

    public static string? Signal(string theme) => SelectionFamily(new FamilyId(theme)).Value switch
    {
        "arsenal" => "Weapons signal",
        "operator" => "Equipment signal",
        "field-supply" => "Supplies signal",
        _ => null
    };

    public static bool IsSignal(string label) => label is "Weapons signal" or "Equipment signal" or "Supplies signal";
}
