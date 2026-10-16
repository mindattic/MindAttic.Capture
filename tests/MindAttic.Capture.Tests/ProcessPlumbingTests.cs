using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MindAttic.Capture.Ffmpeg;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Internal;
using NUnit.Framework;

namespace MindAttic.Capture.Tests;

public class CompatTests
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string cmdLine, out int numArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Parses a command line the same way CommandLineToArgvW/MSVCRT do, so quoting can be checked against the real thing.</summary>
    private static string[] ParseCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        if (argv == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new string[argc];
            for (var i = 0; i < argc; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [TestCase("hello")]
    [TestCase("")]
    [TestCase("hello world")]
    [TestCase("say \"hi\"")]
    [TestCase(@"C:\Program Files\")]
    [TestCase(@"C:\Program Files\ffmpeg.exe")]
    [TestCase("trailing\\\\")]
    [TestCase("a\\\"b")]
    [TestCase("  leading and trailing spaces  ")]
    public void QuoteWindowsArg_round_trips_through_CommandLineToArgvW(string arg)
    {
        var quoted = Compat.QuoteWindowsArg(arg);
        var argv = ParseCommandLine("program.exe " + quoted);
        Assert.That(argv, Has.Length.EqualTo(2));
        Assert.That(argv[1], Is.EqualTo(arg));
    }

    [Test]
    public void AddArguments_populates_the_ArgumentList_unescaped()
    {
        var psi = new ProcessStartInfo();
        psi.AddArguments(["a", "b c", "", "\"quoted\""]);
        Assert.That(psi.ArgumentList, Is.EqualTo(new[] { "a", "b c", "", "\"quoted\"" }));
    }

    [Test]
    public async Task CompletesWithin_reports_true_when_the_task_finishes_in_time() =>
        Assert.That(await Task.Delay(10).CompletesWithin(TimeSpan.FromSeconds(5)), Is.True);

    [Test]
    public async Task CompletesWithin_reports_false_on_timeout()
    {
        var neverCompletes = new TaskCompletionSource();
        Assert.That(await neverCompletes.Task.CompletesWithin(TimeSpan.FromMilliseconds(50)), Is.False);
    }

    [Test]
    public async Task WaitForProcessExitAsync_returns_true_once_the_process_exits()
    {
        using var p = Process.Start(new ProcessStartInfo("ping", "-n 2 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        Assert.That(await p.WaitForProcessExitAsync(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(p.HasExited, Is.True);
    }

    [Test]
    public async Task WaitForProcessExitAsync_returns_false_while_the_process_keeps_running()
    {
        using var p = Process.Start(new ProcessStartInfo("ping", "-n 20 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            Assert.That(await p.WaitForProcessExitAsync(TimeSpan.FromMilliseconds(200)), Is.False);
        }
        finally
        {
            if (!p.HasExited) p.Kill();
        }
    }
}

[NonParallelizable]
public class FfmpegProcessTests
{
    private string? _ffmpeg;
    private readonly ICaptureLog _log = new TraceCaptureLog();

    [OneTimeSetUp]
    public void FindFfmpeg()
    {
        _ffmpeg = FfmpegLocator.TryFind("ffmpeg");
        if (_ffmpeg is null) Assert.Ignore("ffmpeg is not installed; FfmpegProcess tests need it.");
    }

    [Test]
    public void RunAsync_throws_a_CaptureException_with_the_stderr_tail_on_non_zero_exit()
    {
        var ex = Assert.ThrowsAsync<CaptureException>(() =>
            FfmpegProcess.RunAsync(_ffmpeg!, ["-v", "error", "-i", "definitely-does-not-exist.mkv", "-y", "out.mkv"], _log, default));
        Assert.That(ex!.Error, Is.EqualTo(CaptureError.FfmpegFailed));
        Assert.That(ex.Message, Does.Contain("exited with code"));
    }

    [Test]
    public void RunAsync_honors_cancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Assert.CatchAsync<OperationCanceledException>(() =>
            FfmpegProcess.RunAsync(_ffmpeg!, ["-v", "error", "-re", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30", "-t", "30", "-f", "null", "-"], _log, cts.Token));
    }

    [TestCase(0)]
    [TestCase(300)]
    public async Task StopAsync_ends_the_process_within_a_bounded_time(int timeoutMs)
    {
        var ffmpeg = await FfmpegProcess.StartAsync(_ffmpeg!,
            ["-hide_banner", "-nostats", "-loglevel", "info", "-re", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30", "-t", "30", "-f", "null", "-"],
            dataStdin: false, _log, default);
        try
        {
            var sw = Stopwatch.StartNew();
            await ffmpeg.StopAsync(TimeSpan.FromMilliseconds(timeoutMs));
            sw.Stop();
            Assert.That(ffmpeg.HasExited, Is.True);
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            await ffmpeg.DisposeAsync();
        }
    }
}
