using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Physics-trigger based tripwire used by <see cref="PivotSwingTrap"/>.
/// Attach to the child transform whose orientation defines the tripwire (typically the TripWire transform on the trap prefab).
/// A trigger Collider (capsule by default, or box) is auto-configured in this transform's LOCAL space, so the volume
/// rotates / flips correctly when the trap is placed sideways or upside-down by the maze generator.
/// </summary>
/// <remarks>
/// Replaces the previous manual <see cref="Physics.OverlapSphere"/> scan used by PivotSwingTrap. The scan was unreliable
/// inside the procedural maze because the overlap buffer filled up with walls/props and the player could be silently
/// dropped. A native trigger has no such buffer, fires from Unity's broadphase directly, and scales with transform
/// orientation automatically.
/// </remarks>
[DisallowMultipleComponent]
public class TripwireZone : MonoBehaviour
{
    public enum VolumeShape
    {
        [Tooltip("Vertical capsule along local up. Best match for a tripwire because the horizontal reach equals inPlaneRadius in every direction (no 45° \"corner\" overshoot). Uses inPlaneRadius and verticalHalfExtent; boxHalfExtents is ignored.")]
        Capsule,
        [Tooltip("Axis-aligned box in local space. Slightly larger reach at the corners; use when the tripwire should fire the moment a target enters a rectangular footprint. Uses boxHalfExtents; inPlaneRadius / verticalHalfExtent are ignored.")]
        Box,
    }

    [Header("Volume (local space of this transform)")]
    [SerializeField] VolumeShape shape = VolumeShape.Capsule;
    [Tooltip("Capsule: horizontal reach in every direction (in meters). Matches the old tripwire disc radius 1:1, no corner overshoot.")]
    [SerializeField] float inPlaneRadius = 1.5f;
    [Tooltip("Capsule: half the total vertical height along local up (in meters). Full height = 2 × this value. The capsule's rounded caps live inside this span.")]
    [SerializeField] float verticalHalfExtent = 6f;
    [Tooltip("Box mode only: half-size in local space. X/Z are the in-plane reach; Y is the vertical half-extent.")]
    [SerializeField] Vector3 boxHalfExtents = new Vector3(1.5f, 6f, 1.5f);
    [Tooltip("Local offset of the volume center relative to this transform.")]
    [SerializeField] Vector3 localCenter = Vector3.zero;

    [Header("Auto setup")]
    [Tooltip("Add / maintain the appropriate trigger collider (Capsule or Box) on this GameObject to match the values above.")]
    [SerializeField] bool autoConfigureCollider = true;
    [Tooltip("Add a kinematic Rigidbody so OnTrigger events also fire with colliders that are moved via transform rather than physics (e.g. some AI controllers). Recommended.")]
    [SerializeField] bool addKinematicRigidbody = true;
    [Tooltip("On Start, run a single overlap check to pre-populate tracked targets that might already be inside the volume when the trap spawns (e.g. a maze generator placing a trap near an idle player).")]
    [SerializeField] bool primeOnStart = true;

    [Header("Filtering")]
    [Tooltip("Layers considered for priming the occupancy set. 0 uses Physics.DefaultRaycastLayers. The \"Enemy\" layer is always added when present so zombies are picked up. Runtime trigger events are not filtered by this mask; Unity's layer collision matrix controls that.")]
    [SerializeField] LayerMask detectionMask;

    readonly HashSet<PlayerHealth> _players = new();
    readonly HashSet<ZombieHealth> _zombies = new();

    CapsuleCollider _capsule;
    BoxCollider _box;
    Rigidbody _rigidbody;

    /// <summary>True if at least one living player or zombie is currently inside the tripwire volume.</summary>
    public bool HasQualifyingTarget
    {
        get
        {
            Prune();
            return _players.Count > 0 || _zombies.Count > 0;
        }
    }

    /// <summary>Living players currently inside the volume. Safe to iterate but do not mutate.</summary>
    public IReadOnlyCollection<PlayerHealth> PlayersInside
    {
        get
        {
            Prune();
            return _players;
        }
    }

