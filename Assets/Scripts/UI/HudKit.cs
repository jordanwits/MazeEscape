using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Runtime factory for the in-game HUD, sharing <see cref="MenuTheme"/>'s palette, fonts and
/// procedural sprites so gameplay UI speaks the same language as the menus: dark weathered
/// plates, bone frames and type, mustard only where something is selected or charged.
/// </summary>
public static class HudKit
{
    const string CanvasName = "GameHudCanvas";

    /// <summary>Shared screen-space canvas for all HUD elements (created on demand).</summary>
    public static Canvas EnsureHudCanvas()
    {
        GameObject existing = GameObject.Find(CanvasName);
        if (existing != null && existing.TryGetComponent(out Canvas found))
            return found;

        var go = new GameObject(CanvasName);
        go.layer = 5;
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        canvas.vertexColorAlwaysGammaSpace = true;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        go.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    /// <summary>Dark plate + bone frame behind a HUD element. Returns the frame for state tinting.</summary>
    public static Image AddPlate(GameObject go, float fillAlpha = 0.72f, float frameAlpha = 0.20f)
    {
        var bg = go.GetComponent<Image>();
        if (bg == null)
            bg = go.AddComponent<Image>();
        bg.sprite = MenuTheme.RoundedRect(2);
        bg.type = Image.Type.Sliced;
        bg.color = MenuTheme.WithAlpha(MenuTheme.Ink, fillAlpha);
        bg.raycastTarget = false;

        var frameGo = new GameObject("Frame", typeof(RectTransform));
        frameGo.layer = 5;
        var frameRt = (RectTransform)frameGo.transform;
        frameRt.SetParent(go.transform, false);
        frameRt.anchorMin = Vector2.zero;
        frameRt.anchorMax = Vector2.one;
        frameRt.offsetMin = Vector2.zero;
        frameRt.offsetMax = Vector2.zero;
        var frame = frameGo.AddComponent<Image>();
        frame.sprite = MenuTheme.RoundedOutline(2, 1.6f);
        frame.type = Image.Type.Sliced;
        frame.color = MenuTheme.WithAlpha(MenuTheme.Bone, frameAlpha);
        frame.raycastTarget = false;
        var le = frameGo.AddComponent<LayoutElement>();
        le.ignoreLayout = true;
        return frame;
    }

    /// <summary>Square key legend (e.g. E) in the plate language.</summary>
    public static RectTransform CreateKeycap(Transform parent, string key, float size = 27f)
    {
        var go = new GameObject("Keycap_" + key, typeof(RectTransform));
        go.layer = 5;
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.sizeDelta = new Vector2(size, size);

        var fill = go.AddComponent<Image>();
        fill.sprite = MenuTheme.RoundedRect(2);
        fill.type = Image.Type.Sliced;
        fill.color = MenuTheme.WithAlpha(MenuTheme.Tile, 0.9f);
        fill.raycastTarget = false;

        var frameGo = new GameObject("Frame", typeof(RectTransform));
        frameGo.layer = 5;
        var frameRt = (RectTransform)frameGo.transform;
        frameRt.SetParent(rt, false);
        frameRt.anchorMin = Vector2.zero;
        frameRt.anchorMax = Vector2.one;
        frameRt.offsetMin = Vector2.zero;
        frameRt.offsetMax = Vector2.zero;
        var frame = frameGo.AddComponent<Image>();
        frame.sprite = MenuTheme.RoundedOutline(2, 1.6f);
        frame.type = Image.Type.Sliced;
        frame.color = MenuTheme.WithAlpha(MenuTheme.Bone, 0.7f);
        frame.raycastTarget = false;

        var labelGo = new GameObject("Key", typeof(RectTransform));
        labelGo.layer = 5;
        var labelRt = (RectTransform)labelGo.transform;
        labelRt.SetParent(rt, false);
        labelRt.anchorMin = Vector2.zero;
        labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = Vector2.zero;
        labelRt.offsetMax = Vector2.zero;
        var label = labelGo.AddComponent<TextMeshProUGUI>();
        label.font = MenuTheme.DisplayFont;
        label.text = key;
        label.fontSize = size * 0.58f;
        label.color = MenuTheme.Bone;
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;

        var le = go.AddComponent<LayoutElement>();
        le.minWidth = size;
        le.preferredWidth = size;
        le.minHeight = size;
        le.preferredHeight = size;
        return rt;
    }
}

/// <summary>
/// Contextual interaction chip under the crosshair: [E] + a terse verb, or an icon + count for
/// occupancy readouts. "Press E to ..." strings are collapsed to the verb automatically, so
/// legacy prompt messages render terse without touching every call site.
/// </summary>
[DisallowMultipleComponent]
public sealed class HudPrompt : MonoBehaviour
{
    CanvasGroup _group;
    GameObject _keycap;
    Image _icon;
    TextMeshProUGUI _label;
    bool _shown;

