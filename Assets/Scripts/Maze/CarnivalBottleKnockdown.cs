using System.Collections.Generic;
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
/// impulse to the server via <see cref="RequestKnockdownServerRpc"/>; the server <b>validates</b> the
/// report (sender owns a thrown object near this bottle, impulse clamped, contact snapped to the bottle),
/// applies the knockdown, and broadcasts it so every other peer simulates the fall locally.
/// </para>
/// <para>
/// Late join / convergence: the fall is simulated per-peer (the bottle has no NetworkTransform), so the
/// authoritative knock state is replicated in <see cref="_knockState"/>. A client that joins after a
/// bottle was knocked down reconstructs it in <see cref="OnNetworkSpawn"/>; once the server's bottle
/// settles, its rest pose is published and every peer snaps to it — so peers can't disagree about where
/// a knocked bottle finally lies.
/// </para>
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
public sealed class CarnivalBottleKnockdown : NetworkBehaviour
{
    [Tooltip("Fraction of the projectile's linear momentum (mass * velocity) applied to the bottle at the contact point on first impact.")]
    [SerializeField, Range(0.02f, 1f)] float impactImpulseScale = 0.15f;

    [Header("Server validation (client-reported hits)")]
    [Tooltip("A client-reported hit is only accepted if the sender owns a thrown HeavyThrowableHoldItem within this distance of the bottle. Generous to tolerate the server mirror lagging the throw under latency.")]
    [SerializeField, Min(0.25f)] float maxReportedHitDistance = 4f;
    [Tooltip("Client-reported knockdown impulse magnitude is clamped to this so a modified client can't launch bottles across the room.")]
    [SerializeField, Min(1f)] float maxKnockdownImpulse = 220f;

    [Header("Server settle detection")]
    [Tooltip("Below this linear speed the server considers the knocked bottle to be coming to rest.")]
    [SerializeField, Min(0f)] float settleSpeedThreshold = 0.08f;
    [Tooltip("The bottle must stay below the settle speed for this long before the server publishes its rest pose.")]
    [SerializeField, Min(0f)] float settleHoldSeconds = 0.35f;

    /// <summary>
    /// Replicated authoritative knock state. All fields are unmanaged, so memcpy serialization matches the
    /// project's transient-state convention (see NetworkClownAvatar.AttackAnimationState).
    /// </summary>
    public struct BottleKnockState : INetworkSerializeByMemcpy, System.IEquatable<BottleKnockState>
    {
        public byte Knocked;             // 0 = standing, 1 = knocked down
        public byte Settled;             // 0 = still falling, 1 = rest pose below is authoritative
        public Vector3 RestPosition;     // world-space rest position (valid when Settled == 1)
        public Quaternion RestRotation;  // world-space rest rotation (valid when Settled == 1)

        public bool Equals(BottleKnockState o) =>
            Knocked == o.Knocked && Settled == o.Settled
            && RestPosition == o.RestPosition && RestRotation == o.RestRotation;
        public override bool Equals(object o) => o is BottleKnockState s && Equals(s);
        public override int GetHashCode() =>
            Knocked ^ (Settled << 1) ^ RestPosition.GetHashCode() ^ RestRotation.GetHashCode();
    }

    readonly NetworkVariable<BottleKnockState> _knockState = new NetworkVariable<BottleKnockState>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    Rigidbody _rb;
    bool _knockedDown;
    bool _serverSettled;
    float _serverSettleTimer;

