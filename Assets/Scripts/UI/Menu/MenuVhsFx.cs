using UnityEngine;

/// <summary>
/// Drives the VHS full-screen pass while a menu is on screen.
///
/// The pass itself is a <c>FullScreenPassRendererFeature</c> that lives permanently on PC_Renderer
/// (same arrangement as <see cref="MazePostFx"/>'s posterize pass) and bypasses to the untouched
/// image whenever <c>_VhsIntensity</c> is 0. This component raises the intensity on enable and —
/// importantly — drops it back to 0 on disable, because the material is a shared asset loaded from
/// Resources: leaving it hot would carry the tape look straight into gameplay.
///
/// It has to be a camera pass rather than UI: channel splitting and chroma smear re-sample the image
/// behind them, which a Screen Space - Overlay quad cannot do. The overlay canvas draws after all
/// camera rendering, so the menu panels stay crisp over the degraded background.
/// </summary>
[DisallowMultipleComponent]
public sealed class MenuVhsFx : MonoBehaviour
{
    const string MaterialResourcePath = "PostFx/MenuVhs";

    [Tooltip("How much of the tape treatment to mix over the rendered image.")]
    [SerializeField, Range(0f, 1f)] float intensity = 0.7f;

    [Tooltip("Horizontal distance between the red and blue channels, in pixels, at the screen centre.")]
    [SerializeField] float chromaSplit = 2.6f;

    [Tooltip("How far colour drags to the right of an edge, in pixels.")]
    [SerializeField] float smearLength = 24f;

    [SerializeField, Range(0f, 1f)] float smearStrength = 0.65f;

    [Tooltip("Per-scanline horizontal wobble, in pixels.")]
    [SerializeField] float jitterStrength = 2.2f;

    [Tooltip("Slow rolling tape warp, in pixels.")]
    [SerializeField] float warpStrength = 7f;

    [SerializeField, Range(0f, 1f)] float scanlineStrength = 0.11f;
    [SerializeField] float scanlineCount = 240f;
    [SerializeField, Range(0f, 1f)] float noiseStrength = 0.05f;
    [SerializeField, Range(0f, 1f)] float desaturation = 0.2f;
    [SerializeField, Range(0f, 1f)] float vignetteStrength = 0.28f;

    [Tooltip("Strength of the head-switching band that crawls up the frame.")]
    [SerializeField, Range(0f, 1f)] float headSwitch = 0.45f;

    static readonly int IntensityId = Shader.PropertyToID("_VhsIntensity");
    static readonly int ChromaSplitId = Shader.PropertyToID("_ChromaSplit");
    static readonly int SmearLengthId = Shader.PropertyToID("_SmearLength");
    static readonly int SmearStrengthId = Shader.PropertyToID("_SmearStrength");
    static readonly int JitterId = Shader.PropertyToID("_JitterStrength");
    static readonly int WarpId = Shader.PropertyToID("_WarpStrength");
    static readonly int ScanlineStrengthId = Shader.PropertyToID("_ScanlineStrength");
    static readonly int ScanlineCountId = Shader.PropertyToID("_ScanlineCount");
    static readonly int NoiseId = Shader.PropertyToID("_NoiseStrength");
    static readonly int DesaturationId = Shader.PropertyToID("_Desaturation");
    static readonly int VignetteId = Shader.PropertyToID("_VignetteStrength");
    static readonly int HeadSwitchId = Shader.PropertyToID("_HeadSwitch");

    Material _material;
    bool _warnedNoMaterial;

    void OnEnable()
    {
        Apply();
    }

    void OnDisable()
    {
        // Shared Resources material: silence it or the tape look follows us into the level.
        if (TryGetMaterial(out Material mat))
            mat.SetFloat(IntensityId, 0f);
    }

    void OnValidate()
    {
        if (isActiveAndEnabled)
            Apply();
    }

    void Apply()
    {
        if (!TryGetMaterial(out Material mat))
            return;

        mat.SetFloat(IntensityId, intensity);
        mat.SetFloat(ChromaSplitId, chromaSplit);
        mat.SetFloat(SmearLengthId, smearLength);
        mat.SetFloat(SmearStrengthId, smearStrength);
        mat.SetFloat(JitterId, jitterStrength);
        mat.SetFloat(WarpId, warpStrength);
        mat.SetFloat(ScanlineStrengthId, scanlineStrength);
        mat.SetFloat(ScanlineCountId, scanlineCount);
        mat.SetFloat(NoiseId, noiseStrength);
        mat.SetFloat(DesaturationId, desaturation);
        mat.SetFloat(VignetteId, vignetteStrength);
        mat.SetFloat(HeadSwitchId, headSwitch);
    }

    bool TryGetMaterial(out Material mat)
    {
        if (_material == null)
            _material = Resources.Load<Material>(MaterialResourcePath);

        mat = _material;
        if (mat == null && !_warnedNoMaterial)
        {
            _warnedNoMaterial = true;
            Debug.LogWarning(
                $"[{nameof(MenuVhsFx)}] Resources/{MaterialResourcePath} is missing — the menu VHS pass will not run.",
                this);
        }

        return mat != null;
    }
}
