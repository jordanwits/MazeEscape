using Unity.Netcode;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Rotates a pivot (pad-on-a-stick) between a rest pose and a swung pose when a living player
/// is within range and in front of the detection direction. Server drives state when online.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public class PivotSwingTrap : NetworkBehaviour
{
    [Header("Pivot")]
    [Tooltip("Transform that rotates (hinge). Defaults to this object.")]
    [SerializeField] Transform pivot;
    [Tooltip("Swing axis in the pivot's local space (e.g. (0,1,0) for a door-like swing around local Y).")]
    [SerializeField] Vector3 localSwingAxis = Vector3.up;
    [Tooltip("Euler degrees around localSwingAxis when no player is in the trigger zone.")]
    [SerializeField] float restAngleDegrees;
    [Tooltip("Euler degrees around localSwingAxis when a player triggers the swing.")]
    [SerializeField] float swingAngleDegrees = -90f;
    [Tooltip("Blend speed when swinging toward the triggered angle (per second).")]
    [SerializeField] float swingMoveSpeed = 4f;
    [Tooltip("Blend speed when returning to rest after the zone clears (per second). Use a lower value than swing for a slower reset.")]
    [SerializeField] float returnMoveSpeed = 1.5f;

    [Header("Detection")]
    [Tooltip("Measured from this transform's position. Defaults to pivot.")]
    [SerializeField] Transform detectionOrigin;
    [Tooltip("Forward direction for \"in front\" checks. Defaults to pivot forward.")]
    [SerializeField] Transform detectionForward;
    [SerializeField] float triggerDistance = 4f;
    [Tooltip("Max angle off forward (degrees) that still counts as \"in front\".")]
    [SerializeField] float maxAngleFromForwardDegrees = 55f;
    [SerializeField] LayerMask detectionMask;
    [Tooltip("If true, pivot returns to rest when no player qualifies.")]
    [SerializeField] bool returnToRestWhenClear = true;
    [Tooltip("When the detection volume is a tripwire/floor pad players can stand in, enable this so the swing returns after full extension even if they never leave the zone.")]
    [SerializeField] bool returnAfterReachingFullSwingWhileOccupied;
    [Tooltip("With the option above: after the trap resets, it will not swing out again until no qualifying player is detected (briefly leaving the tripwire zone).")]
    [SerializeField] bool requireZoneClearBeforeRetrigger = true;
    [Tooltip("Tripwire mode: seconds to stay at full swing before returning. Damage uses the outward pose; too short a hold turns off hits as soon as the blend hits 1.")]
    [SerializeField] float fullSwingHoldBeforeReturnSeconds = 0.35f;
    [Tooltip("Along Detection Origin's up axis: how far above/below the wire still counts (walk underneath, jump over). Uses the TripWire transform rotation, not world Y. Use 0 for legacy full 3D sphere checks.")]
    [SerializeField] float tripWireVerticalHalfExtent = 6f;
    [Tooltip("Tripwire mode only: radius in the plane perpendicular to Trip Wire Up (wider crossing band). 0 uses Trigger Distance for that radius.")]
    [SerializeField] float tripWireInPlaneRadius;
    [Tooltip("When tripwire auto-return is on: trigger if the player is inside the vertical slab / disc, ignoring forward (corridor approach angle and anchor rotation). Turn off for a one-sided trap.")]
    [SerializeField] bool tripwireOmnidirectionalDetection = true;

    [Header("Tripwire Zone (physics trigger, preferred)")]
    [Tooltip("Physics-trigger tripwire. When assigned (or auto-created below), detection uses native OnTrigger events on this zone instead of the OverlapSphere scan. This is much more reliable inside the procedural maze because it cannot be starved by a full overlap buffer and the box follows the transform, so upside-down / rotated placements work automatically.")]
    [SerializeField] TripwireZone tripwireZone;
    [Tooltip("If no Tripwire Zone is assigned, auto-create one on the Detection Origin at Awake using Trigger Distance / Trip Wire Vertical Half Extent / Trip Wire In-Plane Radius to size the box volume. Existing prefabs keep their configured numbers.")]
    [SerializeField] bool autoCreateTripwireZone = true;

    [Header("Audio")]
    [SerializeField] AudioClip trapSwingSwooshClip;
    [SerializeField, Range(0f, 1f)] float trapSwingSwooshVolume = 0.85f;

    const float BlendEpsilon = 0.001f;
    const int OverlapBufferSize = 128;

    readonly NetworkVariable<float> _networkBlend = new(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<bool> _networkReturning = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    Quaternion _baseLocalRotation;
    float _offlineBlend;
    bool _isReturning;
    bool _detectionClearSinceLastSwingActivation = true;
    float _fullSwingHoldTimer;
    readonly Collider[] _overlapHits = new Collider[OverlapBufferSize];

    float _previousSwingBlend;
    bool _clientSwingBlendPrimed;
    AudioSource _trapAudio;

    static bool IsNetworkActive =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    /// <summary>
    /// When false, the trap is at rest (untriggered pose). Ragdoll/damage should not apply.
    /// When true with <see cref="IsSwingTrapReturning"/>, the trap is moving back to rest — also no damage.
    /// </summary>
    public bool CanDealSwingTrapDamage =>
        ReadBlendForDamageGate() > BlendEpsilon && !ReadReturningForDamageGate();

    public bool IsSwingTrapReturning => ReadReturningForDamageGate();

    void Awake()
    {
        if (pivot == null)
            pivot = transform;
        if (detectionOrigin == null)
            detectionOrigin = pivot;
        if (detectionForward == null)
            detectionForward = pivot;

        _baseLocalRotation = pivot.localRotation;

        EnsureTripwireZone();
        EnsureTrapAudioSource();
#if UNITY_EDITOR
        AutoAssignTrapSwingClipInEditor();
#endif
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        AutoAssignTrapSwingClipInEditor();
    }

    void AutoAssignTrapSwingClipInEditor()
    {
        if (trapSwingSwooshClip == null)
            trapSwingSwooshClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Swoosh.wav");
    }
#endif

    void EnsureTrapAudioSource()
    {
        if (_trapAudio != null)
            return;

        _trapAudio = GetComponent<AudioSource>();
        if (_trapAudio == null)
            _trapAudio = gameObject.AddComponent<AudioSource>();

        _trapAudio.playOnAwake = false;
        _trapAudio.loop = false;
        _trapAudio.spatialBlend = 1f;
        _trapAudio.minDistance = 1f;
        _trapAudio.maxDistance = 45f;
        _trapAudio.rolloffMode = AudioRolloffMode.Linear;
    }

    void MaybePlaySwingSwoosh(float previousBlend, float nextBlend)
    {
        if (trapSwingSwooshClip == null || _trapAudio == null)
            return;

        if (previousBlend > BlendEpsilon || nextBlend <= BlendEpsilon)
            return;

        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_trapAudio);

        _trapAudio.PlayOneShot(trapSwingSwooshClip, Mathf.Max(0f, trapSwingSwooshVolume));
    }

    void EnsureTripwireZone()
    {
        if (tripwireZone != null || !autoCreateTripwireZone || detectionOrigin == null)
            return;

        tripwireZone = detectionOrigin.GetComponent<TripwireZone>();
        if (tripwireZone != null)
            return;

        tripwireZone = detectionOrigin.gameObject.AddComponent<TripwireZone>();

        float plane = tripWireInPlaneRadius > 0f ? tripWireInPlaneRadius : Mathf.Max(0.1f, triggerDistance);
        float vert = tripWireVerticalHalfExtent > 0f ? tripWireVerticalHalfExtent : Mathf.Max(0.1f, triggerDistance);
        tripwireZone.Shape = TripwireZone.VolumeShape.Capsule;
        tripwireZone.InPlaneRadius = plane;
        tripwireZone.VerticalHalfExtent = vert;
    }

    void Update()
    {
        if (!IsNetworkActive)
        {
            float prev = _offlineBlend;
            float targetBlend = ComputeTargetBlendAuthoritative(_offlineBlend);
            float moveSpeed = targetBlend > _offlineBlend ? swingMoveSpeed : returnMoveSpeed;
            float next = Mathf.MoveTowards(_offlineBlend, targetBlend, Time.deltaTime * moveSpeed);
            MaybePlaySwingSwoosh(prev, next);
            _offlineBlend = next;
            ApplyRotation(_offlineBlend);
            return;
        }

        if (!IsSpawned)
            return;

        if (IsServer)
        {
            float current = _networkBlend.Value;
            float targetBlend = ComputeTargetBlendAuthoritative(current);
            _networkReturning.Value = _isReturning;
            float moveSpeed = targetBlend > current ? swingMoveSpeed : returnMoveSpeed;
            float next = Mathf.MoveTowards(current, targetBlend, Time.deltaTime * moveSpeed);
            MaybePlaySwingSwoosh(current, next);
            _networkBlend.Value = next;
            ApplyRotation(next);
            return;
        }

        float clientBlend = _networkBlend.Value;
        if (_clientSwingBlendPrimed)
            MaybePlaySwingSwoosh(_previousSwingBlend, clientBlend);
        else
            _clientSwingBlendPrimed = true;

        _previousSwingBlend = clientBlend;
        ApplyRotation(clientBlend);
    }

    bool TryGetQualifyingPlayerPresent()
    {
        if (tripwireZone != null)
            return QueryTripwireZone();

        if (triggerDistance <= 0f)
            return false;

        Vector3 origin = detectionOrigin.position;
        Vector3 forward = detectionForward.forward;
        if (forward.sqrMagnitude < 1e-6f)
            return false;

        forward.Normalize();
        Transform originTransform = detectionOrigin != null ? detectionOrigin : pivot;
        Vector3 tripWireUp = originTransform != null ? originTransform.up : Vector3.up;
        if (tripWireUp.sqrMagnitude < 1e-6f)
            tripWireUp = Vector3.up;
        tripWireUp.Normalize();

        float maxDot = Mathf.Cos(maxAngleFromForwardDegrees * Mathf.Deg2Rad);
        float sqDistTrigger = triggerDistance * triggerDistance;
        float horizontalRadius = triggerDistance;
        if (tripWireVerticalHalfExtent > 0f && tripWireInPlaneRadius > 0f)
            horizontalRadius = tripWireInPlaneRadius;
        float sqHorizontal = horizontalRadius * horizontalRadius;

        float limitSq = tripWireVerticalHalfExtent > 0f ? sqHorizontal : sqDistTrigger;

        float overlapRadius = triggerDistance;
        if (tripWireVerticalHalfExtent > 0f)
            overlapRadius = Mathf.Sqrt(sqHorizontal + tripWireVerticalHalfExtent * tripWireVerticalHalfExtent);

        int mask = detectionMask.value == 0 ? Physics.DefaultRaycastLayers : detectionMask.value;
        int enemyLayerMask = LayerMask.GetMask("Enemy");
        if (enemyLayerMask != 0)
            mask |= enemyLayerMask;

        int count = Physics.OverlapSphereNonAlloc(
            origin,
            overlapRadius,
            _overlapHits,
            mask,
            QueryTriggerInteraction.Ignore);

        if (count >= OverlapBufferSize)
        {
            Debug.LogWarning(
                $"[{nameof(PivotSwingTrap)}] Overlap buffer full ({OverlapBufferSize}) at {name}. " +
                "Increase OverlapBufferSize or narrow Detection Mask so the player is not dropped from results.",
                this);
        }

        bool skipForwardCone =
            returnAfterReachingFullSwingWhileOccupied && tripwireOmnidirectionalDetection;

        for (int i = 0; i < count; i++)
        {
            Collider col = _overlapHits[i];
            _overlapHits[i] = null;
            if (col == null)
                continue;

            PlayerHealth health = col.GetComponentInParent<PlayerHealth>();
            if (health != null && !health.IsDead)
            {
                if (IsTargetInTriggerCone(
                        health.transform,
                        origin,
                        forward,
                        tripWireUp,
                        limitSq,
                        maxDot,
                        tripWireVerticalHalfExtent,
                        skipForwardCone))
                    return true;
                continue;
            }

            ZombieHealth zombie = col.GetComponentInParent<ZombieHealth>();
            if (zombie != null && !zombie.IsDead)
            {
                if (IsTargetInTriggerCone(
                        zombie.transform,
                        origin,
                        forward,
                        tripWireUp,
                        limitSq,
                        maxDot,
                        tripWireVerticalHalfExtent,
                        skipForwardCone))
                    return true;
            }
        }

        return false;
    }

    bool QueryTripwireZone()
    {
        if (tripwireZone == null || !tripwireZone.HasQualifyingTarget)
            return false;

        bool skipForwardCone =
            returnAfterReachingFullSwingWhileOccupied && tripwireOmnidirectionalDetection;

        // Omnidirectional tripwire: any living target inside the zone is enough.
        if (skipForwardCone)
            return true;

        // One-sided trap: keep the "in front" cone check but evaluate it only against targets already inside
        // the trigger zone (so the expensive per-frame scan is gone and we still honor the approach angle).
        Transform originT = detectionOrigin != null ? detectionOrigin : pivot;
        Transform forwardT = detectionForward != null ? detectionForward : originT;
        if (originT == null || forwardT == null)
            return true;

        Vector3 origin = originT.position;
        Vector3 forward = forwardT.forward;
        if (forward.sqrMagnitude < 1e-6f)
            return true;
        forward.Normalize();

        Vector3 up = originT.up;
        if (up.sqrMagnitude < 1e-6f)
            up = Vector3.up;
        up.Normalize();

        float maxDot = Mathf.Cos(maxAngleFromForwardDegrees * Mathf.Deg2Rad);

        foreach (PlayerHealth player in tripwireZone.PlayersInside)
        {
            if (player == null || player.IsDead)
                continue;
            if (IsInForwardCone(player.transform.position, origin, forward, up, maxDot))
                return true;
        }

        foreach (ZombieHealth zombie in tripwireZone.ZombiesInside)
        {
            if (zombie == null || zombie.IsDead)
                continue;
            if (IsInForwardCone(zombie.transform.position, origin, forward, up, maxDot))
                return true;
        }

        return false;
    }

    static bool IsInForwardCone(Vector3 targetPos, Vector3 origin, Vector3 forward, Vector3 up, float maxDot)
    {
        Vector3 toTarget = targetPos - origin;
        Vector3 horiz = toTarget - up * Vector3.Dot(toTarget, up);
        Vector3 fwdFlat = forward - up * Vector3.Dot(forward, up);

        if (fwdFlat.sqrMagnitude < 1e-6f)
        {
            if (toTarget.sqrMagnitude < 1e-6f)
                return true;
            return Vector3.Dot(forward, toTarget.normalized) >= maxDot;
        }

        if (horiz.sqrMagnitude < 1e-6f)
            return true;

        return Vector3.Dot(fwdFlat.normalized, horiz.normalized) >= maxDot;
    }

    static bool IsTargetInTriggerCone(
        Transform target,
        Vector3 origin,
        Vector3 forward,
        Vector3 tripWireUp,
        float sqDist,
        float maxDot,
        float verticalHalfExtent,
        bool skipForwardCone)
    {
        if (target == null)
            return false;

        Vector3 toTarget = target.position - origin;

        if (verticalHalfExtent > 0f)
        {
            float alongUp = Vector3.Dot(toTarget, tripWireUp);
            if (Mathf.Abs(alongUp) > verticalHalfExtent)
                return false;

            Vector3 horiz = toTarget - tripWireUp * alongUp;
            if (horiz.sqrMagnitude > sqDist)
                return false;

            if (skipForwardCone)
                return true;

            if (horiz.sqrMagnitude < 1e-6f)
                return true;

            Vector3 fwdFlat = forward - tripWireUp * Vector3.Dot(forward, tripWireUp);
            if (fwdFlat.sqrMagnitude < 1e-6f)
                return Vector3.Dot(forward, toTarget.normalized) >= maxDot;

            fwdFlat.Normalize();
            horiz.Normalize();
            return Vector3.Dot(fwdFlat, horiz) >= maxDot;
        }

        if (toTarget.sqrMagnitude > sqDist)
            return false;

        if (skipForwardCone)
            return true;

        if (toTarget.sqrMagnitude < 1e-6f)
            return true;

        toTarget.Normalize();
        return Vector3.Dot(forward, toTarget) >= maxDot;
    }

    /// <summary>
    /// Client-side relay so trap hits on clients still kill zombies on the host.
    /// </summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void RequestZombieTrapKillServerRpc(ulong zombieNetworkObjectId)
    {
        if (!IsServer)
            return;

        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(zombieNetworkObjectId, out NetworkObject netObj))
            return;

        ZombieHealth zh = netObj.GetComponent<ZombieHealth>();
        if (zh == null || zh.IsDead)
            return;

        zh.Die();
    }

    float ComputeTargetBlendAuthoritative(float currentBlend)
    {
        // Only the OverlapSphere fallback depends on triggerDistance / forward; a configured TripwireZone drives
        // detection directly from OnTrigger events, so we skip those guards when a zone is present.
        if (tripwireZone == null)
        {
            if (triggerDistance <= 0f)
                return 0f;

            Vector3 forward = detectionForward.forward;
            if (forward.sqrMagnitude < 1e-6f)
                return 0f;
        }

        bool playerPresent = TryGetQualifyingPlayerPresent();

        if (!playerPresent)
            _detectionClearSinceLastSwingActivation = true;

        if (currentBlend <= BlendEpsilon)
            _isReturning = false;
        else if (!playerPresent && returnToRestWhenClear)
            _isReturning = true;

        if (returnAfterReachingFullSwingWhileOccupied)
        {
            if (currentBlend >= 1f - BlendEpsilon)
            {
                _fullSwingHoldTimer += Time.deltaTime;
                float hold = Mathf.Max(0f, fullSwingHoldBeforeReturnSeconds);
                if (hold <= 0f || _fullSwingHoldTimer >= hold)
                    _isReturning = true;
            }
            else
                _fullSwingHoldTimer = 0f;
        }
        else
            _fullSwingHoldTimer = 0f;

        bool allowActivateFromPresence =
            !returnAfterReachingFullSwingWhileOccupied
            || !requireZoneClearBeforeRetrigger
            || _detectionClearSinceLastSwingActivation;

        if (playerPresent && !_isReturning)
        {
            if (allowActivateFromPresence)
            {
                if (returnAfterReachingFullSwingWhileOccupied && requireZoneClearBeforeRetrigger)
                    _detectionClearSinceLastSwingActivation = false;
                return 1f;
            }

            // Gated retrigger (standing on tripwire after a cycle): hold rest only until we start extending.
            if (currentBlend <= BlendEpsilon)
                return 0f;
            return 1f;
        }

        if (playerPresent && _isReturning)
            return 0f;

        if (returnToRestWhenClear)
            return 0f;

        if (!IsNetworkActive)
            return _offlineBlend;

        return _networkBlend.Value;
    }

    void ApplyRotation(float blend)
    {
        Vector3 axis = localSwingAxis.sqrMagnitude > 1e-6f ? localSwingAxis.normalized : Vector3.up;
        float angle = Mathf.Lerp(restAngleDegrees, swingAngleDegrees, blend);
        Quaternion swing = Quaternion.AngleAxis(angle, axis);
        pivot.localRotation = _baseLocalRotation * swing;
    }

    float ReadBlendForDamageGate()
    {
        if (!IsNetworkActive)
            return _offlineBlend;
        return IsSpawned ? _networkBlend.Value : 0f;
    }

    bool ReadReturningForDamageGate()
    {
        if (!IsNetworkActive)
            return _isReturning;
        return IsSpawned && _networkReturning.Value;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Transform o = detectionOrigin != null ? detectionOrigin : (pivot != null ? pivot : transform);
        Transform f = detectionForward != null ? detectionForward : o;
        if (o == null || triggerDistance <= 0f)
            return;

        float horizontalForGizmo = triggerDistance;
        if (tripWireVerticalHalfExtent > 0f && tripWireInPlaneRadius > 0f)
            horizontalForGizmo = tripWireInPlaneRadius;

        float gizmoRadius = triggerDistance;
        if (tripWireVerticalHalfExtent > 0f)
            gizmoRadius = Mathf.Sqrt(horizontalForGizmo * horizontalForGizmo + tripWireVerticalHalfExtent * tripWireVerticalHalfExtent);

        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.35f);
        Gizmos.DrawWireSphere(o.position, gizmoRadius);

        Vector3 fwd = f.forward.sqrMagnitude > 1e-6f ? f.forward.normalized : Vector3.forward;
        Gizmos.color = Color.cyan;
        float rayLen = tripWireVerticalHalfExtent > 0f ? horizontalForGizmo : triggerDistance;
        Gizmos.DrawRay(o.position, fwd * Mathf.Min(rayLen, 2f));

        if (tripWireVerticalHalfExtent > 0f)
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.55f);
            Vector3 p = o.position;
            Vector3 up = o.up.sqrMagnitude > 1e-6f ? o.up.normalized : Vector3.up;
            Gizmos.DrawLine(p + up * tripWireVerticalHalfExtent, p - up * tripWireVerticalHalfExtent);
        }
    }
#endif
}
