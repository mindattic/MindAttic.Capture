using Vortice.DXGI;

namespace MindAttic.Capture.Windows.Video;

internal static class DxgiOutputs
{
    /// <summary>
    /// Desktop rects of adapter 0's outputs, indexed exactly as ffmpeg's ddagrab output_idx
    /// (ddagrab creates its D3D11 device on the default adapter). Monitors on other adapters
    /// are absent, which routes them to gdigrab.
    /// </summary>
    public static IReadOnlyList<PixelRect> GetAdapter0()
    {
        var result = new List<PixelRect>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (factory.EnumAdapters1(0, out var adapter).Failure) return result;
        using (adapter)
        {
            for (uint i = 0; adapter.EnumOutputs(i, out var output).Success; i++)
            {
                using (output)
                {
                    var r = output.Description.DesktopCoordinates;
                    result.Add(new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
                }
            }
        }
        return result;
    }
}
