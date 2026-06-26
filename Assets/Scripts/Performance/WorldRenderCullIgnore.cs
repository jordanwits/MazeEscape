using UnityEngine;

/// <summary>
/// Marker component. Drop it on a Renderer (or any parent of one) to exclude that geometry
/// from <see cref="WorldRenderCuller"/> — e.g. a large landmark, a skybox proxy, or an
/// animated prop that travels far enough from its rest position to be mis-bucketed and must
/// stay rendered regardless of distance to the player.
/// </summary>
[DisallowMultipleComponent]
public class WorldRenderCullIgnore : MonoBehaviour
{
}
