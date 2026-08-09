using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeSwitch.App;

/// <summary>
/// After first paint, dump chrome control rectangles and a window screenshot
/// for layout acceptance (env CLAUDE_SWITCH_LAYOUT_DIR).
/// </summary>
internal static class LayoutProbe
{
    /// <summary>
    /// Secondary windows to open and capture before exiting, as
    /// (file name, opener). They run inside the probe's own deferred pass, after
    /// the main capture — doing it from a separate Shown handler races the exit
    /// below.
    /// </summary>
    public static List<(string FileName, Func<Form?> Open)> ExtraWindows { get; } = [];

    /// <summary>
    /// Things that draw *over* the main window — menus, drop-downs — which
    /// cannot be captured with PrintWindow because that renders a window's own
    /// content only. These are screen-copied instead, so the overlay is included.
    /// </summary>
    public static List<(string FileName, Action Show)> Overlays { get; } = [];

    /// <summary>
    /// Extra assertions to append to the rects dump.
    /// </summary>
    /// <remarks>
    /// A window used to add its own checks from a second <c>Shown</c> handler,
    /// which appended to a file this probe then overwrote — so those lines had
    /// silently not been produced for some time. Anything that wants to be in
    /// the dump has to be written while the dump is being built.
    /// </remarks>
    public static List<Func<string>> ExtraChecks { get; } = [];

