using UnityEngine;

[CreateAssetMenu(menuName = "Maze Escape/Multiplayer Project Settings", fileName = "MultiplayerProjectSettings")]
public class MultiplayerProjectSettings : ScriptableObject
{
    [SerializeField] GameObject playerPrefab;
    [SerializeField] Vector3 levelStartPosition;
    [SerializeField] Vector3 levelStartEulerAngles;
    [SerializeField] float respawnDelaySeconds = 3f;

    public GameObject PlayerPrefab => playerPrefab;
    public Vector3 LevelStartPosition => levelStartPosition;
    public Quaternion LevelStartRotation => Quaternion.Euler(levelStartEulerAngles);
    public float RespawnDelaySeconds => respawnDelaySeconds;
}
