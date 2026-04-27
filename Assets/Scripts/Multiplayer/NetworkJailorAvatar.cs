using System.Collections.Generic;
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
    static readonly List<ulong> s_FootstepObserverClientIds = new(16);

    [SerializeField] Animator jailorAnimator;
    [SerializeField] JailorAI jailorAI;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;
    [Header("Audio Networking")]
    [Tooltip("Only clients within this distance receive footstep RPCs. Set >= the footstep AudioSource max distance so everyone who can hear the 3D sound gets the same one-shot.")]
    [SerializeField] float maxFootstepObserverDistance = 26f;
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

    public void PlayPickupLaughSfxForObservers()
    {
        if (!IsServer)
            return;

        PlayPickupLaughSfxClientRpc();
    }

    [ClientRpc]
    void PlayPickupLaughSfxClientRpc()
    {
        if (jailorAI == null)
            return;

        jailorAI.PlayPickupLaughSfxLocal();
    }

    public void PlayFootstepSfxForObservers()
    {
        if (!IsServer)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
        {
            PlayFootstepSfxClientRpc();
            return;
        }

        float maxDistanceSqr = Mathf.Max(0.01f, maxFootstepObserverDistance) * Mathf.Max(0.01f, maxFootstepObserverDistance);
        Vector3 jailorPosition = transform.position;
        s_FootstepObserverClientIds.Clear();

        foreach (ulong clientId in nm.ConnectedClientsIds)
        {
            if (!nm.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client?.PlayerObject == null)
                continue;

            Vector3 listenerPosition = client.PlayerObject.transform.position;
            if ((listenerPosition - jailorPosition).sqrMagnitude <= maxDistanceSqr)
                s_FootstepObserverClientIds.Add(clientId);
        }

        if (s_FootstepObserverClientIds.Count == 0)
            return;

        PlayFootstepSfxClientRpc(new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = s_FootstepObserverClientIds.ToArray() }
        });
    }

    [ClientRpc]
    void PlayFootstepSfxClientRpc(ClientRpcParams clientRpcParams = default)
    {
        if (jailorAI == null)
            return;

        jailorAI.PlayFootstepSfxLocal();
    }
}
