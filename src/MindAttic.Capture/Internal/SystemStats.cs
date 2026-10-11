using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MindAttic.Capture.Internal;

/// <summary>Machine-wide memory and CPU readings (Windows; other platforms report nothing).</summary>
internal sealed class SystemStats
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private ulong _idle, _kernel, _user;

    /// <summary>Physical memory available to new allocations, in MB (null when unknown).</summary>
    public static double? AvailableMemoryMB() => Memory() is { } m ? m.ullAvailPhys / 1048576.0 : null;

    /// <summary>Installed physical memory, in MB (null when unknown).</summary>
    public static double? TotalMemoryMB() => Memory() is { } m ? m.ullTotalPhys / 1048576.0 : null;

    /// <summary>Whole-machine CPU use since the previous call, 0-100 (null on the first call or when unknown).</summary>
    public double? CpuPercent()
    {
        if (!IsWindows || !GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return null;
        ulong idle = Ticks(idleFt), kernel = Ticks(kernelFt), user = Ticks(userFt);
        var first = _kernel == 0;
        var dIdle = idle - _idle;
        var dTotal = kernel - _kernel + (user - _user); // kernel time includes idle time
        (_idle, _kernel, _user) = (idle, kernel, user);
        if (first || dTotal == 0) return null;
        return Math.Max(0, Math.Min(100, 100.0 * (dTotal - dIdle) / dTotal));
    }

    /// <summary>Private (committed) memory of a process in MB, or null if it has exited.</summary>
    public static double? PrivateMemoryMB(Process? process)
    {
        if (process is null) return null;
        try
        {
            process.Refresh();
            return process.HasExited ? null : process.PrivateMemorySize64 / 1048576.0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Free space on the volume holding <paramref name="path"/>, in GB (null when unknown).</summary>
    public static double? FreeDiskGB(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace / 1073741824.0 : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static MEMORYSTATUSEX? Memory()
    {
        if (!IsWindows) return null;
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? m : null;
    }

    private static ulong Ticks(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
}
