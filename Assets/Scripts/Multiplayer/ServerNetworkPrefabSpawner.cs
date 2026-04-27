using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Spawns a registered network prefab on the server when hosting starts.
/// If you press Play without hosting (e.g. Staging test scene), optionally instantiates the prefab without Netcode spawn
/// so <see cref="JailorAI"/> and local behaviour still run.
/// </summary>
public sealed class ServerNetworkPrefabSpawner : MonoBehaviour
{
    [SerializeField] GameObject networkPrefab;
    [Tooltip("If null, uses this object's position and rotation.")]
    [SerializeField] Transform spawnPoint;
    [Tooltip("Scene object (e.g. empty at jail cell). Assigned to spawned JailorAI.CarryDestination. Optional.")]
    [SerializeField] Transform carryDestination;
    [Tooltip("Call TrySpawn from Start (covers host-already-running when the scene loads).")]
    [SerializeField] bool trySpawnInStart = true;
    [Tooltip(
        "When no host is running (Play in editor / no NetworkManager session), still Instantiate once for local testing. "
        + "NetworkObject will not be spawned — use real Host + Spawn for multiplayer carry/parent tests.")]
    [SerializeField] bool instantiateForLocalPlayWhenNotHosting = true;

    bool _spawned;

    void OnEnable()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnServerStarted += HandleServerStarted;
    }

    void OnDisable()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnServerStarted -= HandleServerStarted;
    }

    void Start()
    {
        if (trySpawnInStart)
            TrySpawn();
    }

    void HandleServerStarted()
    {
        TrySpawn();
    }

    /// <summary>
    /// If the server is hosting: Instantiate + <see cref="NetworkObject.Spawn"/>.
    /// Otherwise, when <see cref="instantiateForLocalPlayWhenNotHosting"/> is on: Instantiate only (once).
    /// </summary>
    public void TrySpawn()
    {
        if (_spawned || networkPrefab == null)
            return;

        Transform t = spawnPoint != null ? spawnPoint : transform;
        NetworkManager nm = NetworkManager.Singleton;
        bool hosting = nm != null && nm.IsServer && nm.IsListening;

        if (hosting)
        {
            if (networkPrefab.GetComponent<NetworkObject>() == null)
            {
                Debug.LogError(
                    $"{nameof(ServerNetworkPrefabSpawner)}: Assign a prefab with a {nameof(NetworkObject)} and add it to the project's Network Prefabs list.",
                    this);
                return;
            }

            GameObject instance = Instantiate(networkPrefab, t.position, t.rotation);
            NetworkObject instanceNo = instance.GetComponent<NetworkObject>();
            if (instanceNo == null)
            {
                Destroy(instance);
                return;
            }

            instanceNo.Spawn();
            ApplyCarryDestination(instance);
            _spawned = true;
            return;
        }

        if (!instantiateForLocalPlayWhenNotHosting)
            return;

        GameObject localInstance = Instantiate(networkPrefab, t.position, t.rotation);
        ApplyCarryDestination(localInstance);
        _spawned = true;
    }

    void ApplyCarryDestination(GameObject instance)
    {
        if (carryDestination == null || instance == null)
            return;
        if (instance.TryGetComponent(out JailorAI jailorAI))
            jailorAI.SetCarryDestination(carryDestination);
    }
}
