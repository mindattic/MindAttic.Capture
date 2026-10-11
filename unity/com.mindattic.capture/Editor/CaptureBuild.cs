using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MindAttic.Capture.Unity.Editor
{
    /// <summary>
    /// Capture-enabled Windows players (MINDATTIC_CAPTURE define) alongside the project's normal production builds,
    /// plus the per-project folder stamp every Windows build gets.
    /// </summary>
    public sealed class CaptureBuild : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        /// <summary>
        /// Builds the enabled scenes to Builds/Capture/{project}.exe with capture compiled in.
        /// Batch: -executeMethod MindAttic.Capture.Unity.Editor.CaptureBuild.BuildWindows
        /// </summary>
        [MenuItem("MindAttic/Capture/Build Windows Player (Capture Enabled)", priority = 60)]
        public static void BuildWindows()
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError("[MindAttic.Capture] No enabled scenes in Build Settings.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }
            var path = Path.Combine("Builds", "Capture", ProjectFolderName() + ".exe");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = path,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
                extraScriptingDefines = new[] { "MINDATTIC_CAPTURE" },
            });
            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"[MindAttic.Capture] Capture build failed: {report.summary.result}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }
            Debug.Log($"[MindAttic.Capture] Built {Path.GetFullPath(path)}. Run it with -capture video or -capture screen 5.");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.StandaloneWindows64 && report.summary.platform != BuildTarget.StandaloneWindows) return;
            var exe = report.summary.outputPath;
            var streaming = Path.Combine(Path.GetDirectoryName(exe) ?? ".", Path.GetFileNameWithoutExtension(exe) + "_Data", "StreamingAssets");
            Directory.CreateDirectory(streaming);
            var project = Path.GetDirectoryName(Application.dataPath);
            File.WriteAllText(Path.Combine(streaming, CaptureUnity.ProjectNameFile), CaptureNaming.RepoName(project) ?? ProjectFolderName());
        }

        internal static string ProjectFolderName() => Path.GetFileName(Path.GetDirectoryName(Application.dataPath));
    }
}
