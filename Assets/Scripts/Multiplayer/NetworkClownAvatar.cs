using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.AI;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(NetworkTransform))]
[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(ClownAI))]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(CharacterController))]
public class NetworkClownAvatar : NetworkBehaviour
{
    static readonly List<ulong> s_FootstepObserverClientIds = new(16);

    [SerializeField] Animator clownAnimator;
    [SerializeField] ClownAI clownAI;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;
    [Header("Audio Networking")]
    [Tooltip("Only clients within this distance receive footstep RPCs. Set >= the footstep AudioSource max distance so everyone who can hear the 3D sound gets the same one-shot.")]
    [SerializeField] float maxFootstepObserverDistance = 26f;
    ServerNetworkAnimator _serverNetworkAnimator;

    void Awake()
    {
        if (clownAnimator == null)
            clownAnimator = GetComponent<Animator>();
        if (clownAI == null)
            clownAI = GetComponent<ClownAI>();
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

        if (clownAI != null)
            clownAI.enabled = shouldSimulate;

        if (navMeshAgent != null)
            navMeshAgent.enabled = shouldSimulate;

        if (characterController != null)
            characterController.enabled = shouldSimulate;
    }

    void EnsureAnimationSync()
    {
        if (clownAnimator == null)
            return;

        _serverNetworkAnimator = clownAnimator.GetComponent<ServerNetworkAnimator>();
        if (_serverNetworkAnimator == null)
            _serverNetworkAnimator = clownAnimator.gameObject.AddComponent<ServerNetworkAnimator>();
    }

    /// <summary>
    /// Server: fire an animator trigger that replicates to every client via <see cref="ServerNetworkAnimator"/>.
    /// Returns false if it could not route through the network animator (caller should fall back to a local
    /// <c>animator.SetTrigger</c>, e.g. offline). Triggers must go through the NetworkAnimator to replicate —
    /// setting them directly on the Animator does not.
    /// </summary>
    public bool TryServerSetAnimatorTrigger(string triggerName)
    {
        if (_serverNetworkAnimator == null || !IsSpawned || string.IsNullOrEmpty(triggerName))
            return false;

        _serverNetworkAnimator.SetTrigger(triggerName);
        return true;
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
        Vector3 clownPosition = transform.position;
        s_FootstepObserverClientIds.Clear();

        foreach (ulong clientId in nm.ConnectedClientsIds)
        {
            if (!nm.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client?.PlayerObject == null)
                continue;

            Vector3 listenerPosition = client.PlayerObject.transform.position;
            if ((listenerPosition - clownPosition).sqrMagnitude <= maxDistanceSqr)
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
        if (clownAI == null)
            return;

        clownAI.PlayFootstepSfxLocal();
    }
}
