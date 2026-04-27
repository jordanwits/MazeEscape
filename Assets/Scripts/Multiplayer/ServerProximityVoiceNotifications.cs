using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-only fan-out for proximity voice frames so AI (e.g. <see cref="JailorAI"/>) can react without polling.
/// </summary>
public static class ServerProximityVoiceNotifications
{
    static readonly List<JailorAI> s_Jailors = new();

    public static void Register(JailorAI jailor)
    {
        if (jailor == null || s_Jailors.Contains(jailor))
            return;
        s_Jailors.Add(jailor);
    }

    public static void Unregister(JailorAI jailor)
    {
        if (jailor == null)
            return;
        s_Jailors.Remove(jailor);
    }

    public static void NotifyVoiceFrameFromClient(ulong speakerClientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer)
            return;

        for (int i = s_Jailors.Count - 1; i >= 0; i--)
        {
            JailorAI j = s_Jailors[i];
            if (j == null)
            {
                s_Jailors.RemoveAt(i);
                continue;
            }

            j.OnServerHeardVoiceFrame(speakerClientId);
        }
    }
}
