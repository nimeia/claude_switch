using System.Drawing.Drawing2D;

namespace ClaudeSwitch.App;

/// <summary>
/// Toolstrip roles via <see cref="ToolStripItem.Tag"/> string:
/// "primary" | "danger" | "secondary" (default). Menu rows read "danger" too.
/// Disabled primary stays readable (dark text on neutral fill).
/// </summary>
/// <remarks>
/// Rounded, like every other surface in the window. The colours here were
/// already the theme's; what made the toolbar look like a different, older
/// application was that it alone drew hard square rectangles while the cards,
/// list rows and pills beside it were rounded and anti-aliased.
/// </remarks>
internal sealed class SageToolStripRenderer : ToolStripProfessionalRenderer
{
    public SageToolStripRenderer() : base(new SageColorTable()) { }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripButton btn)
        {
            base.OnRenderButtonBackground(e);
            return;
        }
        PaintItem(e, btn.Tag as string ?? "secondary", btn.Selected || btn.Pressed, btn.Enabled, btn);
    }

    /// <summary>
    /// Paint a dropdown exactly like a button.
    /// </summary>
    /// <remarks>
    /// Without this the dropdowns fell through to the base Office renderer, so
    /// 「继续会话 / 运行 / 工具」were drawn by a different engine than the
    /// buttons sitting next to them — visibly borderless where the others had
    /// borders. This was a gap in the theming, not only a matter of polish.
    /// </remarks>
    protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripDropDownButton drop)
        {
            base.OnRenderDropDownButtonBackground(e);
            return;
        }
        PaintItem(
            e,
            drop.Tag as string ?? "secondary",
            drop.Selected || drop.Pressed || drop.DropDown.Visible,
            drop.Enabled,
            drop);
    }

    /// <summary>
    /// The one place a toolbar item's background is drawn.
    /// </summary>
    /// <remarks>
    /// Shared by both overrides so a button and a dropdown cannot drift into
    /// looking like two different controls, which is exactly what happened
    /// while only one of them was implemented.
    /// </remarks>
    private static void PaintItem(
        ToolStripItemRenderEventArgs e, string role, bool hot, bool enabled, ToolStripItem item)
    {
        var g = e.Graphics;
        var r = new Rectangle(Point.Empty, e.Item.Size);
        r.Inflate(-1, -1);

        // The strip shares one canvas across items, so the mode is restored
        // rather than left on for whatever paints next.
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            PaintRole(g, r, role, hot, enabled, item);
        }
        finally
        {
            g.SmoothingMode = previous;
        }
    }

    private static void Fill(Graphics g, Rectangle r, Color fill, Color? border)
    {
        using var path = Shapes.Rounded(r, Theme.ControlRadius);
        using (var b = new SolidBrush(fill))
            g.FillPath(b, path);
        if (border is { } line)
        {
            using var p = new Pen(line);
            g.DrawPath(p, path);
        }
    }

    private static void PaintRole(
        Graphics g, Rectangle r, string role, bool hot, bool enabled, ToolStripItem item)
    {
        if (role == "primary")
        {
            if (!enabled)
            {
                // Readable disabled: neutral fill + solid text (not soft-green wash).
                Fill(g, r, Theme.BgDisabled, Theme.Border);
                item.ForeColor = Theme.TextDisabled;
            }
            else
            {
                Fill(g, r, hot ? Theme.PrimaryDark : Theme.Primary, border: null);
                item.ForeColor = Theme.TextOnPrimary;
            }
        }
        else if (role == "danger")
        {
            if (!enabled)
            {
                Fill(g, r, Theme.BgSurface, Theme.BorderSoft);
                item.ForeColor = Theme.TextMuted;
            }
            else
            {
                Fill(g, r, hot ? Theme.DangerSoft : Theme.BgSurface, hot ? Theme.Danger : Theme.Border);
                item.ForeColor = Theme.Danger;
            }
        }
        else
        {
            // secondary
            if (!enabled)
            {
                Fill(g, r, Theme.BgSurface, Theme.BorderSoft);
                item.ForeColor = Theme.TextMuted;
            }
            else
            {
                Fill(g, r, hot ? Theme.BgHover : Theme.BgSurface, Theme.Border);
                item.ForeColor = Theme.TextPrimary;
            }
        }
    }

    /// <summary>
    /// The dropdown chevron, in the item's own text colour.
    /// </summary>
    /// <remarks>
    /// The base renderer picks its own colour, which on a primary-filled
    /// dropdown left a dark arrow on a dark green fill.
    /// </remarks>
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item is { IsOnDropDown: true } row
            ? MenuTextColor(row)
            : e.Item?.Enabled == false ? Theme.TextDisabled : e.Item?.ForeColor ?? Theme.TextPrimary;
        base.OnRenderArrow(e);
    }

    /// <summary>
    /// Draw a rounded surround for a hosted text box, at its item bounds.
    /// </summary>
    /// <remarks>
    /// Called from the strip's own Paint rather than through a render override:
    /// a <see cref="ToolStripControlHost"/> does not route its background
    /// through the renderer, so overriding <c>OnRenderItemBackground</c> drew
    /// nothing at all. The hosted text box is shorter than its item, so the
    /// border shows in the margin around it.
    /// </remarks>
    public static void PaintTextBoxSurround(Graphics g, Rectangle bounds, bool focused)
    {
        if (bounds.Width < 2 || bounds.Height < 2) return;
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            Fill(g, bounds, Theme.BgSurface, focused ? Theme.Primary : Theme.Border);
        }
        finally
        {
            g.SmoothingMode = previous;
        }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(e.ToolStrip is ToolStripDropDown ? Theme.BgSurface : Theme.BgApp);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // The toolbar has none; a menu does, or in dark mode it has no edge
        // against the window behind it.
        if (e.ToolStrip is not ToolStripDropDown) return;
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    /// <summary>
    /// Menu rows: a rounded slate highlight under the pointer, nothing otherwise.
    /// </summary>
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.IsOnDropDown)
        {
            base.OnRenderMenuItemBackground(e);
            return;
        }
        if (!e.Item.Selected || !e.Item.Enabled) return;

        var r = new Rectangle(Point.Empty, e.Item.Size);
        r.Inflate(-3, -1);
        var g = e.Graphics;
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            Fill(g, r, Theme.BgSelected, border: null);
        }
        finally
        {
            g.SmoothingMode = previous;
        }
    }

    /// <summary>
    /// Menu text in the theme's colours.
    /// </summary>
    /// <remarks>
    /// Left to the base renderer, a menu row took the system's black
    /// <c>ControlText</c> (and <c>GrayText</c> when disabled), which on the dark
    /// menu surface was nearly the colour of the surface itself. Tag "danger"
    /// marks a destructive row, the same role word the toolbar uses.
    /// </remarks>
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item.IsOnDropDown)
            e.TextColor = MenuTextColor(e.Item);
        base.OnRenderItemText(e);
    }

    private static Color MenuTextColor(ToolStripItem item) =>
        !item.Enabled ? Theme.TextMuted
        : item.Tag as string == "danger" ? Theme.Danger
        : Theme.TextPrimary;

    /// <summary>
    /// The check mark, drawn rather than taken from the base renderer's black glyph.
    /// </summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        if (!e.Item.IsOnDropDown)
        {
            base.OnRenderItemCheck(e);
            return;
        }
        var r = e.ImageRectangle;
        if (r.Width < 4 || r.Height < 4) return;

        var g = e.Graphics;
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using var p = new Pen(MenuTextColor(e.Item), Math.Max(1.5f, r.Height / 9f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            g.DrawLines(p, new[]
            {
                new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                new PointF(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.30f),
            });
        }
        finally
        {
            g.SmoothingMode = previous;
        }
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var g = e.Graphics;
        int mid = e.Item.Height / 2;
        using var p = new Pen(Theme.BorderSoft);
        g.DrawLine(p, 4, mid, e.Item.Width - 4, mid);
    }

    private sealed class SageColorTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Theme.BgApp;
        public override Color ToolStripGradientMiddle => Theme.BgApp;
        public override Color ToolStripGradientEnd => Theme.BgApp;
        // The image margin only exists inside menus, so it matches the menu surface.
        public override Color ImageMarginGradientBegin => Theme.BgSurface;
        public override Color ImageMarginGradientMiddle => Theme.BgSurface;
        public override Color ImageMarginGradientEnd => Theme.BgSurface;
        public override Color ToolStripDropDownBackground => Theme.BgSurface;
        public override Color MenuBorder => Theme.Border;
        public override Color SeparatorDark => Theme.BorderSoft;
        public override Color SeparatorLight => Theme.BorderSoft;
    }
}
