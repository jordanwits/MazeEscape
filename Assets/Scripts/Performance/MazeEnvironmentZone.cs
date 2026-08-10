using UnityEngine;

/// <summary>
/// A room-sized box that takes over the level's environment lighting and distance fog while THIS
/// peer's player is inside it.
///
/// The Severance level is lit "sourceless": no realtime lights in the pieces, just a very bright
/// Trilight ambient (sky is pure white) plus a white haze fog. That is what makes the porcelain
/// corridors glow — and it is also what makes a "dark" room impossible by material alone: a wall
/// painted 7% grey still renders at 7% of a full-strength white sky, and the floor, facing straight
/// up into the sky colour, reads as mid-grey. Darkening the albedo further is a trap, because it dims
/// the room's own lights by exactly the same factor.
///
/// So the dark exit hall turns the ambient itself down while you are inside it, and puts the fog back
/// to near-black so the far end doesn't bleach out. What is left to light the hall is what the piece
/// carries: the alarm beacons and the lamp over the elevator.
///
/// Client-side visual only — RenderSettings and the camera are per-peer, so each peer runs this
/// against its own local player and nothing is networked. The containment test is a plain point-in-box
/// against the local player's position (no trigger collider, so it can't be blocked by layers or
/// skipped by a fast move). Fog goes through <see cref="MazeDistanceFog"/> so that component stays the
/// only writer of <see cref="RenderSettings"/> fog state; ambient is written here and restored exactly
/// as captured.
///
/// Authored for levels using Trilight/flat ambient (Level03). On a skybox-ambient level the mode is
/// forced to Trilight while the zone is active, which is a step rather than a blend at the boundary.
/// </summary>
[DisallowMultipleComponent]
public class MazeEnvironmentZone : MonoBehaviour
{
    [Header("Volume (local space)")]
    [Tooltip("Centre of the zone box, in this object's local space.")]
    [SerializeField] Vector3 volumeCenter = Vector3.zero;
    [Tooltip("Size of the zone box, in this object's local space. Keep it inside the room's walls.")]
    [SerializeField] Vector3 volumeSize = new(5f, 6.5f, 37f);

    [Header("Ambient while inside")]
    [Tooltip("Scale the level's authored ambient light down to this fraction while the player is inside. "
        + "0 = pitch black (only the room's own lights), 1 = the level's normal ambient. This is what makes "
        + "the room dark; do NOT reach for darker albedo instead, that dims the room's lights too.")]
    [SerializeField, Range(0f, 1f)] float ambientScale = 0.08f;
    [Tooltip("Optional tint on the dimmed ambient — e.g. push it slightly blue so the residual light reads cold "
        + "against the red alarms. White keeps the level's own hue.")]
    [SerializeField] Color ambientTint = Color.white;
    [Tooltip("Scale the level's environment reflection down to this fraction while inside. Level03 reflects a "
        + "plain white cubemap, and a corridor floor seen at a grazing angle reflects it at nearly full "
        + "strength (Fresnel) — that alone keeps the floor bright no matter how dark the ambient or the albedo.")]
    [SerializeField, Range(0f, 1f)] float reflectionScale = 0.06f;

    [Header("Fog while inside")]
    [Tooltip("Fog colour inside the zone. Distant geometry — and the camera background behind culled "
        + "geometry — fades to this instead of the level's fog colour.")]
    [SerializeField] Color fogColor = new(0.008f, 0.008f, 0.011f, 1f);
    [Tooltip("Distance where the zone's fog starts building.")]
    [SerializeField] float fogStartDistance = 30f;
    [Tooltip("Distance where the zone's fog is solid. Keep this beyond the room's longest sightline or "
        + "the far end of the room disappears into fog.")]
    [SerializeField] float fogEndDistance = 58f;

    [Header("Blend")]
    [Tooltip("Seconds to ease between the level's environment and this zone's, in both directions.")]
    [SerializeField, Min(0.01f)] float blendSeconds = 1.2f;
    [Tooltip("Seconds between inside/outside checks. The blend smooths over the gap, so this can stay coarse.")]
    [SerializeField, Min(0.02f)] float checkInterval = 0.1f;

    MazeDistanceFog _fog;
    bool _inside;
    float _nextCheck;
    float _blend;               // 0 = level environment, 1 = this zone's

