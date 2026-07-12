using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Per-level stylized rendering, modelled on Lethal Company's pipeline (Acerola's breakdown:
/// fixed low render resolution + posterized lighting + near-camera edge lines + bloom/grade).
/// One per gameplay level, on the same GameObject as <see cref="WorldRenderCuller"/> /
/// <see cref="MazeDistanceFog"/> — its presence in a scene IS the "this level is stylized" flag;
/// the Menu has none and stays untouched. Three coordinated pieces:
///
///   1. <b>Retro resolution</b> — a fixed-height render scale (e.g. 520 vertical pixels regardless
///      of the player's native res, aspect-correct) pushed through
///      <see cref="GameGraphicsSettings.SetStylizedRender"/> so this class never writes the URP
///      asset itself and can't fight the player-facing quality settings. Also makes every effect
///      below dramatically cheaper — the whole post chain runs at the low resolution.
///   2. <b>Post volume</b> — a runtime-created global <see cref="Volume"/> (bloom, tonemap, color
///      grade, vignette, film grain, chromatic aberration) plus enabling post-processing on the
///      local player camera (reversible, rechecked like MazeDistanceFog's background override —
///      the camera appears on spawn and can change on respawn/character swap).
///   3. <b>Posterize + edge material</b> — parameters for the PosterizeEdge full-screen pass on
///      PC_Renderer (Resources/PostFx/PosterizeEdge.mat). The pass quantizes luminance (colour
///      gradients survive — the Lethal Company trick, adapted from its volumetric buffer to URP's
///      lit image) and draws depth/luminance edges that fade with distance. Master intensity is
///      forced to 0 in OnDisable so the always-present renderer feature is a no-op outside levels.
///
/// Client-side visual only; the headless server has no camera and nothing here touches gameplay.
/// </summary>
[DisallowMultipleComponent]
public class MazePostFx : MonoBehaviour
{
    const string PosterizeMaterialResourcePath = "PostFx/PosterizeEdge";

    [Header("Retro Resolution")]
    [Tooltip("Render the level at a fixed vertical resolution (Lethal Company renders at 520) and upscale "
        + "to the window. The biggest single ingredient of the look — and a large GPU saving.")]
    [SerializeField] bool enableRetroResolution = true;

    [Tooltip("Vertical resolution the level renders at, regardless of the player's native resolution. "
        + "Lethal Company uses 520. Higher = subtler pixelation.")]
    [SerializeField] int targetVerticalResolution = 520;

    [Tooltip("Point = crunchy visible pixels (Lethal Company). Linear = softer, PS2-ish.")]
    [SerializeField] UpscalingFilterSelection upscalingFilter = UpscalingFilterSelection.Point;

    [Header("Post Volume")]
    [Tooltip("Master switch for the runtime-built global Volume (bloom/tonemap/grade/vignette/grain).")]
    [SerializeField] bool enablePostVolume = true;

    [Tooltip("Bloom strength. Lethal Company leans on bloom blobs bleeding over dark areas.")]
    [SerializeField] float bloomIntensity = 0.7f;

    [Tooltip("Brightness where bloom starts. Lower = more of the scene glows.")]
    [SerializeField] float bloomThreshold = 0.95f;

    [Tooltip("Neutral tonemapping compresses HDR highlights without ACES' heavy saturation shift.")]
    [SerializeField] bool neutralTonemap = true;

    [Tooltip("Post exposure in EV. Small positive values lift the lit areas the posterization then bands.")]
    [SerializeField] float postExposure = 0.15f;

    [SerializeField, Range(-100f, 100f)] float saturation = 8f;
    [SerializeField, Range(-100f, 100f)] float contrast = 6f;

    [Tooltip("Darkened screen corners. Subtle values read as dread rather than a scope overlay.")]
    [SerializeField, Range(0f, 1f)] float vignetteIntensity = 0.28f;

    [Tooltip("Analog-horror grain. Reads strongly at low render resolution; keep subtle.")]
    [SerializeField, Range(0f, 1f)] float filmGrainIntensity = 0.15f;

    [Header("Posterize + Edges")]
    [Tooltip("Master switch for the PosterizeEdge full-screen pass (the custom Lethal-Company-style shader).")]
    [SerializeField] bool enablePosterize = true;

    [Tooltip("Number of allowed brightness levels (in perceptual space, so dark tones keep detail). "
        + "Fewer = harsher banding.")]
    [SerializeField, Range(2, 16)] int posterizeSteps = 8;

    [Tooltip("How fully the quantized lighting replaces the smooth image (1 = hard bands).")]
    [SerializeField, Range(0f, 1f)] float posterizeBlend = 0.55f;

    [Tooltip("Relative depth difference that counts as an outline (silhouettes, wall corners).")]
    [SerializeField] float depthEdgeThreshold = 0.08f;

    [Tooltip("Brightness difference that counts as an outline. Too low and it speckles on noisy "
        + "textures (wood grain, sawdust).")]
    [SerializeField] float luminanceEdgeThreshold = 0.35f;

