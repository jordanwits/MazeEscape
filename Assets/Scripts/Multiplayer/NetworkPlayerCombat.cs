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
}
