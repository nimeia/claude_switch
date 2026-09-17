using System.Diagnostics;
using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// Directories Claude Code has worked in, and the sessions inside each.
///
/// Deliberately not tied to an account: `projects` lives in the global config
/// that a switch leaves alone, and transcripts record no account identity, so
/// these directories belong to the machine rather than to a managed slot.
///
/// Totals are behind a button. Listing reads only file headers (~10 ms); a
/// cumulative scan reads every transcript byte, which for one busy directory
/// here is ~50 MB — not something to spend on every open for a number most
/// people never look at.
/// </summary>
internal sealed class ProjectsWindow : Form
{
    private readonly Engine _engine;
    private readonly ThemedListView _dirs = new();
    private readonly ThemedListView _sessions = new();
    private readonly PrimaryButton _resume = new();
    private readonly SecondaryButton _openDir = new();
    private readonly SecondaryButton _stats = new();
    private readonly SecondaryButton _deleteSessions = new();
    private readonly SecondaryButton _emptyDirs = new();
    private readonly SecondaryButton _cleanup = new();
    private readonly Label _statsText = new();
    private readonly Label _hint = new();
    private readonly Label _title = new();
    private readonly Label _countChip = new();
    private readonly Label _sessionsTitle = new();
    private readonly Panel _header = new();
    private readonly ToolTip _tip = new() { ShowAlways = true, AutoPopDelay = 12000 };

    private readonly SplitContainer _split;
    private List<ProjectRow> _rows = [];
    private bool _loaded;

    /// <summary>
    /// Outcome of the last cleanup, shown in the hint band until the next one.
    /// </summary>
    /// <remarks>
    /// The window re-lists itself whenever it is activated, and closing a
    /// confirmation dialog activates it — so a message written straight into the
    /// band was replaced by the account note before anyone could read it.
    /// </remarks>
    private string? _statusMessage;

    /// <summary>Keep the directory pane at ~58% — it carries the wider columns.</summary>
    private void ApplySplit()
    {
        if (_split.IsDisposed || _split.Width < 200) return;
        int want = (int)(_split.Width * 0.58);
        int min = _split.Panel1MinSize + 1;
        int max = _split.Width - _split.Panel2MinSize - _split.SplitterWidth - 1;
        if (max <= min) return;
        _split.SplitterDistance = Math.Clamp(want, min, max);
    }

    private sealed record ProjectRow(
        string Path,
        string Name,
        int SessionCount,
        long? LastActiveMs,
        string? LastPrompt,
        bool Registered,
        /// <summary>
        /// Every folder holding this directory's transcripts.
        ///
        /// More than one once session mode is used: the default profile and each
        /// session profile keep their own. Reading only the first would hide
        /// whole conversations from the list and undercount the totals.
        /// </summary>
        IReadOnlyList<string> TranscriptDirs,
        long TranscriptBytes,
        /// <summary>
        /// Transcripts plus subagent output and file snapshots — what cleaning
        /// the directory frees. Auto memory is not in it.
        /// </summary>
        long TotalBytes)
    {
        public bool HasTranscripts => TranscriptDirs.Count > 0;

        /// <summary>A row that exists only because Claude Code registered the directory.</summary>
        public bool IsEmpty => SessionCount == 0 && Registered;

        /// <summary>The figure the size column shows; an older engine reports only transcripts.</summary>
        public long SizeBytes => Math.Max(TotalBytes, TranscriptBytes);
    }

    private sealed record SessionRow(
        string Id,
        string? Title,
        long ModifiedMs,
        long? StartedMs,
        long Bytes,
        /// <summary>Folder the transcript sits in — how a deletion names the session.</summary>
        string TranscriptDir,
        /// <summary>
        /// The engine's verdict: a real conversation, nothing running it, and a
        /// config home that can still be launched.
        /// </summary>
        bool Resumable = true,
        bool HasConversation = true,
        bool Live = false,
        /// <summary>Session-mode profile holding the transcript; resuming must use it.</summary>
        int? ProfileNumber = null);

    public ProjectsWindow(Engine engine)
    {
        _engine = engine;
        Text = Loc.T("proj.title");
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.FontBody;
        ShowIcon = false;
        MinimizeBox = false;

        // SplitterDistance set here would be clamped against the not-yet-sized
        // container; applied on Load instead, once the real width is known.
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
        };
        _split = split;

        // One flexible column per list absorbs the leftover width; fixed columns
        // are sized to their longest realistic value. Fixed widths alone
        // overflowed the pane and cut off the right-hand columns entirely.
        // The path carries the name in its tail, so a separate name column spent
        // ~130px repeating it — and left both too narrow to read. Two different
        // directories here are in fact both called "repo".
        ConfigureList(
            _dirs,
            (Loc.T("proj.col.dir"), -1), (Loc.T("proj.col.account"), 118),
            (Loc.T("proj.col.sessions"), 48),
            (Loc.T("proj.col.lastActive"), 104), (Loc.T("proj.col.size"), 74));
        ConfigureList(
            _sessions,
            (Loc.T("proj.col.session"), -1), (Loc.T("proj.col.updated"), 104),
            (Loc.T("proj.col.size"), 74));
        // Counts and sizes are numbers; left-aligned they read as ragged text.
        _dirs.RightAlignedColumns.UnionWith([2, 4]);
        _sessions.RightAlignedColumns.Add(2);

