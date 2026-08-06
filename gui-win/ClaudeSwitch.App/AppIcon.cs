using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ClaudeSwitch.App;

/// <summary>
/// The brand mark: a green badge with two swap arrows.
///
/// One scalable routine feeds the window, the tray, and the .ico compiled into
/// the executable. That last one earns its keep: the app ships unsigned, and a
/// download wearing the generic runtime icon reads as something to distrust.
/// </summary>
internal static class AppIcon
{
    private static Icon? _cached;

    public static Icon Get()
    {
        if (_cached is not null) return _cached;
        using var bmp = Render(32);
        var hIcon = bmp.GetHicon();
        try
        {
            // Icon owns a copy of the HICON; clone before the handle is destroyed.
            using var tmp = Icon.FromHandle(hIcon);
            _cached = (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
        return _cached;
    }

    /// <summary>
    /// Draw the mark at any size.
    /// </summary>
    /// <remarks>
    /// Every coordinate is a fraction of <paramref name="size"/>, so the 256px
    /// icon is the same drawing rather than an upscaled 32px one.
    /// </remarks>
    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float u = size / 32f; // design units → pixels
        float Px(float v) => v * u;

        // Soft circle badge
        using (var bg = new SolidBrush(Theme.Primary))
            g.FillEllipse(bg, Px(1), Px(1), Px(30), Px(30));

        // Inner mint ring
        using (var ring = new Pen(Theme.PrimarySoft, Px(2)))
            g.DrawEllipse(ring, Px(4), Px(4), Px(24), Px(24));

        // Two curved arrows suggesting a swap
        using var pen = new Pen(Color.White, Px(2.2f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawArc(pen, Px(9), Px(9), Px(14), Px(14), 200, 140);
        g.DrawArc(pen, Px(9), Px(9), Px(14), Px(14), 20, 140);

        // Arrow heads
        using var white = new SolidBrush(Color.White);
        g.FillPolygon(
            white,
            new[]
            {
                new PointF(Px(22), Px(10)),
                new PointF(Px(26), Px(14)),
                new PointF(Px(20), Px(14)),
            });
        g.FillPolygon(
            white,
            new[]
            {
                new PointF(Px(10), Px(22)),
                new PointF(Px(6), Px(18)),
                new PointF(Px(12), Px(18)),
            });
        return bmp;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}
