using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// "ACHIEVEMENT UNLOCKED" card in the top-right corner, for when Steam's overlay will not show one (no Steam, the
/// Editor). Its own DontDestroyOnLoad overlay canvas above everything else, so it survives the section load that
/// usually follows an unlock and still shows over the game-over card, which hides the gameplay HUD canvas.
/// Unlocks that arrive together queue and play one after another.
/// </summary>
[DisallowMultipleComponent]
public sealed class AchievementToast : MonoBehaviour
{
    const float FadeSeconds = 0.25f;
    const float HoldSeconds = 4f;

    static AchievementToast s_Instance;

    readonly Queue<(string title, string description)> _queue = new();
    CanvasGroup _group;
    TextMeshProUGUI _title;
    TextMeshProUGUI _description;
    float _timer;
    bool _showing;

    public static void Show(string title, string description)
    {
        if (s_Instance == null)
            s_Instance = Create();

        s_Instance._queue.Enqueue((title, description));
    }

    static AchievementToast Create()
    {
        var root = new GameObject("AchievementToastCanvas");
        root.layer = 5;
        DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 900;
        canvas.vertexColorAlwaysGammaSpace = true;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        var card = new GameObject("AchievementToast", typeof(RectTransform));
        card.layer = 5;
        var rt = (RectTransform)card.transform;
        rt.SetParent(root.transform, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-32f, -32f);
        rt.localRotation = Quaternion.Euler(0f, 0f, 0.4f);

        var toast = card.AddComponent<AchievementToast>();
        toast.Build(card);
        return toast;
    }

    void Build(GameObject card)
    {
        _group = card.AddComponent<CanvasGroup>();
        _group.alpha = 0f;
        _group.blocksRaycasts = false;
        _group.interactable = false;

        Image frame = HudKit.AddPlate(card, 0.86f, 0.30f);
        frame.color = MenuTheme.WithAlpha(MenuTheme.Amber, 0.55f);

        var layout = card.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(18, 22, 12, 14);
        layout.spacing = 3f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        var fitter = card.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        TextMeshProUGUI header = AddLabel("Header", MenuTheme.DisplayFont, 13f, MenuTheme.Amber, 4f);
        header.text = "ACHIEVEMENT UNLOCKED";
        _title = AddLabel("Title", MenuTheme.DisplayFont, 22f, MenuTheme.Bone, 2f);
        _description = AddLabel("Description", MenuTheme.BodyFont, 15f, MenuTheme.Mist, 0f);
    }

    TextMeshProUGUI AddLabel(string name, TMP_FontAsset font, float size, Color color, float spacing)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = 5;
        ((RectTransform)go.transform).SetParent(transform, false);
        var label = go.AddComponent<TextMeshProUGUI>();
        label.font = font;
        label.fontSize = size;
        label.color = color;
        label.characterSpacing = spacing;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.raycastTarget = false;
        return label;
    }

    void Update()
    {
        if (!_showing)
        {
            if (_queue.Count == 0)
                return;

            (string title, string description) = _queue.Dequeue();
            _title.text = title.ToUpperInvariant();
            _description.text = description;
            _timer = 0f;
            _showing = true;
        }

        // Unscaled: an unlock can land while the pause menu has time stopped.
        _timer += Time.unscaledDeltaTime;
        float total = FadeSeconds * 2f + HoldSeconds;
        if (_timer < FadeSeconds)
            _group.alpha = _timer / FadeSeconds;
        else if (_timer < FadeSeconds + HoldSeconds)
            _group.alpha = 1f;
        else if (_timer < total)
            _group.alpha = 1f - (_timer - FadeSeconds - HoldSeconds) / FadeSeconds;
        else
        {
            _group.alpha = 0f;
            _showing = false;
        }
    }
}
