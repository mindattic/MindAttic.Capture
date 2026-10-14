using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MindAttic.Capture.Unity.Editor
{
    /// <summary>MindAttic ▸ Capture: the editor equivalent of -capture video / -capture screen N for Play Mode.</summary>
    [InitializeOnLoad]
    internal static class CaptureMenu
    {
        private const string Root = "MindAttic/Capture/";
        private const string VideoPref = "MindAttic.Capture.PlayModeVideo";
        private const string ScreenPref = "MindAttic.Capture.PlayModeScreenSeconds";
        private const int ScreenSeconds = 5;

        static CaptureMenu()
        {
            // Statics are reset by the play-mode domain reload; an environment variable survives it.
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                    Environment.SetEnvironmentVariable(CaptureUnity.EditorModesVariable, Modes());
            };
        }

        private static string Modes()
        {
            var words = new List<string>();
            if (EditorPrefs.GetBool(VideoPref, false)) words.Add("video");
            var seconds = EditorPrefs.GetInt(ScreenPref, 0);
            if (seconds > 0)
            {
                words.Add("screen");
                words.Add(seconds.ToString());
            }
            return string.Join(" ", words);
        }

        [MenuItem(Root + "Record Video In Play Mode", priority = 1)]
        private static void ToggleVideo() => EditorPrefs.SetBool(VideoPref, !EditorPrefs.GetBool(VideoPref, false));

        [MenuItem(Root + "Record Video In Play Mode", true)]
        private static bool ToggleVideoValidate()
        {
            Menu.SetChecked(Root + "Record Video In Play Mode", EditorPrefs.GetBool(VideoPref, false));
            return !EditorApplication.isPlaying;
        }

        [MenuItem(Root + "Screenshot Every 5s In Play Mode", priority = 2)]
        private static void ToggleScreens() => EditorPrefs.SetInt(ScreenPref, EditorPrefs.GetInt(ScreenPref, 0) > 0 ? 0 : ScreenSeconds);

        [MenuItem(Root + "Screenshot Every 5s In Play Mode", true)]
        private static bool ToggleScreensValidate()
        {
            Menu.SetChecked(Root + "Screenshot Every 5s In Play Mode", EditorPrefs.GetInt(ScreenPref, 0) > 0);
            return !EditorApplication.isPlaying;
        }

        [MenuItem(Root + "Take Screenshot _F12", priority = 20)]
        private static void TakeScreenshot() => CaptureUnity.TakeScreenshot("editor");

        [MenuItem(Root + "Take Screenshot _F12", true)]
        private static bool TakeScreenshotValidate() => EditorApplication.isPlaying && CaptureUnity.Engine.Enabled;

        [MenuItem(Root + "Open Capture Folder", priority = 40)]
        private static void OpenFolder()
        {
            var engine = CaptureUnity.Engine;
            var folder = engine.Enabled && Directory.Exists(engine.SessionFolder) ? engine.SessionFolder : CaptureRoot();
            if (Directory.Exists(folder)) EditorUtility.RevealInFinder(folder);
            else Debug.LogWarning($"[MindAttic.Capture] {folder} does not exist yet.");
        }

        private static string CaptureRoot()
        {
            var options = new CaptureOptions();
            var root = Directory.Exists(Path.GetPathRoot(options.OutputRoot) ?? "") ? options.OutputRoot : options.FallbackRoot;
            var project = Path.GetDirectoryName(Application.dataPath);
            var app = Path.Combine(root, CaptureNaming.RepoName(project) ?? Path.GetFileName(project));
            return Directory.Exists(app) ? app : root;
        }
    }
}
