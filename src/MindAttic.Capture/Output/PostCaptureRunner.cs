using System.Diagnostics;
using System.Runtime.InteropServices;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Internal;

namespace MindAttic.Capture.Output;

/// <summary>
/// Runs developer-configured commands after captures. Commands only ever come from
/// <see cref="CaptureOptions.PostCapture"/> (config files the developer controls), never from events or the JS bridge.
/// </summary>
internal static class PostCaptureRunner
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

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
                    // cmd /s /c "<command>": cmd strips exactly the outer pair, so a command with its own quotes
                    // ("C:\Program Files\tool.exe" "{path}") reaches it intact. sh gets the command as one argument.
                    var psi = windows
                        ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + command + "\"")
                        : new ProcessStartInfo("/bin/sh");
                    if (!windows) psi.AddArguments(["-c", command]);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardError = true;
                    psi.RedirectStandardOutput = true;
                    using var p = Process.Start(psi)!;
                    var stderr = p.StandardError.ReadToEndAsync();
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    if (!await p.WaitForProcessExitAsync(CommandTimeout).ConfigureAwait(false))
                    {
                        log.Warn($"Post-capture command did not finish within {CommandTimeout.TotalMinutes:0} minute(s); killing it: {command}");
                        try
                        {
#if NET
                            p.Kill(entireProcessTree: true);
#else
                            p.Kill();
#endif
                        }
                        catch (InvalidOperationException) { }
                        await p.WaitForProcessExitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                    // A surviving child can hold stdout/stderr open after the process itself exits; don't hang on EOF.
                    await Task.WhenAll(stdout, stderr).CompletesWithin(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    if (p.HasExited && p.ExitCode != 0)
                        log.Warn($"Post-capture command exited with {p.ExitCode}: {command}{Environment.NewLine}{(stderr.IsCompletedSuccessfully ? stderr.Result : "")}");
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

    /// <summary>Fills the placeholders. An unquoted {path} is quoted, since output folders can contain spaces.</summary>
    internal static string Expand(string template, CaptureResult r, string session) => template
        .Replace("\"{path}\"", "\"" + (r.PrimaryFile ?? "") + "\"")
        .Replace("'{path}'", "'" + (r.PrimaryFile ?? "") + "'")
        .Replace("{path}", "\"" + (r.PrimaryFile ?? "") + "\"")
        .Replace("{tag}", OutputManager.SanitizeTag(r.Tag))
        .Replace("{kind}", r.Kind.ToString().ToLowerInvariant())
        .Replace("{session}", session);
}
