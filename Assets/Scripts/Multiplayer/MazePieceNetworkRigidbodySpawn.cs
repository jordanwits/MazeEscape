using Unity.Netcode;
using UnityEngine;
using System.Collections;

/// <summary>
/// Marker on a networked rigidbody rooted under a procedural maze chunk. The server discovers this after
/// <see cref="Instantiate"/> and calls <see cref="NetworkObject.Spawn"/> so physics and transform
/// replicate (see <see cref="ProceduralMazeCoordinator.TrySpawnMazeNetworkRigidbodyPropsIfPresent"/>).
/// Non-server clients also instantiate the same chunk locally; that copy is not spawned. When the replicated
/// spawned instance appears, this duplicate is destroyed. Do not hide the local instance up front — if the
/// spawn is late (slow join, Editor paused, etc.) hiding would leave nothing visible.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class MazePieceNetworkRigidbodySpawn : MonoBehaviour
{
    const float DuplicateSearchSeconds = 60f;
    const float DuplicateMatchDistance = 8f;

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

        foreach (var pair in nm.SpawnManager.SpawnedObjects)
        {
            NetworkObject spawned = pair.Value;
            if (spawned == null || spawned == _networkObject)
                continue;
            if (!LooksLikeSamePrefabInstance(spawned))
                continue;

            // Local procedural copy and the server-spawned instance should align once Netcode applies state.
            // Use a generous radius so one slow frame / Editor pause on focus does not miss the match forever.
            if ((spawned.transform.position - transform.position).sqrMagnitude > DuplicateMatchDistance * DuplicateMatchDistance)
                continue;

            return true;
        }

        return false;
    }

    bool LooksLikeSamePrefabInstance(NetworkObject spawned)
    {
        if (spawned == null)
            return false;

        GameObject spawnedObject = spawned.gameObject;
        if (TryGetComponent(out GrabbableInventoryItem localItem)
            && spawnedObject.TryGetComponent(out GrabbableInventoryItem spawnedItem))
        {
            if (localItem.ItemTypeId != spawnedItem.ItemTypeId)
                return false;

            // Many props share an item type across colors; require the same authored name (RingBlue vs RingGreen).
            return StripCloneSuffix(spawnedObject.name) == StripCloneSuffix(gameObject.name);
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
