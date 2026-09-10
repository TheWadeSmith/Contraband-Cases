using Comfort.Common;
using EFT;
using EFT.UI;
using EFT.UI.Screens;

namespace ContrabandCases.Client.UI;

internal static class LobbyUiContext
{
    // GameWorld alone is insufficient: it may not exist yet between raid scenes.
    internal static bool IsMenuOrStashReady => !Singleton<GameWorld>.Instantiated &&
        EftScreenManager._instance?.CurrentBaseScreenController switch
        {
            MenuScreen.MainMenuScreenController { Closed: false } menu =>
                menu.Screen != null && menu.Screen.isActiveAndEnabled,
            InventoryScreen.InventoryScreenController { Closed: false } stash =>
                stash.Screen != null && stash.Screen.isActiveAndEnabled,
            _ => false
        };
}
