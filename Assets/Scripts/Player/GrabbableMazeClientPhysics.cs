using Unity.Netcode;
using UnityEngine;

/// <summary>
/// On non-server clients, keeps world physics off for <see cref="GrabbableInventoryItem"/> until local procedural maze
/// colliders exist. Prevents Rigidbodies from falling through tables/floors while the maze is still building or before
/// <see cref="ProceduralMazeCoordinator.IsLocalMazeCollidersReady"/> becomes true.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(GrabbableInventoryItem))]
public class GrabbableMazeClientPhysics : NetworkBehaviour
{
    GrabbableInventoryItem _item;
    Rigidbody _rb;
    bool _lockedWorldPhysicsUntilMazeReady;

    public override void OnNetworkSpawn()
    {
        _item = GetComponent<GrabbableInventoryItem>();
        if (_item != null)
            _rb = _item.ItemRigidbody;
        if (_rb == null)
            _rb = GetComponent<Rigidbody>();
    }

    void FixedUpdate()
    {
        if (!IsSpawned || IsServer || _item == null || _rb == null)
            return;

        if (ProceduralMazeCoordinator.IsLocalMazeCollidersReady)
        {
            if (_lockedWorldPhysicsUntilMazeReady)
            {
                if (!_item.IsHeld)
                {
                    _rb.isKinematic = false;
                    _rb.useGravity = true;
                    _rb.linearVelocity = Vector3.zero;
                    _rb.angularVelocity = Vector3.zero;
                    Physics.SyncTransforms();
                }

                _lockedWorldPhysicsUntilMazeReady = false;
            }

            return;
        }

        if (_item.IsHeld)
            return;

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
