using System.Diagnostics;

namespace MindAttic.Capture.Hosting;

/// <summary>Log severity.</summary>
public enum CaptureLogLevel
{
    /// <summary>Verbose detail (ffmpeg command lines, segment activity).</summary>
    Debug,
    /// <summary>Normal activity.</summary>
    Info,
    /// <summary>Something was skipped or degraded.</summary>
    Warning,
    /// <summary>A capture failed.</summary>
    Error,
}

/// <summary>Logging sink; hosts bridge it to ILogger, Unity's Debug.Log, etc.</summary>
public interface ICaptureLog
{
    /// <summary>Writes one entry.</summary>
    void Log(CaptureLogLevel level, string message, Exception? exception = null);
}

/// <summary>Writes to <see cref="Trace"/>; the default when nothing else is configured.</summary>
public sealed class TraceCaptureLog : ICaptureLog
{
    /// <summary>Entries below this level are dropped.</summary>
    public CaptureLogLevel MinimumLevel { get; set; } = CaptureLogLevel.Info;

    /// <inheritdoc/>
    public void Log(CaptureLogLevel level, string message, Exception? exception = null)
    {
        if (level < MinimumLevel) return;
        Trace.WriteLine($"[MindAttic.Capture] {level}: {message}{(exception is null ? "" : Environment.NewLine + exception)}");
    }
}

/// <summary>Writes to a <see cref="TextWriter"/> (the agent uses Console.Error).</summary>
public sealed class TextWriterCaptureLog(TextWriter writer, CaptureLogLevel minimumLevel = CaptureLogLevel.Info) : ICaptureLog
{
    private readonly object _gate = new();

    /// <inheritdoc/>
    public void Log(CaptureLogLevel level, string message, Exception? exception = null)
    {
        if (level < minimumLevel) return;
        lock (_gate)
        {
            writer.WriteLine($"{DateTime.Now:HH:mm:ss} {level.ToString().ToUpperInvariant(),-7} {message}");
            if (exception is not null && level >= CaptureLogLevel.Warning) writer.WriteLine(exception.Message);
        }
    }
}

internal static class LogExtensions
{
    public static void Debug(this ICaptureLog log, string m) => log.Log(CaptureLogLevel.Debug, m);
    public static void Info(this ICaptureLog log, string m) => log.Log(CaptureLogLevel.Info, m);
    public static void Warn(this ICaptureLog log, string m, Exception? ex = null) => log.Log(CaptureLogLevel.Warning, m, ex);
    public static void Error(this ICaptureLog log, string m, Exception? ex = null) => log.Log(CaptureLogLevel.Error, m, ex);
}
