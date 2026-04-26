using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(PlayerController))]
public class NetworkPlayerCombat : NetworkBehaviour
{
    static readonly List<ulong> s_MeleeSwooshObserverClientIds = new List<ulong>(16);

    [SerializeField] PlayerController playerController;

    void Awake()
    {
        if (playerController == null)
            playerController = GetComponent<PlayerController>();
    }

    public void RequestMeleeAttack()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
        {
            playerController?.ApplyServerAuthoritativeMeleeDamage();
            return;
        }

        if (IsServer)
        {
            ServerApplyMeleeWithObserverSwoosh();
            return;
        }

        RequestMeleeAttackServerRpc();
    }

    [ServerRpc]
    void RequestMeleeAttackServerRpc(ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerApplyMeleeWithObserverSwoosh();
    }

    void ServerApplyMeleeWithObserverSwoosh()
    {
        PlayMeleeSwooshForNonOwnerClients();
        playerController?.ApplyServerAuthoritativeMeleeDamage();
    }

    /// <summary>Owner already plays swoosh in <c>TryMelee</c>; other clients get it from the server.</summary>
    void PlayMeleeSwooshForNonOwnerClients()
    {
        if (!IsServer)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
            return;

        s_MeleeSwooshObserverClientIds.Clear();
        foreach (ulong id in nm.ConnectedClientsIds)
        {
            if (id != OwnerClientId)
                s_MeleeSwooshObserverClientIds.Add(id);
        }

        if (s_MeleeSwooshObserverClientIds.Count == 0)
            return;

        PlayMeleeSwooshObserversClientRpc(new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = s_MeleeSwooshObserverClientIds.ToArray() }
        });
    }

    [ClientRpc]
    void PlayMeleeSwooshObserversClientRpc(ClientRpcParams clientRpcParams = default)
    {
        playerController?.PlayMeleeSwooshSfx();
    }

    /// <summary>Server-only: same punch impact sound on every client, with a single chosen clip for everyone.</summary>
    public void NotifyObserversMeleeHit(byte punchClipSlot0To2)
    {
        if (!IsServer)
            return;

        PlayMeleeHitObserversClientRpc(punchClipSlot0To2);
    }

    [ClientRpc]
    void PlayMeleeHitObserversClientRpc(byte punchClipSlot0To2, ClientRpcParams clientRpcParams = default)
    {
        playerController?.PlayMeleeHitSfxWithIndex(punchClipSlot0To2);
    }

    /// <summary>Server-only: tells the owning client to play feedback when a zombie hit lands.</summary>
    public void NotifyOwnerZombieHitSfx()
    {
        if (!IsServer)
            return;

        PlayZombieHitSfxClientRpc(new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        });
    }

    [ClientRpc]
    void PlayZombieHitSfxClientRpc(ClientRpcParams clientRpcParams = default)
    {
        playerController?.PlayZombieHitSfx();
    }
}