        // Finding what takes the space is the first step of freeing it, and a
        // size column that cannot be sorted leaves that to the reader's eye.
        _dirs.Sortable = true;
        _sessions.Sortable = true;
        _sessions.MultiSelect = true;

        _dirs.ShowItemToolTips = true;
        _sessions.ShowItemToolTips = true;
        _dirs.SelectedIndexChanged += (_, _) => OnProjectSelected();
        _sessions.SelectedIndexChanged += (_, _) => UpdateButtons();
        _sessions.DoubleClick += (_, _) => ResumeSelected();
        _sessions.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete) return;
            e.Handled = true;
            DeleteSelectedSessions();
        };

        var sessionMenu = new ContextMenuStrip { Renderer = new SageToolStripRenderer() };
        var resumeItem = new ToolStripMenuItem(Loc.T("proj.menu.resume"), null, (_, _) => ResumeSelected());
        var copyItem = new ToolStripMenuItem(Loc.T("proj.menu.copyId"), null, (_, _) =>
        {
            if (SelectedSession is { } s) Clipboard.SetText(s.Id);
        });
        var deleteItem = new ToolStripMenuItem(
            Loc.T("history.menu.deleteSessions"), null, (_, _) => DeleteSelectedSessions())
        {
            Tag = "danger",
        };
        sessionMenu.Items.AddRange([resumeItem, copyItem, new ToolStripSeparator(), deleteItem]);
        sessionMenu.Opening += (_, e) =>
        {
            int picked = _sessions.SelectedItems.Count;
            if (picked == 0)
            {
                e.Cancel = true;
                return;
            }
            // Resuming and copying an id are about one conversation; deleting
            // works on the whole selection.
            resumeItem.Enabled = picked == 1;
            copyItem.Enabled = picked == 1;
        };
        _sessions.ContextMenuStrip = sessionMenu;

        // Binding lives on the directory, because "which account does this work
        // belong to" is a property of the project, not of one conversation.
        var dirMenu = new ContextMenuStrip { Renderer = new SageToolStripRenderer() };
        dirMenu.Opening += (_, e) =>
        {
            if (SelectedProject is null) { e.Cancel = true; return; }
            BuildDirectoryMenu(dirMenu);
        };
        // A menu with no items never opens, so seed one the Opening handler replaces.
        dirMenu.Items.Add(new ToolStripMenuItem("…") { Enabled = false });
        _dirs.ContextMenuStrip = dirMenu;

        // Each list sits inside a rounded surface, like the account cards.
        var dirsCard = new CardPanel { Dock = DockStyle.Fill };
        dirsCard.Controls.Add(_dirs);
        var dirsPane = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Theme.Space4, Theme.Space2, Theme.Space2, Theme.Space3),
        };
        dirsPane.Controls.Add(dirsCard);
        split.Panel1.Controls.Add(dirsPane);

        _resume.Text = Loc.T("proj.resume");
        _resume.Click += (_, _) => ResumeSelected();
        _tip.SetToolTip(_resume, Loc.T("proj.resume.tip"));

        _openDir.Text = Loc.T("proj.openDir");
        _openDir.Click += (_, _) => OpenSelectedDirectory();

        _stats.Text = Loc.T("proj.stats");
        _stats.Click += (_, _) => ComputeStats();
        _tip.SetToolTip(_stats, Loc.T("proj.stats.tip"));

        _deleteSessions.Text = Loc.T("history.button.delete");
        _deleteSessions.Click += (_, _) => DeleteSelectedSessions();
        _tip.SetToolTip(_deleteSessions, Loc.T("history.button.delete.tip"));

        // Stats text gets its own band: inside the button flow it competed with
        // them for width and pushed them off the panel.
        _statsText.Dock = DockStyle.Bottom;
        _statsText.Height = Theme.FontCaption.Height * 3 + Theme.Space2;
        _statsText.TextAlign = ContentAlignment.MiddleLeft;
        _statsText.Font = Theme.FontCaption;
        _statsText.Padding = new Padding(2, Theme.Space1, 2, 0);

        // Sized by its buttons: a fixed 46px was a 96-DPI figure, and at 150%
        // the buttons alone are about that tall, so their bottom edges were cut.
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, Theme.Space2, 0, 0),
        };
        foreach (var b in new Control[] { _resume, _openDir, _stats, _deleteSessions })
            b.Margin = new Padding(0, 0, Theme.Space2, 0);
        actions.Controls.AddRange([_resume, _openDir, _stats, _deleteSessions]);

        _sessionsTitle.Dock = DockStyle.Top;
        _sessionsTitle.Height = Theme.FontHeading.Height + Theme.Space2;
        _sessionsTitle.Font = Theme.FontHeading;
        _sessionsTitle.TextAlign = ContentAlignment.MiddleLeft;
        _sessionsTitle.Text = Loc.T("proj.sessions");

        var sessionsCard = new CardPanel { Dock = DockStyle.Fill };
        sessionsCard.Controls.Add(_sessions);

        var right = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Theme.Space2, Theme.Space2, Theme.Space4, Theme.Space3),
        };
        // Fill added last so the docked bands claim their height first.
        right.Controls.Add(_statsText);
        right.Controls.Add(actions);
        right.Controls.Add(sessionsCard);
        right.Controls.Add(_sessionsTitle);
        sessionsCard.BringToFront();
        split.Panel2.Controls.Add(right);

        // ── Header band, mirroring the main window's brand row ──
        _title.Text = Loc.T("proj.title");
        _title.Font = Theme.FontBrand;
        _title.AutoSize = true;
        _title.Location = new Point(Theme.Space4, Theme.Space3);

        _countChip.AutoSize = true;
        _countChip.Font = Theme.FontSmall;
        _countChip.Padding = new Padding(Theme.Space2, Theme.Space1, Theme.Space2, Theme.Space1);
        _countChip.Location = new Point(Theme.Space4, Theme.Space3 + Theme.FontBrand.Height + 4);

        _emptyDirs.Text = Loc.T("history.emptyDirs.button");
        _emptyDirs.Click += (_, _) => RemoveEmptyDirectories(null);

        _cleanup.Text = Loc.T("history.button.cleanup");
        _cleanup.Click += (_, _) => OpenCleanupDialog();
        _tip.SetToolTip(_cleanup, Loc.T("history.button.cleanup.tip"));

        _header.Dock = DockStyle.Top;
        // Measured from its own fonts: a fixed band clips at >100% scaling.
        _header.Height = Theme.FontBrand.Height + Theme.FontSmall.Height + Theme.Space6;
        _header.Controls.AddRange([_title, _countChip, _emptyDirs, _cleanup]);
        _header.Resize += (_, _) => PlaceHeaderButtons();

        _hint.Dock = DockStyle.Bottom;
        _hint.Height = Theme.FontCaption.Height + Theme.Space4;
        _hint.TextAlign = ContentAlignment.MiddleLeft;
        _hint.Padding = new Padding(Theme.Space4, 0, Theme.Space4, 0);
        _hint.Font = Theme.FontCaption;

        Controls.Add(split);
        Controls.Add(_header);
        Controls.Add(_hint);

        ApplyTheme();
        Theme.Changed += OnThemeChanged;
        Load += (_, _) =>
        {
            // Sized here, not in the constructor: these are 96-DPI design
            // figures, and at 125% scaling a fixed 1100px window leaves the
            // flexible columns squeezed down to their minimum.
            MinimumSize = new Size(Theme.Scale(this, 860), Theme.Scale(this, 500));
            Size = new Size(Theme.Scale(this, 1120), Theme.Scale(this, 660));
            ApplySplit();
            PlaceHeaderButtons();
            Reload();
        };
        Resize += (_, _) => ApplySplit();
        // The window outlives a close, so reopening it would otherwise show a
        // stale list. Re-listing costs ~10 ms, which is cheap enough to just do.
        Activated += (_, _) =>
        {
            if (_loaded) Reload();
        };
    }

    /// <summary>Right-align the header's buttons, centred on the band.</summary>
    private void PlaceHeaderButtons()
    {
        int right = _header.ClientSize.Width - Theme.Space4;
        foreach (var button in new ThemedButton[] { _cleanup, _emptyDirs })
        {
            button.Width = button.GetPreferredSize(Size.Empty).Width;
            right -= button.Width;
            button.Location = new Point(
                right,
                Math.Max(0, (_header.ClientSize.Height - button.Height) / 2));
            right -= Theme.Space2;
        }
    }

    /// <summary>
    /// Lay out a details list. A positive width is fixed; a negative one is a
    /// share of the leftover space (weight = |width|).
    /// </summary>
    /// <remarks>
    /// All-fixed widths overflowed the pane and cut the right-hand columns off
    /// entirely, and giving the leftover to a single column starved the others.
    /// </remarks>
    internal static void ConfigureList(ThemedListView list, params (string Header, int Width)[] columns)
    {
        list.Dock = DockStyle.Fill;
        foreach (var (header, width) in columns)
            list.Columns.Add(header, Math.Max(40, width));

        int weightTotal = columns.Where(c => c.Width < 0).Sum(c => -c.Width);

        void Fit()
        {
            if (list.IsDisposed || weightTotal == 0 || list.ClientSize.Width < 80) return;

            // Fixed widths are 96-DPI design values. Left unscaled they clip
            // their own headers and values at any display scaling above 100%.
            int fixedTotal = 0;
            for (int i = 0; i < columns.Length; i++)
            {
                if (columns[i].Width <= 0) continue;
                // Never narrower than the header itself: the design widths were
                // set against Chinese headers, and "Sessions" does not fit where
                // "会话" did.
                int headerW =
                    TextRenderer.MeasureText(columns[i].Header, list.Font).Width + Theme.Space4;
                int scaled = Math.Max(Theme.Scale(list, columns[i].Width), headerW);
                list.Columns[i].Width = scaled;
                fixedTotal += scaled;
            }
            // Distribute exactly what is left, reserving the scrollbar so a
            // flexible column never forces a horizontal one into existence.
            // Inflating this to a per-column minimum (times the weight total, at
            // that) made the columns wider than the pane and pushed the
            // right-hand ones out of sight.
            int spare = Math.Max(
                120,
                list.ClientSize.Width
                    - fixedTotal
                    - SystemInformation.VerticalScrollBarWidth
                    - 4);

            int used = 0;
            int lastFlex = Array.FindLastIndex(columns, c => c.Width < 0);
            for (int i = 0; i < columns.Length; i++)
            {
                if (columns[i].Width >= 0) continue;
                int w = i == lastFlex
                    ? spare - used // last one absorbs the rounding
                    : spare * -columns[i].Width / weightTotal;
                list.Columns[i].Width = Math.Max(60, w);
                used += w;
            }
        }
        list.Resize += (_, _) => Fit();
        list.HandleCreated += (_, _) => Fit();
        list.DpiChangedAfterParent += (_, _) => Fit();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        ApplyTheme();
        Invalidate(true);
    }

    /// <summary>
    /// Buttons and lists theme themselves; this covers the chrome around them.
    /// </summary>
    private void ApplyTheme()
    {
        BackColor = Theme.BgApp;
        ForeColor = Theme.TextPrimary;
        _header.BackColor = Theme.BgHeader;
        _title.ForeColor = Theme.TextPrimary;
        _countChip.BackColor = Theme.PrimarySoft;
        _countChip.ForeColor = Theme.PrimaryDark;
        _sessionsTitle.ForeColor = Theme.TextPrimary;
        _hint.ForeColor = Theme.TextMuted;
        _hint.BackColor = Theme.BgHeader;
        _statsText.ForeColor = Theme.TextSecondary;
        _split.BackColor = Theme.BgApp;
        _split.Panel1.BackColor = Theme.BgApp;
        _split.Panel2.BackColor = Theme.BgApp;
    }

    /// <summary>A list cell that sorts by <paramref name="key"/> rather than by its text.</summary>
    private static ListViewItem.ListViewSubItem Cell(ListViewItem item, string text, long key) =>
        new(item, text) { Tag = key };

    private void Reload()
    {
        try
        {
            var node = _engine.Call("list_projects");
            _rows = ParseProjects(node);
            _bindings = DirectoryBindings.Load(_engine);
            _loaded = true;
        }
        catch (Exception ex)
        {
            _hint.Text = Loc.T("proj.readFailed", ex.Message);
            return;
        }

        // Survive a refresh on the row the user was looking at.
        string? keep = SelectedProject?.Path;
        _dirs.BeginUpdate();
        _dirs.Items.Clear();
        foreach (var r in _rows)
        {
            var item = new ListViewItem(r.Path)
            {
                Tag = r,
                ToolTipText = r.LastPrompt is { Length: > 0 } p
                    ? Loc.T("proj.tooltip.recent", r.Name, p)
                    : r.Name,
            };
            item.SubItems.Add(_bindings.Label(r.Path));
            item.SubItems.Add(Cell(item, r.SessionCount.ToString(), r.SessionCount));
            item.SubItems.Add(Cell(
                item,
                Theme.FormatCompactEpoch(r.LastActiveMs) ?? Loc.T("common.dash"),
                r.LastActiveMs ?? 0));
            item.SubItems.Add(Cell(
                item,
                r.SizeBytes > 0 ? FormatBytes(r.SizeBytes) : Loc.T("common.dash"),
                r.SizeBytes));
            if (r.SessionCount == 0)
                item.ForeColor = Theme.TextMuted;
            _dirs.Items.Add(item);
        }
        _dirs.EndUpdate();
        _dirs.Sort();

        int used = _rows.Count(r => r.SessionCount > 0);
        int sessions = _rows.Sum(r => r.SessionCount);
        long total = _rows.Sum(r => r.SizeBytes);
        // "Registered" and "actually worked in" are different questions, so the
        // count says which is which instead of picking one and looking wrong.
        _countChip.Text = Loc.T("proj.count", used, sessions, _rows.Count - used)
            + (total > 0 ? Loc.T("history.totalSize", FormatBytes(total)) : "");
        _hint.Text = _statusMessage ?? Loc.T("proj.accountNote");

        int empty = _rows.Count(r => r.IsEmpty);
        _emptyDirs.Enabled = empty > 0;
        _tip.SetToolTip(_emptyDirs, Loc.T("history.emptyDirs.button.tip", empty));

        var restore = keep is null
            ? null
            : _dirs.Items.Cast<ListViewItem>()
                .FirstOrDefault(i => (i.Tag as ProjectRow)?.Path == keep);
        if (restore is not null)
        {
            restore.Selected = true;
            restore.EnsureVisible();
        }
        else if (_dirs.Items.Count > 0)
        {
            _dirs.Items[0].Selected = true;
        }
        UpdateButtons();
    }

    private static List<ProjectRow> ParseProjects(JsonNode node)
    {
        var list = new List<ProjectRow>();
        if (node["projects"] is not JsonArray arr) return list;
        foreach (var p in arr)
        {
            if (p is null) continue;
            list.Add(new ProjectRow(
                p["path"]?.GetValue<string>() ?? "",
                p["name"]?.GetValue<string>() ?? "",
                p["sessionCount"]?.GetValue<int>() ?? 0,
                p["lastActiveMs"]?.GetValue<long>(),
                p["lastPrompt"]?.GetValue<string>(),
                p["registered"]?.GetValue<bool>() ?? false,
                (p["transcriptDirs"] as JsonArray ?? [])
                    .Select(d => d?.GetValue<string>())
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Select(d => d!)
                    .ToList(),
                p["transcriptBytes"]?.GetValue<long>() ?? 0,
                p["totalBytes"]?.GetValue<long>() ?? 0));
        }
        return list;
    }

    /// <summary>
    /// Directory → account bindings, refreshed with the directory list.
    /// </summary>
    private DirectoryBindings _bindings = DirectoryBindings.Empty;

    private ProjectRow? SelectedProject =>
        _dirs.SelectedItems.Count > 0 ? _dirs.SelectedItems[0].Tag as ProjectRow : null;

    private SessionRow? SelectedSession =>
        _sessions.SelectedItems.Count > 0 ? _sessions.SelectedItems[0].Tag as SessionRow : null;

    private void OnProjectSelected()
    {
        _statsText.Text = "";
        _sessionsTitle.Text = Loc.T("proj.sessions");
        _sessions.BeginUpdate();
        _sessions.Items.Clear();

        var project = SelectedProject;
        if (project is { HasTranscripts: true })
        {
            try
            {
                var node = _engine.Call(
                    "list_sessions", new { transcriptDirs = project.TranscriptDirs });
                int hidden = 0;
                if (node["sessions"] is JsonArray arr)
                {
                    foreach (var s in arr)
                    {
                        if (s is null) continue;
                        // A session opened and left without a single exchange is
                        // not a conversation anyone comes looking for, and listing
                        // it — even greyed out — buried the real ones. It is
                        // counted in the heading instead; cleaning the directory
                        // still removes it.
                        if (s["hasConversation"]?.GetValue<bool>() == false)
                        {
                            hidden++;
                            continue;
                        }
                        string file = s["file"]?.GetValue<string>() ?? "";
                        long bytes = s["bytes"]?.GetValue<long>() ?? 0;
                        long total = s["totalBytes"]?.GetValue<long>() ?? 0;
                        var row = new SessionRow(
                            s["id"]?.GetValue<string>() ?? "",
                            s["title"]?.GetValue<string>() ?? s["firstPrompt"]?.GetValue<string>(),
                            s["modifiedMs"]?.GetValue<long>() ?? 0,
                            s["startedMs"]?.GetValue<long>(),
                            Math.Max(total, bytes),
                            Path.GetDirectoryName(file) ?? "",
                            Resumable: s["resumable"]?.GetValue<bool>() ?? true,
                            HasConversation: s["hasConversation"]?.GetValue<bool>() ?? true,
                            Live: s["live"]?.GetValue<bool>() ?? false,
                            ProfileNumber: s["profileNumber"]?.GetValue<int>());

                        // A running session says so ahead of its title: a long
                        // title is clipped at the end, and a marker after it went
                        // with it. The id has no column of its own — it is what
                        // the resume button uses, not something to read.
                        string title = row.Title ?? Loc.T("proj.untitled");
                        string tip = Loc.T("proj.sessionId", row.Id);
                        if (row.Live)
                        {
                            title = $"{Loc.T("proj.session.live")}{title}";
                            tip = Loc.T("proj.session.live.tip", row.Id);
                        }
                        var item = new ListViewItem(title)
                        {
                            Tag = row,
                            ToolTipText = tip,
                        };
                        if (!row.Resumable)
                            item.ForeColor = Theme.TextMuted;
                        // When it was last written to, not when it began — the
                        // same order the engine lists them in.
                        item.SubItems.Add(Cell(
                            item,
                            Theme.FormatCompactEpoch(row.ModifiedMs) ?? Loc.T("common.dash"),
                            row.ModifiedMs));
                        item.SubItems.Add(Cell(item, FormatBytes(row.Bytes), row.Bytes));
                        _sessions.Items.Add(item);
                    }
                }
                if (hidden > 0)
                    _sessionsTitle.Text = Loc.T("proj.sessions.hidden", hidden);
            }
            catch (Exception ex)
            {
                _statsText.Text = Loc.T("proj.sessionsFailed", ex.Message);
            }
        }

        _sessions.EndUpdate();
        _sessions.Sort();
        if (_sessions.Items.Count > 0)
            _sessions.Items[0].Selected = true;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var project = SelectedProject;
        int picked = _sessions.SelectedItems.Count;
        _resume.Enabled = picked == 1
            && project is not null
            && Directory.Exists(project.Path)
            && SelectedSession is { Resumable: true };
        _openDir.Enabled = project is not null && Directory.Exists(project.Path);
        _stats.Enabled = project is { HasTranscripts: true };
        _deleteSessions.Enabled = picked > 0;
    }

    /// <summary>Resume the selected conversation in the user's own terminal.</summary>
    private void ResumeSelected()
    {
        if (_sessions.SelectedItems.Count != 1) return;
        if (SelectedProject is not { } project || SelectedSession is not { Resumable: true } session) return;
        string shortId = session.Id[..Math.Min(8, session.Id.Length)];

        // Resume in the config home that holds the transcript: Claude Code only
        // looks in its own config directory. The directory's binding decides
        // where a new conversation starts, not where an existing one is found.
        if (session.ProfileNumber is { } number)
        {
            var result = SessionMode.Launch(_engine, number.ToString(), project.Path, session.Id);
            if (!result.Launched)
            {
                MessageBox.Show(
                    this, result.Problem, Loc.T("resume.failed.title"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _hint.Text = result.Note ?? Loc.T("proj.resumedAs", project.Name, shortId, number);
            return;
        }

        if (SessionMode.ResumeDefault(_engine, project.Path, session.Id) is { } problem)
        {
            MessageBox.Show(this, problem, Loc.T("resume.failed.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _hint.Text = Loc.T("proj.resumed", project.Name, shortId);
    }

    /// <summary>Per-directory actions: open a terminal, manage the binding, and clear history.</summary>
    private void BuildDirectoryMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        if (SelectedProject is not { } project) return;

        var match = _bindings.Resolve(project.Path);

        var open = new ToolStripMenuItem(Loc.T("proj.menu.openTerminal"))
        {
            Enabled = Directory.Exists(project.Path),
            ToolTipText = Loc.T("proj.menu.openTerminal.tip"),
        };
        open.Click += (_, _) => OpenTerminalHere(project, match.Binding);
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());

        // Every non-disabled account, so binding is one click rather than a
        // dialog. The list is short by construction — these are the user's own
        // Claude subscriptions, not an arbitrary collection.
        var bind = new ToolStripMenuItem(Loc.T("proj.menu.bind"));
        foreach (var (number, email) in Accounts())
        {
            var choice = new ToolStripMenuItem(Loc.T("proj.bindChoice", number, Pii.MaskEmail(email)))
            {
                Checked = match.Binding?.Number == number && !match.Inherited,
                CheckOnClick = false,
            };
            int slot = number;
            choice.Click += (_, _) => SetBinding(project.Path, slot.ToString());
            bind.DropDownItems.Add(choice);
        }
        if (bind.DropDownItems.Count == 0)
        {
            bind.DropDownItems.Add(new ToolStripMenuItem(Loc.T("proj.bindNone")) { Enabled = false });
        }
        menu.Items.Add(bind);

        var unbind = new ToolStripMenuItem(Loc.T("proj.menu.unbind"))
        {
            // Only an exact binding can be removed here: the inherited one
            // belongs to an ancestor row, and deleting it from this row would
            // silently change every sibling directory too.
            Enabled = match.Found && !match.Inherited,
        };
        unbind.Click += (_, _) => ClearBinding(project.Path);
        menu.Items.Add(unbind);
        menu.Items.Add(new ToolStripSeparator());

        // Two different depths of forgetting. Clearing keeps what the directory
        // has learned (auto memory) and how it is configured (trust, MCP); the
        // purge takes those too, and says so before it runs.
        var clean = new ToolStripMenuItem(Loc.T("history.menu.cleanDirectory"))
        {
            Enabled = project.HasTranscripts,
        };
        clean.Click += (_, _) => CleanDirectory(project);
        menu.Items.Add(clean);

        // A row with no conversations is there only because Claude Code
        // registered the directory; taking it off the list is all there is to do.
        var unlist = new ToolStripMenuItem(Loc.T("history.emptyDirs.menu"))
        {
            Enabled = project.IsEmpty,
        };
        unlist.Click += (_, _) => RemoveEmptyDirectories([project.Path]);
        menu.Items.Add(unlist);

        var purge = new ToolStripMenuItem(Loc.T("history.menu.purge")) { Tag = "danger" };
        purge.Click += (_, _) => PurgeDirectory(project);
        menu.Items.Add(purge);
    }

    /// <summary>Slots available to bind, newest snapshot order.</summary>
    private List<(int Number, string Email)> Accounts()
    {
        var list = new List<(int, string)>();
        try
        {
            if (_engine.Snapshot()["accounts"] is not JsonArray arr) return list;
            foreach (var a in arr)
            {
                if (a is null) continue;
                if (a["disabled"]?.GetValue<bool>() == true) continue;
                list.Add((a["number"]?.GetValue<int>() ?? 0, a["email"]?.GetValue<string>() ?? ""));
            }
        }
        catch (Exception)
        {
            // An unreadable snapshot leaves the submenu empty, which the caller
            // labels; it must not take the whole context menu down with it.
        }
        return list;
    }

    private void SetBinding(string path, string id)
    {
        try
        {
            _engine.Call("mapping_set", new { path, id });
            _bindings = DirectoryBindings.Load(_engine);
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("proj.bindFailed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ClearBinding(string path)
    {
        try
        {
            _engine.Call("mapping_remove", new { path });
            _bindings = DirectoryBindings.Load(_engine);
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("proj.bindFailed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Delete the selected sessions, after saying what that frees.</summary>
    private void DeleteSelectedSessions()
    {
        var picked = _sessions.SelectedItems
            .Cast<ListViewItem>()
            .Select(i => i.Tag)
            .OfType<SessionRow>()
            .Where(s => s.TranscriptDir.Length > 0)
            .Select(s => (s.TranscriptDir, s.Id))
            .ToList();
        if (picked.Count == 0) return;
        RunCleanup(HistoryCleanup.ForSessions(picked));
    }

    /// <summary>Every session of a directory, in every config home. Memory and settings stay.</summary>
    private void CleanDirectory(ProjectRow project)
    {
        if (!project.HasTranscripts) return;
        RunCleanup(HistoryCleanup.ForDirectories(project.TranscriptDirs));
    }

    /// <summary>Plan, confirm, apply, report — the one path every targeted deletion takes.</summary>
    private void RunCleanup(JsonObject request)
    {
        try
        {
            var plan = HistoryCleanup.Plan(_engine, request);
            if (!HistoryDeleteDialog.Confirm(this, plan)) return;

            var outcome = HistoryCleanup.Apply(_engine, request, plan.DeletableIds);
            _statusMessage = Loc.T(
                "history.delete.done",
                outcome.DeletedCount,
                FormatBytes(outcome.FreedBytes));
            Reload();
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

    /// <summary>
    /// Take directories with no conversations off the list — every one, or those named.
    /// </summary>
    /// <remarks>
    /// Such a row exists only because Claude Code registered the directory in its
    /// config, so that entry is what goes. The engine checks each directory for
    /// history and running sessions again under Claude Code's config lock, and
    /// backs the file up before changing it.
    /// </remarks>
    private void RemoveEmptyDirectories(IReadOnlyList<string>? paths)
    {
        try
        {
            var plan = HistoryCleanup.PlanEmptyDirectories(_engine, paths);
            if (!HistoryDeleteDialog.ConfirmEmptyDirectories(this, plan)) return;

            var outcome = HistoryCleanup.RemoveEmptyDirectories(_engine, plan.Paths);
            _statusMessage = Loc.T("history.emptyDirs.done", outcome.RemovedDirectories);
            Reload();
            if (outcome.Failures.Count > 0)
            {
                MessageBox.Show(
                    this,
                    Loc.T("history.emptyDirs.partial", string.Join("\n", outcome.Failures.Take(8))),
                    Loc.T("history.emptyDirs.title"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
        {
            MessageBox.Show(
                this,
                Loc.T("history.emptyDirs.failed", ex.Message),
                Loc.T("history.emptyDirs.title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OpenCleanupDialog()
    {
        using var dlg = new HistoryCleanupDialog(_engine);
        dlg.ShowDialog(this);
        if (dlg.Changed) Reload();
    }

    /// <summary>
    /// Remove everything Claude Code keeps for a directory, via its own purge command.
    /// </summary>
    /// <remarks>
    /// Refused while anything runs there: the command itself does not check, and
    /// a live session would keep writing into the state it just lost. Runs one
    /// purge per config home that holds state, because each profile keeps its
    /// own transcripts, prompt history and <c>.claude.json</c>.
    /// </remarks>
    private async void PurgeDirectory(ProjectRow project)
    {
        PurgePreflightView pre;
        try
        {
            pre = HistoryCleanup.Preflight(_engine, project.Path);
        }
        catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
        {
            MessageBox.Show(this, ex.Message, Loc.T("history.purge.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int running = pre.LiveSessions + pre.RunningRuns
            + BackgroundRuns.Active.Count(r => SamePath(r.Cwd, project.Path));
        if (running > 0)
        {
            MessageBox.Show(
                this, Loc.T("history.purge.live", running), Loc.T("history.purge.title"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (pre.Roots.Count == 0)
        {
            MessageBox.Show(
                this, Loc.T("history.purge.nothing"), Loc.T("history.purge.title"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (ClaudeCli.FindExecutable() is not { } claude)
        {
            MessageBox.Show(
                this, ClaudeCli.NotFoundMessage, Loc.T("history.purge.title"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!HistoryDeleteDialog.ConfirmPurge(this, project.Name, pre)) return;

        _statusMessage = Loc.T("history.purge.running", project.Name);
        _hint.Text = _statusMessage;
        UseWaitCursor = true;
        _split.Enabled = false;
        _cleanup.Enabled = false;
        _emptyDirs.Enabled = false;

        var failures = new List<string>();
        try
        {
            foreach (var root in pre.Roots)
            {
                if (await HistoryCleanup.RunPurgeAsync(claude, project.Path, root.ConfigDir) is { } problem)
                    failures.Add(root.ProfileNumber is { } n ? $"#{n}: {problem}" : problem);
            }
            // Finished run records for the directory would now offer to resume
            // conversations that no longer exist.
            if (failures.Count == 0)
            {
                var store = new AgentRunStore(_engine);
                foreach (var id in pre.RunIds) store.Remove(id);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                _split.Enabled = true;
                _cleanup.Enabled = true;
            }
        }
        if (IsDisposed) return;

        _statusMessage = failures.Count == 0 ? Loc.T("history.purge.done", project.Name) : null;
        Reload();
        if (failures.Count > 0)
        {
            MessageBox.Show(
                this, string.Join("\n\n", failures), Loc.T("history.purge.failedTitle"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            a.Replace('\\', '/').TrimEnd('/'),
            b.Replace('\\', '/').TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Open a terminal in this directory — as its bound account, or the default login.
    /// </summary>
    private void OpenTerminalHere(ProjectRow project, Binding? binding)
    {
        if (binding?.Number is { } number)
        {
            var result = SessionMode.Launch(_engine, number.ToString(), project.Path);
            _hint.Text = result.Launched
                ? result.Note ?? Loc.T("proj.openedAs", project.Name, number)
                : result.Problem ?? "";
            if (!result.Launched)
            {
                MessageBox.Show(
                    this, result.Problem, Loc.T("session.failed.title"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return;
        }

        // Unbound: exactly what typing `claude` here would do.
        if (SessionMode.ResumeDefault(_engine, project.Path) is { } problem)
        {
            MessageBox.Show(this, problem, Loc.T("session.failed.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _hint.Text = Loc.T("proj.openedDefault", project.Name);
    }

    private void OpenSelectedDirectory()
    {
        if (SelectedProject is not { } project || !Directory.Exists(project.Path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{project.Path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("proj.openFailed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>On-demand cumulative scan — the only path that reads whole transcripts.</summary>
    private void ComputeStats()
    {
        if (SelectedProject is not { HasTranscripts: true } project) return;

        _stats.Enabled = false;
        _statsText.Text = Loc.T("proj.scanning", FormatBytes(project.TranscriptBytes));
        Application.DoEvents();
        var sw = Stopwatch.StartNew();
        try
        {
            var s = _engine.Call(
                "project_stats", new { transcriptDirs = project.TranscriptDirs });
            sw.Stop();
            long input = s["inputTokens"]?.GetValue<long>() ?? 0;
            long output = s["outputTokens"]?.GetValue<long>() ?? 0;
            long cacheRead = s["cacheReadTokens"]?.GetValue<long>() ?? 0;
            long cacheWrite = s["cacheCreationTokens"]?.GetValue<long>() ?? 0;
            int userMsgs = s["userMessages"]?.GetValue<int>() ?? 0;
            int skipped = s["skippedLines"]?.GetValue<int>() ?? 0;

            // Two explicit lines beat relying on word wrap, which broke tokens
            // mid-word and crowded the buttons below.
            var text =
                Loc.T(
                    "proj.stats.line1",
                    s["sessions"]?.GetValue<int>() ?? 0, userMsgs,
                    FormatBytes(project.TranscriptBytes), sw.ElapsedMilliseconds)
                + Loc.T(
                    "proj.stats.line2",
                    FormatTokens(input), FormatTokens(output),
                    FormatTokens(cacheWrite), FormatTokens(cacheRead));
            // A partial read must not be presented as an exact total.
            if (skipped > 0)
                text += Loc.T("proj.stats.skipped", skipped);
            _statsText.Text = text;

            ShowStatsWindow(project.Path, s, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _statsText.Text = Loc.T("proj.stats.failed", ex.Message);
        }
        finally
        {
            _stats.Enabled = true;
        }
    }

    /// <summary>One window per directory, reused so repeat scans don't pile up.</summary>
    private ProjectStatsWindow? _statsWindow;

    /// <summary>
    /// Layout-probe hook: select a directory (by path substring) and run the scan
    /// the way the button does, so the charts are checked against real data.
    /// </summary>
    internal Form? ProbeRunStats()
    {
        var want = Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_PROJECT");
        if (!string.IsNullOrWhiteSpace(want))
        {
            var match = _dirs.Items.Cast<ListViewItem>().FirstOrDefault(
                i => (i.Tag as ProjectRow)?.Path.Contains(want, StringComparison.OrdinalIgnoreCase) == true);
            if (match is not null)
            {
                match.Selected = true;
                match.EnsureVisible();
                OnProjectSelected();
            }
        }
        ComputeStats();
        return _statsWindow;
    }

    /// <summary>
    /// Layout-probe hook: the cleanup dialog, shown modelessly so the probe can
    /// capture it. Nothing is deleted — the probe never presses a button.
    /// </summary>
    internal Form ProbeCleanupDialog()
    {
        var dialog = new HistoryCleanupDialog(_engine);
        dialog.Show(this);
        return dialog;
    }

    private void ShowStatsWindow(string projectPath, JsonNode stats, long scanMs)
    {
        if (_statsWindow is { IsDisposed: false })
            _statsWindow.Close();
        _statsWindow = new ProjectStatsWindow(projectPath, stats, scanMs);
        _statsWindow.FormClosed += (_, _) => _statsWindow = null;
        _statsWindow.Show(this);
    }

    private static string FormatBytes(long bytes) => HistoryCleanup.FormatBytes(bytes);

    private static string FormatTokens(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M"
        : n >= 1_000 ? $"{n / 1_000.0:0.#}K"
        : n.ToString();

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
