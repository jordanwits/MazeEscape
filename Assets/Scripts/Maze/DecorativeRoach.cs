using UnityEngine;

/// <summary>
/// One decorative cockroach, pooled and owned by <see cref="RoachColony"/>. Purely local and purely
/// cosmetic: no NetworkObject, no collider, no damage.
///
/// The roach is a single textured quad lying flush against whatever surface it was placed on — floor
/// or wall, it makes no difference here, the anchor is just a point plus a normal. It moves in that
/// surface's tangent plane, never in free space, which is why there is no gravity or physics anywhere
/// in this class.
///
/// It is never completely still. <b>Idle</b> alternates short slow crawls with pauses, and even while
/// paused the body keeps a small yaw wobble, because a perfectly frozen roach reads as a texture decal
/// rather than a live insect. <b>Scatter</b> is the panic run when a flashlight finds the colony: fast,
/// straight-ish, away from the beam, then gone.
/// </summary>
[DisallowMultipleComponent]
public class DecorativeRoach : MonoBehaviour
{
    enum RoachState { Idle, Scatter, Gone }

    [Header("Idle crawl")]
    [Tooltip("Speed range of an idle crawl burst, m/s. Motion is the main thing that makes a roach "
        + "noticeable in a dark corridor — a still one is nearly invisible however big it is — so these "
        + "are livelier than a real resting roach.")]
    [SerializeField] Vector2 crawlSpeedRange = new(0.05f, 0.14f);
    [Tooltip("How long one crawl burst lasts, seconds.")]
    [SerializeField] Vector2 crawlDurationRange = new(0.35f, 1.3f);
    [Tooltip("How long the roach sits between crawls, seconds. Keep short — long pauses are what let a "
        + "nest read as floor texture and get walked past.")]
    [SerializeField] Vector2 pauseDurationRange = new(0.2f, 1f);
    [Tooltip("Metres the roach may wander from where it was placed. Keeps idle drift from walking the "
        + "colony off its wall panel over time.")]
    [SerializeField] float leashRadius = 0.16f;

    [Header("Idle wobble")]
    [Tooltip("Degrees of body sway. Applied while paused too — this is what stops a resting roach reading "
        + "as a decal stuck on the wall.")]
    [SerializeField] float idleWobbleDegrees = 11f;
    [Tooltip("Wobble oscillations per second.")]
    [SerializeField] float idleWobbleRate = 2.6f;

    [Header("Scatter")]
    [Tooltip("Speed range of the panic run, m/s.")]
    [SerializeField] Vector2 scatterSpeedRange = new(1.1f, 2.3f);
    [Tooltip("How long the roach runs before vanishing, seconds. Randomised per roach so the colony "
        + "doesn't blink out all at once.")]
    [SerializeField] Vector2 scatterDurationRange = new(0.55f, 1.35f);
    [Tooltip("Seconds spent shrinking to nothing at the end of the run, so the roach reads as scurrying "
        + "into a crack instead of popping out of existence.")]
    [SerializeField] float vanishSeconds = 0.22f;
    [Tooltip("Degrees per second of weave during the run. Roaches don't flee in straight lines.")]
    [SerializeField] float scatterWeaveDegrees = 220f;

    [Header("Surface")]
    [Tooltip("Metres the quad floats off its surface, to stop it z-fighting with the floor or wall.")]
    [SerializeField] float surfaceOffset = 0.004f;
    [Tooltip("Surfaces the roach can crawl on. Used to check it hasn't run off an edge mid-scatter.")]
    [SerializeField] LayerMask surfaceMask = ~0;

    [Header("Grazing-angle tilt")]
    [Tooltip("How far the quad may tip away from its surface toward the viewer, 0-1. A flat quad on the "
        + "floor projects to almost nothing when you look down a corridor, so without tilt the roaches "
        + "disappear at exactly the distance you'd want to notice them. It also matters for lighting: the "
        + "flashlight sits at the player's eye, so a floor-flat quad viewed at a grazing angle catches "
        + "almost no specular — tipping it toward the viewer is what lets the shell glint. At 1 they stand "
        + "fully upright and stop looking attached to the surface.")]
    [SerializeField, Range(0f, 1f)] float maxViewTilt = 0.55f;

    RoachColony _owner;
    MeshRenderer _renderer;

    RoachState _state = RoachState.Gone;
    Vector3 _anchor;          // where it was placed; the leash is measured from here
    Vector3 _surfaceNormal;
    Vector3 _heading;         // unit vector in the surface tangent plane
    Vector3 _baseScale;

    float _speed;
    float _phaseTimer;        // time left in the current crawl or pause
    bool _crawling;
    float _scatterTimeLeft;
    float _wobbleSeed;

    public bool IsActive => _state != RoachState.Gone;
    public bool IsScattering => _state == RoachState.Scatter;

    void Awake()
    {
        _renderer = GetComponentInChildren<MeshRenderer>(true);
        _baseScale = transform.localScale;
    }

