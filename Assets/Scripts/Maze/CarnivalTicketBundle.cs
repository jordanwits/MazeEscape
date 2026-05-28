using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Physical ticket roll spawned by a carnival minigame on round end. Carries a ticket value;
/// pressing E on it credits the full <see cref="Value"/> to the picker's
/// <see cref="NetworkPlayerCarnivalTickets"/> and despawns. Whoever grabs it first gets the entire payout
/// — same as a real arcade ticket dispenser. Not an inventory item, not stackable, not droppable.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class CarnivalTicketBundle : NetworkBehaviour
{
    readonly NetworkVariable<int> _value = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public int Value => _value.Value;

    /// <summary>Server-only. Call after <see cref="NetworkObject.Spawn"/> so the change replicates to all connected clients.</summary>
    public void ServerSetValue(int v)
    {
        if (!IsServer || !IsSpawned)
            return;
        _value.Value = Mathf.Max(0, v);
    }

    /// <summary>Called from <see cref="PlayerController"/> when the player presses E while aiming at this bundle.</summary>
    public void RequestPickup(PlayerController interactor)
    {
        if (interactor == null)
            return;
        NetworkObject playerNet = interactor.GetComponent<NetworkObject>();
        if (playerNet == null)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !IsSpawned)
            return;

        if (nm.IsServer)
            ServerApplyPickup(playerNet.NetworkObjectId, playerNet.OwnerClientId);
        else
            RequestPickupServerRpc(playerNet.NetworkObjectId);
    }

    [ServerRpc(RequireOwnership = false)]
    void RequestPickupServerRpc(ulong playerNetworkObjectId, ServerRpcParams rpcParams = default)
    {
        ServerApplyPickup(playerNetworkObjectId, rpcParams.Receive.SenderClientId);
    }

    void ServerApplyPickup(ulong playerNetworkObjectId, ulong expectedOwnerClientId)
    {
        if (!IsServer || !IsSpawned)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null
            || !nm.SpawnManager.SpawnedObjects.TryGetValue(playerNetworkObjectId, out NetworkObject playerObj)
            || playerObj == null)
            return;

        if (playerObj.OwnerClientId != expectedOwnerClientId)
            return;

        // Server-side range gate: without this, any client could claim any ticket bundle on the map from
        // anywhere ("whoever grabs it first gets the entire payout"). Validate against the server's known
        // player position — never a client-supplied hint.
        const float ServerMaxPickupHorizontal = 4f;
        const float ServerMaxPickupVertical = 3f;
        Vector3 bundlePos = transform.position;
        Vector3 playerPos = playerObj.transform.position;
        Vector3 flatDelta = new Vector3(bundlePos.x - playerPos.x, 0f, bundlePos.z - playerPos.z);
        if (flatDelta.sqrMagnitude > ServerMaxPickupHorizontal * ServerMaxPickupHorizontal)
            return;
        if (Mathf.Abs(bundlePos.y - playerPos.y) > ServerMaxPickupVertical)
            return;

        NetworkPlayerCarnivalTickets wallet = playerObj.GetComponent<NetworkPlayerCarnivalTickets>();
        if (wallet == null)
            return;

        wallet.ServerAdd(_value.Value);

        NetworkObject self = GetComponent<NetworkObject>();
        if (self != null && self.IsSpawned)
            self.Despawn(true);
    }
}
