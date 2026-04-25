using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Copies steam_appid.txt from the project root next to the built executable.
/// SteamAPI.Init fails in standalone builds without this file; the Editor often works because the process cwd is the project folder.
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

        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string src = Path.Combine(projectRoot, "steam_appid.txt");
        string dst = Path.Combine(buildFolder, "steam_appid.txt");

        if (!File.Exists(src))
        {
            Debug.LogWarning(
                $"[Steam] steam_appid.txt not found at {src}. Steam will not initialize in this build unless you add it next to the executable.");
            return;
        }

        File.Copy(src, dst, overwrite: true);
        Debug.Log($"[Steam] Copied steam_appid.txt to {dst}");
    }
}
