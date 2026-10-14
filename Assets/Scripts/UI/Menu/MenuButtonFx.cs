using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Plate-button presentation: dark plate at rest, flips light with dark type on hover,
/// mustard with an under-ledge when selected. Handles press dip, disabled dimming and
/// hover audio. All motion uses unscaled time.
///
/// "Hover" is the pointer over the plate OR the pad/keyboard selection on it — the same look either way. The
/// mouse never leaves a selection behind (<see cref="UiEventSystem"/> drops it), so mouse play is unchanged.
/// The selection's hover tick is played by <see cref="UiEventSystem"/>, which knows a move from a click.
/// </summary>
[DisallowMultipleComponent]
public sealed class MenuButtonFx : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler,
    ISelectHandler, IDeselectHandler
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

    /// <summary>The tilted plate under this button; when set, hover straightens and lifts it like a nudged sign.</summary>
    public RectTransform plate;
    public float plateBaseTilt;

    CanvasGroup _group;
    float _hover;
    float _press;
    float _select;
    bool _pointerInside;
    bool _pointerDown;
    /// <summary>The EventSystem's selection is on this plate (pad / keyboard focus) — not the mustard <see cref="Selected"/> state.</summary>
    bool _focused;
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
        // Disabling skips OnDeselect, and the EventSystem may still hold this plate, so re-derive like Selectable does.
        EventSystem eventSystem = EventSystem.current;
        _focused = eventSystem != null && eventSystem.currentSelectedGameObject == gameObject;
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

    public void OnSelect(BaseEventData eventData)
    {
        _focused = true;
    }

    public void OnDeselect(BaseEventData eventData)
    {
        _focused = false;
    }

    bool Hovered => (_pointerInside || _focused) && Interactable;

    void Update()
    {
        float k = 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
        bool hovered = Hovered;
        _hover = Mathf.Lerp(_hover, hovered ? 1f : 0f, k);
        _press = Mathf.Lerp(_press, _pointerDown && Interactable ? 1f : 0f, k);
        _select = Mathf.Lerp(_select, _selected ? 1f : 0f, k);

        ApplyVisuals(false);
    }

    void ApplyVisuals(bool instant)
    {
        if (instant)
        {
            _hover = Hovered ? 1f : 0f;
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

        // hover: the hand-placed sign straightens and leans forward a touch
        if (plate != null)
            plate.localRotation = Quaternion.Euler(0f, 0f, plateBaseTilt * (1f - 0.55f * _hover));

        float scale = 1f + 0.022f * _hover - 0.03f * _press;
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
