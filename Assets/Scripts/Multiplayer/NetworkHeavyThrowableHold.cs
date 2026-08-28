using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Carry / throw networking for heavy throwables (StarBall, ring toss rings): not stored in
/// <see cref="NetworkPlayerInventory"/>. Gameplay state (who holds it, pickup validation) stays
/// server-authoritative via <see cref="_holderNetworkObjectId"/>. The physics body uses
/// <b>Owner authority</b> NetworkTransform + NetworkRigidbody so the carrying / throwing client
/// simulates locally — eliminating roundtrip lag on the throw arc. The server reclaims ownership
/// once the body settles, so an idle ball is server-owned again (consistent state for bumps and
/// future pickups). Release uses ClientRpc + <see cref="HeavyThrowableHoldItem.ApplyReleasedWorldStateWithVelocityDelta"/>
/// only on the releasing owner; non-owners snap to the release pose and follow the owner via NGO.
/// <para>
/// The NetworkTransform must stay ENABLED while carried. A disabled NetworkTransform keeps
/// committing states each tick (NGO's tick registration ignores <c>enabled</c>) but stops
/// refreshing the state's NetworkTick, so non-owner interpolators drop every carry update as
/// stale and stay parked at the pre-pickup pose — which made thrown objects dip toward their old
/// resting spot on observer screens before correcting onto the arc. While held, the rendered pose
/// is overridden anyway by the hand attach in <see cref="GrabbableInventoryItem"/>.LateUpdate, so
/// the live replication is cosmetically irrelevant until release, where the owner
/// <see cref="NetworkTransform.Teleport"/>s to the release pose so every non-owner clears its
/// interpolation buffer and picks up the arc cleanly. While those first replicated samples are
/// still in flight (owner RTT + interpolation buffer ≈ 100–300 ms), non-owners hide the gap by
/// flying a locally-integrated ballistic arc from the same release data (observer arc fields
/// below), then crossfade onto the replicated pose. Presentation only — the owner's simulation
/// stays the single authority for bounces and the landing spot.
/// </para>
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

    [Header("Shoot (left click — charge-and-release arc)")]
    [Tooltip("Launch angle when the player is aiming straight down (or below LookDownPitchForStraightThrow). Lower = a flat, near-horizontal throw. Bottom of the look-driven angle range.")]
    [SerializeField, Range(5f, 80f)] float straightThrowLaunchAngleDegrees = 30f;
    [Tooltip("Launch angle when the player is aiming straight up (or above LookUpPitchForArchedThrow). Higher = a tall, sharply-dropping lob (good for landing in a hoop or over pegs). Top of the look-driven angle range.")]
    [SerializeField, Range(5f, 80f)] float archedThrowLaunchAngleDegrees = 70f;
    [Tooltip("How far below horizontal the player must look (degrees) for the throw to reach its flattest, StraightThrowLaunchAngle. Looking further down clamps to that angle.")]
    [SerializeField, Range(1f, 90f)] float lookDownPitchForStraightThrow = 50f;
    [Tooltip("How far above horizontal the player must look (degrees) for the throw to reach its tallest, ArchedThrowLaunchAngle. Looking further up clamps to that angle.")]
    [SerializeField, Range(1f, 90f)] float lookUpPitchForArchedThrow = 50f;
    [Tooltip("Launch speed at zero charge — a quick tap. Keep low for a weak, short lob.")]
    [SerializeField] float minShootSpeed = 2.5f;
    [Tooltip("Launch speed at full charge (bar full), in m/s. Drives the maximum throw distance and arc height.")]
    [SerializeField] float maxShootSpeed = 8.5f;
    [FormerlySerializedAs("releaseForwardOffset")]
    [SerializeField] float releaseForwardOffset = 0.42f;

    [Header("Ring Spin")]
    [Tooltip("Initial spin around a ring's hole axis when shot. Unity uses radians per second.")]
    [SerializeField, Min(0f)] float ringShootSpinAngularSpeed = 24f;
    [Tooltip("Initial spin around a ring's hole axis when dropped/tossed with G.")]
    [SerializeField, Min(0f)] float ringDropSpinAngularSpeed = 8f;

    [Header("Ownership return")]
    [Tooltip("After a client throws, ownership stays with them while the body is moving so they simulate locally. When linear+angular speed stays below this threshold for ServerSettleSecondsBeforeReturn, the server reclaims ownership so character bumps and idle state are server-side again.")]
    [SerializeField, Min(0f)] float serverReclaimSpeedThreshold = 0.35f;
    [SerializeField, Min(0.1f)] float serverSettleSecondsBeforeReturn = 1.25f;

    [Header("Observer arc presentation")]
    [Tooltip("Non-owners fly the object along a locally-integrated ballistic arc the moment the release RPC arrives, instead of waiting frozen at the release pose for replicated samples (owner RTT + interpolation buffer). Cosmetic only — replication remains authoritative for bounces and the landing spot.")]
    [SerializeField] bool observerLocalArcEnabled = true;
    [Tooltip("Seconds to crossfade the rendered pose from the local arc onto the replicated pose once fresh replicated motion arrives.")]
    [SerializeField, Min(0.05f)] float observerArcBlendSeconds = 0.22f;
    [Tooltip("Replicated motion counts as arrived once the replicated body has moved this far (meters) from the release position.")]
    [SerializeField, Min(0.01f)] float observerArcFreshDataDistance = 0.2f;
    [Tooltip("Hard cap on the local-arc override so the rendered pose can never stray from the replicated pose for long if replication stalls.")]
    [SerializeField, Min(0.2f)] float observerArcMaxSeconds = 1.5f;

    readonly NetworkVariable<ulong> _holderNetworkObjectId = new NetworkVariable<ulong>(
        0UL,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    HeavyThrowableHoldItem _item;

    /// <summary>The carried item (used to pick the one-hand vs two-hand hold pose by its socket flag).</summary>
    public GrabbableInventoryItem HeldItem => _item;
    NetworkTransform _networkTransform;
    NetworkObject _networkObject;
    Rigidbody _rb;

    PlayerController _offlineHolder;

    float _serverSettleAccumulator;
    bool _serverWatchingForSettle;

    // Observer arc presentation (non-owner cosmetic override; see class doc).
    bool _observerArcActive;
    bool _observerArcBlending;
    bool _observerArcHitObstruction;
    float _observerArcElapsed;
    float _observerArcBlendElapsed;
    float _observerArcStepAccumulator;
    float _observerArcCastRadius;
    float _observerArcLinearDamping;
    float _observerArcAngularDamping;
    Vector3 _observerArcReleasePosition;
    Vector3 _observerArcPosition;
    Vector3 _observerArcVelocity;
    Vector3 _observerArcAngularVelocity;
    Quaternion _observerArcRotation;

    public ulong HolderNetworkObjectId => _holderNetworkObjectId.Value;

    void Awake()
    {
        _item = GetComponent<HeavyThrowableHoldItem>();
        TryGetComponent(out _networkTransform);
        _networkObject = GetComponent<NetworkObject>();
        _rb = _item != null ? _item.ItemRigidbody : GetComponent<Rigidbody>();
        if (_rb == null)
            _rb = GetComponent<Rigidbody>();
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
        ulong holder = _holderNetworkObjectId.Value;
        _holderNetworkObjectId.OnValueChanged -= OnHolderChanged;
        _observerArcActive = false;

        if (holder == 0UL)
            return;

        // A body despawned while still held (booth round ending on the carrier) never writes the holder
        // back to 0 — the NetworkVariable dies with the object — so OnHolderChanged never runs the
        // release-side refresh and the holder's hotbar stays force-stashed. Run it here, but drop out of
        // the held-by lookup FIRST: this runs before the GameObject is destroyed, so a refresh that still
        // finds this hold would just re-stash everything.
        Instances.Remove(this);
        NotifyHolderInventoryRefresh(holder);
    }

    /// <summary>
    /// React immediately when NGO reassigns ownership (e.g. the thrower disconnected and
    /// DontDestroyWithOwner=true handed the body back to the server). FixedUpdate's safety re-arm would
    /// converge eventually, but this avoids a window where the body sits client-owned with no live owner.
    /// </summary>
    protected override void OnOwnershipChanged(ulong previousOwnerClientId, ulong currentOwnerClientId)
    {
        base.OnOwnershipChanged(previousOwnerClientId, currentOwnerClientId);

        if (!IsServer || _networkObject == null || !_networkObject.IsSpawned)
            return;

        // We already cleared settle state when WE handed ownership out (TransferOwnershipToClientForLocalSimulation)
        // and when WE took it back (ReturnOwnershipToServer). The case left is an EXTERNAL transfer — the only
        // one in practice is the disconnect-driven return to the server. Reset the settle watch so the next
        // throw starts clean instead of inheriting stale accumulator from the orphan.
        if (currentOwnerClientId == NetworkManager.ServerClientId)
        {
            _serverWatchingForSettle = false;
            _serverSettleAccumulator = 0f;
            _hasSpeedSample = false;
        }
    }

    void ApplySpawnHolderState()
    {
        ulong holder = _holderNetworkObjectId.Value;
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
            _observerArcActive = false;
            _item.ApplyNetworkHeldState(current);
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
        TransferOwnershipToClientForLocalSimulation(playerObj.OwnerClientId);
        if (!IsSpawned)
            NetworkPlayerInventory.ServerBroadcastHeavyThrowableStateIfNeeded(_item, true, playerNetworkObjectId);
    }

    /// <summary>
    /// Hand the NetworkObject to the picking-up / throwing client so they own the rigidbody simulation.
    /// With Owner-authority NetworkTransform + NetworkRigidbody(AutoUpdateKinematicState) this makes the
    /// owner's body non-kinematic and all other peers kinematic. Late-join state is still server-driven via
    /// the holder NetworkVariable, so no snapshot path is lost.
    /// </summary>
    void TransferOwnershipToClientForLocalSimulation(ulong newOwnerClientId)
    {
        if (!IsServer || _networkObject == null || !_networkObject.IsSpawned)
            return;
        if (_networkObject.OwnerClientId == newOwnerClientId)
            return;

        _networkObject.ChangeOwnership(newOwnerClientId);
        _serverWatchingForSettle = false;
        _serverSettleAccumulator = 0f;
    }

    void ReturnOwnershipToServer()
    {
        if (!IsServer || _networkObject == null || !_networkObject.IsSpawned)
            return;
        if (_networkObject.IsOwnedByServer)
            return;

        _networkObject.ChangeOwnership(NetworkManager.ServerClientId);
        _serverWatchingForSettle = false;
        _serverSettleAccumulator = 0f;
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
        ulong ownerClientId = ulong.MaxValue;
        if (shooter.TryGetComponent(out NetworkObject shooterNet))
            ownerClientId = shooterNet.OwnerClientId;
        ApplyReleaseAuthority(velocityDelta, angularVelocity, dropPos, dropRot, ownerClientId);
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

    public void RequestShootFromOwningClient(Vector3 cameraForward, PlayerController shooter, float charge01)
    {
        if (shooter == null || _item == null || !_item.IsHeld)
            return;

        if (!NetworkManager.Singleton || !NetworkManager.Singleton.IsListening)
        {
            ShootOffline(cameraForward, shooter, charge01);
            return;
        }

        if (!IsSpawned)
        {
            if (shooter.TryGetComponent(out NetworkPlayerInventory inventory))
                inventory.RequestShootHeavyThrowable(_item.ItemId, _item.ItemTypeId, _item.transform.position, cameraForward, charge01);
            return;
        }

        if (IsServer)
        {
            NetworkObject shooterNet = shooter.GetComponent<NetworkObject>();
            if (shooterNet != null && shooterNet.NetworkObjectId == _holderNetworkObjectId.Value)
                ApplyShootAuthority(cameraForward, shooter, charge01);
            return;
        }

        RequestShootServerRpc(cameraForward, charge01);
    }

    [ServerRpc(RequireOwnership = false)]
    void RequestShootServerRpc(Vector3 cameraForward, float charge01, ServerRpcParams rpcParams = default)
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

        ApplyShootAuthority(cameraForward, pc, charge01);
    }

    void ApplyShootAuthority(Vector3 cameraForward, PlayerController shooter, float charge01)
    {
        BuildShootVelocity(
            shooter,
            cameraForward,
            charge01,
            out Vector3 velocityDelta,
            out Vector3 angularVelocity,
            out Vector3 dropPos,
            out Quaternion dropRot);
        ulong ownerClientId = ulong.MaxValue;
        if (shooter.TryGetComponent(out NetworkObject shooterNet))
            ownerClientId = shooterNet.OwnerClientId;
        ApplyReleaseAuthority(velocityDelta, angularVelocity, dropPos, dropRot, ownerClientId);
    }

    void ApplyReleaseAuthority(
        Vector3 velocityDelta,
        Vector3 angularVelocity,
        Vector3 worldPosition,
        Quaternion worldRotation,
        ulong releasingOwnerClientId)
    {
        if (IsSpawned)
        {
            _holderNetworkObjectId.Value = 0UL;
        }
        else if (_item != null)
        {
            _item.ApplyReleasedWorldStateWithVelocityDelta(worldPosition, worldRotation, velocityDelta, angularVelocity);
            NetworkPlayerInventory.ServerBroadcastHeavyThrowableStateIfNeeded(_item, false, 0UL, worldPosition, worldRotation);
            return;
        }

        // Owner remains the throwing client so they simulate the arc locally — no roundtrip lag.
        // Start a server-side watcher that reclaims ownership once the body settles. Dedicated-server
        // case: the server is never the authority for the throw arc, so we never simulate here; the
        // owning client drives the NetworkTransform.
        _serverWatchingForSettle = IsServer && _networkObject != null && !_networkObject.IsOwnedByServer;
        _serverSettleAccumulator = 0f;
        _hasSpeedSample = false;

        ApplyReleaseClientRpc(worldPosition, worldRotation, velocityDelta, angularVelocity, releasingOwnerClientId);
    }

    void FixedUpdate()
    {
        if (!IsServer || _networkObject == null || !_networkObject.IsSpawned)
            return;

        // Safety re-arm: any released (unheld) body that is still owned by a client must get reclaimed
        // by the server, even if the settle watch was never armed or got cleared by an ownership change
        // we didn't initiate (e.g. NGO reassigning ownership after the thrower disconnected). Without
        // this, such a body could stay client-owned indefinitely, leaving bumps/idle state non-authoritative.
        if (!_serverWatchingForSettle
            && _holderNetworkObjectId.Value == 0UL
            && !_networkObject.IsOwnedByServer)
        {
            _serverWatchingForSettle = true;
            _serverSettleAccumulator = 0f;
            _hasSpeedSample = false;
        }

        if (!_serverWatchingForSettle)
            return;
        if (_holderNetworkObjectId.Value != 0UL)
        {
            // Picked up again — pickup handler already handed off ownership.
            _serverWatchingForSettle = false;
            return;
        }
        if (_networkObject.IsOwnedByServer)
        {
            _serverWatchingForSettle = false;
            return;
        }

        // Server's local rigidbody is kinematic mirror; read replicated transform delta to estimate speed.
        // Simpler: check the rigidbody (it's kinematic on server, so this won't be accurate).
        // Use NetworkTransform's last position via Rigidbody.linearVelocity when non-kinematic, otherwise
        // approximate from transform delta between FixedUpdates.
        float speed = EstimateBodySpeedForSettle();
        if (speed <= serverReclaimSpeedThreshold)
        {
            _serverSettleAccumulator += Time.fixedDeltaTime;
            if (_serverSettleAccumulator >= serverSettleSecondsBeforeReturn)
                ReturnOwnershipToServer();
        }
        else
        {
            _serverSettleAccumulator = 0f;
        }
    }

    Vector3 _lastSpeedSamplePos;
    bool _hasSpeedSample;

    float EstimateBodySpeedForSettle()
    {
        if (_rb != null && !_rb.isKinematic)
            return _rb.linearVelocity.magnitude + _rb.angularVelocity.magnitude * 0.25f;

        Vector3 p = transform.position;
        if (!_hasSpeedSample)
        {
            _hasSpeedSample = true;
            _lastSpeedSamplePos = p;
            return float.PositiveInfinity; // first tick: assume moving
        }

        float dt = Time.fixedDeltaTime;
        Vector3 dp = p - _lastSpeedSamplePos;
        _lastSpeedSamplePos = p;
        return dt > 0f ? dp.magnitude / dt : 0f;
    }

    [ClientRpc]
    void ApplyReleaseClientRpc(
        Vector3 worldPosition,
        Quaternion worldRotation,
        Vector3 velocityDelta,
        Vector3 angularVelocity,
        ulong releasingOwnerClientId)
    {
        // The releasing owner runs the rigidbody locally; everyone else mirrors pose and lets the
        // owner-authority NetworkTransform replicate the rest. Match against the client id captured
        // at release rather than IsOwner alone: if ownership changed while this RPC was in flight
        // (thrower disconnect → server auto-reclaim), the late RPC must not make the new owner
        // re-apply the throw velocity.
        bool simulateLocally = releasingOwnerClientId == ulong.MaxValue
            ? IsOwner
            : IsOwner && NetworkManager != null && NetworkManager.LocalClientId == releasingOwnerClientId;
        if (simulateLocally)
        {
            _item.ApplyReleasedWorldStateWithVelocityDelta(worldPosition, worldRotation, velocityDelta, angularVelocity);

            // Re-base every non-owner at the release pose: the teleport flag makes them clear their
            // position/rotation interpolation buffers (any carry-time or pre-pickup samples) so the
            // replicated arc starts exactly here instead of blending from a stale pose.
            if (_networkTransform != null && _networkTransform.IsSpawned)
                _networkTransform.Teleport(worldPosition, worldRotation, transform.localScale);
        }
        else
        {
            ApplyReleasedMirrorState(worldPosition, worldRotation, velocityDelta, angularVelocity);
        }
    }

    void ApplyReleasedMirrorState(Vector3 worldPosition, Quaternion worldRotation, Vector3 velocityDelta, Vector3 angularVelocity)
    {
        // Non-owner peer: snap to release pose hint; owner-authority NetworkTransform takes over from there.
        _item.ApplyNetworkWorldState(worldPosition, worldRotation, default);
        if (_item != null && _item.ItemRigidbody != null)
        {
            Rigidbody rb = _item.ItemRigidbody;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            // Move the physics body too (NetworkRigidbody.UseRigidBodyForMotion reads/writes rb pose,
            // and assigning rb.position clears the physics interpolation history like the owner path).
            rb.position = worldPosition;
            rb.rotation = worldRotation;
            // Gravity stays ON: a kinematic mirror ignores it, and NGO's AutoUpdateKinematicState only
            // restores isKinematic — not useGravity — when the server reclaims simulation authority
            // after the body settles. Turning it off here left server-owned balls floating when bumped.
        }

        BeginObserverLocalArc(worldPosition, worldRotation, velocityDelta, angularVelocity);
    }

    /// <summary>
    /// Arm the cosmetic local arc: until the owner's replicated samples reach this peer, LateUpdate
    /// renders the object along a locally-integrated projectile path started from the exact release
    /// state (already server-computed and identical to what the owner applies), so the object leaves
    /// the hand instantly instead of hanging at the release pose for owner-RTT + buffer time.
    /// </summary>
    void BeginObserverLocalArc(Vector3 worldPosition, Quaternion worldRotation, Vector3 velocity, Vector3 angularVelocity)
    {
        _observerArcActive = false;
        if (!observerLocalArcEnabled || _item == null)
            return;
        // Near-zero velocity (force-release drops) has no gap worth covering.
        if (velocity.sqrMagnitude < 0.25f)
            return;

        Rigidbody rb = _item.ItemRigidbody != null ? _item.ItemRigidbody : _rb;
        _observerArcLinearDamping = rb != null ? rb.linearDamping : 0f;
        _observerArcAngularDamping = rb != null ? rb.angularDamping : 0.05f;
        _observerArcCastRadius = ComputeObserverArcCastRadius();
        _observerArcReleasePosition = worldPosition;
        _observerArcPosition = worldPosition;
        _observerArcRotation = worldRotation;
        _observerArcVelocity = velocity;
        _observerArcAngularVelocity = angularVelocity;
        _observerArcElapsed = 0f;
        _observerArcBlendElapsed = 0f;
        _observerArcStepAccumulator = 0f;
        _observerArcBlending = false;
        _observerArcHitObstruction = false;
        _observerArcActive = true;
    }

    float ComputeObserverArcCastRadius()
    {
        // Half the smallest combined-bounds dimension: a ball sweeps its radius, a flat ring its
        // thickness. Under-sweeping just ends the override a touch late; the crossfade absorbs it.
        Collider[] colliders = GetComponentsInChildren<Collider>(false);
        bool hasBounds = false;
        Bounds combined = default;
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider c = colliders[i];
            if (c == null || c.isTrigger)
                continue;
            if (!hasBounds)
            {
                combined = c.bounds;
                hasBounds = true;
            }
            else
            {
                combined.Encapsulate(c.bounds);
            }
        }

        if (!hasBounds)
            return 0.08f;

        Vector3 e = combined.extents;
        return Mathf.Clamp(Mathf.Min(e.x, Mathf.Min(e.y, e.z)), 0.03f, 0.5f);
    }

    void LateUpdate()
    {
        if (!_observerArcActive)
            return;

        // Runs after NGO's pose application (same LateUpdate-wins pattern as the held hand-attach).
        // Any state that hands rendering to another system ends the override immediately.
        if (_item == null || _item.IsHeld || IsOwner || !IsSpawned || _holderNetworkObjectId.Value != 0UL)
        {
            _observerArcActive = false;
            return;
        }

        float dt = Time.deltaTime;
        _observerArcElapsed += dt;

        // Integrate in fixed steps mirroring PhysX (gravity, then damping) so the local arc tracks
        // the owner's real simulation to within centimeters until first contact.
        if (!_observerArcHitObstruction)
        {
            _observerArcStepAccumulator += dt;
            float step = Time.fixedDeltaTime;
            while (_observerArcStepAccumulator >= step)
            {
                _observerArcStepAccumulator -= step;
                StepObserverLocalArc(step);
                if (_observerArcHitObstruction)
                    break;
            }
        }

        // The replicated pose lives on the rigidbody (NGO drives it on non-owners; this override
        // only ever writes the transform, so reading rb never sees our own output).
        Rigidbody rb = _item.ItemRigidbody != null ? _item.ItemRigidbody : _rb;
        Vector3 replicatedPos = rb != null ? rb.position : transform.position;
        Quaternion replicatedRot = rb != null ? rb.rotation : transform.rotation;

        bool freshReplicatedMotion =
            (replicatedPos - _observerArcReleasePosition).sqrMagnitude
            >= observerArcFreshDataDistance * observerArcFreshDataDistance;

        if (!_observerArcBlending && (freshReplicatedMotion || _observerArcElapsed >= observerArcMaxSeconds))
            _observerArcBlending = true;

        float replicatedWeight = 0f;
        if (_observerArcBlending)
        {
            _observerArcBlendElapsed += dt;
            replicatedWeight = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_observerArcBlendElapsed / observerArcBlendSeconds));
            if (replicatedWeight >= 1f)
            {
                transform.SetPositionAndRotation(replicatedPos, replicatedRot);
                _observerArcActive = false;
                return;
            }
        }

        // Fractional-step extrapolation keeps the render smooth between fixed integration steps.
        Vector3 localPos = _observerArcPosition + _observerArcVelocity * _observerArcStepAccumulator;
        transform.SetPositionAndRotation(
            Vector3.Lerp(localPos, replicatedPos, replicatedWeight),
            Quaternion.Slerp(_observerArcRotation, replicatedRot, replicatedWeight));

        // Unity syncs transform writes into kinematic bodies at the next physics step, which would
        // leak this cosmetic pose into rb and fool the fresh-data check above into reading our own
        // output. Re-assert the replicated pose we just sampled so rb stays NGO's alone.
        if (rb != null && rb.isKinematic)
        {
            rb.position = replicatedPos;
            rb.rotation = replicatedRot;
        }
    }

    void StepObserverLocalArc(float step)
    {
        Vector3 v = _observerArcVelocity + Physics.gravity * step;
        v *= Mathf.Clamp01(1f - _observerArcLinearDamping * step);
        Vector3 displacement = v * step;
        float distance = displacement.magnitude;

        if (distance > 1e-6f)
        {
            Vector3 direction = displacement / distance;
            if (Physics.SphereCast(
                    _observerArcPosition,
                    _observerArcCastRadius,
                    direction,
                    out RaycastHit hit,
                    distance,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore)
                && hit.rigidbody != (_item != null ? _item.ItemRigidbody : _rb)
                && !hit.collider.transform.IsChildOf(transform))
            {
                // The arc reaches something solid before replication caught up. Don't guess the
                // bounce — park at the contact and wait for the crossfade to the authoritative pose.
                _observerArcPosition += direction * Mathf.Max(0f, hit.distance - 0.01f);
                _observerArcVelocity = Vector3.zero;
                _observerArcHitObstruction = true;
                return;
            }
        }

        _observerArcVelocity = v;
        _observerArcPosition += displacement;

        if (_observerArcAngularVelocity.sqrMagnitude > 1e-8f)
        {
            _observerArcAngularVelocity *= Mathf.Clamp01(1f - _observerArcAngularDamping * step);
            float angleDegrees = _observerArcAngularVelocity.magnitude * Mathf.Rad2Deg * step;
            _observerArcRotation =
                Quaternion.AngleAxis(angleDegrees, _observerArcAngularVelocity.normalized) * _observerArcRotation;
        }
    }

    void BuildShootVelocity(
        PlayerController shooter,
        Vector3 cameraForward,
        float charge01,
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

        // Launch angle tracks where the player is aiming: look up for a tall arcing lob, look down
        // to flatten toward a straight throw. f is unit length, so f.y is sin(lookPitch); map that
        // pitch (clamped between the down/up thresholds) onto the straight→arched angle range. The
        // chosen angle sets the arc SHAPE for this throw; charge only scales the launch speed, so a
        // quick tap is weak and short while a full charge is hard and far, but neither flattens a
        // chosen arc into a line drive. Re-clamp charge here: this runs server-authoritative, so a
        // forged client value can't exceed max range.
        float lookPitchDeg = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
        float lookT = Mathf.InverseLerp(-lookDownPitchForStraightThrow, lookUpPitchForArchedThrow, lookPitchDeg);
        float launchAngleDeg = Mathf.Lerp(straightThrowLaunchAngleDegrees, archedThrowLaunchAngleDegrees, lookT);

        float speed = Mathf.Lerp(minShootSpeed, maxShootSpeed, Mathf.Clamp01(charge01));
        float angleRad = launchAngleDeg * Mathf.Deg2Rad;
        velocityDelta = flat * (speed * Mathf.Cos(angleRad)) + Vector3.up * (speed * Mathf.Sin(angleRad));

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

    void ShootOffline(Vector3 cameraForward, PlayerController shooter, float charge01)
    {
        if (_offlineHolder != shooter || !_item.IsHeld)
            return;

        BuildShootVelocity(
            shooter,
            cameraForward,
            charge01,
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

    /// <summary>
    /// Server-side force-release used when the holding player is torn down by a client disconnect. The body is a
    /// spawned NetworkObject currently parented under the disconnecting avatar, so it must be un-held and returned
    /// to the world before that avatar hierarchy is destroyed (otherwise it is destroyed as a child on every
    /// machine). Clears the replicated holder, drops the server/host copy in place, and mirrors the released world
    /// state to the remaining clients through a surviving relay inventory. NGO reassigns ownership back to the
    /// server on its own (the prefab is DontDestroyWithOwner), and the FixedUpdate settle path then rests it.
    /// </summary>
    public void ServerForceReleaseForHolderDisconnect()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer || _item == null)
            return;

        Vector3 worldPosition = _item.transform.position;
        Quaternion worldRotation = _item.transform.rotation;
        worldPosition.y += 0.05f;

        if (IsSpawned)
            _holderNetworkObjectId.Value = 0UL;

        // Server/host copy: un-hold + re-enable world physics synchronously so it survives the avatar teardown.
        _item.ApplyReleasedWorldStateWithVelocityDelta(worldPosition, worldRotation, Vector3.zero, Vector3.zero);

        // Mirror to the remaining clients so their copies un-parent before the avatar despawn destroys them.
        NetworkPlayerInventory.ServerBroadcastHeavyThrowableStateIfNeeded(_item, false, 0UL, worldPosition, worldRotation);
    }

    /// <summary>
    /// Server-side force-release used when the holding player dies. Unlike the disconnect case the avatar and
    /// its inventory stay spawned, so this is just the normal release with no throw velocity: clearing the
    /// replicated holder lets the booth's throw tracking see the release, and the release ClientRpc un-holds
    /// the body on every peer (which is also what un-stashes the dead player's hotbar for their respawn).
    /// </summary>
    public void ServerForceReleaseForHolderDeath()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer || _item == null)
            return;

        ulong holder = IsSpawned ? _holderNetworkObjectId.Value : _item.HolderNetworkObjectId;
        if (holder == 0UL)
            return;

        Vector3 worldPosition = _item.transform.position;
        Quaternion worldRotation = _item.transform.rotation;
        worldPosition.y += 0.05f;

        ulong releasingOwnerClientId = _networkObject != null ? _networkObject.OwnerClientId : ulong.MaxValue;
        ApplyReleaseAuthority(Vector3.zero, Vector3.zero, worldPosition, worldRotation, releasingOwnerClientId);
    }

    public bool ServerTryDropFromRelay(ulong playerNetworkObjectId, ulong senderClientId)
    {
        if (!TryResolveRelayHolder(playerNetworkObjectId, senderClientId, out PlayerController pc))
            return false;
        ApplyDropAuthority(pc);
        return true;
    }

    public bool ServerTryShootFromRelay(ulong playerNetworkObjectId, ulong senderClientId, Vector3 cameraForward, float charge01)
    {
        if (!TryResolveRelayHolder(playerNetworkObjectId, senderClientId, out PlayerController pc))
            return false;
        ApplyShootAuthority(cameraForward, pc, charge01);
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
