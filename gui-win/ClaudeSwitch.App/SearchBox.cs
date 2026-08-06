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
}
