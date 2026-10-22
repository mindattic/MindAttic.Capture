using System.Globalization;
using MindAttic.Capture.Ffmpeg;

namespace MindAttic.Capture.Tape;

/// <summary>One finished tape segment; times are tape seconds.</summary>
internal sealed record TapeSegment(string File, double Start, double End);

/// <summary>Persisted next to the segments (tape.json) so a separate process can finalize or recover the session.</summary>
internal sealed class TapeState
{
    public int Version { get; set; } = 1;
    /// <summary>1 for the first tape of a session, 2 after an encoder restart...; markers remember which tape their time belongs to.</summary>
    public int Number { get; set; }
    public string AppName { get; set; } = "";
    public string? AppVersion { get; set; }
    public string SessionId { get; set; } = "";
    public string SessionDir { get; set; } = "";
    public string SessionStem { get; set; } = "";
    /// <summary>Where this tape's session export goes; null means <see cref="SessionStem"/>. Tapes after an encoder restart get "_part2", "_part3"...</summary>
    public string? OutputStem { get; set; }
    public string FfmpegPath { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public int Fps { get; set; }
    public TapeQuality Quality { get; set; }
    public string Encoder { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Source { get; set; } = "";
    public bool FixedTimestep { get; set; }
    public bool SessionRecording { get; set; }
    public OutputFormats SessionOutputs { get; set; }
    public List<TapeAudioTrack> Audio { get; set; } = [];
    /// <summary>Owning process; recovery leaves tapes alone while it is still running.</summary>
    public int OwnerPid { get; set; }
    public long OwnerStartTicks { get; set; }

    public bool OwnerIsAlive()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(OwnerPid);
            return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == OwnerStartTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>Written at shutdown: what the finalizer must export.</summary>
internal sealed class FinalizeRequest
{
    public double EndSeconds { get; set; }
    public List<MarkerRecord> Markers { get; set; } = [];
    public bool SeparateAudioTracks { get; set; }
}

/// <summary>A marker on the tape timeline.</summary>
internal sealed class MarkerRecord
{
    public string Name { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public double? TapeSeconds { get; set; }
    /// <summary>The tape (<see cref="TapeState.Number"/>) <see cref="TapeSeconds"/> is measured on; null when no tape ran.</summary>
    public int? Tape { get; set; }
    public Dictionary<string, object?>? Data { get; set; }
}

internal static class TapeSegments
{
    /// <summary>Parses ffmpeg's csv segment list ("file,start,end" per finished segment); partial lines are ignored.</summary>
    public static List<TapeSegment> Parse(string csv, string tapeDir)
    {
        var result = new List<TapeSegment>();
        foreach (var line in csv.Split('\n'))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length != 3) continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)) continue;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var end) || end <= start) continue;
            var file = parts[0].Trim('"');
            result.Add(new TapeSegment(Path.IsPathRooted(file) ? file : Path.Combine(tapeDir, file), start, end));
        }
        // segment_wrap is never used, but keep the latest entry if ffmpeg ever relists a file.
        return result.GroupBy(s => s.File, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).OrderBy(s => s.Start).ToList();
    }

    public static List<TapeSegment> Read(string tapeDir)
    {
        var path = Path.Combine(tapeDir, FfmpegArgs.SegmentList);
        if (!File.Exists(path)) return [];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return Parse(reader.ReadToEnd(), tapeDir);
    }

    /// <summary>
    /// Segments covering [a, b), with inpoint/outpoint relative to each segment. Segments already
    /// deleted by retention are skipped. Returns the pieces and the actual covered start.
    /// </summary>
    public static (List<(string File, double? InPoint, double? OutPoint)> Parts, double CoveredStart, double CoveredEnd)
        Select(IReadOnlyList<TapeSegment> segments, double a, double b)
    {
        var parts = new List<(string, double?, double?)>();
        double coveredStart = double.NaN, coveredEnd = double.NaN;
        foreach (var s in segments)
        {
            if (s.End <= a || s.Start >= b || !File.Exists(s.File)) continue;
            double? inPoint = a > s.Start ? a - s.Start : null;
            double? outPoint = b < s.End ? b - s.Start : null;
            if (double.IsNaN(coveredStart)) coveredStart = Math.Max(a, s.Start);
            coveredEnd = Math.Min(b, s.End);
            parts.Add((s.File, inPoint, outPoint));
        }
        return (parts, coveredStart, coveredEnd);
    }
}
