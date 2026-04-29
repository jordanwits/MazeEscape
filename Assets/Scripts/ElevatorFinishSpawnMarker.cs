using UnityEngine;

/// <summary>
/// Placed on a maze chunk root (e.g. MG_Finish). Identifies the piece so the coordinator can <see cref="Unity.Netcode.NetworkObject.Spawn"/>
/// any embedded <see cref="ElevatorFinishController"/> after the chunk is instantiated.
/// </summary>
[DisallowMultipleComponent]
public class ElevatorFinishSpawnMarker : MonoBehaviour
{
}
