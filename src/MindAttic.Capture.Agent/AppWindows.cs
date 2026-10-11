using System.Diagnostics;
using System.Runtime.InteropServices;
using MindAttic.Capture.Windows;

namespace MindAttic.Capture.Agent;

/// <summary>Finds the window a launched app shows, including windows owned by its child processes.</summary>
internal static class AppWindows
{
    public static async Task<nint> WaitForMainWindowAsync(Process process, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (process.HasExited)
                throw new CaptureException(CaptureError.TargetNotFound, $"The app exited (code {process.ExitCode}) before showing a window.");
            process.Refresh();
            var tree = ProcessTree(process.Id);
            var window = Desktop.GetWindows()
                .Where(w => tree.Contains(w.ProcessId) && w.Bounds.Width >= 100 && w.Bounds.Height >= 100)
                .OrderByDescending(w => w.Hwnd == process.MainWindowHandle)
                .ThenByDescending(w => w.Bounds.Area)
                .FirstOrDefault();
            // Give the window a moment to reach its real size before sizing the tape from it.
            if (window is not null && sw.Elapsed > TimeSpan.FromSeconds(1.5)) return window.Hwnd;
            await Task.Delay(250);
        }
        throw new CaptureException(CaptureError.TargetNotFound, $"No window appeared within {timeout.TotalSeconds:0}s.");
    }

    /// <summary>Polls until <paramref name="target"/> resolves to a window.</summary>
    public static async Task<nint> WaitForTargetAsync(CaptureTarget target, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var match = target switch
                {
                    WindowTitleTarget t => Desktop.GetWindows().FirstOrDefault(w => w.Title.Contains(t.TitleContains, StringComparison.OrdinalIgnoreCase)),
                    ProcessTarget { ProcessName: { } name } => Desktop.GetWindows().FirstOrDefault(w =>
                        string.Equals(w.ProcessName, name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name, StringComparison.OrdinalIgnoreCase)),
                    _ => null,
                };
                if (match is not null && sw.Elapsed > TimeSpan.FromSeconds(1.5)) return match.Hwnd;
            }
            catch (CaptureException) when (sw.Elapsed < timeout)
            {
            }
            if (sw.Elapsed > timeout) throw new CaptureException(CaptureError.TargetNotFound, $"No window matching {target.Label} appeared within {timeout.TotalSeconds:0}s.");
            await Task.Delay(250);
        }
    }

    /// <summary>Completes when the window is destroyed.</summary>
    public static async Task WaitForCloseAsync(nint hwnd)
    {
        while (IsWindow(hwnd)) await Task.Delay(500);
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);

    /// <summary>The process and all of its descendants.</summary>
    internal static HashSet<int> ProcessTree(int root)
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == -1) return [root];
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (var ok = Process32First(snapshot, ref entry); ok; ok = Process32Next(snapshot, ref entry))
                parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
        }
        finally
        {
            CloseHandle(snapshot);
        }
        var tree = new HashSet<int> { root };
        bool grew;
        do
        {
            grew = false;
            foreach (var (pid, parent) in parents)
                if (tree.Contains(parent) && tree.Add(pid)) grew = true;
        } while (grew);
        return tree;
    }

    private const uint TH32CS_SNAPPROCESS = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(nint snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(nint snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
