using Unity.Netcode;
using UnityEngine;

/// <summary>
/// On non-server clients, keeps world physics off until local procedural maze colliders exist. Prevents Rigidbodies
/// from falling through floors while the maze is still building or before
/// <see cref="ProceduralMazeCoordinator.IsLocalMazeCollidersReady"/> becomes true.
/// When <see cref="GrabbableInventoryItem"/> is present, the body stays kinematic while the item is held.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
public class GrabbableMazeClientPhysics : NetworkBehaviour
{
    GrabbableInventoryItem _item;
    Rigidbody _rb;
    bool _lockedWorldPhysicsUntilMazeReady;

    public override void OnNetworkSpawn()
    {
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

        if (!_rb.isKinematic || _rb.useGravity)
        {
            _rb.isKinematic = true;
            _rb.useGravity = false;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }

        _lockedWorldPhysicsUntilMazeReady = true;
    }
}
