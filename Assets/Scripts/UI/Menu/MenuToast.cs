using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Bottom-right status toast: fades in on Show(), lingers, fades out.</summary>
[DisallowMultipleComponent]
public sealed class MenuToast : MonoBehaviour
{
    const float HoldSeconds = 4f;

    CanvasGroup _group;
    TextMeshProUGUI _label;
    float _hideAt;

    public static MenuToast Create(Transform canvasRoot)
    {
        RectTransform rt = MenuWidgets.CreateRect("Toast", canvasRoot);
        rt.anchorMin = new Vector2(1f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = new Vector2(-56f, 48f);
        rt.sizeDelta = new Vector2(560f, 54f);

        var toast = rt.gameObject.AddComponent<MenuToast>();
        toast.Build(rt);
        return toast;
    }

    void Build(RectTransform rt)
    {
        _group = rt.gameObject.AddComponent<CanvasGroup>();
        _group.alpha = 0f;
        _group.blocksRaycasts = false;
        _group.interactable = false;

        Image bg = MenuWidgets.CreateImage(rt, "Bg", MenuTheme.RoundedRect(8), MenuTheme.WithAlpha(MenuTheme.PanelRaised, 0.96f));
        StretchFull(bg.rectTransform);
        Image outline = MenuWidgets.CreateImage(rt, "Outline", MenuTheme.RoundedOutline(8, 1.4f), MenuTheme.WithAlpha(MenuTheme.Stroke, 0.9f));
        StretchFull(outline.rectTransform);

        Image tick = MenuWidgets.CreateImage(rt, "Accent", MenuTheme.Solid(), MenuTheme.Amber);
        RectTransform tickRt = tick.rectTransform;
        tickRt.anchorMin = new Vector2(0f, 0.22f);
        tickRt.anchorMax = new Vector2(0f, 0.78f);
        tickRt.pivot = new Vector2(0f, 0.5f);
        tickRt.anchoredPosition = new Vector2(10f, 0f);
        tickRt.sizeDelta = new Vector2(3f, 0f);

        _label = MenuWidgets.CreateText(rt, "Label", string.Empty, 15.5f, MenuTheme.Bone,
            MenuWidgets.FontKind.Body, TextAlignmentOptions.MidlineLeft);
        RectTransform labelRt = _label.rectTransform;
        StretchFull(labelRt);
        labelRt.offsetMin = new Vector2(26f, 0f);
        labelRt.offsetMax = new Vector2(-18f, 0f);
        _label.textWrappingMode = TextWrappingModes.NoWrap;
        _label.overflowMode = TextOverflowModes.Ellipsis;
    }

    static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    public void Show(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        _label.text = message;
        _hideAt = Time.unscaledTime + HoldSeconds;
        transform.SetAsLastSibling();
    }

    void Update()
    {
        bool visible = Time.unscaledTime < _hideAt;
        float target = visible ? 1f : 0f;
        _group.alpha = Mathf.MoveTowards(_group.alpha, target, Time.unscaledDeltaTime * 7f);
    }
}
