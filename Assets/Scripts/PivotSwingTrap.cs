using Unity.Netcode;
using UnityEngine;

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
    [Tooltip("Blend speed toward rest or swing (per second).")]
    [SerializeField] float swingMoveSpeed = 4f;

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

    readonly NetworkVariable<float> _networkBlend = new(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    Quaternion _baseLocalRotation;
    float _offlineBlend;
    readonly Collider[] _overlapHits = new Collider[32];

    static bool IsNetworkActive =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    void Awake()
    {
        if (pivot == null)
            pivot = transform;
        if (detectionOrigin == null)
            detectionOrigin = pivot;
        if (detectionForward == null)
            detectionForward = pivot;

        _baseLocalRotation = pivot.localRotation;
    }

    void Update()
    {
        if (!IsNetworkActive)
        {
            float targetBlend = ComputeTargetBlendAuthoritative();
            _offlineBlend = Mathf.MoveTowards(_offlineBlend, targetBlend, Time.deltaTime * swingMoveSpeed);
            ApplyRotation(_offlineBlend);
            return;
        }

        if (!IsSpawned)
            return;

        if (IsServer)
        {
            float targetBlend = ComputeTargetBlendAuthoritative();
            float next = Mathf.MoveTowards(_networkBlend.Value, targetBlend, Time.deltaTime * swingMoveSpeed);
            _networkBlend.Value = next;
            ApplyRotation(next);
            return;
        }

        ApplyRotation(_networkBlend.Value);
    }

    float ComputeTargetBlendAuthoritative()
    {
        if (triggerDistance <= 0f)
            return 0f;

        Vector3 origin = detectionOrigin.position;
        Vector3 forward = detectionForward.forward;
        if (forward.sqrMagnitude < 1e-6f)
            return 0f;

        forward.Normalize();
        float maxDot = Mathf.Cos(maxAngleFromForwardDegrees * Mathf.Deg2Rad);
        float sqDist = triggerDistance * triggerDistance;

        int mask = detectionMask.value == 0 ? Physics.DefaultRaycastLayers : detectionMask.value;
        int count = Physics.OverlapSphereNonAlloc(
            origin,
            triggerDistance,
            _overlapHits,
            mask,
            QueryTriggerInteraction.Ignore);

        bool playerPresent = false;
        for (int i = 0; i < count; i++)
        {
            Collider col = _overlapHits[i];
            _overlapHits[i] = null;
            if (col == null)
                continue;

            PlayerHealth health = col.GetComponentInParent<PlayerHealth>();
            if (health == null || health.IsDead)
                continue;

            Vector3 toPlayer = health.transform.position - origin;
            if (toPlayer.sqrMagnitude > sqDist)
                continue;

            if (toPlayer.sqrMagnitude < 1e-6f)
            {
                playerPresent = true;
                break;
            }

            toPlayer.Normalize();
            if (Vector3.Dot(forward, toPlayer) < maxDot)
                continue;

            playerPresent = true;
            break;
        }

        if (playerPresent)
            return 1f;

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

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Transform o = detectionOrigin != null ? detectionOrigin : (pivot != null ? pivot : transform);
        Transform f = detectionForward != null ? detectionForward : o;
        if (o == null || triggerDistance <= 0f)
            return;

        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.35f);
        Gizmos.DrawWireSphere(o.position, triggerDistance);

        Vector3 fwd = f.forward.sqrMagnitude > 1e-6f ? f.forward.normalized : Vector3.forward;
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(o.position, fwd * Mathf.Min(triggerDistance, 2f));
    }
#endif
}
