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
/// Objects with <see cref="NetworkRigidbody"/> replicate server physics; clients must not enable local simulation
/// after the maze is ready or motion fights replication and looks choppy.
/// </remarks>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
public class GrabbableMazeClientPhysics : NetworkBehaviour
{
    GrabbableInventoryItem _item;
    Rigidbody _rb;
    NetworkRigidbody _networkRigidbody;
    bool _lockedWorldPhysicsUntilMazeReady;

    public override void OnNetworkSpawn()
    {
        TryGetComponent(out _networkRigidbody);
        TryGetComponent(out _item);
        if (_item != null)
            _rb = _item.ItemRigidbody;
        if (_rb == null)
            _rb = GetComponent<Rigidbody>();
    }

    void FixedUpdate()
    {
        if (!IsSpawned || IsServer || _rb == null)
            return;

        if (_item != null && _item.IsHeld)
            return;

        // Let the NGO NetworkRigidbody + NetworkTransform show server simulation; never unlock local physics here.
        if (_networkRigidbody != null)
        {
            if (!ProceduralMazeCoordinator.IsLocalMazeCollidersReady)
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
