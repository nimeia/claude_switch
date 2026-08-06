using System.Drawing.Drawing2D;

namespace ClaudeSwitch.App;

/// <summary>
/// One plotted value.
/// </summary>
/// <param name="Label">Axis/row label.</param>
/// <param name="Value">Magnitude.</param>
/// <param name="Tooltip">Full detail — the reader gets everything on hover, so
/// the chart itself only needs the labels that fit.</param>
internal sealed record ChartPoint(string Label, double Value, string Tooltip);

/// <summary>
/// Shared chart chrome: one series, one hue, recessive grid, hover tooltip.
///
/// Every chart here plots a single measure, so color does magnitude, not
/// identity — one hue throughout and no legend (the title says what is plotted).
/// The brand green is the UI's, not a data color: at OKLCH chroma 0.086 it reads
/// as gray once it becomes a mark, so the marks use the nearest step that clears
/// the chroma floor and the mode's lightness band.
/// </summary>
internal abstract class ChartBase : Control
{
    /// <summary>Series hue, validated per mode against the card surface.</summary>
    protected static Color SeriesColor => Theme.Mode == ThemeMode.Dark
        ? Color.FromArgb(0x56, 0xA0, 0x71)
        : Color.FromArgb(0x2E, 0x7D, 0x4F);

    protected const int BarThickness = 20; // spec caps marks at 24px
    protected const int SurfaceGap = 2;
    protected const int DataEndRadius = 4;

    private readonly ToolTip _tip = new() { ShowAlways = true, AutoPopDelay = 15000 };
    private int _hotIndex = -1;

    public IReadOnlyList<ChartPoint> Points { get; private set; } = [];

    /// <summary>Names the measure; stands in for a legend on a single series.</summary>
    public string Caption { get; set; } = "";

