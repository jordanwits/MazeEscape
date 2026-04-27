using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(JailorAI))]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(CharacterController))]
public class NetworkJailorAvatar : NetworkBehaviour
{
    [SerializeField] Animator jailorAnimator;
    [SerializeField] JailorAI jailorAI;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;
    ServerNetworkAnimator _serverNetworkAnimator;

    void Awake()
    {
        if (jailorAnimator == null)
            jailorAnimator = GetComponent<Animator>();
        if (jailorAI == null)
            jailorAI = GetComponent<JailorAI>();
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

        if (jailorAI != null)
            jailorAI.enabled = shouldSimulate;

        if (navMeshAgent != null)
            navMeshAgent.enabled = shouldSimulate;

        if (characterController != null)
            characterController.enabled = shouldSimulate;
    }

    void EnsureAnimationSync()
    {
        if (jailorAnimator == null)
            return;

        _serverNetworkAnimator = jailorAnimator.GetComponent<ServerNetworkAnimator>();
        if (_serverNetworkAnimator == null)
            _serverNetworkAnimator = jailorAnimator.gameObject.AddComponent<ServerNetworkAnimator>();
    }
}
