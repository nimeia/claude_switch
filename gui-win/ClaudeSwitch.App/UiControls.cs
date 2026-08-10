using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeSwitch.App;

/// <summary>
/// Compact title-bar close (×). Soft circle on hover — no system red chrome.
/// </summary>
internal sealed class IconCloseButton : Control
{
    private bool _hover;
    private bool _pressed;

    public IconCloseButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.SupportsTransparentBackColor
            | ControlStyles.ResizeRedraw,
            true);
        Size = new Size(28, 28);
        Cursor = Cursors.Hand;
        TabStop = false;
        Theme.Changed += (_, _) => Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent?.BackColor ?? Theme.BgDrawer);

        var bounds = new Rectangle(2, 2, Width - 5, Height - 5);
        if (_hover || _pressed)
        {
            // Soft neutral disc — not system-red.
            Color fill = _pressed ? Theme.BorderSoft : Theme.BgHover;
            using var path = RoundRect(bounds, bounds.Height / 2);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }

        // Draw × — compact standard size, slightly stronger stroke than thin hairline.
        Color ink = _hover ? Theme.TextPrimary : Theme.TextMuted;
        int m = Math.Max(7, Width / 4);
        using var pen = new Pen(ink, 1.9f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawLine(pen, m, m, Width - m - 1, Height - m - 1);
        g.DrawLine(pen, Width - m - 1, m, m, Height - m - 1);
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>
/// Shared metrics for themed buttons. Row height follows the display DPI and the
/// font's line box: a hard-coded 30px row leaves only ~20px of text area once
/// Windows scaling is above 100%, which clips the bottom of CJK glyphs.
/// Widths stay as declared — callers place buttons at absolute x in fixed dialogs,
/// and AutoSize already grows them for the (DPI-scaled) text.
/// </summary>
internal abstract class ThemedButton : Button
{
    private int _minWidth;

    protected ThemedButton(int minWidth)
    {
        _minWidth = minWidth;
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        Font = Theme.FontBody;
        Cursor = Cursors.Hand;
        Theme.Changed += OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed) ApplyAppearance();
    }

    /// <summary>
    /// Repaint for the current theme <em>and</em> the current enabled state.
    /// </summary>
    /// <remarks>
    /// <c>FlatStyle.Flat</c> plus an explicit <c>BackColor</c> means Windows
    /// draws no disabled state of its own: a greyed-out button kept its full
    /// colour and stayed indistinguishable from a live one. Every flavour gets
    /// the same disabled palette from here, so a new button cannot forget it.
    /// </remarks>
    protected void ApplyAppearance()
    {
        UseVisualStyleBackColor = false;
        if (Enabled)
        {
            Cursor = Cursors.Hand;
            ApplyEnabledPalette();
            return;
        }
        BackColor = Theme.BgDisabled;
        ForeColor = Theme.TextDisabled;
        FlatAppearance.BorderColor = Theme.BorderSoft;
        FlatAppearance.MouseOverBackColor = Theme.BgDisabled;
        FlatAppearance.MouseDownBackColor = Theme.BgDisabled;
        Cursor = Cursors.Default;
    }

    /// <summary>Colours for the live button — the disabled case is handled above.</summary>
    protected abstract void ApplyEnabledPalette();

    protected override void OnEnabledChanged(EventArgs e)
    {
        ApplyAppearance();
        base.OnEnabledChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    public int MinWidth
    {
        get => _minWidth;
        set
        {
            _minWidth = value;
            ApplyMetrics();
        }
    }

    /// <summary>
    /// Row height in device pixels — the design height scaled for DPI, but never
    /// shorter than the text line box plus padding and the flat-border inset.
    /// </summary>
    public int RowHeight =>
        Math.Max(Scale(Theme.ControlHeight), Font.Height + Padding.Vertical + Scale(8));

    /// <summary>Converts a 96-DPI design length to device pixels.</summary>
    protected int Scale(int designPx) => (int)Math.Round(designPx * DeviceDpi / 96.0);

    protected void ApplyMetrics()
    {
        int h = RowHeight;
        MinimumSize = new Size(_minWidth, h);
        MaximumSize = new Size(500, h);
        if (Height != h)
            Height = h;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var s = base.GetPreferredSize(proposedSize);
        return new Size(Math.Max(s.Width, MinimumSize.Width), RowHeight);
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        base.SetBoundsCore(x, y, width, RowHeight, specified);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyMetrics();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ApplyMetrics();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyMetrics();
    }
}

/// <summary>Low-emphasis text/ghost button (e.g. footer 「关闭」).</summary>
internal sealed class GhostButton : ThemedButton
{
    public GhostButton()
        : base(minWidth: 0)
    {
        FlatAppearance.BorderSize = 0;
        AutoSize = false;
        Padding = new Padding(8, 2, 8, 2);
        ApplyMetrics();
        ApplyAppearance();
    }

    protected override void ApplyEnabledPalette()
    {
        BackColor = Theme.BgDrawer;
        ForeColor = Theme.TextSecondary;
        FlatAppearance.BorderColor = Theme.BgDrawer;
        FlatAppearance.MouseOverBackColor = Theme.BgHover;
        FlatAppearance.MouseDownBackColor = Theme.BorderSoft;
        FlatAppearance.BorderSize = 0;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        if (Enabled)
        {
            ForeColor = Theme.TextPrimary;
            BackColor = Theme.BgHover;
        }
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        ApplyAppearance();
        base.OnMouseLeave(e);
    }
}

/// <summary>Toolbar buttons: AutoSize width, single fixed row height so chrome bands cannot overflow.</summary>
internal sealed class PrimaryButton : ThemedButton
{
    public PrimaryButton()
        : base(minWidth: 88)
    {
        FlatAppearance.BorderSize = 0;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12, 3, 12, 3);
        Margin = new Padding(0, 0, 6, 0);
        ApplyMetrics();
        ApplyAppearance();
    }

    protected override void ApplyEnabledPalette()
    {
        BackColor = Theme.Primary;
        ForeColor = Theme.TextOnPrimary;
        FlatAppearance.BorderColor = Theme.Primary;
        FlatAppearance.MouseOverBackColor = Theme.PrimaryDark;
        FlatAppearance.MouseDownBackColor = Theme.PrimaryDark;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        if (Enabled) BackColor = Theme.PrimaryDark;
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        ApplyAppearance();
        base.OnMouseLeave(e);
    }
}

