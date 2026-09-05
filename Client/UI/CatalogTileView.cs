using System.Globalization;
using ContrabandCases.Shared.Catalog;
using UnityEngine;
using UnityEngine.UI;

namespace ContrabandCases.Client.UI;

/// A compact tile for the pre-open confirmation screen's reward catalog
/// grid -- icon (or the same "ITEM" text fallback RewardTileView uses when
/// a sprite is unavailable), name, and the exact published weight
/// percentage, plus a rarity-tier color bar. Deliberately leaner than
/// RewardTileView (no selection outline, landing pulse, dim, or shimmer --
/// none of that spin-animation state applies to a static catalog grid) so
/// up to ~80+ of these can be pooled and bound cheaply.
internal sealed class CatalogTileView
{
    private static readonly Color Background = new(0.078f, 0.088f, 0.088f, 0.98f);
    private static readonly Color IconBackground = new(0.12f, 0.13f, 0.13f, 1f);
    private static readonly Color WeightColor = new(0.80f, 0.83f, 0.82f, 1f);

    private readonly Image _background;
    private readonly Image _icon;
    private readonly Image _rarityBar;
    private readonly Text _name;
    private readonly Text _weight;
    private readonly Text _iconLabel;
    private RewardRarity _grade;

    public CatalogTileView(Transform parent, Font font)
    {
        _background = UiFactory.CreateImage("CatalogTile", parent, Background);
        Root = _background.rectTransform;

        _icon = UiFactory.CreateImage("Icon", Root, IconBackground);
        UiFactory.Center(_icon.rectTransform, 132f, 78f, 0f, 33f);
        _icon.preserveAspect = true;
        _iconLabel = UiFactory.CreateText(
            "Placeholder",
            _icon.transform,
            font,
            13,
            TextAnchor.MiddleCenter,
            new Color(0.70f, 0.73f, 0.72f, 1f));
        UiFactory.Stretch(_iconLabel.rectTransform, 4f, 4f, -4f, -4f);
        _iconLabel.fontStyle = FontStyle.Bold;

        _name = UiFactory.CreateText("Name", Root, font, 14, TextAnchor.MiddleCenter, Color.white);
        UiFactory.Center(_name.rectTransform, 140f, 34f, 0f, -22f);
        _name.fontStyle = FontStyle.Bold;
        _name.resizeTextForBestFit = true;
        _name.resizeTextMinSize = 10;
        _name.resizeTextMaxSize = 14;

        _weight = UiFactory.CreateText("Weight", Root, font, 12, TextAnchor.MiddleCenter, WeightColor);
        UiFactory.Center(_weight.rectTransform, 140f, 18f, 0f, -49f);
        _weight.fontStyle = FontStyle.Bold;
        _weight.resizeTextForBestFit = true;
        _weight.resizeTextMinSize = 9;
        _weight.resizeTextMaxSize = 12;

        _rarityBar = UiFactory.CreateImage("RarityBar", Root, Color.white);
        var barRect = _rarityBar.rectTransform;
        barRect.anchorMin = new Vector2(0f, 0f);
        barRect.anchorMax = new Vector2(1f, 0f);
        barRect.pivot = new Vector2(0.5f, 0f);
        barRect.sizeDelta = new Vector2(0f, 5f);
        barRect.anchoredPosition = Vector2.zero;

        Root.gameObject.SetActive(false);
    }

    public RectTransform Root { get; }

    public string? RewardId { get; private set; }

    public bool IsAvailable =>
        Root != null &&
        _icon != null &&
        _rarityBar != null &&
        _name != null &&
        _weight != null &&
        _iconLabel != null;

    public void Bind(ValidatedReward reward, Sprite? sprite)
    {
        if (reward is null)
        {
            throw new ArgumentNullException(nameof(reward));
        }

        Bind(
            reward.Id,
            reward.DisplayName,
            reward.Weight.ToString("0.##%", CultureInfo.InvariantCulture),
            reward.Rarity,
            sprite);
    }

    /// Binds this tile from a caller-supplied identity/odds text rather
    /// than a ValidatedReward -- used by the Manifest confirmation
    /// screen's full-odds audit grid, whose entries come from the
    /// server-published opening-odds disclosure (ManifestOpeningLotOddsSnapshot)
    /// instead of the reward catalog. `oddsText` is displayed exactly as
    /// given: the audit grid passes the server's own pre-formatted
    /// percentage string (e.g. lot.ConditionalPercent) rather than a
    /// recomputed one, since that exact text is itself part of this
    /// screen's "these are the exact server-published values" guarantee.
    public void Bind(string id, string displayName, string oddsText, RewardRarity rarity, Sprite? sprite)
    {
        if (id is null)
        {
            throw new ArgumentNullException(nameof(id));
        }
        if (displayName is null)
        {
            throw new ArgumentNullException(nameof(displayName));
        }
        if (oddsText is null)
        {
            throw new ArgumentNullException(nameof(oddsText));
        }

        RequireAvailable();

        RewardId = id;
        _grade = rarity;
        _name.text = displayName;
        _weight.text = $"{RewardRarities.GetInfo(rarity).DisplayName} • {oddsText}";
        _rarityBar.color = UiFactory.RarityColor(rarity);
        SetSprite(sprite);
        Root.localScale = Vector3.one;
        Root.gameObject.SetActive(true);
    }

    public void SetSprite(Sprite? sprite)
    {
        RequireAvailable();
        _icon.sprite = sprite == null ? LootArtwork.Seal(_grade) : sprite;
        _icon.color = Color.white;
        _iconLabel.text = string.Empty;
        _iconLabel.gameObject.SetActive(false);
    }

    public void Release()
    {
        RewardId = null;
        if (!IsAvailable)
        {
            return;
        }

        _icon.sprite = null;
        _icon.color = IconBackground;
        _iconLabel.text = string.Empty;
        _iconLabel.gameObject.SetActive(false);
        _name.text = string.Empty;
        _weight.text = string.Empty;
        Root.gameObject.SetActive(false);
    }

    private void RequireAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("The catalog tile was destroyed by Unity.");
        }
    }
}
