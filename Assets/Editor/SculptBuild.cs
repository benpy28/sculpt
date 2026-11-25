using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Sculpt.EditorTools
{
    /// <summary>
    /// SCULPT > Build Windows: a standalone build of the Bootstrap scene in Builds/Sculpt (outside Assets, ignored by
    /// git), ready to zip and copy to a friend's PC for the LAN 1v1. "Dev" adds the dev keys (F1 respawn, F2 HUD,
    /// F6 showcase, F8-F12 network tools); the plain build is what a friend should get.
    /// Also callable from the command line: Unity -batchmode -quit -projectPath . -executeMethod Sculpt.EditorTools.SculptBuild.BuildRelease
    /// </summary>
    public static class SculptBuild
    {
        private const string Scene = "Assets/Scenes/Bootstrap.unity";

        [MenuItem("SCULPT/Build Windows (for a friend)")]
        public static void BuildRelease() => Build(false);

        [MenuItem("SCULPT/Build Windows (dev keys on)")]
        public static void BuildDev() => Build(true);

        private static void Build(bool dev)
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", dev ? "SculptDev" : "Sculpt"));
            Directory.CreateDirectory(dir);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = Path.Combine(dir, "Sculpt.exe"),
                target = BuildTarget.StandaloneWindows64,
                options = dev ? BuildOptions.Development : BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[SCULPT] Build done: {options.locationPathName} ({report.summary.totalSize / (1024 * 1024)} MB). Zip the whole folder for the other PC.");
                if (!Application.isBatchMode) EditorUtility.RevealInFinder(options.locationPathName);
            }
            else Debug.LogError($"[SCULPT] Build failed: {report.summary.result}, {report.summary.totalErrors} errors. See the console.");
        }
    }
}
