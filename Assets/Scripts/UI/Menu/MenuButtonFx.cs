using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Plate-button presentation: dark plate at rest, flips light with dark type on hover,
/// mustard with an under-ledge when selected. Handles press dip, disabled dimming and
/// hover audio. All motion uses unscaled time.
/// </summary>
[DisallowMultipleComponent]
public sealed class MenuButtonFx : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
{
    public Button button;
    public TMP_Text label;

    /// <summary>When true, hover no longer plays <see cref="MenuUiAudio.PlayHover"/> (e.g. blackjack overlay buttons).</summary>
    public bool suppressHoverAudio;
    public Image fill;
    public Image frame;
    public RawImage grunge;
    public RectTransform ledge;
    public Image ledgeImage;

    public Color fillNormal = Color.clear;
    public Color fillHover = Color.clear;
    public Color fillSelected = Color.clear;
    public Color frameNormal = Color.clear;
    public Color frameHover = Color.clear;
    public Color frameSelected = Color.clear;
    public Color labelNormal = Color.white;
    public Color labelHover = Color.white;
    public Color labelSelected = Color.white;
    public Color grungeNormal = Color.clear;
    public Color grungeHover = Color.clear;
    public Color grungeSelected = Color.clear;
    public Color ledgeColor = Color.white;

    /// <summary>Ledge shows permanently (primary CTAs) instead of only while selected.</summary>
    public bool ledgeAlways;

    CanvasGroup _group;
    float _hover;
    float _press;
    float _select;
    bool _pointerInside;
    bool _pointerDown;
    bool _selected;

    public bool Selected => _selected;

    public void SetSelected(bool selected)
    {
        _selected = selected;
    }

    void Awake()
    {
        _group = GetComponent<CanvasGroup>();
        if (_group == null)
            _group = gameObject.AddComponent<CanvasGroup>();
        if (button == null)
            button = GetComponent<Button>();
        ApplyVisuals(true);
    }

    void OnEnable()
    {
        _pointerInside = false;
        _pointerDown = false;
        _hover = 0f;
        _press = 0f;
        _select = _selected ? 1f : 0f;
        ApplyVisuals(true);
    }

    bool Interactable => button == null || button.interactable;

    public void OnPointerEnter(PointerEventData eventData)
    {
        _pointerInside = true;
        if (Interactable && !suppressHoverAudio)
            MenuUiAudio.PlayHover();
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        _pointerInside = false;
        _pointerDown = false;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (eventData.button == PointerEventData.InputButton.Left)
            _pointerDown = true;
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        _pointerDown = false;
    }

    void Update()
    {
        float k = 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
        bool hovered = _pointerInside && Interactable;
        _hover = Mathf.Lerp(_hover, hovered ? 1f : 0f, k);
        _press = Mathf.Lerp(_press, _pointerDown && Interactable ? 1f : 0f, k);
        _select = Mathf.Lerp(_select, _selected ? 1f : 0f, k);

        ApplyVisuals(false);
    }

    void ApplyVisuals(bool instant)
    {
        if (instant)
        {
            _hover = _pointerInside && Interactable ? 1f : 0f;
            _select = _selected ? 1f : 0f;
        }

        // selected overrides the hover flip; hover still nudges the selected plate a touch
        if (fill != null)
            fill.color = Blend(fillNormal, fillHover, fillSelected);
        if (frame != null)
            frame.color = Blend(frameNormal, frameHover, frameSelected);
        if (label != null)
            label.color = Blend(labelNormal, labelHover, labelSelected);
        if (grunge != null)
            grunge.color = Blend(grungeNormal, grungeHover, grungeSelected);

        if (ledge != null)
        {
            float shown = ledgeAlways ? 1f : Mathf.Max(_select, 0f);
            Vector3 s = ledge.localScale;
            s.x = Mathf.Max(0.0001f, shown);
            ledge.localScale = s;
            if (ledgeImage != null)
                ledgeImage.color = MenuTheme.WithAlpha(ledgeColor, ledgeColor.a * shown);
        }

        float scale = 1f - 0.015f * _press;
        transform.localScale = new Vector3(scale, scale, 1f);

        if (_group != null)
            _group.alpha = Interactable ? 1f : 0.35f;
    }

    Color Blend(Color normal, Color hover, Color selected)
    {
        Color c = Color.Lerp(normal, hover, _hover);
        return Color.Lerp(c, selected, _select);
    }
}
