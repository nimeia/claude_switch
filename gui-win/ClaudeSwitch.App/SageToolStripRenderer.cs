namespace ClaudeSwitch.App;

/// <summary>
/// Toolstrip roles via <see cref="ToolStripItem.Tag"/> string:
/// "primary" | "danger" | "secondary" (default).
/// Disabled primary stays readable (dark text on neutral fill).
/// </summary>
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

        var g = e.Graphics;
        var r = new Rectangle(Point.Empty, e.Item.Size);
        r.Inflate(-1, -1);
        string role = btn.Tag as string ?? "secondary";
        bool hot = btn.Selected || btn.Pressed;
        bool enabled = btn.Enabled;

        if (role == "primary")
        {
            if (!enabled)
            {
                // Readable disabled: neutral fill + solid text (not soft-green wash).
                using var b = new SolidBrush(Theme.BgDisabled);
                g.FillRectangle(b, r);
                using var p = new Pen(Theme.Border);
                g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                btn.ForeColor = Theme.TextDisabled;
            }
            else
            {
                using var b = new SolidBrush(hot ? Theme.PrimaryDark : Theme.Primary);
                g.FillRectangle(b, r);
                btn.ForeColor = Theme.TextOnPrimary;
            }
        }
        else if (role == "danger")
        {
            if (!enabled)
            {
                using var b = new SolidBrush(Theme.BgSurface);
                g.FillRectangle(b, r);
                using var p = new Pen(Theme.BorderSoft);
                g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                btn.ForeColor = Theme.TextMuted;
            }
            else
            {
                using var b = new SolidBrush(hot ? Theme.DangerSoft : Theme.BgSurface);
                g.FillRectangle(b, r);
                using var p = new Pen(hot ? Theme.Danger : Theme.Border);
                g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                btn.ForeColor = Theme.Danger;
            }
        }
        else
        {
            // secondary
            if (!enabled)
            {
                using var b = new SolidBrush(Theme.BgSurface);
                g.FillRectangle(b, r);
                using var p = new Pen(Theme.BorderSoft);
                g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                btn.ForeColor = Theme.TextMuted;
            }
            else
            {
                using var b = new SolidBrush(hot ? Theme.BgHover : Theme.BgSurface);
                g.FillRectangle(b, r);
                using var p = new Pen(Theme.Border);
                g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                btn.ForeColor = Theme.TextPrimary;
            }
        }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Theme.BgApp);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // no border
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
        public override Color ImageMarginGradientBegin => Theme.BgApp;
        public override Color ImageMarginGradientMiddle => Theme.BgApp;
        public override Color ImageMarginGradientEnd => Theme.BgApp;
        public override Color SeparatorDark => Theme.BorderSoft;
        public override Color SeparatorLight => Theme.BorderSoft;
    }
}
