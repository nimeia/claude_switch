using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// Pick a recent working directory, then continue its last chat or start a new one.
/// </summary>
/// <remarks>
/// Replaces the bare folder browser on "open terminal as this account": jumping
/// straight to a directory picker hid the directories the user already works in.
/// The frequent path is "same project, last conversation" or "same project, fresh".
/// </remarks>
internal sealed class SessionLaunchDialog : Form
{
    private readonly ThemedListView _list = new();
    private readonly PrimaryButton _btnContinue = new();
    private readonly SecondaryButton _btnNew = new();
    private readonly SecondaryButton _btnBrowse = new();
    private readonly SecondaryButton _btnCancel = new();
    private readonly Label _hint = new();
    private readonly Label _empty = new();
    private readonly List<WorkDir> _rows = [];
    private readonly ToolTip _tip = new() { ShowAlways = true, AutoPopDelay = 12000 };

    /// <summary>Chosen working directory, when the dialog was accepted.</summary>
    public string? Directory { get; private set; }

    /// <summary>
    /// Session id for <c>claude --resume</c>, or null for a brand-new conversation.
    /// </summary>
    public string? SessionId { get; private set; }

    private sealed record WorkDir(
        string Path,
        string Name,
        string? LastSessionId,
        string? LastPrompt,
        long? LastActiveMs,
        int SessionCount);

    public SessionLaunchDialog(Engine engine, int accountNumber, string accountLabel)
    {
        Text = Loc.T("session.launch.title");
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
        Font = Theme.FontBody;
        Icon = AppIcon.Get();
        MinimumSize = new Size(Theme.Scale(this, 520), Theme.Scale(this, 420));
        ClientSize = new Size(Theme.Scale(this, 560), Theme.Scale(this, 480));

        int pad = Theme.Scale(this, 20);
        int gap = Theme.Scale(this, 10);

        var title = new Label
        {
            Text = Loc.T("session.launch.heading", accountNumber, accountLabel),
            Font = Theme.FontHeading,
            ForeColor = Theme.TextPrimary,
            AutoSize = false,
            Location = new Point(pad, pad),
            Height = Theme.Scale(this, 28),
        };

        _hint = new Label
        {
            Text = Loc.T("session.launch.hint"),
            Font = Theme.FontSmall,
            ForeColor = Theme.TextMuted,
            AutoSize = false,
            Location = new Point(pad, title.Bottom + Theme.Scale(this, 4)),
            Height = Theme.Scale(this, 36),
        };

        _list.View = View.Details;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.ShowItemToolTips = true;
        _list.Columns.Add(Loc.T("session.launch.col.dir"), Theme.Scale(this, 160));
        _list.Columns.Add(Loc.T("session.launch.col.when"), Theme.Scale(this, 90));
        _list.Columns.Add(Loc.T("session.launch.col.last"), Theme.Scale(this, 240));
        _list.Location = new Point(pad, _hint.Bottom + gap);
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.SelectedIndexChanged += (_, _) => SyncButtons();
        _list.DoubleClick += (_, _) => AcceptPrimary();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                AcceptPrimary();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        _empty = new Label
        {
            Text = Loc.T("session.launch.empty"),
            Font = Theme.FontBody,
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleCenter,
            Visible = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };

        _btnContinue.Text = Loc.T("session.launch.continue");
        _btnContinue.Click += (_, _) => AcceptContinue();
        _btnNew.Text = Loc.T("session.launch.new");
        _btnNew.Click += (_, _) => AcceptNew();
        _btnBrowse.Text = Loc.T("session.launch.browse");
        _btnBrowse.Click += (_, _) => BrowseOther();
        _btnCancel.Text = Loc.T("common.cancel");
        _btnCancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([title, _hint, _list, _empty, _btnContinue, _btnNew, _btnBrowse, _btnCancel]);
        CancelButton = _btnCancel;

        LoadRows(engine);
        LayoutControls(pad, gap, title);
        Resize += (_, _) => LayoutControls(pad, gap, title);
        Shown += (_, _) =>
        {
            if (_list.Items.Count > 0)
            {
                _list.Items[0].Selected = true;
                _list.Select();
            }
            SyncButtons();
        };
    }

    private void LayoutControls(int pad, int gap, Label title)
    {
        int w = ClientSize.Width;
        int h = ClientSize.Height;
        title.Width = w - pad * 2;
        _hint.Width = w - pad * 2;

        int btnH = Math.Max(_btnContinue.RowHeight, Theme.Scale(this, Theme.ControlHeight));
        int btnY = h - pad - btnH;
        int listBottom = btnY - gap * 2;

        _list.SetBounds(pad, _hint.Bottom + gap, w - pad * 2, Math.Max(80, listBottom - (_hint.Bottom + gap)));
        _empty.Bounds = _list.Bounds;

        // Right-aligned: Cancel | Browse | New | Continue (primary last/rightmost)
        int right = w - pad;
        void Place(ThemedButton b)
        {
            int bw = b.GetPreferredSize(Size.Empty).Width;
            b.SetBounds(right - bw, btnY, bw, btnH);
            right -= bw + Theme.Scale(this, 10);
        }
        Place(_btnCancel);
        Place(_btnBrowse);
        Place(_btnNew);
        Place(_btnContinue);

        // Stretch last column to fill.
        if (_list.Columns.Count >= 3 && _list.ClientSize.Width > 40)
        {
            int used = _list.Columns[0].Width + _list.Columns[1].Width;
            _list.Columns[2].Width = Math.Max(120, _list.ClientSize.Width - used - 4);
        }
    }