    [Tooltip("Metres from the camera where edge lines have fully faded out — Lethal Company only draws "
        + "edges close to the camera.")]
    [SerializeField] float edgeFadeDistance = 12f;

    [SerializeField, Range(0f, 1f)] float edgeIntensity = 0.45f;
    [SerializeField] Color edgeColor = new(0.02f, 0.02f, 0.03f, 1f);

    [Header("Camera")]
    [Tooltip("Seconds between checks for the local camera (it appears when the player spawns and can change "
        + "on respawn / character swap). Also re-derives the retro render scale after window resizes.")]
    [SerializeField] float cameraRecheckInterval = 0.5f;

    // Shader property ids (must match PosterizeEdge.shader).
    static readonly int IntensityId = Shader.PropertyToID("_Intensity");
    static readonly int StepsId = Shader.PropertyToID("_PosterizeSteps");
    static readonly int BlendId = Shader.PropertyToID("_PosterizeBlend");
    static readonly int DepthEdgeId = Shader.PropertyToID("_DepthEdgeThreshold");
    static readonly int LumEdgeId = Shader.PropertyToID("_LumEdgeThreshold");
    static readonly int EdgeFadeId = Shader.PropertyToID("_EdgeFadeDistance");
    static readonly int EdgeIntensityId = Shader.PropertyToID("_EdgeIntensity");
    static readonly int EdgeColorId = Shader.PropertyToID("_EdgeColor");

    /// <summary>The level's active instance (one per gameplay scene) — gameplay hooks (chase panic,
    /// hit feedback) reach the chromatic aberration through this.</summary>
    public static MazePostFx Active { get; private set; }

    // Volume bookkeeping.
    GameObject _volumeGo;
    VolumeProfile _profile;
    Bloom _bloom;
    Tonemapping _tonemap;
    ColorAdjustments _colorAdjust;
    Vignette _vignette;
    FilmGrain _grain;
    ChromaticAberration _chroma;

    // Camera-flag bookkeeping so the change is fully reversible (mirrors MazeDistanceFog).
    Camera _camera;
    bool _savedPostFlag;
    bool _cameraFlagOverridden;
    float _nextCameraCheck;

    Material _posterizeMat;
    bool _warnedNoMaterial;
    int _appliedStylizedHeight = -1;

    void OnEnable()
    {
        Active = this;
        ApplyAll();
        _nextCameraCheck = 0f;
    }

    void OnDisable()
    {
        if (Active == this)
            Active = null;

        RestoreCameraFlag();

        if (GameGraphicsSettings.Instance != null)
            GameGraphicsSettings.Instance.ClearStylizedRender();
        _appliedStylizedHeight = -1;

        // The renderer feature (and its material) outlive the level — silence the pass for the menu.
        if (_posterizeMat != null)
            _posterizeMat.SetFloat(IntensityId, 0f);

        if (_volumeGo != null)
            Destroy(_volumeGo);
        if (_profile != null)
            Destroy(_profile);
        _volumeGo = null;
        _profile = null;
    }

    void LateUpdate()
    {
        float now = Time.unscaledTime;
        if (now < _nextCameraCheck)
            return;
        _nextCameraCheck = now + Mathf.Max(0.1f, cameraRecheckInterval);

        EnsureCameraPostFlag();
        EnsureStylizedScale(); // window size can change mid-level (resolution menu, alt-enter)
    }

    /// <summary>Pushes every setting live. Safe to call at runtime after tuning.</summary>
    public void ApplyAll()
    {
        EnsureStylizedScale();
        EnsureVolume();
        ApplyVolumeSettings();
        ApplyPosterizeSettings();
        EnsureCameraPostFlag();
    }

    /// <summary>Gameplay hook: 0 = calm, ~0.5+ = panic (chases, hits). Purely visual.</summary>
    public void SetChromaticAberration(float intensity)
    {
        if (_chroma != null)
            _chroma.intensity.value = Mathf.Clamp01(intensity);
    }

    // ----------------------------------------------------------------- retro resolution
    void EnsureStylizedScale()
    {
        var graphics = GameGraphicsSettings.Instance;
        if (graphics == null)
            return; // direct scene play without the bootstrap — post volume still works, retro res skipped

        if (!enableRetroResolution || targetVerticalResolution <= 0)
        {
            graphics.ClearStylizedRender();
            _appliedStylizedHeight = -1;
            return;
        }

        // Fixed-height equivalent of Lethal Company's fixed 860x520: scale so the render target is
        // targetVerticalResolution pixels tall regardless of the window (aspect-correct — we skip
        // LC's anamorphic stretch). Re-derived when the window height changes.
        if (_appliedStylizedHeight == Screen.height && graphics.StylizedRenderActive)
            return;

        float scale = Mathf.Clamp((float)targetVerticalResolution / Mathf.Max(1, Screen.height), 0.1f, 1f);
        graphics.SetStylizedRender(scale, upscalingFilter);
        _appliedStylizedHeight = Screen.height;
    }

