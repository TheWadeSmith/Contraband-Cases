namespace ContrabandCases.Server.Settlement;

/// <summary>Ordinary pre-consumption rejection, not a corrupt or failed transaction.</summary>
internal sealed class RelayKeyRequiredException : InvalidOperationException
{
    internal RelayKeyRequiredException()
        : base("BR-12 Relay Key required. Place a usable key in your stash and try again. No items were consumed.")
    {
    }
}
