using MindAttic.Capture.Ffmpeg;
using MindAttic.Capture.Hosting;
using NUnit.Framework;

namespace MindAttic.Capture.Tests;

public class EncoderProbeTests
{
    private readonly ICaptureLog _log = new TraceCaptureLog();

    [OneTimeSetUp]
    public void FindFfmpeg()
    {
        if (FfmpegLocator.TryFind("ffmpeg") is null) Assert.Ignore("ffmpeg is not installed; EncoderProbe tests need it.");
    }

    [Test]
    public async Task FirstWorkingAsync_returns_the_first_candidate_ffmpeg_accepts()
    {
        var ffmpeg = FfmpegLocator.Find("ffmpeg");
        var encoder = await EncoderProbe.FirstWorkingAsync(ffmpeg, ["libx264", "a-candidate-that-must-never-be-tried"], _log);
        Assert.That(encoder, Is.EqualTo("libx264"));
    }

    [Test]
    public async Task FirstWorkingAsync_falls_back_to_the_last_candidate_when_all_fail()
    {
        var ffmpeg = FfmpegLocator.Find("ffmpeg");
        var encoder = await EncoderProbe.FirstWorkingAsync(ffmpeg, ["bogus-encoder-one", "bogus-encoder-two", "bogus-encoder-three"], _log);
        Assert.That(encoder, Is.EqualTo("bogus-encoder-three"));
    }

    [Test]
    public void ProbeAsync_caches_per_ffmpeg_path_case_insensitively()
    {
        // A nonexistent path is enough: caching is about the ConcurrentDictionary key lookup, not about what
        // probing finds, and it keeps this test independent of whether ffmpeg is actually installed.
        var path = $@"Z:\fake-ffmpeg-{Guid.NewGuid():N}.exe";
        var first = EncoderProbe.ProbeAsync(path, _log);
        var second = EncoderProbe.ProbeAsync(path.ToUpperInvariant(), _log);
        Assert.That(ReferenceEquals(first, second), Is.True);
    }

    [Test]
    public void ProbeAsync_does_not_share_the_cache_across_different_paths()
    {
        var pathA = $@"Z:\fake-ffmpeg-{Guid.NewGuid():N}.exe";
        var pathB = $@"Z:\fake-ffmpeg-{Guid.NewGuid():N}.exe";
        var taskA = EncoderProbe.ProbeAsync(pathA, _log);
        var taskB = EncoderProbe.ProbeAsync(pathB, _log);
        Assert.That(ReferenceEquals(taskA, taskB), Is.False);
    }
}
