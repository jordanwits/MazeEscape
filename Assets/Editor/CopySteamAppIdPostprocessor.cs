using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Writes steam_appid.txt next to the built executable for builds that start Steam on their own (see
/// <see cref="SteamworksBootstrap.RunsOutsideSteam"/>): development builds, and any build still on the Spacewar
/// placeholder. The id comes from <see cref="MultiplayerProjectSettings.SteamAppId"/>, the one place it is set.
/// A release build under the real id ships without the file — Steam launches it, and a leftover file would stop
/// SteamAPI.RestartAppIfNecessary from sending a direct exe launch back through Steam.
/// </summary>
public sealed class CopySteamAppIdPostprocessor : IPostprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPostprocessBuild(BuildReport report)
    {
        if (report.summary.result != BuildResult.Succeeded)
            return;

        switch (report.summary.platform)
        {
            case BuildTarget.StandaloneWindows64:
            case BuildTarget.StandaloneLinux64:
            case BuildTarget.StandaloneOSX:
                break;
            default:
                return;
        }

        string exePath = report.summary.outputPath;
        if (string.IsNullOrEmpty(exePath))
            return;

        string buildFolder = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(buildFolder))
            return;

        string dst = Path.Combine(buildFolder, "steam_appid.txt");
        uint appId = SteamworksBootstrap.ConfiguredAppId;
        bool development = (report.summary.options & BuildOptions.Development) != 0;

        if (!development && appId != SteamworksBootstrap.SpacewarAppId)
        {
            // Building over an earlier development build would otherwise leave its file behind.
            if (File.Exists(dst))
                File.Delete(dst);
            Debug.Log($"[Steam] Release build for app {appId}: no steam_appid.txt shipped (Steam launches the game).");
            return;
        }

        File.WriteAllText(dst, appId.ToString());
        Debug.Log($"[Steam] Wrote steam_appid.txt ({appId}) to {dst}");
    }
}
