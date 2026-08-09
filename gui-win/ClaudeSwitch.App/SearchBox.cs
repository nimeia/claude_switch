using System.Runtime.InteropServices;

namespace ClaudeSwitch.App;

/// <summary>
/// Native edit-control watermark ("cue banner") for the toolbar search box.
///
/// The alternative — writing the hint into <c>Text</c> and tracking a
/// "this is only a placeholder" flag — makes the hint indistinguishable from a
/// query at the type level, and every write raises <c>TextChanged</c>
/// synchronously, so any handler that runs before the flag is updated filters
/// the list by the hint itself and reports "no matches". A cue banner is drawn
/// by the control and never appears in <c>Text</c>, so the query is always just
/// the query.
/// </summary>
internal static class SearchBox
{
    private const int EM_SETCUEBANNER = 0x1501;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    /// <summary>
    /// Show <paramref name="hint"/> while the box is empty. Keeps showing it
    /// when focused (wParam 1) so clicking in doesn't blank the hint before the
    /// user has typed anything.
    /// </summary>
    /// <remarks>
    /// Applied on every handle creation: the banner lives on the native window,
    /// so a handle recreation (theme/font changes) silently drops it.
    /// </remarks>
    public static void AttachCueBanner(TextBox? box, string hint)
    {
        if (box is null) return;
        void Apply()
        {
            if (box.IsHandleCreated)
                SendMessage(box.Handle, EM_SETCUEBANNER, 1, hint);
        }
        box.HandleCreated += (_, _) => Apply();
        Apply();
    }

    private const int EM_SETMARGINS = 0x00D3;
    private const int EC_RIGHTMARGIN = 0x0002;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Puts a ✕ inside the search box, visible only while there is a query.
    /// </summary>
    /// <remarks>
    /// The clear action used to be a toolbar button of its own, which spent
    /// permanent width on something that does nothing most of the time — and
    /// sat far enough from the box that it read as a separate command. A child
    /// control plus a right text margin keeps it in the box it clears; the
    /// margin is what stops a long query running underneath the glyph.
    /// </remarks>
    /// <returns>The glyph, so the caller can show and hide it; null if there is no box.</returns>
    public static Control? AttachInlineClear(TextBox? box, string tip, Action clear)
    {
        if (box is null) return null;

        // Design pixels: the glyph is a font, so a fixed device width is a hit
        // target that shrinks as the text it sits beside grows.
        int glyphW = (int)Math.Round(GlyphW * box.DeviceDpi / 96.0);
        var glyph = new Label
        {
            Text = "✕",
            AutoSize = false,
            Width = glyphW,
            Dock = DockStyle.Right,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            Visible = false,
            Font = Theme.FontSmall,
            ForeColor = Theme.TextMuted,
        };
        // Click, not MouseDown: the box keeps focus, so clearing leaves the
        // caret where the user was already typing.
        glyph.Click += (_, _) =>
        {
            clear();
            box.Focus();
        };
        glyph.MouseEnter += (_, _) => glyph.ForeColor = Theme.TextPrimary;
        glyph.MouseLeave += (_, _) => glyph.ForeColor = Theme.TextMuted;
        new ToolTip().SetToolTip(glyph, tip);

        void ApplyMargin()
        {
            if (box.IsHandleCreated)
                SendMessage(box.Handle, EM_SETMARGINS, EC_RIGHTMARGIN, glyphW << 16);
        }
        box.HandleCreated += (_, _) => ApplyMargin();
        box.Controls.Add(glyph);
        ApplyMargin();
        return glyph;
    }

    private const int GlyphW = 20;
}
