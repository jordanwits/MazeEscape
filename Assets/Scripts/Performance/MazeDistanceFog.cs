using UnityEngine;

/// <summary>
/// Dense distance fog for the maze levels. A long hallway can be deeper than the
/// <see cref="WorldRenderCuller"/>'s render distance — when it is, the far walls get culled and the
/// player would otherwise stare straight through the gap at the bright skybox. This component fixes
/// that in two coordinated ways:
///
///   1. <b>Geometry fog</b> — enables the legacy <see cref="RenderSettings"/> linear fog (which URP's
///      Lit/SimpleLit/Unlit shaders honour) so distant geometry fades to the fog colour before it is
///      culled. The fog end is placed a little <i>sooner</i> than the cull distance, so anything still
///      drawn at the cull edge is already buried in solid fog and never pops out/in.
///   2. <b>Background fog</b> — legacy fog does NOT touch the skybox, so the culled gap at the end of a
///      hall would still show sky. To hide it, the local player camera is cleared to the same fog
///      colour instead of the skybox. Because the visible geometry has already faded to that exact
///      colour, the seam between "furthest drawn wall" and "empty culled space" disappears — it all
///      reads as one wall of fog.
///
/// Client-side visual only (RenderSettings + the local camera are per-client); the headless server has
/// no camera and simply skips the background override. Sits on the same GameObject as the
/// <see cref="WorldRenderCuller"/> so it can pull the cull distance and keep the fog edge in lockstep.
/// </summary>
[DisallowMultipleComponent]
public class MazeDistanceFog : MonoBehaviour
{
    [Header("Fog")]
    [Tooltip("Master switch. Off restores fog to disabled and releases the camera background override.")]
    [SerializeField] bool enableFog = true;

    [Tooltip("Fog colour. The camera background (the skybox gap at the end of a long hall) is also cleared "
        + "to this colour, so culled geometry reads as fog rather than sky. Keep it close to the level's "
        + "ambient/darkness so the fade looks natural.")]
    [SerializeField] Color fogColor = new(0.035f, 0.035f, 0.045f, 1f);

    [Header("Distance (derived from the render culler)")]
    [Tooltip("Derive the fog start/end from the WorldRenderCuller's cull distance so the fog always finishes "
        + "just before geometry is culled. Turn off to use the manual Start/End below.")]
    [SerializeField] bool deriveFromCuller = true;

    [Tooltip("The culler whose distance the fog tracks. Auto-found on this GameObject (then the scene) if left empty.")]
    [SerializeField] WorldRenderCuller culler;

    [Tooltip("Metres BEFORE the cull distance where fog reaches full density (fog end). Positive = fog finishes "
        + "sooner than the mesh render distance, so the cull edge is hidden inside solid fog.")]
    [SerializeField] float endMarginBeforeCull = 8f;

    [Tooltip("Where the fog starts building, as a fraction of the fog end distance. 0.5 = fog begins at half "
        + "the end distance. Lower = denser (fog reaches deeper toward the player).")]
    [Range(0f, 0.95f)]
    [SerializeField] float startFraction = 0.5f;

    [Header("Distance (manual — used when Derive From Culler is off)")]
    [SerializeField] float manualStartDistance = 25f;
    [SerializeField] float manualEndDistance = 50f;

    [Header("Background")]
    [Tooltip("Clear the local player camera to the fog colour so a hallway longer than the render distance "
        + "shows fog instead of the skybox. Turn off to keep each level's skybox in the distance.")]
    [SerializeField] bool overrideCameraBackground = true;

    [Tooltip("Seconds between checks for the local camera (it appears when the player spawns and can change "
        + "on respawn / character swap).")]
    [SerializeField] float cameraRecheckInterval = 0.5f;

    // Camera-override bookkeeping so the change is fully reversible.
    Camera _camera;
    CameraClearFlags _savedClearFlags;
    Color _savedBackground;
    bool _cameraOverridden;
    float _nextCameraCheck;

    // Zone override (see SetZoneOverride): a room can ask for its own fog while the local player is
    // inside it. Everything still goes through this component so there is exactly one writer of
    // RenderSettings.fog* and of the camera background.
    Component _zoneOwner;
    Color _zoneColor;
    float _zoneStart;
    float _zoneEnd;
    float _blendPerSecond = 1f;
    Color _currentColor;
    float _currentStart;
    float _currentEnd;
    bool _hasCurrent;

    void OnEnable()
    {
        ApplyFog();
        _nextCameraCheck = 0f;
    }

    void OnDisable()
    {
        RestoreCamera();
        _zoneOwner = null;
        _hasCurrent = false;
        // We own the fog toggle for this scene, so leave it off when we go away.
        RenderSettings.fog = false;
    }

    void LateUpdate()
    {
        if (!enableFog)
            return;

        BlendTowardTarget();

        if (!overrideCameraBackground)
            return;

        float now = Time.unscaledTime;
        if (now < _nextCameraCheck)
            return;
        _nextCameraCheck = now + Mathf.Max(0.1f, cameraRecheckInterval);

        EnsureCameraOverride();
    }

