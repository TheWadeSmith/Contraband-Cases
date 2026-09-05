namespace ContrabandCases.Shared.Catalog;

/// <summary>Closed currency surface. Not a general permission for cash in cargo packs.</summary>
public static class CashPayouts
{
    public const string Provider = "cash-cache";
    public const string SelectionRule = "SingleCashPayoutV1";
    public const string Roubles = "5449016a4bdc2d6f028b456f";
    public const string Dollars = "5696686a4bdc2da3298b456a";
    public const string Euros = "569668774bdc2da2298b4568";
    public const string Bitcoin = "59faff1d86f7746c51718c9c";

    public static bool IsAllowed(string template) => template is Roubles or Dollars or Euros or Bitcoin;

    public static string ValueLabel(string template) => template switch
    {
        Roubles => "Rouble payout",
        Dollars or Euros => "Estimated purchase value — not a rouble cash-out",
        Bitcoin => "Estimated Therapist sale value at catalog startup",
        _ => "Reference value — not a cash payout"
    };
}
