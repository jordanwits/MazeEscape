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
    NetworkPlayerAvatar _networkPlayerAvatar;

    readonly NetworkVariable<bool> _isDead = new(false);
    readonly NetworkVariable<float> _currentHealth = new(100f);

    MultiplayerProjectSettings _projectSettings;
    Coroutine _respawnRoutine;

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

    void HandlePlayerDied()
    {
        if (!IsServer || _isDead.Value)
            return;

        networkPlayerInventory?.ServerDropHeldFlashlightOnDeath();
        _isDead.Value = true;

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

        ApplyRespawnTransform(respawnPosition, respawnRotation);
        playerHealth?.RestoreFullHealth();

        RespawnOwnerClientRpc(respawnPosition, respawnRotation, new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { OwnerClientId }
            }
        });

        _isDead.Value = false;
        _respawnRoutine = null;
    }

    [ClientRpc]
    void RespawnOwnerClientRpc(Vector3 respawnPosition, Quaternion respawnRotation, ClientRpcParams clientRpcParams = default)
    {
        if (IsServer)
            return;

        ApplyRespawnTransform(respawnPosition, respawnRotation);
        playerHealth?.RestoreFullHealth();
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

        if (characterController != null && !(_isDead.Value))
            characterController.enabled = true;
    }
}
