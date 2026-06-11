using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Centered confirm dialog over a click-blocking scrim. Built once per canvas, reused.</summary>
[DisallowMultipleComponent]
public sealed class MenuModal : MonoBehaviour
{
    TextMeshProUGUI _title;
    TextMeshProUGUI _message;
    TextMeshProUGUI _confirmLabel;
    Image _confirmBg;
    MenuButtonFx _confirmFx;
    Action _onConfirm;
    CanvasGroup _group;
    bool _shown;

    public static MenuModal Create(Transform canvasRoot)
    {
        RectTransform root = MenuWidgets.CreateStretched("Modal", canvasRoot);
        var modal = root.gameObject.AddComponent<MenuModal>();
        modal.Build(root);
        return modal;
    }

    void Build(RectTransform root)
    {
        _group = root.gameObject.AddComponent<CanvasGroup>();

        Image scrim = MenuWidgets.CreateImage(root, "Scrim", MenuTheme.Solid(), MenuTheme.WithAlpha(MenuTheme.Ink, 0.78f), true);
        Stretch(scrim.rectTransform);

        RectTransform card = MenuWidgets.CreateRect("Card", root);
        card.sizeDelta = new Vector2(560f, 100f);

        Image shadow = MenuWidgets.CreateImage(card, "Shadow", MenuTheme.SoftGlow(), MenuTheme.WithAlpha(Color.black, 0.7f));
        RectTransform shadowRt = shadow.rectTransform;
        Stretch(shadowRt);
        shadowRt.offsetMin = new Vector2(-80f, -90f);
        shadowRt.offsetMax = new Vector2(80f, 70f);

        Image bg = MenuWidgets.CreateImage(card, "Bg", MenuTheme.RoundedRect(14), MenuTheme.PanelRaised, true);
        Stretch(bg.rectTransform);
        Image outline = MenuWidgets.CreateImage(card, "Outline", MenuTheme.RoundedOutline(14, 1.6f), MenuTheme.Stroke);
        Stretch(outline.rectTransform);

        RectTransform content = MenuWidgets.CreateStretched("Content", card);
        MenuWidgets.AddVertical(content.gameObject, new RectOffset(44, 44, 36, 36), 14f);
        var mirror = card.gameObject.AddComponent<CardHeightMirror>();
        mirror.content = content;

        _title = MenuWidgets.CreateText(content, "Title", "CONFIRM", 30f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 5f);
        MenuWidgets.SetLayout(_title, preferredHeight: 40f);

        _message = MenuWidgets.CreateText(content, "Message", string.Empty, 17.5f, MenuTheme.Mist,
            MenuWidgets.FontKind.Body, TextAlignmentOptions.TopLeft);
        _message.lineSpacing = 8f;

        MenuWidgets.CreateSpacer(content, 10f);

        RectTransform row = MenuWidgets.CreateRow(content, "Buttons", 52f, 14f);
        Button cancel = MenuWidgets.CreateGhostButton(row, "CANCEL", Close);
        MenuWidgets.SetLayout(cancel.transform, flexibleWidth: 1f, minHeight: 52f, preferredHeight: 52f);

        Button confirm = MenuWidgets.CreatePrimaryButton(row, "CONFIRM", () =>
        {
            Action action = _onConfirm;
            Close();
            action?.Invoke();
        }, 52f);
        MenuWidgets.SetLayout(confirm.transform, flexibleWidth: 1f, minHeight: 52f, preferredHeight: 52f);
        _confirmBg = confirm.targetGraphic as Image;
        _confirmFx = confirm.GetComponent<MenuButtonFx>();
        _confirmLabel = confirm.GetComponentInChildren<TextMeshProUGUI>();

        _group.alpha = 0f;
        _group.blocksRaycasts = false;
        _group.interactable = false;
        gameObject.SetActive(false);
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    public void Open(string title, string message, string confirmText, bool danger, Action onConfirm)
    {
        _title.text = title;
        _message.text = message;
        if (_confirmLabel != null)
            _confirmLabel.text = confirmText;
        _onConfirm = onConfirm;

        if (_confirmFx != null)
        {
            _confirmFx.backgroundNormal = danger ? MenuTheme.Blood : MenuTheme.Amber;
            _confirmFx.backgroundHover = danger ? MenuTheme.BloodBright : MenuTheme.AmberBright;
            _confirmFx.labelNormal = danger ? MenuTheme.Bone : new Color(0.10f, 0.08f, 0.04f, 1f);
            _confirmFx.labelHover = danger ? Color.white : new Color(0.05f, 0.04f, 0.02f, 1f);
        }
        if (_confirmBg != null)
            _confirmBg.color = danger ? MenuTheme.Blood : MenuTheme.Amber;

        transform.SetAsLastSibling();
        gameObject.SetActive(true);
        _shown = true;
        _group.blocksRaycasts = true;
        _group.interactable = true;
    }

    public void Close()
    {
        _shown = false;
        _group.blocksRaycasts = false;
        _group.interactable = false;
    }

    void Update()
    {
        float target = _shown ? 1f : 0f;
        _group.alpha = Mathf.MoveTowards(_group.alpha, target, Time.unscaledDeltaTime * 10f);
        if (!_shown && _group.alpha <= 0.001f)
            gameObject.SetActive(false);
    }
}