    public bool IsKnockedDown => _knockedDown;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.isKinematic = true;
    }

    public override void OnNetworkSpawn()
    {
        _knockState.OnValueChanged += OnKnockStateChanged;

        // Late join: reconstruct whatever the server already knows about this bottle. If it has already
        // settled we snap straight to the authoritative rest pose; if it is mid-fall we release it to
        // dynamic and let the settle publish snap us into agreement shortly after.
        if (!IsServer)
            ApplyNetworkKnockState(_knockState.Value);
    }

    public override void OnNetworkDespawn()
    {
        _knockState.OnValueChanged -= OnKnockStateChanged;
        base.OnNetworkDespawn();
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
            // Host playing locally: collision fires on the host's authoritative physics. Apply, replicate
            // the state, and mirror the crisp impulse to all observing clients.
            ServerApplyAndReplicateKnockdown(contactPoint, impulse);
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
    void RequestKnockdownServerRpc(Vector3 contactPoint, Vector3 impulse, ServerRpcParams rpcParams = default)
    {
        if (!IsServer || _knockedDown)
            return;
        if (!ServerValidateReportedHit(rpcParams.Receive.SenderClientId, ref contactPoint, ref impulse))
            return;
        ServerApplyAndReplicateKnockdown(contactPoint, impulse);
    }

    /// <summary>
    /// Gate a client-reported hit. The client owns the ball during its throw, so it (not the host) is the
    /// only witness to the collision — but we never trust its numbers blind: the sender must own a thrown
    /// object near this bottle, the contact point is snapped to the bottle (so a forged far point can't
    /// generate exotic torque), and the impulse magnitude is clamped.
    /// </summary>
    bool ServerValidateReportedHit(ulong senderClientId, ref Vector3 contactPoint, ref Vector3 impulse)
    {
        if (!IsSpawned)
            return false;

        // Reject a degenerate impulse — a zero shove would silently flag the bottle knocked without
        // moving it, letting a cheat mark bottles for score without a visible hit.
        if (impulse.sqrMagnitude < 1e-4f)
            return false;

        if (!ServerSenderHasThrowableNearBottle(senderClientId))
            return false;

        // Snap the contact point onto the bottle (clamp the lever arm) to bound AddForceAtPosition torque.
        Vector3 offset = contactPoint - transform.position;
        const float MaxContactLever = 0.5f;
        if (offset.sqrMagnitude > MaxContactLever * MaxContactLever)
            contactPoint = transform.position + Vector3.ClampMagnitude(offset, MaxContactLever);

        float mag = impulse.magnitude;
        if (mag > maxKnockdownImpulse)
            impulse *= maxKnockdownImpulse / mag;

        return true;
    }

    bool ServerSenderHasThrowableNearBottle(ulong senderClientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return false;

        float maxSq = maxReportedHitDistance * maxReportedHitDistance;
        Vector3 here = transform.position;
        foreach (KeyValuePair<ulong, NetworkObject> pair in nm.SpawnManager.SpawnedObjects)
        {
            NetworkObject no = pair.Value;
            if (no == null || !no.IsSpawned)
                continue;
            if (no.OwnerClientId != senderClientId)
                continue;
            if (no.GetComponent<HeavyThrowableHoldItem>() == null)
                continue;
            if ((no.transform.position - here).sqrMagnitude <= maxSq)
                return true;
        }

        return false;
    }

    void ServerApplyAndReplicateKnockdown(Vector3 contactPoint, Vector3 impulse)
    {
        if (!IsServer || _knockedDown)
            return;

        ApplyKnockdownLocal(contactPoint, impulse);

        // Replicate the state for late joiners + eventual rest-pose convergence, and mirror the precise
        // impulse to already-connected clients so their fall looks like the thrower's.
        _knockState.Value = new BottleKnockState { Knocked = 1, Settled = 0 };
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

    void FixedUpdate()
    {
        if (!IsServer || !_knockedDown || _serverSettled || _rb == null || _rb.isKinematic)
            return;

        bool slow = _rb.IsSleeping() || _rb.linearVelocity.sqrMagnitude <= settleSpeedThreshold * settleSpeedThreshold;
        if (slow)
        {
            _serverSettleTimer += Time.fixedDeltaTime;
            if (_serverSettleTimer >= settleHoldSeconds)
                ServerPublishRestPose();
        }
        else
        {
            _serverSettleTimer = 0f;
        }
    }

    void ServerPublishRestPose()
    {
        if (_serverSettled)
            return;
        _serverSettled = true;
        _knockState.Value = new BottleKnockState
        {
            Knocked = 1,
            Settled = 1,
            RestPosition = _rb.position,
            RestRotation = _rb.rotation,
        };
    }

    void OnKnockStateChanged(BottleKnockState previous, BottleKnockState current)
    {
        if (IsServer)
            return;
        ApplyNetworkKnockState(current);
    }

    /// <summary>Reconstruct replicated knock state on a non-server peer (late-join reconstruct + convergence).</summary>
    void ApplyNetworkKnockState(BottleKnockState state)
    {
        if (state.Knocked == 0)
            return;

        if (state.Settled != 0)
        {
            // Authoritative rest pose: snap and freeze so every peer agrees where the bottle lies.
            _knockedDown = true;
            if (_rb != null)
            {
                _rb.isKinematic = true;
                _rb.useGravity = false;
                _rb.position = state.RestPosition;
                _rb.rotation = state.RestRotation;
            }
            transform.SetPositionAndRotation(state.RestPosition, state.RestRotation);
            return;
        }

        // Knocked but not yet settled: release to dynamic so local physics is live. A late joiner that
        // missed the impulse broadcast may not topple on its own, but the impending settle publish will
        // snap it into agreement. Connected peers that already fell are guarded by _knockedDown.
        if (!_knockedDown)
        {
            _knockedDown = true;
            if (_rb != null)
            {
                _rb.isKinematic = false;
                _rb.useGravity = true;
            }
        }
    }
}
