using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// Copy Claude Code user data and this app's backup tree to another drive,
/// then leave the original paths as directory links.
/// </summary>
internal sealed class RelocateDialog : Form
{
    private readonly Engine _engine;
    private readonly Label _intro;
    private readonly Label _trees;
    private readonly Label _jsonNote;
    private readonly Label _destLabel;
    private readonly TextBox _dest;
    private readonly SecondaryButton _browse;
    private readonly Label _notes;
    private readonly Label _status;
    private readonly PrimaryButton _move;
    private readonly SecondaryButton _restore;
    private readonly SecondaryButton _deleteBackups;
    private readonly SecondaryButton _close;
    private readonly System.Windows.Forms.Timer _replan = new() { Interval = 350 };
    private RelocateScanView _scan = RelocateScanView.Empty;
    private RelocatePlanView _plan = RelocatePlanView.Invalid("dest-not-absolute");
    private bool _busy;

    public RelocateDialog(Engine engine)
    {
        _engine = engine;
        Text = Loc.T("relocate.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
        Font = Theme.FontBody;
        Icon = AppIcon.Get();

        var layout = new DialogLayout(this, textWidth: 520);

        try { _scan = RelocateData.Scan(_engine); }
        catch (Exception) { /* PaintScan shows the failure on the tree lines. */ }

        _intro = layout.Text(Loc.T("relocate.intro"), Theme.FontBody, Theme.TextSecondary);
        _trees = layout.Text(TreeLines(), Theme.FontBody, Theme.TextPrimary);
        _jsonNote = layout.Text(JsonLine(), Theme.FontSmall, Theme.TextMuted);

        _destLabel = layout.Text(Loc.T("relocate.dest"), Theme.FontBody, Theme.TextPrimary);

        _browse = new SecondaryButton
        {
            Text = Loc.T("relocate.browse"),
            Name = "relocateBrowse",
        };
        int browseW = Math.Max(_browse.GetPreferredSize(Size.Empty).Width, layout.Scale(88));
        _browse.Size = new Size(browseW, _browse.RowHeight);

        _dest = new TextBox
        {
            Name = "relocateDest",
            Width = layout.TextWidth - browseW - layout.Scale(8),
            Height = layout.Scale(Theme.ControlHeight),
            Font = Theme.FontBody,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.BgApp,
            ForeColor = Theme.TextPrimary,
            PlaceholderText = Loc.T("relocate.dest.placeholder"),
        };
        int destRowH = Math.Max(_dest.Height, _browse.Height);
        _dest.Location = new Point(layout.Left, layout.Y + (destRowH - _dest.Height) / 2);
        _browse.Location = new Point(
            layout.Left + layout.TextWidth - browseW,
            layout.Y + (destRowH - _browse.Height) / 2);
        layout.Y = Math.Max(_dest.Bottom, _browse.Bottom) + layout.Scale(12);

        var destHint = layout.Text(Loc.T("relocate.dest.hint"), Theme.FontSmall, Theme.TextMuted);
        _notes = layout.Text(" ", Theme.FontSmall, Theme.TextMuted);
        _status = layout.Text(" ", Theme.FontBody, Theme.TextPrimary);

        _move = new PrimaryButton { Text = Loc.T("relocate.apply"), Name = "relocateApply" };
        _restore = new SecondaryButton { Text = Loc.T("relocate.restore"), Name = "relocateRestore" };
        _deleteBackups = new SecondaryButton
        {
            Text = Loc.T("relocate.deleteBackups"),
            Name = "relocateDeleteBackups",
        };
        _close = new SecondaryButton { Text = Loc.T("history.cleanup.close") };

        Controls.AddRange([
            _intro, _trees, _jsonNote, _destLabel, _dest, _browse, destHint, _notes, _status,
            _move, _restore, _deleteBackups, _close,
        ]);

        _dest.Text = SuggestDest();
        RefreshPlan();
        layout.Y = Math.Max(_notes.Bottom, _status.Bottom) + layout.Scale(8);
        layout.ActionRow(_close, _deleteBackups, _restore, _move);

        _browse.Click += (_, _) => Browse();
        _dest.TextChanged += (_, _) => { _replan.Stop(); _replan.Start(); };
        _replan.Tick += (_, _) => { _replan.Stop(); RefreshPlan(); };
        _move.Click += (_, _) => OnMove();
        _restore.Click += (_, _) => OnRestore();
        _deleteBackups.Click += (_, _) => OnDeleteBackups();
        _close.Click += (_, _) => Close();
        CancelButton = _close;
        FormClosed += (_, _) => _replan.Dispose();
    }

    private void Reload()
    {
        try
        {
            _scan = RelocateData.Scan(_engine);
        }
        catch (Exception ex)
        {
            _trees.Text = Loc.T("relocate.scan.failed", ex.Message);
            _move.Enabled = false;
            return;
        }
        PaintScan();
        RefreshPlan();
    }

    private string TreeLines()
    {
        if (_scan.Trees.Count == 0)
            return Loc.T("relocate.scan.failed", "—");
        var lines = new List<string>();
        foreach (var tree in _scan.Trees)
        {
            string state = tree.Linked
                ? Loc.T("relocate.status.linked")
                : Loc.T("relocate.status.local");
            lines.Add($"{RelocateData.TreeTitle(tree.Id)}    {HistoryCleanup.FormatBytes(tree.Bytes)}    {state}");
            if (tree.BackupExists)
                lines.Add("    " + Loc.T("relocate.backup", HistoryCleanup.FormatBytes(tree.BackupBytes)));
        }
        return string.Join("\n", lines);
    }

    private string JsonLine() =>
        _scan.ClaudeJsonBytes > 0
            ? Loc.T("relocate.claudeJson", HistoryCleanup.FormatBytes(_scan.ClaudeJsonBytes))
            : " ";

    private void PaintScan()
    {
        _trees.Text = TreeLines();
        _trees.Size = _trees.GetPreferredSize(new Size(_trees.MaximumSize.Width, 0));
        _jsonNote.Text = JsonLine();
        _jsonNote.Size = _jsonNote.GetPreferredSize(new Size(_jsonNote.MaximumSize.Width, 0));
    }

    private void RefreshPlan()
    {
        if (_busy) return;
        try
        {
            _plan = RelocateData.Plan(_engine, _dest.Text);
        }
        catch (Exception ex)
        {
            _notes.Text = Loc.T("relocate.scan.failed", ex.Message);
            _move.Enabled = false;
            return;
        }

        var notes = new List<string>();
        if (_scan.LiveSessions > 0)
            notes.Add(Loc.T("relocate.live", _scan.LiveSessions));
        if (!string.IsNullOrEmpty(_scan.ClaudeConfigDir))
            notes.Add(Loc.T("relocate.configDir", _scan.ClaudeConfigDir));
        if (_plan.SameVolume)
            notes.Add(Loc.T("relocate.sameVolume"));
        foreach (var w in _plan.Warnings)
        {
            if (w == "same-volume") continue;
            notes.Add(w);
        }
        foreach (var p in _plan.Problems)
            notes.Add(RelocateData.ProblemText(p, _plan));
        if (_scan.Relocated)
            notes.Add(Loc.T("relocate.warn.keepBackup"));

        _notes.Text = notes.Count == 0 ? " " : string.Join("\n\n", notes);
        _notes.Size = _notes.GetPreferredSize(new Size(_notes.MaximumSize.Width, 0));
        _notes.ForeColor = _plan.Problems.Count > 0 || _scan.LiveSessions > 0
            ? Theme.TextPrimary
            : Theme.TextMuted;

        bool backups = _scan.Trees.Any(t => t.BackupExists);
        _move.Enabled = !_busy && _plan.CanApply;
        _restore.Enabled = !_busy && _scan.Relocated;
        _deleteBackups.Enabled = !_busy && _scan.Relocated && backups;
        _dest.Enabled = !_busy && !_scan.Relocated;
        _browse.Enabled = _dest.Enabled;
    }

    private string SuggestDest()
    {
        string source = _scan.Trees.FirstOrDefault()?.Source
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string sourceRoot = Path.GetPathRoot(source) ?? "C:\\";
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            if (d.Name.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase)) continue;
            return Path.Combine(d.Name, "ClaudeData");
        }
        return Path.Combine(sourceRoot, "ClaudeData");
    }

    private void Browse()
    {
        using var fb = new FolderBrowserDialog
        {
            Description = Loc.T("relocate.browse"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (!string.IsNullOrWhiteSpace(_dest.Text) && Directory.Exists(_dest.Text))
            fb.SelectedPath = _dest.Text;
        if (fb.ShowDialog(this) == DialogResult.OK)
            _dest.Text = fb.SelectedPath;
    }

    private async void OnMove()
    {
        string dest = _dest.Text.Trim();
        SetBusy(true, Loc.T("relocate.apply.busy"));
        try
        {
            await Task.Run(() => RelocateData.Apply(_engine, dest)).ConfigureAwait(true);
            _status.Text = Loc.T("relocate.apply.ok");
            Reload();
        }
        catch (Exception ex)
        {
            _status.Text = "";
            MessageBox.Show(
                this,
                Loc.T("relocate.apply.failed", ex.Message),
                Loc.T("relocate.title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            SetBusy(false, _status.Text);
        }
    }

    private async void OnRestore()
    {
        SetBusy(true, "");
        try
        {
            await Task.Run(() => RelocateData.Restore(_engine)).ConfigureAwait(true);
            _status.Text = Loc.T("relocate.restore.ok");
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                Loc.T("relocate.restore.failed", ex.Message),
                Loc.T("relocate.title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            SetBusy(false, _status.Text);
        }
    }

    private async void OnDeleteBackups()
    {
        var ask = MessageBox.Show(
            this,
            Loc.T("relocate.deleteBackups.confirm"),
            Loc.T("relocate.deleteBackups"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (ask != DialogResult.OK) return;

        SetBusy(true, "");
        try
        {
            long bytes = await Task.Run(() => RelocateData.DeleteBackups(_engine)).ConfigureAwait(true);
            _status.Text = Loc.T("relocate.deleteBackups.ok", HistoryCleanup.FormatBytes(bytes));
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                Loc.T("relocate.deleteBackups.failed", ex.Message),
                Loc.T("relocate.title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            SetBusy(false, _status.Text);
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _status.Text = status;
        _close.Enabled = !busy;
        RefreshPlan();
    }
}
