using System.Drawing.Drawing2D;

using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// A task row, painted as a card rather than assembled from flat panels.
/// </summary>
/// <remarks>
/// The account list next to it is drawn this way — rounded surface, soft border,
/// a coloured edge for the row that matters, and a hover state. A stack of
/// square-cornered panels beside those cards reads as a different, older
/// application pasted into the window, which is exactly what "too native" means.
/// Painting the row means one control instead of six, so hover and the accent
/// come for free.
/// </remarks>
internal sealed class TaskRow : Panel
{
    private readonly TaskEntry _entry;
    private readonly Color _accent;
    private bool _hover;

    /// <summary>Design-px width of the coloured edge that marks the state.</summary>
    private const int AccentWidth = 3;

    public TaskRow(TaskEntry entry, Color accent)
    {
        _entry = entry;
        _accent = accent;
        SetStyle(
            ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw,
            true);
        Cursor = Cursors.Hand;
    }

    private float Scale => DeviceDpi / 96f;

    private int Sc(int designPx) => (int)Math.Round(designPx * Scale);

    protected override void OnMouseEnter(EventArgs e)
    {
        SetHover(true);
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        SetHover(false);
        base.OnMouseLeave(e);
    }

    /// <summary>
    /// Change the row's surface, telling the pills what they now sit on.
    /// </summary>
    /// <remarks>
    /// The pills paint their own backdrop, so a row that changed colour under
    /// them without saying so would leave each one in a patch of the old shade.
    /// </remarks>
    private void SetHover(bool hover)
    {
        if (_hover == hover) return;
        _hover = hover;
        foreach (var pill in Descendants(this).OfType<PillButton>())
        {
            pill.Surface = SurfaceColor;
            pill.Invalidate();
        }
        Invalidate();
    }

