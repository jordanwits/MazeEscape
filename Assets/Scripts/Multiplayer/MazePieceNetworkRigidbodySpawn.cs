using System.Collections;
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
    NetworkObject _networkObject;

    void Awake()
    {
        _networkObject = GetComponent<NetworkObject>();
    }

    IEnumerator Start()
    {
        yield return null;

        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsListening || networkManager.IsServer)
            yield break;

        if (_networkObject == null)
            _networkObject = GetComponent<NetworkObject>();

        if (_networkObject == null || _networkObject.IsSpawned)
            yield break;

        Destroy(gameObject);
    }
}