    /// <summary>
    /// Force a window size for the capture (env <c>CLAUDE_SWITCH_PROBE_SIZE</c>,
    /// as <c>1000x640</c>).
    /// </summary>
    /// <remarks>
    /// The bands only clip at the sizes nobody develops at. Capturing at the
    /// declared minimum is the one check that catches a toolbar which fits the
    /// author's window and is cut off in the user's — which is exactly how the
    /// last toolbar regression shipped.
    /// </remarks>
    private static void ApplyProbeSize(Form form)
    {
        var raw = Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_SIZE");
        if (string.IsNullOrWhiteSpace(raw)) return;
        var parts = raw.Split('x', 'X');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var w)
            || !int.TryParse(parts[1], out var h))
            return;
        form.WindowState = FormWindowState.Normal;
        form.Size = new Size(w, h);
    }

    public static void RunIfRequested(Form form, params (string Name, Control Control)[] targets)
    {
        var dir = Environment.GetEnvironmentVariable("CLAUDE_SWITCH_LAYOUT_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return;

        Directory.CreateDirectory(dir);
        form.Shown += (_, _) =>
        {
            // Defer past first layout pass.
            form.BeginInvoke(new Action(() =>
            {
                try
                {
                    ApplyProbeSize(form);
                    form.PerformLayout();
                    Application.DoEvents();
                    // Long enough for background work to land (the activity strip
                    // fills from a scan) and for paints to settle — two earlier
                    // captures caught half-drawn frames at 200 ms. Overridable so
                    // a run can deliberately catch the pre-load state.
                    int settle = int.TryParse(
                        Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_SETTLE_MS"),
                        out var ms) ? Math.Max(0, ms) : 1200;
                    for (int elapsed = 0; elapsed < settle; elapsed += 100)
                    {
                        Thread.Sleep(Math.Min(100, settle - elapsed));
                        Application.DoEvents();
                    }
                    form.Activate();
                    form.BringToFront();
                    form.Refresh();
                    Application.DoEvents();

                    var sb = new StringBuilder();
                    sb.AppendLine($"form={form.ClientSize.Width}x{form.ClientSize.Height}");
                    sb.AppendLine($"bounds={form.Bounds}");
                    var rects = new List<(string Name, Rectangle Screen)>();
                    foreach (var (name, c) in targets)
                    {
                        if (c is null || c.IsDisposed)
                        {
                            sb.AppendLine($"{name}=MISSING");
                            continue;
                        }
                        var r = c.RectangleToScreen(c.ClientRectangle);
                        rects.Add((name, r));
                        sb.AppendLine(
                            $"{name}=vis={c.Visible} size={c.Width}x{c.Height} client=({c.Left},{c.Top},{c.Width},{c.Height}) screen=({r.X},{r.Y},{r.Width},{r.Height}) text={Quote(c is Button b ? b.Text : c is TextBox t ? t.Text : c.Text)}");
                    }

                    // Sibling chrome bands only (not parent/child containment).
                    var bands = new[] { "header", "action", "settings", "list", "status" };
                    var bandRects = rects.Where(r => bands.Contains(r.Name)).ToList();
                    for (int i = 0; i < bandRects.Count; i++)
                    {
                        for (int j = i + 1; j < bandRects.Count; j++)
                        {
                            var a = bandRects[i];
                            var b = bandRects[j];
                            var inter = Rectangle.Intersect(a.Screen, b.Screen);
                            if (!inter.IsEmpty && inter.Width > 2 && inter.Height > 2)
                                sb.AppendLine($"OVERLAP {a.Name} x {b.Name} = {inter.Width}x{inter.Height}");
                        }
                    }
                    // Search must not intersect switch button (siblings in toolbar).
                    var sw = rects.FirstOrDefault(r => r.Name == "switch");
                    var se = rects.FirstOrDefault(r => r.Name == "search");
                    if (sw.Name is not null && se.Name is not null)
                    {
                        var inter = Rectangle.Intersect(sw.Screen, se.Screen);
                        if (!inter.IsEmpty && inter.Width > 2 && inter.Height > 2)
                            sb.AppendLine($"OVERLAP switch x search = {inter.Width}x{inter.Height}");
                        else
                            sb.AppendLine("OK switch_search_no_overlap");
                    }

                    // Switch must stay inside action band (no bleed into settings).
                    var act = rects.FirstOrDefault(r => r.Name == "action");
                    var set = rects.FirstOrDefault(r => r.Name == "settings");
                    if (sw.Name is not null && act.Name is not null)
                    {
                        if (sw.Screen.Bottom > act.Screen.Bottom + 1)
                            sb.AppendLine($"FAIL switch_bleeds_past_action bottom_delta={sw.Screen.Bottom - act.Screen.Bottom}");
                        else
                            sb.AppendLine("OK switch_inside_action");
                    }
                    if (sw.Name is not null && set.Name is not null)
                    {
                        var inter = Rectangle.Intersect(sw.Screen, set.Screen);
                        if (!inter.IsEmpty && inter.Height > 1)
                            sb.AppendLine($"FAIL switch_settings_overlap h={inter.Height}");
                        else
                            sb.AppendLine("OK switch_settings_no_overlap");
                    }

                    // Assert primary button text fully fits (no clip).
                    var switchBtn = targets.FirstOrDefault(t => t.Name == "switch").Control as Button;
                    if (switchBtn is not null)
                    {
                        var need = TextRenderer.MeasureText(switchBtn.Text, switchBtn.Font);
                        // Button width should be at least text width (padding aside).
                        sb.AppendLine($"switch_text_px={need.Width} switch_btn_w={switchBtn.Width}");
                        if (switchBtn.Width + 2 < need.Width)
                            sb.AppendLine("FAIL switch_button_clips_text");
                        else
                            sb.AppendLine("OK switch_button_fits_text");
                        // Dynamic CTA: 请选择账号 / 当前正在使用 / 切换到该账号 / 正在切换…
                        if (switchBtn.Text.Contains(Loc.T("toolbar.switch.select"), StringComparison.Ordinal)
                            || switchBtn.Text.Contains(Loc.T("toolbar.switch.current"), StringComparison.Ordinal)
                            || switchBtn.Text.Contains(Loc.T("toolbar.switch.go"), StringComparison.Ordinal)
                            || switchBtn.Text.Contains(Loc.T("toolbar.switch.busy"), StringComparison.Ordinal))
                            sb.AppendLine("OK switch_label_complete");
                        else
                            sb.AppendLine("FAIL switch_label_incomplete");
                    }

                    var search = targets.FirstOrDefault(t => t.Name == "search").Control;
                    if (search is not null && search.Visible && search.Width > 40 && search.Height > 10)
                        sb.AppendLine("OK search_visible");
                    else
                        sb.AppendLine("FAIL search_not_visible");

                    // List: FlowLayout AutoScroll host — content height vs client height.
                    var scroll = targets.FirstOrDefault(t => t.Name == "scroll").Control as ScrollableControl;
                    var cards = targets.FirstOrDefault(t => t.Name == "cards").Control;
                    var list = targets.FirstOrDefault(t => t.Name == "list").Control;
                    var status = targets.FirstOrDefault(t => t.Name == "status").Control;
                    if (scroll is not null && cards is not null)
                    {
                        int contentH = 0;
                        foreach (Control child in cards.Controls)
                            contentH += child.Height + child.Margin.Vertical;
                        contentH += cards.Padding.Vertical;
                        sb.AppendLine(
                            $"scroll_client={scroll.ClientSize.Width}x{scroll.ClientSize.Height} " +
                            $"display={scroll.DisplayRectangle.Width}x{scroll.DisplayRectangle.Height} " +
                            $"contentH={contentH} childCount={cards.Controls.Count}");
                        if (contentH > scroll.ClientSize.Height + 8)
                        {
                            // Must be able to scroll: display rect taller than client, or vscroll present.
                            bool canScroll = scroll.DisplayRectangle.Height > scroll.ClientSize.Height + 2
                                             || scroll.VerticalScroll.Visible
                                             || scroll.AutoScrollMinSize.Height > scroll.ClientSize.Height;
                            sb.AppendLine(canScroll ? "OK list_scrollable" : "FAIL list_not_scrollable");
                        }
                        else
                            sb.AppendLine("OK list_fits_viewport");
                    }
                    if (list is not null && status is not null)
                    {
                        var lr = list.RectangleToScreen(list.ClientRectangle);
                        var sr = status.RectangleToScreen(status.ClientRectangle);
                        var inter = Rectangle.Intersect(lr, sr);
                        if (!inter.IsEmpty && inter.Height > 1)
                            sb.AppendLine($"FAIL list_status_overlap h={inter.Height}");
                        else
                            sb.AppendLine("OK list_status_no_overlap");
                    }

                    foreach (var line in ExtraChecks)
                    {
                        try { sb.AppendLine(line()); }
                        catch (Exception ex) { sb.AppendLine($"FAIL extra_check {ex.GetType().Name}"); }
                    }

                    File.WriteAllText(Path.Combine(dir, "gui-layout-rects.txt"), sb.ToString(), Encoding.UTF8);

                    var shotPath = Path.Combine(dir, "gui-layout.png");
                    CaptureWindow(form, shotPath);
                    File.WriteAllText(
                        Path.Combine(dir, "gui-launch.log"),
                        $"alive=true capture={File.Exists(shotPath)} dir={dir}\n",
                        Encoding.UTF8);

                    foreach (var (fileName, show) in Overlays)
                    {
                        // A screen copy takes whatever pixels are at those
                        // coordinates, so the window must genuinely be in front —
                        // otherwise the shot captures unrelated applications.
                        form.Activate();
                        form.BringToFront();
                        Application.DoEvents();
                        Thread.Sleep(150);
                        show();
                        Application.DoEvents();
                        Thread.Sleep(400);
                        CaptureScreen(form, Path.Combine(dir, fileName));
                    }

                    foreach (var (fileName, open) in ExtraWindows)
                    {
                        if (open() is not { IsDisposed: false } extra) continue;
                        extra.Activate();
                        Application.DoEvents();
                        Thread.Sleep(250);
                        extra.PerformLayout();
                        Application.DoEvents();
                        CaptureWindow(extra, Path.Combine(dir, fileName));
                    }

                    // Exit after probe so CI/agent can finish.
                    if (string.Equals(
                            Environment.GetEnvironmentVariable("CLAUDE_SWITCH_LAYOUT_EXIT"),
                            "1",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Application.ExitThread();
                        Application.Exit();
                        form.Close();
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(
                            Path.Combine(dir, "gui-layout-capture-error.log"),
                            ex.ToString(),
                            Encoding.UTF8);
                    }
                    catch { /* ignore */ }
                    if (string.Equals(
                            Environment.GetEnvironmentVariable("CLAUDE_SWITCH_LAYOUT_EXIT"),
                            "1",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Application.Exit();
                    }
                }
            }));
        };
    }

    private static string Quote(string? s) =>
        "\"" + (s ?? "").Replace("\"", "'") + "\"";


    /// <summary>Copy the pixels where the window sits, overlays included.</summary>
    private static void CaptureScreen(Form form, string path)
    {
        var b = form.Bounds;
        using var bmp = new Bitmap(Math.Max(1, b.Width), Math.Max(1, b.Height));
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(b.Location, Point.Empty, b.Size);
        bmp.Save(path, ImageFormat.Png);
    }

    private static void CaptureWindow(Form form, string path)
    {
        // Prefer PrintWindow for accurate chrome; fall back to CopyFromScreen.
        var bounds = form.Bounds;
        using var bmp = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            var ok = PrintWindow(form.Handle, g.GetHdc(), 0x00000002); // PW_RENDERFULLCONTENT
            g.ReleaseHdc();
            if (!ok)
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }
        }
        bmp.Save(path, ImageFormat.Png);
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
}
