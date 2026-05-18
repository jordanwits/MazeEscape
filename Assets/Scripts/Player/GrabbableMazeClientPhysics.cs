using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// On non-server clients, keeps world physics off until local procedural maze colliders exist. Prevents Rigidbodies
/// from falling through floors while the maze is still building or before
/// <see cref="ProceduralMazeCoordinator.IsLocalMazeCollidersReady"/> becomes true.
/// When <see cref="GrabbableInventoryItem"/> is present, the body stays kinematic while the item is held.
/// </summary>
/// <remarks>
/// Objects with <see cref="NetworkRigidbody"/> replicate authority-driven physics; non-authority peers
/// (server or owner depending on AuthorityMode) must not enable local simulation after the maze is ready
/// or motion fights replication and looks choppy. Heavy throwables run Owner authority so the owning
/// client is the active simulator and is also skipped here — NGO's AutoUpdateKinematicState manages
/// kinematic state for that case.
/// </remarks>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
public class GrabbableMazeClientPhysics : NetworkBehaviour
{
    GrabbableInventoryItem _item;
    Rigidbody _rb;
    NetworkRigidbody _networkRigidbody;
    CarnivalBottleKnockdown _bottleKnockdown;
    bool _lockedWorldPhysicsUntilMazeReady;
    bool _kinematicByDesign;

    public override void OnNetworkSpawn()
    {
        TryGetComponent(out _networkRigidbody);
        TryGetComponent(out _item);
        TryGetComponent(out _bottleKnockdown);
        if (_item != null)
            _rb = _item.ItemRigidbody;
        if (_rb == null)
            _rb = GetComponent<Rigidbody>();

        // Props without NetworkRigidbody but with kinematic state set by their own Awake (e.g. carnival
        // bottles via CarnivalBottleKnockdown) must stay kinematic on the client — they have no network
        // physics sync, so a non-authoritative local simulation would just drift them off (and through
        // floors when their interact collider is also disabled during the pre-spawn window).
        _kinematicByDesign = _networkRigidbody == null && _rb != null && _rb.isKinematic;
    }

    void FixedUpdate()
    {
        if (!IsSpawned || IsServer || _rb == null)
            return;

        if (_item != null && _item.IsHeld)
            return;

        // Owner-authority bodies simulate locally on the owning client — don't fight NGO's kinematic state.
        if (IsOwner)
            return;

        // Let the NGO NetworkRigidbody + NetworkTransform show authority simulation; never unlock local physics here.
        if (_networkRigidbody != null)
        {
            if (!ProceduralMazeCoordinator.IsLocalMazeCollidersReady)
                FreezeLocalBody();

            return;
        }

        // Prop is kinematic by design (no NetworkRigidbody, no client-side simulation). Just keep it
        // anchored where the server placed it — any local "unfreeze" would only drift it off.
        // Exception: once a knockdown handler explicitly flips the body to dynamic (e.g. a bottle
        // taking a thrown ball), let physics simulate locally so the fall is visible to this client.
        if (_kinematicByDesign)
        {
            if (_bottleKnockdown != null && _bottleKnockdown.IsKnockedDown)
                return;
            if (!_rb.isKinematic || _rb.useGravity)
                FreezeLocalBody();
            return;
        }

        if (ProceduralMazeCoordinator.IsLocalMazeCollidersReady)
        {
            if (_lockedWorldPhysicsUntilMazeReady)
            {
                _rb.isKinematic = false;
                _rb.useGravity = true;
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
                Physics.SyncTransforms();
                _lockedWorldPhysicsUntilMazeReady = false;
            }

            return;
        }

        FreezeLocalBody();
        _lockedWorldPhysicsUntilMazeReady = true;
    }

    void FreezeLocalBody()
    {
        if (!_rb.isKinematic || _rb.useGravity)
        {
            _rb.isKinematic = true;
            _rb.useGravity = false;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
    }
}
