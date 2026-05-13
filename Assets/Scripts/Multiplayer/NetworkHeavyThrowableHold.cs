using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Server-authoritative carry for heavy throwables (StarBall, ring toss rings): not stored in
/// <see cref="NetworkPlayerInventory"/>. Holder replicates via <see cref="_holderNetworkObjectId"/>;
/// release uses ClientRpc +
/// <see cref="HeavyThrowableHoldItem.ApplyReleasedWorldStateWithVelocityDelta"/>.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(HeavyThrowableHoldItem))]
public sealed class NetworkHeavyThrowableHold : NetworkBehaviour
{
    static readonly List<NetworkHeavyThrowableHold> Instances = new(24);

    [Header("Pickup")]
    [SerializeField] float maxPickupHorizontalDistance = 5.25f;
    [SerializeField] float maxPickupVerticalDelta = 2.6f;

    [Header("Drop (G — same idea as hotbar toss)")]
    [Tooltip("Forward speed in m/s ≈ Player DropItemImpulse × this. Impulse on heavy props is weak; use velocity.")]
    [SerializeField] float dropForwardSpeedFromPlayerImpulse = 8.5f;

    [Header("Shoot (left click — toss arc, XZ forward + up)")]
    [Tooltip("Horizontal forward speed added (m/s).")]
    [SerializeField] float shootForwardSpeed = 10f;
    [Tooltip("Upward speed added (m/s).")]
    [SerializeField] float shootUpSpeed = 6f;
    [FormerlySerializedAs("releaseForwardOffset")]
    [SerializeField] float releaseForwardOffset = 0.42f;

    [Header("Ring Spin")]
    [Tooltip("Initial spin around a ring's hole axis when shot. Unity uses radians per second.")]
    [SerializeField, Min(0f)] float ringShootSpinAngularSpeed = 24f;
    [Tooltip("Initial spin around a ring's hole axis when dropped/tossed with G.")]
    [SerializeField, Min(0f)] float ringDropSpinAngularSpeed = 8f;

    readonly NetworkVariable<ulong> _holderNetworkObjectId = new NetworkVariable<ulong>(
        0UL,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    HeavyThrowableHoldItem _item;
    NetworkTransform _networkTransform;

    PlayerController _offlineHolder;
    float _nextServerWorldMirrorTime;
    bool _clientWorldMirrorActive;
    Vector3 _clientWorldMirrorTargetPosition;
    Quaternion _clientWorldMirrorTargetRotation;

    const float ServerWorldMirrorIntervalSeconds = 0.04f;
    const float ClientWorldMirrorSmoothing = 18f;
    const float ClientWorldMirrorSnapDistance = 4f;

    public ulong HolderNetworkObjectId => _holderNetworkObjectId.Value;

    void Awake()
    {
        _item = GetComponent<HeavyThrowableHoldItem>();
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

    void FixedUpdate()
    {
        if (_item == null || _item.IsHeld || IsSpawned)
            return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return;
        if (Time.unscaledTime < _nextServerWorldMirrorTime)
            return;

        _nextServerWorldMirrorTime = Time.unscaledTime + ServerWorldMirrorIntervalSeconds;
        NetworkPlayerInventory.ServerBroadcastHeavyThrowableStateIfNeeded(
            _item,
            false,
            0UL,
            _item.transform.position,
            _item.transform.rotation);
    }

    void Update()
    {
        if (!_clientWorldMirrorActive || _item == null || _item.IsHeld)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || nm.IsServer)
        {
            _clientWorldMirrorActive = false;
            return;
        }

        float t = 1f - Mathf.Exp(-ClientWorldMirrorSmoothing * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, _clientWorldMirrorTargetPosition, t);
        transform.rotation = Quaternion.Slerp(transform.rotation, _clientWorldMirrorTargetRotation, t);
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
            _item.ApplyNetworkHeldState(holder);
            NotifyHolderInventoryRefresh(holder);
        }
    }