internal sealed class SecondaryButton : ThemedButton
{
    public SecondaryButton()
        : base(minWidth: 56)
    {
        FlatAppearance.BorderSize = 1;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10, 3, 10, 3);
        Margin = new Padding(0, 0, 6, 0);
        ApplyMetrics();
        ApplyAppearance();
    }

    protected override void ApplyEnabledPalette()
    {
        FlatAppearance.BorderColor = Theme.Border;
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
        FlatAppearance.MouseOverBackColor = Theme.BgHover;
        FlatAppearance.MouseDownBackColor = Theme.BgHover;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        if (Enabled) BackColor = Theme.BgHover;
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        ApplyAppearance();
        base.OnMouseLeave(e);
    }
}

/// <summary>
/// Stacks a fixed dialog's content from measured text and sizes the form to fit.
/// Lengths passed in are 96-DPI design pixels; everything is scaled per display,
/// so nothing clips at 125/150/200% Windows scaling.
/// </summary>
internal sealed class DialogLayout
{
    private readonly Form _form;
    private readonly int _pad;
    private readonly int _gap;
    private readonly int _textW;
    private int _y;

    public DialogLayout(Form form, int textWidth, int pad = 24, int gap = 12)
    {
        _form = form;
        _pad = Theme.Scale(form, pad);
        _gap = Theme.Scale(form, gap);
        _textW = Theme.Scale(form, textWidth);
        _y = Theme.Scale(form, 20);
    }

