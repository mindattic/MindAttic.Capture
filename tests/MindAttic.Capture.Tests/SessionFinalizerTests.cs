using System.Diagnostics;
using MindAttic.Capture.Ffmpeg;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Output;
using MindAttic.Capture.Tape;
using NUnit.Framework;

namespace MindAttic.Capture.Tests;

/// <summary>
/// Covers crash recovery: a tape left behind under .staging when the owning process never got to
/// finalize it (killed, crashed, machine lost power). These are the only files a user's footage
/// survives on after that, so the recovery rules have to be right.
/// </summary>
[NonParallelizable]
public class SessionFinalizerTests
{
    private string _root = "";
    private string? _ffmpeg;
    private readonly ICaptureJson _json = new SystemTextCaptureJson();
    private readonly ICaptureLog _log = new TraceCaptureLog();

    [OneTimeSetUp]
    public void FindFfmpeg()
    {
        _ffmpeg = FfmpegLocator.TryFind("ffmpeg");
        if (_ffmpeg is null) Assert.Ignore("ffmpeg is not installed; SessionFinalizer tests need it.");
    }

    [SetUp]
    public void Setup() => _root = Directory.CreateTempSubdirectory("capture-finalizer-").FullName;

    [TearDown]
    public void Cleanup()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private OutputManager NewOutput(bool sessionRecording = true) =>
        new(new CaptureOptions { OutputRoot = _root, AppName = "Test", FfmpegPath = _ffmpeg!, SessionRecording = sessionRecording }, "test", _json, _log);

    private TapeState NewState(OutputManager output, int ownerPid, long ownerStartTicks, bool sessionRecording) => new()
    {
        AppName = "Test", SessionId = output.SessionId, SessionDir = output.SessionDir, SessionStem = output.SessionStem,
        OutputStem = output.VideoStem, FfmpegPath = _ffmpeg!, CreatedAt = DateTimeOffset.Now, Fps = 30,
        Quality = TapeQuality.Lossless, Encoder = "ffv1", SessionRecording = sessionRecording, SessionOutputs = OutputFormats.Mkv,
        OwnerPid = ownerPid, OwnerStartTicks = ownerStartTicks,
    };

    /// <summary>Records a short real tape (via ffmpeg) under the given owner and leaves it stopped but un-exported, as if the app had just crashed.</summary>
    private async Task<string> RecordTapeAsync(OutputManager output, int ownerPid, long ownerStartTicks, bool sessionRecording)
    {
        var dir = Path.Combine(output.StagingDir, "tape1");
        var state = NewState(output, ownerPid, ownerStartTicks, sessionRecording);
        var source = new SyntheticSource(160, 90, 30, realTime: true);
        var tape = await CaptureTape.StartAsync(dir, state, source, [], _log, _json, default);
        await Task.Delay(1500); // let at least one 1-second segment finish
        await tape.StopAsync();
        return dir;
    }

    [Test]
    public async Task RecoverAsync_finalizes_orphaned_session_tapes_with_a_dead_owner()
    {
        var output = NewOutput(sessionRecording: true);
        var tapeDir = await RecordTapeAsync(output, ownerPid: -1, ownerStartTicks: 0, sessionRecording: true);

        var recovered = await SessionFinalizer.RecoverAsync(_root, _json, _log, default);

        Assert.That(recovered, Is.EqualTo(1));
        Assert.That(Directory.Exists(tapeDir), Is.False);
        Assert.That(File.Exists(output.VideoStem + ".mkv"), Is.True);
    }

    [Test]
    public async Task RecoverAsync_leaves_tapes_with_a_live_owner_alone()
    {
        var output = NewOutput(sessionRecording: true);
        using var me = Process.GetCurrentProcess();
        var tapeDir = await RecordTapeAsync(output, ownerPid: me.Id, ownerStartTicks: me.StartTime.ToUniversalTime().Ticks, sessionRecording: true);

        var recovered = await SessionFinalizer.RecoverAsync(_root, _json, _log, default);

        Assert.That(recovered, Is.EqualTo(0));
        Assert.That(Directory.Exists(tapeDir), Is.True);
        Assert.That(File.Exists(output.VideoStem + ".mkv"), Is.False);
    }

    [Test]
    public async Task RecoverAsync_discards_orphaned_scratch_tapes_without_exporting()
    {
        var output = NewOutput(sessionRecording: false);
        var tapeDir = await RecordTapeAsync(output, ownerPid: -1, ownerStartTicks: 0, sessionRecording: false);

        var recovered = await SessionFinalizer.RecoverAsync(_root, _json, _log, default);

        Assert.That(recovered, Is.EqualTo(0));
        Assert.That(Directory.Exists(tapeDir), Is.False);
        Assert.That(File.Exists(output.VideoStem + ".mkv"), Is.False);
    }

    [Test]
    public async Task RunAsync_on_an_empty_tape_yields_no_files_and_deletes_the_tape()
    {
        var output = NewOutput();
        var tapeDir = Path.Combine(output.StagingDir, "tape1");
        Directory.CreateDirectory(tapeDir);
        var state = NewState(output, ownerPid: -1, ownerStartTicks: 0, sessionRecording: true);
        File.WriteAllText(Path.Combine(tapeDir, "tape.json"), _json.Serialize(state));

        var files = await SessionFinalizer.RunAsync(tapeDir, _json, _log, default);

        Assert.That(files, Is.Empty);
        Assert.That(Directory.Exists(tapeDir), Is.False);
    }

    [Test]
    public void DeleteTape_removes_empty_parent_staging_folders()
    {
        var staging = Path.Combine(_root, ".staging");
        var sessionA = Path.Combine(staging, "sessionA");
        var tapeA = Path.Combine(sessionA, "tape1");
        Directory.CreateDirectory(tapeA);
        File.WriteAllText(Path.Combine(tapeA, "marker.txt"), "x");

        SessionFinalizer.DeleteTape(tapeA);

        Assert.That(Directory.Exists(tapeA), Is.False);
        Assert.That(Directory.Exists(sessionA), Is.False);
        Assert.That(Directory.Exists(staging), Is.False);
    }

    [Test]
    public void DeleteTape_keeps_a_session_folder_that_still_has_another_tape()
    {
        var staging = Path.Combine(_root, ".staging");
        var sessionB = Path.Combine(staging, "sessionB");
        var tape1 = Path.Combine(sessionB, "tape1");
        var tape2 = Path.Combine(sessionB, "tape2");
        Directory.CreateDirectory(tape1);
        Directory.CreateDirectory(tape2);

        SessionFinalizer.DeleteTape(tape1);

        Assert.That(Directory.Exists(tape1), Is.False);
        Assert.That(Directory.Exists(tape2), Is.True);
        Assert.That(Directory.Exists(sessionB), Is.True);
        Assert.That(Directory.Exists(staging), Is.True);
    }
}
