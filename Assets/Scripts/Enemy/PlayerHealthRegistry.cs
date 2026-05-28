using System.Collections.Generic;

/// <summary>
/// Tracks every enabled <see cref="PlayerHealth"/> in the scene so server-side AI fallback paths
/// (Jailor / Clown / Zombie target search) can iterate candidates without per-frame
/// <c>FindObjectsByType</c> scans. Modeled on <see cref="VoiceClientRegistry"/>; entries are
/// added in <c>PlayerHealth.OnEnable</c> and removed in <c>OnDisable</c>.
/// </summary>
public static class PlayerHealthRegistry
{
    static readonly List<PlayerHealth> s_All = new();
    static readonly IReadOnlyList<PlayerHealth> s_AllReadOnly = s_All;

    public static IReadOnlyList<PlayerHealth> All => s_AllReadOnly;

    public static void Register(PlayerHealth playerHealth)
    {
        if (playerHealth == null)
            return;
        if (s_All.Contains(playerHealth))
            return;
        s_All.Add(playerHealth);
    }

    public static void Unregister(PlayerHealth playerHealth)
    {
        if (playerHealth == null)
            return;
        s_All.Remove(playerHealth);
    }

    public static void Clear()
    {
        s_All.Clear();
    }
}