    /// <summary>Left edge of the content column, in device pixels.</summary>
    public int Left => _pad;

    /// <summary>Content column width, in device pixels.</summary>
    public int TextWidth => _textW;

    /// <summary>Next free y, in device pixels.</summary>
    public int Y
    {
        get => _y;
        set => _y = value;
    }

    /// <summary>Scales a 96-DPI design length for this dialog's display.</summary>
    public int Scale(int designPx) => Theme.Scale(_form, designPx);

    /// <summary>Adds a self-measuring label at the current y and advances past it.</summary>
    public Label Text(string text, Font font, Color color)
    {
        var label = new Label
        {
            Text = text,
            Font = font,
            ForeColor = color,
            AutoSize = true,
            MaximumSize = new Size(_textW, 0),
            Location = new Point(_pad, _y),
        };
        // AutoSize only measures once the label joins a parent — too late to stack
        // from Bottom here, so measure now. The later pass lands on the same size.
        label.Size = label.GetPreferredSize(new Size(_textW, 0));
        Advance(label);
        return label;
    }

    /// <summary>Advances past a control the caller positioned itself.</summary>
    public void Advance(Control c) => _y = c.Bottom + _gap;

    /// <summary>Places buttons right-aligned on one row, then sizes the dialog to fit.</summary>
    public void ActionRow(params ThemedButton[] buttons)
    {
        int width = _pad + _textW + _pad;
        int rowY = _y + _gap;
        int rowH = 0;
        int right = width - _pad;
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            var b = buttons[i];
            int w = b.GetPreferredSize(Size.Empty).Width;
            b.Width = w;
            b.Location = new Point(right - w, rowY);
            right -= w + Scale(10);
            rowH = Math.Max(rowH, b.RowHeight);
        }
        _form.ClientSize = new Size(width, rowY + rowH + _pad);
    }
}

/// <summary>Destructive confirm action (e.g. 「确认删除」).</summary>
internal sealed class DangerButton : ThemedButton
{
    public DangerButton()
        : base(minWidth: 88)
    {
        FlatAppearance.BorderSize = 0;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12, 3, 12, 3);
        Margin = new Padding(0, 0, 6, 0);
        ApplyMetrics();
        ApplyAppearance();
    }

    protected override void ApplyEnabledPalette()
    {
        BackColor = Theme.UsageHigh;
        ForeColor = Color.White;
        FlatAppearance.BorderColor = Theme.UsageHigh;
        FlatAppearance.MouseOverBackColor = Theme.Danger;
        FlatAppearance.MouseDownBackColor = Theme.Danger;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        if (Enabled) BackColor = Theme.Danger;
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        ApplyAppearance();
        base.OnMouseLeave(e);
    }
}

