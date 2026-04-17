using UnityEngine;

/// <summary>
/// Place on an empty transform where a player should be able to spawn or respawn.
/// Lower <see cref="Priority"/> values are visited first when using round-robin initial spawns.
/// </summary>
[DisallowMultipleComponent]
public class MultiplayerSpawnPoint : MonoBehaviour
{
    [SerializeField] int priority;

    public int Priority => priority;
    public Vector3 WorldPosition => transform.position;
    public Quaternion WorldRotation => transform.rotation;

    public void SetPriority(int value)
    {
        priority = value;
    }
}