    /// <summary>Living zombies currently inside the volume. Safe to iterate but do not mutate.</summary>
    public IReadOnlyCollection<ZombieHealth> ZombiesInside
    {
        get
        {
            Prune();
            return _zombies;
        }
    }

    public VolumeShape Shape
    {
        get => shape;
        set
        {
            shape = value;
            RebuildCollider();
        }
    }

    public float InPlaneRadius
    {
        get => inPlaneRadius;
        set
        {
            inPlaneRadius = Mathf.Max(0.01f, value);
            ApplyColliderConfig();
        }
    }

    public float VerticalHalfExtent
    {
        get => verticalHalfExtent;
        set
        {
            verticalHalfExtent = Mathf.Max(0.01f, value);
            ApplyColliderConfig();
        }
    }

    public Vector3 BoxHalfExtents
    {
        get => boxHalfExtents;
        set
        {
            boxHalfExtents = new Vector3(
                Mathf.Max(0.01f, value.x),
                Mathf.Max(0.01f, value.y),
                Mathf.Max(0.01f, value.z));
            ApplyColliderConfig();
        }
    }

    public Vector3 LocalCenter
    {
        get => localCenter;
        set
        {
            localCenter = value;
            ApplyColliderConfig();
        }
    }

    void Reset()
    {
        EnsureComponents();
    }

    void Awake()
    {
        EnsureComponents();
    }

    void Start()
    {
        if (primeOnStart)
            PrimeOccupancy();
    }

    void OnEnable()
    {
        _players.Clear();
        _zombies.Clear();
    }

    void OnDisable()
    {
        _players.Clear();
        _zombies.Clear();
    }

    void OnValidate()
    {
        // Re-sync existing colliders in-editor without spawning new components.
        if (_capsule == null)
            _capsule = GetComponent<CapsuleCollider>();
        if (_box == null)
            _box = GetComponent<BoxCollider>();
        ApplyColliderConfig();
    }

    void EnsureComponents()
    {
        if (autoConfigureCollider)
            RebuildCollider();
        else
        {
            _capsule = GetComponent<CapsuleCollider>();
            _box = GetComponent<BoxCollider>();
            if (_capsule != null) _capsule.isTrigger = true;
            if (_box != null) _box.isTrigger = true;
        }

        if (addKinematicRigidbody)
        {
            _rigidbody = GetComponent<Rigidbody>();
            if (_rigidbody == null)
                _rigidbody = gameObject.AddComponent<Rigidbody>();
            _rigidbody.isKinematic = true;
            _rigidbody.useGravity = false;
            _rigidbody.interpolation = RigidbodyInterpolation.None;
            _rigidbody.collisionDetectionMode = CollisionDetectionMode.Discrete;
        }
    }

    void RebuildCollider()
    {
        _capsule = GetComponent<CapsuleCollider>();
        _box = GetComponent<BoxCollider>();

        if (shape == VolumeShape.Capsule)
        {
            if (_box != null)
            {
                if (Application.isPlaying) Destroy(_box);
                else DestroyImmediate(_box);
                _box = null;
            }
            if (_capsule == null)
                _capsule = gameObject.AddComponent<CapsuleCollider>();
            _capsule.isTrigger = true;
        }
        else
        {
            if (_capsule != null)
            {
                if (Application.isPlaying) Destroy(_capsule);
                else DestroyImmediate(_capsule);
                _capsule = null;
            }
            if (_box == null)
                _box = gameObject.AddComponent<BoxCollider>();
            _box.isTrigger = true;
        }

        ApplyColliderConfig();
    }

    void ApplyColliderConfig()
    {
        if (shape == VolumeShape.Capsule && _capsule != null)
        {
            _capsule.center = localCenter;
            _capsule.radius = Mathf.Max(0.01f, inPlaneRadius);
            _capsule.height = Mathf.Max(_capsule.radius * 2f, verticalHalfExtent * 2f);
            _capsule.direction = 1; // local Y
        }
        else if (shape == VolumeShape.Box && _box != null)
        {
            _box.center = localCenter;
            _box.size = new Vector3(
                Mathf.Max(0.01f, boxHalfExtents.x * 2f),
                Mathf.Max(0.01f, boxHalfExtents.y * 2f),
                Mathf.Max(0.01f, boxHalfExtents.z * 2f));
        }
    }

