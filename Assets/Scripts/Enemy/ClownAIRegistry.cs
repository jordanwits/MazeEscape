using System.Collections.Generic;

/// <summary>
/// Lightweight runtime registry of every live <see cref="ClownAI"/>, mirroring
/// <see cref="ZombieAIRegistry"/> / <see cref="PlayerHealthRegistry"/>. Lets other enemies
/// (e.g. the wind-up monkey) command the Clown without a scene search.
/// Populated from <c>ClownAI.OnEnable/OnDisable</c> on every peer.
/// </summary>
public static class ClownAIRegistry
{
    static readonly List<ClownAI> s_All = new();
    static readonly IReadOnlyList<ClownAI> s_AllReadOnly = s_All;

    public static IReadOnlyList<ClownAI> All => s_AllReadOnly;

    public static void Register(ClownAI clown)
    {
        if (clown != null && !s_All.Contains(clown))
            s_All.Add(clown);
    }

    public static void Unregister(ClownAI clown)
    {
        s_All.Remove(clown);
    }
}
