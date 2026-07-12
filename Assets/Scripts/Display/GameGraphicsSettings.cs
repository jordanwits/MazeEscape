using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Player-facing graphics quality system. Defines Low/Medium/High/Ultra presets in code and applies
/// them at runtime to the active <see cref="UniversalRenderPipelineAsset"/> (render scale, MSAA,
/// upscaler, shadow distance/cascades) plus <see cref="QualitySettings"/> (texture mip limit,
/// anisotropic filtering, LOD bias, reflection probes, soft particles) and the window
/// (<see cref="Screen"/> resolution / mode, vSync, frame-rate cap).
///
/// Lives on the same DontDestroyOnLoad object as <see cref="MultiplayerBootstrap"/>, mirroring
/// <see cref="GameAudioManager"/> / <see cref="GameDisplayBrightness"/>: a singleton that loads from
/// PlayerPrefs in Awake, exposes Set*/getters for the menu UI, and persists every change.
///
/// All settings are pure rendering knobs — none of them affect gameplay, networking, or simulation.
/// On first launch (no saved preset) it auto-detects a sensible tier from <see cref="SystemInfo"/>.
///
/// NOTE: in a build, mutating the shared URP asset only affects the in-memory copy for the session
/// (re-applied from prefs each launch). In the Editor those mutations can linger on the asset until a
/// reimport — that's a dev-only cosmetic side effect, not a shipped one.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-18)]
public sealed class GameGraphicsSettings : MonoBehaviour
{
    public static GameGraphicsSettings Instance { get; private set; }

    // ----------------------------------------------------------------- prefs keys
    const string PrefsTier = "GameGraphics.Tier";
    const string PrefsRenderScale = "GameGraphics.RenderScale";
    const string PrefsVSync = "GameGraphics.VSync";
    const string PrefsFpsCap = "GameGraphics.FpsCap";
    const string PrefsDisplayMode = "GameGraphics.DisplayMode";
    const string PrefsResW = "GameGraphics.ResW";
    const string PrefsResH = "GameGraphics.ResH";
    const string PrefsHasResolution = "GameGraphics.HasResolution";

    public enum Tier { Low = 0, Medium = 1, High = 2, Ultra = 3 }

    /// <summary>A quality preset. Every field is a rendering-only knob.</summary>
    readonly struct TierPreset
    {
        public readonly string Name;
        public readonly float RenderScale;             // 0.5..1.0 (URP renderScale; biggest GPU lever)
        public readonly UpscalingFilterSelection Upscaler; // how a sub-1.0 render scale is upscaled
        public readonly int Msaa;                      // 1 = off, 2/4/8 = MSAA samples
        public readonly float ShadowDistance;          // metres
        public readonly int ShadowCascades;            // 1..4
        public readonly int TextureMipLimit;           // 0 = full res, 1 = half res (saves VRAM)
        public readonly AnisotropicFiltering Aniso;
        public readonly float LodBias;
        public readonly bool RealtimeReflectionProbes;
        public readonly bool SoftParticles;

        public TierPreset(string name, float renderScale, UpscalingFilterSelection upscaler, int msaa,
            float shadowDistance, int shadowCascades, int textureMipLimit, AnisotropicFiltering aniso,
            float lodBias, bool realtimeReflectionProbes, bool softParticles)
        {
            Name = name;
            RenderScale = renderScale;
            Upscaler = upscaler;
            Msaa = msaa;
            ShadowDistance = shadowDistance;
            ShadowCascades = shadowCascades;
            TextureMipLimit = textureMipLimit;
            Aniso = aniso;
            LodBias = lodBias;
            RealtimeReflectionProbes = realtimeReflectionProbes;
            SoftParticles = softParticles;
        }
    }

    // Concrete tier table. Shadow values are tuned for an indoor maze but are currently inert (the game
    // uses ambient-only lighting with no shadow-casting lights) — they cost nothing and are ready if a
    // shadow-casting light is ever added. Render scale is the dominant lever for weak GPUs.
    static readonly TierPreset[] Tiers =
    {
        //          name       rScale upscaler                          msaa shDist cas mip aniso                             lod  refl  soft
        new TierPreset("Low",    0.70f, UpscalingFilterSelection.FSR,     1,  25f,   1,  1,  AnisotropicFiltering.Disable,     0.5f, false, false),
        new TierPreset("Medium", 0.85f, UpscalingFilterSelection.FSR,     1,  40f,   2,  0,  AnisotropicFiltering.Enable,      0.8f, false, false),
        new TierPreset("High",   1.00f, UpscalingFilterSelection.Auto,    1,  50f,   4,  0,  AnisotropicFiltering.Enable,      1.0f, true,  true),
        new TierPreset("Ultra",  1.00f, UpscalingFilterSelection.Auto,    2,  75f,   4,  0,  AnisotropicFiltering.ForceEnable, 1.5f, true,  true),
    };

