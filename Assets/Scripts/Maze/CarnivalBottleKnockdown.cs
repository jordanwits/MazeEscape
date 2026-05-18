using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Carnival bottle stays kinematic on spawn so imperfect shelf geometry / non-uniform parent scales
/// can't tip it over. First impact from a <see cref="HeavyThrowableHoldItem"/> (Ball / StarBall / RingToss)
/// releases it to dynamic and transfers a fraction of the projectile's momentum at the contact point so
/// the knockdown reads as a physical hit rather than a delayed wake-up.
/// <para>
/// Multiplayer: clients own the ball during a throw (owner-authoritative <see cref="Unity.Netcode.Components.NetworkRigidbody"/>),
/// so the server's mirror ball is kinematic. Two kinematic bodies never raise <see cref="MonoBehaviour.OnCollisionEnter"/>,
/// which means the host never witnesses the hit when a remote client throws. We compensate by
/// detecting the collision on the throwing client (where the ball is dynamic) and forwarding the
/// impulse to the server via <see cref="RequestKnockdownServerRpc"/>; the server applies the
/// knockdown and broadcasts it to every other peer so each one simulates the fall locally.
/// </para>
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
public sealed class CarnivalBottleKnockdown : NetworkBehaviour
{
    [Tooltip("Fraction of the projectile's linear momentum (mass * velocity) applied to the bottle at the contact point on first impact.")]
    [SerializeField, Range(0.02f, 1f)] float impactImpulseScale = 0.15f;

    Rigidbody _rb;
    bool _knockedDown;

    public bool IsKnockedDown => _knockedDown;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.isKinematic = true;
    }

    void OnCollisionEnter(Collision collision)
    {
        if (_knockedDown)
            return;
        if (collision.rigidbody == null)
            return;
        if (collision.rigidbody.GetComponent<HeavyThrowableHoldItem>() == null)
            return;

        Vector3 contactPoint = collision.GetContact(0).point;
        Vector3 impulse = collision.rigidbody.linearVelocity * collision.rigidbody.mass * impactImpulseScale;

        NetworkManager nm = NetworkManager.Singleton;

        // Offline / single player: no replication path, just apply locally.
        if (nm == null || !nm.IsListening || !IsSpawned)
        {
            ApplyKnockdownLocal(contactPoint, impulse);
            return;
        }

        if (IsServer)
        {
            // Host playing locally: collision fires on the host's authoritative physics. Apply and
            // mirror to all observing clients.
            ApplyKnockdownLocal(contactPoint, impulse);
            BroadcastKnockdownClientRpc(contactPoint, impulse);
            return;
        }

        // Joined client: only fire when this client owns the ball that hit. Non-owner peers see a
        // kinematic mirror that can't physically push the bottle anyway, and double-firing from
        // every peer would multiply the impulse.
        NetworkObject ballNet = collision.rigidbody.GetComponent<NetworkObject>();
        if (ballNet == null || !ballNet.IsSpawned)
            return;
        if (ballNet.OwnerClientId != nm.LocalClientId)
            return;

        // Apply on the throwing client immediately so the bottle reacts on their screen without a
        // roundtrip wait; the _knockedDown guard prevents the eventual server broadcast from
        // double-applying.
        ApplyKnockdownLocal(contactPoint, impulse);
        RequestKnockdownServerRpc(contactPoint, impulse);
    }

    [ServerRpc(RequireOwnership = false)]
    void RequestKnockdownServerRpc(Vector3 contactPoint, Vector3 impulse)
    {
        if (_knockedDown)
            return;
        ApplyKnockdownLocal(contactPoint, impulse);
        BroadcastKnockdownClientRpc(contactPoint, impulse);
    }

    [ClientRpc]
    void BroadcastKnockdownClientRpc(Vector3 contactPoint, Vector3 impulse)
    {
        if (IsServer)
            return;
        if (_knockedDown)
            return;
        ApplyKnockdownLocal(contactPoint, impulse);
    }

    void ApplyKnockdownLocal(Vector3 contactPoint, Vector3 impulse)
    {
        if (_knockedDown)
            return;
        _knockedDown = true;
        _rb.isKinematic = false;
        // On clients, GrabbableMazeClientPhysics may have turned gravity off while the bottle was
        // kinematic-by-design. Re-enable it so the knocked bottle actually falls.
        _rb.useGravity = true;
        _rb.AddForceAtPosition(impulse, contactPoint, ForceMode.Impulse);
    }
}
