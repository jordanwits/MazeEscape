using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative request: when a trap runs on the host, it can knock down the owning client.
/// Ragdoll simulation runs on the owner (physics + OwnerNetworkTransform).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public class NetworkPlayerRagdoll : NetworkBehaviour
{
    const float TrapRagdollServerCooldownSeconds = 0.45f;
    static readonly Dictionary<ulong, float> s_TrapRagdollNextAllowedTime = new Dictionary<ulong, float>();

    // Grab bone the enemy holds this player by, resolved by Mixamo bone name on every client so the hips
    // pin (see PlayerRagdollController.BeginHeldByPoint) tracks a transform all clients already agree on.
    static readonly string[] s_GrabBoneNames =
    {
        "mixamorig:RightHand", "mixamorig:LeftHand", "mixamorig:Hips", "mixamorig:Spine1"
    };

    [SerializeField] PlayerRagdollController ragdoll;
    [SerializeField] PlayerHealth playerHealth;

    bool _serverRagdollActive;
    bool _subscribedToRecoveryStarted;

    /// <summary>
    /// Late-join snapshot of the persistent "held by an enemy" pose (the Clown grab). The grab is driven on
    /// already-connected clients by the one-shot <see cref="StartHeldRagdollClientRpc"/>; a client that joins
    /// mid-grab never receives that Rpc, so without a replicated value it would draw the victim standing
    /// upright. Server-write only; observers rebuild the pose from it on spawn (see <see cref="OnNetworkSpawn"/>).
    /// </summary>
    public struct HeldByEnemyState : INetworkSerializeByMemcpy, System.IEquatable<HeldByEnemyState>
    {
        public byte Held;          // 0 = not held, 1 = held
        public ulong EnemyNetObjId;
        public int GrabBoneIndex;
        public Vector3 LocalPos;
        public Vector3 LocalEuler;

        public bool Equals(HeldByEnemyState o) =>
            Held == o.Held && EnemyNetObjId == o.EnemyNetObjId && GrabBoneIndex == o.GrabBoneIndex
            && LocalPos == o.LocalPos && LocalEuler == o.LocalEuler;
        public override bool Equals(object o) => o is HeldByEnemyState s && Equals(s);
        public override int GetHashCode() => (int)(Held ^ EnemyNetObjId) ^ GrabBoneIndex;
    }

    readonly NetworkVariable<HeldByEnemyState> _heldByEnemy = new NetworkVariable<HeldByEnemyState>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>
    /// Late-join snapshot of the transient (trap or death) ragdoll. The active visual is driven by
    /// <see cref="StartRagdollClientRpc"/> on already-connected clients; a brand-new client that joins
    /// mid-trap or mid-death would never receive that Rpc and would draw the victim standing upright.
    /// Server-write only; observers rebuild the down pose from it on spawn (see <see cref="OnNetworkSpawn"/>).
    /// AllowAutoRecovery is recorded so a death snapshot (no auto stand-up) stays distinguishable from a
    /// trap snapshot, but reconstruction never starts recovery routines for the observer — the owner runs
    /// recovery and the server fires <see cref="StopRagdollClientRpc"/> when they're back up.
    /// </summary>
    public struct TransientRagdollState : INetworkSerializeByMemcpy, System.IEquatable<TransientRagdollState>
    {
        public byte Active;                 // 0 = standing, 1 = down (trap or death)
        public byte AllowAutoRecovery;      // 0 = death-down, 1 = trap-down (informational only on observer)

        public bool Equals(TransientRagdollState o) =>
            Active == o.Active && AllowAutoRecovery == o.AllowAutoRecovery;
        public override bool Equals(object o) => o is TransientRagdollState s && Equals(s);
        public override int GetHashCode() => Active ^ (AllowAutoRecovery << 1);
    }

    readonly NetworkVariable<TransientRagdollState> _transientRagdoll = new NetworkVariable<TransientRagdollState>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    Coroutine _heldReconstructRoutine;

    void Awake()
    {
        if (ragdoll == null)
            ragdoll = GetComponent<PlayerRagdollController>();
        if (playerHealth == null)
            playerHealth = GetComponent<PlayerHealth>();
    }

    public override void OnNetworkSpawn()
    {
        if (ragdoll != null && !_subscribedToRecoveryStarted)
        {
            ragdoll.RecoveryStarted += OnLocalRagdollRecoveryStarted;
            _subscribedToRecoveryStarted = true;
        }

        // Spawn-time state rule: a client that joins while this player is mid-grab must rebuild the held
        // pose from the replicated snapshot (the one-shot grab Rpc already played for everyone who was
        // connected at grab time). We do NOT subscribe to OnValueChanged: connected clients keep using the
        // Rpc path, so this can never double-apply. Only observers reconstruct — the server is authoritative
        // and the owner runs its own ragdoll via the Rpc it received while connected.
        if (!IsServer && !IsOwner && _heldByEnemy.Value.Held != 0)
            _heldReconstructRoutine = StartCoroutine(ReconstructHeldOnSpawnRoutine(_heldByEnemy.Value));

        // Same pattern for trap/death ragdoll-down. An observer late joining mid-trap or mid-death must see
        // the victim down, not standing. We pass zero force on reconstruction: the original launch happened
        // before this client joined, so the body is already wherever the physics carried it — applying the
        // original impulse here would look like a fresh hit. allowAutoRecovery is forced false because the
        // observer is not the owner; the real owner's client (still connected) drives recovery and the
        // server fires StopRagdollClientRpc to clear everyone when it lands.
        if (!IsServer && !IsOwner && _transientRagdoll.Value.Active != 0 && ragdoll != null)
            ragdoll.ActivateRagdoll(Vector3.zero, Vector3.zero, ForceMode.Impulse, allowAutoRecovery: false);
    }

    public override void OnNetworkDespawn()
    {
        if (_subscribedToRecoveryStarted && ragdoll != null)
        {
            ragdoll.RecoveryStarted -= OnLocalRagdollRecoveryStarted;
            _subscribedToRecoveryStarted = false;
        }

        if (_heldReconstructRoutine != null)
        {
            StopCoroutine(_heldReconstructRoutine);
            _heldReconstructRoutine = null;
        }

        // Per-client trap cooldown lives in a static dict keyed by OwnerClientId; drop our entry on despawn
        // so a long-lived host doesn't leak one slot per join across a multi-hour session.
        if (IsServer)
            s_TrapRagdollNextAllowedTime.Remove(OwnerClientId);
    }

    void OnLocalRagdollRecoveryStarted(Vector3 rootPosition, Quaternion rootRotation, bool onBack)
    {
        // Owner-only: replicate the authoritative recovery pose to observers. Each observer's local ragdoll
        // physics has settled in a slightly different spot, so without this they call DeactivateRagdoll using
        // their own (drifted) hips and stand up off-position until OwnerNetworkTransform catches up — that's
        // the visible "snap to correct position when they start moving again" symptom.
        if (!IsSpawned || !IsOwner)
            return;
        NotifyRecoveryStartedServerRpc(rootPosition, rootRotation, onBack);
    }

    // The enemy NetworkObject and its rig may resolve a few frames after this player spawns on a late
    // joiner, so retry briefly. Bails out (leaving the player standing — i.e. today's behavior, no worse)
    // if the enemy never resolves or the grab ends before we apply it.
    System.Collections.IEnumerator ReconstructHeldOnSpawnRoutine(HeldByEnemyState state)
    {
        const int maxFrames = 180; // ~3s at 60 fps
        for (int i = 0; i < maxFrames; i++)
        {
            if (!IsSpawned || IsOwner || _heldByEnemy.Value.Held == 0 || ragdoll == null)
            {
                _heldReconstructRoutine = null;
                yield break;
            }

            Transform grabBone = ResolveEnemyGrabBone(state.EnemyNetObjId, state.GrabBoneIndex);
            if (grabBone != null)
            {
                ragdoll.BeginHeldByPoint(grabBone, state.LocalPos, Quaternion.Euler(state.LocalEuler));
                _heldReconstructRoutine = null;
                yield break;
            }

            yield return null;
        }
        _heldReconstructRoutine = null;
    }

    /// <summary>
    /// Call from server-only code (e.g. trap OnTriggerEnter when IsServer).
    /// </summary>
    public void RequestTrapHitFromServer(Vector3 worldForce, Vector3 worldForcePosition, float damageAmount, ForceMode forceMode = ForceMode.Impulse)
    {
        if (!IsServer || ragdoll == null || playerHealth == null)
            return;

        ulong id = OwnerClientId;
        float now = Time.time;
        if (s_TrapRagdollNextAllowedTime.TryGetValue(id, out float nextAllowed) && now < nextAllowed)
            return;

        s_TrapRagdollNextAllowedTime[id] = now + TrapRagdollServerCooldownSeconds;
        playerHealth.TakeDamage(damageAmount);
        BeginRagdollFromServer(worldForce, worldForcePosition, forceMode, allowAutoRecovery: true);
    }

    /// <summary>
    /// Call from a client-owned collider (e.g. trap trigger on the joining player). Server relays to owner via ClientRpc.
    /// </summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    public void RequestTrapHitServerRpc(Vector3 worldForce, Vector3 worldForcePosition, float damageAmount, byte forceMode)
    {
        // The damage and force here are client-supplied. The existing per-client cooldown in
        // RequestTrapHitFromServer limits the *rate*, but a malicious client could still send arbitrary
        // values; clamp them to safe bounds (well above any legitimate trap config) so a hit never deals
        // more than a real trap could.
        const float ServerMaxTrapDamage = 50f;
        const float ServerMaxTrapForce = 50f;
        if (damageAmount < 0f) damageAmount = 0f;
        if (damageAmount > ServerMaxTrapDamage) damageAmount = ServerMaxTrapDamage;
        float maxSqr = ServerMaxTrapForce * ServerMaxTrapForce;
        if (worldForce.sqrMagnitude > maxSqr)
            worldForce = worldForce.normalized * ServerMaxTrapForce;

        RequestTrapHitFromServer(worldForce, worldForcePosition, damageAmount, (ForceMode)forceMode);
    }

    [ClientRpc]
    void StartRagdollClientRpc(Vector3 worldForce, Vector3 worldForcePosition, byte forceMode, bool allowAutoRecovery)
    {
        if (ragdoll == null)
            return;

        ragdoll.ActivateRagdoll(
            worldForce,
            worldForcePosition,
            (ForceMode)forceMode,
            allowAutoRecovery: IsOwner && allowAutoRecovery);
    }

    /// <summary>
    /// Call from server when the player dies so the owning client runs ragdoll physics (no auto stand-up until respawn).
    /// </summary>
    public void NotifyDeathRagdollFromServer()
    {
        if (!IsServer || ragdoll == null)
            return;

        BeginRagdollFromServer(Vector3.zero, Vector3.zero, ForceMode.Impulse, allowAutoRecovery: false);
    }

    /// <summary>
    /// Server: an enemy (e.g. the Clown) grabs this player. The player goes limp and the hips are pinned to
    /// the enemy's grab bone on every client. Pair with <see cref="ReleaseSlamFromServer"/>.
    /// </summary>
    public void BeginHeldByEnemyFromServer(ulong enemyNetworkObjectId, int grabBoneIndex, Vector3 localPos, Vector3 localEuler)
    {
        if (!IsServer || ragdoll == null)
            return;

        _serverRagdollActive = true;
        _heldByEnemy.Value = new HeldByEnemyState
        {
            Held = 1,
            EnemyNetObjId = enemyNetworkObjectId,
            GrabBoneIndex = grabBoneIndex,
            LocalPos = localPos,
            LocalEuler = localEuler,
        };
        StartHeldRagdollClientRpc(enemyNetworkObjectId, grabBoneIndex, localPos, localEuler);
    }

    /// <summary>
    /// Server: release a held player into a slam. Applies damage, then launches the ragdoll with the given
    /// world force on every client. The player auto-recovers unless the slam killed them.
    /// </summary>
    public void ReleaseSlamFromServer(Vector3 worldForce, Vector3 worldForcePosition, float damageAmount, byte forceMode)
    {
        if (!IsServer || ragdoll == null)
            return;

        bool survived = true;
        if (playerHealth != null && damageAmount > 0f)
        {
            playerHealth.TakeDamage(damageAmount);
            survived = !playerHealth.IsDead;
        }

        // The rigid held pose is over (it becomes a transient slam ragdoll handled by the Rpc path), so
        // clear the late-join snapshot. A client joining after this sees the player standing/recovering,
        // not pinned to the enemy.
        _heldByEnemy.Value = default;

        // Record the transient ragdoll snapshot so a late joiner during the slam's auto-recovery window
        // sees the victim down. If the slam killed, the death path (NotifyDeathRagdollFromServer →
        // BeginRagdollFromServer) will also write this NV with AllowAutoRecovery=0; either write produces
        // the same observer outcome (down on spawn).
        _transientRagdoll.Value = new TransientRagdollState
        {
            Active = 1,
            AllowAutoRecovery = (byte)(survived ? 1 : 0),
        };

        // When the slam kills, the death flow owns recovery (stays down until respawn); don't auto-stand.
        ReleaseHeldRagdollClientRpc(worldForce, worldForcePosition, forceMode, survived);
    }

    [ClientRpc]
    void StartHeldRagdollClientRpc(ulong enemyNetObjId, int grabBoneIndex, Vector3 localPos, Vector3 localEuler)
    {
        if (ragdoll == null)
            return;

        Transform grabBone = ResolveEnemyGrabBone(enemyNetObjId, grabBoneIndex);
        if (grabBone == null)
            return;

        ragdoll.BeginHeldByPoint(grabBone, localPos, Quaternion.Euler(localEuler));
    }

    [ClientRpc]
    void ReleaseHeldRagdollClientRpc(Vector3 worldForce, Vector3 worldForcePosition, byte forceMode, bool allowAutoRecovery)
    {
        if (ragdoll == null)
            return;

        ragdoll.ReleaseFromHeld(
            worldForce,
            worldForcePosition,
            (ForceMode)forceMode,
            allowAutoRecovery: IsOwner && allowAutoRecovery);
    }

    static Transform ResolveEnemyGrabBone(ulong enemyNetObjId, int boneIndex)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return null;
        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(enemyNetObjId, out NetworkObject enemy) || enemy == null)
            return null;

        return FindGrabBone(enemy.transform, boneIndex);
    }

    /// <summary>
    /// Finds the grab bone (by Mixamo name) under <paramref name="enemyRoot"/> for the given index. Shared by
    /// the networked client path and the offline/server grab logic so both pin the player to the same bone.
    /// </summary>
    public static Transform FindGrabBone(Transform enemyRoot, int boneIndex)
    {
        if (enemyRoot == null || boneIndex < 0 || boneIndex >= s_GrabBoneNames.Length)
            return null;

        string boneName = s_GrabBoneNames[boneIndex];
        Transform[] all = enemyRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].name == boneName)
                return all[i];
        }
        return null;
    }

    void BeginRagdollFromServer(Vector3 worldForce, Vector3 worldForcePosition, ForceMode forceMode, bool allowAutoRecovery)
    {
        if (!IsServer || ragdoll == null)
            return;

        _serverRagdollActive = true;
        // A full ragdoll (trap/death) supersedes any rigid held pose; clear the held snapshot.
        _heldByEnemy.Value = default;
        // Record the transient ragdoll snapshot so late joiners reconstruct the down pose on spawn.
        _transientRagdoll.Value = new TransientRagdollState
        {
            Active = 1,
            AllowAutoRecovery = (byte)(allowAutoRecovery ? 1 : 0),
        };
        StartRagdollClientRpc(worldForce, worldForcePosition, (byte)forceMode, allowAutoRecovery);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    void NotifyRecoveryStartedServerRpc(Vector3 rootPosition, Quaternion rootRotation, bool onBack)
    {
        if (!_serverRagdollActive)
            return;

        _serverRagdollActive = false;
        // Owner is back up; clear the late-join snapshot so a brand-new joiner won't reconstruct the
        // already-finished trap/death pose.
        if (_transientRagdoll.Value.Active != 0)
            _transientRagdoll.Value = default;
        StopRagdollClientRpc(playRecoveryAnimation: true, rootPosition, rootRotation, onBack);
    }

    public void ForceExitRagdollFromServer()
    {
        // Clear the held snapshot even if no server ragdoll was active (e.g. respawn while held): a
        // respawned player must never reconstruct as grabbed on a late joiner.
        if (IsServer && _heldByEnemy.Value.Held != 0)
            _heldByEnemy.Value = default;
        // Same for the trap/death snapshot: respawn must scrub it so a late joiner doesn't see the player
        // dead-down at the new spawn point.
        if (IsServer && _transientRagdoll.Value.Active != 0)
            _transientRagdoll.Value = default;

        if (!IsServer || !_serverRagdollActive)
            return;

        _serverRagdollActive = false;
        // Forced exit doesn't play a get-up animation, so the authoritative pose is irrelevant — observers
        // just kill ragdoll and let OwnerNetworkTransform stream whatever new position the respawn applies.
        StopRagdollClientRpc(playRecoveryAnimation: false, Vector3.zero, Quaternion.identity, false);
    }

    [ClientRpc]
    void StopRagdollClientRpc(bool playRecoveryAnimation, Vector3 rootPosition, Quaternion rootRotation, bool onBack)
    {
        if (ragdoll == null)
            return;

        if (!playRecoveryAnimation)
        {
            ragdoll.ForceExitRagdollWithoutGroundSnap();
            return;
        }

        // Owner already ran DeactivateRagdoll locally — that's what produced this RPC. Don't re-run it; the
        // get-up coroutine is already in flight and DeactivateRagdoll would no-op anyway (IsRagdolled false).
        if (IsOwner)
            return;

        ragdoll.DeactivateRagdollAtAuthoritativeRoot(rootPosition, rootRotation, onBack);
    }
}
