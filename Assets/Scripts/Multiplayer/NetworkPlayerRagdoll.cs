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
