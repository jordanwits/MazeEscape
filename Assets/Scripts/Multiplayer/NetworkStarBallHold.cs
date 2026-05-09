using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Server-authoritative carry for the StarBall: not stored in <see cref="NetworkPlayerInventory"/>.
/// Hold state replicates via <see cref="_holderNetworkObjectId"/>; release uses ClientRpc +
/// <see cref="StarBallItem.ApplyReleasedWorldStateWithVelocityDelta"/> (heavy mass makes Impulse ineffective).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(StarBallItem))]
public sealed class NetworkStarBallHold : NetworkBehaviour
{
    static readonly List<NetworkStarBallHold> Instances = new(4);

    [Header("Pickup")]
    [SerializeField] float maxPickupHorizontalDistance = 5.25f;
    [SerializeField] float maxPickupVerticalDelta = 2.6f;

    [Header("Drop (G — same idea as hotbar toss)")]
    [Tooltip("Forward speed in m/s ≈ Player DropItemImpulse × this. Impulse on a ~90 mass ball is negligible; we use velocity instead.")]
    [SerializeField] float dropForwardSpeedFromPlayerImpulse = 8.5f;

    [Header("Shoot (left click — basketball arc, XZ forward + up)")]
    [Tooltip("Horizontal forward speed added (m/s).")]
    [SerializeField] float shootForwardSpeed = 10f;
    [Tooltip("Upward speed added (m/s).")]
    [SerializeField] float shootUpSpeed = 6f;
    [FormerlySerializedAs("releaseForwardOffset")]
    [SerializeField] float releaseForwardOffset = 0.42f;

    readonly NetworkVariable<ulong> _holderNetworkObjectId = new NetworkVariable<ulong>(
        0UL,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    StarBallItem _starBall;
    NetworkTransform _networkTransform;

    /// <summary>Offline / no NGO: which player is carrying this ball (not replicated).</summary>
    PlayerController _offlineHolder;

    public ulong HolderNetworkObjectId => _holderNetworkObjectId.Value;

    void Awake()
    {
        _starBall = GetComponent<StarBallItem>();
        TryGetComponent(out _networkTransform);
    }

    void OnEnable()
    {
        Instances.Add(this);
    }

    void OnDisable()
    {
        Instances.Remove(this);
    }

    public override void OnNetworkSpawn()
    {
        _holderNetworkObjectId.OnValueChanged += OnHolderChanged;
        ApplySpawnHolderState();
    }

    public override void OnNetworkDespawn()
    {
        _holderNetworkObjectId.OnValueChanged -= OnHolderChanged;
    }

    void ApplySpawnHolderState()
    {
        ulong holder = _holderNetworkObjectId.Value;
        SetPhysicsNetworkingEnabled(holder == 0UL);
        if (holder != 0UL)
        {
            _starBall.ApplyNetworkHeldState(holder);
            NotifyHolderInventoryRefresh(holder);
        }
    }

    void OnHolderChanged(ulong previous, ulong current)
    {
        if (current != 0UL)
        {
            SetPhysicsNetworkingEnabled(false);
            _starBall.ApplyNetworkHeldState(current);
        }
        else if (previous != 0UL)
        {
            SetPhysicsNetworkingEnabled(true);
        }

        if (previous != 0UL)
            NotifyHolderInventoryRefresh(previous);
        if (current != 0UL)
            NotifyHolderInventoryRefresh(current);
    }

    static void NotifyHolderInventoryRefresh(ulong playerNetworkObjectId)
    {
        if (playerNetworkObjectId == 0UL)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return;

        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(playerNetworkObjectId, out NetworkObject po) || po == null)
            return;

        if (!po.TryGetComponent(out PlayerController pc))
            return;

        pc.RefreshInventoryViewFromNetwork();
    }

    void SetPhysicsNetworkingEnabled(bool worldPhysics)
    {
        if (_networkTransform != null)
            _networkTransform.enabled = worldPhysics;
    }

    public void TryPickupOffline(PlayerController player)
    {
        if (player == null || _starBall == null || _starBall.IsHeld)
            return;
        if (!player.TryGetInventoryAttachmentTargets(out Transform hold, out Transform follow, out _))
            return;

        _offlineHolder = player;
        _starBall.Pickup(hold, follow);
        player.RefreshInventoryViewFromNetwork();
    }

    public void RequestPickupFromInteract(PlayerController interactor)
    {
        if (interactor == null || _starBall == null || _starBall.IsHeld)
            return;

        if (!NetworkManager.Singleton || !NetworkManager.Singleton.IsListening)
        {
            TryPickupOffline(interactor);
            return;
        }

        if (!IsSpawned)
            return;

        NetworkObject interactorNet = interactor.GetComponent<NetworkObject>();
        if (interactorNet == null)
            return;

        if (IsServer)
        {
            ServerValidateAndPickup(interactorNet.NetworkObjectId, interactorNet.OwnerClientId);
            return;
        }

        RequestPickupServerRpc(interactorNet.NetworkObjectId);
    }

