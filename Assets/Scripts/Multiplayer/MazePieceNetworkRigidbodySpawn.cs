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
    Renderer[] _placeholderRenderers;
    Collider[] _placeholderColliders;
    Rigidbody[] _placeholderRigidbodies;
    bool _placeholderSuppressed;

    void Awake()
    {
        _networkObject = GetComponent<NetworkObject>();
        _placeholderRenderers = GetComponentsInChildren<Renderer>(true);
        _placeholderColliders = GetComponentsInChildren<Collider>(true);
        _placeholderRigidbodies = GetComponentsInChildren<Rigidbody>(true);
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

        SuppressLocalPlaceholder();

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

            // Same prefab identity can be verified without NGO's GlobalObjectIdHash (not on all Netcode versions).
            // Local procedural copy and the server-spawned instance should overlap when they describe the same placement.
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
            return localItem.ItemTypeId == spawnedItem.ItemTypeId;
        }

        return StripCloneSuffix(spawnedObject.name) == StripCloneSuffix(gameObject.name);
    }

    void SuppressLocalPlaceholder()
    {
        if (_placeholderSuppressed)
            return;

        SetRenderersEnabled(false);
        SetCollidersEnabled(false);
        FreezeRigidbodies();
        _placeholderSuppressed = true;
    }

    void SetRenderersEnabled(bool enabled)
    {
        if (_placeholderRenderers == null)
            return;

        for (int i = 0; i < _placeholderRenderers.Length; i++)
        {
            if (_placeholderRenderers[i] != null)
                _placeholderRenderers[i].enabled = enabled;
        }
    }

    void SetCollidersEnabled(bool enabled)
    {
        if (_placeholderColliders == null)
            return;

        for (int i = 0; i < _placeholderColliders.Length; i++)
        {
            if (_placeholderColliders[i] != null)
                _placeholderColliders[i].enabled = enabled;
        }
    }

    void FreezeRigidbodies()
    {
        if (_placeholderRigidbodies == null)
            return;

        for (int i = 0; i < _placeholderRigidbodies.Length; i++)
        {
            Rigidbody body = _placeholderRigidbodies[i];
            if (body == null)
                continue;

            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
            body.useGravity = false;
        }
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
