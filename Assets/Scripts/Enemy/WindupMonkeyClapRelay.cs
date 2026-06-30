using UnityEngine;

/// <summary>
/// Sits on the rigged model child (the GameObject that owns the <see cref="Animator"/>) so the
/// WalkClap clip's AnimationEvents can reach it. Forwards each cymbal-clap event up to the
/// <see cref="WindupMonkeyAI"/> on a parent. The event fires on every peer that plays the clip
/// (the server replicates the animator via ServerNetworkAnimator), which is exactly what we want:
/// each client plays the clap sound locally, and the server also lures the Clown.
/// </summary>
[DisallowMultipleComponent]
public class WindupMonkeyClapRelay : MonoBehaviour
{
    [SerializeField] WindupMonkeyAI monkey;

    void Awake()
    {
        if (monkey == null)
            monkey = GetComponentInParent<WindupMonkeyAI>();
    }

    // AnimationEvent target name (set on the WalkClap clip at the two clap frames).
    public void OnCymbalClap()
    {
        if (monkey == null)
            monkey = GetComponentInParent<WindupMonkeyAI>();
        if (monkey != null)
            monkey.HandleCymbalClap();
    }
}
