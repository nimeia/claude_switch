using System.Text.Json.Nodes;

namespace ClaudeSwitch.App;

/// <summary>
/// A compact activity heatmap on the main window, so the picture costs no clicks.
///
/// Filled from a cached scan on a background thread: the full read is ~500 ms
/// over ~85 MB, which must never sit on the UI thread or on startup. Until it
/// arrives the strip stays quiet rather than showing a spinner for something
/// nobody asked for.
/// </summary>
internal sealed class ActivityStrip : Panel
{
    private readonly CalendarHeatmap _heatmap = new();
    private readonly Label _summary = new();
    private readonly Label _hint = new();
    private bool _loaded;
    /// <summary>Last rendered summary, kept so a language change can re-render
    /// it without re-running the scan.</summary>
    private string _summaryText = "";

    /// <summary>Raised when the strip is clicked — the host opens the full view.</summary>
    public event EventHandler? OpenRequested;

    public ActivityStrip()
    {
        Dock = DockStyle.Bottom;
        Padding = new Padding(Theme.Space4, Theme.Space2, Theme.Space4, Theme.Space2);
        Cursor = Cursors.Hand;

        _summary.Dock = DockStyle.Top;
        _summary.Height = Theme.FontBody.Height + Theme.Space1;
        _summary.Font = Theme.FontBody;
        _summary.TextAlign = ContentAlignment.MiddleLeft;

        _hint.Dock = DockStyle.Right;
        _hint.Width = 96;
        _hint.Font = Theme.FontCaption;
        _hint.Text = Loc.T("strip.open");
        _hint.TextAlign = ContentAlignment.MiddleRight;

        _heatmap.Dock = DockStyle.Fill;
        // Small cells: the strip is a glance under the account list, not the
        // centrepiece, and full-size cells made it ~280px tall.
        _heatmap.MaxCellSize = 13;
        // The strip is a summary, not a control surface: clicking anywhere opens
        // the full view, so the cells must not swallow the click.
        _heatmap.Enabled = false;

        Controls.Add(_heatmap);
        Controls.Add(_summary);
        Controls.Add(_hint);

        foreach (Control c in new Control[] { this, _summary, _hint, _heatmap })
            c.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        ApplyTheme();
        Theme.Changed += OnThemeChanged;
        ShowSkeleton();
        Resize += (_, _) => SyncHeight();
    }

    /// <summary>
    /// Reserve the final geometry and show a placeholder while the scan runs.
    /// </summary>
    /// <remarks>
    /// The strip used to stay at zero height and then appear, shoving the account
    /// list up half a second after launch. Claiming the space immediately makes
    /// the arrival a swap rather than a jolt — and the cell cap keeps that height
    /// the same whatever the data turns out to be.
    /// </remarks>
    private void ShowSkeleton()
    {
        _heatmap.ShowSkeleton = true;
        _summary.Text = Loc.T("strip.loading");
        _hint.Visible = false;
        SyncHeight();
    }

    /// <summary>
    /// Height for the current geometry. Cell size depends on the available
    /// width, so this is only final once the strip has been laid out.
    /// </summary>
    private int StripHeight =>
        _summary.Height + _heatmap.PreferredHeight + Padding.Vertical + 4;

    /// <summary>
    /// Re-measure once the real width is known.
    /// </summary>
    /// <remarks>
    /// Sizing only at construction made the skeleton 35px shorter than the
    /// loaded strip: with no width yet, the heatmap's cells clamp to their
    /// minimum, and the grid grew when the layout gave it room. Re-measuring on
    /// resize is what actually makes the swap jump-free.
    /// </remarks>
    private void SyncHeight()
    {
        if (_collapsed) return;
        int want = StripHeight;
        if (_hostHeight > 0) want = Math.Min(want, _hostHeight * 2 / 5);
        if (Height != want) Height = want;
    }

    /// <summary>Height of the window the strip is in, or 0 while unknown.</summary>
    private int _hostHeight;

    /// <summary>
    /// Tell the strip how much window there is, so it can stand down.
    /// </summary>
    /// <remarks>
    /// The heatmap asks for a fixed height whatever the window does. In a short
    /// window that left the account list a two-line slot under a full-size
    /// chart — the summary crowding out the thing it summarises. Two fifths is
    /// the most it may take.
    /// </remarks>
    public void CapHeight(int hostHeight)
    {
        if (_hostHeight == hostHeight) return;
        _hostHeight = hostHeight;
        SyncHeight();
    }

    /// <summary>Nothing to show at all — the strip stays out of the way.</summary>
    private bool _collapsed;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        ApplyTheme();
        Invalidate(true);
    }

    private void ApplyTheme()
    {
        BackColor = Theme.BgSurface;
        _summary.ForeColor = _loaded ? Theme.TextPrimary : Theme.TextMuted;
        _hint.ForeColor = Theme.Primary;
        _hint.BackColor = Theme.BgSurface;
        _summary.BackColor = Theme.BgSurface;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // A hairline above separates the strip from the list without a box.
        using var pen = new Pen(Theme.BorderSoft);
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }

    /// <summary>
    /// Populate from an `overview_stats` payload. Safe to call with nothing
    /// worth showing — the strip then stays collapsed.
    /// </summary>
    public void Apply(JsonNode? stats)
    {
        _loaded = true;
        _summary.ForeColor = Theme.TextPrimary;
        long Num(string key) => stats?[key]?.GetValue<long>() ?? 0;

        var cells = new List<ChartPoint>();
        if (stats?["daily"] is JsonArray days)
        {
            // Only the recent past: the strip is a glance, and squeezing a year
            // into it would shrink the cells past legibility.
            const int MaxDays = 16 * 7;
            int skip = Math.Max(0, days.Count - MaxDays);
            foreach (var d in days.Skip(skip))
            {
                if (d is null) continue;
                string day = d["day"]?.GetValue<string>() ?? "";
                long q = d["userMessages"]?.GetValue<long>() ?? 0;
                cells.Add(new ChartPoint(day, q, day));
            }
        }

        _heatmap.ShowSkeleton = false;
        if (cells.Count == 0)
        {
            // Nothing to show at all (no transcripts): collapse rather than leave
            // an empty frame implying something failed to load.
            _collapsed = true;
            Height = 0;
            return;
        }

        if (DateTime.TryParse(cells[0].Label, out var start))
            _heatmap.FirstWeekday = ((int)start.DayOfWeek + 6) % 7;
        _heatmap.SetPoints(cells);
        _hint.Visible = true;

        _summaryText = Loc.T(
            "strip.summary",
            Compact(Num("outputTokens")), Num("sessions"),
            Num("projects"), Num("longestStreak"));
        _summary.Text = _summaryText;

        SyncHeight();
    }

    /// <summary>True once a payload has been applied, successful or empty.</summary>
    public bool Loaded => _loaded;

    /// <summary>
    /// Re-labels the strip in the current language. The scan is not repeated:
    /// the numbers are language-independent, only the sentence around them is.
    /// </summary>
    public void ApplyTexts()
    {
        _hint.Text = Loc.T("strip.open");
        _summary.Text = _loaded ? _summaryText : Loc.T("strip.loading");
    }

    private static string Compact(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M"
        : n >= 1_000 ? $"{n / 1_000.0:0.#}K"
        : n.ToString();

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
