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
        ApplyTheme();
        Theme.Changed += (_, _) => ApplyTheme();
    }

    private void ApplyTheme()
    {
        UseVisualStyleBackColor = false;
        BackColor = Theme.BgDrawer;
        ForeColor = Theme.TextSecondary;
        FlatAppearance.BorderColor = Theme.BgDrawer;
        FlatAppearance.MouseOverBackColor = Theme.BgHover;
        FlatAppearance.MouseDownBackColor = Theme.BorderSoft;
        FlatAppearance.BorderSize = 0;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        ForeColor = Theme.TextPrimary;
        BackColor = Theme.BgHover;
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        ForeColor = Theme.TextSecondary;
        BackColor = Theme.BgDrawer;
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
        ApplyTheme();
        Theme.Changed += (_, _) => ApplyTheme();
    }

    private void ApplyTheme()
    {
        UseVisualStyleBackColor = false;
        if (Enabled)
        {
            BackColor = Theme.Primary;
            ForeColor = Theme.TextOnPrimary;
            FlatAppearance.BorderColor = Theme.Primary;
            FlatAppearance.MouseOverBackColor = Theme.PrimaryDark;
            FlatAppearance.MouseDownBackColor = Theme.PrimaryDark;
        }
        else
        {
            // Keep label readable when disabled (audit: not washed soft-green).
            BackColor = Theme.BgDisabled;
            ForeColor = Theme.TextDisabled;
            FlatAppearance.BorderColor = Theme.Border;
            FlatAppearance.MouseOverBackColor = Theme.BgDisabled;
            FlatAppearance.MouseDownBackColor = Theme.BgDisabled;
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        if (Enabled) BackColor = Theme.PrimaryDark;
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        BackColor = Enabled ? Theme.Primary : Theme.BgDisabled;
        base.OnMouseLeave(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        ApplyTheme();
        base.OnEnabledChanged(e);
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
        ApplyTheme();
        Theme.Changed += (_, _) => ApplyTheme();
    }

    private void ApplyTheme()
    {
        UseVisualStyleBackColor = false;
        FlatAppearance.BorderColor = Theme.Border;
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
        FlatAppearance.MouseOverBackColor = Theme.BgHover;
        FlatAppearance.MouseDownBackColor = Theme.BgHover;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        BackColor = Theme.BgHover;
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        BackColor = Theme.BgSurface;
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
        ApplyTheme();
        Theme.Changed += (_, _) => ApplyTheme();
    }

    private void ApplyTheme()
    {
        UseVisualStyleBackColor = false;
        BackColor = Theme.UsageHigh;
        ForeColor = Color.White;
        FlatAppearance.BorderColor = Theme.UsageHigh;
        FlatAppearance.MouseOverBackColor = Theme.Danger;
        FlatAppearance.MouseDownBackColor = Theme.Danger;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        BackColor = Theme.Danger;
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        BackColor = Theme.UsageHigh;
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