    [ServerRpc(RequireOwnership = false)]
    void RequestPickupServerRpc(ulong playerNetworkObjectId, ServerRpcParams rpcParams = default)
    {
        ServerValidateAndPickup(playerNetworkObjectId, rpcParams.Receive.SenderClientId);
    }

    void ServerValidateAndPickup(ulong playerNetworkObjectId, ulong senderClientId)
    {
        if (!IsServer || !IsSpawned)
            return;
        if (_holderNetworkObjectId.Value != 0UL)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.SpawnManager.SpawnedObjects.TryGetValue(playerNetworkObjectId, out NetworkObject playerObj) || playerObj == null)
            return;

        if (playerObj.OwnerClientId != senderClientId)
            return;

        if (!IsInPickupRange(playerObj.transform.position))
            return;

        _holderNetworkObjectId.Value = playerNetworkObjectId;
    }

    bool IsInPickupRange(Vector3 playerFeet)
    {
        Vector3 p = transform.position;
        float dxz = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(playerFeet.x, playerFeet.z));
        if (dxz > maxPickupHorizontalDistance)
            return false;
        if (Mathf.Abs(p.y - playerFeet.y) > maxPickupVerticalDelta)
            return false;
        return true;
    }

    public void RequestDropFromOwningClient(PlayerController shooter)
    {
        if (shooter == null || _starBall == null || !_starBall.IsHeld)
            return;

        if (!NetworkManager.Singleton || !NetworkManager.Singleton.IsListening)
        {
            DropOffline(shooter);
            return;
        }

        if (!IsSpawned)
            return;

        if (IsServer)
        {
            NetworkObject shooterNet = shooter.GetComponent<NetworkObject>();
            if (shooterNet != null && shooterNet.NetworkObjectId == _holderNetworkObjectId.Value)
                ApplyDropAuthority(shooter);
            return;
        }

        RequestDropServerRpc();
    }

    [ServerRpc(RequireOwnership = false)]
    void RequestDropServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!IsServer || !IsSpawned)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.ConnectedClients.TryGetValue(rpcParams.Receive.SenderClientId, out NetworkClient client)
            || client.PlayerObject == null)
            return;

        ulong playerObjId = client.PlayerObject.NetworkObjectId;
        if (playerObjId != _holderNetworkObjectId.Value)
            return;

        if (!client.PlayerObject.TryGetComponent(out PlayerController pc))
            return;

        ApplyDropAuthority(pc);
    }

    void ApplyDropAuthority(PlayerController shooter)
    {
        BuildInventoryStyleDropVelocity(shooter, out Vector3 velocityDelta, out Vector3 dropPos, out Quaternion dropRot);
        ApplyReleaseAuthority(velocityDelta, dropPos, dropRot);
    }

    void DropOffline(PlayerController shooter)
    {
        if (_offlineHolder != shooter || !_starBall.IsHeld)
            return;

        BuildInventoryStyleDropVelocity(shooter, out Vector3 velocityDelta, out Vector3 dropPos, out Quaternion dropRot);
        _offlineHolder = null;
        _starBall.ApplyReleasedWorldStateWithVelocityDelta(dropPos, dropRot, velocityDelta);
        shooter.RefreshInventoryViewFromNetwork();
    }

    /// <summary>Matches <see cref="PlayerController.Inventory"/> local drop offsets + forward from camera.</summary>
    void BuildInventoryStyleDropVelocity(PlayerController shooter, out Vector3 velocityDelta, out Vector3 dropPosition, out Quaternion dropRotation)
    {
        Transform cam = shooter.CameraTransformForFacing;
        Vector3 f = cam != null ? cam.forward : shooter.transform.forward;
        if (f.sqrMagnitude < 0.0001f)
            f = shooter.transform.forward;
        f.Normalize();

        float toss = Mathf.Max(0.05f, shooter.DropItemImpulse * dropForwardSpeedFromPlayerImpulse);
        velocityDelta = f * toss;

        if (!shooter.TryGetInventoryAttachmentTargets(out Transform holdPoint, out _, out _))
        {
            dropPosition = shooter.transform.position + f * 0.75f;
            dropRotation = shooter.transform.rotation;
        }
        else
        {
            dropPosition = holdPoint.position + f * 0.35f;
            dropRotation = holdPoint.rotation;
        }

        dropPosition.y = Mathf.Max(dropPosition.y, shooter.transform.position.y + 0.1f);
    }

    public void RequestShootFromOwningClient(Vector3 cameraForward, PlayerController shooter)
    {
        if (shooter == null || _starBall == null || !_starBall.IsHeld)
            return;

        if (!NetworkManager.Singleton || !NetworkManager.Singleton.IsListening)
        {
            ShootOffline(cameraForward, shooter);
            return;
        }

        if (!IsSpawned)
            return;

        if (IsServer)
        {
            NetworkObject shooterNet = shooter.GetComponent<NetworkObject>();
            if (shooterNet != null && shooterNet.NetworkObjectId == _holderNetworkObjectId.Value)
                ApplyShootAuthority(cameraForward, shooter);
            return;
        }

        RequestShootServerRpc(cameraForward);
    }

    [ServerRpc(RequireOwnership = false)]
    void RequestShootServerRpc(Vector3 cameraForward, ServerRpcParams rpcParams = default)
    {
        if (!IsServer || !IsSpawned)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.ConnectedClients.TryGetValue(rpcParams.Receive.SenderClientId, out NetworkClient client)
            || client.PlayerObject == null)
            return;

        ulong playerObjId = client.PlayerObject.NetworkObjectId;
        if (playerObjId != _holderNetworkObjectId.Value)
            return;

        if (!client.PlayerObject.TryGetComponent(out PlayerController pc))
            return;

        ApplyShootAuthority(cameraForward, pc);
    }

    void ApplyShootAuthority(Vector3 cameraForward, PlayerController shooter)
    {
        BuildShootVelocity(shooter, cameraForward, out Vector3 velocityDelta, out Vector3 dropPos, out Quaternion dropRot);
        ApplyReleaseAuthority(velocityDelta, dropPos, dropRot);
    }

    void ApplyReleaseAuthority(Vector3 velocityDelta, Vector3 worldPosition, Quaternion worldRotation)
    {
        _holderNetworkObjectId.Value = 0UL;

        if (IsServer && !IsClient)
            _starBall.ApplyReleasedWorldStateWithVelocityDelta(worldPosition, worldRotation, velocityDelta);

        ApplyBallReleaseClientRpc(worldPosition, worldRotation, velocityDelta);
    }

    [ClientRpc]
    void ApplyBallReleaseClientRpc(Vector3 worldPosition, Quaternion worldRotation, Vector3 velocityDelta)
    {
        if (IsServer && !IsClient)
            return;

        _starBall.ApplyReleasedWorldStateWithVelocityDelta(worldPosition, worldRotation, velocityDelta);
        SetPhysicsNetworkingEnabled(true);
    }

    void BuildShootVelocity(PlayerController shooter, Vector3 cameraForward, out Vector3 velocityDelta, out Vector3 dropPosition, out Quaternion dropRotation)
    {
        Vector3 f = cameraForward.sqrMagnitude > 0.0001f ? cameraForward.normalized : shooter.transform.forward;
        Vector3 flat = Vector3.ProjectOnPlane(f, Vector3.up);
        if (flat.sqrMagnitude < 0.0001f)
            flat = Vector3.ProjectOnPlane(shooter.transform.forward, Vector3.up);
        flat.Normalize();

        velocityDelta = flat * shootForwardSpeed + Vector3.up * shootUpSpeed;

        Transform cam = shooter.CameraTransformForFacing;
        Vector3 origin = cam != null ? cam.position : shooter.transform.position + Vector3.up * 1.6f;
        Vector3 relFwd = cam != null ? cam.forward : shooter.transform.forward;
        dropPosition = origin + relFwd.normalized * releaseForwardOffset;
        dropPosition.y = Mathf.Max(dropPosition.y, shooter.transform.position.y + 0.1f);
        dropRotation = cam != null ? cam.rotation : Quaternion.LookRotation(shooter.transform.forward, Vector3.up);
    }

    void ShootOffline(Vector3 cameraForward, PlayerController shooter)
    {
        if (_offlineHolder != shooter || !_starBall.IsHeld)
            return;

        BuildShootVelocity(shooter, cameraForward, out Vector3 velocityDelta, out Vector3 dropPos, out Quaternion dropRot);
        _offlineHolder = null;
        _starBall.ApplyReleasedWorldStateWithVelocityDelta(dropPos, dropRot, velocityDelta);
        shooter.RefreshInventoryViewFromNetwork();
    }

    public static NetworkStarBallHold FindHeldByPlayerObjectId(ulong playerNetworkObjectId)
    {
        for (int i = 0; i < Instances.Count; i++)
        {
            NetworkStarBallHold h = Instances[i];
            if (h != null && h.IsSpawned && h._holderNetworkObjectId.Value == playerNetworkObjectId)
                return h;
        }

        return null;
    }

    public static NetworkStarBallHold FindOfflineHeldBy(PlayerController player)
    {
        if (player == null)
            return null;
        for (int i = 0; i < Instances.Count; i++)
        {
            NetworkStarBallHold h = Instances[i];
            if (h != null && h._offlineHolder == player && h._starBall != null && h._starBall.IsHeld)
                return h;
        }

        return null;
    }
}