    // FPS-cap options surfaced to the menu (last entry = uncapped).
    static readonly int[] FpsCapOptions = { 30, 60, 120, 144, -1 };

    int _tier = (int)Tier.High;
    float _renderScale = 1f;
    int _vSync = 1;
    int _fpsCap = -1;
    FullScreenMode _displayMode = FullScreenMode.FullScreenWindow;
    bool _hasStoredResolution;
    int _storedResW;
    int _storedResH;

    List<Resolution> _resolutions = new List<Resolution>();

    // ----------------------------------------------------------------- public read API (for the menu)
    public int TierCount => Tiers.Length;
    public int CurrentTier => _tier;
    public float RenderScale => _renderScale;
    public int VSync => _vSync;
    public int FpsCap => _fpsCap;
    public FullScreenMode DisplayMode => _displayMode;
    public IReadOnlyList<Resolution> Resolutions => _resolutions;

    public static string[] TierNames()
    {
        var names = new string[Tiers.Length];
        for (int i = 0; i < Tiers.Length; i++)
            names[i] = Tiers[i].Name;
        return names;
    }

    public static int[] FpsOptions => FpsCapOptions;

    /// <summary>Index into <see cref="FpsCapOptions"/> for the current cap (defaults to "uncapped").</summary>
    public int FpsCapOptionIndex
    {
        get
        {
            for (int i = 0; i < FpsCapOptions.Length; i++)
                if (FpsCapOptions[i] == _fpsCap)
                    return i;
            return FpsCapOptions.Length - 1;
        }
    }

    /// <summary>Index of the display mode within the menu's [Fullscreen, Borderless, Windowed] order.</summary>
    public int DisplayModeIndex => DisplayModeToIndex(_displayMode);

