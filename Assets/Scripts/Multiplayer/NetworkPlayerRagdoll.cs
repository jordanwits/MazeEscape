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

    void Awake()
    {
        if (ragdoll == null)
            ragdoll = GetComponent<PlayerRagdollController>();
    }

    /// <summary>
    /// Call from server-only code (e.g. trap OnTriggerEnter when IsServer).
    /// </summary>
    public void RequestRagdollFromServer(Vector3 worldForce, Vector3 worldForcePosition, ForceMode forceMode = ForceMode.Impulse)
    {
        if (!IsServer || ragdoll == null)
            return;

        ulong id = OwnerClientId;
        float now = Time.time;
        if (s_TrapRagdollNextAllowedTime.TryGetValue(id, out float nextAllowed) && now < nextAllowed)
            return;

        s_TrapRagdollNextAllowedTime[id] = now + TrapRagdollServerCooldownSeconds;

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
    public void RequestRagdollFromTrapServerRpc(Vector3 worldForce, Vector3 worldForcePosition, byte forceMode)
    {
        RequestRagdollFromServer(worldForce, worldForcePosition, (ForceMode)forceMode);
    }

    [ClientRpc]
    void RagdollOwnerClientRpc(Vector3 worldForce, Vector3 worldForcePosition, byte forceMode, ClientRpcParams clientRpcParams = default)
    {
        if (ragdoll == null || !IsOwner)
            return;

        ragdoll.ActivateRagdoll(worldForce, worldForcePosition, (ForceMode)forceMode);
    }
}
