using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Network glue for the Severance security guard. Mirrors <see cref="NetworkZombieAvatar"/>: the server
/// simulates AI/movement, clients keep <see cref="SecurityGuardAI"/> enabled purely for cosmetic audio, and a
/// <c>ServerNetworkAnimator</c> replicates the animator (locomotion params + attack/stagger cross-fades).
/// There is no death state — the guard is unkillable.
///
/// Also relays the MMA kick's non-ragdoll shove to the hit player's owner (their CharacterController is
/// owner-authoritative), since <see cref="SecurityGuardAI"/> is a plain MonoBehaviour and cannot send RPCs —
/// same pattern as <see cref="NetworkSkeletonAvatar"/>'s bash shove.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(SecurityGuardAI))]
public class NetworkSecurityGuardAvatar : NetworkBehaviour
{
    [SerializeField] Animator guardAnimator;
    [SerializeField] SecurityGuardAI guardAI;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;

    ServerNetworkAnimator _serverNetworkAnimator;

    void Awake()
    {
        if (guardAnimator == null)
            guardAnimator = GetComponent<Animator>();
        if (guardAI == null)
            guardAI = GetComponent<SecurityGuardAI>();
        if (navMeshAgent == null)
            navMeshAgent = GetComponent<NavMeshAgent>();
        if (characterController == null)
            characterController = GetComponent<CharacterController>();

        EnsureAnimationSync();
    }

    public override void OnNetworkSpawn()
    {
        ApplyAuthorityState();
    }

    void ApplyAuthorityState()
    {
        bool shouldSimulate = !NetworkManager.Singleton || !NetworkManager.Singleton.IsListening || IsServer;

        // SecurityGuardAI stays enabled on clients so footsteps/whooshes can run from replicated
        // motion/animator. Movement and targeting remain server-only inside SecurityGuardAI.Update.

        if (navMeshAgent != null)
            navMeshAgent.enabled = shouldSimulate;

        if (characterController != null)
            characterController.enabled = shouldSimulate;

        // Observer clients disable the CC (server drives movement) — keep the enemy solid for remote
        // players and client-thrown props via a mirrored kinematic capsule.
        EnemyClientCollisionProxy.Apply(characterController, shouldSimulate);
    }

    /// <summary>
    /// Server-only. Relay the MMA kick's non-ragdoll shove to the hit player's OWNER (their
    /// CharacterController is owner-authoritative, so only they can move themselves).
    /// </summary>
    public void ServerRelayPush(NetworkObject playerNetworkObject, Vector3 horizontalVelocity, float upwardVelocity, float controlLockSeconds)
    {
        if (!IsServer || playerNetworkObject == null)
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

    void EnsureAnimationSync()
    {
        if (guardAnimator == null)
            return;

        _serverNetworkAnimator = guardAnimator.GetComponent<ServerNetworkAnimator>();
        if (_serverNetworkAnimator == null)
            _serverNetworkAnimator = guardAnimator.gameObject.AddComponent<ServerNetworkAnimator>();
    }
}
