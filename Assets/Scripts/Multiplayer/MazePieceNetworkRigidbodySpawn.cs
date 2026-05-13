using Unity.Netcode;
using UnityEngine;
using System.Collections;

/// <summary>
/// Marker on a networked rigidbody rooted under a procedural maze chunk. The server discovers this after
/// <see cref="Instantiate"/> and calls <see cref="NetworkObject.Spawn"/> so physics and transform
/// replicate (see <see cref="ProceduralMazeCoordinator.TrySpawnMazeNetworkRigidbodyPropsIfPresent"/>).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class MazePieceNetworkRigidbodySpawn : MonoBehaviour
{
    const float DuplicateSearchSeconds = 5f;
    const float DuplicateMatchDistance = 1.5f;

    NetworkObject _networkObject;

    void Awake()
    {
        _networkObject = GetComponent<NetworkObject>();
    }

    IEnumerator Start()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || nm.IsServer)
            yield break;

        if (_networkObject == null)
            _networkObject = GetComponent<NetworkObject>();

        if (_networkObject == null || _networkObject.IsSpawned)
            yield break;

        float end = Time.unscaledTime + DuplicateSearchSeconds;
        while (Time.unscaledTime < end)
        {
            if (HasSpawnedCounterpartNearby())
            {
                Destroy(gameObject);
                yield break;
            }

            yield return null;
        }
    }

    bool HasSpawnedCounterpartNearby()
    {
        if (_networkObject == null)
            return false;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return false;

        foreach (var pair in nm.SpawnManager.SpawnedObjects)
        {
            NetworkObject spawned = pair.Value;
            if (spawned == null || spawned == _networkObject)
                continue;
            if (!LooksLikeSamePrefabInstance(spawned.gameObject))
                continue;
            if ((spawned.transform.position - transform.position).sqrMagnitude > DuplicateMatchDistance * DuplicateMatchDistance)
                continue;

            return true;
        }

        return false;
    }

    bool LooksLikeSamePrefabInstance(GameObject spawned)
    {
        if (spawned == null)
            return false;

        if (TryGetComponent(out GrabbableInventoryItem localItem)
            && spawned.TryGetComponent(out GrabbableInventoryItem spawnedItem))
        {
            return localItem.ItemTypeId == spawnedItem.ItemTypeId;
        }

        return StripCloneSuffix(spawned.name) == StripCloneSuffix(gameObject.name);
    }

    static string StripCloneSuffix(string value)
    {
        const string cloneSuffix = "(Clone)";
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.EndsWith(cloneSuffix, System.StringComparison.Ordinal)
            ? value.Substring(0, value.Length - cloneSuffix.Length).TrimEnd()
            : value;
    }
}
