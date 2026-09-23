using UnityEngine;

/// <summary>
/// Where the Mutant (Level03's final boss) waits. Lives inside the exit-hall piece
/// (<c>SeveranceFinishHall1</c>); <see cref="ProceduralMazeCoordinator"/> finds it after the maze is built and
/// server-spawns the boss prefab from the level's <see cref="ProceduralMazeConfig"/> here, facing along the
/// marker's forward. A plain marker — never a nested NetworkObject, which clients would silently drop
/// (see <c>MultiplayerBootstrap.EnsureNestedPrefabHashOverrides</c>).
/// </summary>
[DisallowMultipleComponent]
public class MutantBossSpawnMarker : MonoBehaviour
{
    void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.85f, 0.1f, 0.15f, 0.85f);
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 1.05f, 0.45f);
        Gizmos.DrawLine(transform.position + Vector3.up * 1.05f, transform.position + Vector3.up * 1.05f + transform.forward * 1.5f);
    }
}
