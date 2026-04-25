using UnityEngine;

/// <summary>
/// PlayerPrefs-backed voice mode (open mic default; push-to-talk when enabled in settings).
/// </summary>
public static class VoiceUserSettings
{
    const string PrefKey = "MazeEscape.VoiceMode";

    public const int ModeOpenMic = 0;
    public const int ModePushToTalk = 1;

    public static int Mode
    {
        get
        {
            if (!PlayerPrefs.HasKey(PrefKey))
                return ModeOpenMic;
            return PlayerPrefs.GetInt(PrefKey, ModeOpenMic);
        }
        set
        {
            int v = value == ModePushToTalk ? ModePushToTalk : ModeOpenMic;
            PlayerPrefs.SetInt(PrefKey, v);
            PlayerPrefs.Save();
        }
    }

    public static bool IsOpenMic => Mode == ModeOpenMic;
    public static bool IsPushToTalk => Mode == ModePushToTalk;

    public static void SetOpenMic() => Mode = ModeOpenMic;
    public static void SetPushToTalk() => Mode = ModePushToTalk;
}
