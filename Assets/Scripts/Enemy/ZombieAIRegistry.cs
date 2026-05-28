using System.Collections.Generic;

/// <summary>
/// Tracks every enabled <see cref="ZombieAI"/> in the scene so server-side AI hearing paths
/// (Jailor / Clown noise lookup) can iterate candidates without per-frame
/// <c>FindObjectsByType</c> scans. Modeled on <see cref="VoiceClientRegistry"/>; entries are
/// added in <c>ZombieAI.OnEnable</c> and removed in <c>OnDisable</c>.
/// </summary>
public static class ZombieAIRegistry
{
    static readonly List<ZombieAI> s_All = new();
    static readonly IReadOnlyList<ZombieAI> s_AllReadOnly = s_All;

    public static IReadOnlyList<ZombieAI> All => s_AllReadOnly;

    public static void Register(ZombieAI zombie)
    {
        if (zombie == null)
            return;
        if (s_All.Contains(zombie))
            return;
        s_All.Add(zombie);
    }

    public static void Unregister(ZombieAI zombie)
    {
        if (zombie == null)
            return;
        s_All.Remove(zombie);
    }

    public static void Clear()
    {
        s_All.Clear();
    }
}
