using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server spawns this <see cref="NetworkObject"/> after the maze chunk is built. Pure clients also build the
/// same chunk locally and get an unspawned duplicate; that copy is removed when the replicated instance is found.
/// While the duplicate is alive, its interact colliders are disabled so the player can only interact with the
/// server-spawned counterpart (which keeps its colliders). The gating runs in <see cref="Start"/> rather than
/// <see cref="Awake"/> because NGO flips <see cref="NetworkObject.IsSpawned"/> AFTER Awake — checking at Awake
/// can't distinguish "duplicate" from "spawned counterpart that hasn't been marked yet."
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

        // Server / host / offline: nothing to gate. The server is the authority and always keeps its own
        // colliders enabled.
        if (nm == null || !nm.IsListening || nm.IsServer)
            yield break;

        if (_networkObject == null)
            _networkObject = GetComponent<NetworkObject>();

        if (_networkObject == null)
            yield break;

        // Network-spawned counterpart: by Start, NGO has flipped IsSpawned to true. This is the real
        // instance — leave its colliders enabled so the player can interact with it.
        if (_networkObject.IsSpawned)
            yield break;

        // Local procedurally-built duplicate: silence its colliders so the player can't hit it with a
        // raycast or have a thrown body bounce off it, and freeze its rigidbodies so it can't fall/drift
        // (or fall through the floor) under client-only physics while it waits to be deduped — including
        // the case where the server counterpart never arrives and this duplicate lives out the timeout.
        NeutralizeLocalClientDuplicate();

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

        if (TryGetComponent(out RingTossGameController _)
            && spawnedObject.TryGetComponent(out RingTossGameController _))
            return true;

        if (TryGetComponent(out HoleBoardGameController _)
            && spawnedObject.TryGetComponent(out HoleBoardGameController _))
            return true;

        if (TryGetComponent(out BlackjackGameController _)
            && spawnedObject.TryGetComponent(out BlackjackGameController _))
            return true;

        if (TryGetComponent(out BottleBoothGameController _)
            && spawnedObject.TryGetComponent(out BottleBoothGameController _))
            return true;

        return NormalizeInstanceName(spawnedObject.name) == NormalizeInstanceName(gameObject.name);
    }

    void NeutralizeLocalClientDuplicate()
    {
        Collider[] colliders = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null)
                colliders[i].enabled = false;
        }

        // Without disabling the dynamic body, the duplicate keeps simulating locally (no NGO authority):
        // it falls under gravity / drifts, and since its colliders are now off it can fall out of the
        // world. Freezing it kinematic keeps it parked where the maze placed it until dedup destroys it.
        Rigidbody[] bodies = GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < bodies.Length; i++)
        {
            if (bodies[i] == null)
                continue;
            if (!bodies[i].isKinematic)
            {
                bodies[i].linearVelocity = Vector3.zero;
                bodies[i].angularVelocity = Vector3.zero;
            }
            bodies[i].isKinematic = true;
        }
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
