using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Client-only visibility in dark areas by scaling Environment Lighting (ambient intensity + reflection intensity),
/// matching the Lighting window "Intensity Multiplier" style behavior. Re-baselines per scene load.
/// Slider 0.5 leaves the scene as authored; lower dims ambient, higher boosts it.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-19)]
public sealed class GameDisplayBrightness : MonoBehaviour
{
    public static GameDisplayBrightness Instance { get; private set; }

    const string PrefsKey = "GameDisplay.BrightnessNormalized";

    [SerializeField] int _menuStripCanvasSortOrder = 2100;

    [SerializeField, Range(0f, 1f)] float _defaultNormalized = 0.5f;

    [SerializeField, Tooltip("Ambient + reflection multiplier at slider 0 (relative to each scene's authored values).")]
    float _intensityMulAtSlider0 = 0.22f;

    [SerializeField, Tooltip("Ambient + reflection multiplier at slider 1.")]
    float _intensityMulAtSlider1 = 2.35f;

    [SerializeField, Tooltip("Max combined ambient intensity after boost (safety clamp).")]
    float _maxAmbientIntensity = 8f;

    [SerializeField, Tooltip("Max reflection intensity after boost (safety clamp).")]
    float _maxReflectionIntensity = 8f;

    float _normalized = 0.5f;

    float _baselineAmbientIntensity;
    float _baselineReflectionIntensity;

    GameObject _menuStripRoot;
    Slider _menuSlider;
    bool _menuSliderListenersWired;

    public float BrightnessNormalized => _normalized;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;
        _normalized = Mathf.Clamp01(PlayerPrefs.GetFloat(PrefsKey, _defaultNormalized));
        SceneManager.sceneLoaded += OnSceneLoaded;

