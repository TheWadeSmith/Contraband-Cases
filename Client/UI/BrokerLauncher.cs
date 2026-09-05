using UnityEngine;
using UnityEngine.UI;

namespace ContrabandCases.Client.UI;

/// <summary>Entry beside Trading, owned and layered by the native main menu.</summary>
internal sealed class BrokerLauncher : IDisposable
{
    private GameObject? _root;
    private RectTransform? _menu;
    private RectTransform? _buttonRect;
    private readonly Vector3[] _corners = new Vector3[4];
    private readonly List<LauncherBounds> _obstacles = new();
    private readonly Action _open;
    internal BrokerLauncher(Action open) => _open = open;

    internal void SetVisible(bool visible, RectTransform? menu = null,
        RectTransform? trading = null, IReadOnlyList<RectTransform>? obstacles = null)
    {
        if (!visible || menu == null || trading == null ||
            !menu.gameObject.activeInHierarchy || !trading.gameObject.activeInHierarchy)
        {
            Hide();
            return;
        }
        if (_menu != menu) Dispose();
        var canvas = menu.GetComponentInParent<Canvas>();
        if (canvas == null) { Hide(); return; }
        var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var safeArea = Screen.safeArea;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(menu, safeArea.min, camera, out var min) ||
            !RectTransformUtility.ScreenPointToLocalPointInRectangle(menu, safeArea.max, camera, out var max))
        {
            Hide();
            return;
        }
        // Never move native menu entries, use another corner, or cover an occupied slot.
        // If a different menu layout leaves no room here, MCM remains available.
        _obstacles.Clear();
        if (obstacles is not null)
            foreach (var obstacle in obstacles)
                if (obstacle != null && obstacle != trading && obstacle.gameObject.activeInHierarchy &&
                    obstacle.rect.width > 0 && obstacle.rect.height > 0)
                    _obstacles.Add(BoundsInMenu(obstacle, menu));
        var left = Mathf.Max(min.x, menu.rect.xMin);
        var bottom = Mathf.Max(min.y, menu.rect.yMin);
        var right = Mathf.Min(max.x, menu.rect.xMax);
        var top = Mathf.Min(max.y, menu.rect.yMax);
        if (!BrokerLauncherPlacement.TryPlace(BoundsInMenu(trading, menu),
                new LauncherBounds(left, bottom, right - left, top - bottom), _obstacles, out var placement))
        {
            Hide();
            return;
        }
        if (_root == null)
        {
            var rootRect = UiFactory.CreateRect("ContrabandBrokerLauncher", menu);
            _root = rootRect.gameObject;
            _root.SetActive(false);
            _menu = menu;
            rootRect.pivot = menu.pivot;
            UiFactory.Stretch(rootRect);
            _root.AddComponent<LayoutElement>().ignoreLayout = true;
            var font = UiFactory.ResolveFont();
            var button = UiFactory.CreateButton("Broker", rootRect, font, "Broker", new Color(0.12f, 0.17f, 0.20f));
            _buttonRect = button.GetComponent<RectTransform>();
            _buttonRect.anchorMin = _buttonRect.anchorMax = menu.pivot;
            _buttonRect.pivot = Vector2.zero;
            button.onClick.AddListener(() => { if (_root != null && _root.activeInHierarchy) _open(); });
        }
        _buttonRect!.sizeDelta = new Vector2(placement.Width, placement.Height);
        _buttonRect.anchoredPosition = new Vector2(placement.Left, placement.Bottom);
        _root.SetActive(true);
    }

    private LauncherBounds BoundsInMenu(RectTransform target, RectTransform menu)
    {
        target.GetWorldCorners(_corners);
        var first = menu.InverseTransformPoint(_corners[0]);
        var min = new Vector2(first.x, first.y);
        var max = min;
        for (var index = 1; index < _corners.Length; index++)
        {
            var point = menu.InverseTransformPoint(_corners[index]);
            min = Vector2.Min(min, new Vector2(point.x, point.y));
            max = Vector2.Max(max, new Vector2(point.x, point.y));
        }
        return new LauncherBounds(min.x, min.y, max.x - min.x, max.y - min.y);
    }

    private void Hide() { if (_root != null) _root.SetActive(false); }

    public void Dispose()
    {
        if (_root != null)
        {
            _root.SetActive(false);
            UnityEngine.Object.Destroy(_root);
        }
        _root = null;
        _buttonRect = null;
        _menu = null;
    }
}
