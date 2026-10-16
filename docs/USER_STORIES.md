---
codex: 1
project: MindAttic.Capture
code: M.CAPT
layer: stories
status: living
updated: 2026-10-12
---
<!-- GENERATED from MindAttic.Workflow/data/workflow.db by `workflow db render`. Do not edit: this file is rewritten from the database; change stories and tickets with the workflow CLI. -->

# MindAttic.Capture — User Stories
> ✅ done (shipped & tested) · 🟡 partial · ⬜ planned. Every ✅ cites the test.

## Priority backlog
_Nothing recorded yet._

## Tickets
> Permanent log of every change and fix — this repo's ticket system. Rows are append-only: never
> delete or reword one; only move its Status (open → closed / wont-do) and fill in Ref. Status is
> a word, not a glyph, so tickets don't count toward the story index. Keep this section last.

| ID | Date | Type | Status | Summary | Ref |
|---|---|---|---|---|---|
| M.CAPT-T1 | 2026-10-12 | docs | closed | Start the permanent ticket log in USER_STORIES.md | — |
| M.CAPT-T2 | 2026-10-14 | chore | closed | NUnit tests for the agent HTTP API (src/MindAttic.Capture.Agent/ApiServer.cs): start ApiServer on a free port against a fake ICapture and cover bearer-token auth (missing/wrong token rejected, token from InfoFile accepted), CORS allowOrigin echo, each op (raise, marker, screenshot, record/start\|stop\|pause\|resume, clip, replay) reaching the fake, unknown op -> error status, oversized body rejected by ReadBodyAsync, NaN/out-of-range seconds clamped, /status shape, and the /ws WebSocket path; only IsAllowedOrigin is tested today and this is the remote-control surface | 9770dd8 tests/MindAttic.Capture.Windows.Tests/ApiServerTests.cs |
| M.CAPT-T3 | 2026-10-14 | chore | closed | NUnit tests for crash recovery (SessionFinalizer.RunAsync/RecoverAsync/DeleteTape and TapeState.OwnerIsAlive): orphaned session tapes whose owner PID/start-ticks are dead get finalized, live-owner tapes are left alone, orphaned scratch (non-session) tapes are discarded, empty tape dir yields no files, DeleteTape removes the tape then empty session staging and .staging folders but keeps non-empty ones; this is what saves footage after a crash and is untested | 0d8ae74 tests/MindAttic.Capture.Tests/SessionFinalizerTests.cs |
| M.CAPT-T4 | 2026-10-14 | chore | closed | NUnit tests for Exporter (src/MindAttic.Capture/Output/Exporter.cs) derived outputs: given a short synthetic tape, each OutputFormats flag (Mp4, WebM, Gif, WebP) produces its file, a .thumb.jpg thumbnail is written, a failing format (e.g. bad encoder) is logged and skipped without losing the master or other formats, multi-range jobs join parts, and ExportOutput.Markers/DurationSeconds match the job; engine tests only check default MKV/MP4 durations | 33fae22 tests/MindAttic.Capture.Tests/ExporterTests.cs |
| M.CAPT-T5 | 2026-10-14 | chore | closed | NUnit tests for OutputManager manifest and sidecars (src/MindAttic.Capture/Output/OutputManager.cs): WriteSidecar JSON content next to the primary file, AddEvent/AddMarker/AddCapture accumulate into SaveManifest output (round-trip via ICaptureJson), MarkersSnapshot returns a copy safe under concurrent AddMarker, and Sanitize handles app names with invalid path chars; only naming/root resolution is tested today | a0f1872 tests/MindAttic.Capture.Tests/OutputManagerManifestTests.cs |
| M.CAPT-T6 | 2026-10-14 | chore | closed | NUnit tests for process plumbing in Internal/Compat.cs and FfmpegProcess: QuoteWindowsArg/AddArguments round-trip through CommandLineToArgvW rules (spaces, embedded quotes, trailing backslashes, empty arg), CompletesWithin/WaitForProcessExitAsync true/false on timeout, FfmpegProcess.RunAsync throws CaptureException with stderr tail on non-zero exit and honors cancellation, StopAsync kills after timeout (zero timeout kills at once), and TryFind resolves absolute path / app dir / PATH / missing | 8717fc1 tests/MindAttic.Capture.Tests/ProcessPlumbingTests.cs |
| M.CAPT-T7 | 2026-10-14 | chore | closed | NUnit tests for EncoderProbe (src/MindAttic.Capture/Ffmpeg/EncoderProbe.cs): FirstWorkingAsync returns the first candidate ffmpeg accepts, falls back to the last candidate when all fail (use a fake encoder list with bogus names), and ProbeAsync caches per ffmpeg path case-insensitively; a wrong pick silently changes every recording's encoder | 34543ab tests/MindAttic.Capture.Tests/EncoderProbeTests.cs |
| M.CAPT-T8 | 2026-10-14 | chore | closed | NUnit tests for capture logging (Hosting/ICaptureLog.cs): TextWriterCaptureLog and TraceCaptureLog drop messages below MinimumLevel and include exception text, SessionLogFile forwards to the inner log and, after Open(path), also appends to the session log file (including messages logged before Open if buffered); session logs are the main post-mortem for failed captures | 883f9f0 tests/MindAttic.Capture.Tests/CaptureLogTests.cs |
| M.CAPT-T9 | 2026-10-14 | chore | closed | Unity Test Framework (NUnit) EditMode tests for the Unity package (unity/com.mindattic.capture/Runtime): add a Tests asmdef and cover HotkeyBinding.TryParse (modifiers, bad keys), GameAudioTrack.Write/PadTo silence padding to keep audio in sync with the stopwatch, ChannelCount per AudioSpeakerMode, and UnitySupport agent Find/IsAgent path resolution; the package has no tests at all | 54713cd unity/com.mindattic.capture/Tests/Editor |
| M.CAPT-T10 | 2026-10-15 | feature | open | Tray/taskbar launcher: attach Capture to a running process, auto-fill lossless + matching resolution, assignable Start/Stop Record hotkey | — |
| M.CAPT-T11 | 2026-10-16 | chore | closed | install.ps1 adds this repo's 15 .prose/commands stubs for the shared global commands, so any LLM client finds them | mindattic-agent-standard/install.ps1 run 2026-10-16 |
