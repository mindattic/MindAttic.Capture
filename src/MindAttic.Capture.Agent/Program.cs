using System.Diagnostics;
using MindAttic.Capture;
using MindAttic.Capture.Agent;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Windows;

return await AgentProgram.MainAsync(args);

namespace MindAttic.Capture.Agent
{
    internal static class AgentProgram
    {
        public const string StopEventName = @"Local\MindAttic.Capture.Stop";
        public const string SaveEventName = @"Local\MindAttic.Capture.Save";

        public static async Task<int> MainAsync(string[] argv)
        {
            AgentArgs args;
            try
            {
                args = AgentArgs.Parse(argv);
            }
            catch (CaptureException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            var log = new TextWriterCaptureLog(Console.Error, args.Has("verbose") ? CaptureLogLevel.Debug : CaptureLogLevel.Info);
            try
            {
                return args.Command switch
                {
                    "monitors" => Monitors(),
                    "windows" => Windows(args),
                    "shot" or "screenshot" => await ShotAsync(args, log),
                    "record" => await RecordAsync(args, log),
                    "replay" => await ReplayAsync(args, log),
                    "run" => await RunAsync(args, log),
                    "serve" => await ServeAsync(args, log),
                    "raise" => await ApiClient.RaiseAsync(args),
                    "stop" => Signal(StopEventName, "stop"),
                    "save" => Signal(SaveEventName, "save"),
                    "finalize" => await FinalizeAsync(args, log),
                    "recover" => await RecoverAsync(args, log),
                    _ => Help(),
                };
            }
            catch (CaptureException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return (int)ex.Error;
            }
        }

        private static int Help()
        {
            Console.WriteLine("""
                MindAttic capture agent

                  capture monitors                         list monitors (index, bounds)
                  capture windows [filter]                 list visible windows
                  capture shot   <target> [--format png|jpg|webp|bmp|raw] [--tag T]
                  capture record <target> [--duration 30s] [--preset trailer|lossless|devlog|bugreport] [--outputs mp4,gif]
                  capture replay <target> [--length 30s]   keep a rolling buffer; Enter or `capture save` saves it
                  capture run    [--url URL|folder | -- app.exe args]   record an app (--url: in C:\Apps\MindAttic.Browser) until it exits
                  capture serve  <target> [--port 47800] [--allow-origin URL]   localhost API for browsers and scripts
                  capture raise  <event> [key=value ...]   send an event to a running `capture serve`
                  capture stop | save                      signal a running record/replay/run/serve
                  capture finalize <tapeDir>               export a stopped session tape
                  capture recover [root]                   export sessions left behind by crashed apps

                target:  --monitor N | --region x,y,w,h | --window "title" | --hwnd 0x1234 | --process name
                common:  --out R:\MindAttic\Capture  --app Name  --config capture.json  --fps 60  --hq|--lossless
                         --audio app|mic  --gdi  --no-wgc  --ffmpeg path  --verbose
                """);
            return 1;
        }

        private static int Monitors()
        {
            foreach (var m in Desktop.GetMonitors())
                Console.WriteLine($"{m.Index}  {m.Bounds}{(m.IsPrimary ? "  primary" : "")}  {m.DeviceName}");
            return 0;
        }

        private static int Windows(AgentArgs args)
        {
            var filter = args.Positional.FirstOrDefault();
            foreach (var w in Desktop.GetWindows())
            {
                if (filter is not null && w.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    w.ProcessName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Console.WriteLine($"0x{w.Hwnd:x8}  {w.ProcessName,-20} {w.Bounds,-22} {w.Title}");
            }
            return 0;
        }

        private static CaptureOptions Options(AgentArgs args, ICaptureLog log, string defaultApp)
        {
            var o = new CaptureOptions { AppName = defaultApp, AppVersion = null, Log = log };
            args.ApplyCommon(o);
            return o;
        }

        private static async Task<int> ShotAsync(AgentArgs args, ICaptureLog log)
        {
            var o = Options(args, log, "Desktop");
            await using var engine = await CaptureEngine.StartNewAsync(new DesktopCaptureHost(args.Target(), args.HostOptions()), o);
            var result = await engine.ScreenshotAsync(args.Get("tag") ?? args.Target().Label, new ScreenshotOptions { Format = args.ImageFormat() });
            return Report(result);
        }

        private static async Task<int> RecordAsync(AgentArgs args, ICaptureLog log)
        {
            var o = Options(args, log, "Desktop");
            await using var engine = await CaptureEngine.StartNewAsync(new DesktopCaptureHost(args.Target(), args.HostOptions()), o);
            var tag = args.Get("tag") ?? args.Target().Label;
            var recording = new RecordingOptions { Preset = args.Preset(), Outputs = args.Outputs(), SeparateAudioTracks = args.Has("separate-audio") };
            await engine.StartRecordingAsync(tag, recording);
            if (!engine.IsRecording) return (int)CaptureError.CaptureFailed;
            var duration = args.GetDuration("duration");
            Console.Error.WriteLine(duration is { } d ? $"Recording for {d.TotalSeconds:0.#}s..." : "Recording; press Enter, Ctrl+C or run `capture stop` to finish.");
            await WaitForStopAsync(duration, null);
            return Report(await engine.StopRecordingAsync());
        }

        private static async Task<int> ReplayAsync(AgentArgs args, ICaptureLog log)
        {
            var o = Options(args, log, "Desktop");
            o.ReplayArmed = true;
            o.ReplayLength = args.GetDuration("length") ?? TimeSpan.FromSeconds(30);
            await using var engine = await CaptureEngine.StartNewAsync(new DesktopCaptureHost(args.Target(), args.HostOptions()), o);
            Console.Error.WriteLine($"Replay buffer armed ({o.ReplayLength.TotalSeconds:0}s). Enter or `capture save` saves; Ctrl+C or `capture stop` exits.");
            var preset = args.Preset();
            await WaitForStopAsync(null, async () =>
            {
                Report(await engine.SaveReplayAsync(o.ReplayLength, TimeSpan.Zero, args.Get("tag") ?? "replay", new RecordingOptions { Preset = preset, Outputs = args.Outputs() }));
            });
            return 0;
        }

        /// <summary>Launches an app (or the deployed MindAttic Browser for --url) and records its window as a session until it exits.</summary>
        private static async Task<int> RunAsync(AgentArgs args, ICaptureLog log)
        {
            var url = args.Get("url");
            if (url is null && args.Passthrough.Count == 0)
                throw new CaptureException(CaptureError.InvalidArgument, "run needs --url URL or -- app.exe [args].");
            var (process, appName, profileDir) = url is not null ? LaunchBrowserApp(url) : LaunchExe(args.Passthrough);
            try
            {
                Console.Error.WriteLine($"Waiting for {appName}'s window...");
                // Launcher stubs (Store apps, Unity/Electron launchers) exit and hand off to another process:
                // --window / --process name the real window instead of following the launched process.
                var hwnd = args.Has("window") || args.Has("process")
                    ? await AppWindows.WaitForTargetAsync(args.Target(), TimeSpan.FromSeconds(60))
                    : await AppWindows.WaitForMainWindowAsync(process, TimeSpan.FromSeconds(60));
                var o = Options(args, log, appName);
                o.SessionRecording = true;
                await using var engine = await CaptureEngine.StartNewAsync(new DesktopCaptureHost(CaptureTarget.Window(hwnd), args.HostOptions()), o);
                GlobalHotkeys? hotkeys = !args.Has("no-hotkeys") && o.Hotkeys.Count > 0 ? new GlobalHotkeys(engine, o.Hotkeys, log) : null;
                using (hotkeys)
                {
                    Console.Error.WriteLine($"Recording {appName} until it exits (or `capture stop`). Session folder: {engine.SessionFolder}");
                    await WaitForStopAsync(null, null, closed: AppWindows.WaitForCloseAsync(hwnd));
                }
            }
            finally
            {
                process.Dispose();
                if (profileDir is not null) TryDeleteDir(profileDir);
            }
            return 0;
        }

        private static (Process Process, string AppName, string? ProfileDir) LaunchExe(IReadOnlyList<string> command)
        {
            var exe = command[0];
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe)) ?? Environment.CurrentDirectory };
            foreach (var a in command.Skip(1)) psi.ArgumentList.Add(a);
            try
            {
                return (Process.Start(psi)!, CaptureNaming.RepoName(psi.WorkingDirectory) ?? Path.GetFileNameWithoutExtension(exe), null);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new CaptureException(CaptureError.TargetNotFound, $"Could not start \"{exe}\": {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Opens <paramref name="url"/> (a URL or a local site folder) in the deployed MindAttic Browser
        /// (C:\Apps\MindAttic.Browser), which this agent then records. Its process lives exactly as long as its window.
        /// </summary>
        private static (Process Process, string AppName, string? ProfileDir) LaunchBrowserApp(string url)
        {
            var browser = BrowserLocator.Require();
            var psi = new ProcessStartInfo(browser) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(browser)! };
            psi.ArgumentList.Add(url);
            var name = Directory.Exists(url) ? Path.GetFileName(Path.GetFullPath(url).TrimEnd('\\', '/'))
                : Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? $"{uri.Host}{(uri.IsDefaultPort ? "" : "-" + uri.Port)}"
                : "Browser";
            try
            {
                return (Process.Start(psi)!, name, null);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new CaptureException(CaptureError.TargetNotFound, $"Could not start MindAttic Browser at {browser}: {ex.Message}", ex);
            }
        }

        private static async Task<int> ServeAsync(AgentArgs args, ICaptureLog log)
        {
            var o = Options(args, log, args.Get("app") ?? "Browser");
            if (o.Rules.Count == 0 && !o.ReplayArmed) o.ReplayArmed = true;
            await using var engine = await CaptureEngine.StartNewAsync(new DesktopCaptureHost(args.Target(), args.HostOptions()), o);
            await using var server = await ApiServer.StartAsync(engine, args.GetInt("port") ?? ApiServer.DefaultPort,
                args.Get("allow-origin")?.Split(',').Select(s => s.Trim()).ToList() ?? [], log);
            Console.Error.WriteLine($"Listening on {server.Url} (token in {ApiServer.InfoFile}). Ctrl+C or `capture stop` exits.");
            await WaitForStopAsync(null, null);
            return 0;
        }

        private static async Task<int> FinalizeAsync(AgentArgs args, ICaptureLog log)
        {
            var dir = args.Positional.FirstOrDefault() ?? throw new CaptureException(CaptureError.InvalidArgument, "finalize needs a tape directory.");
            var json = new SystemTextCaptureJson();
            var files = await SessionFinalizer.RunAsync(dir, json, log, CancellationToken.None);
            foreach (var f in files) Console.WriteLine(f);
            return 0;
        }

        private static async Task<int> RecoverAsync(AgentArgs args, ICaptureLog log)
        {
            var explicitRoot = args.Positional.FirstOrDefault() ?? args.Get("out");
            var defaults = new CaptureOptions();
            // Sessions are staged under the fallback root when the output drive was missing, so scan that too.
            var roots = explicitRoot is not null ? [explicitRoot] : new[] { defaults.OutputRoot, defaults.FallbackRoot };
            var n = 0;
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                n += await SessionFinalizer.RecoverAsync(root, new SystemTextCaptureJson(), log, CancellationToken.None);
            Console.Error.WriteLine($"Recovered {n} session(s).");
            return 0;
        }

        private static int Signal(string name, string what)
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var handle))
            {
                Console.Error.WriteLine($"No running capture agent to {what}.");
                return (int)CaptureError.TargetNotFound;
            }
            using (handle) handle.Set();
            return 0;
        }

        /// <summary>Blocks until Ctrl+C, Enter (when interactive), `capture stop`, the duration, or <paramref name="closed"/> completes.</summary>
        private static async Task WaitForStopAsync(TimeSpan? duration, Func<Task>? onSave, Task? closed = null)
        {
            using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName);
            using var save = new EventWaitHandle(false, EventResetMode.AutoReset, SaveEventName);
            stop.Reset();
            save.Reset();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                e.Cancel = true;
                done.TrySetResult();
            };
            Console.CancelKeyPress += onCancel;
            try
            {
                var waits = new List<Task> { done.Task, WaitHandleTask(stop) };
                if (duration is { } d) waits.Add(Task.Delay(d));
                if (closed is not null) waits.Add(closed);
                if (!Console.IsInputRedirected && closed is null && onSave is null)
                    waits.Add(Task.Run(() => Console.ReadLine()));
                var saveTask = WaitHandleTask(save);
                Task? enter = null;
                while (true)
                {
                    if (onSave is not null && !Console.IsInputRedirected && enter is null) enter = Task.Run(() => Console.ReadLine());
                    var all = new List<Task>(waits) { saveTask };
                    if (enter is not null) all.Add(enter);
                    var finished = await Task.WhenAny(all);
                    if (onSave is not null && (finished == saveTask || finished == enter))
                    {
                        if (finished == saveTask) saveTask = WaitHandleTask(save);
                        else enter = null;
                        await onSave();
                        continue;
                    }
                    return;
                }
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        private static Task WaitHandleTask(WaitHandle handle)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => tcs.TrySetResult(), null, Timeout.Infinite, true);
            _ = tcs.Task.ContinueWith(_ => registration.Unregister(null), TaskScheduler.Default);
            return tcs.Task;
        }

        private static int Report(CaptureResult result)
        {
            if (result.Succeeded)
            {
                foreach (var f in result.Files) Console.WriteLine(f);
                return 0;
            }
            Console.Error.WriteLine($"error: {result.Error}");
            return (int)CaptureError.CaptureFailed;
        }

        private static void TryDeleteDir(string dir)
        {
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(500);
                }
            }
        }
    }
}
