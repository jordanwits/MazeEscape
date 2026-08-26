using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Replication shell for the carnival Bomber, mirroring <see cref="NetworkClownAvatar"/> but far smaller:
/// the Bomber has no health, no corpse and no one-shot attack trigger, so there is no death state to
/// reconcile and no late-join animation snapshot to rebuild — he is either standing, running, or gone.
///
/// All this does is (a) gate simulation to the server, (b) keep observer clients solid via
/// <see cref="EnemyClientCollisionProxy"/>, and (c) guarantee a <see cref="ServerNetworkAnimator"/> exists
/// so the server's <c>Chasing</c> bool reaches every peer. Movement rides the <see cref="NetworkTransform"/>.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(NetworkTransform))]
[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(BomberAI))]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(CharacterController))]
public class NetworkBomberAvatar : NetworkBehaviour
{
    [SerializeField] Animator bomberAnimator;
    [SerializeField] BomberAI bomberAI;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;

    ServerNetworkAnimator _serverNetworkAnimator;

    void Awake()
    {
        if (bomberAnimator == null) bomberAnimator = GetComponent<Animator>();
        if (bomberAI == null) bomberAI = GetComponent<BomberAI>();
        if (navMeshAgent == null) navMeshAgent = GetComponent<NavMeshAgent>();
        if (characterController == null) characterController = GetComponent<CharacterController>();

        EnsureAnimationSync();
    }

    public override void OnNetworkSpawn()
    {
        ApplyAuthorityState();
    }

    void ApplyAuthorityState()
    {
        bool shouldSimulate = NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening || IsServer;

        // BomberAI stays ENABLED on observers: its Update also drives the fuse-swell visual off the
        // replicated fuse flag, and every server-only branch inside is already gated on ShouldSimulate.
        if (navMeshAgent != null)
            navMeshAgent.enabled = shouldSimulate;

        if (characterController != null)
            characterController.enabled = shouldSimulate;

        // Observer clients disable the CC (the server drives movement) — keep him solid for remote players
        // and client-thrown props via a mirrored kinematic capsule.
        EnemyClientCollisionProxy.Apply(characterController, shouldSimulate);
    }

    void EnsureAnimationSync()
    {
        if (bomberAnimator == null)
            return;

        _serverNetworkAnimator = bomberAnimator.GetComponent<ServerNetworkAnimator>();
        if (_serverNetworkAnimator == null)
            _serverNetworkAnimator = bomberAnimator.gameObject.AddComponent<ServerNetworkAnimator>();
    }
}
