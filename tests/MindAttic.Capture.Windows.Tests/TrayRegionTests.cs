using MindAttic.Capture.Tray;
using NUnit.Framework;

namespace MindAttic.Capture.Windows.Tests;

public class TrayRegionTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);

    [Test]
    public void Dragging_in_any_direction_spans_the_same_rectangle()
    {
        var expected = new PixelRect(100, 50, 300, 200);
        Assert.That(RegionMath.FromCorners(100, 50, 400, 250), Is.EqualTo(expected));
        Assert.That(RegionMath.FromCorners(400, 250, 100, 50), Is.EqualTo(expected));
        Assert.That(RegionMath.FromCorners(400, 50, 100, 250), Is.EqualTo(expected));
    }

    [Test]
    public void Clamp_slides_a_rectangle_back_on_screen_and_shrinks_one_that_is_too_big()
    {
        Assert.That(RegionMath.Clamp(new PixelRect(1800, 1000, 300, 200), Screen), Is.EqualTo(new PixelRect(1620, 880, 300, 200)));
        Assert.That(RegionMath.Clamp(new PixelRect(-50, -10, 300, 200), Screen), Is.EqualTo(new PixelRect(0, 0, 300, 200)));
        Assert.That(RegionMath.Clamp(new PixelRect(10, 10, 4000, 3000), Screen), Is.EqualTo(Screen));
    }

    [Test]
    public void Clamp_respects_a_virtual_desktop_with_negative_coordinates()
    {
        var left = new PixelRect(-1920, 0, 1920, 1080);
        Assert.That(RegionMath.Clamp(new PixelRect(-2000, 100, 400, 300), left), Is.EqualTo(new PixelRect(-1920, 100, 400, 300)));
    }

    [Test]
    public void Even_rounds_sizes_down_for_the_encoder()
    {
        Assert.That(RegionMath.Even(new PixelRect(5, 7, 301, 199)), Is.EqualTo(new PixelRect(5, 7, 300, 198)));
        Assert.That(RegionMath.Even(new PixelRect(0, 0, 1, 1)), Is.EqualTo(new PixelRect(0, 0, 2, 2)));
    }
}
