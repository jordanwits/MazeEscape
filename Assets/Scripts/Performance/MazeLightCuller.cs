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
/// only flips the runtime enabled flag, plus the shadow type when
/// <see cref="manageShadows"/> is on (restored on disable). Lights re-light automatically
/// as the player approaches. Self-discovers lights on an interval so it works with pieces
/// that spawn in over time.
///
/// Excluded automatically: Directional lights (the sun), anything under a
/// <c>PlayerController</c> (the flashlight, which manages its own enabled state), and
/// anything carrying a <see cref="MazeLightCullIgnore"/> marker.
/// </summary>
[DisallowMultipleComponent]
public class MazeLightCuller : MonoBehaviour
{
    [Header("Activation")]
    [Tooltip("Multiplies each light's own range when deciding how far away it switches on. 1 = switch on only " +
             "once its lit pool could reach you. Raise it to see lit fixtures far down a long sightline — a light " +
             "you can SEE the effect of is useful long before it could reach you. Because it scales with range, " +
             "big room lights carry further than small fill lights instead of all of them extending together.")]
    [SerializeField] float activationRangeMultiplier = 1f;

    [Tooltip("Metres added to each light's own range before it switches on. Larger = lights fade in earlier (smoother) but more stay lit at once.")]
    [SerializeField] float activationBuffer = 6f;

    [Tooltip("Hard cap on activation distance, so very large-range lights (e.g. room lights) still get culled at a sane distance.")]
    [SerializeField] float maxActivationDistance = 35f;

    [Header("Timing")]
    [Tooltip("Seconds between distance evaluations. Walking speed doesn't need an every-frame pass.")]
    [SerializeField] float updateInterval = 0.15f;

    [Tooltip("Seconds between rescans that pick up newly spawned maze lights.")]
    [SerializeField] float rescanInterval = 2f;

    [Header("Shadows")]
    [Tooltip("Drop shadow casting on lights beyond shadowDistance, restoring it as the player gets close. " +
             "Lets a level author shadow-casting lights everywhere without paying for every one of them at once. " +
             "OFF leaves each light's authored shadow setting completely alone.")]
    [SerializeField] bool manageShadows = false;

    [Tooltip("Metres within which a light keeps its authored shadows. Beyond this it still lights the scene " +
             "but stops casting, which is far cheaper and hard to notice at distance.")]
    [SerializeField] float shadowDistance = 20f;

    [Header("View cone")]
    [Tooltip("Only keep distant lights on when they are roughly in front of the camera. Distance alone is a " +
             "sphere, so a long activation distance switches on every light around and behind the player — " +
             "including through walls — and a big maze blows past URP's visible-additional-light budget. " +
             "Gating on direction is what makes a long activation distance affordable.")]
    [SerializeField] bool useViewCone = false;

    [Tooltip("Degrees added to the camera's own half-FOV before a light is considered off-screen. Generous " +
             "margin matters: a light whose centre is just outside the frustum can still spill into view.")]
    [SerializeField] float viewConeMarginDegrees = 35f;

    [Tooltip("Lights closer than this stay on regardless of direction — they light the room you are standing " +
             "in, which you see the effects of even when the source is behind you.")]
    [SerializeField] float alwaysOnRadius = 12f;

    [Header("Scope")]
    [Tooltip("Also manage Directional lights. Leave OFF — the directional/sun should normally stay on.")]
    [SerializeField] bool includeDirectional = false;

    readonly List<Light> _lights = new List<Light>();
    readonly List<Transform> _transforms = new List<Transform>();
    readonly List<float> _activateSqr = new List<float>();

    /// <summary>
    /// Each managed light's shadow setting as authored. This has to survive <see cref="Rescan"/>:
    /// once we have forced a distant light to <c>LightShadows.None</c>, re-reading <c>light.shadows</c>
    /// on the next rescan would capture our own override as if it were the authored value and the real
    /// setting would be lost for good. So a light's authored value is recorded once, the first time it
    /// is seen, and only forgotten when the light itself is destroyed.
    /// </summary>
    readonly Dictionary<Light, LightShadows> _authoredShadows = new Dictionary<Light, LightShadows>();
    readonly List<Light> _shadowPruneScratch = new List<Light>();

    Transform _viewpoint;
    Camera _viewCamera;
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
        Vector3 forward = vp.forward;
        float shadowSqr = shadowDistance * shadowDistance;
        float alwaysOnSqr = alwaysOnRadius * alwaysOnRadius;

