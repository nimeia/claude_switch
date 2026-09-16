using System.Drawing.Drawing2D;

namespace ClaudeSwitch.App;

/// <summary>
/// One choice among a few short options, drawn as a single control.
/// </summary>
/// <remarks>
/// This started as three <see cref="PillButton"/>s in a row, and read as three
/// buttons: three outlines competing with each other, with the checkbox beside
/// them and with the fields on the rows above. A segmented control says "one of
/// these" in one outline, which is what the setting actually is.
///
/// Its own control rather than radio buttons because the options are one word
/// each and the row is already dense: three labelled circles would cost several
/// times the width to say the same thing.
/// </remarks>
internal sealed class SegmentedChoice : Control
{
    /// <summary>Design-pixel padding either side of the widest option.</summary>
    private const int PadX = 14;

    /// <summary>Design-pixel height, matching the fields it sits with.</summary>
    private const int BoxH = 26;

    private readonly List<(string Key, string Text)> _options;
    private int _selected;
    private int _hot = -1;

    /// <summary>Raised when the user picks an option, never when code sets it.</summary>
    public event EventHandler? SelectionChanged;

    public SegmentedChoice(params (string Key, string Text)[] options)
    {
        _options = [.. options];
        SetStyle(
            ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Font = Theme.FontSmall;
        Cursor = Cursors.Hand;
        // A bare Control starts 0x0, and a flow layout measuring that lays out
        // nothing at all.
        AutoSize = true;
        TabStop = true;
        // The closest single-control role: a reader then announces the control's
        // name and the option in force, which is what this is.
        AccessibleRole = AccessibleRole.ComboBox;
        Theme.Changed += OnThemeChanged;
        Size = GetPreferredSize(Size.Empty);
        SyncAccessibleValue();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Invalidate();

    /// <summary>The key in force. Setting it repaints without raising the event.</summary>
    public string Selected
    {
        get => _options.Count == 0 ? "" : _options[_selected].Key;
        set
        {
            int index = _options.FindIndex(o => o.Key == value);
            if (index < 0 || index == _selected) return;
            _selected = index;
            SyncAccessibleValue();
            Invalidate();
        }
    }

    /// <summary>Re-label an option, for a language change.</summary>
    public void SetText(string key, string text)
    {
        int index = _options.FindIndex(o => o.Key == key);
        if (index < 0) return;
        _options[index] = (key, text);
        SyncAccessibleValue();
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    private void SyncAccessibleValue() =>
        AccessibleDescription = _options.Count == 0 ? null : _options[_selected].Text;

    private float Scale => DeviceDpi / 96f;

    /// <summary>
    /// Equal cells: segments that each take their label's width read as a row of
    /// buttons again, and shift under the mouse when the language changes.
    /// </summary>
    private int CellWidth()
    {
        int widest = 0;
        foreach (var (_, text) in _options)
        {
            widest = Math.Max(
                widest,
                TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPadding).Width);
        }
        return widest + (int)(PadX * 2 * Scale);
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        new((CellWidth() * Math.Max(1, _options.Count)) + 1, (int)(BoxH * Scale));

    protected override void OnFontChanged(EventArgs e)
    {
        Size = GetPreferredSize(Size.Empty);
        base.OnFontChanged(e);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        Size = GetPreferredSize(Size.Empty);
        base.OnDpiChangedAfterParent(e);
    }

    private int IndexAt(int x)
    {
        int cell = CellWidth();
        if (cell <= 0) return -1;
        int index = x / cell;
        return index >= 0 && index < _options.Count ? index : -1;
    }

    private void Choose(int index)
    {
        if (index < 0 || index >= _options.Count || index == _selected) return;
        _selected = index;
        SyncAccessibleValue();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hot = IndexAt(e.X);
        if (hot != _hot)
        {
            _hot = hot;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        Choose(IndexAt(e.X));
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left:
                Choose(_selected - 1);
                e.Handled = true;
                break;
            case Keys.Right:
                Choose(_selected + 1);
                e.Handled = true;
                break;
            case Keys.Home:
                Choose(0);
                e.Handled = true;
                break;
            case Keys.End:
                Choose(_options.Count - 1);
                e.Handled = true;
                break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _hot = -1;
        Invalidate();
        base.OnLostFocus(e);
    }

    /// <summary>Nearest ancestor colour that is actually paintable.</summary>
    /// <remarks>
    /// <see cref="Graphics.Clear"/> ignores alpha, so clearing to a transparent
    /// parent colour paints solid black.
    /// </remarks>
    private Color OpaqueBackdrop()
    {
        for (var p = Parent; p is not null; p = p.Parent)
        {
            if (p.BackColor.A == 255) return p.BackColor;
        }
        return Theme.BgApp;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(OpaqueBackdrop());
        if (_options.Count == 0) return;

        int cell = CellWidth();
        var outer = new Rectangle(0, 0, cell * _options.Count, Height - 1);
        using var path = Shapes.Rounded(outer, outer.Height / 2);

        // Fills are clipped to the outline, so the end cells keep its curve
        // instead of squaring off the corners they sit in.
        using (var clip = new Region(path))
        {
            g.Clip = clip;
            using (var surface = new SolidBrush(Enabled ? Theme.BgSurface : Theme.BgDisabled))
            {
                g.FillRectangle(surface, outer);
            }
            for (int i = 0; i < _options.Count; i++)
            {
                Color fill = i == _selected ? Theme.PrimarySoft
                    : i == _hot && Enabled ? Theme.BgHover
                    : Color.Empty;
                if (fill.IsEmpty) continue;
                using var brush = new SolidBrush(fill);
                g.FillRectangle(brush, new Rectangle(cell * i, 0, cell, outer.Height));
            }
            g.ResetClip();
        }

        // Dividers, but never against the selected cell: a line there only
        // fights the edge of the fill.
        using (var divider = new Pen(Theme.BorderSoft))
        {
            int inset = (int)(6 * Scale);
            for (int i = 1; i < _options.Count; i++)
            {
                if (i == _selected || i - 1 == _selected) continue;
                int x = cell * i;
                g.DrawLine(divider, x, outer.Top + inset, x, outer.Bottom - inset);
            }
        }

        using (var border = Focused
            ? new Pen(Theme.SelectionBorder, 2f)
            : new Pen(Enabled ? Theme.BorderSoft : Theme.BgDisabled))
        {
            g.DrawPath(border, path);
        }

        for (int i = 0; i < _options.Count; i++)
        {
            Color text = !Enabled ? Theme.TextDisabled
                : i == _selected ? Theme.PrimaryDark
                : i == _hot ? Theme.TextPrimary
                : Theme.TextSecondary;
            TextRenderer.DrawText(
                g,
                _options[i].Text,
                Font,
                new Rectangle(cell * i, 0, cell, outer.Height),
                text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPrefix);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
