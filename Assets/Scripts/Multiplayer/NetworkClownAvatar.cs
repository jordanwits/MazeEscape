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

    /// <summary>
    /// Late-join snapshot of the Grab and Slam animation. NGO's <see cref="NetworkAnimator"/> replicates
    /// parameter state but NOT one-shot triggers, so a client that joins mid-slam would see an idle Clown
    /// while the held player snapshot rebuilds the pinned pose against nothing. This struct lets observers
    /// jump the animator straight to the right state at the right normalized time on spawn.
    /// </summary>
    public struct SlamAnimationState : INetworkSerializeByMemcpy, System.IEquatable<SlamAnimationState>
    {
        public byte Active;                 // 0 = not slamming, 1 = slamming
        public int StateNameHash;           // animator state hash to Play() (matches the grab/slam state name)
        public float ServerTimeStarted;     // NetworkManager.ServerTime.TimeAsFloat at slam start
        public float ClipDurationSeconds;   // for normalizing elapsed → 0..1

        public bool Equals(SlamAnimationState o) =>
            Active == o.Active && StateNameHash == o.StateNameHash
            && ServerTimeStarted == o.ServerTimeStarted
            && ClipDurationSeconds == o.ClipDurationSeconds;
        public override bool Equals(object o) => o is SlamAnimationState s && Equals(s);
        public override int GetHashCode() =>
            Active ^ StateNameHash ^ ServerTimeStarted.GetHashCode() ^ ClipDurationSeconds.GetHashCode();
    }

    readonly NetworkVariable<SlamAnimationState> _slamAnimation = new NetworkVariable<SlamAnimationState>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

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

        // Spawn-time state rule: rebuild the slam animation from the replicated snapshot for clients that
        // joined mid-slam. The server is authoritative and the slam was started here, so it doesn't need
        // to reconstruct from itself.
        if (!IsServer && _slamAnimation.Value.Active != 0)
            ReconstructSlamAnimationFromSnapshot(_slamAnimation.Value);
    }

    void ReconstructSlamAnimationFromSnapshot(SlamAnimationState state)
    {
        if (clownAnimator == null || state.Active == 0 || state.StateNameHash == 0)
            return;

        // Compute how far through the clip we are based on server time. Play() jumps the animator directly
        // to the state regardless of trigger transitions, so this works even though the original Trigger
        // wasn't replayed for us.
        float clipDuration = Mathf.Max(0.05f, state.ClipDurationSeconds);
        float nowServer = NetworkManager.Singleton != null
            ? NetworkManager.Singleton.ServerTime.TimeAsFloat
            : 0f;
        float elapsed = Mathf.Max(0f, nowServer - state.ServerTimeStarted);
        float normalized = elapsed / clipDuration;

        // If we missed the whole clip, do nothing — the server has either already cleared the snapshot or
        // is about to; either way the recovery state owns visuals from here.
        if (normalized >= 1f)
            return;

        clownAnimator.Play(state.StateNameHash, 0, normalized);
        clownAnimator.Update(0f);
    }

    /// <summary>
    /// Server: record that the Grab and Slam animation is now playing so late joiners can reconstruct it.
    /// Call alongside the existing <see cref="TryServerSetAnimatorTrigger"/> path (the trigger drives clients
    /// already connected; this snapshot covers everyone who joins during the ~2.3s slam clip).
    /// </summary>
    public void ServerMarkSlamAnimationStarted(int stateNameHash, float clipDurationSeconds)
    {
        if (!IsServer || !IsSpawned)
            return;

        float nowServer = NetworkManager.Singleton != null
            ? NetworkManager.Singleton.ServerTime.TimeAsFloat
            : 0f;

        _slamAnimation.Value = new SlamAnimationState
        {
            Active = 1,
            StateNameHash = stateNameHash,
            ServerTimeStarted = nowServer,
            ClipDurationSeconds = Mathf.Max(0.05f, clipDurationSeconds),
        };
    }

    /// <summary>Server: the slam animation finished (recovery begins or the Clown is despawned mid-grab).</summary>
    public void ServerMarkSlamAnimationEnded()
    {
        if (!IsServer || !IsSpawned)
            return;
        if (_slamAnimation.Value.Active == 0)
            return;

        _slamAnimation.Value = default;
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
