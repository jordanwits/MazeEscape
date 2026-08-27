using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Network glue for the dungeon Skeleton enemy. Mirrors <see cref="NetworkZombieAvatar"/>: the server simulates
/// AI/movement, clients keep the AI component enabled only for cosmetic audio, and a <c>ServerNetworkAnimator</c>
/// replicates the animator.
///
/// On death it fires a reliable client RPC so every peer swaps the animated skinned skeleton for a locally-spawned
/// <see cref="SkeletonCrumble"/> bone pile (cosmetic, client-side physics), then the server despawns the network
/// object shortly after. Also relays the close-range bash's non-ragdoll shove to the hit player's owner, since
/// <see cref="SkeletonAI"/> is a plain MonoBehaviour and cannot send RPCs.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(SkeletonAI))]
[RequireComponent(typeof(SkeletonHealth))]
public class NetworkSkeletonAvatar : NetworkBehaviour
{
    [SerializeField] Animator skeletonAnimator;
    [SerializeField] SkeletonAI skeletonAI;
    [SerializeField] SkeletonHealth skeletonHealth;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;

    [Header("Held skull")]
    [Tooltip("The flaming skull held in the right hand (bash weapon / thrown ammo). Hidden while a thrown skull " +
             "is in flight, hidden on death. Synced so all clients agree on when it's in-hand vs thrown.")]
    [SerializeField] Transform heldSkull;

    [Header("Death crumble")]
    [Tooltip("Renderers (the animated skinned skeleton) hidden the instant it dies, replaced by the bone pile.")]
    [SerializeField] Renderer[] visualRenderers;
    [Tooltip("Local, non-networked bone-pile prefab spawned on every client when the skeleton dies.")]
    [SerializeField] GameObject crumblePrefab;
    [Tooltip("Seconds the server keeps the (now invisible) network object alive after death so the crumble RPC " +
             "is delivered before despawn.")]
    [SerializeField] float despawnDelayAfterDeath = 1.5f;

    ServerNetworkAnimator _serverNetworkAnimator;
    readonly NetworkVariable<bool> _isDead = new(false);
    readonly NetworkVariable<bool> _heldSkullHidden = new(false);
    bool _deathVisualsDone;

    void Awake()
    {
        if (skeletonAnimator == null)
            skeletonAnimator = GetComponentInChildren<Animator>();
        if (skeletonAI == null)
            skeletonAI = GetComponent<SkeletonAI>();
        if (skeletonHealth == null)
            skeletonHealth = GetComponent<SkeletonHealth>();
        if (navMeshAgent == null)
            navMeshAgent = GetComponent<NavMeshAgent>();
        if (characterController == null)
            characterController = GetComponent<CharacterController>();

        EnsureAnimationSync();
    }

    public override void OnNetworkSpawn()
    {
        _isDead.OnValueChanged += HandleDeadStateChanged;
        _heldSkullHidden.OnValueChanged += HandleHeldSkullChanged;
        ApplyAuthorityState();
        ApplyHeldSkull(_heldSkullHidden.Value);

        // Late join: if it's already dead, show the crumble immediately.
        if (_isDead.Value)
            HandleDeathVisuals();
    }

    public override void OnNetworkDespawn()
    {
        _isDead.OnValueChanged -= HandleDeadStateChanged;
        _heldSkullHidden.OnValueChanged -= HandleHeldSkullChanged;
    }

    /// <summary>
    /// Throw / bash cues. <see cref="SkeletonAI"/> runs both routines server-only, so playing them there left
    /// clients hearing a silent wind-up. Mirrors <see cref="NetworkJailorAvatar.PlayPickupLaughSfxForObservers"/>:
    /// the RPC goes to EVERYONE and the server does not also play its own local copy, which is what keeps the
    /// listen-server host from hearing it twice.
    /// </summary>
    public void PlayThrowSfxForObservers()
    {
        if (!IsServer)
            return;

        PlayThrowSfxClientRpc();
    }

    [ClientRpc]
    void PlayThrowSfxClientRpc()
    {
        if (skeletonAI != null)
            skeletonAI.PlayThrowSfxLocal();
    }

    /// <seealso cref="PlayThrowSfxForObservers"/>
    public void PlayBashSfxForObservers()
    {
        if (!IsServer)
            return;

        PlayBashSfxClientRpc();
    }

    [ClientRpc]
    void PlayBashSfxClientRpc()
    {
        if (skeletonAI != null)
            skeletonAI.PlayBashSfxLocal();
    }

