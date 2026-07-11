using System.Collections.Generic;

/// <summary>
/// Lightweight runtime registry of every live <see cref="JailorAI"/>, mirroring
/// <see cref="ClownAIRegistry"/> / <see cref="ZombieAIRegistry"/> / <see cref="PlayerHealthRegistry"/>.
/// Lets the local player cheaply find the nearest Jailor for the proximity screen shake without a
/// per-frame <c>FindObjectsByType</c> scan. Populated from <c>JailorAI.OnEnable/OnDisable</c> on every
/// peer (the AI logic only runs on the server, but the component stays enabled on clients and its
/// transform is network-driven, so the registry is valid for everyone).
/// </summary>
public static class JailorAIRegistry
{
    static readonly List<JailorAI> s_All = new();
    static readonly IReadOnlyList<JailorAI> s_AllReadOnly = s_All;

    public static IReadOnlyList<JailorAI> All => s_AllReadOnly;

    public static void Register(JailorAI jailor)
    {
        if (jailor != null && !s_All.Contains(jailor))
            s_All.Add(jailor);
    }

    public static void Unregister(JailorAI jailor)
    {
        s_All.Remove(jailor);
    }
}
