# MindAttic.Capture

Programmatic video capture and screenshots for MindAttic games and apps: trailer footage, store
screenshots, devlog clips and bug-repro video, triggered from code when things happen ("critical
hit", "battle complete") or recorded for a whole run with `-capture`.

```text
 your app / game                     MindAttic.Capture                               R:\MindAttic\Capture\
 ───────────────                     ─────────────────                               ─────────────────────
 Capture.Raise("critical-hit") ──>  trigger rules ──> replay / clip / screenshot ──>  {RepoName}\{RepoName}_{timestamp}.{ext}
 -capture video / screen N     ──>  session tape (1 s segments, lossless FFV1)  ──>    {RepoName}_{start}.mkv + .mp4
 ScreenshotAsync / ClipAsync   ──>  host adapter (desktop, WPF, WebView2, Unity)       *.png  *.mp4  *.json sidecars
                                                                                        session.json manifest
```

| Package | Target | What it does |
|---|---|---|
| `MindAttic.Capture` | netstandard2.1; net10.0 | Core: API, `-capture` parsing, trigger rules, rolling tape, exports, sidecars, session manifest. No third-party dependencies (Unity consumes the netstandard2.1 build). |
| `MindAttic.Capture.Windows` | net10.0-windows10.0.19041 | Desktop capture: Windows Graphics Capture for windows, Desktop Duplication / GDI for monitors and regions, WASAPI audio, global hotkeys, DI. |
| `MindAttic.Capture.Wpf` | net10.0-windows10.0.19041 | `CaptureWpf.Init(this, e.Args)`: records the main window at native size, export on exit. |
| `MindAttic.Capture.WebView2` | net10.0-windows10.0.19041 | `window.mindattic.capture` JavaScript bridge for any WebView2. |
| `MindAttic.Browser.exe` | net10.0-windows10.0.19041 | A WebView2 browser for viewing, navigating and capturing any website or local site folder. |
| `capture.exe` (agent) | net10.0-windows10.0.19041 | CLI and localhost API: record any app with no code changes, serve browser pages and scripts, finalize and recover sessions. |
| `com.mindattic.capture` (Unity) | Unity 6, Windows | Game-view capture via GPU readback, game audio, Play Mode menu; reads `-capture` from the player command line. |

Install the agent and browser once with `tools\install.ps1` (to `%LOCALAPPDATA%\MindAttic\Capture\bin`; add `-AddToPath` for `capture` everywhere). With the agent installed, apps hand their session export to it and exit immediately.

Video is encoded by **ffmpeg** (`choco install ffmpeg`, or put `ffmpeg.exe` next to the app). Hardware encoders (NVENC → QuickSync → AMF) are probed automatically, with a software fallback.

## How it works

Everything video comes from one **tape**: a recorder writing 1-second segments to
`{root}\.staging\`. A recording, clip, replay or session is just a time range cut from the
tape. Starting a recording is instant, and `SaveReplay(before, after)` can include footage from
before the event. The tape is lossless FFV1 (`TapeQuality.Lossless`) or hardware HEVC
(`HighQuality`). Exports produce a `.mkv` master plus, depending on the preset, an H.264 `.mp4`
that imports into any editor, WebM, GIF or animated WebP, and a thumbnail.

## The `-capture` flag

Every MindAttic GUI app wired to MindAttic.Capture understands the same flag:

```
Game.exe -capture video                 # film from start to exit at the app's native resolution
Game.exe -capture screen 5              # a PNG every 5 seconds until exit (also 500ms, 10s, 1m)
Game.exe -capture video screen 5        # both
Game.exe -demo -capture video hq        # hardware HEVC tape (~0.2 GB/min) instead of lossless
Game.exe -capture video 8h              # stop after 8 hours (the shut-off valve can stop it sooner)
Game.exe -capture                       # same as -capture video
Game.exe -capture video -capture-config capture.json
set MINDATTIC_CAPTURE=video screen 5    # same words via the environment; MINDATTIC_CAPTURE=0 is a kill switch
```

`-capture` followed by anything that isn't a capture word (for example an app's own `-capture <folder>`)
is left alone. `-demo` and other flags belong to the app: capture only records what runs.

When the app closes, the session goes to `R:\MindAttic\Capture\{RepoName}\{RepoName}_{session start}.mkv`
and `.mp4`, alongside every screenshot, clip and replay its events triggered. If `capture.exe` is
installed, the export runs in a detached process so the app exits immediately. If the app
crashes, a job object stops ffmpeg, the finished segments stay on disk, and the next launch (or
`capture recover`) exports them as `session-recovered`.

If `R:` is missing, output falls back to `%USERPROFILE%\Videos\MindAttic` with a warning.

### The shut-off valve

A long run can't fill the disk or exhaust memory. While capture runs, the engine polls the
machine every 5 s. The first limit crossed stops capture and exports everything recorded so far,
exactly as if the app had closed. The app keeps running, and nothing is deleted.

| Limit (`limits` in capture.json) | Default | Why |
|---|---|---|
| `minFreeDiskGB` | 50 | output (and staging) drive nearly full |
| `minFreeMemoryMB` | 2048 | the machine is running out of RAM |
| `maxEncoderMemoryMB` | 4096 | frames are backing up inside ffmpeg (the drive or CPU can't keep up) |
| `maxEncoderLagSeconds` | 60 | the encoder is that far behind real time |
| `maxDuration` | off | `-capture video 8h`, or `"maxDuration": "8h"` |

Set a numeric limit to 0 to turn it off, or `"enabled": false` to turn the whole valve off.
`pollEvery` changes the interval. Every poll is appended to `{RepoName}_{start}.stats.csv`. That
file records free disk, available RAM, CPU, app and encoder memory, encoder lag, tape time,
frames written and dropped, and the drive's write rate. The engine log goes to
`{RepoName}_{start}.log`, with a status line each minute. The session manifest records the
lowest and highest values seen, plus `stoppedBy` when the valve closed.

## Quick start

**WPF app** (one line in `App.OnStartup`, before showing the main window):

```csharp
CaptureWpf.Init(this, e.Args);   // -capture video / -capture screen N; capture.json beside the exe for rules
```

The app's project needs `net10.0-windows10.0.19041.0` (or later) and `MINDATTIC_CAPTURE` in
`DefineConstants` for `Capture.Raise` hooks. Automata is the worked example.

**Unity game:** add to `Packages/manifest.json`
`"com.mindattic.capture": "file:../../MindAttic.Capture/unity/com.mindattic.capture"`. Players
then honor `-capture video` / `-capture screen 5`; in the editor use **MindAttic ▸ Capture** to
record Play Mode. Game code raises events with `CaptureUnity.Raise("battle-complete")`; rules go in
`Assets/StreamingAssets/capture.json`.

**Websites:** `MindAttic.Browser.exe <url | site folder> -capture video`. A folder is served at
`https://<folder>.local/`, and its captures are named after the folder (`mindattic.com\mindattic.com_<timestamp>.mkv`)
or, for generic folders like `dist` or `wwwroot`, after the repository. A URL's captures are named after its host. Pages (in the browser, any WebView2 with the bridge, or plain browsers
with `capture serve`) call:

