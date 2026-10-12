using System.Diagnostics;
using System.Runtime.InteropServices;
using MindAttic.Capture.Hosting;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace MindAttic.Capture.Windows.Wgc;

/// <summary>
/// Windows Graphics Capture of one window: follows moves, works while covered, includes GPU content.
/// The latest frame is re-sent at a constant rate so the tape keeps advancing while the window is idle.
/// Frames are cropped or padded to the size the window had when capture started.
/// </summary>
public sealed class WgcWindowSource : PushVideoSource
{
    private readonly nint _hwnd;
    private readonly int _fps;
    private readonly bool _drawMouse;
    private readonly int _width, _height;
    private readonly byte[] _latest;
    private readonly object _frameGate = new();
    private readonly object _d3dGate = new();
    private ID3D11Device? _device;
    private ID3D11Texture2D? _staging;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private Thread? _pump;
    private volatile bool _running;
    private bool _haveFrame;

    /// <summary>Creates a source for <paramref name="hwnd"/>; the tape size is the window's current capture size.</summary>
    public WgcWindowSource(nint hwnd, int fps, bool drawMouse = true)
    {
        if (!IsSupported) throw new CaptureException(CaptureError.CaptureFailed, "Windows Graphics Capture is not available on this system.");
        _hwnd = hwnd;
        _fps = fps;
        _drawMouse = drawMouse;
        _item = CreateItemForWindow(hwnd);
        // Even sizes: HQ tapes encode yuv420p straight from these frames, which rejects odd widths and heights.
        _width = Math.Max(2, _item.Size.Width & ~1);
        _height = Math.Max(2, _item.Size.Height & ~1);
        _latest = new byte[_width * _height * 4];
    }

    /// <summary>True when WGC can capture windows here (Windows 10 2004+).</summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                return ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 10) && GraphicsCaptureSession.IsSupported();
            }
            catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidCastException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc/>
    public override int Width => _width;
    /// <inheritdoc/>
    public override int Height => _height;
    /// <inheritdoc/>
    public override bool RealTime => true;
    /// <inheritdoc/>
    public override string Description => $"wgc window 0x{_hwnd:x}";

    /// <inheritdoc/>
    public override Task StartAsync(IFrameSink sink, CancellationToken ct)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1], out _device).CheckError();
        var winrtDevice = CreateWinRtDevice(_device!);
        var item = _item!;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _pool.FrameArrived += OnFrameArrived;
        item.Closed += (_, _) => _running = false;
        _session = _pool.CreateCaptureSession(item);
        if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
            _session.IsCursorCaptureEnabled = _drawMouse;
        DisableBorder(_session);
        _running = true;
        _session.StartCapture();
        _pump = new Thread(() => Pump(sink)) { IsBackground = true, Name = "MindAttic.Capture WGC pump", Priority = ThreadPriority.AboveNormal };
        _pump.Start();
        return Task.CompletedTask;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // Held for the whole copy so StopAsync can't release the device or staging texture mid-Map.
        lock (_d3dGate)
        {
            if (!_running || _device is null) return;
            CopyFrame(sender);
        }
    }

    private void CopyFrame(Direct3D11CaptureFramePool sender)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null || _device is null) return;
        var size = frame.ContentSize;
        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;
        var texPtr = access.GetInterface(ref iid);
        using var texture = new ID3D11Texture2D(texPtr);
        var desc = texture.Description;
        if (_staging is null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
        {
            _staging?.Dispose();
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1, Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None, MiscFlags = ResourceOptionFlags.None,
            });
        }
        var ctx = _device.ImmediateContext;
        ctx.CopyResource(_staging, texture);
        var mapped = ctx.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var copyW = Math.Min(_width, Math.Min(size.Width, (int)desc.Width));
            var copyH = Math.Min(_height, Math.Min(size.Height, (int)desc.Height));
            lock (_frameGate)
            {
                if (copyW < _width || copyH < _height) Array.Clear(_latest, 0, _latest.Length);
                unsafe
                {
                    fixed (byte* dst = _latest)
                    {
                        for (var y = 0; y < copyH; y++)
                            Buffer.MemoryCopy((byte*)mapped.DataPointer + (long)y * mapped.RowPitch, dst + (long)y * _width * 4, _width * 4L, copyW * 4L);
                    }
                }
                _haveFrame = true;
            }
        }
        finally
        {
            ctx.Unmap(_staging, 0);
        }
        if (size.Width != (int)desc.Width || size.Height != (int)desc.Height)
            sender.Recreate(CreateWinRtDevice(_device), DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
    }

    private void Pump(IFrameSink sink)
    {
        TimeBeginPeriod(1);
        try
        {
            var frame = new byte[_latest.Length];
            var interval = Stopwatch.Frequency / (double)_fps;
            var clock = Stopwatch.StartNew();
            long n = 0;
            while (_running)
            {
                var due = (long)(n * interval);
                var wait = due - clock.ElapsedTicks;
                if (wait > 0)
                {
                    var ms = (int)(wait * 1000 / Stopwatch.Frequency);
                    if (ms > 1) Thread.Sleep(ms - 1);
                    else Thread.SpinWait(50);
                    continue;
                }
                n++;
                bool have;
                lock (_frameGate)
                {
                    have = _haveFrame;
                    if (have) Buffer.BlockCopy(_latest, 0, frame, 0, frame.Length);
                }
                if (have) sink.WriteFrame(frame);
                // Fell far behind (debugger, sleep): skip ahead instead of bursting.
                if (clock.ElapsedTicks - (long)(n * interval) > Stopwatch.Frequency) n = (long)(clock.ElapsedTicks / interval);
            }
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    /// <inheritdoc/>
    public override Task StopAsync()
    {
        _running = false;
        _pump?.Join(TimeSpan.FromSeconds(2));
        if (_pool is { } pool) pool.FrameArrived -= OnFrameArrived;
        _session?.Dispose();
        _pool?.Dispose();
        _session = null;
        _pool = null;
        lock (_d3dGate)
        {
            _staging?.Dispose();
            _device?.Dispose();
            _staging = null;
            _device = null;
        }
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- interop

    /// <summary>IGraphicsCaptureSession3 (Windows 11): not in the 19041 projection, so set IsBorderRequired through the ABI.</summary>
    private static readonly Guid GraphicsCaptureSession3Iid = new("f2cdd966-22ae-5ea1-9596-3a289344c3be");

    /// <summary>Removes the yellow capture border on Windows 11; Windows 10 keeps it.</summary>
    private static unsafe void DisableBorder(GraphicsCaptureSession session)
    {
        try
        {
            var iid = GraphicsCaptureSession3Iid;
            if (((IWinRTObject)session).NativeObject.TryAs(iid, out var ptr) < 0 || ptr == 0) return;
            try
            {
                // Vtable: IUnknown (0-2), IInspectable (3-5), get_IsBorderRequired (6), put_IsBorderRequired (7).
                var vtable = *(void***)ptr;
                var put = (delegate* unmanaged[Stdcall]<nint, byte, int>)vtable[7];
                put(ptr, 0);
            }
            finally
            {
                Marshal.Release(ptr);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
        }
    }

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        nint ptr;
        try
        {
            ptr = interop.CreateForWindow(hwnd, ref iid);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            throw new CaptureException(CaptureError.TargetNotFound, $"Windows Graphics Capture cannot capture window 0x{hwnd:x}: {ex.Message}", ex);
        }
        try
        {
            return GraphicsCaptureItem.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    private static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    [ComImport, System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);
        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }

    [ComImport, System.Runtime.InteropServices.Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);
}
