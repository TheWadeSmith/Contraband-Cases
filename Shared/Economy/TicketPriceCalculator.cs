namespace ContrabandCases.Shared.Economy;

/// <summary>
/// The finalized Mechanic purchase / handbook price for the BR-12 Relay Case. The BR-12 Relay Key is no
/// longer sold anywhere (it is find-only, added to raid loot instead -- see
/// ContrabandCases.Server.Loot.ContrabandKeyLootInjector), so this type only ever carries a case price now.
/// The name is kept from when it also carried a key price, to minimise churn across call sites.
/// </summary>
public sealed class TicketPrices
{
    public TicketPrices(long casePrice)
    {
        CasePrice = casePrice;
    }

    public long CasePrice { get; }
}

public static class TicketPriceCalculator
{
    /// <summary>
    /// Computes the case's automatic (non-fixed) Mechanic/handbook price from the reward pack's expected
    /// value, targeting a player return rate of <paramref name="targetReturn"/> and rounding up to the
    /// nearest <paramref name="rounding"/> roubles. Prior to the BR-12 Relay Key becoming find-only, this
    /// total was split 70/30 between the case and the key; the case is now the only priced item, so it
    /// receives the full computed total.
    /// </summary>
    public static TicketPrices Calculate(decimal expectedValue, decimal targetReturn, int rounding)
    {
        if (expectedValue < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedValue));
        }

        if (targetReturn <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(targetReturn));
        }

        if (rounding <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rounding));
        }

        try
        {
            var rawTicket = expectedValue / targetReturn;
            var roundedUnits = decimal.Ceiling(rawTicket / rounding);
            var total = checked((long)(roundedUnits * rounding));
            return new TicketPrices(total);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedValue), "The calculated price exceeds Int64.");
        }
    }
}
