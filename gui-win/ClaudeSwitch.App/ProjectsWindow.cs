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
        long TranscriptBytes)
    {
        public bool HasTranscripts => TranscriptDirs.Count > 0;
    }

    private sealed record SessionRow(
        string Id,
        string? Title,
        long ModifiedMs,
        long? StartedMs,
        long Bytes);

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
            (Loc.T("proj.col.session"), -1), (Loc.T("proj.col.started"), 104),
            (Loc.T("proj.col.size"), 74));
        // Counts and sizes are numbers; left-aligned they read as ragged text.
        _dirs.RightAlignedColumns.UnionWith([2, 4]);
        _sessions.RightAlignedColumns.Add(2);

        _dirs.ShowItemToolTips = true;
        _sessions.ShowItemToolTips = true;
        _dirs.SelectedIndexChanged += (_, _) => OnProjectSelected();
        _sessions.SelectedIndexChanged += (_, _) => UpdateButtons();
        _sessions.DoubleClick += (_, _) => ResumeSelected();

        var sessionMenu = new ContextMenuStrip();
        sessionMenu.Items.Add(Loc.T("proj.menu.resume"), null, (_, _) => ResumeSelected());
        sessionMenu.Items.Add(Loc.T("proj.menu.copyId"), null, (_, _) =>
        {
            if (SelectedSession is { } s) Clipboard.SetText(s.Id);
        });
        _sessions.ContextMenuStrip = sessionMenu;

        // Binding lives on the directory, because "which account does this work
        // belong to" is a property of the project, not of one conversation.
        var dirMenu = new ContextMenuStrip();
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

        // Stats text gets its own band: inside the button flow it competed with
        // them for width and pushed them off the panel.
        _statsText.Dock = DockStyle.Bottom;
        _statsText.Height = Theme.FontCaption.Height * 3 + Theme.Space2;
        _statsText.TextAlign = ContentAlignment.MiddleLeft;
        _statsText.Font = Theme.FontCaption;
        _statsText.Padding = new Padding(2, Theme.Space1, 2, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, Theme.Space2, 0, 0),
        };
        foreach (var b in new Control[] { _resume, _openDir, _stats })
            b.Margin = new Padding(0, 0, Theme.Space2, 0);
        actions.Controls.AddRange([_resume, _openDir, _stats]);

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

        _header.Dock = DockStyle.Top;
        // Measured from its own fonts: a fixed band clips at >100% scaling.
        _header.Height = Theme.FontBrand.Height + Theme.FontSmall.Height + Theme.Space6;
        _header.Controls.AddRange([_title, _countChip]);

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

    /// <summary>
    /// Lay out a details list. A positive width is fixed; a negative one is a
    /// share of the leftover space (weight = |width|).
    /// </summary>
    /// <remarks>
    /// All-fixed widths overflowed the pane and cut the right-hand columns off
    /// entirely, and giving the leftover to a single column starved the others.
    /// </remarks>
    private static void ConfigureList(ThemedListView list, params (string Header, int Width)[] columns)
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
            item.SubItems.Add(r.SessionCount.ToString());
            item.SubItems.Add(Theme.FormatCompactEpoch(r.LastActiveMs) ?? Loc.T("common.dash"));
            item.SubItems.Add(r.TranscriptBytes > 0 ? FormatBytes(r.TranscriptBytes) : Loc.T("common.dash"));
            if (r.SessionCount == 0)
                item.ForeColor = Theme.TextMuted;
            _dirs.Items.Add(item);
        }
        _dirs.EndUpdate();

        int used = _rows.Count(r => r.SessionCount > 0);
        int sessions = _rows.Sum(r => r.SessionCount);
        // "Registered" and "actually worked in" are different questions, so the
        // count says which is which instead of picking one and looking wrong.
        _countChip.Text = Loc.T("proj.count", used, sessions, _rows.Count - used);
        _hint.Text = Loc.T("proj.accountNote");

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
                p["transcriptBytes"]?.GetValue<long>() ?? 0));
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
        _sessions.BeginUpdate();
        _sessions.Items.Clear();

        var project = SelectedProject;
        if (project is { HasTranscripts: true })
        {
            try
            {
                var node = _engine.Call(
                    "list_sessions", new { transcriptDirs = project.TranscriptDirs });
                if (node["sessions"] is JsonArray arr)
                {
                    foreach (var s in arr)
                    {
                        if (s is null) continue;
                        var row = new SessionRow(
                            s["id"]?.GetValue<string>() ?? "",
                            s["firstPrompt"]?.GetValue<string>(),
                            s["modifiedMs"]?.GetValue<long>() ?? 0,
                            s["startedMs"]?.GetValue<long>(),
                            s["bytes"]?.GetValue<long>() ?? 0);
                        var item = new ListViewItem(row.Title ?? Loc.T("proj.untitled"))
                        {
                            Tag = row,
                            // The id has no column of its own — it is what the
                            // resume button uses, not something to read.
                            ToolTipText = Loc.T("proj.sessionId", row.Id),
                        };
                        item.SubItems.Add(
                            Theme.FormatCompactEpoch(row.StartedMs ?? row.ModifiedMs)
                            ?? Loc.T("common.dash"));
                        item.SubItems.Add(FormatBytes(row.Bytes));
                        _sessions.Items.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                _statsText.Text = Loc.T("proj.sessionsFailed", ex.Message);
            }
        }

        _sessions.EndUpdate();
        if (_sessions.Items.Count > 0)
            _sessions.Items[0].Selected = true;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var project = SelectedProject;
        _resume.Enabled = SelectedSession is not null && project is not null;
        _openDir.Enabled = project is not null && Directory.Exists(project.Path);
        _stats.Enabled = project is { HasTranscripts: true };
    }

    /// <summary>Resume the selected conversation in the user's own terminal.</summary>
    private void ResumeSelected()
    {
        if (SelectedProject is not { } project || SelectedSession is not { } session) return;
        string shortId = session.Id[..Math.Min(8, session.Id.Length)];

        // A bound directory resumes under its own account; anything else keeps
        // the original behaviour of plain `claude --resume`.
        if (_bindings.Resolve(project.Path).Binding?.Number is { } number)
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

        if (ClaudeCli.Resume(project.Path, session.Id) is { } problem)
        {
            MessageBox.Show(this, problem, Loc.T("resume.failed.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _hint.Text = Loc.T("proj.resumed", project.Name, shortId);
    }

    /// <summary>Per-directory actions: open a terminal, and manage the binding.</summary>
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
        if (ClaudeCli.Resume(project.Path) is { } problem)
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

    private void ShowStatsWindow(string projectPath, JsonNode stats, long scanMs)
    {
        if (_statsWindow is { IsDisposed: false })
            _statsWindow.Close();
        _statsWindow = new ProjectStatsWindow(projectPath, stats, scanMs);
        _statsWindow.FormClosed += (_, _) => _statsWindow = null;
        _statsWindow.Show(this);
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0} KB"
        : $"{bytes} B";

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
