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
    bool _lastOwnerWasRagdolled;

    void Awake()
    {
        if (ragdoll == null)
            ragdoll = GetComponent<PlayerRagdollController>();
        if (playerHealth == null)
            playerHealth = GetComponent<PlayerHealth>();
    }

    public override void OnNetworkSpawn()
    {
        _lastOwnerWasRagdolled = ragdoll != null && ragdoll.IsRagdolled;
    }

    void Update()
    {
        if (!IsSpawned || !IsOwner || ragdoll == null)
            return;

        bool isRagdolledNow = ragdoll.IsRagdolled;
        if (_lastOwnerWasRagdolled && !isRagdolledNow)
            NotifyRecoveryStartedServerRpc();

        _lastOwnerWasRagdolled = isRagdolledNow;
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
        StartRagdollClientRpc(worldForce, worldForcePosition, (byte)forceMode, allowAutoRecovery);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    void NotifyRecoveryStartedServerRpc()
    {
        if (!_serverRagdollActive)
            return;

        _serverRagdollActive = false;
        StopRagdollClientRpc(playRecoveryAnimation: true);
    }

    public void ForceExitRagdollFromServer()
    {
        if (!IsServer || !_serverRagdollActive)
            return;

        _serverRagdollActive = false;
        StopRagdollClientRpc(playRecoveryAnimation: false);
    }

    [ClientRpc]
    void StopRagdollClientRpc(bool playRecoveryAnimation)
    {
        if (ragdoll == null)
            return;

        if (playRecoveryAnimation)
            ragdoll.DeactivateRagdoll();
        else
            ragdoll.ForceExitRagdollWithoutGroundSnap();
    }
}