        // Widen the camera's vertical FOV to its horizontal equivalent before adding the margin —
        // a 16:9 view is much wider than tall, and gating on the vertical angle would cut lights
        // that are plainly on screen at the left and right edges.
        float cutoffCos = -1f;
        if (useViewCone)
        {
            float halfFov = 60f;
            if (_viewCamera != null)
            {
                float vHalf = _viewCamera.fieldOfView * 0.5f;
                float hHalf = Mathf.Atan(Mathf.Tan(vHalf * Mathf.Deg2Rad) * Mathf.Max(0.1f, _viewCamera.aspect)) * Mathf.Rad2Deg;
                halfFov = Mathf.Max(vHalf, hHalf);
            }
            cutoffCos = Mathf.Cos(Mathf.Min(179f, halfFov + viewConeMarginDegrees) * Mathf.Deg2Rad);
        }

        for (int i = 0; i < _lights.Count; i++)
        {
            Light l = _lights[i];
            if (l == null)
                continue;

            Vector3 toLight = _transforms[i].position - eye;
            float distSqr = toLight.sqrMagnitude;

            // A light dimmed to zero by a flicker effect or a dead fixture emits nothing, so keep it
            // switched off however close it is. Without this the culler fights the flicker: it would
            // re-enable a light mid-dropout on its next pass and hold a renderer slot open for a light
            // contributing no photons. Restoring intensity is what brings it back, which the flicker
            // does in the same frame it re-enables the light.
            bool shouldBeOn = distSqr <= _activateSqr[i] && l.intensity > 0f;

            if (shouldBeOn && useViewCone && distSqr > alwaysOnSqr)
            {
                // dot(normalize(toLight), forward) < cutoffCos, rearranged to one sqrt and no divide.
                float dist = Mathf.Sqrt(distSqr);
                if (Vector3.Dot(toLight, forward) < cutoffCos * dist)
                    shouldBeOn = false;
            }

            if (l.enabled != shouldBeOn)
                l.enabled = shouldBeOn;

            if (!manageShadows)
                continue;
            if (!_authoredShadows.TryGetValue(l, out LightShadows authored) || authored == LightShadows.None)
                continue; // authored shadowless — nothing to manage.

            LightShadows want = distSqr <= shadowSqr ? authored : LightShadows.None;
            if (l.shadows != want)
                l.shadows = want;
        }
    }

    void OnDisable()
    {
        // Hand every light back exactly as authored, so switching the culler off (or a level teardown)
        // never leaves the scene permanently shadowless.
        if (!manageShadows)
            return;

        foreach (KeyValuePair<Light, LightShadows> entry in _authoredShadows)
        {
            if (entry.Key != null)
                entry.Key.shadows = entry.Value;
        }
    }

    /// <summary>Rebuilds the managed-light list from the current scene.</summary>
    public void Rescan()
    {
        _lights.Clear();
        _transforms.Clear();
        _activateSqr.Clear();
        PruneDestroyedShadowEntries();

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

            float activate = Mathf.Min(l.range * Mathf.Max(0f, activationRangeMultiplier) + activationBuffer, maxActivationDistance);
            _lights.Add(l);
            _transforms.Add(l.transform);
            _activateSqr.Add(activate * activate);

            // First sighting of this light is the only chance to read its authored shadow setting.
            if (!_authoredShadows.ContainsKey(l))
                _authoredShadows.Add(l, l.shadows);
        }
    }

    /// <summary>Drops entries whose Light has been destroyed (every maze rebuild replaces the pieces).</summary>
    void PruneDestroyedShadowEntries()
    {
        if (_authoredShadows.Count == 0)
            return;

        _shadowPruneScratch.Clear();
        foreach (KeyValuePair<Light, LightShadows> entry in _authoredShadows)
        {
            if (entry.Key == null)
                _shadowPruneScratch.Add(entry.Key);
        }

        for (int i = 0; i < _shadowPruneScratch.Count; i++)
            _authoredShadows.Remove(_shadowPruneScratch[i]);
        _shadowPruneScratch.Clear();
    }

    Transform ResolveViewpoint()
    {
        if (_viewpoint != null && _viewpoint.gameObject.activeInHierarchy)
            return _viewpoint;

        _viewCamera = null;

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

        _viewCamera = cam;
        _viewpoint = cam != null ? cam.transform : null;
        return _viewpoint;
    }
}
