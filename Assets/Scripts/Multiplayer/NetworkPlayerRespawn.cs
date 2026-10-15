using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative life state for one player, plus the placement moves that must go through the owner
/// (level-start spawn, teleport orb).
///
/// Death is permanent for the rest of the section: the body stays down where it fell and the owner drops into
/// <see cref="PlayerSpectatorCamera"/>. Nothing in this section brings them back — every section switch
/// replaces the avatar with a fresh one (see <see cref="LevelCarryOverStore"/>), and that new avatar is what
/// returns a dead player to the run. When the last living player dies, <see cref="TeamWipeDirector"/> ends it.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(PlayerHealth))]
public class NetworkPlayerRespawn : NetworkBehaviour
{
    [SerializeField] PlayerHealth playerHealth;
    [SerializeField] CharacterController characterController;
    [SerializeField] NetworkPlayerInventory networkPlayerInventory;
    [SerializeField] PlayerRagdollController ragdollController;
    [SerializeField, Min(0f)] float respawnPitKillGraceSeconds = 1.5f;
    NetworkPlayerAvatar _networkPlayerAvatar;

    readonly NetworkVariable<bool> _isDead = new(false);
    // The real current health is set by the server in OnNetworkSpawn from PlayerHealth.CurrentHealth,
    // and late joiners receive that value in the spawn snapshot. Initializing to a hardcoded "100" used
    // to silently lie about MaxHealth if PlayerHealth.maxHealth was tuned to anything else; use 0 so the
    // value can only ever come from the authoritative server write.
    readonly NetworkVariable<float> _currentHealth = new(0f);

    OwnerNetworkTransform _ownerNetworkTransform;
    float _ignorePitKillsUntil;

    void Awake()
    {
        if (playerHealth == null)
            playerHealth = GetComponent<PlayerHealth>();
        if (characterController == null)
            characterController = GetComponent<CharacterController>();
        if (networkPlayerInventory == null)
            networkPlayerInventory = GetComponent<NetworkPlayerInventory>();
        if (_networkPlayerAvatar == null)
            _networkPlayerAvatar = GetComponent<NetworkPlayerAvatar>();
        if (ragdollController == null)
            ragdollController = GetComponent<PlayerRagdollController>();
        if (_ownerNetworkTransform == null)
            _ownerNetworkTransform = GetComponent<OwnerNetworkTransform>();

        // No-session play mode (a level scene entered straight from the editor) never spawns this, so the
        // networked death path below never runs there. That lone player's death is still a team wipe.
        if (playerHealth != null)
            playerHealth.Died += HandlePlayerDiedWithoutSession;
    }

    public override void OnDestroy()
    {
        if (playerHealth != null)
            playerHealth.Died -= HandlePlayerDiedWithoutSession;

        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        _isDead.OnValueChanged += HandleDeadStateChanged;
        _currentHealth.OnValueChanged += HandleCurrentHealthChanged;
        if (playerHealth != null)
        {
            playerHealth.Damaged += HandlePlayerHealthChanged;
            playerHealth.Died += HandlePlayerDied;
            playerHealth.Restored += HandlePlayerHealthChanged;
            playerHealth.Healed += HandlePlayerHealthChanged;
        }

        if (IsServer && playerHealth != null)
        {
            _currentHealth.Value = playerHealth.CurrentHealth;
            _isDead.Value = playerHealth.IsDead;
        }

        ApplyHealthState(_currentHealth.Value, _isDead.Value);
    }

    public override void OnNetworkDespawn()
    {
        _isDead.OnValueChanged -= HandleDeadStateChanged;
        _currentHealth.OnValueChanged -= HandleCurrentHealthChanged;
        if (playerHealth != null)
        {
            playerHealth.Damaged -= HandlePlayerHealthChanged;
            playerHealth.Died -= HandlePlayerDied;
            playerHealth.Restored -= HandlePlayerHealthChanged;
            playerHealth.Healed -= HandlePlayerHealthChanged;
        }
    }

