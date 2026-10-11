using System.Runtime.InteropServices;
using MindAttic.Capture.Hosting;

namespace MindAttic.Capture.Windows;

/// <summary>
/// System-wide hotkeys (RegisterHotKey) mapped to capture commands, e.g. "F9" → "screenshot",
/// "Ctrl+Shift+R" → "toggle-recording". Keys already taken by another app are skipped with a warning.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_QUIT = 0x0012;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _threadId;

    /// <summary>Registers <paramref name="bindings"/> (key → command) and runs commands on <paramref name="capture"/>.</summary>
    public GlobalHotkeys(ICapture capture, IReadOnlyDictionary<string, string> bindings, ICaptureLog log)
    {
        var parsed = new List<(uint Mods, uint Vk, string Key, string Command)>();
        foreach (var kv in bindings)
        {
            if (TryParse(kv.Key, out var mods, out var vk)) parsed.Add((mods, vk, kv.Key, kv.Value));
            else log.Log(CaptureLogLevel.Warning, $"Hotkey \"{kv.Key}\" is not a recognized key; ignored.");
        }
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var ids = new Dictionary<int, string>();
            for (var i = 0; i < parsed.Count; i++)
            {
                if (RegisterHotKey(0, i + 1, parsed[i].Mods | MOD_NOREPEAT, parsed[i].Vk)) ids[i + 1] = parsed[i].Command;
                else log.Log(CaptureLogLevel.Warning, $"Hotkey {parsed[i].Key} is already in use by another app; ignored.");
            }
            _ready.TrySetResult(true);
            while (GetMessage(out var msg, 0, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY && ids.TryGetValue((int)msg.wParam, out var command))
                    _ = capture.ExecuteCommandAsync(command);
            }
            foreach (var id in ids.Keys) UnregisterHotKey(0, id);
        }) { IsBackground = true, Name = "MindAttic.Capture hotkeys" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Parses "F9", "Ctrl+Shift+S", "Alt+PrintScreen", "Win+F12".</summary>
    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        var parts = text.Split(['+'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
        if (parts.Count == 0) return false;
        foreach (var m in parts.Take(parts.Count - 1))
        {
            switch (m.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= MOD_CONTROL; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "win" or "windows": modifiers |= MOD_WIN; break;
                default: return false;
            }
        }
        var key = parts[parts.Count - 1].ToUpperInvariant();
        if (key.Length == 1 && (char.IsLetterOrDigit(key[0]))) { vk = key[0]; return true; }
        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24) { vk = (uint)(0x70 + f - 1); return true; }
        vk = key switch
        {
            "PRINTSCREEN" or "PRTSC" or "SNAPSHOT" => 0x2C,
            "PAUSE" => 0x13,
            "SCROLLLOCK" => 0x91,
            "INSERT" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "SPACE" => 0x20,
            _ => 0,
        };
        return vk != 0;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
