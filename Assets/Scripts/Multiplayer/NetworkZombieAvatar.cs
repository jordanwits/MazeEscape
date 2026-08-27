using System.Collections;
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

        // Observer clients disable the CC (server drives movement) — keep the enemy solid for remote
        // players and client-thrown props via a mirrored kinematic capsule.
        EnemyClientCollisionProxy.Apply(characterController, shouldSimulate);
    }

    void HandleDeadStateChanged(bool previousValue, bool currentValue)
    {
        ApplyDeadState(currentValue);
    }

    void ApplyDeadState(bool isDead)
    {
        if (!isDead)
            return;

        if (zombieAI != null)
            zombieAI.HandleDeath();

        // ZombieHealth disables the corpse's colliders after its own delay, but that routine is server-only
        // (Die() early-returns off-server), so observers keep the mirrored kinematic capsule standing where the
        // zombie fell — an invisible body-block in the corridor on clients only, while the host walks straight
        // through it, until the object despawns. Drop the stand-in on the SAME beat the server drops the real
        // collider. Matches NetworkClownAvatar / NetworkSecurityGuardAvatar.
        if (characterController != null && !IsServer)
            StartCoroutine(DropCollisionProxyRoutine());
    }

    IEnumerator DropCollisionProxyRoutine()
    {
        float delay = zombieHealth != null ? zombieHealth.DisableColliderDelay : 0f;
        if (delay > 0f)
            yield return new WaitForSeconds(delay);

        EnemyClientCollisionProxy.Deactivate(characterController);
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
