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

    void OnEnable()
    {
        ApplyFog();
        _nextCameraCheck = 0f;
    }

    void OnDisable()
    {
        RestoreCamera();
        // We own the fog toggle for this scene, so leave it off when we go away.
        RenderSettings.fog = false;
    }

    void LateUpdate()
    {
        if (!enableFog || !overrideCameraBackground)
            return;

        float now = Time.unscaledTime;
        if (now < _nextCameraCheck)
            return;
        _nextCameraCheck = now + Mathf.Max(0.1f, cameraRecheckInterval);

        EnsureCameraOverride();
    }

    /// <summary>Pushes the current settings into <see cref="RenderSettings"/>. Safe to call at runtime after tuning.</summary>
    public void ApplyFog()
    {
        if (!enableFog)
        {
            RenderSettings.fog = false;
            RestoreCamera();
            return;
        }

        ResolveDistances(out float start, out float end);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = fogColor;
        RenderSettings.fogStartDistance = start;
        RenderSettings.fogEndDistance = end;

        // Re-apply the fog colour to any camera we've already taken over (e.g. colour tweaked at runtime).
        if (_cameraOverridden && _camera != null)
            _camera.backgroundColor = fogColor;
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
                cam.backgroundColor = fogColor;
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
        _camera.backgroundColor = fogColor;
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
