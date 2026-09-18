using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// Uninstall Claude Code: program + runtime files, with a choice to keep
/// imported accounts as a backup.
/// </summary>
internal sealed class PurgeDialog : Form
{
    private readonly Engine _engine;
    private readonly Label _required;
    private readonly ThemedCheckBox _chkIde;
    private readonly ThemedCheckBox _chkProject;
    private readonly ThemedCheckBox? _chkDesktop;
    private readonly ThemedCheckBox? _chkThird;
    private readonly ThemedCheckBox? _chkManaged;
    private readonly RadioButton _keepAccounts;
    private readonly RadioButton _deleteAccounts;
    private readonly Label _notes;
    private readonly Label _status;
    private readonly DangerButton _apply;
    private readonly SecondaryButton _close;
    private PurgeScanView _scan = PurgeScanView.Empty;
    private PurgePlanView _plan = PurgePlanView.Invalid("nothing");
    private bool _busy;

    /// <summary>Whether apply finished and the main window should refresh.</summary>
    public bool Changed { get; private set; }

    /// <summary>Whether imported-account backups were kept.</summary>
    public bool AccountsKept { get; private set; } = true;

    public PurgeDialog(Engine engine)
    {
        _engine = engine;
        Text = Loc.T("purge.title");
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

        try { _scan = PurgeData.Scan(_engine); }
        catch (Exception) { /* PaintRequired shows the failure. */ }

        var intro = layout.Text(Loc.T("purge.intro"), Theme.FontBody, Theme.TextSecondary);
        _required = layout.Text(RequiredLines(), Theme.FontBody, Theme.TextPrimary);

        _chkIde = PlaceCheck(layout, "purge.opt.ide", true, "purgeIde");
        _chkProject = PlaceCheck(layout, "purge.opt.project", false, "purgeProject");
        if (_scan.Group("desktop") is { Items.Count: > 0 })
            _chkDesktop = PlaceCheck(layout, "purge.opt.desktop", false, "purgeDesktop");
        if (_scan.Group("third-party") is { Items.Count: > 0 })
            _chkThird = PlaceCheck(layout, "purge.opt.third", false, "purgeThird");
        if (_scan.Group("managed") is { Items.Count: > 0 })
            _chkManaged = PlaceCheck(layout, "purge.opt.managed", false, "purgeManaged");

        if (_scan.ImportedAccounts > 0 || _scan.Group("accounts") is { Items.Count: > 0 })
        {
            var heading = layout.Text(
                Loc.T("purge.accounts.heading", _scan.ImportedAccounts),
                Theme.FontBody,
                Theme.TextPrimary);
            Controls.Add(heading);
            _keepAccounts = new RadioButton { Name = "purgeKeepAccounts", Checked = true };
            _deleteAccounts = new RadioButton { Name = "purgeDeleteAccounts" };
            layout.Radio(_keepAccounts, Loc.T("purge.accounts.keep"));
            var keepHint = layout.Text(
                Loc.T("purge.accounts.keepHint"), Theme.FontSmall, Theme.TextMuted);
            layout.Radio(_deleteAccounts, Loc.T("purge.accounts.delete"));
            var deleteHint = layout.Text(
                Loc.T("purge.accounts.deleteHint"), Theme.FontSmall, Theme.TextMuted);
            Controls.AddRange([_keepAccounts, keepHint, _deleteAccounts, deleteHint]);
            _keepAccounts.CheckedChanged += (_, _) => RefreshPlan();
            _deleteAccounts.CheckedChanged += (_, _) => RefreshPlan();
        }
        else
        {
            _keepAccounts = new RadioButton { Checked = true, Visible = false };
            _deleteAccounts = new RadioButton { Visible = false };
            var none = layout.Text(Loc.T("purge.accounts.none"), Theme.FontSmall, Theme.TextMuted);
            Controls.Add(none);
        }

        _notes = layout.Text(" ", Theme.FontSmall, Theme.TextMuted);
        _status = layout.Text(" ", Theme.FontBody, Theme.TextPrimary);

        _apply = new DangerButton
        {
            Text = Loc.T("purge.apply"),
            Name = "purgeApply",
        };
        _close = new SecondaryButton { Text = Loc.T("common.cancel") };

        Controls.AddRange([intro, _required, _chkIde, _chkProject, _notes, _status, _apply, _close]);
        if (_chkDesktop is not null) Controls.Add(_chkDesktop);
        if (_chkThird is not null) Controls.Add(_chkThird);
        if (_chkManaged is not null) Controls.Add(_chkManaged);

        layout.ActionRow(_close, _apply);
        _apply.Click += (_, _) => OnApply();
        _close.Click += (_, _) => Close();
        CancelButton = _close;

        RefreshPlan();
    }

