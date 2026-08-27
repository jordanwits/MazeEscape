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
    // Handover from the observer's local presentation launch (BeginObserverRagdollWithLocalLaunch) to the
    // authoritative stream. The stream renders the owner's flight a constant delay behind real time
    // (owner→server→observer legs + tick/frame latency), so handing over at the FIRST sample rewinds the
    // body by the whole in-flight gap — measured ~2.2 m of backward yank in a live trap test. The catch-up
    // test must be TEMPORAL, not spatial: near the arc's apex the trajectory is slow and spatially
    // compact, so a raw sim-vs-stream distance check fires mid-air while the stream is still ~half a
    // second behind, which rendered as the body hanging frozen at the apex until the stream caught up.
    // Instead both hips' cumulative path lengths are tracked from launch, and the puppet takes over only
    // once the stream has TRAVELED to within this many meters of the sim's path distance — or at the hard
    // cap, whichever comes first.
    const float ObserverHandoverPathCatchupMeters = 0.35f;
    const float ObserverHandoverMaxCoverSeconds = 2.0f;
    // The handover blend adds a decaying offset (sim pose − stream pose at handover) on top of the LIVE
    // stream, so the body always keeps moving with the stream's velocity — a frozen-pose lerp would hover
    // in place whenever the stream is still approaching the handover point. The decay duration scales with
    // the offset being absorbed (≈ this many m/s), clamped to the min/max window.
    const float ObserverHandoverBlendMetersPerSecond = 2.0f;
    const float ObserverHandoverBlendMinSeconds = 0.25f;
    const float ObserverHandoverBlendMaxSeconds = 0.6f;

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

    float _observerLocalSimStartTime;
    float _simHipsPathLen;
    Vector3 _simHipsPathLastPos;
    bool _simHipsPathInit;
    float _streamHipsPathLen;
    Vector3 _streamHipsPathLastPos;
    bool _streamHipsPathInit;
    Vector3[] _handoverPosOffset;
    Quaternion[] _handoverRotOffset;
    bool _handoverCrossfadeActive;
    float _handoverCrossfadeStart;
    float _handoverCrossfadeDuration;

    NetworkPlayerAvatar _avatar;

    void Awake()
    {
        if (ragdoll == null)
            ragdoll = GetComponent<PlayerRagdollController>();
        if (playerHealth == null)
            playerHealth = GetComponent<PlayerHealth>();
        if (ownerNetTransform == null)
            ownerNetTransform = GetComponent<OwnerNetworkTransform>();
        _avatar = GetComponent<NetworkPlayerAvatar>();
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
        // Vector3.one, not the current localScale: the player invariant is unit scale (see
        // NetworkPlayerAvatar.EnforceUnitWorldScale), so passing whatever scale happens to be on the root at
        // recovery would re-cement a corrupted value instead of letting the invariant repair it.
        if (ownerNetTransform != null)
            ownerNetTransform.Teleport(rootPosition, rootRotation, Vector3.one);

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
    /// Impact cue the hit RPC carries so every peer plays the same one positionally. Deliberately a kind and
    /// not a clip: the emitters are deterministic local scene objects present on all peers, so the RPC only
    /// has to say which family of sound this hit was.
    /// </summary>
    public enum TrapImpactSfxKind : byte
    {
        None = 0,
        TrapMetallic = 1,
    }

    /// <summary>
    /// Call from server-only code (e.g. trap OnTriggerEnter when IsServer).
    /// </summary>
    public void RequestTrapHitFromServer(Vector3 worldForce, Vector3 worldForcePosition, float damageAmount,
        ForceMode forceMode = ForceMode.Impulse, TrapImpactSfxKind impactSfxKind = TrapImpactSfxKind.None)
    {
        if (!IsServer || ragdoll == null || playerHealth == null)
            return;

        ulong id = OwnerClientId;
        float now = Time.time;
        if (s_TrapRagdollNextAllowedTime.TryGetValue(id, out float nextAllowed) && now < nextAllowed)
            return;

        s_TrapRagdollNextAllowedTime[id] = now + TrapRagdollServerCooldownSeconds;
        playerHealth.TakeDamage(damageAmount);
        BeginRagdollFromServer(worldForce, worldForcePosition, forceMode, allowAutoRecovery: true, impactSfxKind: impactSfxKind);
    }

    // NOTE: trap hits are now adjudicated server-only (see RagdollTrap.TryHit). The former owner-invoke
    // RequestTrapHitServerRpc — where a non-authoritative client authored its own trap hit and the server merely
    // clamped the client-supplied force/damage — has been removed to eliminate the host/client hitbox desync.
    // The sole trap-hit entry point is now the server-only RequestTrapHitFromServer above.

    [ClientRpc]
    void StartRagdollClientRpc(Vector3 worldForce, Vector3 worldForcePosition, byte forceMode, bool allowAutoRecovery,
        byte impactSfxKind)
    {
        // The impact cue rides the hit RPC rather than local trigger detection: a trap's trigger only fires for
        // the victim's own controller and (unreliably) the observers' blocking proxy, so observers used to lose
        // a race against the proxy suppression below and hear nothing. Resolved before the ragdoll work so the
        // sound is not delayed by it, and it plays on the host too (loop-back), which is why the server no
        // longer plays its own local copy.
        if (impactSfxKind == (byte)TrapImpactSfxKind.TrapMetallic)
            RagdollTrap.PlayNearestTrapImpactSfx(worldForcePosition);

        if (ragdoll == null)
            return;

        // Owner runs the authoritative physics simulation. Observers ultimately render a kinematic puppet
        // driven by the streamed pose (see BroadcastRagdollPoseServerRpc / ApplyRagdollPoseClientRpc), but the
        // first sample only lands ~the owner's full RTT after this RPC — waiting for it froze the victim in
        // the last animated pose and then snapped him into the stream. So observers launch a LOCAL,
        // presentation-only dynamic sim with the same server-authored impulse right now, and the pose handler
        // blends it onto the authoritative stream at the first sample. Divergence can't outlive that
        // data-in-flight window, so the old "each observer settles somewhere else" bug stays fixed.
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
            // The victim's own blocking-proxy capsule would overlap the freshly-dynamic bones and PhysX
            // would eject them, so suppress it before the launch. Restored at stream handover / stop.
            if (_avatar != null)
                _avatar.SetBlockingProxySuppressedForRagdoll(true);
            ragdoll.BeginObserverRagdollWithLocalLaunch(worldForce, worldForcePosition, (ForceMode)forceMode);
            _observerLocalSimStartTime = Time.time;
            _observerHasInterpData = false;
            _handoverCrossfadeActive = false;
            _simHipsPathInit = false;
            _simHipsPathLen = 0f;
            _streamHipsPathInit = false;
            _streamHipsPathLen = 0f;
        }
    }

    /// <summary>
    /// Server: relay a pit's spike stab for this player's kill to every peer. The pit is plain local geometry
    /// with no NetworkObject of its own, so the position rides the Rpc and each peer resolves its own copy of
    /// the pit from it — same treatment as the trap clank above, and for the same reason: an observer's
    /// proxy-vs-trigger race against death replication usually loses, so it heard nothing.
    /// </summary>
    public void BroadcastPitStabSfxFromServer(Vector3 worldPosition)
    {
        if (!IsServer || !IsSpawned)
            return;

        PlayPitStabSfxClientRpc(worldPosition);
    }

    [ClientRpc]
    void PlayPitStabSfxClientRpc(Vector3 worldPosition)
    {
        PitKillZone.PlayNearestPitStabSfx(worldPosition);
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
    public void BeginHeldByEnemyFromServer(ulong enemyNetworkObjectId, int grabBoneIndex, Vector3 localPos, Vector3 localEuler,
        Vector3 approachLocalPos, float approachSeconds)
    {
        if (!IsServer || ragdoll == null)
            return;

        _serverRagdollActive = true;
        // The persistent snapshot stores only the final hold offset — a late joiner adopts the settled grip,
        // it does not replay the approach glide (a sub-second transient).
        _heldByEnemy.Value = new HeldByEnemyState
        {
            Held = 1,
            EnemyNetObjId = enemyNetworkObjectId,
            GrabBoneIndex = grabBoneIndex,
            LocalPos = localPos,
            LocalEuler = localEuler,
        };
        StartHeldRagdollClientRpc(enemyNetworkObjectId, grabBoneIndex, localPos, localEuler, approachLocalPos, approachSeconds);
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
    void StartHeldRagdollClientRpc(ulong enemyNetObjId, int grabBoneIndex, Vector3 localPos, Vector3 localEuler,
        Vector3 approachLocalPos, float approachSeconds)
    {
        if (ragdoll == null)
            return;

        Transform grabBone = ResolveEnemyGrabBone(enemyNetObjId, grabBoneIndex);
        if (grabBone == null)
            return;

        ragdoll.BeginHeldByPoint(grabBone, localPos, Quaternion.Euler(localEuler), approachLocalPos, approachSeconds);
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

    void BeginRagdollFromServer(Vector3 worldForce, Vector3 worldForcePosition, ForceMode forceMode, bool allowAutoRecovery,
        TrapImpactSfxKind impactSfxKind = TrapImpactSfxKind.None)
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
        StartRagdollClientRpc(worldForce, worldForcePosition, (byte)forceMode, allowAutoRecovery, (byte)impactSfxKind);
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

        // A stop can land while the local launch sim is still covering (recovery before the gap ever
        // closed), so put the bones back to kinematic BEFORE the proxy capsule returns. All of these are
        // no-ops in the normal case (sim already handed over, suppression already released).
        ragdoll.EndObserverLocalSimToPuppet();
        if (_avatar != null)
            _avatar.SetBlockingProxySuppressedForRagdoll(false);
        _handoverCrossfadeActive = false;

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
        {
            OwnerSampleAndBroadcastPose();
        }
        else
        {
            // Sim path must accumulate from launch, including the pre-first-sample window — the stream
            // path replays that same stretch later, and undercounting the sim side would make the
            // catch-up test fire early (the mid-air freeze all over again).
            if (ragdoll.IsObserverLocalSimActive)
                AccumulateSimHipsPath();
            ObserverInterpolatePose();
        }
    }

    void AccumulateSimHipsPath()
    {
        Transform hips = ragdoll.HipsTransform;
        if (hips == null)
            return;

        Vector3 p = hips.position;
        if (_simHipsPathInit)
            _simHipsPathLen += (p - _simHipsPathLastPos).magnitude;
        else
            _simHipsPathInit = true;
        _simHipsPathLastPos = p;
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

        // While the local launch sim covers, the stream above is tracking only. Hand the bones to the
        // puppet once the stream has caught up (or the cap forces it); until then physics keeps rendering.
        if (ragdoll.IsObserverLocalSimActive)
        {
            AccumulateStreamHipsPath(count);
            if (!TryBeginObserverHandover(count))
                return;
        }

        if (_handoverCrossfadeActive)
        {
            float w = Mathf.Clamp01((Time.time - _handoverCrossfadeStart) / _handoverCrossfadeDuration);
            w = 1f - w * w * (3f - 2f * w); // smoothstep decay 1 → 0
            if (w <= 0f)
            {
                _handoverCrossfadeActive = false;
            }
            else
            {
                // Live stream pose + decaying offset: the body keeps the stream's velocity throughout the
                // blend instead of hovering at a frozen handover pose while the stream approaches it.
                for (int i = 0; i < count && i < _handoverPosOffset.Length; i++)
                {
                    _observerLerpPosBuffer[i] += _handoverPosOffset[i] * w;
                    _observerLerpRotBuffer[i] =
                        Quaternion.Slerp(Quaternion.identity, _handoverRotOffset[i], w) * _observerLerpRotBuffer[i];
                }
            }
        }

        ragdoll.ApplyObserverRagdollPose(_observerLerpPosBuffer, _observerLerpRotBuffer);
    }

    void AccumulateStreamHipsPath(int count)
    {
        int hipsIndex = ragdoll.HipsBodyIndex;
        if (hipsIndex < 0 || hipsIndex >= count)
            return;

        Vector3 p = _observerLerpPosBuffer[hipsIndex];
        if (_streamHipsPathInit)
            _streamHipsPathLen += (p - _streamHipsPathLastPos).magnitude;
        else
            _streamHipsPathInit = true;
        _streamHipsPathLastPos = p;
    }

    /// <summary>
    /// Decides the local-sim → stream handover. Both trajectories cover the same flight, offset by the
    /// stream's constant lag, so the stream's traveled path length reaching the sim's means it has caught
    /// up in TIME — the violent part of the knockback always renders from the immediate local sim, and the
    /// puppet takes over where the remaining correction is small. (A spatial sim-vs-stream distance check
    /// is NOT sufficient: near the arc apex the two poses pass within centimeters while the stream is
    /// still half a second behind.) The cap bounds settle divergence from different local contacts; the
    /// offset-decay blend absorbs whatever residual gap remains at either trigger. Returns true once the
    /// puppet owns the bones.
    /// </summary>
    bool TryBeginObserverHandover(int count)
    {
        bool caughtUp = _simHipsPathInit && _streamHipsPathInit
                        && _streamHipsPathLen >= _simHipsPathLen - ObserverHandoverPathCatchupMeters;
        bool capReached = Time.time - _observerLocalSimStartTime >= ObserverHandoverMaxCoverSeconds;
        if (!caughtUp && !capReached)
            return false;

        if (_handoverPosOffset == null || _handoverPosOffset.Length != count)
        {
            _handoverPosOffset = new Vector3[count];
            _handoverRotOffset = new Quaternion[count];
        }

        float maxOffset = 0f;
        if (count == ragdoll.RagdollBodyCount)
        {
            // Offset = sim pose − stream pose at this instant, per bone (sampled into the offset arrays,
            // then converted in place).
            ragdoll.SampleOwnerRagdollPose(_handoverPosOffset, _handoverRotOffset);
            for (int i = 0; i < count; i++)
            {
                _handoverPosOffset[i] -= _observerLerpPosBuffer[i];
                _handoverRotOffset[i] = _handoverRotOffset[i] * Quaternion.Inverse(_observerLerpRotBuffer[i]);
                float m = _handoverPosOffset[i].magnitude;
                if (m > maxOffset)
                    maxOffset = m;
            }
        }
        else
        {
            // Bone-count mismatch (shouldn't happen — all peers run the same rig): degrade to a plain cut.
            for (int i = 0; i < count; i++)
            {
                _handoverPosOffset[i] = Vector3.zero;
                _handoverRotOffset[i] = Quaternion.identity;
            }
        }

        // Bones kinematic first, then the blocking proxy may return — never both dynamic and solid.
        ragdoll.EndObserverLocalSimToPuppet();
        if (_avatar != null)
            _avatar.SetBlockingProxySuppressedForRagdoll(false);

        _handoverCrossfadeActive = true;
        _handoverCrossfadeStart = Time.time;
        _handoverCrossfadeDuration = Mathf.Clamp(
            maxOffset / ObserverHandoverBlendMetersPerSecond,
            ObserverHandoverBlendMinSeconds,
            ObserverHandoverBlendMaxSeconds);
        return true;
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

        // While the local launch sim is active, these samples are only TRACKED (prev/target updated) so the
        // stream's current pose is always known — ObserverInterpolatePose decides when the puppet takes
        // over (gap-gated handover) and until then never applies them to the dynamic bones.
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
