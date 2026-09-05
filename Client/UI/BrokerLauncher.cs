using UnityEngine;
using UnityEngine.UI;

namespace ContrabandCases.Client.UI;

/// <summary>Small menu-only entry point. It never creates its own EventSystem.</summary>
internal sealed class BrokerLauncher : IDisposable
{
    private GameObject? _root;
    private readonly Action _open;
    internal BrokerLauncher(Action open) => _open = open;

    internal void SetVisible(bool visible)
    {
        if (!visible) { if (_root != null) _root.SetActive(false); return; }
        if (_root == null)
        {
            _root = new GameObject("ContrabandBrokerLauncher", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 29000;
            var scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            var font = UiFactory.ResolveFont();
            var button = UiFactory.CreateButton("Broker", _root.transform, font, "BROKER", new Color(0.12f, 0.17f, 0.20f));
            var rect = button.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 1);
            rect.sizeDelta = new Vector2(190, 58);
            rect.anchoredPosition = new Vector2(-28, -104);
            button.onClick.AddListener(() => _open());
        }
        _root.SetActive(true);
    }

    public void Dispose()
    {
        if (_root != null)
        {
            _root.SetActive(false);
            UnityEngine.Object.Destroy(_root);
        }
        _root = null;
    }
}
