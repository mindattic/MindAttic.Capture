using System.Diagnostics;
using System.Runtime.InteropServices;
using MindAttic.Capture.Hosting;

namespace MindAttic.Capture.Output;

/// <summary>
/// Runs developer-configured commands after captures. Commands only ever come from
/// <see cref="CaptureOptions.PostCapture"/> (config files the developer controls), never from events or the JS bridge.
/// </summary>
internal static class PostCaptureRunner
{
    public static void Run(IReadOnlyList<PostCaptureConfig> configs, CaptureResult result, string session, ICaptureLog log)
    {
        foreach (var c in configs)
        {
            if (string.IsNullOrWhiteSpace(c.Run) || !Matches(c.On, result)) continue;
            var command = Expand(c.Run, result, session);
            _ = Task.Run(async () =>
            {
                try
                {
                    var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                    var psi = new ProcessStartInfo(windows ? "cmd.exe" : "/bin/sh", windows ? "/d /c " + command : "-c \"" + command.Replace("\"", "\\\"") + "\"")
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                    };
                    using var p = Process.Start(psi)!;
                    var stderr = p.StandardError.ReadToEndAsync();
                    await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    p.WaitForExit();
                    if (p.ExitCode != 0) log.Warn($"Post-capture command exited with {p.ExitCode}: {command}{Environment.NewLine}{await stderr.ConfigureAwait(false)}");
                }
                catch (Exception ex)
                {
                    log.Warn($"Post-capture command failed to run: {command}", ex);
                }
            });
        }
    }

    internal static bool Matches(string on, CaptureResult r) =>
        string.IsNullOrWhiteSpace(on) || on == "*" ||
        string.Equals(on, r.Kind.ToString(), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(on, r.Tag, StringComparison.OrdinalIgnoreCase) ||
        (r.Trigger is not null && string.Equals(on, r.Trigger, StringComparison.OrdinalIgnoreCase));

    internal static string Expand(string template, CaptureResult r, string session) => template
        .Replace("{path}", r.PrimaryFile ?? "")
        .Replace("{tag}", OutputManager.SanitizeTag(r.Tag))
        .Replace("{kind}", r.Kind.ToString().ToLowerInvariant())
        .Replace("{session}", session);
}