    protected ChartBase()
    {
        SetStyle(
            ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
        DoubleBuffered = true;
        BackColor = Theme.BgSurface;
        Theme.Changed += OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        BackColor = Theme.BgSurface;
        Invalidate();
    }

    public void SetPoints(IReadOnlyList<ChartPoint> points)
    {
        Points = points;
        _hotIndex = -1;
        _tip.SetToolTip(this, "");
        Invalidate();
    }

    protected double MaxValue =>
        Points.Count == 0 ? 0 : Math.Max(1e-9, Points.Max(p => p.Value));

    /// <summary>Index under the pointer, or -1. Implemented per chart geometry.</summary>
    protected abstract int HitTest(Point location);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit != _hotIndex)
        {
            _hotIndex = hit;
            // A chart with no room for every label still answers every question
            // on hover; that is what lets the axis stay uncluttered.
            _tip.SetToolTip(this, hit >= 0 ? Points[hit].Tooltip : "");
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hotIndex != -1)
        {
            _hotIndex = -1;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected int HotIndex => _hotIndex;

    /// <summary>
    /// Bar with a rounded data-end and a square baseline, per the mark spec.
    /// </summary>
    protected static GraphicsPath BarPath(RectangleF r, bool vertical)
    {
        var path = new GraphicsPath();
        float radius = Math.Min(DataEndRadius, Math.Min(r.Width, r.Height) / 2f);
        if (radius <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        float d = radius * 2;
        if (vertical)
        {
            // Grows upward: round the top, keep the baseline square.
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddLine(r.Right, r.Bottom, r.Left, r.Bottom);
        }
        else
        {
            // Grows rightward: round the right end.
            path.AddLine(r.Left, r.Top, r.Right - radius, r.Top);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddLine(r.Left, r.Bottom, r.Left, r.Top);
        }
        path.CloseFigure();
        return path;
    }

    protected static string FormatCompact(double v) =>
        v >= 1_000_000 ? $"{v / 1_000_000:0.#}M"
        : v >= 1_000 ? $"{v / 1_000:0.#}K"
        : $"{v:0}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Activity over time: one column per day, gaps included.
///
/// The series arrives with idle days filled in, so the spacing is real time —
/// plotting only the active days would close the gaps and make a fortnight off
/// look like consecutive work.
/// </summary>
internal sealed class ColumnChart : ChartBase
{
    // Bands are derived from the font, never fixed pixels: at 125% display
    // scaling a hardcoded box is shorter than the text it holds, so the axis row
    // lost its descenders and the peak label was cropped by the top edge.
    private int LabelHeight => Theme.FontCaption.Height + 2;
    private int AxisBand => LabelHeight + 6;
    private int TopPad => LabelHeight + 8;

    private float SlotWidth =>
        Points.Count == 0 ? 0 : (float)(Width - 8) / Points.Count;

    protected override int HitTest(Point location)
    {
        if (Points.Count == 0 || location.Y > Height - AxisBand) return -1;
        int i = (int)((location.X - 4) / Math.Max(1f, SlotWidth));
        return i >= 0 && i < Points.Count ? i : -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        if (Points.Count == 0) return;

        int plotBottom = Height - AxisBand;
        int plotHeight = Math.Max(1, plotBottom - TopPad);
        double max = MaxValue;

        // Baseline only — a single hairline is enough grid for a short series.
        using (var axis = new Pen(Theme.BorderSoft))
            g.DrawLine(axis, 0, plotBottom, Width, plotBottom);

        float slot = SlotWidth;
        float barW = Math.Min(BarThickness, Math.Max(1f, slot - SurfaceGap));
        int peak = Points.Select((p, i) => (p, i)).OrderByDescending(t => t.p.Value).First().i;

        for (int i = 0; i < Points.Count; i++)
        {
            double v = Points[i].Value;
            if (v <= 0) continue;
            float h = (float)(v / max * plotHeight);
            float x = 4 + i * slot + (slot - barW) / 2f;
            var r = new RectangleF(x, plotBottom - h, barW, h);

            bool hot = i == HotIndex;
            using var brush = new SolidBrush(
                hot ? Theme.Primary : SeriesColor);
            using var path = BarPath(r, vertical: true);
            g.FillPath(brush, path);
        }

        // Label the ends and the peak only — a number on every column is noise.
        DrawAxisLabel(g, 0, plotBottom, slot, TextFormatFlags.Left);
        if (Points.Count > 1)
            DrawAxisLabel(g, Points.Count - 1, plotBottom, slot, TextFormatFlags.Right);

        var peakPoint = Points[peak];
        if (peakPoint.Value > 0)
        {
            float h = (float)(peakPoint.Value / max * plotHeight);
            var box = new Rectangle(
                (int)(4 + peak * slot) - 24,
                Math.Max(0, (int)(plotBottom - h) - LabelHeight - 1),
                (int)Math.Max(48, slot) + 48,
                LabelHeight);
            TextRenderer.DrawText(
                g,
                FormatCompact(peakPoint.Value),
                Theme.FontCaption,
                box,
                Theme.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }
    }

    private void DrawAxisLabel(Graphics g, int index, int y, float slot, TextFormatFlags align)
    {
        var box = new Rectangle(4, y + 2, Width - 8, AxisBand - 2);
        TextRenderer.DrawText(
            g,
            Points[index].Label,
            Theme.FontCaption,
            box,
            Theme.TextMuted,
            align | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        _ = slot;
    }
}

/// <summary>
/// Magnitude across a handful of named rows — horizontal so long labels fit.
/// </summary>
internal sealed class BarListChart : ChartBase
{
    private const int LabelWidth = 190;
    private const int ValueWidth = 62;

    /// <summary>Row box sized to the text it holds, not to a 96-DPI constant.</summary>
    private int RowHeight => Math.Max(BarThickness + 10, Theme.FontSmall.Height + 14);

    public int PreferredHeight => Math.Max(RowHeight, Points.Count * RowHeight) + 4;

    protected override int HitTest(Point location)
    {
        int i = (location.Y - 2) / RowHeight;
        return i >= 0 && i < Points.Count ? i : -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        if (Points.Count == 0) return;

        int labelW = Math.Min(LabelWidth, Math.Max(80, Width / 3));
        int trackLeft = labelW + Theme.Space2;
        int trackWidth = Math.Max(20, Width - trackLeft - ValueWidth - Theme.Space2);
        double max = MaxValue;

        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            int top = 2 + i * RowHeight;
            int barTop = top + (RowHeight - BarThickness) / 2;

            TextRenderer.DrawText(
                g,
                p.Label,
                Theme.FontSmall,
                new Rectangle(0, top, labelW, RowHeight),
                i == HotIndex ? Theme.TextPrimary : Theme.TextSecondary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            float w = (float)(p.Value / max * trackWidth);
            if (w >= 1)
            {
                var r = new RectangleF(trackLeft, barTop, w, BarThickness);
                using var brush = new SolidBrush(i == HotIndex ? Theme.Primary : SeriesColor);
                using var path = BarPath(r, vertical: false);
                g.FillPath(brush, path);
            }

            // Value sits past the bar end, never inside it — an in-bar label on a
            // short bar gets clipped.
            TextRenderer.DrawText(
                g,
                FormatCompact(p.Value),
                Theme.FontCaption,
                new Rectangle(trackLeft + trackWidth + Theme.Space1, top, ValueWidth, RowHeight),
                Theme.TextSecondary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right
                | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }
}

/// <summary>
/// Calendar heatmap: one cell per day, weeks as columns, weekdays as rows.
///
/// Magnitude, so colour is a single-hue sequential ramp — validated for monotone
/// lightness, ≥0.06 lightness between steps, and a light end that still clears
/// 2:1 on its surface. Empty days sit outside the ramp in a neutral, because the
/// distinction that matters most is "nothing that day" vs "a little": inside the
/// ramp those two measured ΔE 8.3, which is below the floor for telling colours
/// apart with full colour vision.
/// </summary>
internal sealed class CalendarHeatmap : ChartBase
{
    private static Color[] Ramp => Theme.Mode == ThemeMode.Dark
        ? [
            Color.FromArgb(0x41, 0x78, 0x57),
            Color.FromArgb(0x52, 0x99, 0x6C),
            Color.FromArgb(0x66, 0xB8, 0x84),
            Color.FromArgb(0x7F, 0xD6, 0x9E),
        ]
        : [
            Color.FromArgb(0x7B, 0xC2, 0x9B),
            Color.FromArgb(0x4F, 0xA8, 0x7B),
            Color.FromArgb(0x30, 0x84, 0x59),
            Color.FromArgb(0x1C, 0x63, 0x40),
        ];

    private static Color EmptyCell => Theme.Mode == ThemeMode.Dark
        ? Color.FromArgb(0x22, 0x2A, 0x26)
        : Color.FromArgb(0xF4, 0xF5, 0xF4);

    private const int Gap = 2; // the surface gap; no cell borders

    /// <summary>
    /// Cells grow to fill the width available.
    ///
    /// A fixed cell size makes a short history a postage stamp in the corner of a
    /// wide window — this is the view's centrepiece, so it takes the room it has.
    /// </summary>
    private int Cell
    {
        get
        {
            int usable = Math.Max(60, Width - LeftGutter - 8);
            return Math.Clamp(usable / WeekCount - Gap, 8, MaxCellSize);
        }
    }

    private int Step => Cell + Gap;

    /// <summary>
    /// Left edge of the grid, centred in the space after the weekday rail.
    /// A few weeks of history pinned to the left edge leaves most of the view
    /// empty and reads as truncated rather than as "early days".
    /// </summary>
    private int GridLeft
    {
        get
        {
            int gridWidth = WeekCount * Step;
            return LeftGutter + Math.Max(0, (Width - LeftGutter - gridWidth) / 2);
        }
    }
    /// <summary>
    /// The weekday rail only earns its space when a row is tall enough to hold
    /// the label. Below that the glyphs are clipped top and bottom, which reads
    /// as a rendering fault rather than as an axis.
    /// </summary>
    private bool ShowWeekdayRail => Cell >= Theme.FontCaption.Height;

    // Fixed, not derived from Cell: Cell now depends on the gutter.
    private int LeftGutter => Theme.FontCaption.Height + 12;
    private int TopGutter => Theme.FontCaption.Height + 4;

    /// <summary>Day-of-week of the first cell, so weeks line up with real weeks.</summary>
    public int FirstWeekday { get; set; }

    /// <summary>
    /// Upper bound on cell size. The full view lets cells grow so the calendar
    /// is the centrepiece; an embedded strip caps them so it stays a glance and
    /// does not eat the list it sits under.
    /// </summary>
    public int MaxCellSize { get; set; } = 30;

    /// <summary>
    /// Draw placeholder cells instead of data.
    ///
    /// The grid keeps its real geometry while waiting, so the space is reserved
    /// up front and the arriving data swaps in without shoving the layout.
    /// </summary>
    public bool ShowSkeleton { get; set; }

    /// <summary>Weeks assumed while empty, so the placeholder is the real size.</summary>
    public int SkeletonWeeks { get; set; } = 16;

    private int WeekCount => Points.Count == 0
        ? Math.Max(1, SkeletonWeeks)
        : Math.Max(1, (Points.Count + FirstWeekday + 6) / 7);

    public int PreferredHeight => TopGutter + Step * 7 + 4;

    private (int Col, int Row) CellAt(int index)
    {
        int offset = index + FirstWeekday;
        return (offset / 7, offset % 7);
    }

    protected override int HitTest(Point location)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            var (col, row) = CellAt(i);
            var r = new Rectangle(GridLeft + col * Step, TopGutter + row * Step, Cell, Cell);
            if (r.Contains(location)) return i;
        }
        return -1;
    }

    /// <summary>
    /// Bucket by share of the busiest day, so the ramp always uses its full range.
    /// </summary>
    private int Level(double value, double max)
    {
        if (value <= 0) return -1;
        double share = value / max;
        return share > 0.66 ? 3 : share > 0.33 ? 2 : share > 0.12 ? 1 : 0;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        if (Points.Count == 0)
        {
            if (ShowSkeleton) PaintSkeleton(g);
            return;
        }

        double max = MaxValue;
        var ramp = Ramp;
        string lastMonth = "";

        for (int i = 0; i < Points.Count; i++)
        {
            var (col, row) = CellAt(i);
            var r = new Rectangle(GridLeft + col * Step, TopGutter + row * Step, Cell, Cell);
            int level = Level(Points[i].Value, max);
            using (var brush = new SolidBrush(level < 0 ? EmptyCell : ramp[level]))
            using (var path = CardPanel.Rounded(r, 2))
                g.FillPath(brush, path);

            if (i == HotIndex)
            {
                using var ring = new Pen(Theme.TextPrimary, 1.5f);
                g.DrawPath(ring, CardPanel.Rounded(r, 2));
            }

            // Month label at the first column of each new month.
            string month = Points[i].Label.Length >= 7 ? Points[i].Label[..7] : "";
            if (month != lastMonth && row == 0 || (i == 0 && month != lastMonth))
            {
                lastMonth = month;
                TextRenderer.DrawText(
                    g,
                    month.Length >= 7 ? $"{int.Parse(month[5..7])}月" : month,
                    Theme.FontCaption,
                    new Rectangle(r.X - 2, 0, Step * 5, TopGutter),
                    Theme.TextMuted,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            }
        }

        // Weekday rail — only alternate rows, so the labels never crowd.
        if (!ShowWeekdayRail) return;
        string[] names = ["一", "二", "三", "四", "五", "六", "日"];
        for (int row = 1; row < 7; row += 2)
        {
            TextRenderer.DrawText(
                g,
                names[row],
                Theme.FontCaption,
                new Rectangle(0, TopGutter + row * Step, GridLeft - Gap, Cell),
                Theme.TextMuted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// Placeholder grid in the exact geometry the data will occupy.
    /// </summary>
    /// <remarks>
    /// One flat tone, no animation: a 700 ms wait does not need a shimmer, and
    /// the caption beside the grid already says the work is running. What the
    /// placeholder must do is hold the space so nothing jumps.
    /// </remarks>
    private void PaintSkeleton(Graphics g)
    {
        using var brush = new SolidBrush(EmptyCell);
        for (int col = 0; col < WeekCount; col++)
        {
            for (int row = 0; row < 7; row++)
            {
                var r = new Rectangle(
                    GridLeft + col * Step, TopGutter + row * Step, Cell, Cell);
                if (r.Right > Width) continue;
                using var path = CardPanel.Rounded(r, 2);
                g.FillPath(brush, path);
            }
        }
    }

    /// <summary>Legend swatches — the ramp needs a key to be readable.</summary>
    public void PaintLegend(Graphics g, Rectangle bounds)
    {
        int cell = Math.Min(Cell, 16);
        const int TailWidth = 26; // room for the "多" label past the swatches
        int x = bounds.Right - (cell + Gap) * 4 - TailWidth;
        TextRenderer.DrawText(
            g, "少", Theme.FontCaption,
            new Rectangle(x - 24, bounds.Y, 20, bounds.Height),
            Theme.TextMuted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        foreach (var c in Ramp)
        {
            var r = new Rectangle(x, bounds.Y + (bounds.Height - cell) / 2, cell, cell);
            using (var brush = new SolidBrush(c))
            using (var path = CardPanel.Rounded(r, 2))
                g.FillPath(brush, path);
            x += cell + Gap;
        }
        TextRenderer.DrawText(
            g, "多", Theme.FontCaption,
            new Rectangle(x + 4, bounds.Y, TailWidth - 6, bounds.Height),
            Theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// A headline number with its label — the right form for a single value, where a
/// one-bar chart would be noise.
/// </summary>
internal sealed class StatTile : Control
{
    private string _value = "";
    private string _label = "";

    public StatTile()
    {
        SetStyle(
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.ResizeRedraw | ControlStyles.UserPaint,
            true);
        DoubleBuffered = true;
        BackColor = Theme.BgSurface;
        Theme.Changed += OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        BackColor = Theme.BgSurface;
        Invalidate();
    }

    public void Set(string label, string value)
    {
        _label = label;
        _value = value;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = CardPanel.Rounded(bounds, Theme.ControlRadius))
        using (var fill = new SolidBrush(Theme.BgRowAlt))
            g.FillPath(fill, path);

        // Rows are measured from their fonts, never fixed pixels: a 16px box
        // holds an 8pt caption at 100% scaling and clips it at 125%.
        const TextFormatFlags Flags =
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        int inner = Width - Theme.Space3 * 2;
        int labelH = Theme.FontCaption.Height + 2;
        int valueH = Theme.FontHeading.Height + 2;

        // Centre the pair vertically so the tile looks the same whatever the
        // fonts measure at this DPI.
        int top = Math.Max(Theme.Space1, (Height - labelH - valueH) / 2);

        TextRenderer.DrawText(
            g, _label, Theme.FontCaption,
            new Rectangle(Theme.Space3, top, inner, labelH),
            Theme.TextMuted,
            Flags);
        TextRenderer.DrawText(
            g, _value, Theme.FontHeading,
            new Rectangle(Theme.Space3, top + labelH, inner, valueH),
            Theme.TextPrimary,
            Flags);
    }

    /// <summary>Height needed for both rows at the current DPI, plus padding.</summary>
    public int PreferredHeight =>
        Theme.FontCaption.Height + Theme.FontHeading.Height + 4 + Theme.Space3;

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
