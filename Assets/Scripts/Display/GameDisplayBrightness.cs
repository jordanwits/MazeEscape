using UnityEngine;
using UnityEngine.SceneManagement;

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
    }
}
