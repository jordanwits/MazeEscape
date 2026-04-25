using System;
using UnityEngine;

/// <summary>
/// Scene singleton that collects <see cref="MultiplayerSpawnPoint"/> markers and hands out
/// spawn transforms on the server. Falls back to <see cref="MultiplayerProjectSettings"/> when
/// there are no markers.
/// </summary>
[DisallowMultipleComponent]
public class MultiplayerSpawnRegistry : MonoBehaviour
{
    public static MultiplayerSpawnRegistry Instance { get; private set; }

    [SerializeField] bool randomizeRespawnSpawn = true;

    MultiplayerSpawnPoint[] _points = Array.Empty<MultiplayerSpawnPoint>();
    int _nextInitialSpawnIndex;

    public int SpawnPointCount => _points.Length;

    const string GeneratedSpawnRootName = "GeneratedSpawnPoints";

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[Multiplayer] Multiple MultiplayerSpawnRegistry objects; keeping the first instance only.", this);
            enabled = false;
            return;
        }

        Instance = this;
        RefreshSpawnPoints();
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    /// <summary>Rebuilds the cached list from the current scene (including inactive objects).</summary>
    public void RefreshSpawnPoints()
    {
        MultiplayerSpawnPoint[] found = FindObjectsByType<MultiplayerSpawnPoint>(FindObjectsInactive.Include);

        // Procedurally created spawn points and the MG start prefab both use
        // small priority values (0,1,2…), so a plain priority sort is unstable. Underlying FindObjects
        // order and tie-breaking can differ between frames or network latency, so the "second" player
        // could be sent to a different point than expected (e.g. outside the start area).
        Array.Sort(found, (a, b) =>
        {
            int c = a.Priority.CompareTo(b.Priority);
            if (c != 0)
                return c;
            c = IsUnderName(a.transform, GeneratedSpawnRootName).CompareTo(
                IsUnderName(b.transform, GeneratedSpawnRootName));
            if (c != 0)
                return -c;
            return a.GetEntityId().CompareTo(b.GetEntityId());
        });
        _points = found;
    }

    public void ResetInitialJoinRoundRobin()
    {
        _nextInitialSpawnIndex = 0;
    }

    static bool IsUnderName(Transform t, string nodeName)
    {
        for (; t != null; t = t.parent)
        {
            if (t.name == nodeName)
                return true;
        }

        return false;
    }

    /// <summary>Round-robin among spawn points; used when players first join.</summary>
    public bool TryGetInitialJoinSpawn(out Vector3 position, out Quaternion rotation)
    {
        return TryConsumeRoundRobin(ref _nextInitialSpawnIndex, out position, out rotation);
    }

    /// <summary>Either random or round-robin among spawn points, depending on settings.</summary>
    public bool TryGetRespawnSpawn(out Vector3 position, out Quaternion rotation)
    {
        if (randomizeRespawnSpawn)
            return TryConsumeRandom(out position, out rotation);

        return TryConsumeRoundRobin(ref _nextInitialSpawnIndex, out position, out rotation);
    }

    bool TryConsumeRoundRobin(ref int index, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;

        if (_points == null || _points.Length == 0)
            return false;

        MultiplayerSpawnPoint point = _points[index % _points.Length];
        index++;
        position = point.WorldPosition;
        rotation = point.WorldRotation;
        return true;
    }

    bool TryConsumeRandom(out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;

        if (_points == null || _points.Length == 0)
            return false;

        MultiplayerSpawnPoint point = _points[UnityEngine.Random.Range(0, _points.Length)];
        position = point.WorldPosition;
        rotation = point.WorldRotation;
        return true;
    }
}