        CaptureBaselineFromRenderSettings();
        ApplyEnvironmentLightingFromUserSetting();
        EnsureMenuBrightnessStrip();
        RefreshMenuStripActive();
        SyncMenuSliderFromSettings();
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (Instance == this)
            Instance = null;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        CaptureBaselineFromRenderSettings();
        ApplyEnvironmentLightingFromUserSetting();
        RefreshMenuStripActive();
        SyncMenuSliderFromSettings();
    }

    void CaptureBaselineFromRenderSettings()
    {
        _baselineAmbientIntensity = RenderSettings.ambientIntensity;
        _baselineReflectionIntensity = RenderSettings.reflectionIntensity;
    }

    void RefreshMenuStripActive()
    {
        if (_menuStripRoot == null)
            return;

        Scene active = SceneManager.GetActiveScene();
        bool inMenu = active.IsValid() && active.name == MultiplayerSceneFlow.MenuSceneName;
        _menuStripRoot.SetActive(inMenu);
    }

    void SyncMenuSliderFromSettings()
    {
        if (_menuSlider == null)
            return;

        _menuSlider.SetValueWithoutNotify(_normalized);
    }

    void EnsureMenuBrightnessStrip()
    {
        if (_menuStripRoot != null)
            return;

        _menuStripRoot = new GameObject("MenuBrightnessStrip");
        _menuStripRoot.layer = 5;
        _menuStripRoot.transform.SetParent(transform, false);

        var canvas = _menuStripRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = _menuStripCanvasSortOrder;
        canvas.vertexColorAlwaysGammaSpace = true;

        var scaler = _menuStripRoot.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        _menuStripRoot.AddComponent<GraphicRaycaster>();

        var panel = new GameObject("Panel", typeof(RectTransform));
        panel.layer = 5;
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.SetParent(_menuStripRoot.transform, false);
        panelRt.anchorMin = new Vector2(0f, 0f);
        panelRt.anchorMax = new Vector2(1f, 0f);
        panelRt.pivot = new Vector2(0.5f, 0f);
        panelRt.anchoredPosition = new Vector2(0f, 16f);
        panelRt.sizeDelta = new Vector2(-48f, 52f);
        var panelBg = panel.AddComponent<Image>();
        panelBg.color = new Color(0f, 0f, 0f, 0.58f);
        panelBg.raycastTarget = true;

        var labelGo = new GameObject("Label", typeof(RectTransform));
        labelGo.layer = 5;
        var labelRt = labelGo.GetComponent<RectTransform>();
        labelRt.SetParent(panelRt, false);
        labelRt.anchorMin = new Vector2(0f, 0f);
        labelRt.anchorMax = new Vector2(0f, 1f);
        labelRt.pivot = new Vector2(0f, 0.5f);
        labelRt.sizeDelta = new Vector2(280f, 0f);
        labelRt.anchoredPosition = new Vector2(14f, 0f);
        var label = labelGo.AddComponent<Text>();
        label.text = "Environment light";
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (label.font == null)
            label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        label.fontSize = 18;
        label.color = Color.white;
        label.alignment = TextAnchor.MiddleLeft;
        label.raycastTarget = false;

        var sliderGo = new GameObject("BrightnessSlider", typeof(RectTransform));
        sliderGo.layer = 5;
        var sliderRt = sliderGo.GetComponent<RectTransform>();
        sliderRt.SetParent(panelRt, false);
        sliderRt.anchorMin = new Vector2(0f, 0.12f);
        sliderRt.anchorMax = new Vector2(1f, 0.88f);
        sliderRt.offsetMin = new Vector2(268f, 0f);
        sliderRt.offsetMax = new Vector2(-14f, 0f);

        _menuSlider = sliderGo.AddComponent<Slider>();
        _menuSlider.minValue = 0f;
        _menuSlider.maxValue = 1f;
        BuildSliderVisuals(_menuSlider, sliderRt);
        _menuSlider.SetValueWithoutNotify(_normalized);

        WireMenuSliderIfNeeded();
    }

    static void BuildSliderVisuals(Slider slider, RectTransform parent)
    {
        var bg = new GameObject("Background");
        bg.layer = 5;
        var bgRt = bg.AddComponent<RectTransform>();
        bgRt.SetParent(parent, false);
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.sizeDelta = Vector2.zero;
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = new Color(0.12f, 0.12f, 0.14f, 1f);

        var fillArea = new GameObject("Fill Area");
        fillArea.layer = 5;
        var fillAreaRt = fillArea.AddComponent<RectTransform>();
        fillAreaRt.SetParent(parent, false);
        fillAreaRt.anchorMin = Vector2.zero;
        fillAreaRt.anchorMax = Vector2.one;
        fillAreaRt.offsetMin = new Vector2(8f, 6f);
        fillAreaRt.offsetMax = new Vector2(-8f, -6f);

        var fill = new GameObject("Fill");
        fill.layer = 5;
        var fillRt = fill.AddComponent<RectTransform>();
        fillRt.SetParent(fillAreaRt, false);
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = Vector2.one;
        fillRt.offsetMin = Vector2.zero;
        fillRt.offsetMax = Vector2.zero;
        var fillImg = fill.AddComponent<Image>();
        fillImg.color = new Color(0.45f, 0.55f, 0.95f, 1f);

        var handleArea = new GameObject("Handle Slide Area");
        handleArea.layer = 5;
        var handleAreaRt = handleArea.AddComponent<RectTransform>();
        handleAreaRt.SetParent(parent, false);
        handleAreaRt.anchorMin = Vector2.zero;
        handleAreaRt.anchorMax = Vector2.one;
        handleAreaRt.offsetMin = new Vector2(8f, 6f);
        handleAreaRt.offsetMax = new Vector2(-8f, -6f);

        var handle = new GameObject("Handle");
        handle.layer = 5;
        var handleRt = handle.AddComponent<RectTransform>();
        handleRt.SetParent(handleAreaRt, false);
        handleRt.anchorMin = new Vector2(0.5f, 0f);
        handleRt.anchorMax = new Vector2(0.5f, 1f);
        handleRt.pivot = new Vector2(0.5f, 0.5f);
        handleRt.sizeDelta = new Vector2(14f, 0f);
        var handleImg = handle.AddComponent<Image>();
        handleImg.color = Color.white;

        slider.fillRect = fillRt;
        slider.handleRect = handleRt;
        slider.targetGraphic = handleImg;
        slider.direction = Slider.Direction.LeftToRight;
        slider.navigation = Navigation.defaultNavigation;
    }

    void WireMenuSliderIfNeeded()
    {
        if (_menuSliderListenersWired || _menuSlider == null)
            return;

        _menuSlider.onValueChanged.AddListener(SetBrightnessNormalized);
        _menuSliderListenersWired = true;
    }

    /// <summary>1.0 at slider 0.5; below scales down, above scales up.</summary>
    float UserIntensityMultiplier()
    {
        float n = Mathf.Clamp01(_normalized);
        if (n <= 0.5f)
            return Mathf.Lerp(_intensityMulAtSlider0, 1f, n * 2f);
        return Mathf.Lerp(1f, _intensityMulAtSlider1, (n - 0.5f) * 2f);
    }

    public void SetBrightnessNormalized(float linear01)
    {
        _normalized = Mathf.Clamp01(linear01);
        PlayerPrefs.SetFloat(PrefsKey, _normalized);
        PlayerPrefs.Save();
        ApplyEnvironmentLightingFromUserSetting();

        if (_menuSlider != null && Mathf.Abs(_menuSlider.value - _normalized) > 0.0001f)
            _menuSlider.SetValueWithoutNotify(_normalized);
    }

    void ApplyEnvironmentLightingFromUserSetting()
    {
        float mul = UserIntensityMultiplier();

        float amb = Mathf.Clamp(_baselineAmbientIntensity * mul, 0f, _maxAmbientIntensity);
        float refl = Mathf.Clamp(_baselineReflectionIntensity * mul, 0f, _maxReflectionIntensity);

        RenderSettings.ambientIntensity = amb;
        RenderSettings.reflectionIntensity = refl;
    }
}