    private ThemedCheckBox PlaceCheck(DialogLayout layout, string key, bool on, string name)
    {
        var check = new ThemedCheckBox
        {
            Text = Loc.T(key),
            Checked = on,
            AutoSize = false,
            Font = Theme.FontBody,
            ForeColor = Theme.TextPrimary,
            Name = name,
        };
        var textSize = TextRenderer.MeasureText(
            check.Text,
            check.Font,
            new Size(layout.TextWidth - layout.Scale(22), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        check.Size = new Size(
            layout.TextWidth,
            Math.Max(layout.Scale(Theme.ControlHeight), textSize.Height + layout.Scale(6)));
        check.Location = new Point(layout.Left, layout.Y);
        layout.Advance(check);
        check.CheckedChanged += (_, _) => RefreshPlan();
        return check;
    }

    private string RequiredLines()
    {
        if (_scan.Groups.Count == 0 && _scan.InstallKind == "none")
            return Loc.T("purge.warn.nothing");
        var lines = new List<string> { Loc.T("purge.required") };
        foreach (var group in _scan.Groups.Where(g => g.Required && g.Items.Count > 0))
        {
            lines.Add($"{PurgeData.GroupTitle(group.Id)}    {HistoryCleanup.FormatBytes(group.Bytes)}");
            foreach (var item in group.Items.Take(8))
                lines.Add("    " + item.Path);
            if (group.Items.Count > 8)
                lines.Add("    …");
        }
        return string.Join("\n", lines);
    }

    private PurgeOptionsView CurrentOptions() => new(
        _chkIde.Checked,
        _chkProject.Checked,
        _chkDesktop?.Checked ?? false,
        _chkThird?.Checked ?? false,
        _chkManaged?.Checked ?? false,
        _deleteAccounts.Checked);

    private void RefreshPlan()
    {
        if (_busy) return;
        try
        {
            _plan = PurgeData.Plan(_engine, CurrentOptions());
        }
        catch (Exception ex)
        {
            _notes.Text = Loc.T("purge.scan.failed", ex.Message);
            _apply.Enabled = false;
            return;
        }

        var notes = new List<string>();
        foreach (var p in _plan.Problems)
            notes.Add(PurgeData.ProblemText(p, _plan));
        foreach (var w in _plan.Warnings)
            notes.Add(PurgeData.WarningText(w));
        _notes.Text = notes.Count == 0 ? " " : string.Join("\n\n", notes);
        _notes.Size = _notes.GetPreferredSize(new Size(_notes.MaximumSize.Width, 0));
        _notes.ForeColor = _plan.Problems.Count > 0 ? Theme.UsageHigh : Theme.TextMuted;
        _apply.Enabled = !_busy && _plan.CanApply;
    }

    private async void OnApply()
    {
        var opts = CurrentOptions();
        var preview = string.Join("\n", _plan.Items.Take(12).Select(i => "• " + i.Path));
        if (_plan.Items.Count > 12)
            preview += "\n• …";
        var ask = MessageBox.Show(
            this,
            Loc.T("purge.confirm.body", preview),
            Loc.T("purge.title"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (ask != DialogResult.OK) return;

        SetBusy(true, Loc.T("purge.apply.busy"));
        try
        {
            var outcome = await Task.Run(() => PurgeData.Apply(_engine, opts)).ConfigureAwait(true);
            Changed = true;
            AccountsKept = !opts.RemoveImportedAccounts;
            if (outcome.Failed.Count == 0)
            {
                _status.Text = Loc.T("purge.apply.ok");
                _apply.Enabled = false;
            }
            else
            {
                _status.Text = Loc.T(
                    "purge.apply.partial",
                    outcome.Deleted.Count,
                    outcome.Failed.Count);
                MessageBox.Show(
                    this,
                    string.Join("\n", outcome.Failed.Select(f => $"{f.Path}: {f.Reason}")),
                    Loc.T("purge.title"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _status.Text = "";
            MessageBox.Show(
                this,
                Loc.T("purge.apply.failed", ex.Message),
                Loc.T("purge.title"),
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
        _chkIde.Enabled = !busy;
        _chkProject.Enabled = !busy;
        if (_chkDesktop is not null) _chkDesktop.Enabled = !busy;
        if (_chkThird is not null) _chkThird.Enabled = !busy;
        if (_chkManaged is not null) _chkManaged.Enabled = !busy;
        _keepAccounts.Enabled = !busy;
        _deleteAccounts.Enabled = !busy;
        if (!busy) RefreshPlan();
        else _apply.Enabled = false;
    }
}
