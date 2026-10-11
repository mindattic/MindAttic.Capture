using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Internal;

namespace MindAttic.Capture.Ffmpeg;

internal static class FfmpegLocator
{
    /// <summary>Resolves ffmpeg: an absolute path, next to the app, or on PATH.</summary>
    public static string Find(string configured) => TryFind(configured)
        ?? throw new CaptureException(CaptureError.FfmpegFailed,
            $"ffmpeg not found (\"{configured}\"). Install it (choco install ffmpeg), put ffmpeg.exe next to the app, or set FfmpegPath.");

    public static string? TryFind(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) configured = "ffmpeg";
        if (Path.IsPathRooted(configured)) return File.Exists(configured) ? ResolveShim(configured) : null;
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var name = isWindows && !configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? configured + ".exe" : configured;
        var local = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(local)) return ResolveShim(local);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split([Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(full)) return ResolveShim(full);
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }

    /// <summary>
    /// Maps a Chocolatey or Scoop shim to the real executable. A shim runs ffmpeg as its own child,
    /// so stopping or killing the shim would leave the real encoder running and holding our pipes.
    /// </summary>
    internal static string ResolveShim(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            var name = Path.GetFileName(path);
            var stem = Path.GetFileNameWithoutExtension(path);

            // Scoop: shims\ffmpeg.exe next to shims\ffmpeg.shim containing: path = "C:\...\ffmpeg.exe"
            var scoop = Path.Combine(dir, stem + ".shim");
            if (File.Exists(scoop))
            {
                foreach (var line in File.ReadAllLines(scoop))
                {
                    var eq = line.IndexOf('=');
                    if (eq < 0 || !line.Substring(0, eq).Trim().Equals("path", StringComparison.OrdinalIgnoreCase)) continue;
                    var target = line.Substring(eq + 1).Trim().Trim('"');
                    if (File.Exists(target)) return target;
                }
            }

            // Chocolatey: <choco>\bin\ffmpeg.exe is a shim for <choco>\lib\<package>\tools\...\ffmpeg.exe
            if (Path.GetFileName(dir).Equals("bin", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(Path.Combine(Path.GetDirectoryName(dir) ?? "", "lib"))
                && Path.GetFileName(Path.GetDirectoryName(dir) ?? "").IndexOf("chocolatey", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var lib = Path.Combine(Path.GetDirectoryName(dir)!, "lib");
                foreach (var package in Directory.EnumerateDirectories(lib, stem + "*").OrderBy(p => p.Length))
                {
                    var tools = Path.Combine(package, "tools");
                    if (!Directory.Exists(tools)) continue;
                    var real = Directory.EnumerateFiles(tools, name, SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault();
                    if (real is not null) return real;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Not resolvable; use the shim.
        }
        return path;
    }
}

/// <summary>An ffmpeg child process: captured stderr, graceful stop, killed with the app via a job object.</summary>
internal sealed class FfmpegProcess : IAsyncDisposable
{
    private const int TailLines = 40;
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _tail = new();
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _dataStdin;

    private FfmpegProcess(string exe, IReadOnlyList<string> args, bool redirectStdin, bool dataStdin)
    {
        _dataStdin = dataStdin;
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = redirectStdin,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            // No console: a Ctrl+C in the host console must not kill ffmpeg before the file is finalized.
            CreateNoWindow = true,
        };
        psi.AddArguments(args);
        Args = args;
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data is null) return;
            _tail.Enqueue(e.Data);
            while (_tail.Count > TailLines) _tail.TryDequeue(out _);
            if (e.Data.IndexOf("Press [q]", StringComparison.Ordinal) >= 0) _started.TrySetResult(true);
        };
        _process.OutputDataReceived += (_, _) => { };
        _process.Exited += (_, _) => _started.TrySetResult(false);
    }

    public IReadOnlyList<string> Args { get; }
    public string StderrTail => string.Join(Environment.NewLine, _tail);
    public bool HasExited => _process.HasExited;
    /// <summary>The ffmpeg process itself (for resource polling).</summary>
    public Process Process => _process;

    /// <summary>Raw-frame stdin (data mode only).</summary>
    public Stream DataStdin => _dataStdin ? _process.StandardInput.BaseStream : throw new InvalidOperationException("Not a data-pipe ffmpeg.");

    /// <summary>
    /// Starts a long-running ffmpeg. In control mode it waits until ffmpeg reports it is processing
    /// ("Press [q]"), so an input that cannot open fails here with ffmpeg's own message.
    /// </summary>
    public static async Task<FfmpegProcess> StartAsync(string exe, IReadOnlyList<string> args, bool dataStdin, ICaptureLog log, CancellationToken ct)
    {
        var p = new FfmpegProcess(exe, args, redirectStdin: true, dataStdin);
        p.Launch(log);
        if (dataStdin) return p;
        var started = p._started.Task;
        using (ct.Register(() => p._started.TrySetCanceled()))
        {
            bool ok;
            try
            {
                if (!await started.CompletesWithin(TimeSpan.FromSeconds(20)).ConfigureAwait(false))
                {
                    await p.DisposeAsync().ConfigureAwait(false);
                    throw new CaptureException(CaptureError.FfmpegFailed, $"ffmpeg did not start within 20s.{Environment.NewLine}{p.StderrTail}");
                }
                ok = await started.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await p.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            if (!ok || p._process.HasExited)
            {
                await p._process.WaitForExitCompatAsync().ConfigureAwait(false);
                var code = p._process.ExitCode;
                p._process.Dispose();
                throw new CaptureException(CaptureError.FfmpegFailed, $"ffmpeg exited with code {code} before capturing.{Environment.NewLine}{p.StderrTail}");
            }
        }
        return p;
    }

    private void Launch(ICaptureLog log)
    {
        log.Debug("ffmpeg " + string.Join(" ", Args.Select(Compat.QuoteWindowsArg)));
        try
        {
            _process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new CaptureException(CaptureError.FfmpegFailed, $"Could not start ffmpeg at \"{_process.StartInfo.FileName}\": {ex.Message}", ex);
        }
        ChildProcessJob.Assign(_process);
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();
    }

    /// <summary>
    /// Ends the capture so the container is finalized: 'q' in control mode, closing stdin in data mode.
    /// Kills ffmpeg (and anything it started) after <paramref name="timeout"/>; a zero timeout kills at once.
    /// Never waits longer than the timeout plus a few seconds.
    /// </summary>
    public async Task<int> StopAsync(TimeSpan timeout)
    {
        if (!_process.HasExited && timeout > TimeSpan.Zero)
        {
            var signal = Task.Run(async () =>
            {
                try
                {
                    if (_dataStdin)
                    {
                        _process.StandardInput.BaseStream.Flush();
                        _process.StandardInput.Close();
                    }
                    else
                    {
                        await _process.StandardInput.WriteAsync('q').ConfigureAwait(false);
                        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // Pipe already closed: ffmpeg is exiting on its own.
                }
            });
            // Closing a full pipe blocks until ffmpeg reads; the exit wait below bounds that too.
            await signal.CompletesWithin(timeout).ConfigureAwait(false);
            await _process.WaitForProcessExitAsync(timeout).ConfigureAwait(false);
        }
        if (!_process.HasExited) Kill();
        // After a kill, wait for the process itself, not for EOF on its output pipes: a grandchild can hold those open.
        if (!await _process.WaitForProcessExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)) return -1;
        return _process.ExitCode;
    }

    /// <summary>Kills ffmpeg and its whole process tree.</summary>
    private void Kill()
    {
        try
        {
            if (_process.HasExited) return;
#if NET
            _process.Kill(entireProcessTree: true);
#else
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) KillTreeWithTaskkill(_process.Id);
            if (!_process.HasExited) _process.Kill();
#endif
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

#if !NET
    private static void KillTreeWithTaskkill(int pid)
    {
        try
        {
            using var taskkill = Process.Start(new ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            taskkill?.WaitForExit(5000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
#endif

    /// <summary>Runs ffmpeg to completion, optionally feeding <paramref name="stdin"/>; non-zero exit throws with ffmpeg's error.</summary>
    public static async Task RunAsync(string exe, IReadOnlyList<string> args, ICaptureLog log, CancellationToken ct, byte[]? stdin = null)
    {
        var p = new FfmpegProcess(exe, args, redirectStdin: stdin is not null, dataStdin: stdin is not null);
        try
        {
            p.Launch(log);
            if (stdin is not null)
            {
                var input = p._process.StandardInput.BaseStream;
                await input.WriteAsync(stdin, 0, stdin.Length, ct).ConfigureAwait(false);
                input.Close();
            }
            try
            {
                await p._process.WaitForExitCompatAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                p.Kill();
                throw;
            }
            if (p._process.ExitCode != 0)
                throw new CaptureException(CaptureError.FfmpegFailed, $"ffmpeg exited with code {p._process.ExitCode}.{Environment.NewLine}{p.StderrTail}");
        }
        finally
        {
            p._process.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Kill();
        _process.Dispose();
        return default;
    }
}

/// <summary>
/// A Windows job object that kills ffmpeg children when this process dies, so a crashed app
/// never leaves a recorder running. Finished segments stay on disk for recovery.
/// </summary>
internal static class ChildProcessJob
{
    private static readonly Lazy<IntPtr> Job = new(Create);

    public static void Assign(Process process)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var job = Job.Value;
        if (job == IntPtr.Zero) return;
        try
        {
            AssignProcessToJobObject(job, process.Handle);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static IntPtr Create()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return IntPtr.Zero;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return job;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
