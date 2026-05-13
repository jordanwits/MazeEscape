using Unity.Netcode;
using UnityEngine;
using System.Collections;

/// <summary>
/// Server spawns this <see cref="NetworkObject"/> after the maze chunk is built. Pure clients also build the
/// same chunk locally and get an unspawned duplicate; that copy is removed when the replicated instance is found.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class MazePieceNetworkRigidbodySpawn : MonoBehaviour
{
    const float DuplicateSearchSeconds = 15f;
    const float DuplicateMatchDistance = 3f;

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
            if (HasSpawnedCounterpart())
            {
                Destroy(gameObject);
                yield break;
            }

            yield return null;
        }
    }

    bool HasSpawnedCounterpart()
    {
        if (_networkObject == null)
            return false;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return false;

        float d2 = DuplicateMatchDistance * DuplicateMatchDistance;

        foreach (var pair in nm.SpawnManager.SpawnedObjects)
        {
            NetworkObject spawned = pair.Value;
            if (spawned == null || spawned == _networkObject)
                continue;
            if (!SamePropIdentity(spawned.gameObject))
                continue;
            if ((spawned.transform.position - transform.position).sqrMagnitude > d2)
                continue;

            return true;
        }

        return false;
    }

    bool SamePropIdentity(GameObject spawnedObject)
    {
        if (spawnedObject == null)
            return false;

        if (TryGetComponent(out GrabbableInventoryItem localItem)
            && spawnedObject.TryGetComponent(out GrabbableInventoryItem other))
        {
            return localItem.ItemTypeId == other.ItemTypeId
                && StripCloneSuffix(spawnedObject.name) == StripCloneSuffix(gameObject.name);
        }

        return StripCloneSuffix(spawnedObject.name) == StripCloneSuffix(gameObject.name);
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
