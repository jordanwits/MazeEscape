using System.Collections;
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
[RequireComponent(typeof(ClownHealth))]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(CharacterController))]
public class NetworkClownAvatar : NetworkBehaviour
{
    static readonly List<ulong> s_FootstepObserverClientIds = new(16);
    static readonly List<ulong> s_VoiceObserverClientIds = new(16);

    [SerializeField] Animator clownAnimator;
    [SerializeField] ClownAI clownAI;
    [SerializeField] ClownHealth clownHealth;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;
    [Header("Audio Networking")]
    [Tooltip("Only clients within this distance receive footstep RPCs. Set >= the footstep AudioSource max distance so everyone who can hear the 3D sound gets the same one-shot.")]
    [SerializeField] float maxFootstepObserverDistance = 26f;
    [Tooltip("Only clients within this distance receive Clown voice (laugh) RPCs. Set >= the voice AudioSource max distance.")]
    [SerializeField] float maxVoiceObserverDistance = 32f;
    ServerNetworkAnimator _serverNetworkAnimator;

    /// <summary>
    /// Late-join snapshot of the Hammer Swing attack animation. NGO's <see cref="NetworkAnimator"/> replicates
    /// parameter state but NOT one-shot triggers, so a client that joins mid-swing would see an idle Clown.
    /// This struct lets observers jump the animator straight to the right state at the right normalized time
    /// on spawn.
    /// </summary>
    public struct AttackAnimationState : INetworkSerializeByMemcpy, System.IEquatable<AttackAnimationState>
    {
        public byte Active;                 // 0 = not swinging, 1 = swinging
        public int StateNameHash;           // animator state hash to Play() (matches the swing state name)
        public float ServerTimeStarted;     // NetworkManager.ServerTime.TimeAsFloat at swing start
        public float ClipDurationSeconds;   // for normalizing elapsed → 0..1

        public bool Equals(AttackAnimationState o) =>
            Active == o.Active && StateNameHash == o.StateNameHash
            && ServerTimeStarted == o.ServerTimeStarted
            && ClipDurationSeconds == o.ClipDurationSeconds;
        public override bool Equals(object o) => o is AttackAnimationState s && Equals(s);
        public override int GetHashCode() =>
            Active ^ StateNameHash ^ ServerTimeStarted.GetHashCode() ^ ClipDurationSeconds.GetHashCode();
    }

    readonly NetworkVariable<AttackAnimationState> _attackAnimation = new NetworkVariable<AttackAnimationState>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<bool> _isDead = new(false);

    void Awake()
    {
        if (clownAnimator == null)
            clownAnimator = GetComponent<Animator>();
        if (clownAI == null)
            clownAI = GetComponent<ClownAI>();
        if (clownHealth == null)
            clownHealth = GetComponent<ClownHealth>();
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
        ApplyDeadState(_isDead.Value); // late joiners inherit an already-dead body

        // Spawn-time state rule: rebuild the swing animation from the replicated snapshot for clients that
        // joined mid-swing. The server is authoritative and the swing was started here, so it doesn't need
        // to reconstruct from itself.
        if (!IsServer && _attackAnimation.Value.Active != 0)
            ReconstructAttackAnimationFromSnapshot(_attackAnimation.Value);
    }

    public override void OnNetworkDespawn()
    {
        _isDead.OnValueChanged -= HandleDeadStateChanged;
    }

    void Update()
    {
        if (!IsServer || clownHealth == null)
            return;

        if (_isDead.Value != clownHealth.IsDead)
            _isDead.Value = clownHealth.IsDead;
    }

    void HandleDeadStateChanged(bool previousValue, bool currentValue)
    {
        ApplyDeadState(currentValue);
    }

    void ApplyDeadState(bool isDead)
    {
        if (!isDead)
            return;

        // ClownAI is DISABLED on observers (see ApplyAuthorityState), so this direct call is the only thing
        // that runs the client-side cleanup — dropping the hammer-carry layer and silencing the laugh.
        if (clownAI != null)
            clownAI.HandleDeath();

        // The server disables the corpse's CharacterController once the fall has finished; observers mirror
        // that on the SAME delay by dropping the kinematic stand-in, or the body would keep blocking a
        // corridor on clients only, while the host walked straight through it.
        if (characterController != null && !IsServer)
            StartCoroutine(DropCollisionProxyRoutine());
    }

