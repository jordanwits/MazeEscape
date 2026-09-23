using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Network glue for the Mutant (Level03's final boss). Same shape as <see cref="NetworkSecurityGuardAvatar"/>: the
/// server simulates AI and movement, a <see cref="ServerNetworkAnimator"/> replicates the animator (locomotion
/// params + every ability cross-fade), and clients keep <see cref="MutantAI"/> enabled for presentation only.
///
/// What the Mutant adds is procedural presentation that is NOT in the animator — stretching limbs, the hips
/// spin, blackouts, blow-outs. Those are timelines, so instead of streaming bones the server sends each one
/// ONCE with its parameters and every peer plays it locally (<c>Broadcast*</c> below: the server/offline peer
/// applies it directly, the Rpc reaches everyone else). Persistent facts late joiners need — dead, awake,
/// phase, who his head is watching, where he shrieked — ride NetworkVariables.
///
/// Also relays shoves to a player's OWNER (their CharacterController is owner-authoritative) and teleports
/// through <see cref="NetworkTransform.Teleport"/> so observers snap instead of lerping across the corridor.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(MutantAI))]
[RequireComponent(typeof(MutantHealth))]
public class NetworkMutantAvatar : NetworkBehaviour
{
    const ulong NoTarget = ulong.MaxValue;

    [SerializeField] Animator mutantAnimator;
    [SerializeField] MutantAI mutantAI;
    [SerializeField] MutantHealth mutantHealth;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;

    ServerNetworkAnimator _serverNetworkAnimator;
    NetworkTransform _networkTransform;

    readonly NetworkVariable<bool> _isDead = new(false);
    readonly NetworkVariable<bool> _isAwake = new(false);
    readonly NetworkVariable<byte> _phase = new(1);
    readonly NetworkVariable<ulong> _lookTargetId = new(NoTarget);
    readonly NetworkVariable<bool> _shrieked = new(false);
    readonly NetworkVariable<Vector3> _shriekCenter = new(Vector3.zero);

    bool ServerSpawned => IsSpawned && IsServer;

    void Awake()
    {
        if (mutantAnimator == null)
            mutantAnimator = GetComponent<Animator>();
        if (mutantAI == null)
            mutantAI = GetComponent<MutantAI>();
        if (mutantHealth == null)
            mutantHealth = GetComponent<MutantHealth>();
        if (navMeshAgent == null)
            navMeshAgent = GetComponent<NavMeshAgent>();
        if (characterController == null)
            characterController = GetComponent<CharacterController>();
        _networkTransform = GetComponent<NetworkTransform>();

        EnsureAnimationSync();
    }

    public override void OnNetworkSpawn()
    {
        _isDead.OnValueChanged += HandleDeadChanged;
        _isAwake.OnValueChanged += HandleAwakeChanged;
        _phase.OnValueChanged += HandlePhaseChanged;
        _lookTargetId.OnValueChanged += HandleLookTargetChanged;

        ApplyAuthorityState();

        if (IsServer)
            return;

        // Late joiners inherit the fight as it stands.
        if (_isAwake.Value)
            mutantAI.ApplyReplicatedAwake();
        mutantAI.ApplyReplicatedPhase(_phase.Value);
        ApplyLookTarget(_lookTargetId.Value);
        if (_shrieked.Value)
            mutantAI.PresentShriekBlowOut(_shriekCenter.Value);
        ApplyDeadState(_isDead.Value);
    }

    public override void OnNetworkDespawn()
    {
        _isDead.OnValueChanged -= HandleDeadChanged;
        _isAwake.OnValueChanged -= HandleAwakeChanged;
        _phase.OnValueChanged -= HandlePhaseChanged;
        _lookTargetId.OnValueChanged -= HandleLookTargetChanged;
    }

    void Update()
    {
        if (!ServerSpawned || mutantHealth == null)
            return;

        if (_isDead.Value != mutantHealth.IsDead)
            _isDead.Value = mutantHealth.IsDead;
    }

    // ------------------------------------------------------------------
    // Persistent state (server writes; clients react)
    // ------------------------------------------------------------------

    public void ServerSetAwake()
    {
        if (ServerSpawned)
            _isAwake.Value = true;
    }

    public void ServerSetPhase(int phase)
    {
        if (ServerSpawned)
            _phase.Value = (byte)Mathf.Clamp(phase, 1, 255);
    }

    /// <summary>Who the owl neck tracks on every peer (null clears).</summary>
    public void ServerSetLookTarget(Transform target)
    {
        if (!ServerSpawned)
            return;

        NetworkObject no = target != null ? target.GetComponentInParent<NetworkObject>() : null;
        ulong id = no != null && no.IsSpawned ? no.NetworkObjectId : NoTarget;
        if (_lookTargetId.Value != id)
            _lookTargetId.Value = id;
    }

    void HandleAwakeChanged(bool previous, bool current)
    {
        if (current && !IsServer)
            mutantAI.ApplyReplicatedAwake();
    }

    void HandlePhaseChanged(byte previous, byte current)
    {
        if (!IsServer)
            mutantAI.ApplyReplicatedPhase(current);
    }

    void HandleLookTargetChanged(ulong previous, ulong current)
    {
        if (!IsServer)
            ApplyLookTarget(current);
    }

    void ApplyLookTarget(ulong id)
    {
        Transform target = null;
        NetworkManager nm = NetworkManager.Singleton;
        if (id != NoTarget && nm != null && nm.SpawnManager != null
            && nm.SpawnManager.SpawnedObjects.TryGetValue(id, out NetworkObject no) && no != null)
            target = no.transform;
        mutantAI.ApplyReplicatedLookTarget(target);
    }

    void HandleDeadChanged(bool previous, bool current) => ApplyDeadState(current);

    void ApplyDeadState(bool isDead)
    {
        if (!isDead)
            return;

        mutantAI.HandleDeath();

        // The server drops the corpse's CharacterController once the fall is over; observers drop their
        // kinematic stand-in on the same delay, or the body keeps blocking the hall on clients only.
        if (characterController != null && !IsServer)
            StartCoroutine(DropCollisionProxyRoutine());
    }

    IEnumerator DropCollisionProxyRoutine()
    {
        float delay = mutantHealth != null ? mutantHealth.DisableColliderDelay : 0f;
        if (delay > 0f)
            yield return new WaitForSeconds(delay);
        EnemyClientCollisionProxy.Deactivate(characterController);
    }

    void ApplyAuthorityState()
    {
        bool shouldSimulate = !NetworkManager.Singleton || !NetworkManager.Singleton.IsListening || IsServer;

        if (navMeshAgent != null)
            navMeshAgent.enabled = shouldSimulate;
        if (characterController != null)
            characterController.enabled = shouldSimulate;

        // Observers keep him solid for their own player and thrown props via a mirrored kinematic capsule.
        EnemyClientCollisionProxy.Apply(characterController, shouldSimulate);
    }

    // ------------------------------------------------------------------
    // One-shot presentation timelines (server/offline applies locally; the Rpc reaches everyone else)
    // ------------------------------------------------------------------

    public void BroadcastReachWindup(int side, float seconds)
    {
        mutantAI.PresentReachWindup(side, seconds);
        if (ServerSpawned)
            ReachWindupRpc((byte)side, seconds);
    }

    [Rpc(SendTo.NotServer)]
    void ReachWindupRpc(byte side, float seconds) => mutantAI.PresentReachWindup(side, seconds);

    public void BroadcastReachFire(int side, Vector3 aimPoint, float extendSeconds, float maxLength)
    {
        mutantAI.PresentReachFire(side, aimPoint, extendSeconds, maxLength);
        if (ServerSpawned)
            ReachFireRpc((byte)side, aimPoint, extendSeconds, maxLength);
    }

    [Rpc(SendTo.NotServer)]
    void ReachFireRpc(byte side, Vector3 aimPoint, float extendSeconds, float maxLength)
        => mutantAI.PresentReachFire(side, aimPoint, extendSeconds, maxLength);

    public void BroadcastReachReel(int side, float seconds)
    {
        mutantAI.PresentReachReel(side, seconds);
        if (ServerSpawned)
            ReachReelRpc((byte)side, seconds);
    }

    [Rpc(SendTo.NotServer)]
    void ReachReelRpc(byte side, float seconds) => mutantAI.PresentReachReel(side, seconds);

    public void BroadcastReachRetract(int side, float seconds)
    {
        mutantAI.PresentReachRetract(side, seconds);
        if (ServerSpawned)
            ReachRetractRpc((byte)side, seconds);
    }

    [Rpc(SendTo.NotServer)]
    void ReachRetractRpc(byte side, float seconds) => mutantAI.PresentReachRetract(side, seconds);

    public void BroadcastSweep(float windup, float spin, float degrees, float hold, float retract, float armLength, float baseYaw)
    {
        mutantAI.PresentSweep(windup, spin, degrees, hold, retract, armLength, baseYaw);
        if (ServerSpawned)
            SweepRpc(windup, spin, degrees, hold, retract, armLength, baseYaw);
    }

    [Rpc(SendTo.NotServer)]
    void SweepRpc(float windup, float spin, float degrees, float hold, float retract, float armLength, float baseYaw)
        => mutantAI.PresentSweep(windup, spin, degrees, hold, retract, armLength, baseYaw);

    public void BroadcastCancelLimbs()
    {
        mutantAI.PresentCancelLimbs();
        if (ServerSpawned)
            CancelLimbsRpc();
    }

    [Rpc(SendTo.NotServer)]
    void CancelLimbsRpc() => mutantAI.PresentCancelLimbs();

    public void BroadcastBlackout(Vector3 from, Vector3 to, float radius, float darkIn, float hold, float recover)
    {
        mutantAI.PresentBlackout(from, to, radius, darkIn, hold, recover);
        if (ServerSpawned)
            BlackoutRpc(from, to, radius, darkIn, hold, recover);
    }

    [Rpc(SendTo.NotServer)]
    void BlackoutRpc(Vector3 from, Vector3 to, float radius, float darkIn, float hold, float recover)
        => mutantAI.PresentBlackout(from, to, radius, darkIn, hold, recover);

    public void BroadcastWakeStutter(Vector3 center, float radius, float seconds)
    {
        mutantAI.PresentWakeStutter(center, radius, seconds);
        if (ServerSpawned)
            WakeStutterRpc(center, radius, seconds);
    }

    [Rpc(SendTo.NotServer)]
    void WakeStutterRpc(Vector3 center, float radius, float seconds) => mutantAI.PresentWakeStutter(center, radius, seconds);

    /// <summary>The shriek's shockwave presentation (shake + blow-outs); recorded so late joiners lose the same lights.</summary>
    public void BroadcastShriek(Vector3 center)
    {
        if (ServerSpawned)
        {
            _shrieked.Value = true;
            _shriekCenter.Value = center;
        }

        mutantAI.PresentShriek(center);
        if (ServerSpawned)
            ShriekRpc(center);
    }

    [Rpc(SendTo.NotServer)]
    void ShriekRpc(Vector3 center) => mutantAI.PresentShriek(center);

    public void BroadcastSfx(MutantAI.SfxKind kind, Vector3 position)
    {
        mutantAI.PresentSfx(kind, position);
        if (ServerSpawned)
            SfxRpc((byte)kind, position);
    }

    [Rpc(SendTo.NotServer)]
    void SfxRpc(byte kind, Vector3 position) => mutantAI.PresentSfx((MutantAI.SfxKind)kind, position);

    // ------------------------------------------------------------------
    // Server utilities
    // ------------------------------------------------------------------

    /// <summary>Server: shove a player through their OWNER, who alone may move their CharacterController.</summary>
    public void ServerRelayPush(NetworkObject playerNetworkObject, Vector3 horizontalVelocity, float upwardVelocity, float controlLockSeconds)
    {
        if (!ServerSpawned || playerNetworkObject == null)
            return;

        ApplyPushRpc(playerNetworkObject.NetworkObjectId, horizontalVelocity, upwardVelocity, controlLockSeconds,
            RpcTarget.Single(playerNetworkObject.OwnerClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void ApplyPushRpc(ulong playerNetworkObjectId, Vector3 horizontalVelocity, float upwardVelocity,
        float controlLockSeconds, RpcParams rpcParams = default)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return;
        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(playerNetworkObjectId, out NetworkObject no) || no == null)
            return;

        no.GetComponent<PlayerController>()?.ApplyExternalPush(horizontalVelocity, upwardVelocity, controlLockSeconds);
    }

    /// <summary>
    /// Server/offline: move him instantly. Through the NetworkTransform when spawned so observers snap rather than
    /// interpolating a 20 m slide across the blackout.
    /// </summary>
    public void ServerTeleport(Vector3 position, Quaternion rotation)
    {
        bool ccWasEnabled = characterController != null && characterController.enabled;
        if (ccWasEnabled)
            characterController.enabled = false; // a live CC would pull the transform back to its old capsule

        if (ServerSpawned && _networkTransform != null)
            _networkTransform.Teleport(position, rotation, transform.localScale);
        else
            transform.SetPositionAndRotation(position, rotation);

        if (ccWasEnabled)
            characterController.enabled = true;

        if (navMeshAgent != null && navMeshAgent.enabled)
            navMeshAgent.Warp(position);
    }

    void EnsureAnimationSync()
    {
        if (mutantAnimator == null)
            return;

        _serverNetworkAnimator = mutantAnimator.GetComponent<ServerNetworkAnimator>();
        if (_serverNetworkAnimator == null)
            _serverNetworkAnimator = mutantAnimator.gameObject.AddComponent<ServerNetworkAnimator>();
    }
}