    /// <summary>
    /// Anchors the roach to a surface point. <paramref name="normal"/> is the surface normal — up for a
    /// floor, horizontal for a wall — and everything else is derived from it.
    /// </summary>
    public void Place(RoachColony owner, Vector3 position, Vector3 normal)
    {
        _owner = owner;
        _surfaceNormal = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;
        _anchor = position;
        _wobbleSeed = Random.value * 100f;

        // Any direction in the tangent plane will do for a starting heading.
        Vector3 seed = Mathf.Abs(Vector3.Dot(_surfaceNormal, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
        _heading = Vector3.ProjectOnPlane(Quaternion.AngleAxis(Random.value * 360f, _surfaceNormal) * seed,
            _surfaceNormal).normalized;

        transform.position = position + _surfaceNormal * surfaceOffset;
        transform.localScale = _baseScale;

        _state = RoachState.Idle;
        BeginPause();
        gameObject.SetActive(true);
        ApplyOrientation();
    }

    /// <summary>Kicks the roach into its panic run, heading away from <paramref name="threatPoint"/>.</summary>
    public void Scatter(Vector3 threatPoint)
    {
        if (_state != RoachState.Idle)
            return;

        Vector3 away = Vector3.ProjectOnPlane(transform.position - threatPoint, _surfaceNormal);
        _heading = away.sqrMagnitude > 0.0001f ? away.normalized : _heading;

        // Fan the colony out rather than sending every roach along the same escape vector.
        _heading = (Quaternion.AngleAxis(Random.Range(-55f, 55f), _surfaceNormal) * _heading).normalized;

        _speed = Random.Range(scatterSpeedRange.x, scatterSpeedRange.y);
        _scatterTimeLeft = Random.Range(scatterDurationRange.x, scatterDurationRange.y);
        _state = RoachState.Scatter;
    }

    void Update()
    {
        switch (_state)
        {
            case RoachState.Idle:
                TickIdle(Time.deltaTime);
                break;
            case RoachState.Scatter:
                TickScatter(Time.deltaTime);
                break;
            default:
                return;
        }

        ApplyOrientation();
    }

    void TickIdle(float dt)
    {
        _phaseTimer -= dt;
        if (_phaseTimer <= 0f)
        {
            if (_crawling)
                BeginPause();
            else
                BeginCrawl();
        }

        if (!_crawling)
            return;

        Vector3 step = _heading * (_speed * dt);
        Vector3 next = transform.position + step;

        // Leash: if the crawl would take it too far from where it spawned, turn back toward the anchor
        // instead of stopping dead — a roach that reverses mid-stride looks more alive than one that
        // freezes at an invisible boundary.
        Vector3 fromAnchor = Vector3.ProjectOnPlane(next - _anchor, _surfaceNormal);
        if (fromAnchor.magnitude > leashRadius)
        {
            _heading = Vector3.ProjectOnPlane(-fromAnchor, _surfaceNormal).normalized;
            _heading = (Quaternion.AngleAxis(Random.Range(-40f, 40f), _surfaceNormal) * _heading).normalized;
            return;
        }

        transform.position = next;
    }

    void TickScatter(float dt)
    {
        _scatterTimeLeft -= dt;

        if (_scatterTimeLeft <= 0f)
        {
            Finish();
            return;
        }

        _heading = (Quaternion.AngleAxis(
            Mathf.Sin(Time.time * 9f + _wobbleSeed) * scatterWeaveDegrees * dt, _surfaceNormal) * _heading)
            .normalized;

        Vector3 next = transform.position + _heading * (_speed * dt);

        // Confirm the surface is still under it. Running off the edge of a wall panel or into a doorway
        // gap is exactly when a roach should disappear, so a miss ends the roach rather than being
        // treated as an error.
        Vector3 probeOrigin = next + _surfaceNormal * 0.05f;
        if (!Physics.Raycast(probeOrigin, -_surfaceNormal, out RaycastHit hit, 0.16f,
                surfaceMask, QueryTriggerInteraction.Ignore))
        {
            Finish();
            return;
        }

        transform.position = hit.point + _surfaceNormal * surfaceOffset;

        if (vanishSeconds > 0f && _scatterTimeLeft < vanishSeconds)
            transform.localScale = _baseScale * Mathf.Clamp01(_scatterTimeLeft / vanishSeconds);
    }

    void BeginCrawl()
    {
        _crawling = true;
        _phaseTimer = Random.Range(crawlDurationRange.x, crawlDurationRange.y);
        _speed = Random.Range(crawlSpeedRange.x, crawlSpeedRange.y);
        _heading = (Quaternion.AngleAxis(Random.Range(-120f, 120f), _surfaceNormal) * _heading).normalized;
    }

    void BeginPause()
    {
        _crawling = false;
        _phaseTimer = Random.Range(pauseDurationRange.x, pauseDurationRange.y);
    }

    /// <summary>
    /// Aligns the quad to its surface, adds the idle sway, then tips it partway toward the viewer.
    /// </summary>
    void ApplyOrientation()
    {
        Vector3 up = _surfaceNormal;

        Transform viewer = _owner != null ? _owner.ViewerTransform : null;
        if (viewer != null && maxViewTilt > 0f)
        {
            Vector3 toViewer = viewer.position - transform.position;
            if (toViewer.sqrMagnitude > 0.0001f)
            {
                toViewer.Normalize();

                // 1 when the viewer is edge-on to the surface, 0 when looking straight down at it.
                float grazing = 1f - Mathf.Abs(Vector3.Dot(toViewer, _surfaceNormal));
                up = Vector3.Slerp(_surfaceNormal, toViewer, grazing * maxViewTilt).normalized;
            }
        }

        float wobble = Mathf.Sin(Time.time * idleWobbleRate + _wobbleSeed) * idleWobbleDegrees;
        Vector3 forward = Vector3.ProjectOnPlane(_heading, up);
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.ProjectOnPlane(transform.forward, up);
        if (forward.sqrMagnitude < 0.0001f)
            return;

        Quaternion rotation = Quaternion.LookRotation(forward.normalized, up);
        if (_state == RoachState.Idle)
            rotation = Quaternion.AngleAxis(wobble, up) * rotation;

        transform.rotation = rotation;
    }

    void Finish()
    {
        _state = RoachState.Gone;
        transform.localScale = _baseScale;
        gameObject.SetActive(false);

        if (_owner != null)
            _owner.NotifyRoachFinished(this);
    }
}
