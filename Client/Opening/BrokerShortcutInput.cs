namespace ContrabandCases.Client.Opening;

/// <summary>Accepts only a fresh, safe Broker request; blocked presses are never queued.</summary>
internal static class BrokerShortcutInput
{
    internal static bool ShouldOpen(bool pressed, bool focused, bool available, bool menuOrStash, bool editing) =>
        pressed && focused && available && menuOrStash && !editing;
}
