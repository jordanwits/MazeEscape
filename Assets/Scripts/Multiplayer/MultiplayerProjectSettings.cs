using UnityEngine;

[CreateAssetMenu(menuName = "Maze Escape/Multiplayer Project Settings", fileName = "MultiplayerProjectSettings")]
public class MultiplayerProjectSettings : ScriptableObject
{
    [SerializeField] GameObject playerPrefab;
    [SerializeField] Vector3 levelStartPosition;
    [SerializeField] Vector3 levelStartEulerAngles;
    [SerializeField] float respawnDelaySeconds = 3f;

    [Header("Connection approval")]
    [Tooltip("If left empty, falls back to Application.version (the build version set in PlayerSettings). " +
        "Override only when you need a network-protocol version that's independent from the Unity build version.")]
    [SerializeField] string buildVersionOverride = "";
    [Tooltip("Hard cap including the host. Connection approval rejects further joins once the lobby is full.")]
    [SerializeField, Min(1)] int maxPlayers = 4;

    public GameObject PlayerPrefab => playerPrefab;
    public Vector3 LevelStartPosition => levelStartPosition;
    public Quaternion LevelStartRotation => Quaternion.Euler(levelStartEulerAngles);
    public float RespawnDelaySeconds => respawnDelaySeconds;
    public string BuildVersion => string.IsNullOrWhiteSpace(buildVersionOverride) ? Application.version : buildVersionOverride;
    public int MaxPlayers => maxPlayers;
}
