using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MindAttic.Capture.Hosting;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
#if MINDATTIC_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MindAttic.Capture.Unity
{
    /// <summary>
    /// The Unity capture host. Grabs the final game frame (UI included) at the end of each frame with
    /// ScreenCapture + AsyncGPUReadback, so it works in Built-in and URP without touching the render
    /// pipeline, and only the game is recorded (no window chrome, works when the window is covered).
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class CaptureRunner : MonoBehaviour, ICaptureHost
    {
        private const int MaxFramesInFlight = 3;

        private readonly ConcurrentQueue<(ScreenshotOptions Options, TaskCompletionSource<CapturedImage> Tcs)> _stills =
            new ConcurrentQueue<(ScreenshotOptions, TaskCompletionSource<CapturedImage>)>();
        private CaptureOptions _options;
        private readonly object _engineGate = new object();
        private ICapture _engine;
        private Task _startTask;
        // Main thread only: screenshots waiting on a GPU readback, failed at quit because the readback callback needs
        // the main thread that OnQuitting is blocking.
        private readonly List<TaskCompletionSource<CapturedImage>> _readbackStills = new List<TaskCompletionSource<CapturedImage>>();
        private RenderTexture _grab, _flip;
        private volatile IFrameSink _sink;
        private int _tapeWidth, _tapeHeight, _screenWidth, _screenHeight, _inFlight;
        private byte[] _resizeBuffer;
        private volatile bool _quitting;
        private int _sampleRate, _channels;
        private GameAudioTrack _audio;
        private float _nextListenerCheck;
        private readonly List<(string Command, HotkeyBinding Key)> _hotkeys = new List<(string, HotkeyBinding)>();

        public string Name => "unity";

        public HostCapabilities Capabilities => HostCapabilities.Supersample | HostCapabilities.AppAudio;

        internal void Begin(CaptureOptions options)
        {
            _options = options;
            _screenWidth = Screen.width;
            _screenHeight = Screen.height;
            _sampleRate = AudioSettings.outputSampleRate;
            _channels = ChannelCount(AudioSettings.speakerMode);
            foreach (var kv in options.Hotkeys)
            {
                if (HotkeyBinding.TryParse(kv.Key, out var key)) _hotkeys.Add((kv.Value, key));
                else options.Log.Log(CaptureLogLevel.Warning, $"Hotkey \"{kv.Key}\" is not a recognized key; ignored.");
            }
            Application.quitting += OnQuitting;
        }

        /// <summary>First frame: game code has had BeforeSceneLoad, AfterSceneLoad and Awake to adjust CaptureUnity.Options.</summary>
        private void Start()
        {
            _screenWidth = Screen.width;
            _screenHeight = Screen.height;
            StartCoroutine(EndOfFrameLoop());
            _startTask = StartEngineAsync();
        }

        private async Task StartEngineAsync()
        {
            try
            {
                if (!SystemInfo.supportsAsyncGPUReadback)
                    throw new CaptureException(CaptureError.CaptureFailed, $"AsyncGPUReadback is not supported on {SystemInfo.graphicsDeviceType}.");
                // ConfigureAwait(false): OnQuitting blocks the main thread waiting on this task, so it must not need it.
                var engine = await CaptureEngine.StartNewAsync(this, _options).ConfigureAwait(false);
                bool quitting;
                lock (_engineGate)
                {
                    quitting = _quitting;
                    if (!quitting) _engine = engine;
                }
                if (quitting)
                {
                    // The game quit while ffmpeg was starting: shut the engine down here so nothing is left running.
                    await engine.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                if (_options.SessionRecording)
                    UnityEngine.Debug.Log($"[MindAttic.Capture] Recording {_tapeWidth}x{_tapeHeight} to {engine.SessionFolder}");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[MindAttic.Capture] Could not start capture: " + ex.Message);
            }
        }

        // ------------------------------------------------------------ ICaptureHost

        public ValueTask<CapturedImage> CaptureStillAsync(ScreenshotOptions options, CancellationToken ct)
        {
            if (_quitting) return new ValueTask<CapturedImage>(Task.FromException<CapturedImage>(ShuttingDown()));
            var tcs = new TaskCompletionSource<CapturedImage>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (ct.CanBeCanceled) ct.Register(() => tcs.TrySetCanceled());
            _stills.Enqueue((options, tcs));
            // OnQuitting may have drained the queue between the check above and the enqueue.
            if (_quitting) tcs.TrySetException(ShuttingDown());
            return new ValueTask<CapturedImage>(tcs.Task);
        }

        public VideoSource CreateVideoSource(int fps)
        {
            _tapeWidth = _screenWidth & ~1;
            _tapeHeight = _screenHeight & ~1;
            return new GameVideoSource(this, _tapeWidth, _tapeHeight);
        }

        public IReadOnlyList<IAudioTrackCapture> CreateAudioTracks(AudioMode mode)
        {
            if (mode == AudioMode.None) return Array.Empty<IAudioTrackCapture>();
            _audio = new GameAudioTrack(new AudioFormat(_sampleRate, _channels, "f32le"));
            return new IAudioTrackCapture[] { _audio };
        }

        private sealed class GameVideoSource : PushVideoSource
        {
            private readonly CaptureRunner _runner;
            private readonly int _width, _height;

            public GameVideoSource(CaptureRunner runner, int width, int height)
            {
                _runner = runner;
                _width = width;
                _height = height;
            }

            public override int Width => _width;
            public override int Height => _height;
            public override bool RealTime => true;
            public override string Description => "unity-readback";

            public override Task StartAsync(IFrameSink sink, CancellationToken ct)
            {
                _runner._sink = sink;
                return Task.CompletedTask;
            }

            public override Task StopAsync()
            {
                _runner._sink = null;
                return Task.CompletedTask;
            }
        }

        // ------------------------------------------------------------ frame grabbing

        private IEnumerator EndOfFrameLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                if (_quitting) yield break;
                GrabFrame();
            }
        }

        private void Update()
        {
            _screenWidth = Screen.width;
            _screenHeight = Screen.height;
            if (_audio != null && Time.unscaledTime >= _nextListenerCheck)
            {
                _nextListenerCheck = Time.unscaledTime + 1f;
                _audio.AttachTo(FindAnyObjectByType<AudioListener>());
            }
            foreach (var (command, key) in _hotkeys)
                if (key.WasPressed()) _ = Capture.Current.ExecuteCommandAsync(command);
        }

        private void GrabFrame()
        {
            // A zero-size view (minimized window, collapsed Game view) has nothing to grab; leave screenshots queued.
            var w = Screen.width;
            var h = Screen.height;
            if (w <= 0 || h <= 0) return;

            var sink = _sink;
            var stills = new List<(ScreenshotOptions, TaskCompletionSource<CapturedImage>)>();
            while (_stills.TryDequeue(out var s))
            {
                if (s.Tcs.Task.IsCompleted) continue;
                if (s.Options.Supersample > 1) CaptureSupersampled(s.Options.Supersample, s.Tcs);
                else stills.Add(s);
            }
            var wantVideo = sink != null && _inFlight < MaxFramesInFlight;
            if (!wantVideo && stills.Count == 0) return;

            EnsureTargets(w, h);
            ScreenCapture.CaptureScreenshotIntoRenderTexture(_grab);
            // Readback rows start at the texture's first row. Where UVs start at the top (D3D, Vulkan, Metal) that is
            // already the top of the screen (verified on D3D12); OpenGL stores bottom-up, so flip there.
            var source = _grab;
            if (!SystemInfo.graphicsUVStartsAtTop)
            {
                Graphics.Blit(_grab, _flip, new Vector2(1, -1), new Vector2(0, 1));
                source = _flip;
            }
            _inFlight++;
            foreach (var s in stills) _readbackStills.Add(s.Item2);
            AsyncGPUReadback.Request(source, 0, TextureFormat.BGRA32, request => OnReadback(request, w, h, wantVideo ? sink : null, stills));
        }

        private void OnReadback(AsyncGPUReadbackRequest request, int w, int h, IFrameSink sink, List<(ScreenshotOptions Options, TaskCompletionSource<CapturedImage> Tcs)> stills)
        {
            _inFlight--;
            foreach (var s in stills) _readbackStills.Remove(s.Tcs);
            if (request.hasError)
            {
                foreach (var s in stills) s.Tcs.TrySetException(new CaptureException(CaptureError.CaptureFailed, "GPU readback failed."));
                return;
            }
            var data = request.GetData<byte>();
            if (stills.Count > 0)
            {
                var pixels = data.ToArray();
                var image = new CapturedImage(w, h, pixels);
                foreach (var s in stills) s.Tcs.TrySetResult(image);
            }
            if (sink != null && _sink == sink)
            {
                if (w == _tapeWidth && h == _tapeHeight)
                {
                    sink.WriteFrame(data.AsReadOnlySpan());
                }
                else
                {
                    // Resolution changed mid-run: keep the tape size, cropping or padding with black.
                    var size = _tapeWidth * _tapeHeight * 4;
                    if (_resizeBuffer == null || _resizeBuffer.Length != size) _resizeBuffer = new byte[size];
                    Array.Clear(_resizeBuffer, 0, size);
                    var src = data.AsReadOnlySpan();
                    var rows = Math.Min(h, _tapeHeight);
                    var rowBytes = Math.Min(w, _tapeWidth) * 4;
                    for (var y = 0; y < rows; y++)
                        src.Slice(y * w * 4, rowBytes).CopyTo(new Span<byte>(_resizeBuffer, y * _tapeWidth * 4, rowBytes));
                    sink.WriteFrame(_resizeBuffer);
                }
            }
        }

        private static void CaptureSupersampled(int factor, TaskCompletionSource<CapturedImage> tcs)
        {
            Texture2D tex = null;
            try
            {
                tex = ScreenCapture.CaptureScreenshotAsTexture(factor);
                var w = tex.width;
                var h = tex.height;
                var rgba = tex.GetPixels32();
                var bgra = new byte[w * h * 4];
                // Texture rows are bottom-up; CapturedImage is top-down BGRA.
                for (var y = 0; y < h; y++)
                {
                    var srcRow = (h - 1 - y) * w;
                    var dst = y * w * 4;
                    for (var x = 0; x < w; x++, dst += 4)
                    {
                        var c = rgba[srcRow + x];
                        bgra[dst] = c.b;
                        bgra[dst + 1] = c.g;
                        bgra[dst + 2] = c.r;
                        bgra[dst + 3] = 255;
                    }
                }
                tcs.TrySetResult(new CapturedImage(w, h, bgra));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                if (tex != null) Destroy(tex);
            }
        }

        private void EnsureTargets(int w, int h)
        {
            if (_grab != null && _grab.width == w && _grab.height == h) return;
            Release();
            _grab = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "MindAttic.Capture.Grab" };
            _flip = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "MindAttic.Capture.Flip" };
            _grab.Create();
            _flip.Create();
        }

        private void Release()
        {
            if (_grab != null) { _grab.Release(); Destroy(_grab); _grab = null; }
            if (_flip != null) { _flip.Release(); Destroy(_flip); _flip = null; }
        }

        // ------------------------------------------------------------ shutdown

        private void OnQuitting()
        {
            ICapture engine;
            lock (_engineGate)
            {
                if (_quitting) return;
                _quitting = true;
                engine = _engine;
                _engine = null;
            }
            _sink = null;
            while (_stills.TryDequeue(out var s)) s.Tcs.TrySetException(ShuttingDown());
            foreach (var tcs in _readbackStills) tcs.TrySetException(ShuttingDown());
            _readbackStills.Clear();
            // Still starting: StartEngineAsync sees _quitting and disposes the engine itself, so wait for that.
            var shutdown = engine != null ? Task.Run(() => engine.DisposeAsync().AsTask()) : _startTask;
            if (shutdown == null || shutdown.IsCompleted) return;
            var sw = Stopwatch.StartNew();
            try
            {
                // The engine never needs the main thread to finish; with capture.exe installed the session export
                // runs after exit, otherwise it runs here before the game closes.
                if (!shutdown.Wait(TimeSpan.FromMinutes(15)))
                    UnityEngine.Debug.LogWarning("[MindAttic.Capture] Capture did not finish shutting down within 15 minutes.");
            }
            catch (AggregateException ex)
            {
                UnityEngine.Debug.LogError("[MindAttic.Capture] Capture shutdown failed: " + ex.GetBaseException().Message);
            }
            _options.Log.Log(CaptureLogLevel.Info, $"Capture stopped in {sw.Elapsed.TotalSeconds:0.0}s.");
        }

        private static CaptureException ShuttingDown() =>
            new CaptureException(CaptureError.CaptureFailed, "The game is shutting down.");

        private void OnDestroy()
        {
            OnQuitting();
            Application.quitting -= OnQuitting;
            _audio?.AttachTo(null);
            Release();
            CaptureUnity.Detach(this);
        }

        private static int ChannelCount(AudioSpeakerMode mode)
        {
            switch (mode)
            {
                case AudioSpeakerMode.Mono: return 1;
                case AudioSpeakerMode.Quad: return 4;
                case AudioSpeakerMode.Surround: return 5;
                case AudioSpeakerMode.Mode5point1: return 6;
                case AudioSpeakerMode.Mode7point1: return 8;
                default: return 2;
            }
        }

        // ------------------------------------------------------------ audio

        /// <summary>The game's own mix, tapped at the AudioListener (no other apps' sound).</summary>
        private sealed class GameAudioTrack : IAudioTrackCapture
        {
            private readonly object _gate = new object();
            private readonly Stopwatch _clock = new Stopwatch();
            private Stream _sink;
            private long _written;
            private AudioTap _tap;
            private byte[] _bytes = new byte[0];

            public GameAudioTrack(AudioFormat format) => Format = format;

            public string Name => "game";
            public AudioFormat Format { get; }

            public Task StartAsync(Stream sink, CancellationToken ct)
            {
                lock (_gate)
                {
                    _sink = sink;
                    _clock.Start();
                }
                return Task.CompletedTask;
            }

            public Task StopAsync()
            {
                lock (_gate)
                {
                    if (_sink != null) PadTo(Expected());
                    _sink = null;
                }
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => default;

            /// <summary>Main thread: follow the active AudioListener across scene loads.</summary>
            public void AttachTo(AudioListener listener)
            {
                if (listener != null && _tap != null && _tap.gameObject == listener.gameObject) return;
                if (_tap != null) Destroy(_tap);
                _tap = null;
                if (listener == null) return;
                _tap = listener.gameObject.AddComponent<AudioTap>();
                _tap.hideFlags = HideFlags.HideAndDontSave;
                _tap.Track = this;
            }

            /// <summary>Audio thread.</summary>
            public void Write(float[] data, int channels)
            {
                if (channels != Format.Channels) return;
                lock (_gate)
                {
                    if (_sink == null) return;
                    var count = data.Length * 4;
                    try
                    {
                        PadTo(Expected() - count);
                        if (_bytes.Length < count) _bytes = new byte[count];
                        Buffer.BlockCopy(data, 0, _bytes, 0, count);
                        _sink.Write(_bytes, 0, count);
                        _written += count;
                    }
                    catch (IOException)
                    {
                        _sink = null;
                    }
                }
            }

            private long Expected()
            {
                var block = Format.Channels * 4;
                var bytes = (long)(_clock.Elapsed.TotalSeconds * Format.SampleRate) * block;
                return bytes;
            }

            /// <summary>Silence for gaps (no listener, paused audio) longer than 100 ms, so audio stays in sync with video.</summary>
            private void PadTo(long target)
            {
                var block = Format.Channels * 4;
                var gap = target - _written;
                if (gap <= Format.SampleRate * block / 10) return;
                gap -= gap % block;
                var zeros = new byte[Math.Min(gap, 1 << 16)];
                while (gap > 0)
                {
                    var n = (int)Math.Min(gap, zeros.Length);
                    _sink.Write(zeros, 0, n);
                    _written += n;
                    gap -= n;
                }
            }
        }

        private sealed class AudioTap : MonoBehaviour
        {
            internal GameAudioTrack Track;

            private void OnAudioFilterRead(float[] data, int channels) => Track?.Write(data, channels);
        }

        // ------------------------------------------------------------ hotkeys

        private readonly struct HotkeyBinding
        {
            private readonly bool _ctrl, _shift, _alt;
#if MINDATTIC_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            private readonly Key _key;
            private HotkeyBinding(Key key, bool ctrl, bool shift, bool alt) { _key = key; _ctrl = ctrl; _shift = shift; _alt = alt; }
#else
            private readonly KeyCode _key;
            private HotkeyBinding(KeyCode key, bool ctrl, bool shift, bool alt) { _key = key; _ctrl = ctrl; _shift = shift; _alt = alt; }
#endif

            public static bool TryParse(string text, out HotkeyBinding binding)
            {
                binding = default;
                var parts = text.Split('+');
                bool ctrl = false, shift = false, alt = false;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    switch (parts[i].Trim().ToLowerInvariant())
                    {
                        case "ctrl": case "control": ctrl = true; break;
                        case "shift": shift = true; break;
                        case "alt": alt = true; break;
                        default: return false;
                    }
                }
                var name = parts[parts.Length - 1].Trim();
                if (name.Length == 1 && char.IsDigit(name[0])) name = (
#if MINDATTIC_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                    "Digit"
#else
                    "Alpha"
#endif
                    ) + name;
                if (string.Equals(name, "PrintScreen", StringComparison.OrdinalIgnoreCase)) name =
#if MINDATTIC_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                    "PrintScreen";
#else
                    "Print";
#endif
#if MINDATTIC_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                if (!Enum.TryParse(name, true, out Key key) || key == Key.None) return false;
#else
                if (!Enum.TryParse(name, true, out KeyCode key) || key == KeyCode.None) return false;
#endif
                binding = new HotkeyBinding(key, ctrl, shift, alt);
                return true;
            }

            public bool WasPressed()
            {
#if MINDATTIC_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                var kb = Keyboard.current;
                if (kb == null || !kb[_key].wasPressedThisFrame) return false;
                return (!_ctrl || kb.ctrlKey.isPressed) && (!_shift || kb.shiftKey.isPressed) && (!_alt || kb.altKey.isPressed);
#elif ENABLE_LEGACY_INPUT_MANAGER
                if (!Input.GetKeyDown(_key)) return false;
                bool Held(KeyCode a, KeyCode b) => Input.GetKey(a) || Input.GetKey(b);
                return (!_ctrl || Held(KeyCode.LeftControl, KeyCode.RightControl))
                       && (!_shift || Held(KeyCode.LeftShift, KeyCode.RightShift))
                       && (!_alt || Held(KeyCode.LeftAlt, KeyCode.RightAlt));
#else
                return false;
#endif
            }
        }
    }
}
