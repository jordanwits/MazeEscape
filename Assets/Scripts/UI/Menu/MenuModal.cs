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

        Image scrim = MenuWidgets.CreateImage(root, "Scrim", MenuTheme.Solid(), MenuTheme.WithAlpha(MenuTheme.Ink, 0.82f), true);
        scrim.rectTransform.SetStretch();

        RectTransform card = MenuWidgets.CreateRect("Card", root);
        card.sizeDelta = new Vector2(520f, 100f);

        Image shadow = MenuWidgets.CreateImage(card, "Shadow", MenuTheme.RoundedRect(3), MenuTheme.WithAlpha(MenuTheme.Ink, 0.7f));
        shadow.rectTransform.SetStretch();
        shadow.rectTransform.offsetMin = new Vector2(9f, -12f);
        shadow.rectTransform.offsetMax = new Vector2(9f, -12f);

        Image bg = MenuWidgets.CreateImage(card, "Bg", MenuTheme.RoundedRect(3), MenuTheme.PanelRaised, true);
        bg.rectTransform.SetStretch();
        MenuWidgets.CreateGrunge(card, MenuTheme.WithAlpha(Color.white, 0.05f));
        Image outline = MenuWidgets.CreateImage(card, "Outline", MenuTheme.RoundedOutline(3, 1.6f), MenuTheme.WithAlpha(MenuTheme.Bone, 0.22f));
        outline.rectTransform.SetStretch();
        MenuWidgets.CreateCornerBrackets(card, MenuTheme.WithAlpha(MenuTheme.Bone, 0.6f));

        RectTransform content = MenuWidgets.CreateStretched("Content", card);
        MenuWidgets.AddVertical(content.gameObject, new RectOffset(44, 44, 36, 36), 14f);
        var mirror = card.gameObject.AddComponent<CardHeightMirror>();
        mirror.content = content;

        _title = MenuWidgets.CreateText(content, "Title", "CONFIRM", 30f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 5f);
        MenuWidgets.SetLayout(_title, preferredHeight: 40f);

        _message = MenuWidgets.CreateText(content, "Message", string.Empty, 16.5f, MenuTheme.Mist,
            MenuWidgets.FontKind.Body, TextAlignmentOptions.TopLeft);
        _message.lineSpacing = 8f;

        MenuWidgets.CreateSpacer(content, 8f);

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
        _confirmFx = confirm.GetComponent<MenuButtonFx>();
        _confirmLabel = confirm.GetComponentInChildren<TextMeshProUGUI>();

        _group.alpha = 0f;
        _group.blocksRaycasts = false;
        _group.interactable = false;
        gameObject.SetActive(false);
    }

    public void Open(string title, string message, string confirmText, bool danger, Action onConfirm)
    {
        _title.text = title;
        bool hasMessage = !string.IsNullOrWhiteSpace(message);
        _message.text = hasMessage ? message : string.Empty;
        if (_message.gameObject.activeSelf != hasMessage)
            _message.gameObject.SetActive(hasMessage);
        if (_confirmLabel != null)
            _confirmLabel.text = confirmText;
        _onConfirm = onConfirm;

        if (_confirmFx != null)
        {
            if (danger)
            {
                _confirmFx.fillNormal = MenuTheme.Blood;
                _confirmFx.fillHover = MenuTheme.BloodBright;
                _confirmFx.frameNormal = MenuTheme.WithAlpha(new Color(0.32f, 0.10f, 0.08f, 1f), 0.95f);
                _confirmFx.frameHover = MenuTheme.WithAlpha(new Color(0.32f, 0.10f, 0.08f, 1f), 0.95f);
                _confirmFx.labelNormal = MenuTheme.Bone;
                _confirmFx.labelHover = Color.white;
                _confirmFx.ledgeColor = MenuTheme.WithAlpha(MenuTheme.Bone, 0.7f);
            }
            else
            {
                MenuWidgets.ApplyPlateStyle(_confirmFx, MenuWidgets.PlateStyle.Primary);
            }
        }

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
