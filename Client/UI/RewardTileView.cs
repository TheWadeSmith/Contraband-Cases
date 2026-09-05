using ContrabandCases.Client.Opening;
using ContrabandCases.Shared;
using ContrabandCases.Shared.Catalog;
using UnityEngine;
using UnityEngine.UI;

namespace ContrabandCases.Client.UI;

internal sealed class RewardTileView
{
    private static readonly Color StandardBackground = new(0.085f, 0.095f, 0.095f, 0.98f);
    private static readonly Color StandardIconBackground = new(0.12f, 0.13f, 0.13f, 1f);
    private static readonly Color SealedBackground = new(0.065f, 0.072f, 0.072f, 0.98f);
    private static readonly Color SealedIconBackground = new(0.085f, 0.092f, 0.092f, 1f);
    private static readonly Color SealedAccent = new(0.46f, 0.49f, 0.49f, 1f);
    private static readonly Color FamilyAccent = new(0.30f, 0.52f, 0.52f, 1f);

    // Vault ("god case") lots get their own accent so a jackpot pull reads
    // as visibly different from an ordinary Black Label at a glance, now
    // that Vault odds have been made rare -- distinct hue from
    // UiFactory.RarityColor's BlackLabel burnt orange (0.88, 0.55, 0.16).
    private static readonly Color VaultAccent = new(0.78f, 0.22f, 0.85f, 1f);

    // Landing-reveal pulse/flash colors, driven purely cosmetically by
    // SetPulse. Vault gets its own flash tint to match its accent.
    private static readonly Color LandingFlashColor = new(1f, 0.96f, 0.78f, 1f);
    private static readonly Color VaultLandingFlashColor = new(0.92f, 0.55f, 1f, 1f);

    // Ambient highlight shared by SetDim (spotlight-on-landing) and
    // SetShimmer (idle shimmer mid-spin). Both always recompute from the
    // stored base colors below, so either is safe to call every frame
    // with a varying amount and never compounds across calls.
    private static readonly Color ShimmerHighlight = new(1f, 0.92f, 0.62f, 1f);

    private readonly Image _background;
    private readonly Image _icon;
    private readonly Image _rarityBar;
    private readonly Text _name;
    private readonly Text _rarity;
    private readonly Text _iconLabel;
    private readonly Text _selectionLabel;
    private readonly Outline _selectionOutline;
    private Color _baseBackground = StandardBackground;
    private Color _baseRarityBarColor = StandardBackground;
    private bool _isVaultAccent;

    public RewardTileView(Transform parent, Font font)
    {
        _background = UiFactory.CreateImage(
            "RewardTile",
            parent,
            StandardBackground);
        Root = _background.rectTransform;

        _selectionOutline = _background.gameObject.AddComponent<Outline>();
        _selectionOutline.effectColor = new Color(1f, 0.72f, 0.24f, 1f);
        _selectionOutline.effectDistance = new Vector2(3f, -3f);
        _selectionOutline.useGraphicAlpha = false;
        _selectionOutline.enabled = false;

        _icon = UiFactory.CreateImage("Icon", Root, StandardIconBackground);
        UiFactory.Center(_icon.rectTransform, 222f, 154f, 0f, 47f);
        _icon.preserveAspect = true;
        _iconLabel = UiFactory.CreateText(
            "Placeholder",
            _icon.transform,
            font,
            22,
            TextAnchor.MiddleCenter,
            new Color(0.70f, 0.73f, 0.72f, 1f));
        UiFactory.Stretch(_iconLabel.rectTransform, 6f, 6f, -6f, -6f);
        _iconLabel.fontStyle = FontStyle.Bold;

        _name = UiFactory.CreateText("Name", Root, font, 24, TextAnchor.MiddleCenter, Color.white);
        UiFactory.Center(_name.rectTransform, 238f, 74f, 0f, -74f);
        _name.fontStyle = FontStyle.Bold;
        _name.resizeTextForBestFit = false;

        _rarity = UiFactory.CreateText("Rarity", Root, font, 24, TextAnchor.MiddleCenter, Color.white);
        UiFactory.Center(_rarity.rectTransform, 238f, 32f, 0f, -120f);

        _rarityBar = UiFactory.CreateImage("RarityIcon", Root, Color.white);
        var barRect = _rarityBar.rectTransform;
        barRect.anchorMin = new Vector2(0f, 0f);
        barRect.anchorMax = new Vector2(1f, 0f);
        barRect.pivot = new Vector2(0.5f, 0f);
        barRect.sizeDelta = new Vector2(0f, 10f);
        barRect.anchoredPosition = Vector2.zero;

        _selectionLabel = UiFactory.CreateText(
            "SelectionLabel",
            Root,
            font,
            22,
            TextAnchor.MiddleCenter,
            Color.white);
        UiFactory.Center(_selectionLabel.rectTransform, 238f, 28f, 0f, 134f);
        _selectionLabel.fontStyle = FontStyle.Bold;
        _selectionLabel.text = "SELECTED";
        _selectionLabel.gameObject.SetActive(false);

        Root.gameObject.SetActive(false);
    }

