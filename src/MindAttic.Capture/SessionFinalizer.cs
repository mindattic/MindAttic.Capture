using System.Diagnostics;
using MindAttic.Capture.Ffmpeg;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Internal;
using MindAttic.Capture.Output;
using MindAttic.Capture.Tape;

namespace MindAttic.Capture;

/// <summary>
/// Turns a stopped session tape into session.mkv/.mp4 in the session folder. Runs in-process, in a
/// detached <c>capture finalize</c> process, or from <c>capture recover</c> after a crash.
/// </summary>
public static class SessionFinalizer
{
    /// <summary>Finalizes one tape directory. Returns the files written (empty when there was nothing to do).</summary>
    public static async Task<IReadOnlyList<string>> RunAsync(string tapeDir, ICaptureJson json, ICaptureLog log, CancellationToken ct)
    {
        var statePath = Path.Combine(tapeDir, "tape.json");
        if (!File.Exists(statePath)) throw new CaptureException(CaptureError.InvalidArgument, $"\"{tapeDir}\" is not a capture tape (no tape.json).");

        FileStream lockFile;
        try
        {
            lockFile = new FileStream(Path.Combine(tapeDir, "finalize.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            log.Info($"{tapeDir} is already being finalized by another process.");
            return [];
        }
        using (lockFile)
        {
            var state = json.Deserialize<TapeState>(File.ReadAllText(statePath))
                        ?? throw new CaptureException(CaptureError.InvalidArgument, $"Unreadable {statePath}.");
            var segments = TapeSegments.Read(tapeDir);
            if (segments.Count == 0)
            {
                log.Warn($"Session tape {tapeDir} has no finished segments; nothing to export.");
                lockFile.Dispose();
                DeleteTape(tapeDir);
                return [];
            }
            var requestPath = Path.Combine(tapeDir, "finalize.json");
            var request = File.Exists(requestPath) ? json.Deserialize<FinalizeRequest>(File.ReadAllText(requestPath)) : null;
            var manifestPath = state.SessionStem + ".session.json";
            var manifest = File.Exists(manifestPath) ? json.Deserialize<SessionManifest>(File.ReadAllText(manifestPath)) : null;
            var end = Math.Min(request?.EndSeconds ?? double.MaxValue, segments[segments.Count - 1].End);
            var markers = request?.Markers ?? manifest?.Markers ?? [];
            var recovered = request is null;
            if (recovered) log.Warn($"Recovering session {state.SessionId} of {state.AppName} (the app did not shut down cleanly).");

            var exporter = new Exporter(state.FfmpegPath, () => EncoderProbe.ProbeAsync(state.FfmpegPath, log), log);
            var spec = new PresetSpec(state.SessionOutputs == OutputFormats.None ? OutputFormats.Mkv | OutputFormats.Mp4 : state.SessionOutputs, 16, null, null);
            var sw = Stopwatch.StartNew();
            var output = await exporter.ExportAsync(new ExportJob(tapeDir, state, segments, [(0, end)], state.OutputStem ?? state.SessionStem, spec, markers,
                request?.SeparateAudioTracks ?? false), ct).ConfigureAwait(false);

            var result = new CaptureResult
            {
                Kind = CaptureKind.Session, Tag = recovered ? "session-recovered" : "session", Files = output.Files,
                Timestamp = state.CreatedAt, Duration = TimeSpan.FromSeconds(output.DurationSeconds),
            };
            var sidecar = new CaptureSidecar
            {
                Kind = "session", Tag = result.Tag, App = state.AppName, AppVersion = state.AppVersion, Session = state.SessionId,
                Timestamp = state.CreatedAt, DurationSeconds = output.DurationSeconds, TapeStart = 0, TapeEnd = end,
                Width = state.Width, Height = state.Height, Preset = "Session", Encoder = state.Encoder,
                Files = output.Files.Select(Path.GetFileName).ToList()!, Markers = output.Markers.ToList(),
                Host = manifest?.Host ?? "",
            };
            File.WriteAllText(output.Files[0] + ".json", json.Serialize(sidecar));
            if (manifest is not null)
            {
                manifest.Captures.Add(new ManifestCapture
                {
                    Kind = "session", Tag = result.Tag, At = state.CreatedAt, DurationSeconds = output.DurationSeconds, Files = output.Files.ToList(),
                });
                manifest.Ended ??= DateTimeOffset.Now;
                File.WriteAllText(manifestPath, json.Serialize(manifest));
            }
            log.Info($"Session exported in {sw.Elapsed.TotalSeconds:0}s: {string.Join(", ", output.Files)}");
            lockFile.Dispose();
            DeleteTape(tapeDir);
            return output.Files;
        }
    }

    /// <summary>Finalizes every orphaned session tape under <paramref name="root"/> whose owning process is gone; discards orphaned scratch tapes.</summary>
    public static async Task<int> RecoverAsync(string root, ICaptureJson json, ICaptureLog log, CancellationToken ct)
    {
        var staging = Path.Combine(root, ".staging");
        if (!Directory.Exists(staging)) return 0;
        var recovered = 0;
        foreach (var statePath in Directory.EnumerateFiles(staging, "tape.json", SearchOption.AllDirectories).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var tapeDir = Path.GetDirectoryName(statePath)!;
            TapeState? state;
            try
            {
                state = json.Deserialize<TapeState>(File.ReadAllText(statePath));
            }
            catch (Exception ex)
            {
                log.Warn($"Skipping unreadable {statePath}.", ex);
                continue;
            }
            if (state is null || state.OwnerIsAlive()) continue;
            if (!state.SessionRecording)
            {
                DeleteTape(tapeDir);
                continue;
            }
            try
            {
                if ((await RunAsync(tapeDir, json, log, ct).ConfigureAwait(false)).Count > 0) recovered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad tape (corrupt finalize.json, I/O error) must not block recovery of the others.
                log.Error($"Could not recover {tapeDir}: {ex.Message}", ex is CaptureException ? null : ex);
            }
        }
        return recovered;
    }

    /// <summary>Deletes a tape folder, then its session staging folder and .staging when they become empty.</summary>
    public static void DeleteTape(string tapeDir)
    {
        try
        {
            if (Directory.Exists(tapeDir)) Directory.Delete(tapeDir, recursive: true);
            var parent = Path.GetDirectoryName(tapeDir);
            for (var i = 0; i < 2 && parent is not null; i++, parent = Path.GetDirectoryName(parent))
            {
                if (!Directory.Exists(parent) || Directory.EnumerateFileSystemEntries(parent).Any()) break;
                Directory.Delete(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Starts the agent detached from this process (not in the kill-on-exit job), so it outlives the app.</summary>
    internal static void SpawnDetached(string agent, IReadOnlyList<string> args, ICaptureLog log)
    {
        try
        {
            var psi = new ProcessStartInfo(agent) { UseShellExecute = false, CreateNoWindow = true };
            psi.AddArguments(args);
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            log.Error($"Could not start \"{agent}\" {string.Join(" ", args)}.", ex);
        }
    }
}
