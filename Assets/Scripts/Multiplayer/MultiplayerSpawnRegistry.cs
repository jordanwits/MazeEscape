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
        MultiplayerSpawnPoint[] found = FindObjectsByType<MultiplayerSpawnPoint>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);

        Array.Sort(found, (a, b) => a.Priority.CompareTo(b.Priority));
        _points = found;
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
