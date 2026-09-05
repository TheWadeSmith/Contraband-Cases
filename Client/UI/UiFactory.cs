using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ContrabandCases.Client.UI;

internal static class UiFactory
{
    internal static Font ResolveFont()
    {
        var nativeFont = Resources.FindObjectsOfTypeAll<Text>()
            .Where(text => text != null && text.gameObject.activeInHierarchy)
            .Select(text => text.font).FirstOrDefault(font => font != null);
        if (nativeFont != null) return nativeFont;
        // Unity renamed the built-in font in 2022; older Tarkov runtimes use Arial.
        try
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null) return font;
        }
        catch (ArgumentException) { /* Older Unity: try the original built-in name. */ }
        return Resources.GetBuiltinResource<Font>("Arial.ttf")
            ?? throw new InvalidOperationException("No native or built-in Unity UI font is available.");
    }

    public static RectTransform CreateRect(string name, Transform parent)
    {
        var gameObject = new GameObject(name, typeof(RectTransform));
        var rect = gameObject.GetComponent<RectTransform>();
        rect.SetParent(parent, worldPositionStays: false);
        return rect;
    }

    public static Image CreateImage(string name, Transform parent, Color color, bool raycastTarget = false)
    {
        var rect = CreateRect(name, parent);
        var image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = raycastTarget;
        return image;
    }

    public static Text CreateText(
        string name,
        Transform parent,
        Font font,
        int fontSize,
        TextAnchor alignment,
        Color color)
    {
        var rect = CreateRect(name, parent);
        var text = rect.gameObject.AddComponent<Text>();
        text.font = font;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = color;
        text.supportRichText = true;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.raycastTarget = false;
        return text;
    }

    // Card names have a fixed footprint; inspection always retains the full name.
    internal static void SetCardName(Text label, string fullName)
    {
        label.text = fullName;
        if (label.preferredHeight <= label.rectTransform.rect.height) return;
        var low = 0;
        var high = fullName.Length;
        var best = "…";
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var length = middle;
            if (length > 0 && char.IsHighSurrogate(fullName[length - 1])) length--;
            var candidate = fullName.Substring(0, length).TrimEnd() + "…";
            label.text = candidate;
            if (label.preferredHeight <= label.rectTransform.rect.height)
            {
                best = candidate;
                low = middle + 1;
            }
            else high = middle - 1;
        }
        label.text = best;
    }

    public static Button CreateButton(
        string name,
        Transform parent,
        Font font,
        string label,
        Color background)
    {
        var image = CreateImage(name, parent, background, raycastTarget: true);
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        var colors = button.colors;
        colors.highlightedColor = Brighten(background, 0.16f);
        colors.pressedColor = Brighten(background, -0.10f);
        colors.disabledColor = new Color(background.r, background.g, background.b, 0.35f);
        button.colors = colors;
        AddFocusTreatment(button);

        var text = CreateText("Label", button.transform, font, 22, TextAnchor.MiddleCenter, Color.white);
        Stretch(text.rectTransform, 8f, 4f, -8f, -4f);
        text.text = label;
        text.fontStyle = FontStyle.Bold;
        return button;
    }

    public static void AddFocusTreatment(Selectable button)
    {
        if (button.GetComponent<FocusOutline>() != null) return;
        var outline = button.gameObject.AddComponent<Outline>();
        outline.effectColor = Color.white;
        outline.effectDistance = new Vector2(3f, -3f);
        outline.useGraphicAlpha = false;
        outline.enabled = false;
        button.gameObject.AddComponent<FocusOutline>().Outline = outline;
    }

    public static void Stretch(
        RectTransform rect,
        float left = 0f,
        float bottom = 0f,
        float right = 0f,
        float top = 0f)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(right, top);
    }

    public static void Center(RectTransform rect, float width, float height, float x = 0f, float y = 0f)
    {
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(width, height);
        rect.anchoredPosition = new Vector2(x, y);
    }

    public static Color RarityColor(Shared.Catalog.RewardRarity rarity) => rarity switch
    {
        Shared.Catalog.RewardRarity.ScavGrade => new Color(0.58f, 0.61f, 0.61f, 1f),
        Shared.Catalog.RewardRarity.Uncommon => new Color(0.25f, 0.72f, 0.38f, 1f),
        Shared.Catalog.RewardRarity.Contractor => new Color(0.20f, 0.48f, 0.86f, 1f),
        Shared.Catalog.RewardRarity.Restricted => new Color(0.58f, 0.28f, 0.78f, 1f),
        Shared.Catalog.RewardRarity.BlackLabel => new Color(0.88f, 0.55f, 0.16f, 1f),
        _ => Color.white
    };

    public static string Hex(Color color) => ColorUtility.ToHtmlStringRGB(color);

    private static Color Brighten(Color color, float amount) => new(
        Mathf.Clamp01(color.r + amount),
        Mathf.Clamp01(color.g + amount),
        Mathf.Clamp01(color.b + amount),
        color.a);
}

