using System.Diagnostics;
using MindAttic.Capture.Hosting;
using NUnit.Framework;

namespace MindAttic.Capture.Tests;

public class TextWriterCaptureLogTests
{
    [Test]
    public void Drops_messages_below_MinimumLevel()
    {
        var writer = new StringWriter();
        var log = new TextWriterCaptureLog(writer, CaptureLogLevel.Warning);

        log.Log(CaptureLogLevel.Debug, "debug message");
        log.Log(CaptureLogLevel.Info, "info message");
        log.Log(CaptureLogLevel.Warning, "warning message");

        var text = writer.ToString();
        Assert.That(text, Does.Not.Contain("debug message"));
        Assert.That(text, Does.Not.Contain("info message"));
        Assert.That(text, Does.Contain("warning message"));
    }

    [Test]
    public void Includes_exception_text_for_warnings_and_errors_but_not_info()
    {
        var writer = new StringWriter();
        var log = new TextWriterCaptureLog(writer, CaptureLogLevel.Debug);
        var ex = new InvalidOperationException("boom");

        log.Log(CaptureLogLevel.Info, "just info", ex);
        log.Log(CaptureLogLevel.Warning, "a warning", ex);
        log.Log(CaptureLogLevel.Error, "an error", ex);

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines.Count(l => l.Contains("boom")), Is.EqualTo(2));
    }
}

public class TraceCaptureLogTests
{
    private StringWriter _writer = new();
    private TextWriterTraceListener _listener = null!;

    [SetUp]
    public void Setup()
    {
        _writer = new StringWriter();
        _listener = new TextWriterTraceListener(_writer);
        Trace.Listeners.Add(_listener);
    }

    [TearDown]
    public void Cleanup()
    {
        Trace.Listeners.Remove(_listener);
        _listener.Dispose();
    }

    [Test]
    public void Drops_messages_below_MinimumLevel()
    {
        var log = new TraceCaptureLog { MinimumLevel = CaptureLogLevel.Warning };

        log.Log(CaptureLogLevel.Info, "info message");
        log.Log(CaptureLogLevel.Error, "error message");
        Trace.Flush();

        var text = _writer.ToString();
        Assert.That(text, Does.Not.Contain("info message"));
        Assert.That(text, Does.Contain("error message"));
    }

    [Test]
    public void Includes_exception_text()
    {
        var log = new TraceCaptureLog();
        log.Log(CaptureLogLevel.Error, "an error", new InvalidOperationException("kaboom"));
        Trace.Flush();

        Assert.That(_writer.ToString(), Does.Contain("kaboom"));
    }
}

public class SessionLogFileTests
{
    private sealed class RecordingLog : ICaptureLog
    {
        public readonly List<(CaptureLogLevel Level, string Message, Exception? Exception)> Entries = [];
        public void Log(CaptureLogLevel level, string message, Exception? exception = null) => Entries.Add((level, message, exception));
    }

    private string _dir = "";

    [SetUp]
    public void Setup() => _dir = Directory.CreateTempSubdirectory("capture-sessionlog-").FullName;

    [TearDown]
    public void Cleanup() => Directory.Delete(_dir, true);

    [Test]
    public void Every_entry_is_forwarded_to_the_inner_log_regardless_of_Open()
    {
        var inner = new RecordingLog();
        var log = new SessionLogFile(inner);

        log.Log(CaptureLogLevel.Debug, "before open");
        log.Open(Path.Combine(_dir, "session.log"));
        log.Log(CaptureLogLevel.Info, "after open");

        Assert.That(inner.Entries.Select(e => e.Message), Is.EqualTo(new[] { "before open", "after open" }));
    }

    [Test]
    public void Entries_logged_before_Open_are_buffered_then_flushed_to_the_file()
    {
        var log = new SessionLogFile(new RecordingLog());
        log.Log(CaptureLogLevel.Info, "buffered one");
        log.Log(CaptureLogLevel.Warning, "buffered two");

        var path = Path.Combine(_dir, "session.log");
        log.Open(path);

        var lines = File.ReadAllLines(path);
        Assert.That(lines, Has.Length.EqualTo(2));
        Assert.That(lines[0], Does.Contain("buffered one"));
        Assert.That(lines[1], Does.Contain("buffered two"));
    }

    [Test]
    public void Entries_below_Info_are_not_buffered_or_written_but_are_still_forwarded()
    {
        var inner = new RecordingLog();
        var log = new SessionLogFile(inner);
        log.Log(CaptureLogLevel.Debug, "debug noise");

        var path = Path.Combine(_dir, "session.log");
        log.Open(path);

        Assert.That(inner.Entries, Has.Count.EqualTo(1));
        Assert.That(File.ReadAllLines(path), Is.Empty);
    }

    [Test]
    public void Entries_after_Open_are_appended_directly()
    {
        var log = new SessionLogFile(new RecordingLog());
        var path = Path.Combine(_dir, "session.log");
        log.Open(path);

        log.Log(CaptureLogLevel.Info, "first");
        log.Log(CaptureLogLevel.Error, "second", new InvalidOperationException("why"));

        var lines = File.ReadAllLines(path);
        Assert.That(lines, Has.Length.EqualTo(2));
        Assert.That(lines[0], Does.Contain("first"));
        Assert.That(lines[1], Does.Contain("second"));
        Assert.That(lines[1], Does.Contain("why"));
    }
}