    // The level's own environment, captured before we ever write to it so it can be restored exactly.
    bool _baseCaptured;
    UnityEngine.Rendering.AmbientMode _baseAmbientMode;
    Color _baseSky;
    Color _baseEquator;
    Color _baseGround;
    float _baseReflection;

    void OnDisable()
    {
        if (_fog != null)
            _fog.ClearZoneOverride(this, 0f);
        if (_baseCaptured)
            RestoreEnvironment();
        _inside = false;
        _blend = 0f;
    }

    void Update()
    {
        float now = Time.unscaledTime;
        if (now >= _nextCheck)
        {
            _nextCheck = now + Mathf.Max(0.02f, checkInterval);
            UpdateInsideState();
        }

        UpdateBlend();
    }

    void UpdateInsideState()
    {
        bool inside = IsLocalPlayerInside();
        if (inside == _inside)
            return;

        _inside = inside;
        if (!ResolveFog())
            return;

        if (inside)
            _fog.SetZoneOverride(this, fogColor, fogStartDistance, fogEndDistance, blendSeconds);
        else
            _fog.ClearZoneOverride(this, blendSeconds);
    }

    void UpdateBlend()
    {
        float target = _inside ? 1f : 0f;
        if (Mathf.Approximately(_blend, target))
            return;

        // Capture at the start of every fade-in rather than once: the brightness slider
        // (GameDisplayBrightness) can move reflection intensity between visits, and we want to give
        // back what the level had, not what it had the first time.
        if (_blend <= 0f)
            CaptureBase();

        _blend = Mathf.MoveTowards(_blend, target, Time.unscaledDeltaTime / Mathf.Max(0.01f, blendSeconds));
        ApplyEnvironment(_blend);

        if (_blend <= 0f)
            RestoreEnvironment();
    }

    void CaptureBase()
    {
        _baseAmbientMode = RenderSettings.ambientMode;
        _baseSky = RenderSettings.ambientSkyColor;
        _baseEquator = RenderSettings.ambientEquatorColor;
        _baseGround = RenderSettings.ambientGroundColor;
        _baseReflection = RenderSettings.reflectionIntensity;
        _baseCaptured = true;
    }

    void ApplyEnvironment(float t)
    {
        Color sky = Color.Lerp(_baseSky, ScaleAmbient(_baseSky), t);
        Color equator = Color.Lerp(_baseEquator, ScaleAmbient(_baseEquator), t);
        Color ground = Color.Lerp(_baseGround, ScaleAmbient(_baseGround), t);

        // Trilight is the only mode where writing these three colours is the whole story.
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = sky;
        RenderSettings.ambientEquatorColor = equator;
        RenderSettings.ambientGroundColor = ground;
        RenderSettings.reflectionIntensity = Mathf.Lerp(_baseReflection, _baseReflection * Mathf.Clamp01(reflectionScale), t);
    }

    Color ScaleAmbient(Color source)
    {
        Color scaled = source * Mathf.Clamp01(ambientScale);
        return new Color(scaled.r * ambientTint.r, scaled.g * ambientTint.g, scaled.b * ambientTint.b, source.a);
    }

    void RestoreEnvironment()
    {
        if (!_baseCaptured)
            return;

        RenderSettings.ambientMode = _baseAmbientMode;
        RenderSettings.ambientSkyColor = _baseSky;
        RenderSettings.ambientEquatorColor = _baseEquator;
        RenderSettings.ambientGroundColor = _baseGround;
        RenderSettings.reflectionIntensity = _baseReflection;
    }

    bool ResolveFog()
    {
        if (_fog == null)
            _fog = FindAnyObjectByType<MazeDistanceFog>();
        return _fog != null;
    }

    bool IsLocalPlayerInside()
    {
        PlayerController local = ResolveLocalPlayer();
        if (local == null)
            return false;

        Vector3 localPoint = transform.InverseTransformPoint(local.transform.position) - volumeCenter;
        Vector3 half = volumeSize * 0.5f;
        return Mathf.Abs(localPoint.x) <= half.x
            && Mathf.Abs(localPoint.y) <= half.y
            && Mathf.Abs(localPoint.z) <= half.z;
    }

    /// <summary>The player this peer is driving, or null before the local avatar spawns.</summary>
    static PlayerController ResolveLocalPlayer()
    {
        var players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null)
                continue;
            PlayerController controller = player.GetComponent<PlayerController>();
            if (controller != null && controller.HasLocalControl)
                return controller;
        }
        return null;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.35f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(volumeCenter, volumeSize);
    }
#endif
}