    private void LoadRows(Engine engine)
    {
        _rows.Clear();
        _list.Items.Clear();
        try
        {
            var node = engine.Call("list_projects");
            if (node["projects"] is not JsonArray arr)
            {
                ShowEmpty(true);
                return;
            }

            var tmp = new List<WorkDir>();
            foreach (var p in arr)
            {
                if (p is null) continue;
                string path = p["path"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(path)) continue;
                // Prefer dirs that still exist on disk; skip ghosts from moved repos.
                if (!System.IO.Directory.Exists(path)) continue;

                tmp.Add(new WorkDir(
                    path,
                    p["name"]?.GetValue<string>() ?? System.IO.Path.GetFileName(path.TrimEnd('\\', '/')),
                    p["lastSessionId"]?.GetValue<string>(),
                    p["lastPrompt"]?.GetValue<string>(),
                    p["lastActiveMs"]?.GetValue<long>(),
                    p["sessionCount"]?.GetValue<int>() ?? 0));
            }

            // Newest activity first; dirs never touched still appear at the end.
            tmp.Sort((a, b) =>
            {
                long aa = a.LastActiveMs ?? 0;
                long bb = b.LastActiveMs ?? 0;
                int c = bb.CompareTo(aa);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            const int max = 24;
            foreach (var row in tmp.Take(max))
            {
                _rows.Add(row);
                string when = Theme.FormatCompactEpoch(row.LastActiveMs) ?? Loc.T("common.dash");
                string last = string.IsNullOrWhiteSpace(row.LastPrompt)
                    ? (row.SessionCount > 0
                        ? Loc.T("session.launch.sessions", row.SessionCount)
                        : Loc.T("session.launch.noChat"))
                    : RecentSessions.Truncate(row.LastPrompt!.Trim(), 40);
                var item = new ListViewItem([row.Name, when, last])
                {
                    Tag = row,
                    ToolTipText = row.Path,
                };
                if (string.IsNullOrEmpty(row.LastSessionId))
                    item.ForeColor = Theme.TextMuted;
                _list.Items.Add(item);
            }
        }
        catch
        {
            // Empty list; browse still works.
        }

        ShowEmpty(_rows.Count == 0);
    }

    private void ShowEmpty(bool empty)
    {
        _empty.Visible = empty;
        _list.Visible = !empty || _rows.Count > 0;
        if (empty)
        {
            _list.Visible = false;
            _empty.Visible = true;
        }
    }

    private WorkDir? Selected()
    {
        if (_list.SelectedItems.Count == 0) return null;
        return _list.SelectedItems[0].Tag as WorkDir;
    }

    private void SyncButtons()
    {
        var row = Selected();
        bool hasDir = row is not null;
        bool canResume = hasDir && !string.IsNullOrEmpty(row!.LastSessionId);
        _btnContinue.Enabled = canResume;
        _btnNew.Enabled = hasDir;
        _tip.SetToolTip(_btnContinue, canResume
            ? Loc.T("session.launch.continue.tip")
            : Loc.T("session.launch.continue.disabled"));
        _tip.SetToolTip(_btnNew, Loc.T("session.launch.new.tip"));
    }

    /// <summary>Enter / double-click: resume when possible, otherwise new chat.</summary>
    private void AcceptPrimary()
    {
        var row = Selected();
        if (row is null) return;
        if (!string.IsNullOrEmpty(row.LastSessionId))
            AcceptContinue();
        else
            AcceptNew();
    }

    private void AcceptContinue()
    {
        var row = Selected();
        if (row is null || string.IsNullOrEmpty(row.LastSessionId)) return;
        Directory = row.Path;
        SessionId = row.LastSessionId;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void AcceptNew()
    {
        var row = Selected();
        if (row is null) return;
        Directory = row.Path;
        SessionId = null;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void BrowseOther()
    {
        if (SessionMode.AskDirectory(this, Selected()?.Path) is not { } path)
            return;
        Directory = path;
        SessionId = null; // browse = start fresh in that folder
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// Show the picker; returns path + optional resume id, or null if cancelled.
    /// </summary>
    public static (string Directory, string? SessionId)? Pick(
        IWin32Window owner,
        Engine engine,
        int accountNumber,
        string accountLabel)
    {
        using var dlg = new SessionLaunchDialog(engine, accountNumber, accountLabel);
        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return null;
        if (string.IsNullOrEmpty(dlg.Directory))
            return null;
        return (dlg.Directory, dlg.SessionId);
    }
}
