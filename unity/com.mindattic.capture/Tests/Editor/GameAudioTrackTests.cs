using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MindAttic.Capture.Hosting;
using MindAttic.Capture.Unity;
using NUnit.Framework;

namespace MindAttic.Capture.Unity.Tests
{
    public class GameAudioTrackTests
    {
        [Test]
        public async Task Write_appends_samples_without_padding_when_there_is_no_gap()
        {
            var track = new CaptureRunner.GameAudioTrack(new AudioFormat(100, 1, "f32le"));
            using var sink = new MemoryStream();
            await track.StartAsync(sink, CancellationToken.None);

            track.Write(new[] { 0.1f, 0.2f, 0.3f }, 1);

            Assert.That(sink.Length, Is.EqualTo(3 * 4));
        }

        [Test]
        public void Write_ignores_a_channel_count_mismatch()
        {
            // Never started (no sink): a mismatched channel count must bail out before touching it.
            var track = new CaptureRunner.GameAudioTrack(new AudioFormat(100, 2, "f32le"));
            Assert.DoesNotThrow(() => track.Write(new[] { 1f, 2f }, 1));
        }

        [Test]
        public async Task Write_pads_silence_for_gaps_longer_than_100ms_to_stay_in_sync()
        {
            var track = new CaptureRunner.GameAudioTrack(new AudioFormat(100, 1, "f32le"));
            using var sink = new MemoryStream();
            await track.StartAsync(sink, CancellationToken.None);
            await Task.Delay(300);

            track.Write(new[] { 1f }, 1);

            var bytes = sink.ToArray();
            Assert.That(bytes.Length, Is.GreaterThan(4), "a 300ms gap should have been padded with silence");
            for (var i = 0; i < bytes.Length - 4; i++) Assert.That(bytes[i], Is.EqualTo(0), $"byte {i} should be silence");
        }

        [Test]
        public async Task StopAsync_pads_trailing_silence_for_a_gap_before_stopping()
        {
            var track = new CaptureRunner.GameAudioTrack(new AudioFormat(100, 1, "f32le"));
            using var sink = new MemoryStream();
            await track.StartAsync(sink, CancellationToken.None);
            track.Write(new[] { 1f }, 1);
            await Task.Delay(300);

            await track.StopAsync();

            Assert.That(sink.Length, Is.GreaterThan(4), "the trailing gap before Stop should have been padded");
        }
    }
}
