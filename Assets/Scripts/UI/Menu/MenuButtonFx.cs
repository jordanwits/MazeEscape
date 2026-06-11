using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Hover/press presentation for menu buttons: color shifts, sliding accent bar,
/// subtle indent and press dip. All motion uses unscaled time.
/// </summary>
[DisallowMultipleComponent]
public sealed class MenuButtonFx : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
{
    public Button button;
    public TMP_Text label;
    public Image background;
    public Image outline;
    public RectTransform accentBar;

    public Color labelNormal = Color.white;
    public Color labelHover = Color.white;
    public Color backgroundNormal = Color.clear;
    public Color backgroundHover = Color.clear;
    public Color outlineNormal = Color.clear;
    public Color outlineHover = Color.clear;
    public float hoverShift;

    CanvasGroup _group;
    RectTransform _labelRect;
    Vector2 _labelBasePosition;
    float _hover;
    float _press;
    bool _pointerInside;
    bool _pointerDown;

    void Awake()
    {
        // hover shift is applied to the label, not the root: the root is usually owned by a
        // layout group and writing its anchoredPosition would fight the layout system.
        if (label != null)
        {
            _labelRect = label.rectTransform;
            _labelBasePosition = _labelRect.anchoredPosition;
        }
        _group = GetComponent<CanvasGroup>();
        if (_group == null)
            _group = gameObject.AddComponent<CanvasGroup>();
        if (button == null)
            button = GetComponent<Button>();
        ApplyVisuals();
    }

    void OnEnable()
    {
        _pointerInside = false;
        _pointerDown = false;
        _hover = 0f;
        _press = 0f;
        ApplyVisuals();
    }

    bool Interactable => button == null || button.interactable;

    public void OnPointerEnter(PointerEventData eventData)
    {
        _pointerInside = true;
        if (Interactable)
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

        ApplyVisuals();
    }

    void ApplyVisuals()
    {
        if (label != null)
            label.color = Color.Lerp(labelNormal, labelHover, _hover);
        if (background != null)
            background.color = Color.Lerp(backgroundNormal, backgroundHover, _hover);
        if (outline != null)
            outline.color = Color.Lerp(outlineNormal, outlineHover, _hover);
        if (accentBar != null)
        {
            Vector3 s = accentBar.localScale;
            s.y = Mathf.Max(0.0001f, _hover);
            accentBar.localScale = s;
        }

        if (_labelRect != null)
            _labelRect.anchoredPosition = _labelBasePosition + new Vector2(hoverShift * _hover, 0f);

        float scale = 1f - 0.015f * _press;
        transform.localScale = new Vector3(scale, scale, 1f);

        if (_group != null)
            _group.alpha = Interactable ? 1f : 0.4f;
    }
}