```js
mindattic.capture.raise('critical-hit', { damage: 912 });
await mindattic.capture.screenshot('victory');
await mindattic.capture.replay(15, 5, 'boss-death');
```

**Windows app (in-process, any UI framework):**

```csharp
await using var capture = await DesktopCapture.StartAsync(CaptureTarget.CurrentApp, args,
    o => { o.AppName = "GridGame"; o.ReplayArmed = true; });

// anywhere in game/app code (compiled out when MINDATTIC_CAPTURE is not defined):
Capture.Raise("critical-hit", new CaptureData { ["damage"] = 912 });
Capture.Marker("boss-phase-2");
Capture.Screenshot("victory");

// or the instance API:
var shot   = await capture.Engine.ScreenshotAsync("store-shot");
var clip   = await capture.Engine.ClipAsync(TimeSpan.FromSeconds(10), "devlog", new() { Preset = CapturePreset.Devlog });
var replay = await capture.Engine.SaveReplayAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5), "boss-death");
capture.Engine.Triggers.On("boss-death").Replay(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5)).Cooldown(TimeSpan.FromSeconds(30));
```

**Any app, no code changes (agent):**

```
capture run -- Game.exe -demo                  # record the app's window from launch to exit
capture run --window "Notepad" -- notepad.exe  # launcher stubs: name the real window
capture run --url http://localhost:5173        # record a web app in an Edge app window
capture shot --region 0,0,1920,1080 --format png
capture record --monitor 0 --duration 30s --preset devlog
capture replay --window "GridGame" --length 30s   # Enter or `capture save` saves the last 30 s
capture serve --window "Tutor"                    # localhost API for browser pages and scripts
capture raise critical-hit damage=900              # send an event to `capture serve`
capture recover R:\MindAttic\Capture             # export sessions left by crashed apps
```

