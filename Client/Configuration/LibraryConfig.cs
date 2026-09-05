using BepInEx.Configuration;
using ContrabandCases.Client.Opening;

namespace ContrabandCases.Client.Configuration;

internal sealed class LibraryConfig
{
    public LibraryConfig(ConfigFile config)
    {
        ShowBrokerButton = config.Bind("Broker Dossier", "Show Broker Button", true,
            "Show the BROKER entry in the main menu/stash. Opens read-only rewards, saved openings, history and status. Hidden in raids and during case operations.");
        OpenDossier = config.Bind("Broker Dossier", "Open Dossier", false,
            "Open Broker: reward browser, saved settlement history, Favor and status/sound test. Resume a saved reward without another case/key. Requires the main menu/stash and a connected server. Resets after one request.");
        OpenGallery = config.Bind("Testing (Catalog Gallery)", "Open Gallery", false,
            "Browse real resolved server lots in the current UI. Requires Testing Mode. Reads the catalog only; never spends or grants items. Resets after one request.");
        LotId = config.Bind("Testing (Catalog Gallery)", "Lot ID", "",
            "Optional exact lotId or providerId/lotId. Leave empty to browse all matching lots. IDs are in catalog-report.json.");
        Rarity = config.Bind("Testing (Catalog Gallery)", "Rarity", GalleryRarity.Any,
            "Filter real lots by their actual rarity. Does not regrade items or affect real openings.");
        State = config.Bind("Testing (Catalog Gallery)", "Preview State", GalleryState.Offer1,
            "Choose the real UI layout to preview. The preview is clearly labeled and cannot send economic actions.");
        MissingArtwork = config.Bind("Testing (Catalog Gallery)", "Force Missing Artwork", false,
            "Skip sprite requests in the gallery to inspect the clearly labeled unavailable-preview state.");
        LongName = config.Bind("Testing (Catalog Gallery)", "Long Name Stress Test", false,
            "Use a long sample title in the gallery only to inspect wrapping.");
    }

    public ConfigEntry<bool> OpenDossier { get; }
    public ConfigEntry<bool> ShowBrokerButton { get; }
    public ConfigEntry<bool> OpenGallery { get; }
    public ConfigEntry<string> LotId { get; }
    public ConfigEntry<GalleryRarity> Rarity { get; }
    public ConfigEntry<GalleryState> State { get; }
    public ConfigEntry<bool> MissingArtwork { get; }
    public ConfigEntry<bool> LongName { get; }
}