    // ----------------------------------------------------------------- post volume
    void EnsureVolume()
    {
        if (!enablePostVolume)
        {
            if (_volumeGo != null)
                _volumeGo.SetActive(false);
            return;
        }

        if (_volumeGo != null)
        {
            _volumeGo.SetActive(true);
            return;
        }

        _profile = ScriptableObject.CreateInstance<VolumeProfile>();
        _bloom = _profile.Add<Bloom>();
        _tonemap = _profile.Add<Tonemapping>();
        _colorAdjust = _profile.Add<ColorAdjustments>();
        _vignette = _profile.Add<Vignette>();
        _grain = _profile.Add<FilmGrain>();
        _chroma = _profile.Add<ChromaticAberration>();

        _volumeGo = new GameObject("MazePostFxVolume");
        _volumeGo.transform.SetParent(transform, false);
        var volume = _volumeGo.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 100f; // above any stray authored volume
        volume.sharedProfile = _profile;
    }

    void ApplyVolumeSettings()
    {
        if (_profile == null)
            return;

        _bloom.intensity.Override(Mathf.Max(0f, bloomIntensity));
        _bloom.threshold.Override(Mathf.Max(0f, bloomThreshold));
        _bloom.scatter.Override(0.7f);

        _tonemap.mode.Override(neutralTonemap ? TonemappingMode.Neutral : TonemappingMode.None);

        _colorAdjust.postExposure.Override(postExposure);
        _colorAdjust.saturation.Override(saturation);
        _colorAdjust.contrast.Override(contrast);

        _vignette.intensity.Override(vignetteIntensity);
        _vignette.smoothness.Override(0.35f);

        _grain.type.Override(FilmGrainLookup.Thin1);
        _grain.intensity.Override(filmGrainIntensity);

        _chroma.intensity.Override(0f); // idle; gameplay pulses via SetChromaticAberration
    }

    // ----------------------------------------------------------------- posterize material
    void ApplyPosterizeSettings()
    {
        if (_posterizeMat == null)
        {
            _posterizeMat = Resources.Load<Material>(PosterizeMaterialResourcePath);
            if (_posterizeMat == null)
            {
                if (!_warnedNoMaterial)
                {
                    Debug.LogWarning("[MazePostFx] Resources/" + PosterizeMaterialResourcePath
                        + ".mat not found — posterize/edge pass disabled.", this);
                    _warnedNoMaterial = true;
                }
                return;
            }
        }

        _posterizeMat.SetFloat(IntensityId, enablePosterize ? 1f : 0f);
        _posterizeMat.SetFloat(StepsId, posterizeSteps);
        _posterizeMat.SetFloat(BlendId, posterizeBlend);
        _posterizeMat.SetFloat(DepthEdgeId, Mathf.Max(0.0001f, depthEdgeThreshold));
        _posterizeMat.SetFloat(LumEdgeId, Mathf.Max(0.0001f, luminanceEdgeThreshold));
        _posterizeMat.SetFloat(EdgeFadeId, Mathf.Max(0.1f, edgeFadeDistance));
        _posterizeMat.SetFloat(EdgeIntensityId, edgeIntensity);
        _posterizeMat.SetColor(EdgeColorId, edgeColor);
    }

    // ----------------------------------------------------------------- camera flag
    void EnsureCameraPostFlag()
    {
        Camera cam = ResolveViewpoint();
        if (cam == _camera)
        {
            // Same camera — re-assert in case something else reset it.
            if (cam != null && _cameraFlagOverridden)
                SetPostFlag(cam, true);
            return;
        }

        // Camera changed (first spawn, respawn, character swap) — restore the old one, take over the new.
        RestoreCameraFlag();

        _camera = cam;
        if (_camera == null)
            return;

        var data = _camera.GetUniversalAdditionalCameraData();
        _savedPostFlag = data.renderPostProcessing;
        data.renderPostProcessing = true;
        _cameraFlagOverridden = true;
    }

    void RestoreCameraFlag()
    {
        if (_cameraFlagOverridden && _camera != null)
            SetPostFlag(_camera, _savedPostFlag);
        _cameraFlagOverridden = false;
        _camera = null;
    }

    static void SetPostFlag(Camera cam, bool enabled)
    {
        var data = cam.GetUniversalAdditionalCameraData();
        if (data.renderPostProcessing != enabled)
            data.renderPostProcessing = enabled;
    }

    /// <summary>
    /// Local Game camera. Camera.main is null in this project (PlayerView is Untagged), so fall back to
    /// the first enabled Game camera — on a client that's the local player's view.
    /// </summary>
    static Camera ResolveViewpoint()
    {
        Camera cam = Camera.main;
        if (cam != null)
            return cam;

        Camera[] cams = Camera.allCameras; // enabled cameras only
        for (int i = 0; i < cams.Length; i++)
        {
            if (cams[i] != null && cams[i].isActiveAndEnabled && cams[i].cameraType == CameraType.Game)
                return cams[i];
        }
        return null;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        // Live-tune the look in play mode from the inspector.
        if (Application.isPlaying && isActiveAndEnabled)
            ApplyAll();
    }
#endif
}