    public RectTransform Root { get; }

    public string? RewardId { get; private set; }

    // Persisted past Bind so RouletteOverlay can decide, at arbitrary
    // times during the spin (not just at bind time), whether this tile
    // qualifies for the idle shimmer treatment. Placeholder seal tiles
    // are always bound with RewardRarity.ScavGrade (see
    // ManifestPresentationFlow.CreateReveal), so they never qualify.
    public RewardRarity Grade { get; private set; } = RewardRarity.ScavGrade;

    public bool IsAvailable =>
        Root != null &&
        _icon != null &&
        _rarityBar != null &&
        _name != null &&
        _rarity != null &&
        _iconLabel != null &&
        _selectionLabel != null &&
        _selectionOutline != null;

    public void Bind(ValidatedReward reward, Sprite? sprite)
    {
        if (reward is null)
        {
            throw new ArgumentNullException(nameof(reward));
        }

        RequireAvailable();

        RewardId = reward.Id;
        Grade = reward.Rarity;
        _isVaultAccent = false;
        _baseBackground = StandardBackground;
        _background.color = _baseBackground;
        UiFactory.SetCardName(_name, ManifestPresentationPolicy.PlainText(reward.DisplayName));
        _rarity.text = RewardRarities.GetInfo(reward.Rarity).DisplayName;
        _baseRarityBarColor = UiFactory.RarityColor(reward.Rarity);
        _rarityBar.color = _baseRarityBarColor;
        _iconLabel.text = string.Empty;
        SetSprite(sprite);
        if (sprite == null) _iconLabel.text = "LOADING PREVIEW";
        SetSelected(false);
        Root.localScale = Vector3.one;
        Root.gameObject.SetActive(true);
    }

    public void Bind(ManifestTilePresentation tile)
    {
        if (tile is null)
        {
            throw new ArgumentNullException(nameof(tile));
        }

        RequireAvailable();

        RewardId = tile.Id;
        Grade = tile.Grade;
        var sealedFamily = string.Equals(tile.Detail, "CONTENTS UNDISCLOSED", StringComparison.Ordinal);
        var publishedFamily = string.Equals(tile.Detail, "PUBLISHED FAMILY", StringComparison.Ordinal);
        var gradedTile = !sealedFamily && !publishedFamily;
        _isVaultAccent = false;
        _baseBackground = sealedFamily ? SealedBackground : StandardBackground;
        _background.color = _baseBackground;
        UiFactory.SetCardName(_name, ManifestPresentationPolicy.PlainText(tile.DisplayName));
        _rarity.text = gradedTile
            ? RewardRarities.GetInfo(tile.Grade).DisplayName
            : tile.Detail;
        _baseRarityBarColor = sealedFamily
            ? SealedAccent
            : publishedFamily
                ? FamilyAccent
                : _isVaultAccent
                    ? VaultAccent
                    : UiFactory.RarityColor(tile.Grade);
        _rarityBar.color = _baseRarityBarColor;
        _icon.sprite = gradedTile ? null : LootArtwork.Seal(tile.Grade);
        _icon.color = gradedTile ? StandardIconBackground : Color.white;
        _iconLabel.text = sealedFamily
            ? "SEALED"
            : gradedTile ? "LOADING PREVIEW" : "FAMILY";
        _iconLabel.gameObject.SetActive(true);
        SetSelected(false);
        Root.localScale = Vector3.one;
        Root.gameObject.SetActive(true);
    }

