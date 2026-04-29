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
    readonly NetworkVariable<bool> _isDead = new(false);

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
        _isDead.OnValueChanged += HandleDeadStateChanged;
        ApplyAuthorityState();
        ApplyDeadState(_isDead.Value);
    }

    public override void OnNetworkDespawn()
    {
        _isDead.OnValueChanged -= HandleDeadStateChanged;
    }

    void Update()
    {
        if (!IsServer || zombieHealth == null)
            return;

        if (_isDead.Value != zombieHealth.IsDead)
            _isDead.Value = zombieHealth.IsDead;
    }

    void ApplyAuthorityState()
    {
        bool shouldSimulate = !NetworkManager.Singleton || !NetworkManager.Singleton.IsListening || IsServer;

        // ZombieAI must stay enabled on clients so groans/footsteps can run from replicated motion/animator.
        // Movement and targeting remain server-only inside ZombieAI.Update.

        if (navMeshAgent != null)
            navMeshAgent.enabled = shouldSimulate;

        if (characterController != null)
            characterController.enabled = shouldSimulate;
    }

    void HandleDeadStateChanged(bool previousValue, bool currentValue)
    {
        ApplyDeadState(currentValue);
    }

    void ApplyDeadState(bool isDead)
    {
        if (!isDead || zombieAI == null)
            return;

        zombieAI.HandleDeath();
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
