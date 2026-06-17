using UnityEngine;

/// <summary>
/// Marker component. Drop it on a Light (or any parent of one) to exclude that light
/// from <see cref="MazeLightCuller"/> — e.g. a finish beacon or scripted effect light
/// that must stay lit regardless of distance to the player.
/// </summary>
[DisallowMultipleComponent]
public class MazeLightCullIgnore : MonoBehaviour
{
}
