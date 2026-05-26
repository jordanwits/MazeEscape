using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-only fan-out for proximity voice frames so AI (e.g. <see cref="JailorAI"/>, <see cref="ClownAI"/>)
/// can react without polling.
/// </summary>
public static class ServerProximityVoiceNotifications
{
    static readonly List<JailorAI> s_Jailors = new();
    static readonly List<ClownAI> s_Clowns = new();

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

    public static void Register(ClownAI clown)
    {
        if (clown == null || s_Clowns.Contains(clown))
            return;
        s_Clowns.Add(clown);
    }

    public static void Unregister(ClownAI clown)
    {
        if (clown == null)
            return;
        s_Clowns.Remove(clown);
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

        for (int i = s_Clowns.Count - 1; i >= 0; i--)
        {
            ClownAI c = s_Clowns[i];
            if (c == null)
            {
                s_Clowns.RemoveAt(i);
                continue;
            }

            c.OnServerHeardVoiceFrame(speakerClientId);
        }
    }
}