    void OnHolderChanged(ulong previous, ulong current)
    {
        if (current != 0UL)
        {
            _clientWorldMirrorActive = false;
            SetPhysicsNetworkingEnabled(false);
            _item.ApplyNetworkHeldState(current);
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

    public bool ApplyClientWorldMirrorSnapshot(Vector3 worldPosition, Quaternion worldRotation)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || nm.IsServer || IsSpawned || _item == null)
            return false;

        if (_item.IsHeld || !_clientWorldMirrorActive)
        {
            ApplyReleasedMirrorState(worldPosition, worldRotation);
        }
        else if ((transform.position - worldPosition).sqrMagnitude > ClientWorldMirrorSnapDistance * ClientWorldMirrorSnapDistance)
        {
            transform.SetPositionAndRotation(worldPosition, worldRotation);
        }

        _clientWorldMirrorTargetPosition = worldPosition;
        _clientWorldMirrorTargetRotation = worldRotation;
        _clientWorldMirrorActive = true;
        EnsureClientMirrorPhysicsFrozen();
        return true;
    }

    void EnsureClientMirrorPhysicsFrozen()
    {
        if (_item == null || _item.ItemRigidbody == null)
            return;

        Rigidbody rb = _item.ItemRigidbody;
        if (!rb.isKinematic)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        rb.isKinematic = true;
        rb.useGravity = false;
    }

    public void TryPickupOffline(PlayerController player)
    {
        if (player == null || _item == null || _item.IsHeld)
            return;
        if (FindOfflineHeldBy(player) != null)
            return;
        if (!player.TryGetInventoryAttachmentTargets(out Transform hold, out Transform follow, out _))
            return;

        _offlineHolder = player;
        _item.Pickup(hold, follow);
        player.RefreshInventoryViewFromNetwork();
    }

    public void RequestPickupFromInteract(PlayerController interactor)
    {
        if (interactor == null || _item == null || _item.IsHeld)
            return;

        if (!NetworkManager.Singleton || !NetworkManager.Singleton.IsListening)
        {
            TryPickupOffline(interactor);
            return;
        }

        if (!IsSpawned)
        {
            if (interactor.TryGetComponent(out NetworkPlayerInventory inventory))
                inventory.RequestPickupHeavyThrowable(_item.ItemId, _item.ItemTypeId, _item.transform.position);
            return;
        }

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

        if (FindHeldByPlayerObjectId(playerNetworkObjectId) != null)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.SpawnManager.SpawnedObjects.TryGetValue(playerNetworkObjectId, out NetworkObject playerObj) || playerObj == null)
            return;

        if (playerObj.OwnerClientId != senderClientId)
            return;

        if (!IsInPickupRange(playerObj.transform.position))
            return;