    /// <summary>Index of the closest available resolution to what's currently applied.</summary>
    public int CurrentResolutionIndex
    {
        get
        {
            int w = _hasStoredResolution ? _storedResW : Screen.width;
            int h = _hasStoredResolution ? _storedResH : Screen.height;
            int best = 0;
            long bestDelta = long.MaxValue;
            for (int i = 0; i < _resolutions.Count; i++)
            {
                long dw = _resolutions[i].width - w;
                long dh = _resolutions[i].height - h;
                long delta = dw * dw + dh * dh;
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = i;
                }
            }
            return best;
        }
    }

    // ----------------------------------------------------------------- lifecycle
    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;

        BuildResolutionList();
        LoadPrefsOrAutoDetect();
        ApplyAll();
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void BuildResolutionList()
    {
        var byShape = new Dictionary<long, Resolution>();
        Resolution[] all = Screen.resolutions;
        for (int i = 0; i < all.Length; i++)
        {
            Resolution r = all[i];
            long key = ((long)r.width << 20) ^ r.height; // unique per (w,h)
            if (!byShape.TryGetValue(key, out Resolution existing)
                || r.refreshRateRatio.value > existing.refreshRateRatio.value)
            {
                byShape[key] = r; // keep the highest refresh rate for each w×h
            }
        }

        _resolutions = new List<Resolution>(byShape.Values);
        _resolutions.Sort((a, b) => a.width != b.width ? a.width.CompareTo(b.width) : a.height.CompareTo(b.height));

        if (_resolutions.Count == 0)
            _resolutions.Add(Screen.currentResolution); // headless / unusual platform fallback
    }

    void LoadPrefsOrAutoDetect()
    {
        if (PlayerPrefs.HasKey(PrefsTier))
            _tier = Mathf.Clamp(PlayerPrefs.GetInt(PrefsTier, (int)Tier.High), 0, Tiers.Length - 1);
        else
        {
            _tier = AutoDetectTier();
            PlayerPrefs.SetInt(PrefsTier, _tier); // remember the auto-detected choice
        }

        _renderScale = Mathf.Clamp(PlayerPrefs.GetFloat(PrefsRenderScale, Tiers[_tier].RenderScale), 0.5f, 1f);
        _vSync = Mathf.Clamp(PlayerPrefs.GetInt(PrefsVSync, 1), 0, 2);
        _fpsCap = PlayerPrefs.GetInt(PrefsFpsCap, -1);
        _displayMode = (FullScreenMode)PlayerPrefs.GetInt(PrefsDisplayMode, (int)Screen.fullScreenMode);

        _hasStoredResolution = PlayerPrefs.GetInt(PrefsHasResolution, 0) == 1;
        _storedResW = PlayerPrefs.GetInt(PrefsResW, Screen.width);
        _storedResH = PlayerPrefs.GetInt(PrefsResH, Screen.height);
    }

    /// <summary>Picks a conservative starting tier from the GPU/CPU/RAM so weak machines don't open at Ultra.</summary>
    int AutoDetectTier()
    {
        int vram = SystemInfo.graphicsMemorySize; // MB (may be shared on integrated GPUs)
        int ram = SystemInfo.systemMemorySize;     // MB
        int cores = SystemInfo.processorCount;

        if (vram >= 6000 && cores >= 8 && ram >= 12000)
            return (int)Tier.Ultra;
        if (vram >= 3000 && cores >= 6 && ram >= 8000)
            return (int)Tier.High;
        if (vram >= 1500 && cores >= 4)
            return (int)Tier.Medium;
        return (int)Tier.Low;
    }

    // ----------------------------------------------------------------- stylized render override
    // MazePostFx (one per gameplay level) drives a fixed-height "retro" render resolution as art
    // direction (Lethal-Company-style low-res). While active it replaces the user's render scale and
    // the tier's upscaling filter. The user's stored values are untouched and win again the moment
    // the level unloads (MazePostFx clears the override in OnDisable). Routing this through the same
    // Apply* choke points keeps this class the single writer of the URP asset.
    float _stylizedScale = -1f; // <= 0 means inactive
    UpscalingFilterSelection _stylizedFilter = UpscalingFilterSelection.Point;

    public bool StylizedRenderActive => _stylizedScale > 0f;

    public void SetStylizedRender(float renderScale, UpscalingFilterSelection filter)
    {
        _stylizedScale = Mathf.Clamp(renderScale, 0.1f, 1f);
        _stylizedFilter = filter;
        ApplyTierVisuals();
        ApplyRenderScale();
    }

    public void ClearStylizedRender()
    {
        if (!StylizedRenderActive)
            return;
        _stylizedScale = -1f;
        ApplyTierVisuals();
        ApplyRenderScale();
    }

    // ----------------------------------------------------------------- apply
    static UniversalRenderPipelineAsset ActiveUrpAsset =>
        (QualitySettings.renderPipeline != null
            ? QualitySettings.renderPipeline
            : GraphicsSettings.defaultRenderPipeline) as UniversalRenderPipelineAsset;

    void ApplyAll()
    {
        ApplyTierVisuals();
        ApplyRenderScale();
        ApplyVSync();
        ApplyFpsCap();

        // Only force a window change if the player explicitly chose one — never surprise them on first launch.
        if (_hasStoredResolution)
            ApplyResolutionAndMode();
        else
            Screen.fullScreenMode = _displayMode;
    }

    void ApplyTierVisuals()
    {
        TierPreset t = Tiers[Mathf.Clamp(_tier, 0, Tiers.Length - 1)];

        UniversalRenderPipelineAsset urp = ActiveUrpAsset;
        if (urp != null)
        {
            urp.msaaSampleCount = t.Msaa;
            urp.upscalingFilter = StylizedRenderActive ? _stylizedFilter : t.Upscaler;
            urp.shadowDistance = t.ShadowDistance;
            urp.shadowCascadeCount = t.ShadowCascades;
        }

        QualitySettings.globalTextureMipmapLimit = t.TextureMipLimit;
        QualitySettings.anisotropicFiltering = t.Aniso;
        QualitySettings.lodBias = t.LodBias;
        QualitySettings.realtimeReflectionProbes = t.RealtimeReflectionProbes;
        QualitySettings.softParticles = t.SoftParticles;
    }

    void ApplyRenderScale()
    {
        UniversalRenderPipelineAsset urp = ActiveUrpAsset;
        if (urp != null)
            urp.renderScale = StylizedRenderActive ? _stylizedScale : Mathf.Clamp(_renderScale, 0.5f, 1f);
    }

    void ApplyVSync() => QualitySettings.vSyncCount = Mathf.Clamp(_vSync, 0, 2);

    // targetFrameRate is only honoured by Unity when vSyncCount == 0; we still set it so it takes effect
    // the moment vSync is turned off.
    void ApplyFpsCap() => Application.targetFrameRate = _fpsCap;

    void ApplyResolutionAndMode()
    {
        int idx = CurrentResolutionIndex;
        if (idx >= 0 && idx < _resolutions.Count)
        {
            Resolution r = _resolutions[idx];
            Screen.SetResolution(r.width, r.height, _displayMode, r.refreshRateRatio);
        }
        else
        {
            Screen.fullScreenMode = _displayMode;
        }
    }

    // ----------------------------------------------------------------- public write API (menu calls these)
    public void SetTier(int tierIndex)
    {
        _tier = Mathf.Clamp(tierIndex, 0, Tiers.Length - 1);
        _renderScale = Tiers[_tier].RenderScale; // a preset resets render scale to its default; player can re-tweak

        PlayerPrefs.SetInt(PrefsTier, _tier);
        PlayerPrefs.SetFloat(PrefsRenderScale, _renderScale);
        PlayerPrefs.Save();

        ApplyTierVisuals();
        ApplyRenderScale();
    }

    public void SetRenderScale(float scale)
    {
        _renderScale = Mathf.Clamp(scale, 0.5f, 1f);
        PlayerPrefs.SetFloat(PrefsRenderScale, _renderScale);
        PlayerPrefs.Save();
        ApplyRenderScale();
    }

    public void SetVSync(int count)
    {
        _vSync = Mathf.Clamp(count, 0, 2);
        PlayerPrefs.SetInt(PrefsVSync, _vSync);
        PlayerPrefs.Save();
        ApplyVSync();
    }

    public void SetFpsCapOptionIndex(int optionIndex)
    {
        optionIndex = Mathf.Clamp(optionIndex, 0, FpsCapOptions.Length - 1);
        _fpsCap = FpsCapOptions[optionIndex];
        PlayerPrefs.SetInt(PrefsFpsCap, _fpsCap);
        PlayerPrefs.Save();
        ApplyFpsCap();
    }

    public void SetDisplayModeIndex(int index)
    {
        _displayMode = IndexToDisplayMode(index);
        PlayerPrefs.SetInt(PrefsDisplayMode, (int)_displayMode);
        PlayerPrefs.Save();

        if (_hasStoredResolution)
            ApplyResolutionAndMode();
        else
            Screen.fullScreenMode = _displayMode;
    }

    public void SetResolutionIndex(int index)
    {
        if (_resolutions.Count == 0)
            return;

        index = Mathf.Clamp(index, 0, _resolutions.Count - 1);
        Resolution r = _resolutions[index];

        _hasStoredResolution = true;
        _storedResW = r.width;
        _storedResH = r.height;
        PlayerPrefs.SetInt(PrefsHasResolution, 1);
        PlayerPrefs.SetInt(PrefsResW, r.width);
        PlayerPrefs.SetInt(PrefsResH, r.height);
        PlayerPrefs.Save();

        Screen.SetResolution(r.width, r.height, _displayMode, r.refreshRateRatio);
    }

    // ----------------------------------------------------------------- menu label helpers
    public string ResolutionLabel(int index)
    {
        if (index < 0 || index >= _resolutions.Count)
            return "—";
        Resolution r = _resolutions[index];
        return r.width + " × " + r.height;
    }

    public static string[] DisplayModeNames() => new[] { "FULLSCREEN", "BORDERLESS", "WINDOWED" };

    public static string[] VSyncNames() => new[] { "OFF", "ON" };

    public static string[] FpsCapNames()
    {
        var names = new string[FpsCapOptions.Length];
        for (int i = 0; i < FpsCapOptions.Length; i++)
            names[i] = FpsCapOptions[i] < 0 ? "UNCAPPED" : FpsCapOptions[i].ToString();
        return names;
    }

    static int DisplayModeToIndex(FullScreenMode mode)
    {
        switch (mode)
        {
            case FullScreenMode.ExclusiveFullScreen: return 0;
            case FullScreenMode.FullScreenWindow: return 1;
            case FullScreenMode.Windowed: return 2;
            default: return 1; // MaximizedWindow and anything else map to Borderless
        }
    }

    static FullScreenMode IndexToDisplayMode(int index)
    {
        switch (index)
        {
            case 0: return FullScreenMode.ExclusiveFullScreen;
            case 2: return FullScreenMode.Windowed;
            default: return FullScreenMode.FullScreenWindow;
        }
    }
}