    public void SetSelected(bool selected)
    {
        RequireAvailable();
        _selectionOutline.enabled = selected;
        _selectionOutline.effectColor = _isVaultAccent
            ? VaultAccent
            : new Color(1f, 0.72f, 0.24f, 1f);
        _selectionLabel.gameObject.SetActive(selected);
    }

    /// Drives the instant-of-reveal scale-pulse/flash. Called every frame
    /// for a short window right after RevealLandingSelection, then left at
    /// (1f, 0f) -- purely cosmetic, never touches which tile actually won.
    /// <param name="scale">Uniform scale to apply to the tile root.</param>
    /// <param name="flashIntensity">0 = resting background color, 1 = full flash tint.</param>
    public void SetPulse(float scale, float flashIntensity)
    {
        RequireAvailable();
        var clampedScale = Mathf.Max(0.01f, scale);
        Root.localScale = new Vector3(clampedScale, clampedScale, 1f);

        var flash = Mathf.Clamp01(flashIntensity);
        _background.color = flash <= 0f
            ? _baseBackground
            : Color.Lerp(_baseBackground, _isVaultAccent ? VaultLandingFlashColor : LandingFlashColor, flash);
    }

    /// Dims this tile toward black; used to spotlight the landing tile by
    /// darkening every other tile in the strip once the reel settles.
    /// Always recomputed from the stored base colors, so it is safe to
    /// call every frame with a varying amount, and SetDim(0f) fully
    /// restores normal appearance. Purely cosmetic -- never touches
    /// selection state.
    /// <param name="dimAmount">0 = full brightness, 1 = fully darkened.</param>
    public void SetDim(float dimAmount)
    {
        RequireAvailable();
        var clamped = Mathf.Clamp01(dimAmount) * 0.82f;
        var normalIconColor = _icon.sprite == null ? StandardIconBackground : Color.white;
        _background.color = Color.Lerp(_baseBackground, Color.black, clamped);
        _icon.color = Color.Lerp(normalIconColor, Color.black, clamped);
        _rarityBar.color = Color.Lerp(_baseRarityBarColor, Color.black, clamped);
    }

    /// Subtle ambient glow for a currently-visible BlackLabel tile while
    /// the reel is still spinning -- teases the rarest possible outcome
    /// without implying this particular tile is the winner. Always
    /// recomputed from the stored base colors, so it is safe to call
    /// every frame with a varying intensity, and SetShimmer(0f) fully
    /// restores normal appearance.
    /// <param name="intensity">0 = no glow, 1 = peak glow.</param>
    public void SetShimmer(float intensity)
    {
        RequireAvailable();
        var clamped = Mathf.Clamp01(intensity);
        _rarityBar.color = Color.Lerp(_baseRarityBarColor, ShimmerHighlight, clamped * 0.55f);
        _background.color = Color.Lerp(_baseBackground, ShimmerHighlight, clamped * 0.12f);
    }

    public void SetSprite(Sprite? sprite)
    {
        RequireAvailable();
        _icon.sprite = sprite;
        _icon.color = sprite == null ? StandardIconBackground : Color.white;
        _iconLabel.text = sprite == null ? "PREVIEW UNAVAILABLE" : string.Empty;
        _iconLabel.gameObject.SetActive(sprite == null);
    }

    internal void CompleteSpriteLoading()
    {
        if (IsAvailable && _icon.sprite == null) SetSprite(null);
    }

    public void Release()
    {
        RewardId = null;
        if (!IsAvailable)
        {
            return;
        }

        _icon.sprite = null;
        Grade = RewardRarity.ScavGrade;
        _isVaultAccent = false;
        _baseBackground = StandardBackground;
        _baseRarityBarColor = StandardBackground;
        _background.color = _baseBackground;
        _icon.color = StandardIconBackground;
        _iconLabel.text = string.Empty;
        _iconLabel.gameObject.SetActive(false);
        _selectionOutline.enabled = false;
        _selectionLabel.gameObject.SetActive(false);
        _name.text = string.Empty;
        _rarity.text = string.Empty;
        Root.localScale = Vector3.one;
        Root.gameObject.SetActive(false);
    }

    private void RequireAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("The reward tile was destroyed by Unity.");
        }
    }
}
