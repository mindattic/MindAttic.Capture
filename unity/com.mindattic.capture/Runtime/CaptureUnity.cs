using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace MindAttic.Capture.Unity
{
    /// <summary>
    /// MindAttic.Capture for Unity. Off unless you ask: no environment variable, no auto-loaded
    /// project config — only the literal <c>-capture</c> flag on the command line turns it on, in the
    /// Windows editor and in capture-enabled Windows players:
    /// <list type="bullet">
    /// <item><c>Game.exe -capture video</c>: film the game view from start to exit at native resolution.</item>
    /// <item><c>Game.exe -capture screen 5</c>: a PNG every 5 seconds until exit (both: <c>-capture video screen 5</c>).</item>
    /// <item>Editor: menu MindAttic ▸ Capture toggles the same modes for Play Mode.</item>
    /// <item>Game code: <c>CaptureUnity.Raise("critical-hit")</c>; rules loaded via an explicit <c>-capture-config path.json</c> turn events into screenshots, clips and replays.</item>
    /// <item>Production builds ignore all of it: capture runs in the editor, development builds, and builds made with the
    /// MINDATTIC_CAPTURE define (MindAttic ▸ Capture ▸ Build Windows Player (Capture Enabled)).</item>
    /// </list>
    /// Output: R:\MindAttic\Capture\{RepoName}\{RepoName}_{timestamp}.{ext}, e.g. UnitySolarSystem_2026-10-11T14.29.20.123.png (falls back to Videos\MindAttic\Capture). Needs ffmpeg on PATH for video.
    /// </summary>
    public static class CaptureUnity
    {
        /// <summary>Editor play-mode modes ("video screen 5"), set by the MindAttic ▸ Capture menu.</summary>
        public const string EditorModesVariable = "MINDATTIC_CAPTURE_EDITOR";

        /// <summary>Written into a built player's StreamingAssets by the package so recordings land in a per-project folder.</summary>
        public const string ProjectNameFile = "mindattic-capture-app.txt";

        private static CaptureRunner _runner;

        /// <summary>True in the editor, in development builds, and in builds with the MINDATTIC_CAPTURE scripting define.</summary>
        public static bool IsCaptureBuild =>
#if MINDATTIC_CAPTURE
            true;
#else
            Application.isEditor || Debug.isDebugBuild;
#endif

        /// <summary>The running engine (a no-op instance until capture starts, in production builds and on non-Windows platforms).</summary>
        public static ICapture Engine => Capture.Current;

        /// <summary>The parsed -capture flags (null when capture is off). Read them from your own startup code.</summary>
        public static CaptureArgs Args { get; private set; }

        /// <summary>
        /// Effective options, or null when capture is off. Game code may change them until the first frame (the engine
        /// starts in the first Start()), e.g. clear ScreenshotInterval to decide itself when to take screenshots.
        /// </summary>
        public static CaptureOptions Options { get; private set; }

        /// <summary>Raises a game event ("critical-hit", "battle-complete") for the capture.json rules. Cheap; never blocks.</summary>
        public static void Raise(string eventName, CaptureData data = null) => Capture.Current.Raise(eventName, data);

        /// <summary>Drops a marker (a chapter in exported video).</summary>
        public static void Marker(string name, CaptureData data = null) => Capture.Current.Marker(name, data);

        /// <summary>Takes a screenshot of the game view (fire and forget).</summary>
        public static void Screenshot(string tag = "screenshot") => _ = Capture.Current.ScreenshotAsync(tag);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            if (_runner != null) return;
            if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor) return;
            if (!IsCaptureBuild)
            {
                if (CaptureArgs.Parse(Environment.GetCommandLineArgs().Skip(1)).Any)
                    Debug.Log("[MindAttic.Capture] -capture is ignored in production builds; use MindAttic ▸ Capture ▸ Build Windows Player (Capture Enabled).");
                return;
            }

            CaptureOptions options;
            try
            {
                options = BuildOptions();
            }
            catch (CaptureException ex)
            {
                Debug.LogError("[MindAttic.Capture] Capture disabled: " + ex.Message);
                return;
            }
            Options = options;
            var go = new GameObject("MindAttic.Capture") { hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<CaptureRunner>();
            _runner.Begin(options);
        }

        private static CaptureOptions BuildOptions()
        {
            var options = new CaptureOptions
            {
                AppName = ProjectName(),
                AppVersion = Application.version,
                Json = new NewtonsoftCaptureJson(),
                Log = new UnityCaptureLog(),
            };
            CaptureArgs args;
            if (Application.isEditor)
            {
                var modes = Environment.GetEnvironmentVariable(EditorModesVariable);
                args = string.IsNullOrWhiteSpace(modes)
                    ? CaptureArgs.Parse(Array.Empty<string>())
                    : CaptureArgs.Parse(new[] { "-capture" }.Concat(modes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)));
            }
            else
            {
                args = CaptureArgs.Parse(Environment.GetCommandLineArgs().Skip(1));
            }
            if (args.Ignored != null)
                Debug.Log($"[MindAttic.Capture] \"-capture {args.Ignored}\" is not a capture mode (video, screen N); left for the game.");
            Args = args;
            options.Apply(args);
            options.DetachedFinalizerPath = UnityAgentLocator.Find();
            return options;
        }

        /// <summary>
        /// The repo the Unity project lives in (GridGame2026, UnityEve, ...), so projects sharing a productName still get
        /// separate capture folders: from the project path in the editor, from the stamped file in players.
        /// </summary>
        private static string ProjectName()
        {
            try
            {
                if (Application.isEditor)
                {
                    var project = Path.GetDirectoryName(Application.dataPath);
                    return CaptureNaming.RepoName(project) ?? Path.GetFileName(project);
                }
                var stamped = Path.Combine(Application.streamingAssetsPath, ProjectNameFile);
                if (File.Exists(stamped))
                {
                    var name = File.ReadAllText(stamped).Trim();
                    if (name.Length > 0) return name;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
            }
            return string.IsNullOrWhiteSpace(Application.productName) ? "UnityGame" : Application.productName;
        }

        internal static void Detach(CaptureRunner runner)
        {
            if (_runner == runner) _runner = null;
        }
    }
}