    /// <summary>Server-authoritative show/hide of the in-hand skull. <see cref="SkeletonAI"/> calls this during throws.</summary>
    public void SetHeldSkullHidden(bool hidden)
    {
        if (IsSpawned && IsServer)
            _heldSkullHidden.Value = hidden;
        else if (!IsSpawned)
            ApplyHeldSkull(hidden); // offline / single-player fallback
    }

    void HandleHeldSkullChanged(bool previousValue, bool currentValue) => ApplyHeldSkull(currentValue);

    void ApplyHeldSkull(bool hidden)
    {
        if (heldSkull != null)
            heldSkull.gameObject.SetActive(!hidden);
    }

    void Update()
    {
        if (!IsServer || skeletonHealth == null)
            return;

        if (!_isDead.Value && skeletonHealth.IsDead)
        {
            _isDead.Value = true;
            PlayCrumbleClientRpc();
            StartCoroutine(ServerDespawnAfterDelay());
        }
    }

    void ApplyAuthorityState()
    {
        bool shouldSimulate = !NetworkManager.Singleton || !NetworkManager.Singleton.IsListening || IsServer;

        // SkeletonAI stays enabled on clients for replicated-motion audio; movement/targeting are server-only inside it.
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
        if (currentValue)
            HandleDeathVisuals();
    }

    [Rpc(SendTo.Everyone)]
    void PlayCrumbleClientRpc()
    {
        HandleDeathVisuals();
    }

    void HandleDeathVisuals()
    {
        if (_deathVisualsDone)
            return;
        _deathVisualsDone = true;

        if (skeletonAI != null)
            skeletonAI.HandleDeath();

        if (visualRenderers != null)
        {
            for (int i = 0; i < visualRenderers.Length; i++)
            {
                if (visualRenderers[i] != null)
                    visualRenderers[i].enabled = false;
            }
        }

        // Drop the flaming skull out of his hand so it falls and rests on the ground (flame still burning).
        DropHeldSkull();

        SpawnCrumble();
    }

    void SpawnCrumble()
    {
        if (crumblePrefab == null)
            return;

        // Spawn standing at the (replicated) skeleton root pose — feet at the root origin — unparented so the
        // server despawn doesn't destroy it. Yaw only; the skeleton stays upright in life.
        Vector3 euler = transform.rotation.eulerAngles;
        Quaternion uprightYaw = Quaternion.Euler(0f, euler.y, 0f);
        GameObject pile = Instantiate(crumblePrefab, transform.position, uprightYaw);
        SkeletonCrumble crumble = pile.GetComponent<SkeletonCrumble>();
        if (crumble == null)
            crumble = pile.AddComponent<SkeletonCrumble>();

        // Pure gravity collapse from rest — no propelling force.
        crumble.Initialize();
    }

    void DropHeldSkull()
    {
        if (heldSkull == null)
            return;

        heldSkull.gameObject.SetActive(true);                    // ensure visible if a throw had it hidden
        heldSkull.SetParent(null, worldPositionStays: true);     // detach so it survives the skeleton's despawn
        heldSkull.gameObject.layer = 0;                          // Default — collides with the floor

        if (heldSkull.GetComponent<Collider>() == null)
        {
            SphereCollider sphere = heldSkull.gameObject.AddComponent<SphereCollider>();
            sphere.radius = 0.15f;
        }
        if (heldSkull.GetComponent<Rigidbody>() == null)
        {
            Rigidbody rb = heldSkull.gameObject.AddComponent<Rigidbody>();
            rb.mass = 1f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

        // Reuse the bone-pile's gravity-only fall + freeze/despawn behaviour for this single piece.
        SkeletonCrumble drop = heldSkull.GetComponent<SkeletonCrumble>();
        if (drop == null)
            drop = heldSkull.gameObject.AddComponent<SkeletonCrumble>();
        drop.Initialize();

        heldSkull = null; // it's dropped; no longer the in-hand skull
    }

    IEnumerator ServerDespawnAfterDelay()
    {
        if (despawnDelayAfterDeath > 0f)
            yield return new WaitForSeconds(despawnDelayAfterDeath);

        if (NetworkObject != null && NetworkObject.IsSpawned && IsServer)
            NetworkObject.Despawn(true);
    }

    /// <summary>
    /// Server-only. Relay the bash's non-ragdoll shove to the hit player's OWNER (their CharacterController is
    /// owner-authoritative, so only they can move themselves). <see cref="SkeletonAI"/> calls this for the bash hit.
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
        if (skeletonAnimator == null)
            return;

        _serverNetworkAnimator = skeletonAnimator.GetComponent<ServerNetworkAnimator>();
        if (_serverNetworkAnimator == null)
            _serverNetworkAnimator = skeletonAnimator.gameObject.AddComponent<ServerNetworkAnimator>();
    }
}
