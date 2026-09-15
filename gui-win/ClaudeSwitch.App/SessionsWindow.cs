using System.Diagnostics;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// Every conversation that can be picked up again, in one list.
/// </summary>
/// <remarks>
/// <para>
/// The home page shows the last few. Its "see all" used to expand that band in
/// place and page through the rest between the account cards: no room to
/// search, sort or clear out a few hundred conversations, and it pushed the
/// accounts the page is for out of sight. The whole list lives here instead.
/// </para>
/// <para>
/// Per conversation and newest first, like the band. The directory window is
/// still where history is handled directory by directory.
/// </para>
/// </remarks>
internal sealed class SessionsWindow : Form
{
    /// <summary>How many conversations one listing reads.</summary>
    /// <remarks>
    /// Each costs a read of its transcript's head, done off the UI thread. A few
    /// hundred covers months of work; the hint says when the list stopped short.
    /// </remarks>
    internal const int Limit = 500;

    private readonly Engine _engine;
    private readonly Func<RecentSession, IWin32Window, bool> _resume;
    private readonly Func<int, string> _accountLabel;
    private readonly Action _openDirectories;
    private readonly Action _changed;

    private readonly ThemedListView _list = new();
    private readonly PrimaryButton _resumeButton = new();
    private readonly SecondaryButton _openDir = new();
    private readonly SecondaryButton _delete = new();
    private readonly SecondaryButton _byDirectory = new();
    private readonly SecondaryButton _refresh = new();
    private readonly SearchField _filter = new();
    private readonly Control? _filterClear;
    private readonly Label _title = new();
    private readonly Label _countChip = new();
    private readonly Label _hint = new();
    private readonly Panel _header = new();
    private readonly ToolTip _tip = new() { ShowAlways = true, AutoPopDelay = 12000 };

    /// <summary>Everything the last listing returned; the filter picks from it.</summary>
    private List<RecentSession> _all = [];

    private bool _loaded;
    private bool _loading;
    private DateTime _loadedUtc = DateTime.MinValue;

    /// <summary>Whether the first listing has landed; the layout probe waits for it.</summary>
    internal bool IsLoadedForProbe => _loaded;

    /// <summary>
    /// Outcome of the last action, kept in the hint band until the next one.
    /// </summary>
    /// <remarks>
    /// Closing a confirmation dialog activates the window, and activation
    /// re-lists it; a message written straight into the band would be replaced
    /// before anyone read it.
    /// </remarks>
    private string? _statusMessage;

    /// <param name="engine">Where conversations are listed and deleted.</param>
    /// <param name="resume">
    /// Continues one conversation; errors are shown over the given window.
    /// Returns whether a terminal was started.
    /// </param>
    /// <param name="accountLabel">How an account is named, masked as the main window masks it.</param>
    /// <param name="openDirectories">Opens the directory window.</param>
    /// <param name="changed">Tells the home page its recent list may be stale.</param>
    public SessionsWindow(
        Engine engine,
        Func<RecentSession, IWin32Window, bool> resume,
        Func<int, string> accountLabel,
        Action openDirectories,
        Action changed)
    {
        _engine = engine;
        _resume = resume;
        _accountLabel = accountLabel;
        _openDirectories = openDirectories;
        _changed = changed;

        Text = Loc.T("sessions.title");
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.FontBody;
        ShowIcon = false;
        MinimizeBox = false;
        KeyPreview = true;

        // The title and the directory share what is left; the rest are sized to
        // their longest realistic value, as in the directory window.
        ProjectsWindow.ConfigureList(
            _list,
            (Loc.T("proj.col.session"), -3), (Loc.T("proj.col.dir"), -2),
            (Loc.T("proj.col.account"), 118), (Loc.T("proj.col.updated"), 104),
            (Loc.T("proj.col.size"), 74));
        _list.RightAlignedColumns.Add(4);
        _list.Sortable = true;
        _list.MultiSelect = true;
        _list.ShowItemToolTips = true;
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => ResumeSelected();
        _list.KeyDown += (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Delete:
                    e.Handled = true;
                    DeleteSelected();
                    break;
                case Keys.Enter:
                    e.Handled = true;
                    ResumeSelected();
                    break;
            }
        };
        _list.ContextMenuStrip = BuildMenu();

