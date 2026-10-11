using System.Globalization;
using MindAttic.Capture.Hosting;

namespace MindAttic.Capture.Windows.Video;

/// <summary>Builds ffmpeg-native desktop inputs: Desktop Duplication (ddagrab) when possible, GDI (gdigrab) as fallback.</summary>
internal static class DesktopInputs
{
    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Candidates for <paramref name="rect"/>: ddagrab when one adapter-0 output fully contains it (offsets are
    /// relative to that output), then gdigrab in absolute virtual-desktop coordinates.
    /// </summary>
    public static List<NativeInput> Candidates(PixelRect rect, IReadOnlyList<PixelRect> ddaOutputs, int fps, bool drawMouse, bool forceGdi)
    {
        var list = new List<NativeInput>();
        if (!forceGdi)
        {
            for (var i = 0; i < ddaOutputs.Count; i++)
            {
                var o = ddaOutputs[i];
                if (!o.Contains(rect)) continue;
                list.Add(new NativeInput("ddagrab",
                [
                    "-f", "lavfi", "-i",
                    $"ddagrab=output_idx={I(i)}:framerate={I(fps)}:draw_mouse={(drawMouse ? 1 : 0)}" +
                    $":offset_x={I(rect.X - o.X)}:offset_y={I(rect.Y - o.Y)}:video_size={I(rect.Width)}x{I(rect.Height)}" +
                    ",hwdownload,format=bgra",
                ], IsLavfiGraph: true));
                break;
            }
        }
        list.Add(new NativeInput("gdigrab",
        [
            "-f", "gdigrab", "-framerate", I(fps), "-draw_mouse", drawMouse ? "1" : "0",
            "-offset_x", I(rect.X), "-offset_y", I(rect.Y),
            "-video_size", $"{I(rect.Width)}x{I(rect.Height)}", "-i", "desktop",
        ]));
        return list;
    }
}
