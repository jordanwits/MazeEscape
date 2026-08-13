using UnityEngine;

/// <summary>
/// Per-marker override for the loot markers <see cref="ProceduralMazeCoordinator"/> collects on generated
/// maze pieces. A marker carrying this component spawns its own prefab at its own rate instead of drawing
/// from the level config's <c>ItemSpawn</c> pool — that is how one authored spot (the energy drink on the
/// table in SeveranceSmallRoom1) always holds a specific item rather than a random draw from the pool
/// shared by every marker on the level.
///
/// The component identifies the marker on its own, so these markers are free to be named after what they
/// hold ("EnergyDrinkSpawn") instead of carrying the <c>ItemSpawn</c> prefix.
///
/// Like the pooled markers, this spawns a plain local pickup on every peer (not a networked spawn) — the
/// roll is driven by the maze seed, so all peers agree on whether the item is there.
/// </summary>
[DisallowMultipleComponent]
public class MazeItemSpawnPoint : MonoBehaviour
{
    [Tooltip("Pickup prefab spawned at this marker. Leave empty to fall back to the level config's ItemSpawn pool.")]
    [SerializeField] GameObject itemPrefab;

    [Tooltip("Probability [0,1] that this marker spawns its item on a given maze build.")]
    [Range(0f, 1f)]
    [SerializeField] float spawnChance = 0.5f;

    [Tooltip(
        "Stand the pickup up on the marker: the item keeps its prefab's authored (upright) pose turned by "
        + "this marker's yaw, and is lifted so the bottom of the item rests at the marker rather than its "
        + "pivot. Turn off to spawn at the marker's exact position and rotation.")]
    [SerializeField] bool standUprightOnMarker = true;

    [Tooltip(
        "Extra rotation (euler, degrees) applied on top of the prefab's authored pose while standing the "
        + "item up — for prefabs authored lying down (the bandage roll rests on its side, so -90 on X "
        + "stands it on end). Leave at zero when the prefab is already authored upright.")]
    [SerializeField] Vector3 uprightRotationOffset;

    /// <summary>Prefab this marker spawns, or null to fall back to the level config's pool.</summary>
    public GameObject ItemPrefab => itemPrefab;

    /// <summary>Probability [0,1] that this marker spawns its item on a given maze build.</summary>
    public float SpawnChance => Mathf.Clamp01(spawnChance);

    /// <summary>Whether the pickup is stood upright with its base on the marker instead of adopting the marker's pose.</summary>
    public bool StandUprightOnMarker => standUprightOnMarker;

    /// <summary>Extra rotation layered onto the prefab's authored pose when standing the item up; identity by default.</summary>
    public Quaternion UprightRotationOffset => Quaternion.Euler(uprightRotationOffset);
}