internal sealed class SoftPanel : Panel
{
    public SoftPanel()
    {
        BackColor = Theme.BgSurface;
        Padding = new Padding(12);
        Theme.Changed += (_, _) =>
        {
            BackColor = Theme.BgSurface;
            Invalidate();
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.BorderSoft);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>
/// A check box that follows the app theme.
/// </summary>
/// <remarks>
/// The stock control draws its box through the OS visual style renderer, which
/// knows nothing about our palette: in dark mode it stays a bright white square,
/// which is the first thing the eye lands on and the last thing that should be.
/// Painting the box ourselves is the only way to make it match — there is no
/// property that recolours the glyph.
/// </remarks>
internal sealed class ThemedCheckBox : CheckBox
{
    /// <summary>Box edge in design px, before DPI scaling.</summary>
    private const int BoxSize = 14;

    /// <summary>Gap between the box and its label, in design px.</summary>
    private const int Gap = 7;

    private bool _hot;

    public ThemedCheckBox()
    {
        SetStyle(
            ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.SupportsTransparentBackColor,
            true);
        // The parent's colour shows through, so the row it sits in stays one
        // continuous surface instead of a label on a patch.
        BackColor = Color.Transparent;
        Theme.Changed += (_, _) => Invalidate();
    }

    private float Scale => DeviceDpi / 96f;

    private int Box => (int)(BoxSize * Scale);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        return new Size(
            Box + (int)(Gap * Scale) + text.Width + Padding.Horizontal,
            Math.Max(Box, text.Height) + Padding.Vertical);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hot = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    /// <summary>Nearest ancestor colour that is actually paintable.</summary>
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
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        // Graphics.Clear ignores alpha, so clearing to a transparent parent
        // colour paints solid black. Walk up to something opaque instead.
        g.Clear(OpaqueBackdrop());

        int box = Box;
        int top = (Height - box) / 2;
        var rect = new Rectangle(Padding.Left, top, box, box);

        if (Checked)
        {
            using var fill = new SolidBrush(Enabled ? Theme.Primary : Theme.BgDisabled);
            g.FillRectangle(fill, rect);
        }
        else
        {
            using var fill = new SolidBrush(Enabled ? Theme.BgSurface : Theme.BgDisabled);
            g.FillRectangle(fill, rect);
        }

        using var border = new Pen(
            !Enabled ? Theme.BorderSoft
            : Checked ? Theme.Primary
            : _hot ? Theme.Primary
            : Theme.Border);
        g.DrawRectangle(border, rect);

        if (Checked)
        {
            // Drawn rather than a glyph font: a tick from a font would be at the
            // mercy of whatever is installed.
            using var tick = new Pen(Theme.TextOnPrimary, Math.Max(1.6f, box * 0.14f))
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            float l = rect.Left + box * 0.24f;
            float m = rect.Left + box * 0.44f;
            float r = rect.Left + box * 0.76f;
            float midY = rect.Top + box * 0.62f;
            g.DrawLines(tick,
            [
                new PointF(l, rect.Top + box * 0.5f),
                new PointF(m, midY),
                new PointF(r, rect.Top + box * 0.3f),
            ]);
        }

        var textRect = new Rectangle(
            rect.Right + (int)(Gap * Scale),
            0,
            Width - rect.Right - (int)(Gap * Scale) - Padding.Right,
            Height);
        TextRenderer.DrawText(
            g,
            Text,
            Font,
            textRect,
            Enabled ? Theme.TextPrimary : Theme.TextDisabled,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// Makes native scrollbars follow the app theme.
/// </summary>
/// <remarks>
/// Scrollbars on an <c>AutoScroll</c> container are drawn by the OS, not by us,
/// so a dark window gets a bright white stripe down its edge that no colour
/// property reaches. Windows 10 1809 and later expose a dark variant of the
/// Explorer visual style; asking for it by name is the supported way in. On
/// anything older the call simply does nothing, which is the right fallback.
/// </remarks>
internal static class NativeScrollbars
{
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? subAppName, string? subIdList);

    /// <summary>Apply the current theme's scrollbars to <paramref name="control"/> and its children.</summary>
    public static void Apply(Control control)
    {
        if (control.IsDisposed) return;
        try
        {
            if (control.IsHandleCreated)
            {
                SetWindowTheme(
                    control.Handle,
                    Theme.Mode == ThemeMode.Dark ? "DarkMode_Explorer" : "Explorer",
                    null);
            }
        }
        catch (DllNotFoundException)
        {
            // No uxtheme: nothing to restyle, and not worth failing over.
            return;
        }
        catch (EntryPointNotFoundException)
        {
            return;
        }

        foreach (Control child in control.Controls) Apply(child);
    }
}

/// <summary>
/// A spin box whose buttons follow the app theme.
/// </summary>
/// <remarks>
/// <see cref="NumericUpDown"/> hosts a native up-down control, so its two
/// arrows are drawn by the OS from the system visual style and stay bright
/// white in a dark window — the loudest thing on the settings row. The control
/// gives no way to recolour them, so the buttons are hidden and painted here,
/// over the same edit box the base class already manages.
/// </remarks>
internal sealed class ThemedNumericUpDown : NumericUpDown
{
    /// <summary>Design-px width reserved for the two arrows.</summary>
    private const int ButtonWidth = 17;

    private bool _upHot;
    private bool _downHot;

    public ThemedNumericUpDown()
    {
        BorderStyle = BorderStyle.None;
        TextAlign = HorizontalAlignment.Center;
        // The base class positions its native buttons on top of everything; the
        // only reliable way to be rid of them is to stop them being shown.
        Controls[0].Visible = false;
        Theme.Changed += OnThemeChanged;
        ApplyTheme();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        ApplyTheme();
        Invalidate(true);
    }

    private void ApplyTheme()
    {
        BackColor = Enabled ? Theme.BgSurface : Theme.BgDisabled;
        ForeColor = Enabled ? Theme.TextPrimary : Theme.TextDisabled;
        foreach (Control c in Controls)
        {
            c.BackColor = BackColor;
            c.ForeColor = ForeColor;
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        ApplyTheme();
        base.OnEnabledChanged(e);
    }

    private float ScaleF => DeviceDpi / 96f;

    private Rectangle ButtonArea =>
        new(Width - (int)(ButtonWidth * ScaleF) - 1, 1, (int)(ButtonWidth * ScaleF), Height - 2);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        // Keep the text clear of the painted arrows.
        if (Controls.Count > 1 && Controls[1] is Control edit)
        {
            var area = ButtonArea;
            edit.SetBounds(edit.Left, edit.Top, Math.Max(4, area.Left - edit.Left - 2), edit.Height);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var area = ButtonArea;
        bool up = area.Contains(e.Location) && e.Y < area.Top + area.Height / 2;
        bool down = area.Contains(e.Location) && !up;
        if (up != _upHot || down != _downHot)
        {
            _upHot = up;
            _downHot = down;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _upHot = _downHot = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var area = ButtonArea;
        if (area.Contains(e.Location))
        {
            if (e.Y < area.Top + area.Height / 2) UpButton();
            else DownButton();
            Invalidate();
            return;
        }
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var box = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var fill = new SolidBrush(BackColor))
            g.FillRectangle(fill, ButtonArea);
        using (var border = new Pen(Enabled ? Theme.Border : Theme.BorderSoft))
            g.DrawRectangle(border, box);

        var area = ButtonArea;
        DrawArrow(g, new Rectangle(area.X, area.Y, area.Width, area.Height / 2), up: true, hot: _upHot);
        DrawArrow(
            g,
            new Rectangle(area.X, area.Y + area.Height / 2, area.Width, area.Height / 2),
            up: false,
            hot: _downHot);
    }

    private void DrawArrow(Graphics g, Rectangle r, bool up, bool hot)
    {
        if (hot && Enabled)
        {
            using var glow = new SolidBrush(Theme.BgHover);
            g.FillRectangle(glow, r);
        }
        float w = Math.Max(3f, r.Width * 0.30f);
        float cx = r.X + r.Width / 2f;
        float cy = r.Y + r.Height / 2f;
        float h = w * 0.55f;
        var points = up
            ? new[]
            {
                new PointF(cx - w / 2, cy + h / 2),
                new PointF(cx + w / 2, cy + h / 2),
                new PointF(cx, cy - h / 2),
            }
            : new[]
            {
                new PointF(cx - w / 2, cy - h / 2),
                new PointF(cx + w / 2, cy - h / 2),
                new PointF(cx, cy + h / 2),
            };
        using var brush = new SolidBrush(
            !Enabled ? Theme.TextDisabled : hot ? Theme.PrimaryDark : Theme.TextSecondary);
        g.FillPolygon(brush, points);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
