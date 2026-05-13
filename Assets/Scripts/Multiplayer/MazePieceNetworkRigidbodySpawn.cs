using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Marker on a networked rigidbody rooted under a procedural maze chunk. The server discovers this after
/// <see cref="Instantiate"/> and calls <see cref="NetworkObject.Spawn"/> so physics and transform
/// replicate (see <see cref="ProceduralMazeCoordinator.TrySpawnMazeNetworkRigidbodyPropsIfPresent"/>).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class MazePieceNetworkRigidbodySpawn : MonoBehaviour
{
}
