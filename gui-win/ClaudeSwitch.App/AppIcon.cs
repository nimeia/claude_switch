using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ClaudeSwitch.App;

/// <summary>Programmatic brand icon (soft green leaf mark) for window + tray.</summary>
internal static class AppIcon
{
    private static Icon? _cached;

    public static Icon Get()
    {
        if (_cached is not null) return _cached;
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Soft circle badge
            using (var bg = new SolidBrush(Theme.Primary))
                g.FillEllipse(bg, 1, 1, 30, 30);

            // Inner mint ring
            using (var ring = new Pen(Theme.PrimarySoft, 2f))
                g.DrawEllipse(ring, 4, 4, 24, 24);

            // Simple "swap" arcs (two curved arrows suggestion)
            using var pen = new Pen(Color.White, 2.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawArc(pen, 9, 9, 14, 14, 200, 140);
            g.DrawArc(pen, 9, 9, 14, 14, 20, 140);

            // Arrow heads (small triangles)
            using var white = new SolidBrush(Color.White);
            g.FillPolygon(white, new[]
            {
                new Point(22, 10), new Point(26, 14), new Point(20, 14),
            });
            g.FillPolygon(white, new[]
            {
                new Point(10, 22), new Point(6, 18), new Point(12, 18),
            });
        }

        // Icon owns a copy of the HICON; keep bitmap until GetHicon then clone icon.
        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            _cached = (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
        return _cached;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}
