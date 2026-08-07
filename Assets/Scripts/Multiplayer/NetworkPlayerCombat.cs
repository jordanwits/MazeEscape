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
    [SerializeField] PlayerHealth playerHealth;

    float _serverNextMeleeTime;

    void Awake()
    {
        if (playerController == null)
            playerController = GetComponent<PlayerController>();
        if (playerHealth == null)
            playerHealth = GetComponent<PlayerHealth>();
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

        // Server-side cooldown + alive gate. The client's TryMelee already gates these for fairness, but
        // those checks are bypassable; without enforcing them here, a client could spam melee or attack
        // while dead. Cooldown uses the same value the client uses so legitimate attacks always pass.
        if (playerHealth != null && playerHealth.IsDead)
            return;
        float now = Time.time;
        if (now < _serverNextMeleeTime)
            return;
        // Whichever weapon the attacker has selected — the sword's cooldown is longer than the punch's, and
        // gating a sword swing on the punch value would let it be spammed.
        float cooldown = playerController != null ? playerController.ActiveMeleeCooldownForServer : 0.8f;
        _serverNextMeleeTime = now + cooldown;

        ServerApplyMeleeWithObserverSwoosh();
    }

    void ServerApplyMeleeWithObserverSwoosh()
    {
        PlayMeleeSwooshForNonOwnerClients();
        playerController?.ApplyServerAuthoritativeMeleeDamage();
    }

    /// <summary>
    /// Owner already plays swoosh in <c>TryMelee</c>; other clients get it from the server. Not for the
    /// sword: this runs when the SERVER adjudicates, which is a hit-delay plus a round trip after the swing
    /// began, so the sound would arrive well after the observer's own blade had already come down. The sword's
    /// whoosh is an animation event in SwordSwing.anim, which every peer fires off its own clip in step with
    /// the animation it is actually showing.
    /// </summary>
    void PlayMeleeSwooshForNonOwnerClients()
    {
        if (!IsServer)
            return;

        if (playerController != null && playerController.IsSwordSelected)
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

    /// <summary>Server-only: play the Skeleton-specific impact sound on every client (replaces the punch sound).</summary>
    public void NotifyObserversSkeletonHit()
    {
        if (!IsServer)
            return;

        PlaySkeletonHitObserversClientRpc();
    }

    [ClientRpc]
    void PlaySkeletonHitObserversClientRpc(ClientRpcParams clientRpcParams = default)
    {
        playerController?.PlaySkeletonHitSfx();
    }

    /// <summary>Server-only: blade impact sound on every client, whatever the sword connected with.</summary>
    public void NotifyObserversSwordHit()
    {
        if (!IsServer)
            return;

        PlaySwordHitObserversClientRpc();
    }

    [ClientRpc]
    void PlaySwordHitObserversClientRpc(ClientRpcParams clientRpcParams = default)
    {
        playerController?.PlaySwordHitSfx();
    }
}