    IEnumerator DropCollisionProxyRoutine()
    {
        float delay = clownHealth != null ? clownHealth.DisableColliderDelay : 0f;
        if (delay > 0f)
            yield return new WaitForSeconds(delay);

        EnemyClientCollisionProxy.Deactivate(characterController);
    }

    void ReconstructAttackAnimationFromSnapshot(AttackAnimationState state)
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

        // The swing exists on the base layer AND (same state name) on the "Hammer Carry" upper-body
        // layer; jump every layer that has it so a late joiner's arms aren't desynced from the body.
        for (int layer = 0; layer < clownAnimator.layerCount; layer++)
        {
            if (clownAnimator.HasState(layer, state.StateNameHash))
                clownAnimator.Play(state.StateNameHash, layer, normalized);
        }
        clownAnimator.Update(0f);
    }

    /// <summary>
    /// Server: record that the Hammer Swing animation is now playing so late joiners can reconstruct it.
    /// Call alongside the existing <see cref="TryServerSetAnimatorTrigger"/> path (the trigger drives clients
    /// already connected; this snapshot covers everyone who joins during the swing clip).
    /// </summary>
    public void ServerMarkAttackAnimationStarted(int stateNameHash, float clipDurationSeconds)
    {
        if (!IsServer || !IsSpawned)
            return;

        float nowServer = NetworkManager.Singleton != null
            ? NetworkManager.Singleton.ServerTime.TimeAsFloat
            : 0f;

        _attackAnimation.Value = new AttackAnimationState
        {
            Active = 1,
            StateNameHash = stateNameHash,
            ServerTimeStarted = nowServer,
            ClipDurationSeconds = Mathf.Max(0.05f, clipDurationSeconds),
        };
    }

    /// <summary>Server: the swing animation finished (recovery begins or the Clown is despawned mid-swing).</summary>
    public void ServerMarkAttackAnimationEnded()
    {
        if (!IsServer || !IsSpawned)
            return;
        if (_attackAnimation.Value.Active == 0)
            return;

        _attackAnimation.Value = default;
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

        // Observer clients disable the CC (server drives movement) — keep the enemy solid for remote
        // players and client-thrown props via a mirrored kinematic capsule.
        EnemyClientCollisionProxy.Apply(characterController, shouldSimulate);
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

    /// <summary>
    /// Server: replicate a Clown voice line (laugh) to every nearby client so observers hear the
    /// same 3D one-shot the host's <see cref="ClownAI"/> decided to play. Mirrors the footstep RPC path.
    /// </summary>
    public void PlayVoiceSfxForObservers(byte clipId)
    {
        if (!IsServer)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
        {
            PlayVoiceSfxClientRpc(clipId);
            return;
        }

        float maxDistanceSqr = Mathf.Max(0.01f, maxVoiceObserverDistance) * Mathf.Max(0.01f, maxVoiceObserverDistance);
        Vector3 clownPosition = transform.position;
        s_VoiceObserverClientIds.Clear();

        foreach (ulong clientId in nm.ConnectedClientsIds)
        {
            if (!nm.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client?.PlayerObject == null)
                continue;

            Vector3 listenerPosition = client.PlayerObject.transform.position;
            if ((listenerPosition - clownPosition).sqrMagnitude <= maxDistanceSqr)
                s_VoiceObserverClientIds.Add(clientId);
        }

        if (s_VoiceObserverClientIds.Count == 0)
            return;

        PlayVoiceSfxClientRpc(clipId, new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = s_VoiceObserverClientIds.ToArray() }
        });
    }

    [ClientRpc]
    void PlayVoiceSfxClientRpc(byte clipId, ClientRpcParams clientRpcParams = default)
    {
        if (clownAI == null)
            return;

        clownAI.PlayVoiceSfxLocal(clipId);
    }
}
