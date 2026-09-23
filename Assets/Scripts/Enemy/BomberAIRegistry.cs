using System.Collections.Generic;

/// <summary>
/// Lightweight runtime registry of every live <see cref="BomberAI"/>, mirroring
/// <see cref="SecurityGuardAIRegistry"/> / <see cref="ClownAIRegistry"/> / <see cref="JailorAIRegistry"/>.
/// Lets a <see cref="DecoyGrenade"/> find the bombers in earshot without a scene search or a wide physics
/// overlap — the decoy's hearing radius is far too large to sweep with an OverlapSphere in a maze.
/// Populated from <c>BomberAI.Awake/OnDestroy</c> on every peer.
/// </summary>
public static class BomberAIRegistry
{
    static readonly List<BomberAI> s_All = new();
    static readonly IReadOnlyList<BomberAI> s_AllReadOnly = s_All;

    public static IReadOnlyList<BomberAI> All => s_AllReadOnly;

    public static void Register(BomberAI bomber)
    {
        if (bomber != null && !s_All.Contains(bomber))
            s_All.Add(bomber);
    }

    public static void Unregister(BomberAI bomber)
    {
        s_All.Remove(bomber);
    }
}
