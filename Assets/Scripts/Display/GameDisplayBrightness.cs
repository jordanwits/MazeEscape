using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Client-only visibility in dark areas by scaling Environment Lighting (ambient intensity + reflection intensity),
/// matching the Lighting window "Intensity Multiplier" style behavior. Re-baselines per scene load.
/// Slider 0.5 leaves the scene as authored; lower dims ambient, higher boosts it.
///
/// ambientIntensity only scales Skybox ambient. A Trilight or Flat level (Level03) builds its ambient straight
/// from the colours, so there the colours themselves are scaled — the slider did almost nothing on that level
/// before. <see cref="MazeEnvironmentZone"/> also writes those colours; it listens to <see cref="EnvironmentApplied"/>.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-19)]
public sealed class GameDisplayBrightness : MonoBehaviour
{
    public static GameDisplayBrightness Instance { get; private set; }

    /// <summary>
    /// Raised after the level's environment lighting has been rewritten from the slider (scene load or slider move).
    /// Anything holding its own copy of those values to restore later must re-capture them here.
    /// </summary>
    public static event Action EnvironmentApplied;

    const string PrefsKey = "GameDisplay.BrightnessNormalized";

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
    AmbientMode _baselineAmbientMode;
    Color _baselineSky;
    Color _baselineEquator;
    Color _baselineGround;

    public float BrightnessNormalized => _normalized;

    /// <summary>True when this level's ambient is driven by its colours (not Skybox), so the slider rewrites them.</summary>
    public bool ScalesAmbientColours => _baselineAmbientMode != AmbientMode.Skybox;

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
    }

    void CaptureBaselineFromRenderSettings()
    {
        _baselineAmbientIntensity = RenderSettings.ambientIntensity;
        _baselineReflectionIntensity = RenderSettings.reflectionIntensity;
        _baselineAmbientMode = RenderSettings.ambientMode;
        _baselineSky = RenderSettings.ambientSkyColor;
        _baselineEquator = RenderSettings.ambientEquatorColor;
        _baselineGround = RenderSettings.ambientGroundColor;
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
    }

    void ApplyEnvironmentLightingFromUserSetting()
    {
        float mul = UserIntensityMultiplier();

        float amb = Mathf.Clamp(_baselineAmbientIntensity * mul, 0f, _maxAmbientIntensity);
        float refl = Mathf.Clamp(_baselineReflectionIntensity * mul, 0f, _maxReflectionIntensity);

        RenderSettings.ambientIntensity = amb;
        RenderSettings.reflectionIntensity = refl;

        if (ScalesAmbientColours)
        {
            RenderSettings.ambientSkyColor = ScaleAmbientColour(_baselineSky, mul);
            RenderSettings.ambientEquatorColor = ScaleAmbientColour(_baselineEquator, mul);
            RenderSettings.ambientGroundColor = ScaleAmbientColour(_baselineGround, mul);
        }

        EnvironmentApplied?.Invoke();
    }

    Color ScaleAmbientColour(Color authored, float mul)
    {
        return new Color(
            Mathf.Clamp(authored.r * mul, 0f, _maxAmbientIntensity),
            Mathf.Clamp(authored.g * mul, 0f, _maxAmbientIntensity),
            Mathf.Clamp(authored.b * mul, 0f, _maxAmbientIntensity),
            authored.a);
    }
}