internal sealed class FocusOutline : MonoBehaviour, ISelectHandler, IDeselectHandler
{
    public Outline? Outline { get; set; }
    public void OnSelect(BaseEventData eventData) { if (Outline != null) Outline.enabled = true; }
    public void OnDeselect(BaseEventData eventData) { if (Outline != null) Outline.enabled = false; }
    private void OnDisable() { if (Outline != null) Outline.enabled = false; }
}

/// <summary>Page keys scroll the active overlay without moving focus into the game.</summary>
internal sealed class OverlayScrollKeys : MonoBehaviour, IPointerEnterHandler, ISelectHandler
{
    private static OverlayScrollKeys? _active;
    public ScrollRect? Scroll { get; set; }
    private void OnEnable() => _active = this;
    private void OnDisable() { if (_active == this) _active = null; }
    public void OnPointerEnter(PointerEventData eventData) => _active = this;
    public void OnSelect(BaseEventData eventData) => _active = this;
    private void Update()
    {
        if (_active != this || Scroll == null || !Application.isFocused) return;
        var rect = Scroll.viewport.rect;
        var overflow = Scroll.content.rect.height - rect.height;
        if (overflow <= 0) return;
        var step = rect.height * 0.8f / overflow;
        if (Input.GetKeyDown(KeyCode.PageDown)) Scroll.verticalNormalizedPosition = Mathf.Clamp01(Scroll.verticalNormalizedPosition - step);
        if (Input.GetKeyDown(KeyCode.PageUp)) Scroll.verticalNormalizedPosition = Mathf.Clamp01(Scroll.verticalNormalizedPosition + step);
        if (Input.GetKeyDown(KeyCode.Home)) Scroll.verticalNormalizedPosition = 1f;
        if (Input.GetKeyDown(KeyCode.End)) Scroll.verticalNormalizedPosition = 0f;
    }
}

internal sealed class OverlayTabNavigation : MonoBehaviour
{
    public Selectable[] Controls { get; set; } = [];
    private void Update()
    {
        if (!Application.isFocused || !Input.GetKeyDown(KeyCode.Tab) || EventSystem.current == null) return;
        var controls = Controls.Where(button => button != null && button.isActiveAndEnabled && button.IsInteractable()).ToArray();
        if (controls.Length == 0) return;
        var current = Array.FindIndex(controls, button => button.gameObject == EventSystem.current.currentSelectedGameObject);
        var step = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? -1 : 1;
        var next = current < 0 ? (step == 1 ? 0 : controls.Length - 1) : (current + step + controls.Length) % controls.Length;
        EventSystem.current.SetSelectedGameObject(controls[next].gameObject);
    }
}

/// <summary>Submit only on an explicit Enter press, never just because the input loses focus.</summary>
internal sealed class BrokerSearchSubmit : MonoBehaviour
{
    public Action? Submit { get; set; }
    private void Update()
    {
        if (Application.isFocused && EventSystem.current != null &&
            EventSystem.current.currentSelectedGameObject == gameObject &&
            (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))) Submit?.Invoke();
    }
}

internal sealed class BrokerBrowserResize : MonoBehaviour
{
    public int Columns { get; set; }
    public Action? Refresh { get; set; }
    private void Update()
    {
        var columns = Opening.BrokerLibraryLayout.ForScreen(Screen.width, Screen.height).Columns;
        if (Columns == columns) return;
        Columns = columns;
        Refresh?.Invoke();
    }
}
