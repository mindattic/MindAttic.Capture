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

    private static readonly PixelRect Sel = new(100, 100, 400, 300);

    [Test]
    public void The_upper_left_corner_is_the_move_grip_and_the_rest_resize()
    {
        Assert.That(RegionMath.HitTest(Sel, 103, 97, 8), Is.EqualTo(Grip.Move));
        Assert.That(RegionMath.HitTest(Sel, 500, 100, 8), Is.EqualTo(Grip.TopRight));
        Assert.That(RegionMath.HitTest(Sel, 500, 400, 8), Is.EqualTo(Grip.BottomRight));
        Assert.That(RegionMath.HitTest(Sel, 100, 400, 8), Is.EqualTo(Grip.BottomLeft));
        Assert.That(RegionMath.HitTest(Sel, 300, 102, 8), Is.EqualTo(Grip.Top));
        Assert.That(RegionMath.HitTest(Sel, 300, 398, 8), Is.EqualTo(Grip.Bottom));
        Assert.That(RegionMath.HitTest(Sel, 101, 250, 8), Is.EqualTo(Grip.Left));
        Assert.That(RegionMath.HitTest(Sel, 499, 250, 8), Is.EqualTo(Grip.Right));
        Assert.That(RegionMath.HitTest(Sel, 300, 250, 8), Is.EqualTo(Grip.None));
        Assert.That(RegionMath.HitTest(Sel, 800, 800, 8), Is.EqualTo(Grip.None));
    }

    [Test]
    public void Moving_keeps_the_size_and_stops_at_the_screen_edge()
    {
        Assert.That(RegionMath.Drag(Sel, Grip.Move, 50, -20, Screen), Is.EqualTo(new PixelRect(150, 80, 400, 300)));
        Assert.That(RegionMath.Drag(Sel, Grip.Move, -500, -500, Screen), Is.EqualTo(new PixelRect(0, 0, 400, 300)));
    }

    [Test]
    public void Resizing_moves_only_the_grabbed_edges()
    {
        Assert.That(RegionMath.Drag(Sel, Grip.Right, 100, 999, Screen), Is.EqualTo(new PixelRect(100, 100, 500, 300)));
        Assert.That(RegionMath.Drag(Sel, Grip.Top, 999, 50, Screen), Is.EqualTo(new PixelRect(100, 150, 400, 250)));
        Assert.That(RegionMath.Drag(Sel, Grip.BottomLeft, -50, 60, Screen), Is.EqualTo(new PixelRect(50, 100, 450, 360)));
        Assert.That(RegionMath.Drag(Sel, Grip.TopRight, 20, -30, Screen), Is.EqualTo(new PixelRect(100, 70, 420, 330)));
    }

    [Test]
    public void Resizing_never_inverts_the_rectangle_or_leaves_the_screen()
    {
        Assert.That(RegionMath.Drag(Sel, Grip.Left, 1000, 0, Screen), Is.EqualTo(new PixelRect(500 - RegionMath.MinSide, 100, RegionMath.MinSide, 300)));
        Assert.That(RegionMath.Drag(Sel, Grip.BottomRight, 5000, 5000, Screen), Is.EqualTo(new PixelRect(100, 100, 1820, 980)));
    }

    [Test]
    public void Nudging_slides_by_the_step_but_stays_on_screen()
    {
        Assert.That(RegionMath.Nudge(Sel, 10, -1, Screen), Is.EqualTo(new PixelRect(110, 99, 400, 300)));
        Assert.That(RegionMath.Nudge(new PixelRect(0, 0, 10, 10), -1, -1, Screen), Is.EqualTo(new PixelRect(0, 0, 10, 10)));
    }

    [Test]
    public void Even_rounds_sizes_down_for_the_encoder()
    {
        Assert.That(RegionMath.Even(new PixelRect(5, 7, 301, 199)), Is.EqualTo(new PixelRect(5, 7, 300, 198)));
        Assert.That(RegionMath.Even(new PixelRect(0, 0, 1, 1)), Is.EqualTo(new PixelRect(0, 0, 2, 2)));
    }
}