Targets: `--monitor N`, `--region x,y,w,h`, `--window "title"`, `--hwnd 0x1234`, `--process name`.

## capture.json

Placed beside the exe (picked up automatically), or passed with `-capture-config` / `--config`:

```json
{
  "outputRoot": "R:\\Recording",
  "fileTemplate": "{app}_{timestamp}",
  "tapeQuality": "Lossless",
  "audio": "App",
  "replay": { "armed": true, "seconds": 30 },
  "rules": [
    { "on": "critical-hit", "action": "replay", "before": "5s", "after": "2s",
      "where": { "damage": ">=500" }, "debounce": "1s", "maxPerSession": 25 },
    { "on": "battle-complete", "action": "screenshot", "tag": "victory", "cooldown": "10s" },
    { "on": "boss-death", "action": "replay", "before": "15s", "after": "5s", "preset": "Trailer" },
    { "on": "battle-*", "action": "marker" }
  ],
  "hotkeys": { "F9": "screenshot", "F10": "toggle-recording", "F11": "save-replay" },
  "diskWarnGB": 500,
  "limits": { "minFreeDiskGB": 50, "minFreeMemoryMB": 2048, "maxEncoderMemoryMB": 4096, "maxEncoderLagSeconds": 60, "maxDuration": "8h" },
  "postCapture": [ { "on": "replay", "run": "pwsh -File notify.ps1 \"{path}\"" } ]
}
```

- **Actions:** `screenshot`, `replay`, `clip`, `marker`, `start-recording`, `stop-recording`, `toggle-recording`.
- **Rule controls:** `where`, `cooldown`, `debounce`, `maxPerSession`, `enabled`, `preset`, `format`.
- **Presets:**
  - `Trailer`: lossless master + high-quality MP4
  - `Lossless`
  - `Devlog`: MP4, 1080p max
  - `BugReport`: 720p30 MP4
  - `Session`
- **Disk usage:** nothing is ever deleted automatically. `diskWarnGB` only warns.

## Localhost API (`capture serve`)

- **Endpoint:** `http://127.0.0.1:47800/`.
- **Who can call it:**
  - Browser pages on `localhost`/`127.0.0.1` origins.
  - Other origins added with `--allow-origin`.
  - Non-browser clients that send the `X-Capture-Token` header from
    `%LOCALAPPDATA%\MindAttic\Capture\agent.json`.
- **Operations:** `POST /raise {event, data}`, `/marker`, `/screenshot`, `/clip {seconds}`,
  `/replay {before, after}`, `/record/start|stop|pause|resume`, `GET /status`.
- **WebSocket:** `/ws` accepts the same operations as `{op, ...}` messages.
- **Commands:** the API can never run commands; `postCapture` only comes from config files.

## Output

