using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Place on a ring-spawn anchor (e.g. GreenRingSpawn / BlueRingSpawn / YellowRingSpawn under CarnivalMainRoom).
/// Instantiates the assigned ring prefab at this transform on level build.
/// Only the server (or offline single-player) creates the instance so the coordinator can
/// <see cref="NetworkObject.Spawn"/> it; pure clients receive the spawned object from the host.
/// </summary>
[DisallowMultipleComponent]
public sealed class CarnivalRingSpawnAnchor : MonoBehaviour
{
    [SerializeField] GameObject ringPrefab;

    void Awake()
    {
        if (ringPrefab == null)
            return;

        if (!ShouldSpawnInstanceHere())
            return;

        GameObject instance = Instantiate(ringPrefab, transform);
        instance.transform.localPosition = Vector3.zero;
        instance.transform.localRotation = Quaternion.identity;
    }

    static bool ShouldSpawnInstanceHere()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
            return true;

        return nm.IsServer;
    }
}
