using System.Collections;
using Unity.Netcode;
using UnityEngine;

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

    MultiplayerProjectSettings _projectSettings;
    Coroutine _respawnRoutine;
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

        _projectSettings = Resources.Load<MultiplayerProjectSettings>("MultiplayerProjectSettings");
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

        RespawnOwnerClientRpc(spawnPosition, spawnRotation, new ClientRpcParams
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
    /// but does NOT heal the player. No-op if the player is dead/respawning.
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
        // The host already applied the move in ServerTeleport; only remote owners run it here.
        if (IsServer)
            return;

        ApplyRespawnTransform(position, rotation);
    }

    /// <summary>
    /// Offline / non-networked teleport: moves the local player directly using the same
    /// CharacterController-safe path as respawn. Used when no netcode session is active.
    /// </summary>
    public void LocalTeleport(Vector3 position, Quaternion rotation)
    {
        ApplyRespawnTransform(position, rotation);
    }

    public bool ShouldIgnorePitKill()
    {
        return IsSpawned && Time.time < _ignorePitKillsUntil;
    }

    void HandlePlayerDied()
    {
        if (!IsServer || _isDead.Value)
            return;

        networkPlayerInventory?.ServerDropAllHeldOnDeath();
        _isDead.Value = true;

        NetworkPlayerRagdoll netRagdoll = GetComponent<NetworkPlayerRagdoll>();
        netRagdoll?.NotifyDeathRagdollFromServer();

        if (_respawnRoutine != null)
            StopCoroutine(_respawnRoutine);

        _respawnRoutine = StartCoroutine(ServerRespawnRoutine());
    }

    void HandlePlayerHealthChanged()
    {
        if (!IsServer || playerHealth == null)
            return;

        _currentHealth.Value = playerHealth.CurrentHealth;
        _isDead.Value = playerHealth.IsDead;
    }

    IEnumerator ServerRespawnRoutine()
    {
        float delaySeconds = _projectSettings != null ? Mathf.Max(0f, _projectSettings.RespawnDelaySeconds) : 3f;
        if (delaySeconds > 0f)
            yield return new WaitForSeconds(delaySeconds);

        Vector3 respawnPosition = _projectSettings != null ? _projectSettings.LevelStartPosition : transform.position;
        Quaternion respawnRotation = _projectSettings != null ? _projectSettings.LevelStartRotation : transform.rotation;
        if (MultiplayerSpawnRegistry.Instance != null
            && MultiplayerSpawnRegistry.Instance.TryGetRespawnSpawn(out Vector3 registryPosition, out Quaternion registryRotation))
        {
            respawnPosition = registryPosition;
            respawnRotation = registryRotation;
        }

        BeginRespawnPitKillGrace();
        playerHealth?.RestoreFullHealth();
        _isDead.Value = false;

        GetComponent<NetworkPlayerRagdoll>()?.ForceExitRagdollFromServer();
        ApplyRespawnTransform(respawnPosition, respawnRotation);

        RespawnOwnerClientRpc(respawnPosition, respawnRotation, new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { OwnerClientId }
            }
        });

        _respawnRoutine = null;
    }

    [ClientRpc]
    void RespawnOwnerClientRpc(Vector3 respawnPosition, Quaternion respawnRotation, ClientRpcParams clientRpcParams = default)
    {
        if (IsServer)
            return;

        playerHealth?.RestoreFullHealth();
        ApplyRespawnTransform(respawnPosition, respawnRotation);
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

        bool wasCharacterControllerEnabled = characterController != null && characterController.enabled;

        if (characterController != null && wasCharacterControllerEnabled)
            characterController.enabled = false;

        transform.SetPositionAndRotation(respawnPosition, respawnRotation);

        bool isDead = playerHealth != null ? playerHealth.IsDead : _isDead.Value;
        if (characterController != null && !isDead)
            characterController.enabled = true;

        // After THIS client's own player respawns (ragdoll exit + CharacterController teleport above), the
        // OwnerNetworkTransform interpolators for every OTHER (remote) player on this client can be left seated at a
        // stale Y, so remote players render "floating" above the ground even though they are grounded on their own
        // machines. Re-seat every observer transform to its latest replicated state — the same resync
        // ProceduralMazeCoordinator applies after a maze build. Gated on IsOwner so it only runs on the client whose
        // local player actually respawned.
        if (IsOwner && isActiveAndEnabled)
            StartCoroutine(ResyncRemoteObserverTransformsAfterRespawn());
    }

    IEnumerator ResyncRemoteObserverTransformsAfterRespawn()
    {
        // Repeat over a couple of frames so it corrects the stale interpolation whenever it settles during the
        // respawn transition (ragdoll exit, health/life-state replication, CharacterController re-enable).
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