Everything goes to `R:\MindAttic\Capture\{RepoName}\`. Each file name starts with the project name, so a file
copied elsewhere still says what it shows, followed by ISO 8601 local time with `:` replaced by `.`
(Windows file names cannot contain `:`):

```
R:\MindAttic\Capture\UnitySolarSystem\
  UnitySolarSystem_2026-10-11T14.38.02.625.session.json   session manifest: app, version, events, markers, captures, diagnostics
  UnitySolarSystem_2026-10-11T14.38.02.625.mkv / .mp4 / .thumb.jpg / .mkv.json   the -capture video session (named by its start)
  UnitySolarSystem_2026-10-11T14.38.03.577.png / .png.json   a screenshot and its sidecar
  UnitySolarSystem_2026-10-11T14.39.12.004.mkv / .mp4        a replay, clip or recording
```

`tools\prefix-existing-captures.ps1` renames captures made before the prefix (and fixes the names inside their
manifests). It only renames, skips files still in use, and is safe to re-run.

`{RepoName}` is the git repository the app runs from (an app in `bin\Debug`, a Unity project, a local site
folder), so projects that share a product name (GridGame, GridGame2025, GridGame2026) still get separate
folders. Unity players carry it from the build. Outside a repo it falls back to the app name. If `R:` is
missing, output goes to `%USERPROFILE%\Videos\MindAttic\Capture` with a warning. `fileTemplate` can
change the names (tokens `{timestamp} {date} {time} {session} {tag} {index} {kind} {app}`).

Each sidecar records:
- kind, the tag (e.g. the tour stop or event name), app, build version and session
- host, timestamp and the trigger that fired
- tape range, with markers as offsets
- your custom key/values

Markers are also written into exported video as chapters.

## Production builds

Capture is a development tool and stays out of shipped builds:

- **.NET apps:** reference the packages only outside Release. Automata is the example:
  ```xml
  <ItemGroup Condition="'$(Configuration)' != 'Release'">
    <PackageReference Include="MindAttic.Capture.Wpf" Version="1.0.0" />
  </ItemGroup>
  ```
  The packages define `MINDATTIC_CAPTURE`, so wrap the init call and hooks in `#if MINDATTIC_CAPTURE`.
  Release builds then contain no capture code at all. The packages come from the workspace feed
  `C:\LocalNuGet` (refresh with `dotnet pack src\<project> -c Release -o C:\LocalNuGet`).
- **Unity:**
  - Capture runs in the editor, in development builds, and in players built with
    **MindAttic ▸ Capture ▸ Build Windows Player (Capture Enabled)** (adds the `MINDATTIC_CAPTURE`
    define, output `Builds/Capture/`).
  - Normal production players ignore `-capture` (one log line) and never start ffmpeg.
  - Unity projects reference the package by path
    (`file:../../MindAttic.Capture/unity/com.mindattic.capture`), which needs this repo checked out next
    to them, the same as any sibling-repo dependency here. Once the repo is pushed it can be switched to
    `https://github.com/mindattic/MindAttic.Capture.git?path=/unity/com.mindattic.capture#<tag>` if you
    prefer pinned versions.
  - After changing the core, run `tools\update-unity-plugin.ps1`.

## Limits worth knowing

- **Lossless is big.** FFV1 is roughly 1–7 GB per minute depending on resolution (a 5120x1440
  desktop measured about 7 GB/min). Use `-capture=hq` for long sessions.
- **Window capture size.** It uses the window's size when capture starts; later resizes are
  cropped or padded.
- **Windows 10 border.** Windows 10 shows a yellow capture border (Windows 11 doesn't).
- **H.264 width.** Hardware H.264 is limited to 4096 px wide; wider MP4s use x264.
- **Audio.** "App" audio is currently the whole-system mix (WASAPI loopback). Per-process audio
  capture is a later step.
- **ffmpeg licensing.** The common Windows builds are GPL. That's fine as a separate exe for
  internal use. Public builds that bundle ffmpeg need an LGPL build or GPL compliance.

## Development

```
dotnet build MindAttic.Capture.slnx
dotnet test  MindAttic.Capture.slnx      # engine and desktop tests need ffmpeg and an interactive desktop
```