        _resumeButton.Text = Loc.T("proj.resume");
        _resumeButton.Click += (_, _) => ResumeSelected();
        _tip.SetToolTip(_resumeButton, Loc.T("proj.resume.tip"));
        _openDir.Text = Loc.T("proj.openDir");
        _openDir.Click += (_, _) => OpenSelectedDirectory();
        _delete.Text = Loc.T("history.button.delete");
        _delete.Click += (_, _) => DeleteSelected();
        _tip.SetToolTip(_delete, Loc.T("history.button.delete.tip"));
        _byDirectory.Text = Loc.T("sessions.byDirectory");
        _byDirectory.Click += (_, _) => _openDirectories();
        _tip.SetToolTip(_byDirectory, Loc.T("sessions.byDirectory.tip"));
        _refresh.Text = Loc.T("toolbar.refresh");
        _refresh.Click += (_, _) => Reload(force: true);

        SearchBox.AttachCueBanner(_filter.Box, Loc.T("sessions.filter.hint"));
        _filterClear = SearchBox.AttachInlineClear(
            _filter.Box, Loc.T("sessions.filter.clear"), () => _filter.Box.Text = "");
        _filter.Box.TextChanged += (_, _) =>
        {
            if (_filterClear is not null) _filterClear.Visible = _filter.Box.Text.Length > 0;
            _statusMessage = null;
            Populate();
        };
        _filter.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && _filter.Box.Text.Length > 0)
            {
                _filter.Box.Text = "";
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Down && _list.Items.Count > 0)
            {
                // From the query straight into the results.
                _list.Focus();
                if (_list.SelectedItems.Count == 0) _list.Items[0].Selected = true;
                e.Handled = true;
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.F)
            {
                _filter.Box.Focus();
                _filter.Box.SelectAll();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F5)
            {
                Reload(force: true);
                e.Handled = true;
            }
        };

        // Sized by its buttons: a fixed band is a 96-DPI figure, and at 150% the
        // buttons alone are about that tall.
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
        };
        foreach (var b in new Control[] { _resumeButton, _openDir, _delete })
        {
            b.Margin = new Padding(0, 0, Theme.Space2, 0);
            actions.Controls.Add(b);
        }
        _byDirectory.Anchor = AnchorStyles.Right;
        _byDirectory.Margin = new Padding(0);
        var actionRow = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, Theme.Space2, 0, 0),
            Margin = new Padding(0),
        };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.Controls.Add(actions, 0, 0);
        actionRow.Controls.Add(_byDirectory, 1, 0);

        // The list sits inside a rounded surface, like the account cards.
        var card = new CardPanel { Dock = DockStyle.Fill };
        card.Controls.Add(_list);
        var body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Theme.Space4, Theme.Space3, Theme.Space4, Theme.Space3),
        };
        body.Controls.Add(card);
        body.Controls.Add(actionRow);
        card.BringToFront();

        // ── Header band, mirroring the directory window's ──
        _title.Text = Loc.T("sessions.title");
        _title.Font = Theme.FontBrand;
        _title.AutoSize = true;
        _title.Location = new Point(Theme.Space4, Theme.Space3);

        _countChip.AutoSize = true;
        _countChip.Font = Theme.FontSmall;
        _countChip.Padding = new Padding(Theme.Space2, Theme.Space1, Theme.Space2, Theme.Space1);
        _countChip.Location = new Point(Theme.Space4, Theme.Space3 + Theme.FontBrand.Height + 4);
        _countChip.Visible = false;

        _header.Dock = DockStyle.Top;
        // Measured from its own fonts: a fixed band clips at >100% scaling.
        _header.Height = Theme.FontBrand.Height + Theme.FontSmall.Height + Theme.Space6;
        _header.Controls.AddRange([_title, _countChip, _filter, _refresh]);
        _header.Resize += (_, _) => PlaceHeaderControls();

        _hint.Dock = DockStyle.Bottom;
        _hint.Height = Theme.FontCaption.Height + Theme.Space4;
        _hint.TextAlign = ContentAlignment.MiddleLeft;
        _hint.Padding = new Padding(Theme.Space4, 0, Theme.Space4, 0);
        _hint.Font = Theme.FontCaption;
        _hint.AutoEllipsis = true;

        Controls.Add(body);
        Controls.Add(_header);
        Controls.Add(_hint);

        ApplyTheme();
        Theme.Changed += OnThemeChanged;
        Load += (_, _) =>
        {
            // Sized here, not in the constructor: these are 96-DPI design figures.
            MinimumSize = new Size(Theme.Scale(this, 760), Theme.Scale(this, 440));
            Size = new Size(Theme.Scale(this, 1080), Theme.Scale(this, 660));
            PlaceHeaderControls();
            UpdateButtons();
            UpdateHint(0, "");
            Reload(force: true);
        };
        // Conversations are written by terminals this window does not own, so
        // coming back to it is when the list is most likely stale.
        Activated += (_, _) =>
        {
            if (_loaded) Reload(force: false);
        };
        // Open ready to type: finding one conversation is the common reason to be here.
        Shown += (_, _) => _filter.Box.Focus();
    }

    /// <summary>Filter box and refresh on the right of the header, centred on the band.</summary>
    private void PlaceHeaderControls()
    {
        int Middle(Control c) => Math.Max(0, (_header.ClientSize.Height - c.Height) / 2);

        int right = _header.ClientSize.Width - Theme.Space4;
        _refresh.Width = _refresh.GetPreferredSize(Size.Empty).Width;
        right -= _refresh.Width;
        _refresh.Location = new Point(right, Middle(_refresh));
        right -= Theme.Space2;

        // As tall as the button beside it, the way the main toolbar's box is.
        _filter.Height = _refresh.Height;
        _filter.Width = Theme.Scale(this, 280);
        right -= _filter.Width;
        _filter.Location = new Point(Math.Max(_title.Right + Theme.Space4, right), Middle(_filter));
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new SageToolStripRenderer() };
        var resume = new ToolStripMenuItem(Loc.T("proj.menu.resume"), null, (_, _) => ResumeSelected());
        var folder = new ToolStripMenuItem(Loc.T("proj.openDir"), null, (_, _) => OpenSelectedDirectory());
        var copy = new ToolStripMenuItem(Loc.T("proj.menu.copyId"), null, (_, _) =>
        {
            if (Selected is [var one]) Clipboard.SetText(one.SessionId);
        });
        var delete = new ToolStripMenuItem(
            Loc.T("history.menu.deleteSessions"), null, (_, _) => DeleteSelected())
        {
            Tag = "danger",
        };
        menu.Items.AddRange([resume, folder, copy, new ToolStripSeparator(), delete]);
        menu.Opening += (_, e) =>
        {
            var picked = Selected;
            if (picked.Count == 0)
            {
                e.Cancel = true;
                return;
            }
            // Continuing, opening and copying are about one conversation;
            // deleting works on the whole selection.
            resume.Enabled = CanResume(picked);
            folder.Enabled = picked is [var one] && Directory.Exists(one.Path);
            copy.Enabled = picked.Count == 1;
        };
        return menu;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        ApplyTheme();
        Invalidate(true);
    }

    /// <summary>Buttons and the list theme themselves; this covers the chrome around them.</summary>
    private void ApplyTheme()
    {
        BackColor = Theme.BgApp;
        ForeColor = Theme.TextPrimary;
        _header.BackColor = Theme.BgHeader;
        _title.ForeColor = Theme.TextPrimary;
        _countChip.BackColor = Theme.PrimarySoft;
        _countChip.ForeColor = Theme.PrimaryDark;
        _hint.ForeColor = Theme.TextMuted;
        _hint.BackColor = Theme.BgHeader;
        // The outline's rounded corners show the band behind them.
        _filter.BackColor = Theme.BgHeader;
        _filter.ApplyTheme();
    }

    private List<RecentSession> Selected =>
        _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<RecentSession>().ToList();

    /// <summary>One conversation, not running, whose directory still exists.</summary>
    private static bool CanResume(List<RecentSession> picked) =>
        picked is [{ Live: false } one] && Directory.Exists(one.Path);

    private void UpdateButtons()
    {
        var picked = Selected;
        _resumeButton.Enabled = CanResume(picked);
        _openDir.Enabled = picked is [var one] && Directory.Exists(one.Path);
        _delete.Enabled = picked.Count > 0;
    }

    /// <summary>Re-read the list on a worker thread; a recent read is reused unless forced.</summary>
    private void Reload(bool force)
    {
        if (_loading || IsDisposed) return;
        if (!force && DateTime.UtcNow - _loadedUtc < TimeSpan.FromSeconds(5)) return;

        _loading = true;
        _refresh.Enabled = false;
        var engine = _engine;
        _ = Task.Run(() =>
        {
            try
            {
                var node = engine.Call("recent_sessions", new { limit = Limit });
                return (Sessions: RecentSessions.Parse(node, Limit), Error: (string?)null);
            }
            catch (Exception ex) when (ex is EngineException or ObjectDisposedException or InvalidOperationException)
            {
                return (Sessions: new List<RecentSession>(), Error: (string?)ex.Message);
            }
        }).ContinueWith(
            done =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(() => OnLoaded(done.Result.Sessions, done.Result.Error));
                }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
                {
                    // Closed while the list was being read.
                }
            },
            TaskScheduler.Default);
    }

    private void OnLoaded(List<RecentSession> sessions, string? error)
    {
        _loading = false;
        if (IsDisposed) return;
        _refresh.Enabled = true;

        if (error is not null)
        {
            // Keep what is on screen: a failed refresh is not an empty history.
            _statusMessage = Loc.T("proj.sessionsFailed", error);
            UpdateHint(_list.Items.Count, _filter.Box.Text.Trim());
            return;
        }
        _all = sessions;
        _loaded = true;
        _loadedUtc = DateTime.UtcNow;
        Populate();
    }

    /// <summary>Show what matches the filter, keeping the selection across a refresh.</summary>
    private void Populate()
    {
        string query = _filter.Box.Text.Trim();
        var keep = Selected.Select(s => s.SessionId).ToHashSet();
        int shown = 0;

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var session in _all)
            {
                string account = AccountText(session);
                if (!Matches(session, account, query)) continue;
                var item = BuildItem(session, account);
                _list.Items.Add(item);
                item.Selected = keep.Contains(session.SessionId);
                shown++;
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _countChip.Text = Loc.T("sessions.count", _all.Count);
        _countChip.Visible = _loaded;
        UpdateButtons();
        UpdateHint(shown, query);
    }

    private static ListViewItem BuildItem(RecentSession session, string account)
    {
        string title = DisplayTitle(session);
        // A prefix, not a suffix: a long title is cut with an ellipsis, and the
        // one word that says "do not continue this here" went with it.
        var item = new ListViewItem(session.Live ? Loc.T("proj.session.live") + title : title)
        {
            Tag = session,
        };
        // The tail of the path, which is the part that tells two "repo"s apart.
        item.SubItems.Add(TaskBoard.ShortPath(session.Path));
        item.SubItems.Add(account);
        item.SubItems.Add(new ListViewItem.ListViewSubItem(
            item, Theme.FormatCompactEpoch(session.LastActiveMs) ?? "") { Tag = session.LastActiveMs ?? 0 });
        item.SubItems.Add(new ListViewItem.ListViewSubItem(
            item, session.Bytes > 0 ? HistoryCleanup.FormatBytes(session.Bytes) : "") { Tag = session.Bytes });

        var tip = new List<string>();
        if (!string.IsNullOrWhiteSpace(session.Title)) tip.Add(session.Title!.Trim());
        tip.Add(session.Live
            ? Loc.T("proj.session.live.tip", session.SessionId)
            : Loc.T("proj.sessionId", session.SessionId));
        tip.Add(session.Path);
        item.ToolTipText = string.Join(Environment.NewLine, tip);
        return item;
    }

    private static string DisplayTitle(RecentSession session) =>
        string.IsNullOrWhiteSpace(session.Title) ? session.Name : session.Title!.Trim();

    private string AccountText(RecentSession session) =>
        session.ProfileNumber is { } number
            ? _accountLabel(number)
            : Loc.T("sessions.account.default");

    /// <summary>
    /// Whether a row answers the search box.
    /// </summary>
    /// <remarks>
    /// Everything the row shows can be searched for, plus the session id, which
    /// is what a terminal prints and what gets pasted from one.
    /// </remarks>
    internal static bool Matches(RecentSession session, string account, string query)
    {
        string q = query.Trim();
        if (q.Length == 0) return true;
        foreach (var field in new[] { session.Title, session.Name, session.Path, account, session.SessionId })
        {
            if (field?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return true;
        }
        return false;
    }

    private void UpdateHint(int shown, string query)
    {
        _hint.Text = _statusMessage
            ?? (!_loaded ? Loc.T("sessions.loading")
                : _all.Count == 0 ? Loc.T("sessions.empty")
                : query.Length > 0 && shown == 0 ? Loc.T("sessions.noMatch", query)
                : query.Length > 0 ? Loc.T("sessions.filtered", shown, _all.Count)
                : _all.Count >= Limit ? Loc.T("sessions.capped", Limit)
                : Loc.T("sessions.hint"));
    }

    private void ResumeSelected()
    {
        var picked = Selected;
        if (!CanResume(picked)) return;
        var session = picked[0];
        if (!_resume(session, this)) return;

        _statusMessage = Loc.T("sessions.resumed", DisplayTitle(session));
        UpdateHint(_list.Items.Count, _filter.Box.Text.Trim());
        _changed();
    }

    private void OpenSelectedDirectory()
    {
        if (Selected is not [var session]) return;
        if (!Directory.Exists(session.Path))
        {
            MessageBox.Show(
                this, Loc.T("acp.err.workDir", session.Path), Loc.T("proj.openFailed"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{session.Path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, Loc.T("proj.openFailed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Plan, confirm, apply, report — the path every deletion in the app takes.</summary>
    private void DeleteSelected()
    {
        var picked = Selected
            .Where(s => !string.IsNullOrEmpty(s.File))
            .Select(s => (TranscriptDir: Path.GetDirectoryName(s.File!) ?? "", Id: s.SessionId))
            .Where(p => p.TranscriptDir.Length > 0)
            .ToList();
        if (picked.Count == 0) return;

        var request = HistoryCleanup.ForSessions(picked);
        try
        {
            var plan = HistoryCleanup.Plan(_engine, request);
            if (!HistoryDeleteDialog.Confirm(this, plan)) return;

            var outcome = HistoryCleanup.Apply(_engine, request, plan.DeletableIds);
            _statusMessage = Loc.T(
                "history.delete.done", outcome.DeletedCount, HistoryCleanup.FormatBytes(outcome.FreedBytes));
            // Off the list now rather than after the re-read lands; the re-read
            // puts back anything that could not be deleted after all.
            var deleted = plan.DeletableIds.ToHashSet();
            _all.RemoveAll(s => deleted.Contains(s.SessionId));
            Populate();
            Reload(force: true);
            _changed();
            HistoryDeleteDialog.ReportFailures(this, outcome);
        }
        catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
        {
            MessageBox.Show(
                this,
                Loc.T("history.delete.failed", ex.Message),
                Loc.T("history.delete.title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// A text box inside the app's rounded outline, for use off a tool strip.
/// </summary>
/// <remarks>
/// The native border is square and ignores the theme — the one hard edge on a
/// window of rounded surfaces, which is why the toolbar's search box paints its
/// own. The edit control is only as tall as its text, so it is centred in the
/// outline, and a click anywhere in the outline lands in it.
/// </remarks>
internal sealed class SearchField : Panel
{
    public SearchField()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.IBeam;
        Box = new TextBox { BorderStyle = BorderStyle.None, Font = Theme.FontBody };
        Controls.Add(Box);
        // The outline's colour follows focus, and the box does not own the outline.
        Box.Enter += (_, _) => Invalidate();
        Box.Leave += (_, _) => Invalidate();
        MouseDown += (_, _) => Box.Focus();
    }

    public TextBox Box { get; }

    /// <summary>Colour the edit area like the outline's fill.</summary>
    public void ApplyTheme()
    {
        Box.BackColor = Theme.BgSurface;
        Box.ForeColor = Theme.TextPrimary;
        // The inline ✕ sits on the same surface.
        foreach (Control child in Box.Controls) child.BackColor = Theme.BgSurface;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int left = Theme.Space3;
        int right = Theme.Space1;
        Box.Width = Math.Max(0, ClientSize.Width - left - right);
        Box.Location = new Point(left, Math.Max(0, (ClientSize.Height - Box.Height) / 2));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        SageToolStripRenderer.PaintTextBoxSurround(
            e.Graphics,
            new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1),
            Box.Focused);
    }
}