    public static HudPrompt Create(Transform canvasRoot)
    {
        var go = new GameObject("HudPrompt", typeof(RectTransform));
        go.layer = 5;
        var rt = (RectTransform)go.transform;
        rt.SetParent(canvasRoot, false);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -96f);
        rt.localRotation = Quaternion.Euler(0f, 0f, -0.4f);

        var prompt = go.AddComponent<HudPrompt>();
        prompt.Build(go);
        return prompt;
    }

    void Build(GameObject go)
    {
        _group = go.AddComponent<CanvasGroup>();
        _group.alpha = 0f;
        _group.blocksRaycasts = false;
        _group.interactable = false;

        HudKit.AddPlate(go, 0.78f, 0.24f);

        var layout = go.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(12, 16, 8, 8);
        layout.spacing = 10f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _keycap = HudKit.CreateKeycap(transform, "E").gameObject;

        var iconGo = new GameObject("Icon", typeof(RectTransform));
        iconGo.layer = 5;
        ((RectTransform)iconGo.transform).SetParent(transform, false);
        _icon = iconGo.AddComponent<Image>();
        _icon.preserveAspect = true;
        _icon.raycastTarget = false;
        _icon.color = MenuTheme.Bone;
        var iconLe = iconGo.AddComponent<LayoutElement>();
        iconLe.minWidth = 24f;
        iconLe.preferredWidth = 24f;
        iconLe.minHeight = 24f;
        iconLe.preferredHeight = 24f;
        iconGo.SetActive(false);

        var labelGo = new GameObject("Label", typeof(RectTransform));
        labelGo.layer = 5;
        ((RectTransform)labelGo.transform).SetParent(transform, false);
        _label = labelGo.AddComponent<TextMeshProUGUI>();
        _label.font = MenuTheme.DisplayFont;
        _label.fontSize = 17f;
        _label.color = MenuTheme.Bone;
        _label.characterSpacing = 3f;
        _label.alignment = TextAlignmentOptions.Midline;
        _label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        _label.raycastTarget = false;
    }

    /// <summary>Show a prompt message; leading "Press E (to)" collapses into the keycap.</summary>
    public void ShowMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            Hide();
            return;
        }

        string label = message.Trim();
        bool keycap = false;
        const string prefixTo = "press e to ";
        const string prefix = "press e ";
        if (label.Length >= prefixTo.Length && label.StartsWith("Press E to ", System.StringComparison.OrdinalIgnoreCase))
        {
            label = label.Substring(prefixTo.Length);
            keycap = true;
        }
        else if (label.Length >= prefix.Length && label.StartsWith("Press E ", System.StringComparison.OrdinalIgnoreCase))
        {
            label = label.Substring(prefix.Length);
            keycap = true;
        }

        Apply(keycap, null, label.ToUpperInvariant());
    }

    /// <summary>Icon + value readout (e.g. elevator occupancy).</summary>
    public void ShowCount(Sprite icon, string text)
    {
        Apply(false, icon, text);
    }

    void Apply(bool keycap, Sprite icon, string text)
    {
        if (_keycap.activeSelf != keycap)
            _keycap.SetActive(keycap);
        bool hasIcon = icon != null;
        if (_icon.gameObject.activeSelf != hasIcon)
            _icon.gameObject.SetActive(hasIcon);
        if (hasIcon)
            _icon.sprite = icon;
        _label.text = text;
        _shown = true;
    }

    public void Hide()
    {
        _shown = false;
    }

    void Update()
    {
        float target = _shown ? 1f : 0f;
        float a = Mathf.MoveTowards(_group.alpha, target, Time.unscaledDeltaTime * 9f);
        if (!Mathf.Approximately(a, _group.alpha))
            _group.alpha = a;
    }
}
