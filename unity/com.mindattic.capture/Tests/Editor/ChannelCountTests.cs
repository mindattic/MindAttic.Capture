using MindAttic.Capture.Unity;
using NUnit.Framework;
using UnityEngine;

namespace MindAttic.Capture.Unity.Tests
{
    public class ChannelCountTests
    {
        [TestCase(AudioSpeakerMode.Mono, 1)]
        [TestCase(AudioSpeakerMode.Stereo, 2)]
        [TestCase(AudioSpeakerMode.Quad, 4)]
        [TestCase(AudioSpeakerMode.Surround, 5)]
        [TestCase(AudioSpeakerMode.Mode5point1, 6)]
        [TestCase(AudioSpeakerMode.Mode7point1, 8)]
        public void Maps_speaker_mode_to_channel_count(AudioSpeakerMode mode, int expected) =>
            Assert.That(CaptureRunner.ChannelCount(mode), Is.EqualTo(expected));
    }
}
