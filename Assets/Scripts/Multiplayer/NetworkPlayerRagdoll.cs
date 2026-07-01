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

    // Owner samples its authoritative ragdoll pose at this cadence and broadcasts it; observers apply it as a
    // kinematic puppet. 20 Hz is fine-grained enough that the body looks continuous even during fast tumbles
    // and keeps bandwidth at ~6 KB/s per ragdolling player (~300 B / snapshot for 11 bones × pos+rot).
    const float RagdollPoseSyncIntervalSeconds = 0.05f;
    // Observers interpolate between received pose snapshots over this window so motion looks smooth at render
    // rate (60+ fps) instead of stuttering at the 20 Hz send cadence. Matching the send interval means
    // observers reach the latest sample just as the next one arrives; if a sample is delayed by jitter, the
    // body holds at the last target briefly.
    const float RagdollPoseInterpDuration = 0.05f;

    // Grab bone the enemy holds this player by, resolved by Mixamo bone name on every client so the hips
    // pin (see PlayerRagdollController.BeginHeldByPoint) tracks a transform all clients already agree on.
    static readonly string[] s_GrabBoneNames =
    {
        "mixamorig:RightHand", "mixamorig:LeftHand", "mixamorig:Hips", "mixamorig:Spine1"
    };

    [SerializeField] PlayerRagdollController ragdoll;
    [SerializeField] PlayerHealth playerHealth;
    [SerializeField] OwnerNetworkTransform ownerNetTransform;

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

    Vector3[] _ownerPosBuffer;
    Quaternion[] _ownerRotBuffer;
    float _nextPoseSyncTime;

    Vector3[] _observerPrevPos;
    Quaternion[] _observerPrevRot;
    Vector3[] _observerTargetPos;
    Quaternion[] _observerTargetRot;
    Vector3[] _observerLerpPosBuffer;
    Quaternion[] _observerLerpRotBuffer;
    float _observerInterpStart;
    bool _observerHasInterpData;

    void Awake()
    {
        if (ragdoll == null)
            ragdoll = GetComponent<PlayerRagdollController>();
        if (playerHealth == null)
            playerHealth = GetComponent<PlayerHealth>();
        if (ownerNetTransform == null)
            ownerNetTransform = GetComponent<OwnerNetworkTransform>();
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
        // the victim down, not standing. We enter the kinematic observer-puppet path; the next pose-stream
        // tick from the owner (within ~50 ms) populates the bones in the correct world pose. Owner is still
        // connected and runs recovery; the server fires StopRagdollClientRpc to clear everyone when it lands.
        if (!IsServer && !IsOwner && _transientRagdoll.Value.Active != 0 && ragdoll != null)
        {
            ragdoll.BeginObserverRagdoll();
            _observerHasInterpData = false;
        }
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

        // Flag the new pose as a teleport on NetworkTransform so observers skip interpolation. Without this,
        // observer NetworkTransforms are anchored at the (stationary) grab-start root position — the owner's
        // CharacterController/animator were off the whole ragdoll, so only the hips Rigidbody moved, and
        // NetworkTransform never saw a root delta to broadcast. Recovery suddenly sets transform.position to
        // the landing spot, and without a teleport flag the observer interpolates back toward the stale anchor
        // (the visible "snap to near the Clown grab spot" the player sees just after the body settles).
        if (ownerNetTransform != null)
            ownerNetTransform.Teleport(rootPosition, rootRotation, transform.localScale);

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

    // NOTE: trap hits are now adjudicated server-only (see RagdollTrap.TryHit). The former owner-invoke
    // RequestTrapHitServerRpc — where a non-authoritative client authored its own trap hit and the server merely
    // clamped the client-supplied force/damage — has been removed to eliminate the host/client hitbox desync.
    // The sole trap-hit entry point is now the server-only RequestTrapHitFromServer above.

    [ClientRpc]
    void StartRagdollClientRpc(Vector3 worldForce, Vector3 worldForcePosition, byte forceMode, bool allowAutoRecovery)
    {
        if (ragdoll == null)
            return;

        // Owner runs the authoritative physics simulation. Observers run a kinematic puppet driven entirely by
        // the streamed pose (see BroadcastRagdollPoseServerRpc / ApplyRagdollPoseClientRpc) — they do NOT apply
        // the impulse locally, because their joint-solver/float-precision results would diverge from the
        // owner's and produce the visible "snap to correct position when they start moving" bug.
        if (IsOwner)
        {
            ragdoll.ActivateRagdoll(
                worldForce,
                worldForcePosition,
                (ForceMode)forceMode,
                allowAutoRecovery: allowAutoRecovery);
            // First pose tick goes out as soon as the next Update runs — no need to wait the 50ms tick gate.
            _nextPoseSyncTime = 0f;
        }
        else
        {
            ragdoll.BeginObserverRagdoll();
            _observerHasInterpData = false;
        }
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

        // Owner simulates the slam; observers transition from the kinematic Clown carry directly into the
        // kinematic observer-puppet ragdoll. The pose stream picks up the slam trajectory from the owner —
        // observers no longer apply the launch impulse locally, which is what made them drift to a different
        // settle position before.
        if (IsOwner)
        {
            ragdoll.ReleaseFromHeld(
                worldForce,
                worldForcePosition,
                (ForceMode)forceMode,
                allowAutoRecovery: allowAutoRecovery);
            // Push the first slam pose tick out next Update, so observers don't wait 50ms for the first sample.
            _nextPoseSyncTime = 0f;
        }
        else
        {
            ragdoll.BeginObserverRagdoll();
            _observerHasInterpData = false;
            // Snap observers off the old enemy-hand position right away. The next pose-stream tick (~RPC
            // latency, often <50ms) will follow with the owner's full physics-integrated pose, but this gets
            // the body to roughly the right place immediately so there's no visible "stuck on the Clown's hand"
            // beat during the slam.
            ragdoll.SetObserverHipsPosition(worldForcePosition);
        }
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

    void Update()
    {
        if (!IsSpawned || ragdoll == null)
            return;

        if (IsOwner)
            OwnerSampleAndBroadcastPose();
        else
            ObserverInterpolatePose();
    }

    void OwnerSampleAndBroadcastPose()
    {
        // Stream the authoritative ragdoll pose to observers while we're either flailing (full ragdoll) or
        // being swung around in the rigid Clown carry. The carry was already deterministic across clients
        // (everyone pins to the same enemy bone), but funneling it through the same stream avoids relying on
        // observer-local pinning math during the swing.
        if (!ragdoll.IsRagdolled && !ragdoll.IsHeld)
            return;

        if (Time.time < _nextPoseSyncTime)
            return;
        _nextPoseSyncTime = Time.time + RagdollPoseSyncIntervalSeconds;

        int count = ragdoll.RagdollBodyCount;
        if (count == 0)
            return;

        if (_ownerPosBuffer == null || _ownerPosBuffer.Length != count)
            _ownerPosBuffer = new Vector3[count];
        if (_ownerRotBuffer == null || _ownerRotBuffer.Length != count)
            _ownerRotBuffer = new Quaternion[count];

        ragdoll.SampleOwnerRagdollPose(_ownerPosBuffer, _ownerRotBuffer);
        BroadcastRagdollPoseServerRpc(_ownerPosBuffer, _ownerRotBuffer);
    }

    void ObserverInterpolatePose()
    {
        // Render-rate interpolation between the last two received pose snapshots. Without this, observers see
        // the body update only at the 20 Hz send rate, which looks stuttery against a 60+ fps render rate.
        if (!_observerHasInterpData || !ragdoll.IsRagdolled)
            return;
        if (_observerTargetPos == null || _observerTargetPos.Length == 0)
            return;

        int count = _observerTargetPos.Length;
        if (_observerLerpPosBuffer == null || _observerLerpPosBuffer.Length != count)
            _observerLerpPosBuffer = new Vector3[count];
        if (_observerLerpRotBuffer == null || _observerLerpRotBuffer.Length != count)
            _observerLerpRotBuffer = new Quaternion[count];

        float t = Mathf.Clamp01((Time.time - _observerInterpStart) / RagdollPoseInterpDuration);
        for (int i = 0; i < count; i++)
        {
            _observerLerpPosBuffer[i] = Vector3.Lerp(_observerPrevPos[i], _observerTargetPos[i], t);
            _observerLerpRotBuffer[i] = Quaternion.Slerp(_observerPrevRot[i], _observerTargetRot[i], t);
        }

        ragdoll.ApplyObserverRagdollPose(_observerLerpPosBuffer, _observerLerpRotBuffer);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    void BroadcastRagdollPoseServerRpc(Vector3[] bonePositions, Quaternion[] boneRotations)
    {
        // Only forward while the server believes this player is mid-ragdoll or mid-carry. Without this gate, a
        // misbehaving client could stream pose updates outside of legitimate ragdoll windows and visually
        // teleport observer copies of the player.
        if (!_serverRagdollActive && _heldByEnemy.Value.Held == 0)
            return;

        ApplyRagdollPoseClientRpc(bonePositions, boneRotations);
    }

    [ClientRpc]
    void ApplyRagdollPoseClientRpc(Vector3[] bonePositions, Quaternion[] boneRotations)
    {
        if (IsOwner || ragdoll == null || bonePositions == null || boneRotations == null)
            return;

        int count = bonePositions.Length;
        if (count == 0 || boneRotations.Length != count)
            return;

        bool needAlloc = !_observerHasInterpData
                         || _observerPrevPos == null
                         || _observerPrevPos.Length != count;

        if (needAlloc)
        {
            _observerPrevPos = new Vector3[count];
            _observerPrevRot = new Quaternion[count];
            _observerTargetPos = new Vector3[count];
            _observerTargetRot = new Quaternion[count];

            // First snapshot for this ragdoll: prev == target, so the very next frame applies the sample with
            // no interp lurch.
            for (int i = 0; i < count; i++)
            {
                _observerPrevPos[i] = bonePositions[i];
                _observerPrevRot[i] = boneRotations[i];
                _observerTargetPos[i] = bonePositions[i];
                _observerTargetRot[i] = boneRotations[i];
            }
        }
        else
        {
            // Anchor prev at the pose we're rendering RIGHT NOW (the in-flight lerp value), then aim the next
            // interp at the new sample. Without this, a sample arriving before the prior interp completed
            // would discontinuously snap back to the old prev.
            float t = Mathf.Clamp01((Time.time - _observerInterpStart) / RagdollPoseInterpDuration);
            for (int i = 0; i < count; i++)
            {
                _observerPrevPos[i] = Vector3.Lerp(_observerPrevPos[i], _observerTargetPos[i], t);
                _observerPrevRot[i] = Quaternion.Slerp(_observerPrevRot[i], _observerTargetRot[i], t);
                _observerTargetPos[i] = bonePositions[i];
                _observerTargetRot[i] = boneRotations[i];
            }
        }

        _observerInterpStart = Time.time;
        _observerHasInterpData = true;
    }
}
