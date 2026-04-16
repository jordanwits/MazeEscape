using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(ZombieAI))]
[RequireComponent(typeof(ZombieHealth))]
public class NetworkZombieAvatar : NetworkBehaviour
{
    [SerializeField] Animator zombieAnimator;
    [SerializeField] ZombieAI zombieAI;
    [SerializeField] ZombieHealth zombieHealth;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;
    ServerNetworkAnimator _serverNetworkAnimator;

    void Awake()
    {
        if (zombieAnimator == null)
            zombieAnimator = GetComponent<Animator>();
        if (zombieAI == null)
            zombieAI = GetComponent<ZombieAI>();
        if (zombieHealth == null)
            zombieHealth = GetComponent<ZombieHealth>();
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

        if (zombieAI != null)
            zombieAI.enabled = shouldSimulate;

        if (navMeshAgent != null)
            navMeshAgent.enabled = shouldSimulate;

        if (characterController != null)
            characterController.enabled = shouldSimulate;
    }

    void EnsureAnimationSync()
    {
        if (zombieAnimator == null)
            return;

        _serverNetworkAnimator = zombieAnimator.GetComponent<ServerNetworkAnimator>();
        if (_serverNetworkAnimator == null)
            _serverNetworkAnimator = zombieAnimator.gameObject.AddComponent<ServerNetworkAnimator>();
    }
}
