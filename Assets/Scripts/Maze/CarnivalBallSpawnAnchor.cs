using Unity.Netcode;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Place on an empty anchor (e.g. BallSpawn / BottleSpawn under a carnival piece). Instantiates the assigned
/// prefab at this transform on level build. Only the server (or offline single-player) creates the instance so
/// <see cref="ProceduralMazeCoordinator.TrySpawnMazeNetworkRigidbodyPropsIfPresent"/> can
/// <see cref="NetworkObject.Spawn"/> it; pure clients receive the spawned object from the host.
/// Runs in <see cref="Awake"/> so it executes during maze piece <see cref="Object.Instantiate"/> before the coordinator spawns network rigidbodies.
/// The spawned instance is parented to the enclosing <see cref="MazePieceDefinition"/> root so it inherits the
/// piece's clean uniform scale rather than any non-uniform booth/sub-scale chain (otherwise the rigidbody
/// distorts visually and physics misbehaves on the contortioned mesh/collider).
/// </summary>
[DisallowMultipleComponent]
public sealed class CarnivalBallSpawnAnchor : MonoBehaviour
{
    [FormerlySerializedAs("starBallPrefab")]
    [SerializeField] GameObject prefab;

    void Awake()
    {
        if (prefab == null)
            return;

        if (!ShouldSpawnInstanceHere())
            return;

        Transform pieceRoot = FindMazePieceRoot();
        Instantiate(prefab, transform.position, transform.rotation, pieceRoot != null ? pieceRoot : transform);
    }

    Transform FindMazePieceRoot()
    {
        for (Transform t = transform; t != null; t = t.parent)
        {
            if (t.GetComponent<MazePieceDefinition>() != null)
                return t;
        }
        return null;
    }

    static bool ShouldSpawnInstanceHere()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
            return true;

        return nm.IsServer;
    }
}
