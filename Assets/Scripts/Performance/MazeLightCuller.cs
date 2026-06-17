using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime light culler for the procedurally-assembled maze. The carnival pieces and
/// props ship with ~100 realtime point/spot lights; in a maze you only ever see a
/// handful at once. This component keeps every authored light but toggles
/// <c>Light.enabled</c> by distance to the local view camera, so URP only shades the
/// lights near the player.
///
/// Non-destructive: it never edits ranges, intensities or the scene/prefab assets — it
/// only flips the runtime enabled flag. Lights re-light automatically as the player
/// approaches. Self-discovers lights on an interval so it works with pieces that spawn
/// in over time.
///
/// Excluded automatically: Directional lights (the sun), anything under a
/// <c>PlayerController</c> (the flashlight, which manages its own enabled state), and
/// anything carrying a <see cref="MazeLightCullIgnore"/> marker.
/// </summary>
[DisallowMultipleComponent]
public class MazeLightCuller : MonoBehaviour
{
    [Header("Activation")]
    [Tooltip("Metres added to each light's own range before it switches on. Larger = lights fade in earlier (smoother) but more stay lit at once.")]
    [SerializeField] float activationBuffer = 6f;

    [Tooltip("Hard cap on activation distance, so very large-range lights (e.g. room lights) still get culled at a sane distance.")]
    [SerializeField] float maxActivationDistance = 35f;

    [Header("Timing")]
    [Tooltip("Seconds between distance evaluations. Walking speed doesn't need an every-frame pass.")]
    [SerializeField] float updateInterval = 0.15f;

    [Tooltip("Seconds between rescans that pick up newly spawned maze lights.")]
    [SerializeField] float rescanInterval = 2f;

    [Header("Scope")]
    [Tooltip("Also manage Directional lights. Leave OFF — the directional/sun should normally stay on.")]
    [SerializeField] bool includeDirectional = false;

    readonly List<Light> _lights = new List<Light>();
    readonly List<Transform> _transforms = new List<Transform>();
    readonly List<float> _activateSqr = new List<float>();

    Transform _viewpoint;
    float _nextUpdate;
    float _nextRescan;

    void OnEnable()
    {
        _nextUpdate = 0f;
        _nextRescan = 0f;
        Rescan();
    }

    void LateUpdate()
    {
        float now = Time.unscaledTime;

        if (now >= _nextRescan)
        {
            _nextRescan = now + Mathf.Max(0.25f, rescanInterval);
            Rescan();
        }

        if (now < _nextUpdate)
            return;
        _nextUpdate = now + Mathf.Max(0.02f, updateInterval);

        Transform vp = ResolveViewpoint();
        if (vp == null)
            return; // no local camera yet (or headless server) — leave lights as authored.

        Vector3 eye = vp.position;
        for (int i = 0; i < _lights.Count; i++)
        {
            Light l = _lights[i];
            if (l == null)
                continue;

            float distSqr = (_transforms[i].position - eye).sqrMagnitude;
            bool shouldBeOn = distSqr <= _activateSqr[i];
            if (l.enabled != shouldBeOn)
                l.enabled = shouldBeOn;
        }
    }

    /// <summary>Rebuilds the managed-light list from the current scene.</summary>
    public void Rescan()
    {
        _lights.Clear();
        _transforms.Clear();
        _activateSqr.Clear();

        Light[] all = Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
        for (int i = 0; i < all.Length; i++)
        {
            Light l = all[i];
            if (l == null)
                continue;
            if (l.type == LightType.Directional && !includeDirectional)
                continue;
            if (l.GetComponentInParent<MazeLightCullIgnore>() != null)
                continue;
            // Player-attached lights (flashlight) manage their own enabled state.
            if (l.GetComponentInParent<PlayerController>() != null)
                continue;

            float activate = Mathf.Min(l.range + activationBuffer, maxActivationDistance);
            _lights.Add(l);
            _transforms.Add(l.transform);
            _activateSqr.Add(activate * activate);
        }
    }

    Transform ResolveViewpoint()
    {
        if (_viewpoint != null && _viewpoint.gameObject.activeInHierarchy)
            return _viewpoint;

        // Camera.main is null in this project (PlayerView is Untagged), so fall back to
        // the enabled Game camera — on a client that's the local player's view camera.
        Camera cam = Camera.main;
        if (cam == null)
        {
            Camera[] cams = Camera.allCameras; // enabled cameras only
            for (int i = 0; i < cams.Length; i++)
            {
                if (cams[i] != null && cams[i].isActiveAndEnabled && cams[i].cameraType == CameraType.Game)
                {
                    cam = cams[i];
                    break;
                }
            }
        }

        _viewpoint = cam != null ? cam.transform : null;
        return _viewpoint;
    }
}
