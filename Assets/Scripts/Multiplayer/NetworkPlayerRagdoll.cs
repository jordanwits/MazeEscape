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

    void Awake()
    {
        if (ragdoll == null)
            ragdoll = GetComponent<PlayerRagdollController>();
        if (playerHealth == null)
            playerHealth = GetComponent<PlayerHealth>();
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

        byte mode = (byte)forceMode;
        RagdollOwnerClientRpc(worldForce, worldForcePosition, mode, new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { OwnerClientId }
            }
        });
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
    void RagdollOwnerClientRpc(Vector3 worldForce, Vector3 worldForcePosition, byte forceMode, ClientRpcParams clientRpcParams = default)
    {
        if (ragdoll == null || !IsOwner)
            return;

        ragdoll.ActivateRagdoll(worldForce, worldForcePosition, (ForceMode)forceMode);
    }
}