    void PrimeOccupancy()
    {
        int mask = detectionMask.value == 0 ? Physics.DefaultRaycastLayers : detectionMask.value;
        int enemyLayer = LayerMask.GetMask("Enemy");
        if (enemyLayer != 0)
            mask |= enemyLayer;

        Vector3 worldCenter = transform.TransformPoint(localCenter);
        Quaternion worldRot = transform.rotation;

        Collider[] hits;
        if (shape == VolumeShape.Capsule)
        {
            float r = Mathf.Max(0.01f, inPlaneRadius);
            float h = Mathf.Max(r * 2f, verticalHalfExtent * 2f);
            float halfLen = Mathf.Max(0f, h * 0.5f - r);
            Vector3 up = transform.up;
            Vector3 p0 = worldCenter + up * halfLen;
            Vector3 p1 = worldCenter - up * halfLen;
            hits = Physics.OverlapCapsule(p0, p1, r, mask, QueryTriggerInteraction.Ignore);
        }
        else
        {
            Vector3 half = new(
                Mathf.Max(0.01f, boxHalfExtents.x),
                Mathf.Max(0.01f, boxHalfExtents.y),
                Mathf.Max(0.01f, boxHalfExtents.z));
            hits = Physics.OverlapBox(worldCenter, half, worldRot, mask, QueryTriggerInteraction.Ignore);
        }

        for (int i = 0; i < hits.Length; i++)
            TryAdd(hits[i]);
    }

    void OnTriggerEnter(Collider other)
    {
        TryAdd(other);
    }

    void OnTriggerExit(Collider other)
    {
        if (other == null)
            return;

        PlayerHealth ph = other.GetComponentInParent<PlayerHealth>();
        if (ph != null)
            _players.Remove(ph);

        ZombieHealth zh = other.GetComponentInParent<ZombieHealth>();
        if (zh != null)
            _zombies.Remove(zh);
    }

    void TryAdd(Collider other)
    {
        if (other == null)
            return;

        PlayerHealth ph = other.GetComponentInParent<PlayerHealth>();
        if (ph != null && !ph.IsDead && !IsCarriedByJailor(ph))
        {
            _players.Add(ph);
            return;
        }

        ZombieHealth zh = other.GetComponentInParent<ZombieHealth>();
        if (zh != null && !zh.IsDead)
            _zombies.Add(zh);
    }

    void Prune()
    {
        _players.RemoveWhere(p => p == null || p.IsDead || IsCarriedByJailor(p));
        _zombies.RemoveWhere(z => z == null || z.IsDead);
    }

    static bool IsCarriedByJailor(PlayerHealth player)
    {
        if (player == null)
            return false;

        NetworkPlayerAvatar avatar = player.GetComponent<NetworkPlayerAvatar>();
        return avatar != null && avatar.IsCarriedByJailor;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(1f, 0.55f, 0f, 0.45f);

        if (shape == VolumeShape.Capsule)
        {
            float r = Mathf.Max(0.01f, inPlaneRadius);
            float h = Mathf.Max(r * 2f, verticalHalfExtent * 2f);
            float halfLen = Mathf.Max(0f, h * 0.5f - r);
            Vector3 top = localCenter + Vector3.up * halfLen;
            Vector3 bot = localCenter - Vector3.up * halfLen;
            Gizmos.DrawWireSphere(top, r);
            Gizmos.DrawWireSphere(bot, r);
            Gizmos.DrawLine(top + Vector3.right * r, bot + Vector3.right * r);
            Gizmos.DrawLine(top - Vector3.right * r, bot - Vector3.right * r);
            Gizmos.DrawLine(top + Vector3.forward * r, bot + Vector3.forward * r);
            Gizmos.DrawLine(top - Vector3.forward * r, bot - Vector3.forward * r);
        }
        else
        {
            Gizmos.DrawWireCube(localCenter, new Vector3(
                Mathf.Max(0.01f, boxHalfExtents.x * 2f),
                Mathf.Max(0.01f, boxHalfExtents.y * 2f),
                Mathf.Max(0.01f, boxHalfExtents.z * 2f)));
        }
    }
#endif
}