    private Color SurfaceColor => _hover ? Theme.BgHover : Theme.BgSurface;

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var c in Descendants(child)) yield return c;
        }
    }

    /// <summary>Re-seed the pills after a theme change, which moves the palette.</summary>
    public void RefreshSurfaces()
    {
        foreach (var pill in Descendants(this).OfType<PillButton>())
        {
            pill.Surface = SurfaceColor;
            pill.Invalidate();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.BgApp);

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        int radius = Theme.CardRadius;
        using (var path = Rounded(bounds, radius))
        using (var fill = new SolidBrush(_hover ? Theme.BgHover : Theme.BgSurface))
            g.FillPath(fill, path);

        // The state's colour lives on the edge, not on the text: it marks the
        // row without competing with what the row says.
        using (var clip = Rounded(bounds, radius))
        {
            var saved = g.Clip;
            g.SetClip(clip);
            using var accent = new SolidBrush(_accent);
            g.FillRectangle(accent, bounds.X, bounds.Y, Sc(AccentWidth), bounds.Height + 1);
            g.Clip = saved;
        }

        using (var path = Rounded(bounds, radius))
        using (var pen = new Pen(_hover ? Theme.Border : Theme.BorderSoft))
            g.DrawPath(pen, path);

        int left = Sc(AccentWidth) + Sc(12);
        int right = Width - Sc(8) - ActionsWidth;
        int textW = Math.Max(10, right - left - Sc(12));

        // Two lines: what it is, then everything about it. The title carries the
        // weight so the eye lands there first.
        TextRenderer.DrawText(
            g,
            _entry.Title,
            Theme.FontBody,
            new Rectangle(left, Sc(5), textW, Height / 2 - Sc(4)),
            Theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        TextRenderer.DrawText(
            g,
            Detail,
            Theme.FontSmall,
            new Rectangle(left, Height / 2 - Sc(1), textW, Height / 2 - Sc(4)),
            Theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>Width the action buttons occupy, so text knows where to stop.</summary>
    public int ActionsWidth { get; set; }

    /// <summary>Second line: account, where, state, and how long.</summary>
    public string Detail { get; init; } = "";

    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// A small pill-shaped action, for use inside a painted row.
/// </summary>
/// <remarks>
/// The stock flat button is a hard-edged rectangle; three of them in a row read
/// as a toolbar from a decade ago. A pill with a hairline border and a filled
/// hover state matches the rounded surface it sits on.
/// </remarks>
internal sealed class PillButton : Control
{
    private bool _hover;
    private bool _down;

    /// <summary>Draws the primary action with more weight than the rest.</summary>
    public bool Emphasis { get; init; }

    /// <summary>
    /// Colour behind the pill, so its rounded corners blend into the row.
    /// </summary>
    /// <remarks>
    /// Set explicitly rather than read from the parent: the parent is a
    /// transparent flow panel, and <see cref="Graphics.Clear"/> ignores alpha —
    /// clearing to <see cref="Color.Transparent"/> paints solid black, which is
    /// exactly what showed up as square black corners around every pill.
    /// </remarks>
    public Color Surface { get; set; } = Theme.BgSurface;

    public PillButton(string text)
    {
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
        // A bare Control starts 0×0, and a flow layout measuring that lays out
        // nothing at all — the buttons were invisible until this was set.
        AutoSize = true;
        Text = text;
        Size = GetPreferredSize(Size.Empty);
    }

    private float Scale => DeviceDpi / 96f;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        return new Size(text.Width + (int)(24 * Scale), (int)(26 * Scale));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Size = GetPreferredSize(Size.Empty);
        base.OnTextChanged(e);
    }

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

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _down = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _down = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _down = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Surface);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = TaskRow.Rounded(r, r.Height / 2);

        Color fill = _down ? Theme.BgActive
            : _hover ? (Emphasis ? Theme.PrimarySoft : Theme.BgHover)
            : Emphasis ? Theme.PrimarySoft
            : Color.Transparent;
        if (fill != Color.Transparent)
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }

        using (var pen = new Pen(Emphasis || _hover ? Theme.Primary : Theme.BorderSoft))
            g.DrawPath(pen, path);

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            r,
            Emphasis || _hover ? Theme.PrimaryDark : Theme.TextSecondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// What a supervised task is doing right now.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrower than the vocabulary an agent manager might want. Every
/// state here is one the app can actually observe: a run in flight, a dialog
/// waiting for an answer, a journal record, a takeover that failed. States like
/// "paused" or "waiting for input" are missing because ACP has no such notion —
/// a label that can never light up is worse than no label, because it teaches
/// the user that the others might be guesses too.
/// </para>
/// <para>
/// Ordering is by how much a person is needed, which is also the order the board
/// sorts in.
/// </para>
/// </remarks>
internal enum TaskState
{
    /// <summary>Blocked on a permission dialog. Nothing moves until answered.</summary>
    AwaitingPermission,
    /// <summary>A takeover could not rescue this session.</summary>
    NeedsAttention,
    /// <summary>Stopped on an error; retrying is the obvious next move.</summary>
    Failed,
    /// <summary>An agent is working right now.</summary>
    Running,
    /// <summary>Cut off mid-flight and resumable.</summary>
    Interrupted,
    /// <summary>Stopped by the user.</summary>
    Cancelled,
}

/// <summary>One task on the board.</summary>
/// <param name="Account">Which Claude account is spending the quota, if known.</param>
/// <param name="Cwd">Full working directory; the row shows a shortened form.</param>
/// <param name="Elapsed">Time readout — how long it has run, or how long it has been stuck.</param>
/// <param name="Primary">
/// The action the state calls for. Its label changes with the state, because
/// "open" tells a user nothing about what a stuck task needs.
/// </param>
/// <param name="Stop">Stops a run in flight; null when there is nothing to stop.</param>
/// <param name="Ignore">
/// Takes the row off the board, or null when it cannot be dismissed. A live run
/// has no such action: hiding work that is still happening is how a supervised
/// run becomes an unsupervised one.
/// </param>
internal sealed record TaskEntry(
    TaskState State,
    string Title,
    string? Account,
    string Cwd,
    string Elapsed,
    Action Primary,
    string PrimaryLabel,
    Action? Stop = null,
    Action? Ignore = null,
    Action? OpenFolder = null,
    string? Note = null);

/// <summary>
/// The supervised work this app is doing, on the main window.
/// </summary>
/// <remarks>
/// <para>
/// Auto-continue is the one feature whose whole point is to work while nobody is
/// watching, which makes it the one feature with nothing to show for itself. A
/// dropdown was enough while a run was something you had just started; it is not
/// enough once the app resumes sessions on its own, because then the first time
/// you hear about a task is when it has already failed.
/// </para>
/// <para>
/// Two lines per task rather than a table: the account and the directory are
/// context for the title, not columns to scan across, and a table wide enough
/// for a full Windows path leaves no room for anything else.
/// </para>
/// <para>
/// The band takes no space when there is nothing to report — an empty panel
/// permanently above the account list would cost every user height to tell most
/// of them nothing.
/// </para>
/// </remarks>
internal sealed class TaskBoard : Panel
{
    /// <summary>Tasks shown while the band is collapsed.</summary>
    /// <remarks>
    /// The band competes with the account list for height. Three is enough to
    /// answer "is anything wrong"; the rest is one click away, in place.
    /// </remarks>
    public const int MaxRows = 3;

    /// <summary>Tasks per page once the band is expanded.</summary>
    /// <remarks>
    /// Expanding must not push the account list off the window, so the expanded
    /// view pages rather than growing without limit. Four keeps the accounts
    /// visible on a normal window — six was measured squeezing them down to a
    /// single partial row. The pager is there for the case that proves this
    /// wrong, not as the normal way to read the list.
    /// </remarks>
    public const int PageSize = 4;

    /// <summary>
    /// Share of the window the band may take when expanded.
    /// </summary>
    /// <remarks>
    /// A backstop independent of <see cref="PageSize"/>: on a short window even
    /// four rows can crowd out the accounts, and this app is an account list
    /// first. Past this the rows scroll instead of growing.
    /// </remarks>
    public const float MaxHeightShare = 0.38f;

    /// <summary>Design-px height of the pager strip.</summary>
    public const int PagerHeight = 24;

    /// <summary>Design-px height of one two-line row, before DPI scaling.</summary>
    public const int RowHeight = 46;

    /// <summary>Design-px gap between rows, so they read as separate tasks.</summary>
    public const int RowGap = 3;

    /// <summary>
    /// Longest note shown inline; the rest is left to the tooltip.
    /// </summary>
    /// <remarks>
    /// Failure messages quote the path they failed on, which the row has
    /// already shown in short form. Repeating it in full pushes everything else
    /// off the line to say nothing new.
    /// </remarks>
    private const int MaxNoteChars = 28;

    /// <summary>Design-px height of the summary line.</summary>
    /// <remarks>
    /// Tall enough for the font's full line box, not just its cap height: at 22
    /// the tops of CJK glyphs were shaved off, which is the kind of clipping
    /// that only shows up in the language it breaks.
    /// </remarks>
    public const int HeadingHeight = 26;

    private readonly Label _summary;
    private readonly LinkLabel _seeAll;
    private readonly Panel _headingRow;
    private readonly Panel _rows;
    private readonly Panel _pager;
    private readonly LinkLabel _prev;
    private readonly LinkLabel _next;
    private readonly Label _pageLabel;

    /// <summary>Every task, of which the view shows a page.</summary>
    private IReadOnlyList<TaskEntry> _entries = [];

    /// <summary>Whether the band is showing more than its collapsed few.</summary>
    private bool _expanded;

    private int _page;

    /// <summary>Rows the window has room for; see <see cref="SetMaxVisibleRows"/>.</summary>
    private int _maxVisibleRows = PageSize;

    /// <summary>Rows on one expanded page, never more than the window allows.</summary>
    private int EffectivePageSize => Math.Min(PageSize, _maxVisibleRows);

    /// <summary>
    /// Explains what an action does when its label cannot.
    /// </summary>
    /// <remarks>
    /// "Ignore" next to unfinished work reads like "throw it away", and the row
    /// is the only place to say otherwise. The shortened path needs one too:
    /// the row shows the tail, and the whole point is that the rest is still
    /// available without leaving the page.
    /// </remarks>
    private readonly ToolTip _tips = new();

    /// <summary>
    /// Raised when the band wants a different height.
    /// </summary>
    /// <remarks>
    /// Expanding and paging change how much room the band needs, and the row
    /// that holds it is sized by the form. Without this the band would grow its
    /// contents inside a height that never changed, and clip them.
    /// </remarks>
    public event Action? LayoutChanged;

    /// <summary>Rows currently displayed, for measuring the band.</summary>
    public int RowCount { get; private set; }

    /// <summary>
    /// Height this band needs, in design px before DPI scaling.
    /// </summary>
    /// <remarks>
    /// Computed here rather than by the form: the band is the only thing that
    /// knows whether it is expanded and whether a pager is showing.
    /// </remarks>
    public float DesiredHeight => RowCount == 0
        ? 0f
        : HeadingHeight
          + RowCount * (RowHeight + RowGap)
          + (_pager.Visible ? PagerHeight : 0)
          + Theme.Space2;

    public TaskBoard()
    {
        Dock = DockStyle.Fill;
        Margin = new Padding(0);
        Padding = new Padding(Theme.Space4, 0, Theme.Space4, Theme.Space1);

        _summary = new Label
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            Font = Theme.FontSmall,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _seeAll = new LinkLabel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            Font = Theme.FontSmall,
            TextAlign = ContentAlignment.MiddleRight,
            Text = Loc.T("board.seeAll"),
        };
        // Expanding happens here rather than in a dropdown: sending someone to
        // another surface to see the rest of a list they are already looking at
        // is a detour, not a disclosure.
        _seeAll.LinkClicked += (_, _) =>
        {
            _expanded = !_expanded;
            _page = 0;
            Render();
            LayoutChanged?.Invoke();
        };

        _headingRow = new Panel { Dock = DockStyle.Top, Height = HeadingHeight };
        _headingRow.Controls.Add(_summary);
        _headingRow.Controls.Add(_seeAll);

        _prev = new LinkLabel { AutoSize = true, Font = Theme.FontSmall, Text = Loc.T("board.page.prev") };
        _next = new LinkLabel { AutoSize = true, Font = Theme.FontSmall, Text = Loc.T("board.page.next") };
        _pageLabel = new Label
        {
            AutoSize = true,
            Font = Theme.FontSmall,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(Theme.Space3, 3, Theme.Space3, 0),
        };
        _prev.LinkClicked += (_, _) => TurnPage(-1);
        _next.LinkClicked += (_, _) => TurnPage(1);

        var pagerFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0),
            Padding = new Padding(Theme.Space2, 0, 0, 0),
        };
        pagerFlow.Controls.Add(_prev);
        pagerFlow.Controls.Add(_pageLabel);
        pagerFlow.Controls.Add(_next);

        _pager = new Panel { Dock = DockStyle.Bottom, Height = PagerHeight, Visible = false };
        _pager.Controls.Add(pagerFlow);

        _rows = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0) };

        Controls.Add(_rows);
        Controls.Add(_pager);
        Controls.Add(_headingRow);
    }

    private void TurnPage(int delta)
    {
        int pages = PageCount;
        _page = Math.Clamp(_page + delta, 0, Math.Max(0, pages - 1));
        Render();
        LayoutChanged?.Invoke();
    }

    private int PageCount =>
        _entries.Count == 0 ? 1 : (int)Math.Ceiling(_entries.Count / (double)EffectivePageSize);

    // ── test seams ───────────────────────────────────────────────────────
    // The expand and page controls are links inside the band; driving them
    // through a click would test WinForms, not the paging rules.

    internal bool IsExpandedForTest => _expanded;

    internal void ToggleForTest()
    {
        _expanded = !_expanded;
        _page = 0;
        Render();
    }

    internal void TurnPageForTest(int delta) => TurnPage(delta);

    /// <summary>
    /// Cap the rows shown, so the band always fits the room it is given.
    /// </summary>
    /// <remarks>
    /// Paging to fit rather than scrolling to fit. A scrolling band clipped its
    /// last row in half and, on Windows, brought a native scrollbar the theme
    /// cannot recolour — a bright white stripe down a dark window. Showing
    /// fewer rows costs nothing: the pager already exists to reach the rest.
    /// </remarks>
    public void SetMaxVisibleRows(int rows)
    {
        int capped = Math.Max(1, rows);
        if (capped == _maxVisibleRows) return;
        _maxVisibleRows = capped;
        _page = Math.Clamp(_page, 0, Math.Max(0, PageCount - 1));
        Render();
    }

    /// <summary>
    /// Rows that fit in <paramref name="designPx"/> of band height.
    /// </summary>
    /// <remarks>
    /// The pager's height is always reserved, so expanding a list that turns out
    /// to need paging does not reflow everything below it.
    /// </remarks>
    public static int RowsThatFit(float designPx)
    {
        float forRows = designPx - HeadingHeight - PagerHeight - Theme.Space2;
        return Math.Max(1, (int)(forRows / (RowHeight + RowGap)));
    }

    /// <summary>Repaint the board from the current set of tasks.</summary>
    public void Show(IReadOnlyList<TaskEntry> entries)
    {
        _entries = entries;
        // A refresh can shorten the list under a reader who is on the last page.
        _page = Math.Clamp(_page, 0, Math.Max(0, PageCount - 1));
        if (entries.Count <= MaxRows) _expanded = false;
        Render();
    }

    private void Render()
    {
        var entries = _entries;
        _rows.SuspendLayout();
        foreach (Control c in _rows.Controls) c.Dispose();
        _rows.Controls.Clear();

        var shown = _expanded
            ? entries.Skip(_page * EffectivePageSize).Take(EffectivePageSize).ToList()
            // Even collapsed, never claim more rows than the window can show —
            // a clipped half-row reads as a rendering fault.
            : entries.Take(Math.Min(MaxRows, _maxVisibleRows)).ToList();
        RowCount = shown.Count;

        // Docked top-down, so added in reverse: the last control docked to the
        // top ends up highest.
        foreach (var entry in Enumerable.Reverse(shown))
        {
            // Each row sits in a holder whose bottom padding shows the page
            // background through: docked rows stack flush, and three surfaces
            // touching edge to edge read as one block rather than three tasks.
            var holder = new Panel
            {
                Dock = DockStyle.Top,
                Height = (int)Scaled(RowHeight + RowGap),
                Padding = new Padding(0, 0, 0, (int)Scaled(RowGap)),
                Margin = new Padding(0),
            };
            var row = BuildRow(entry);
            row.Dock = DockStyle.Fill;
            holder.Controls.Add(row);
            _rows.Controls.Add(holder);
        }

        _summary.Text = SummaryText(entries);
        // The link is offered whenever collapsing would hide something, and
        // stays offered once expanded so there is a way back.
        _seeAll.Visible = _expanded || entries.Count > MaxRows;
        _seeAll.Text = _expanded
            ? Loc.T("board.seeAll.collapse")
            : Loc.T("board.seeAll.count", entries.Count);
        _headingRow.Height = (int)Scaled(HeadingHeight);

        int pages = PageCount;
        _pager.Visible = _expanded && pages > 1;
        if (_pager.Visible)
        {
            _pager.Height = (int)Scaled(PagerHeight);
            _pageLabel.Text = Loc.T("board.page.of", _page + 1, pages);
            _prev.Enabled = _page > 0;
            _next.Enabled = _page < pages - 1;
        }

        _rows.ResumeLayout();
        ApplyTheme();
    }

    /// <summary>
    /// One line saying how many tasks there are and what they are doing.
    /// </summary>
    /// <remarks>
    /// A count per state rather than a total plus "N more elsewhere": the old
    /// wording made people ask whether the extras were additional tasks or the
    /// same ones counted twice. Only non-zero states appear, so a quiet board
    /// stays short.
    /// </remarks>
    private static string SummaryText(IReadOnlyList<TaskEntry> entries)
    {
        var parts = new List<string> { Loc.T("board.summary.total", entries.Count) };
        foreach (var state in new[]
        {
            TaskState.AwaitingPermission, TaskState.NeedsAttention, TaskState.Failed,
            TaskState.Running, TaskState.Interrupted, TaskState.Cancelled,
        })
        {
            int n = entries.Count(e => e.State == state);
            if (n > 0) parts.Add($"{Glyph(state)} {n} {Label(state)}");
        }
        return string.Join("    ", parts);
    }

    private float Scaled(float designPx) => designPx * (DeviceDpi / 96f);

    /// <summary>
    /// The tail of a path, which is the part that identifies it.
    /// </summary>
    /// <remarks>
    /// A full Windows path is mostly prefix nobody reads and would push the
    /// account and status off the row. The whole path stays one hover away.
    /// </remarks>
    public static string ShortPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var parts = path.TrimEnd('\\', '/').Split('\\', '/');
        if (parts.Length <= 2) return path;
        return "…\\" + string.Join('\\', parts[^2..]);
    }

    private Control BuildRow(TaskEntry entry)
    {
        // Second line: who is paying for it, where it is, and how it is going.
        var context = new List<string>();
        if (entry.Account is { Length: > 0 }) context.Add(entry.Account);
        if (entry.Cwd.Length > 0) context.Add(ShortPath(entry.Cwd));
        context.Add(entry.Elapsed.Length > 0
            ? $"{Label(entry.State)} · {entry.Elapsed}"
            : Label(entry.State));
        if (entry.Note is { Length: > 0 } note)
        {
            context.Add(note.Length <= MaxNoteChars
                ? note
                : string.Concat(note.AsSpan(0, MaxNoteChars - 1), "…"));
        }

        var row = new TaskRow(entry, StateColor(entry.State))
        {
            Detail = string.Join("  ·  ", context),
            Tag = entry.State,
        };
        row.Click += (_, _) => entry.Primary();

        // The row shows a shortened path and a clipped note; the tooltip is
        // where the full versions stay reachable without leaving the page.
        var full = new List<string>();
        if (entry.Cwd.Length > 0) full.Add(entry.Cwd);
        if (entry.Note is { Length: > 0 } fullNote) full.Add(fullNote);
        if (full.Count > 0) _tips.SetToolTip(row, string.Join(Environment.NewLine, full));

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };

        void Add(Control c) => actions.Controls.Add(c);

        var primary = new PillButton(entry.PrimaryLabel) { Emphasis = true, Margin = new Padding(0) };
        primary.Click += (_, _) => entry.Primary();
        Add(primary);

        if (entry.Stop is { } stop)
        {
            var stopButton = new PillButton(Loc.T("board.stop"))
            {
                Margin = new Padding(Theme.Space1, 0, 0, 0),
            };
            stopButton.Click += (_, _) => stop();
            _tips.SetToolTip(stopButton, Loc.T("board.stop.tip"));
            Add(stopButton);
        }

        // Laid out rather than hidden behind an overflow: there are at most two
        // of them, the row has the width, and a menu that has to be opened to
        // find out what is in it is worse than two visible words.
        if (entry.OpenFolder is { } openFolder)
        {
            var b = new PillButton(Loc.T("board.action.folder"))
            {
                Margin = new Padding(Theme.Space1, 0, 0, 0),
            };
            b.Click += (_, _) => openFolder();
            Add(b);
        }
        if (entry.Ignore is { } ignore)
        {
            var b = new PillButton(Loc.T("board.action.remove"))
            {
                Margin = new Padding(Theme.Space1, 0, 0, 0),
            };
            b.Click += (_, _) => ignore();
            _tips.SetToolTip(b, Loc.T("board.ignore.tip"));
            Add(b);
        }

        row.Controls.Add(actions);
        row.RefreshSurfaces();
        // The painted row needs to know how much room the buttons take so its
        // text ellipsises before running under them.
        row.Layout += (_, _) => PlaceActions(row, actions);
        row.SizeChanged += (_, _) => PlaceActions(row, actions);
        PlaceActions(row, actions);

        return row;
    }

    /// <summary>Right-align the actions inside a painted row and tell it their width.</summary>
    private static void PlaceActions(TaskRow row, Control actions)
    {
        int margin = (int)Math.Round(10 * row.DeviceDpi / 96.0);
        var size = actions.PreferredSize;
        actions.Bounds = new Rectangle(
            Math.Max(0, row.Width - size.Width - margin),
            Math.Max(0, (row.Height - size.Height) / 2),
            size.Width,
            size.Height);
        if (row.ActionsWidth != size.Width + margin)
        {
            row.ActionsWidth = size.Width + margin;
            row.Invalidate();
        }
    }

    /// <summary>
    /// The mark for a state in the summary line.
    /// </summary>
    /// <remarks>
    /// Only the summary uses one now — a row carries its state on its painted
    /// edge, which does not compete with the text the way a glyph in front of
    /// the title did.
    /// </remarks>
    private static string Glyph(TaskState state) => state switch
    {
        TaskState.Running => "●",
        TaskState.AwaitingPermission => "◆",
        TaskState.NeedsAttention or TaskState.Failed => "▲",
        _ => "○",
    };

    private static string Label(TaskState state) => Loc.T($"board.state.{state switch
    {
        TaskState.Running => "running",
        TaskState.AwaitingPermission => "awaitingPermission",
        TaskState.NeedsAttention => "needsAttention",
        TaskState.Failed => "failed",
        TaskState.Interrupted => "interrupted",
        _ => "cancelled",
    }}");

    /// <summary>
    /// Colour by what it means, not by what it is.
    /// </summary>
    /// <remarks>
    /// Green only for work that is genuinely progressing; orange for anything
    /// that wants a person; grey for endings. A stopped-by-choice run is not a
    /// problem and must not compete for attention with one that broke.
    /// </remarks>
    private static Color StateColor(TaskState state) => state switch
    {
        TaskState.Running => Theme.Success,
        TaskState.AwaitingPermission or TaskState.NeedsAttention => Theme.Warning,
        TaskState.Failed => Theme.Danger,
        _ => Theme.TextMuted,
    };

    protected override void Dispose(bool disposing)
    {
        // A ToolTip is a component, not a child control, so the base sweep does
        // not reach it.
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Re-colour after a theme change.</summary>
    public void ApplyTheme()
    {
        BackColor = Theme.BgApp;
        _headingRow.BackColor = Theme.BgApp;
        _summary.BackColor = Theme.BgApp;
        _summary.ForeColor = Theme.TextMuted;
        _seeAll.BackColor = Theme.BgApp;
        _seeAll.LinkColor = Theme.Primary;
        _seeAll.ActiveLinkColor = Theme.PrimaryDark;
        _rows.BackColor = Theme.BgApp;

        _pager.BackColor = Theme.BgApp;
        _pageLabel.BackColor = Theme.BgApp;
        _pageLabel.ForeColor = Theme.TextMuted;
        foreach (var link in new[] { _prev, _next })
        {
            link.BackColor = Theme.BgApp;
            link.LinkColor = Theme.Primary;
            link.ActiveLinkColor = Theme.PrimaryDark;
            link.DisabledLinkColor = Theme.TextDisabled;
        }
        foreach (Control c in _pager.Controls) c.BackColor = Theme.BgApp;

        // The rows paint themselves from the palette, so a theme change only has
        // to invalidate them.
        foreach (Control holder in _rows.Controls)
        {
            // The holder's padding is the gap between rows, so it has to show
            // the page colour rather than the row's.
            holder.BackColor = Theme.BgApp;
            foreach (Control row in holder.Controls)
            {
                // The pills cache the shade they sit on, so a palette change has
                // to hand them the new one.
                if (row is TaskRow painted) painted.RefreshSurfaces();
                else row.Invalidate(true);
            }
        }
    }
}
