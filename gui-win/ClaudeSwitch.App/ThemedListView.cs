using System.Drawing.Drawing2D;

namespace ClaudeSwitch.App;

/// <summary>
/// A details-mode list painted in the app's palette.
///
/// A stock <see cref="ListView"/> ignores the theme entirely: grey system header,
/// Windows-blue selection, no hover feedback, and a boxed border — next to the
/// custom-painted account cards it reads as a different application. Owner-draw
/// keeps the control's scrolling, column and selection behaviour while replacing
/// every painted pixel.
/// </summary>
internal sealed class ThemedListView : ListView
{
    private int _hoverIndex = -1;

    /// <summary>Column indexes whose values are numbers and read better right-aligned.</summary>
    public HashSet<int> RightAlignedColumns { get; } = [];

    public ThemedListView()
    {
        View = View.Details;
        OwnerDraw = true;
        FullRowSelect = true;
        MultiSelect = false;
        HideSelection = false;
        GridLines = false;
        BorderStyle = BorderStyle.None;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        Font = Theme.FontBody;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        ApplyTheme();
        Theme.Changed += OnThemeChanged;
    }

    // Scrollbars stay system-drawn. `SetWindowTheme(…, "DarkMode_Explorer")` does
    // not darken them without the undocumented app-level dark-mode ordinals, and
    // it makes the theme paint column dividers across the empty area below the
    // last row. The main window's card list has native scrollbars too, so
    // leaving them alone is also the consistent choice.

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        ApplyTheme();
        Invalidate();
    }

    private void ApplyTheme()
    {
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int index = HitTest(e.Location).Item?.Index ?? -1;
        if (index != _hoverIndex)
        {
            int old = _hoverIndex;
            _hoverIndex = index;
            InvalidateRow(old);
            InvalidateRow(index);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverIndex >= 0)
        {
            int old = _hoverIndex;
            _hoverIndex = -1;
            InvalidateRow(old);
        }
        base.OnMouseLeave(e);
    }

    private void InvalidateRow(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        Invalidate(Items[index].Bounds);
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(Theme.BgHeader))
            g.FillRectangle(bg, e.Bounds);
        using (var line = new Pen(Theme.BorderSoft))
            g.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

        var text = new Rectangle(
            e.Bounds.X + Theme.Space2,
            e.Bounds.Y,
            Math.Max(0, e.Bounds.Width - Theme.Space2 * 2),
            e.Bounds.Height);
        TextRenderer.DrawText(
            g,
            e.Header?.Text ?? "",
            Theme.FontCaption,
            text,
            Theme.TextMuted,
            Flags(e.ColumnIndex));
        e.DrawDefault = false;
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        var g = e.Graphics;
        bool selected = e.Item?.Selected == true;
        bool hover = e.ItemIndex == _hoverIndex;

        Color fill = selected ? Theme.BgSelected
            : hover ? Theme.BgHover
            : e.ItemIndex % 2 == 1 ? Theme.BgRowAlt
            : Theme.BgSurface;
        using (var bg = new SolidBrush(fill))
            g.FillRectangle(bg, e.Bounds);

        // A left rail instead of a full ring: rows sit edge to edge, so a border
        // would collide with its neighbours' borders.
        if (selected)
        {
            using var rail = new SolidBrush(Theme.SelectionBorder);
            g.FillRectangle(rail, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);
        }
        e.DrawDefault = false;
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null) return;
        var g = e.Graphics;

        // Row background is painted by OnDrawItem; sub-items only add text. The
        // first column is inset past the selection rail so they never overlap.
        int inset = e.ColumnIndex == 0 ? Theme.Space2 + 3 : Theme.Space2;
        var box = new Rectangle(
            e.Bounds.X + inset,
            e.Bounds.Y,
            Math.Max(0, e.Bounds.Width - inset - Theme.Space1),
            e.Bounds.Height);

        Color color = e.Item.ForeColor == Color.Empty || e.Item.ForeColor == SystemColors.WindowText
            ? (e.ColumnIndex == 0 ? Theme.TextPrimary : Theme.TextSecondary)
            : e.Item.ForeColor;

        TextRenderer.DrawText(
            g,
            e.SubItem?.Text ?? "",
            e.ColumnIndex == 0 ? Theme.FontBody : Theme.FontSmall,
            box,
            color,
            Flags(e.ColumnIndex));
        e.DrawDefault = false;
    }

    private TextFormatFlags Flags(int column) =>
        TextFormatFlags.VerticalCenter
        | TextFormatFlags.EndEllipsis
        | TextFormatFlags.NoPrefix
        | TextFormatFlags.SingleLine
        | (RightAlignedColumns.Contains(column) ? TextFormatFlags.Right : TextFormatFlags.Left);

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Surface with a soft rounded border, matching the account cards.
/// </summary>
internal sealed class CardPanel : Panel
{
    public CardPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        DoubleBuffered = true;
        Padding = new Padding(1);
        ApplyTheme();
        Theme.Changed += OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        ApplyTheme();
        Invalidate();
    }

    private void ApplyTheme() => BackColor = Theme.BgSurface;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Rounded(bounds, Theme.CardRadius);
        using var pen = new Pen(Theme.BorderSoft);
        g.DrawPath(pen, path);
    }

    /// <summary>
    /// Local name for the shared shape.
    /// </summary>
    /// <remarks>
    /// The copy that used to live here clamped the corner diameter only against
    /// the radius, not against the rectangle — a short row folded the path in on
    /// itself. <see cref="Shapes.Rounded"/> guards both.
    /// </remarks>
    internal static GraphicsPath Rounded(Rectangle r, int radius) => Shapes.Rounded(r, radius);

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
