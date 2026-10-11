using System;
using System.Diagnostics;
using System.IO;
using MindAttic.Capture.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace MindAttic.Capture.Unity
{
    /// <summary>Newtonsoft JSON for the core (System.Text.Json is not available to Unity's netstandard2.1 build).</summary>
    public sealed class NewtonsoftCaptureJson : ICaptureJson
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            Converters = { new StringEnumConverter() },
        };

        public string Serialize<T>(T value) => JsonConvert.SerializeObject(value, Settings);

        public T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);
    }

    /// <summary>Routes capture logging to the Unity console (warnings and errors always; info in the editor and development builds).</summary>
    public sealed class UnityCaptureLog : ICaptureLog
    {
        // Debug.isDebugBuild is main-thread-only, but Log() also runs from the background thread
        // CaptureRunner.OnQuitting dispatches DisposeAsync to; querying it live there throws and aborts
        // the shutdown export. Cache it here instead: UnityCaptureLog is always constructed on the main
        // thread (CaptureUnity.Boot runs via RuntimeInitializeOnLoadMethod).
        private readonly bool _isDebugBuild = UnityEngine.Debug.isDebugBuild;

        public void Log(CaptureLogLevel level, string message, Exception exception = null)
        {
            var text = "[MindAttic.Capture] " + message;
            switch (level)
            {
                case CaptureLogLevel.Error:
                    UnityEngine.Debug.LogError(exception == null ? text : text + "\n" + exception);
                    break;
                case CaptureLogLevel.Warning:
                    UnityEngine.Debug.LogWarning(text);
                    break;
                case CaptureLogLevel.Info:
                    if (_isDebugBuild) UnityEngine.Debug.Log(text);
                    break;
            }
        }
    }

    /// <summary>Finds capture.exe (installed by MindAttic.Capture/tools/install.ps1) so exports run after the game exits.</summary>
    internal static class UnityAgentLocator
    {
        private const string ProductName = "MindAttic.Capture.Agent";

        public static string Find()
        {
            var env = Environment.GetEnvironmentVariable("MINDATTIC_CAPTURE_AGENT");
            if (IsAgent(env)) return env;
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MindAttic", "Capture", "bin", "capture.exe");
            if (IsAgent(installed)) return installed;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (dir.Length == 0) continue;
                string candidate;
                try { candidate = Path.Combine(dir.Trim('"'), "capture.exe"); }
                catch (ArgumentException) { continue; }
                if (IsAgent(candidate)) return candidate;
            }
            return null;
        }

        private static bool IsAgent(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && File.Exists(path) && FileVersionInfo.GetVersionInfo(path).ProductName == ProductName;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
