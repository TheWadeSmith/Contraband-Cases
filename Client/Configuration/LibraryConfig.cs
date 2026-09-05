using BepInEx.Configuration;
using ContrabandCases.Client.Opening;
using UnityEngine;

namespace ContrabandCases.Client.Configuration;

internal sealed class LibraryConfig
{
    public LibraryConfig(ConfigFile config, Func<bool>? previewsEnabled = null)
    {
        // Deliberate opt-in under a new key: the old default-true setting must not
        // resurrect the intrusive launcher on upgrade. BepInEx preserves it as an orphan.
        ShowBrokerButton = config.Bind("Broker Dossier", "Show Menu Button", false,
            McmSettings.Option(McmSettings.General, "Show Broker button", 20,
                "Optional: show Broker beside Trading on the main menu when there is room. Off by default, including upgrades. The shortcut and Open Broker below work without this button.", advanced: true));
        OpenBrokerShortcut = config.Bind("Broker Dossier", "Open Broker Shortcut", new KeyboardShortcut(KeyCode.F5),
            McmSettings.Option(McmSettings.General, "Open Broker shortcut", 15,
                "Open Broker from the main menu or stash (default F5). Change or clear this shortcut here. Ignored in raids, while a case window is active, or while MCM is open. Use Close/Escape to leave Broker."));
        OpenDossier = McmSettings.BindAction(config, "Broker Dossier", "Open Dossier",
            McmSettings.General, "Open Broker", 10,
            "Browse rewards, resume a saved package, view history and Favor, or test sound under Status. Requires a connected server and no other case window.");
        OpenGallery = McmSettings.BindAction(config, "Testing (Catalog Gallery)", "Open Gallery",
            McmSettings.Advanced, "Open catalog preview", 160,
            "Preview real catalog packages with the filters below. Reads the server catalog but never spends or grants items. Enable animation previews first.",
            () => previewsEnabled?.Invoke() == true ? null : "Enable previews first", advanced: true);
        LotId = config.Bind("Testing (Catalog Gallery)", "Lot ID", "",
            McmSettings.Option(McmSettings.Advanced, "Package ID filter", 100,
                "Optional lotId or providerId/lotId from catalog-report.json. Empty shows all matches.", advanced: true));
        Rarity = config.Bind("Testing (Catalog Gallery)", "Rarity", GalleryRarity.Any,
            McmSettings.Option(McmSettings.Advanced, "Catalog rarity filter", 110,
                "Filter catalog previews by actual rarity. Does not change opening odds.", advanced: true));
        State = config.Bind("Testing (Catalog Gallery)", "Preview State", GalleryState.Offer1,
            McmSettings.Option(McmSettings.Advanced, "Catalog screen to preview", 120,
                "Choose a read-only screen layout to inspect. Preview buttons cannot spend or grant anything.", advanced: true));
        MissingArtwork = config.Bind("Testing (Catalog Gallery)", "Force Missing Artwork", false,
            McmSettings.Option(McmSettings.Advanced, "Simulate missing pictures", 130,
                "Catalog preview only: skip pictures to inspect the missing-art fallback.", advanced: true));
        LongName = config.Bind("Testing (Catalog Gallery)", "Long Name Stress Test", false,
            McmSettings.Option(McmSettings.Advanced, "Simulate long package names", 140,
                "Catalog preview only: use a long sample title to inspect text wrapping.", advanced: true));
    }

    public ConfigEntry<bool> OpenDossier { get; }
    public ConfigEntry<bool> ShowBrokerButton { get; }
    public ConfigEntry<KeyboardShortcut> OpenBrokerShortcut { get; }
    public ConfigEntry<bool> OpenGallery { get; }
    public ConfigEntry<string> LotId { get; }
    public ConfigEntry<GalleryRarity> Rarity { get; }
    public ConfigEntry<GalleryState> State { get; }
    public ConfigEntry<bool> MissingArtwork { get; }
    public ConfigEntry<bool> LongName { get; }
}
