using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Place on an empty anchor (e.g. BallSpawn under CarnivalStart). Instantiates the StarBall prefab at this
/// transform on level build. Only the server (or offline single-player) creates the instance so
/// <see cref="ProceduralMazeCoordinator.TrySpawnMazeNetworkRigidbodyPropsIfPresent"/> can
/// <see cref="NetworkObject.Spawn"/> it; pure clients receive the spawned object from the host.
/// Runs in <see cref="Awake"/> so it executes during maze piece <see cref="Object.Instantiate"/> before the coordinator spawns network rigidbodies.
/// </summary>
[DisallowMultipleComponent]
public sealed class CarnivalBallSpawnAnchor : MonoBehaviour
{
    [SerializeField] GameObject starBallPrefab;

    void Awake()
    {
        if (starBallPrefab == null)
            return;

        if (!ShouldSpawnInstanceHere())
            return;

        GameObject instance = Instantiate(starBallPrefab, transform);
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
