using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Cosmetic "collapse into a pile" corpse, built from a duplicate of the maze-prop Skeleton, split into a few
/// rigid CHUNKS (upper body, lower body, head, two arms) rather than every bone. Spawned LOCALLY on every client
/// when a <see cref="SkeletonAI"/> dies (NOT a NetworkObject) — each client simulates its own fall, which is fine
/// for a corpse and far cheaper than streaming bone transforms.
///
/// The prefab is authored standing with its feet at the root origin, so it spawns right where the enemy died.
/// There is NO propelling force: the chunks just fall under gravity from rest and topple. Inter-chunk collisions
/// are ignored so the overlapping joints don't depenetrate into a "pop" — chunks only collide with the world.
/// </summary>
[DisallowMultipleComponent]
public class SkeletonCrumble : MonoBehaviour
{
    [Tooltip("Ignore collisions between the chunks so the standing pose's overlapping joints don't shove each " +
             "other apart on spawn. Chunks still collide with the floor/walls.")]
    [SerializeField] bool ignoreInterChunkCollisions = true;
    [Tooltip("Seconds before the chunks are frozen (set kinematic) to stop ongoing physics cost.")]
    [SerializeField] float freezeAfterSeconds = 6f;
    [Tooltip("Seconds before the whole pile is destroyed.")]
    [SerializeField] float destroyAfterSeconds = 14f;

    readonly List<Rigidbody> _chunks = new();
    bool _initialized;
    float _spawnTime;
    bool _frozen;

    // Fallback in case the spawner didn't call Initialize explicitly.
    void Start() => Initialize();

    /// <summary>Begin the gravity-only collapse. Safe to call once; further calls are ignored.</summary>
    public void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        _spawnTime = Time.time;

        _chunks.Clear();
        _chunks.AddRange(GetComponentsInChildren<Rigidbody>(true));

        // Pure gravity — explicitly clear any velocity so there's no propulsion.
        foreach (var rb in _chunks)
        {
            if (rb == null)
                continue;
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        if (ignoreInterChunkCollisions)
            IgnoreInterChunkCollisions();
    }

    void IgnoreInterChunkCollisions()
    {
        var perChunk = new List<Collider[]>(_chunks.Count);
        foreach (var rb in _chunks)
            if (rb != null)
                perChunk.Add(rb.GetComponentsInChildren<Collider>(true));

        for (int a = 0; a < perChunk.Count; a++)
            for (int b = a + 1; b < perChunk.Count; b++)
                foreach (var ca in perChunk[a])
                    foreach (var cb in perChunk[b])
                        if (ca != null && cb != null)
                            Physics.IgnoreCollision(ca, cb, true);
    }

    void Update()
    {
        if (!_initialized)
            return;

        float age = Time.time - _spawnTime;

        if (!_frozen && age >= freezeAfterSeconds)
        {
            _frozen = true;
            for (int i = 0; i < _chunks.Count; i++)
                if (_chunks[i] != null)
                    _chunks[i].isKinematic = true;
        }

        if (age >= destroyAfterSeconds)
            Destroy(gameObject);
    }
}
