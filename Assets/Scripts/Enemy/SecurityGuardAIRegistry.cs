using System.Collections.Generic;

/// <summary>
/// Lightweight runtime registry of every live <see cref="SecurityGuardAI"/>, mirroring
/// <see cref="ClownAIRegistry"/> / <see cref="JailorAIRegistry"/>. Lets a <see cref="DecoyGrenade"/>
/// find the guards in earshot without a scene search or a wide physics overlap — the decoy's hearing
/// radius is far too large to sweep with an OverlapSphere in a maze.
/// Populated from <c>SecurityGuardAI.OnEnable/OnDisable</c> on every peer.
/// </summary>
public static class SecurityGuardAIRegistry
{
    static readonly List<SecurityGuardAI> s_All = new();
    static readonly IReadOnlyList<SecurityGuardAI> s_AllReadOnly = s_All;

    public static IReadOnlyList<SecurityGuardAI> All => s_AllReadOnly;

    public static void Register(SecurityGuardAI guard)
    {
        if (guard != null && !s_All.Contains(guard))
            s_All.Add(guard);
    }

    public static void Unregister(SecurityGuardAI guard)
    {
        s_All.Remove(guard);
    }
}
