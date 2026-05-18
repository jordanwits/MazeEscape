using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server spawns this <see cref="NetworkObject"/> after the maze chunk is built. Pure clients also build the
/// same chunk locally and get an unspawned duplicate; that copy is removed when the replicated instance is found.
/// Until the duplicate is resolved, its interact colliders are disabled so the player can only interact with the
/// server-spawned counterpart. <see cref="OnNetworkSpawn"/> restores those colliders on the network-spawned
/// instance (NGO marks <see cref="NetworkObject.IsSpawned"/> after Awake, so the spawned counterpart goes through
/// the same Awake-time disable as the duplicate and must re-enable itself once it knows it is the real one).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class MazePieceNetworkRigidbodySpawn : NetworkBehaviour
{
    const float DuplicateSearchSeconds = 15f;
    const float DuplicateMatchDistance = 3f;

    NetworkObject _networkObject;
    List<Collider> _collidersDisabledAtAwake;

    void Awake()
    {
        _networkObject = GetComponent<NetworkObject>();

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening && !nm.IsServer
            && _networkObject != null && !_networkObject.IsSpawned)
        {
            DisableLocalClientInteractColliders();
        }
    }

    public override void OnNetworkSpawn()
    {
        // NGO instantiates the spawned counterpart on the client by calling Unity Instantiate, which runs
        // our Awake before NGO flips IsSpawned to true. That makes Awake mistake the real spawned object for
        // a duplicate and disable its colliders. Once OnNetworkSpawn fires (IsSpawned is true here), undo
        // the disable so the player can interact with this instance. The local procedurally-built duplicate
        // never reaches OnNetworkSpawn, so its colliders stay disabled until it is destroyed.
        RestoreCollidersDisabledAtAwake();
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
                && NormalizeInstanceName(spawnedObject.name) == NormalizeInstanceName(gameObject.name);
        }

        if (TryGetComponent(out BasketballGameController _)
            && spawnedObject.TryGetComponent(out BasketballGameController _))
            return true;

        return NormalizeInstanceName(spawnedObject.name) == NormalizeInstanceName(gameObject.name);
    }

    void DisableLocalClientInteractColliders()
    {
        Collider[] colliders = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider c = colliders[i];
            if (c == null || !c.enabled)
                continue;
            c.enabled = false;
            (_collidersDisabledAtAwake ??= new List<Collider>(colliders.Length)).Add(c);
        }
    }

    void RestoreCollidersDisabledAtAwake()
    {
        if (_collidersDisabledAtAwake == null)
            return;

        for (int i = 0; i < _collidersDisabledAtAwake.Count; i++)
        {
            Collider c = _collidersDisabledAtAwake[i];
            if (c != null)
                c.enabled = true;
        }

        _collidersDisabledAtAwake = null;
    }

    static string NormalizeInstanceName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        value = value.TrimEnd();
        const string cloneSuffix = "(Clone)";
        if (value.EndsWith(cloneSuffix, System.StringComparison.Ordinal))
            value = value.Substring(0, value.Length - cloneSuffix.Length).TrimEnd();

        // Nested prefab instances often use Unity's " (1)" duplicate suffix instead of "(Clone)".
        int spaceParen = value.LastIndexOf(" (", System.StringComparison.Ordinal);
        if (spaceParen > 0 && value.EndsWith(")", System.StringComparison.Ordinal))
        {
            string digits = value.Substring(spaceParen + 2, value.Length - spaceParen - 3);
            if (int.TryParse(digits, out _))
                value = value.Substring(0, spaceParen);
        }

        return value;
    }
}
