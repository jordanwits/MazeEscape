using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Runtime factory for every widget in the menu system (canvases, text, buttons, sliders,
/// inputs, cards, rows). All widgets are styled from <see cref="MenuTheme"/> so the whole
/// UI shares one design language without any scene-authored assets.
/// </summary>
public static class MenuWidgets
{
    public enum FontKind { Body, Display }

    // ---------------------------------------------------------------- structure

    public static Canvas CreateCanvas(string name, int sortingOrder, Transform parent = null)
    {
        var go = new GameObject(name);
        go.layer = 5;
        if (parent != null)
            go.transform.SetParent(parent, false);

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        canvas.vertexColorAlwaysGammaSpace = true;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        go.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    public static RectTransform CreateRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = 5;
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }

    public static RectTransform CreateStretched(string name, Transform parent)
    {
        RectTransform rt = CreateRect(name, parent);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        return rt;
    }

    public static Image CreateImage(Transform parent, string name, Sprite sprite, Color color, bool raycastTarget = false)
    {
        RectTransform rt = CreateRect(name, parent);
        var image = rt.gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.type = sprite != null && sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
        image.color = color;
        image.raycastTarget = raycastTarget;
        return image;
    }

    public static RawImage CreateRawImage(Transform parent, string name, Texture texture, Color color)
    {
        RectTransform rt = CreateRect(name, parent);
        var image = rt.gameObject.AddComponent<RawImage>();
        image.texture = texture;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    public static TextMeshProUGUI CreateText(
        Transform parent, string name, string text, float size, Color color,
        FontKind font = FontKind.Body, TextAlignmentOptions alignment = TextAlignmentOptions.Left,
        float characterSpacing = 0f, FontStyles style = FontStyles.Normal)
    {
        RectTransform rt = CreateRect(name, parent);
        var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        tmp.font = font == FontKind.Display ? MenuTheme.DisplayFont : MenuTheme.BodyFont;
        tmp.text = text;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = alignment;
        tmp.characterSpacing = characterSpacing;
        tmp.fontStyle = style;
        tmp.raycastTarget = false;
        tmp.textWrappingMode = TextWrappingModes.Normal;
        return tmp;
    }

    // ---------------------------------------------------------------- layout helpers

    public static VerticalLayoutGroup AddVertical(
        GameObject go, RectOffset padding, float spacing,
        TextAnchor alignment = TextAnchor.UpperLeft, bool controlHeight = true)
    {
        var layout = go.AddComponent<VerticalLayoutGroup>();
        layout.padding = padding;
        layout.spacing = spacing;
        layout.childAlignment = alignment;
        layout.childControlWidth = true;
        layout.childControlHeight = controlHeight;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return layout;
    }

    public static RectTransform CreateRow(Transform parent, string name, float height, float spacing,
        TextAnchor alignment = TextAnchor.MiddleLeft)
    {
        RectTransform rt = CreateRect(name, parent);
        var layout = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = spacing;
        layout.childAlignment = alignment;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = height;
        le.minHeight = height;
        return rt;
    }

    public static LayoutElement SetLayout(Component c, float minWidth = -1f, float preferredWidth = -1f,
        float flexibleWidth = -1f, float minHeight = -1f, float preferredHeight = -1f)
    {
        var le = c.gameObject.GetComponent<LayoutElement>();
        if (le == null)
            le = c.gameObject.AddComponent<LayoutElement>();
        le.minWidth = minWidth;
        le.preferredWidth = preferredWidth;
        le.flexibleWidth = flexibleWidth;
        le.minHeight = minHeight;
        le.preferredHeight = preferredHeight;
        return le;
    }

    public static void CreateSpacer(Transform parent, float height)
    {
        RectTransform rt = CreateRect("Spacer", parent);
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.minHeight = height;
        le.preferredHeight = height;
    }

    public static void CreateFlexibleSpacer(Transform parent)
    {
        RectTransform rt = CreateRect("Flex", parent);
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.flexibleHeight = 1f;
        le.flexibleWidth = 1f;
    }

    public static Image CreateHairline(Transform parent, Color? color = null)
    {
        Image line = CreateImage(parent, "Hairline", MenuTheme.Solid(), color ?? MenuTheme.WithAlpha(MenuTheme.Stroke, 0.9f));
        var le = line.gameObject.AddComponent<LayoutElement>();
        le.minHeight = 1f;
        le.preferredHeight = 1f;
        return line;
    }

    /// <summary>Spaced-caps section header with hairline underneath.</summary>
    public static void CreateSection(Transform parent, string title)
    {
        CreateSpacer(parent, 10f);
        TextMeshProUGUI label = CreateText(parent, "Section_" + title, title, 15f,
            MenuTheme.WithAlpha(MenuTheme.Mist, 0.95f), FontKind.Body, TextAlignmentOptions.Left, 10f);
        var le = label.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = 24f;
        CreateHairline(parent);
        CreateSpacer(parent, 4f);
    }

    /// <summary>Panel card with soft drop shadow, rounded fill, hairline outline, vertical layout.</summary>
    public static RectTransform CreateCard(Transform parent, string name, float width)
    {
        RectTransform rt = CreateRect(name, parent);
        rt.sizeDelta = new Vector2(width, 100f);

        Image shadow = CreateImage(rt, "Shadow", MenuTheme.SoftGlow(), MenuTheme.WithAlpha(Color.black, 0.55f));
        RectTransform shadowRt = shadow.rectTransform;
        shadowRt.anchorMin = Vector2.zero;
        shadowRt.anchorMax = Vector2.one;
        shadowRt.offsetMin = new Vector2(-70f, -78f);
        shadowRt.offsetMax = new Vector2(70f, 62f);

        Image bg = CreateImage(rt, "Bg", MenuTheme.RoundedRect(14), MenuTheme.WithAlpha(MenuTheme.Panel, 0.97f), true);
        RectTransform bgRt = bg.rectTransform;
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;

        Image outline = CreateImage(rt, "Outline", MenuTheme.RoundedOutline(14, 1.6f), MenuTheme.WithAlpha(MenuTheme.Stroke, 0.95f));
        RectTransform outlineRt = outline.rectTransform;
        outlineRt.anchorMin = Vector2.zero;
        outlineRt.anchorMax = Vector2.one;
        outlineRt.offsetMin = Vector2.zero;
        outlineRt.offsetMax = Vector2.zero;

        RectTransform content = CreateStretched("Content", rt);
        AddVertical(content.gameObject, new RectOffset(46, 46, 38, 40), 12f);

        // The card's height should follow its content: mirror the content's preferred height.
        var mirror = rt.gameObject.AddComponent<CardHeightMirror>();
        mirror.content = content;
        return content;
    }

    // ---------------------------------------------------------------- buttons

    public static Button CreateNavButton(Transform parent, string text, Action onClick, out MenuButtonFx fx)
    {
        RectTransform rt = CreateRect("Nav_" + text, parent);
        SetLayout(rt, minHeight: 58f, preferredHeight: 58f);

        Image bg = CreateImageStretched(rt, "Bg", MenuTheme.RoundedRect(6), Color.clear, true);

        Image bar = CreateImage(rt, "AccentBar", MenuTheme.Solid(), MenuTheme.Amber);
        RectTransform barRt = bar.rectTransform;
        barRt.anchorMin = new Vector2(0f, 0.2f);
        barRt.anchorMax = new Vector2(0f, 0.8f);
        barRt.pivot = new Vector2(0f, 0.5f);
        barRt.anchoredPosition = new Vector2(2f, 0f);
        barRt.sizeDelta = new Vector2(3f, 0f);

        TextMeshProUGUI label = CreateText(rt, "Label", text, 27f, MenuTheme.Mist,
            FontKind.Display, TextAlignmentOptions.Left, 7f);
        RectTransform labelRt = label.rectTransform;
        labelRt.anchorMin = Vector2.zero;
        labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = new Vector2(30f, 0f);
        labelRt.offsetMax = Vector2.zero;
        label.alignment = TextAlignmentOptions.MidlineLeft;

        Button button = AddButton(rt.gameObject, bg, onClick);

        fx = rt.gameObject.AddComponent<MenuButtonFx>();
        fx.button = button;
        fx.label = label;
        fx.background = bg;
        fx.accentBar = barRt;
        fx.labelNormal = MenuTheme.Mist;
        fx.labelHover = MenuTheme.Bone;
        fx.backgroundNormal = Color.clear;
        fx.backgroundHover = MenuTheme.WithAlpha(Color.white, 0.045f);
        fx.hoverShift = 10f;
        return button;
    }

    public static Button CreatePrimaryButton(Transform parent, string text, Action onClick, float height = 56f)
    {
        RectTransform rt = CreateRect("Primary_" + text, parent);
        SetLayout(rt, minHeight: height, preferredHeight: height, flexibleWidth: 1f);

        Image bg = CreateImageStretched(rt, "Bg", MenuTheme.RoundedRect(8), MenuTheme.Amber, true);
        TextMeshProUGUI label = CreateText(rt, "Label", text, 21f, new Color(0.10f, 0.08f, 0.04f, 1f),
            FontKind.Display, TextAlignmentOptions.Center, 5f);
        StretchText(label);

        Button button = AddButton(rt.gameObject, bg, onClick);
        var fx = rt.gameObject.AddComponent<MenuButtonFx>();
        fx.button = button;
        fx.label = label;
        fx.background = bg;
        fx.labelNormal = new Color(0.10f, 0.08f, 0.04f, 1f);
        fx.labelHover = new Color(0.05f, 0.04f, 0.02f, 1f);
        fx.backgroundNormal = MenuTheme.Amber;
        fx.backgroundHover = MenuTheme.AmberBright;
        return button;
    }

    public static Button CreateGhostButton(Transform parent, string text, Action onClick,
        bool danger = false, float height = 52f, float fontSize = 19f)
    {
        RectTransform rt = CreateRect("Ghost_" + text, parent);
        SetLayout(rt, minHeight: height, preferredHeight: height, flexibleWidth: 1f);

        Image bg = CreateImageStretched(rt, "Bg", MenuTheme.RoundedRect(8), MenuTheme.WithAlpha(Color.white, 0.02f), true);
        Image outline = CreateImageStretched(rt, "Outline", MenuTheme.RoundedOutline(8, 1.6f), MenuTheme.Stroke);
        TextMeshProUGUI label = CreateText(rt, "Label", text, fontSize,
            danger ? MenuTheme.Mist : MenuTheme.Bone, FontKind.Display, TextAlignmentOptions.Center, 4f);
        StretchText(label);

        Button button = AddButton(rt.gameObject, bg, onClick);
        var fx = rt.gameObject.AddComponent<MenuButtonFx>();
        fx.button = button;
        fx.label = label;
        fx.background = bg;
        fx.outline = outline;
        fx.labelNormal = danger ? MenuTheme.Mist : MenuTheme.Bone;
        fx.labelHover = danger ? MenuTheme.BloodBright : Color.white;
        fx.backgroundNormal = MenuTheme.WithAlpha(Color.white, 0.02f);
        fx.backgroundHover = danger
            ? MenuTheme.WithAlpha(MenuTheme.Blood, 0.10f)
            : MenuTheme.WithAlpha(MenuTheme.Amber, 0.08f);
        fx.outlineNormal = MenuTheme.Stroke;
        fx.outlineHover = danger ? MenuTheme.Blood : MenuTheme.Amber;
        return button;
    }

    /// <summary>Small inline button (e.g. COPY chips next to IDs).</summary>
    public static Button CreateMiniButton(Transform parent, string text, Action onClick, float width = 92f)
    {
        Button button = CreateGhostButton(parent, text, onClick, false, 34f, 13.5f);
        SetLayout(button.transform, minWidth: width, preferredWidth: width, minHeight: 34f, preferredHeight: 34f);
        return button;
    }

    static Button AddButton(GameObject go, Graphic target, Action onClick)
    {
        var button = go.AddComponent<Button>();
        button.targetGraphic = target;
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() =>
        {
            MenuUiAudio.PlayClick();
            onClick?.Invoke();
        });
        return button;
    }

    static Image CreateImageStretched(Transform parent, string name, Sprite sprite, Color color, bool raycast = false)
    {
        Image image = CreateImage(parent, name, sprite, color, raycast);
        RectTransform rt = image.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        return image;
    }

    static void StretchText(TextMeshProUGUI label)
    {
        RectTransform rt = label.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    // ---------------------------------------------------------------- sliders

    public struct LabeledSlider
    {
        public Slider Slider;
        public TextMeshProUGUI ValueLabel;
    }

    public static LabeledSlider CreateLabeledSlider(Transform parent, string label)
    {
        RectTransform row = CreateRow(parent, "SliderRow_" + label, 40f, 18f);

        TextMeshProUGUI name = CreateText(row, "Name", label, 17f, MenuTheme.Bone,
            FontKind.Body, TextAlignmentOptions.MidlineLeft, 2.5f);
        SetLayout(name, minWidth: 220f, preferredWidth: 220f);

        Slider slider = CreateSlider(row);
        SetLayout(slider, flexibleWidth: 1f, minHeight: 36f, preferredHeight: 36f);

        TextMeshProUGUI value = CreateText(row, "Value", "100%", 16f, MenuTheme.Mist,
            FontKind.Body, TextAlignmentOptions.MidlineRight);
        SetLayout(value, minWidth: 64f, preferredWidth: 64f);

        return new LabeledSlider { Slider = slider, ValueLabel = value };
    }

    public static Slider CreateSlider(Transform parent)
    {
        RectTransform rt = CreateRect("Slider", parent);

        Image track = CreateImage(rt, "Track", MenuTheme.RoundedRect(4), new Color(0.10f, 0.11f, 0.15f, 1f), true);
        RectTransform trackRt = track.rectTransform;
        trackRt.anchorMin = new Vector2(0f, 0.5f);
        trackRt.anchorMax = new Vector2(1f, 0.5f);
        trackRt.offsetMin = new Vector2(0f, -3.5f);
        trackRt.offsetMax = new Vector2(0f, 3.5f);

        RectTransform fillArea = CreateRect("Fill Area", rt);
        fillArea.anchorMin = new Vector2(0f, 0.5f);
        fillArea.anchorMax = new Vector2(1f, 0.5f);
        fillArea.offsetMin = new Vector2(0f, -3.5f);
        fillArea.offsetMax = new Vector2(-8f, 3.5f);

        Image fill = CreateImage(fillArea, "Fill", MenuTheme.RoundedRect(4), MenuTheme.Amber);
        RectTransform fillRt = fill.rectTransform;
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = Vector2.one;
        fillRt.offsetMin = Vector2.zero;
        fillRt.offsetMax = new Vector2(8f, 0f);

        RectTransform handleArea = CreateRect("Handle Slide Area", rt);
        handleArea.anchorMin = new Vector2(0f, 0.5f);
        handleArea.anchorMax = new Vector2(1f, 0.5f);
        handleArea.offsetMin = new Vector2(8f, 0f);
        handleArea.offsetMax = new Vector2(-8f, 0f);

        Image handle = CreateImage(handleArea, "Handle", MenuTheme.Circle(), MenuTheme.Bone, true);
        RectTransform handleRt = handle.rectTransform;
        handleRt.sizeDelta = new Vector2(18f, 18f);

        var slider = rt.gameObject.AddComponent<Slider>();
        slider.fillRect = fillRt;
        slider.handleRect = handleRt;
        slider.targetGraphic = handle;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.navigation = new Navigation { mode = Navigation.Mode.None };
        slider.transition = Selectable.Transition.ColorTint;
        var colors = slider.colors;
        colors.normalColor = MenuTheme.Bone;
        colors.highlightedColor = MenuTheme.AmberBright;
        colors.pressedColor = MenuTheme.Amber;
        colors.selectedColor = MenuTheme.Bone;
        colors.disabledColor = MenuTheme.Faint;
        slider.colors = colors;
        return slider;
    }

    // ---------------------------------------------------------------- inputs

    public static TMP_InputField CreateInputField(Transform parent, string name, string placeholder,
        float width, TMP_InputField.ContentType contentType = TMP_InputField.ContentType.Standard)
    {
        RectTransform rt = CreateRect("Input_" + name, parent);
        SetLayout(rt, minWidth: width, preferredWidth: width, minHeight: 48f, preferredHeight: 48f);

        Image bg = CreateImageStretched(rt, "Bg", MenuTheme.RoundedRect(6), MenuTheme.WithAlpha(MenuTheme.PanelRaised, 0.9f), true);
        CreateImageStretched(rt, "Outline", MenuTheme.RoundedOutline(6, 1.4f), MenuTheme.WithAlpha(MenuTheme.Stroke, 0.9f));

        RectTransform viewport = CreateStretched("Text Area", rt);
        viewport.offsetMin = new Vector2(16f, 6f);
        viewport.offsetMax = new Vector2(-16f, -7f);
        viewport.gameObject.AddComponent<RectMask2D>();

        TextMeshProUGUI placeholderText = CreateText(viewport, "Placeholder", placeholder, 18f,
            MenuTheme.WithAlpha(MenuTheme.Faint, 0.9f), FontKind.Body, TextAlignmentOptions.MidlineLeft, 1f, FontStyles.Italic);
        StretchText(placeholderText);

        TextMeshProUGUI text = CreateText(viewport, "Text", string.Empty, 18f, MenuTheme.Bone,
            FontKind.Body, TextAlignmentOptions.MidlineLeft, 1f);
        StretchText(text);
        text.textWrappingMode = TextWrappingModes.NoWrap;

        var input = rt.gameObject.AddComponent<TMP_InputField>();
        input.targetGraphic = bg;
        input.textViewport = viewport;
        input.textComponent = text;
        input.placeholder = placeholderText;
        input.contentType = contentType;
        input.customCaretColor = true;
        input.caretColor = MenuTheme.AmberBright;
        input.caretWidth = 2;
        input.selectionColor = MenuTheme.WithAlpha(MenuTheme.Amber, 0.35f);
        input.navigation = new Navigation { mode = Navigation.Mode.None };
        input.transition = Selectable.Transition.None;
        return input;
    }

    // ---------------------------------------------------------------- segmented control

    public static MenuSegmented CreateSegmented(Transform parent, string[] options, float height = 44f)
    {
        RectTransform row = CreateRow(parent, "Segmented", height, 10f);
        var segmented = row.gameObject.AddComponent<MenuSegmented>();
        segmented.Build(options, height);
        return segmented;
    }

    // ---------------------------------------------------------------- scroll view

    /// <summary>
    /// Vertical <see cref="ScrollRect"/> whose height hugs its content up to <paramref name="maxHeight"/>,
    /// then scrolls (mouse wheel, drag, or the slim right-edge scrollbar). Returns the content transform to
    /// build widgets into. Keeps tall settings panels from overflowing the card/screen.
    /// </summary>
    public static RectTransform CreateScrollView(Transform parent, float maxHeight)
    {
        RectTransform root = CreateRect("ScrollView", parent);
        var rootLayout = root.gameObject.AddComponent<LayoutElement>();
        rootLayout.preferredHeight = maxHeight;
        rootLayout.flexibleWidth = 1f;
        rootLayout.flexibleHeight = 0f;

        var scroll = root.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 26f;
        scroll.inertia = false;

        RectTransform viewport = CreateStretched("Viewport", root);
        viewport.offsetMax = new Vector2(-10f, 0f); // leave room for the scrollbar
        viewport.gameObject.AddComponent<RectMask2D>();
        // Transparent raycast target so the wheel scrolls even over the gaps between widgets.
        var catcher = viewport.gameObject.AddComponent<Image>();
        catcher.color = Color.clear;
        catcher.raycastTarget = true;

        RectTransform content = CreateRect("Content", viewport);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.offsetMin = Vector2.zero;
        content.offsetMax = Vector2.zero;
        AddVertical(content.gameObject, new RectOffset(0, 6, 0, 0), 10f);
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.viewport = viewport;
        scroll.content = content;

        RectTransform barRt = CreateRect("Scrollbar", root);
        barRt.anchorMin = new Vector2(1f, 0f);
        barRt.anchorMax = new Vector2(1f, 1f);
        barRt.pivot = new Vector2(1f, 0.5f);
        barRt.sizeDelta = new Vector2(5f, 0f);
        barRt.anchoredPosition = Vector2.zero;
        CreateImageStretched(barRt, "Track", MenuTheme.RoundedRect(3), MenuTheme.WithAlpha(Color.white, 0.05f));
        var scrollbar = barRt.gameObject.AddComponent<Scrollbar>();
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        scrollbar.transition = Selectable.Transition.None;
        scrollbar.navigation = new Navigation { mode = Navigation.Mode.None };

        RectTransform slideArea = CreateStretched("Sliding Area", barRt);
        RectTransform handle = CreateStretched("Handle", slideArea);
        Image handleImg = handle.gameObject.AddComponent<Image>();
        handleImg.sprite = MenuTheme.RoundedRect(3);
        handleImg.type = Image.Type.Sliced;
        handleImg.color = MenuTheme.WithAlpha(MenuTheme.Bone, 0.5f);
        scrollbar.handleRect = handle;
        scrollbar.targetGraphic = handleImg;

        scroll.verticalScrollbar = scrollbar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

        var clamp = root.gameObject.AddComponent<ScrollViewHeightClamp>();
        clamp.content = content;
        clamp.target = rootLayout;
        clamp.maxHeight = maxHeight;

        return content;
    }

    // ---------------------------------------------------------------- stepper (◀ value ▶)

    /// <summary>Prev/next selector for option lists too long for a segmented control (e.g. resolutions).</summary>
    public static MenuStepper CreateStepper(Transform parent, float height = 44f)
    {
        RectTransform row = CreateRow(parent, "Stepper", height, 8f);
        var stepper = row.gameObject.AddComponent<MenuStepper>();
        stepper.Build(height);
        return stepper;
    }
}

/// <summary>Keeps a card root's height matched to its layout-driven content.</summary>
[DisallowMultipleComponent]
public sealed class CardHeightMirror : MonoBehaviour
{
    public RectTransform content;

    RectTransform _self;

    void Awake()
    {
        _self = (RectTransform)transform;
    }

    void LateUpdate()
    {
        if (content == null)
            return;
        float h = LayoutUtility.GetPreferredHeight(content);
        if (!Mathf.Approximately(_self.sizeDelta.y, h))
            _self.sizeDelta = new Vector2(_self.sizeDelta.x, h);
    }
}

/// <summary>Two-or-more option selector (e.g. OPEN MIC / PUSH TO TALK).</summary>
[DisallowMultipleComponent]
public sealed class MenuSegmented : MonoBehaviour
{
    public event Action<int> Changed;

    Button[] _buttons;
    Image[] _fills;
    Image[] _outlines;
    TMPro.TextMeshProUGUI[] _labels;
    int _selected = -1;

    public int Selected => _selected;

    public void Build(string[] options, float height)
    {
        _buttons = new Button[options.Length];
        _fills = new Image[options.Length];
        _outlines = new Image[options.Length];
        _labels = new TMPro.TextMeshProUGUI[options.Length];

        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            RectTransform opt = MenuWidgets.CreateRect("Option_" + options[i], transform);
            MenuWidgets.SetLayout(opt, flexibleWidth: 1f, minHeight: height, preferredHeight: height);

            var fill = opt.gameObject.AddComponent<Image>();
            fill.sprite = MenuTheme.RoundedRect(6);
            fill.type = Image.Type.Sliced;
            fill.raycastTarget = true;

            Image outline = MenuWidgets.CreateImage(opt, "Outline", MenuTheme.RoundedOutline(6, 1.4f), MenuTheme.Stroke);
            RectTransform outlineRt = outline.rectTransform;
            outlineRt.anchorMin = Vector2.zero;
            outlineRt.anchorMax = Vector2.one;
            outlineRt.offsetMin = Vector2.zero;
            outlineRt.offsetMax = Vector2.zero;

            TMPro.TextMeshProUGUI label = MenuWidgets.CreateText(opt, "Label", options[i], 15.5f, MenuTheme.Mist,
                MenuWidgets.FontKind.Body, TMPro.TextAlignmentOptions.Center, 4f);
            RectTransform labelRt = label.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;

            var button = opt.gameObject.AddComponent<Button>();
            button.targetGraphic = fill;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(() =>
            {
                MenuUiAudio.PlayClick();
                Set(index, true);
            });

            _buttons[i] = button;
            _fills[i] = fill;
            _outlines[i] = outline;
            _labels[i] = label;
        }

        Refresh();
    }

    public void Set(int index, bool notify)
    {
        if (index == _selected)
        {
            Refresh();
            return;
        }

        _selected = index;
        Refresh();
        if (notify)
            Changed?.Invoke(index);
    }

    void Refresh()
    {
        if (_fills == null)
            return;

        for (int i = 0; i < _fills.Length; i++)
        {
            bool on = i == _selected;
            _fills[i].color = on ? MenuTheme.WithAlpha(MenuTheme.Amber, 0.92f) : MenuTheme.WithAlpha(Color.white, 0.02f);
            _outlines[i].color = on ? MenuTheme.Amber : MenuTheme.Stroke;
            _labels[i].color = on ? new Color(0.10f, 0.08f, 0.04f, 1f) : MenuTheme.Mist;
        }
    }
}

/// <summary>Clamps a scroll view's height to its content up to a maximum, then lets it scroll.</summary>
[DisallowMultipleComponent]
public sealed class ScrollViewHeightClamp : MonoBehaviour
{
    public RectTransform content;
    public LayoutElement target;
    public float maxHeight = 560f;

    void LateUpdate()
    {
        if (content == null || target == null)
            return;
        float preferred = LayoutUtility.GetPreferredHeight(content);
        float clamped = Mathf.Min(preferred, maxHeight);
        if (!Mathf.Approximately(target.preferredHeight, clamped))
            target.preferredHeight = clamped;
    }
}

/// <summary>Prev/next option selector (e.g. screen resolution) — a labelled value flanked by ◀ ▶ buttons.</summary>
[DisallowMultipleComponent]
public sealed class MenuStepper : MonoBehaviour
{
    public event Action<int> Changed;

    string[] _options = Array.Empty<string>();
    int _index;
    TMPro.TextMeshProUGUI _label;

    public int Index => _index;

    public void Build(float height)
    {
        Button prev = MenuWidgets.CreateGhostButton(transform, "<", () => Step(-1), false, height, 20f);
        MenuWidgets.SetLayout(prev.transform, minWidth: 46f, preferredWidth: 46f, minHeight: height, preferredHeight: height);

        RectTransform value = MenuWidgets.CreateRect("Value", transform);
        MenuWidgets.SetLayout(value, flexibleWidth: 1f, minHeight: height, preferredHeight: height);
        Image bg = MenuWidgets.CreateImage(value, "Bg", MenuTheme.RoundedRect(6), MenuTheme.WithAlpha(MenuTheme.PanelRaised, 0.6f), true);
        RectTransform bgRt = bg.rectTransform;
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;

        _label = MenuWidgets.CreateText(value, "Val", "—", 16f, MenuTheme.Mist,
            MenuWidgets.FontKind.Body, TMPro.TextAlignmentOptions.Center, 1f);
        RectTransform labelRt = _label.rectTransform;
        labelRt.anchorMin = Vector2.zero;
        labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = Vector2.zero;
        labelRt.offsetMax = Vector2.zero;

        Button next = MenuWidgets.CreateGhostButton(transform, ">", () => Step(1), false, height, 20f);
        MenuWidgets.SetLayout(next.transform, minWidth: 46f, preferredWidth: 46f, minHeight: height, preferredHeight: height);
    }

    public void SetOptions(string[] options, int index, bool notify)
    {
        _options = options ?? Array.Empty<string>();
        Set(index, notify);
    }

    public void Set(int index, bool notify)
    {
        _index = _options.Length == 0 ? 0 : Mathf.Clamp(index, 0, _options.Length - 1);
        Refresh();
        if (notify)
            Changed?.Invoke(_index);
    }

    void Step(int direction)
    {
        if (_options.Length == 0)
            return;
        int next = Mathf.Clamp(_index + direction, 0, _options.Length - 1);
        if (next == _index)
            return;
        _index = next;
        Refresh();
        Changed?.Invoke(_index);
    }

    void Refresh()
    {
        if (_label != null)
            _label.text = _options.Length > 0 ? _options[_index] : "—";
    }
}