    public void ApplyInitialSpawn(Vector3 spawnPosition, Quaternion spawnRotation)
    {
        if (!IsServer)
            return;

        ApplyRespawnTransform(spawnPosition, spawnRotation);

        // Placement only — never heal. A player arriving from the previous maze section keeps the health the
        // server seated on this avatar (see LevelCarryOverStore); healing locally on the owner would leave that
        // client showing full health against a server that still has them wounded, and _currentHealth never
        // changes again to correct it.
        TeleportOwnerClientRpc(spawnPosition, spawnRotation, new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { OwnerClientId }
            }
        });
    }

    /// <summary>
    /// Server-authoritative teleport of THIS player to an already-validated destination (e.g. from a
    /// <see cref="TeleportOrb"/>). Mirrors <see cref="ApplyInitialSpawn"/> — applies on the server for
    /// coherence and tells the owning client (the OwnerNetworkTransform authority) to perform the move —
    /// but does NOT heal the player. No-op if the player is dead.
    /// </summary>
    public void ServerTeleport(Vector3 position, Quaternion rotation)
    {
        if (!IsServer || !IsSpawned)
            return;
        if (_isDead.Value)
            return;

        // Brief pit-kill grace so the CharacterController re-enable frame at the destination can't trip a
        // pit volume on the same tick (the destination is already NavMesh-validated away from pits).
        BeginRespawnPitKillGrace();

        ApplyRespawnTransform(position, rotation);

        TeleportOwnerClientRpc(position, rotation, new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { OwnerClientId }
            }
        });
    }

    [ClientRpc]
    void TeleportOwnerClientRpc(Vector3 position, Quaternion rotation, ClientRpcParams clientRpcParams = default)
    {
        // The host already applied the move on the server side; only remote owners run it here.
        if (IsServer)
            return;

        ApplyRespawnTransform(position, rotation);
    }

    /// <summary>
    /// Offline / non-networked teleport: moves the local player directly using the same
    /// CharacterController-safe path as the spawn placement. Used when no netcode session is active.
    /// </summary>
    public void LocalTeleport(Vector3 position, Quaternion rotation)
    {
        ApplyRespawnTransform(position, rotation);
    }

    public bool ShouldIgnorePitKill()
    {
        return IsSpawned && Time.time < _ignorePitKillsUntil;
    }

    /// <summary>
    /// Server: the team is wiped. Sent through this player's object only because a spawned NetworkBehaviour
    /// is needed to reach every peer; every peer — the host included — runs its own exit sequence.
    /// <paramref name="everyoneDead"/> is false when someone was left alive but locked in the jail for good.
    /// </summary>
    public void ServerBroadcastTeamWipe(bool everyoneDead)
    {
        if (!IsServer || !IsSpawned)
            return;

        AchievementHooks.ServerTeamWipe(everyoneDead);
        TeamWipeRpc(everyoneDead);
    }

    [Rpc(SendTo.ClientsAndHost)]
    void TeamWipeRpc(bool everyoneDead)
    {
        TeamWipeSequence.Begin(everyoneDead);
    }

    void HandlePlayerDied()
    {
        if (!IsServer || _isDead.Value)
            return;

        networkPlayerInventory?.ServerDropAllHeldOnDeath();
        // A heavy throwable (booth ball / ring) is carried outside the hotbar via a replicated holder id, so
        // the slot sweep above cannot reach it. Left held, the corpse keeps the id for the rest of the section:
        // the booth round never sees its prop released and nobody else can pick it up.
        NetworkHeavyThrowableHold.FindHeldByPlayerObjectId(NetworkObjectId)?.ServerForceReleaseForHolderDeath();
        _isDead.Value = true;
        AchievementHooks.ServerPlayerDied(OwnerClientId);

        NetworkPlayerRagdoll netRagdoll = GetComponent<NetworkPlayerRagdoll>();
        netRagdoll?.NotifyDeathRagdollFromServer();

        // No respawn: the body stays down until the next section replaces this avatar. If that was the last
        // living player, the run is over.
        TeamWipeDirector.ServerEvaluate();
    }

    void HandlePlayerDiedWithoutSession()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
            return;

        TeamWipeDirector.EvaluateWithoutSession();
    }

    void HandlePlayerHealthChanged()
    {
        if (!IsServer || playerHealth == null)
            return;

        _currentHealth.Value = playerHealth.CurrentHealth;
        _isDead.Value = playerHealth.IsDead;
    }

    void HandleDeadStateChanged(bool previousValue, bool currentValue)
    {
        ApplyHealthState(_currentHealth.Value, currentValue);
    }

    void HandleCurrentHealthChanged(float previousValue, float currentValue)
    {
        ApplyHealthState(currentValue, _isDead.Value);
    }

    void ApplyHealthState(float currentHealth, bool isDead)
    {
        if (playerHealth != null && !IsServer)
            playerHealth.ApplyReplicatedState(currentHealth, isDead);

        if (_networkPlayerAvatar != null)
            _networkPlayerAvatar.SetLifeState(!isDead);
    }

    void ApplyRespawnTransform(Vector3 respawnPosition, Quaternion respawnRotation)
    {
        ragdollController?.ForceExitRagdollWithoutGroundSnap();

        bool hasNetworkedTransform = _ownerNetworkTransform != null && _ownerNetworkTransform.IsSpawned;

        if (hasNetworkedTransform && !_ownerNetworkTransform.CanCommitToTransform)
        {
            // Not the transform authority for this player — this is the server's copy of a client-owned
            // avatar. That owner applies the very same move through the owner-targeted ClientRpc and
            // replicates it here; writing the transform locally as well only fights the interpolator, which
            // is the visible "teleport, then rubber-band back" on the host.
            _ownerNetworkTransform.SnapObserverToLatestNetworkState();
        }
        else
        {
            bool wasCharacterControllerEnabled = characterController != null && characterController.enabled;

            if (characterController != null && wasCharacterControllerEnabled)
                characterController.enabled = false;

            transform.SetPositionAndRotation(respawnPosition, respawnRotation);

            // Flag the jump as a teleport, otherwise observers interpolate it as a streak across the level.
            if (hasNetworkedTransform)
                _ownerNetworkTransform.Teleport(respawnPosition, respawnRotation, Vector3.one);

            bool isDead = playerHealth != null ? playerHealth.IsDead : _isDead.Value;
            if (characterController != null && !isDead)
                characterController.enabled = true;
        }

        // After THIS client's own player is placed (ragdoll exit + CharacterController teleport above), the
        // OwnerNetworkTransform interpolators for every OTHER (remote) player on this client can be left seated at a
        // stale Y, so remote players render "floating" above the ground even though they are grounded on their own
        // machines. Re-seat every observer transform to its latest replicated state — the same resync
        // ProceduralMazeCoordinator applies after a maze build. Gated on IsOwner so it only runs on the client whose
        // local player actually moved.
        if (IsOwner && isActiveAndEnabled)
            StartCoroutine(ResyncRemoteObserverTransformsAfterRespawn());
    }

    IEnumerator ResyncRemoteObserverTransformsAfterRespawn()
    {
        // Repeat over a couple of frames so it corrects the stale interpolation whenever it settles during the
        // placement transition (ragdoll exit, health/life-state replication, CharacterController re-enable).
        for (int i = 0; i < 3; i++)
        {
            ResnapRemoteObserverTransforms();
            yield return null;
        }
    }

    static void ResnapRemoteObserverTransforms()
    {
        // SnapObserverToLatestNetworkState self-guards to non-authoritative (observer) instances, so the local
        // player's own transform is left untouched and only remote players are re-seated.
        OwnerNetworkTransform[] transforms = FindObjectsByType<OwnerNetworkTransform>();
        for (int i = 0; i < transforms.Length; i++)
        {
            if (transforms[i] != null)
                transforms[i].SnapObserverToLatestNetworkState();
        }

        Physics.SyncTransforms();
    }

    void BeginRespawnPitKillGrace()
    {
        if (respawnPitKillGraceSeconds <= 0f)
            return;

        _ignorePitKillsUntil = Mathf.Max(_ignorePitKillsUntil, Time.time + respawnPitKillGraceSeconds);
    }
}
