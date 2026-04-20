using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Example trap: add a trigger collider, assign a force direction (world space) and magnitude.
/// In multiplayer, only the server applies ragdoll so all clients stay in sync.
/// </summary>
[RequireComponent(typeof(Collider))]
public class RagdollTrap : MonoBehaviour
{
    const float TrapDamageAmount = 25f;

    public enum KnockbackDirectionMode
    {
        CustomWorldVector,
        OppositeTrapForward,
        TrapForward,
        [Tooltip("From trap position toward the player on the floor — works even if trap forward is wrong.")]
        PushAwayFromTrapHorizontal,
    }

    [SerializeField] KnockbackDirectionMode knockbackDirection = KnockbackDirectionMode.PushAwayFromTrapHorizontal;
    [Tooltip("With Velocity Change: horizontal push in m/s. With Impulse: impulse strength (mass-dependent).")]
    [SerializeField] float impulseMagnitude = 12f;
    [SerializeField] Vector3 forceDirectionWorld = Vector3.forward;
    [Tooltip("If true, knockback direction ignores vertical component (slide along the floor).")]
    [SerializeField] bool knockbackHorizontalOnly = true;
    [Tooltip("Added upward delta. With Velocity Change this is m/s upward.")]
    [SerializeField] float upwardImpulse = 5f;
    [Tooltip("Velocity Change = reliable knock (m/s). Impulse = physics impulse at hips.")]
    [SerializeField] ForceMode forceMode = ForceMode.VelocityChange;

    void Reset()
    {
        Collider c = GetComponent<Collider>();
        if (c != null)
            c.isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (!isActiveAndEnabled)
            return;

        TryHit(other);
    }

    void OnTriggerStay(Collider other)
    {
        if (!isActiveAndEnabled)
            return;

        PlayerRagdollController ragdoll = other.GetComponentInParent<PlayerRagdollController>();
        if (ragdoll != null && ragdoll.IsRagdolled)
            return;

        TryHit(other);
    }

    void TryHit(Collider other)
    {
        NetworkPlayerRagdoll netRagdoll = other.GetComponentInParent<NetworkPlayerRagdoll>();
        PlayerRagdollController ragdoll = other.GetComponentInParent<PlayerRagdollController>();

        Vector3 hitCenter = other.bounds.center;
        Vector3 dir = ResolveKnockbackDirection(hitCenter);
        Vector3 force = dir * impulseMagnitude;
        if (upwardImpulse > 0f)
            force += Vector3.up * upwardImpulse;
        Vector3 hitPoint = hitCenter;

        NetworkManager net = NetworkManager.Singleton;
        if (net != null && net.IsListening && netRagdoll != null)
        {
            if (net.IsServer)
            {
                netRagdoll.RequestTrapHitFromServer(force, hitPoint, TrapDamageAmount, forceMode);
                return;
            }

            NetworkObject playerNetObj = other.GetComponentInParent<NetworkObject>();
            if (playerNetObj != null && playerNetObj.IsOwner)
                netRagdoll.RequestTrapHitServerRpc(force, hitPoint, TrapDamageAmount, (byte)forceMode);

            return;
        }

        PlayerHealth health = other.GetComponentInParent<PlayerHealth>();
        health?.TakeDamage(TrapDamageAmount);

        if (ragdoll != null)
            ragdoll.ActivateRagdoll(force, hitPoint, forceMode);
    }

    Vector3 ResolveKnockbackDirection(Vector3 otherBoundsCenter)
    {
        Vector3 dir;
        switch (knockbackDirection)
        {
            case KnockbackDirectionMode.PushAwayFromTrapHorizontal:
                dir = otherBoundsCenter - transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.0001f)
                    dir = -transform.forward;
                dir.y = 0f;
                return dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
            case KnockbackDirectionMode.TrapForward:
                dir = transform.forward;
                break;
            case KnockbackDirectionMode.OppositeTrapForward:
                dir = -transform.forward;
                break;
            default:
                dir = forceDirectionWorld.sqrMagnitude > 1e-6f ? forceDirectionWorld : transform.forward;
                break;
        }

        if (knockbackHorizontalOnly)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f)
                dir = -transform.forward;
            dir.y = 0f;
        }

        return dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
    }
}