        _holderNetworkObjectId.Value = playerNetworkObjectId;
        if (!IsSpawned)
            NetworkPlayerInventory.ServerBroadcastHeavyThrowableStateIfNeeded(_item, true, playerNetworkObjectId);
    }

    public bool ServerTryPickupFromRelay(ulong playerNetworkObjectId, ulong senderClientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return false;
        if (IsSpawned)
        {
            ServerValidateAndPickup(playerNetworkObjectId, senderClientId);
            return _holderNetworkObjectId.Value == playerNetworkObjectId;
        }

        if (_item == null || _item.IsHeld)
            return false;
        if (FindHeldByPlayerObjectId(playerNetworkObjectId) != null)
            return false;

        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(playerNetworkObjectId, out NetworkObject playerObj) || playerObj == null)
            return false;
        if (playerObj.OwnerClientId != senderClientId)
            return false;
        if (!IsInPickupRange(playerObj.transform.position))
            return false;

        _item.ApplyNetworkHeldState(playerNetworkObjectId);
        NetworkPlayerInventory.ServerBroadcastHeavyThrowableStateIfNeeded(_item, true, playerNetworkObjectId);
        NotifyHolderInventoryRefresh(playerNetworkObjectId);
        return true;
    }

    bool IsInPickupRange(Vector3 playerFeet)
    {
        Vector3 p = _item != null
            ? _item.GetInteractAimPointClosestTo(playerFeet)
            : transform.position;
        float dxz = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(playerFeet.x, playerFeet.z));
        if (dxz > maxPickupHorizontalDistance)
            return false;
        if (Mathf.Abs(p.y - playerFeet.y) > maxPickupVerticalDelta)
            return false;
        return true;
    }

    /// <summary>Same thresholds as <see cref="ServerValidateAndPickup"/>; used for HUD / client-side interaction hints.</summary>
    public bool IsWithinPickupProximity(Vector3 playerFeetWorld)
    {
        Vector3 p = _item != null
            ? _item.GetInteractAimPointClosestTo(playerFeetWorld)
            : transform.position;
        float dxz = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(playerFeetWorld.x, playerFeetWorld.z));
        if (dxz > maxPickupHorizontalDistance)
            return false;
        if (Mathf.Abs(p.y - playerFeetWorld.y) > maxPickupVerticalDelta)
            return false;
        return true;
    }

    public void RequestDropFromOwningClient(PlayerController shooter)
    {
        if (shooter == null || _item == null || !_item.IsHeld)
            return;

        if (!NetworkManager.Singleton || !NetworkManager.Singleton.IsListening)
        {
            DropOffline(shooter);
            return;
        }

        if (!IsSpawned)
        {
            if (shooter.TryGetComponent(out NetworkPlayerInventory inventory))
                inventory.RequestDropHeavyThrowable(_item.ItemId, _item.ItemTypeId, _item.transform.position);
            return;
        }

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
        BuildInventoryStyleDropVelocity(
            shooter,
            out Vector3 velocityDelta,
            out Vector3 angularVelocity,
            out Vector3 dropPos,
            out Quaternion dropRot);
        ApplyReleaseAuthority(velocityDelta, angularVelocity, dropPos, dropRot);
    }

    void DropOffline(PlayerController shooter)
    {
        if (_offlineHolder != shooter || !_item.IsHeld)
            return;

        BuildInventoryStyleDropVelocity(
            shooter,
            out Vector3 velocityDelta,
            out Vector3 angularVelocity,
            out Vector3 dropPos,
            out Quaternion dropRot);
        _offlineHolder = null;
        _item.ApplyReleasedWorldStateWithVelocityDelta(dropPos, dropRot, velocityDelta, angularVelocity);
        shooter.RefreshInventoryViewFromNetwork();
    }

    /// <summary>Matches <see cref="PlayerController.Inventory"/> local drop offsets + forward from camera.</summary>
    void BuildInventoryStyleDropVelocity(
        PlayerController shooter,
        out Vector3 velocityDelta,
        out Vector3 angularVelocity,
        out Vector3 dropPosition,
        out Quaternion dropRotation)
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

        if (TryComputeRingFlatReleaseRotation(velocityDelta, shooter, out Quaternion ringFlat))
            dropRotation = ringFlat;

        angularVelocity = BuildRingReleaseAngularVelocity(dropRotation, ringDropSpinAngularSpeed);
    }

    /// <summary>
    /// Release uses camera rotation, which puts a torus plane vertical (“standing”). Lay the ring flat instead:
    /// hole axis → world up, then yaw toward horizontal throw direction.
    /// </summary>
    bool TryComputeRingFlatReleaseRotation(Vector3 velocityGuess, PlayerController shooter, out Quaternion worldRotation)
    {
        worldRotation = default;
        if (_item.GetComponent<RingTossItem>() == null)
            return false;

        Vector3 planar = Vector3.ProjectOnPlane(velocityGuess, Vector3.up);
        if (planar.sqrMagnitude < 1e-8f)
        {
            Transform camPlanar = shooter != null ? shooter.CameraTransformForFacing : null;
            Vector3 camF =
                camPlanar != null ? camPlanar.forward : (_item.transform.forward);
            planar = Vector3.ProjectOnPlane(camF, Vector3.up);
        }

        planar = planar.sqrMagnitude < 1e-8f ? _item.transform.forward : planar.normalized;

        RingCompoundColliders ringShape = GetComponent<RingCompoundColliders>();
        Vector3 holeAxisLocal =
            ringShape != null ? ringShape.NormalizedTorusHoleAxisLocal : Vector3.up;

        // Map local axes: hole axis ↔ world up, ring opening ↔ horizontal toss direction.
        // (Avoid stacking FromToRotation * LookRotation in different frames — preserves prefab/import twist.)
        Vector3 fwdGuess = Vector3.forward;
        if (Mathf.Abs(Vector3.Dot(fwdGuess, holeAxisLocal)) > 0.92f)
            fwdGuess = Vector3.right;
        Vector3 fwdL = Vector3.ProjectOnPlane(fwdGuess, holeAxisLocal);
        fwdL = fwdL.sqrMagnitude < 1e-8f ? Vector3.Cross(holeAxisLocal, Vector3.right).normalized : fwdL.normalized;

        Quaternion localOpeningFrame = Quaternion.LookRotation(fwdL, holeAxisLocal);
        Quaternion worldFlatFrame = Quaternion.LookRotation(planar, Vector3.up);
        worldRotation = worldFlatFrame * Quaternion.Inverse(localOpeningFrame);
        return true;
    }

    public void RequestShootFromOwningClient(Vector3 cameraForward, PlayerController shooter)
    {
        if (shooter == null || _item == null || !_item.IsHeld)
            return;

        if (!NetworkManager.Singleton || !NetworkManager.Singleton.IsListening)
        {
            ShootOffline(cameraForward, shooter);
            return;
        }

        if (!IsSpawned)
        {
            if (shooter.TryGetComponent(out NetworkPlayerInventory inventory))
                inventory.RequestShootHeavyThrowable(_item.ItemId, _item.ItemTypeId, _item.transform.position, cameraForward);
            return;
        }

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
        BuildShootVelocity(
            shooter,
            cameraForward,
            out Vector3 velocityDelta,
            out Vector3 angularVelocity,
            out Vector3 dropPos,
            out Quaternion dropRot);
        ApplyReleaseAuthority(velocityDelta, angularVelocity, dropPos, dropRot);
    }

    void ApplyReleaseAuthority(Vector3 velocityDelta, Vector3 angularVelocity, Vector3 worldPosition, Quaternion worldRotation)
    {
        if (IsSpawned)
            _holderNetworkObjectId.Value = 0UL;
        else if (_item != null)
            _item.ApplyReleasedWorldStateWithVelocityDelta(worldPosition, worldRotation, velocityDelta, angularVelocity);

        if (IsSpawned && IsServer && !IsClient)
            _item.ApplyReleasedWorldStateWithVelocityDelta(worldPosition, worldRotation, velocityDelta, angularVelocity);

        if (!IsSpawned)
            NetworkPlayerInventory.ServerBroadcastHeavyThrowableStateIfNeeded(_item, false, 0UL, worldPosition, worldRotation);
        if (IsSpawned)
            ApplyReleaseClientRpc(worldPosition, worldRotation, velocityDelta, angularVelocity);
    }

    [ClientRpc]
    void ApplyReleaseClientRpc(Vector3 worldPosition, Quaternion worldRotation, Vector3 velocityDelta, Vector3 angularVelocity)
    {
        if (IsServer && !IsClient)
            return;

        if (IsServer)
            _item.ApplyReleasedWorldStateWithVelocityDelta(worldPosition, worldRotation, velocityDelta, angularVelocity);
        else
            ApplyReleasedMirrorState(worldPosition, worldRotation);

        SetPhysicsNetworkingEnabled(true);
    }

    void ApplyReleasedMirrorState(Vector3 worldPosition, Quaternion worldRotation)
    {
        _item.ApplyNetworkWorldState(worldPosition, worldRotation, default);
        EnsureClientMirrorPhysicsFrozen();
    }

    void BuildShootVelocity(
        PlayerController shooter,
        Vector3 cameraForward,
        out Vector3 velocityDelta,
        out Vector3 angularVelocity,
        out Vector3 dropPosition,
        out Quaternion dropRotation)
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

        dropRotation =
            TryComputeRingFlatReleaseRotation(velocityDelta, shooter, out Quaternion ringFlatShoot)
                ? ringFlatShoot
                : CameraStyleReleaseRotation(shooter);

        angularVelocity = BuildRingReleaseAngularVelocity(dropRotation, ringShootSpinAngularSpeed);
    }

    Vector3 BuildRingReleaseAngularVelocity(Quaternion worldRotation, float angularSpeed)
    {
        if (angularSpeed <= 0f || _item.GetComponent<RingTossItem>() == null)
            return Vector3.zero;

        RingCompoundColliders ringShape = GetComponent<RingCompoundColliders>();
        Vector3 holeAxisLocal =
            ringShape != null ? ringShape.NormalizedTorusHoleAxisLocal : Vector3.up;
        Vector3 worldSpinAxis = worldRotation * holeAxisLocal;
        if (worldSpinAxis.sqrMagnitude < 1e-8f)
            return Vector3.zero;

        return worldSpinAxis.normalized * angularSpeed;
    }

    static Quaternion CameraStyleReleaseRotation(PlayerController shooter)
    {
        Transform cam = shooter.CameraTransformForFacing;
        return cam != null ? cam.rotation : Quaternion.LookRotation(shooter.transform.forward, Vector3.up);
    }

    void ShootOffline(Vector3 cameraForward, PlayerController shooter)
    {
        if (_offlineHolder != shooter || !_item.IsHeld)
            return;

        BuildShootVelocity(
            shooter,
            cameraForward,
            out Vector3 velocityDelta,
            out Vector3 angularVelocity,
            out Vector3 dropPos,
            out Quaternion dropRot);
        _offlineHolder = null;
        _item.ApplyReleasedWorldStateWithVelocityDelta(dropPos, dropRot, velocityDelta, angularVelocity);
        shooter.RefreshInventoryViewFromNetwork();
    }

    public static NetworkHeavyThrowableHold FindHeldByPlayerObjectId(ulong playerNetworkObjectId)
    {
        for (int i = 0; i < Instances.Count; i++)
        {
            NetworkHeavyThrowableHold h = Instances[i];
            if (h == null)
                continue;
            if (h.IsSpawned && h._holderNetworkObjectId.Value == playerNetworkObjectId)
                return h;
            if (h._item != null && h._item.HolderNetworkObjectId == playerNetworkObjectId)
                return h;
        }

        return null;
    }

    public bool ServerTryDropFromRelay(ulong playerNetworkObjectId, ulong senderClientId)
    {
        if (!TryResolveRelayHolder(playerNetworkObjectId, senderClientId, out PlayerController pc))
            return false;
        ApplyDropAuthority(pc);
        return true;
    }

    public bool ServerTryShootFromRelay(ulong playerNetworkObjectId, ulong senderClientId, Vector3 cameraForward)
    {
        if (!TryResolveRelayHolder(playerNetworkObjectId, senderClientId, out PlayerController pc))
            return false;
        ApplyShootAuthority(cameraForward, pc);
        return true;
    }

    bool TryResolveRelayHolder(ulong playerNetworkObjectId, ulong senderClientId, out PlayerController playerController)
    {
        playerController = null;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return false;

        ulong heldBy = IsSpawned ? _holderNetworkObjectId.Value : (_item != null ? _item.HolderNetworkObjectId : 0UL);
        if (heldBy != playerNetworkObjectId)
            return false;

        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(playerNetworkObjectId, out NetworkObject playerObj) || playerObj == null)
            return false;
        if (playerObj.OwnerClientId != senderClientId)
            return false;

        return playerObj.TryGetComponent(out playerController) && playerController != null;
    }

    public static NetworkHeavyThrowableHold FindOfflineHeldBy(PlayerController player)
    {
        if (player == null)
            return null;
        for (int i = 0; i < Instances.Count; i++)
        {
            NetworkHeavyThrowableHold h = Instances[i];
            if (h != null && h._offlineHolder == player && h._item != null && h._item.IsHeld)
                return h;
        }

        return null;
    }
}