    /// <summary>
    /// Hands the fog to a zone (a room whose look needs different fog from the level's — e.g. the dark
    /// Level03 exit hall, which the level's bright haze would otherwise wash out). The change eases in
    /// over <paramref name="blendSeconds"/>. Only one zone owns the fog at a time; a second caller
    /// simply takes over. Pass the same <paramref name="owner"/> to <see cref="ClearZoneOverride"/>.
    /// </summary>
    public void SetZoneOverride(Component owner, Color color, float startDistance, float endDistance, float blendSeconds)
    {
        _zoneOwner = owner;
        _zoneColor = color;
        _zoneStart = Mathf.Max(0f, startDistance);
        _zoneEnd = Mathf.Max(_zoneStart + 0.01f, endDistance);
        _blendPerSecond = 1f / Mathf.Max(0.01f, blendSeconds);
    }

    /// <summary>
    /// Releases a zone override and eases back to the level's own fog. Ignored when another zone has
    /// since taken over, so a player crossing straight from one zone into another never gets a flash
    /// of level fog from the zone they just left.
    /// </summary>
    public void ClearZoneOverride(Component owner, float blendSeconds)
    {
        if (_zoneOwner != owner)
            return;

        _zoneOwner = null;
        _blendPerSecond = 1f / Mathf.Max(0.01f, blendSeconds);
    }

    /// <summary>Pushes the current settings into <see cref="RenderSettings"/>. Safe to call at runtime after tuning.</summary>
    public void ApplyFog()
    {
        if (!enableFog)
        {
            RenderSettings.fog = false;
            RestoreCamera();
            _hasCurrent = false;
            return;
        }

        // Snap rather than blend: this is the level's own fog being (re)applied, not a zone transition.
        ResolveTarget(out _currentColor, out _currentStart, out _currentEnd);
        _hasCurrent = true;
        PushFogToRenderSettings();
    }

    /// <summary>The fog the level currently wants: a zone's if one owns it, otherwise the serialized level fog.</summary>
    void ResolveTarget(out Color color, out float start, out float end)
    {
        if (_zoneOwner != null)
        {
            color = _zoneColor;
            start = _zoneStart;
            end = _zoneEnd;
            return;
        }

        color = fogColor;
        ResolveDistances(out start, out end);
    }

    void BlendTowardTarget()
    {
        ResolveTarget(out Color target, out float targetStart, out float targetEnd);

        if (!_hasCurrent)
        {
            _currentColor = target;
            _currentStart = targetStart;
            _currentEnd = targetEnd;
            _hasCurrent = true;
            PushFogToRenderSettings();
            return;
        }

        float step = _blendPerSecond * Time.unscaledDeltaTime;
        bool settled = Mathf.Abs(_currentStart - targetStart) < 0.01f
            && Mathf.Abs(_currentEnd - targetEnd) < 0.01f
            && Mathf.Abs(_currentColor.r - target.r) + Mathf.Abs(_currentColor.g - target.g) + Mathf.Abs(_currentColor.b - target.b) < 0.002f;
        if (settled)
            return;

        _currentColor = Color.Lerp(_currentColor, target, Mathf.Clamp01(step));
        _currentStart = Mathf.Lerp(_currentStart, targetStart, Mathf.Clamp01(step));
        _currentEnd = Mathf.Lerp(_currentEnd, targetEnd, Mathf.Clamp01(step));
        PushFogToRenderSettings();
    }

    void PushFogToRenderSettings()
    {
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = _currentColor;
        RenderSettings.fogStartDistance = _currentStart;
        RenderSettings.fogEndDistance = _currentEnd;

        // Keep the camera clear colour welded to the fog colour, or the culled gap at the end of a
        // hall would show the previous colour while the fog blends.
        if (_cameraOverridden && _camera != null)
            _camera.backgroundColor = _currentColor;
    }

    void ResolveDistances(out float start, out float end)
    {
        if (deriveFromCuller)
        {
            float cull = ResolveCullDistance();
            end = Mathf.Max(1f, cull - Mathf.Max(0f, endMarginBeforeCull));
            start = end * Mathf.Clamp01(startFraction);
        }
        else
        {
            start = Mathf.Max(0f, manualStartDistance);
            end = Mathf.Max(start + 0.01f, manualEndDistance);
        }
    }

    float ResolveCullDistance()
    {
        if (culler == null)
            culler = GetComponent<WorldRenderCuller>();
        if (culler == null)
            culler = FindAnyObjectByType<WorldRenderCuller>();
        return culler != null ? culler.CullDistance : 60f;
    }

    void EnsureCameraOverride()
    {
        Camera cam = ResolveViewpoint();
        if (cam == _camera)
        {
            // Same camera — make sure our override is still in place (something else may have reset it).
            if (cam != null && _cameraOverridden && cam.clearFlags != CameraClearFlags.SolidColor)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = _currentColor;
            }
            return;
        }

        // Camera changed (first spawn, respawn, character swap) — restore the old one, take over the new.
        RestoreCamera();

        _camera = cam;
        if (_camera == null)
            return;

        _savedClearFlags = _camera.clearFlags;
        _savedBackground = _camera.backgroundColor;
        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.backgroundColor = _hasCurrent ? _currentColor : fogColor;
        _cameraOverridden = true;
    }

    void RestoreCamera()
    {
        if (_cameraOverridden && _camera != null)
        {
            _camera.clearFlags = _savedClearFlags;
            _camera.backgroundColor = _savedBackground;
        }
        _cameraOverridden = false;
        _camera = null;
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
        // Live-tune fog in play mode from the inspector.
        if (Application.isPlaying && isActiveAndEnabled)
            ApplyFog();
    }
#endif
}
