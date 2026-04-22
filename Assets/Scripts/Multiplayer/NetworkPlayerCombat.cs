using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(PlayerController))]
public class NetworkPlayerCombat : NetworkBehaviour
{
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
            playerController?.ApplyServerAuthoritativeMeleeDamage();
            return;
        }

        RequestMeleeAttackServerRpc();
    }

    [ServerRpc]
    void RequestMeleeAttackServerRpc(ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        playerController?.ApplyServerAuthoritativeMeleeDamage();
    }

    /// <summary>Server-only: tells the owning client to play melee hit feedback after a confirmed zombie hit.</summary>
    public void NotifyOwnerMeleeHit()
    {
        if (!IsServer)
            return;

        PlayMeleeHitFeedbackClientRpc(new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        });
    }

    [ClientRpc]
    void PlayMeleeHitFeedbackClientRpc(ClientRpcParams clientRpcParams = default)
    {
        playerController?.PlayMeleeHitSfx();
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
