using UnityEngine;

public static class RuntimeNetworkBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Initialize()
    {
        if (Object.FindFirstObjectByType<MultiplayerBootstrap>() != null)
            return;

        GameObject bootstrapRoot = new GameObject("MultiplayerBootstrap");
        bootstrapRoot.AddComponent<MultiplayerBootstrap>();
    }
}
