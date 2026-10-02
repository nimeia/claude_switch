using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// Uninstall Claude Code: program + runtime files, with a choice to keep
/// imported accounts as a backup.
/// </summary>
internal sealed class PurgeDialog : Form
{
    private readonly Func<PurgeScanView> _scanData;
    private readonly Func<PurgeOptionsView, PurgePlanView> _planData;
    private readonly Func<PurgeOptionsView, PurgeOutcomeView> _applyData;
    private readonly Label _required;
    private readonly ThemedCheckBox _chkIde;
    private readonly ThemedCheckBox _chkProject;
    private readonly ThemedCheckBox _chkDesktop;
    private readonly ThemedCheckBox _chkDesktopData;
    private readonly ThemedCheckBox _chkBrowser;
    private readonly ThemedCheckBox _chkThird;
    private readonly ThemedCheckBox _chkManaged;
    private readonly Label _accountsHeading;
    private readonly Control[] _accountChoices;
    private readonly RadioButton _keepAccounts;
    private readonly RadioButton _deleteAccounts;
    private readonly Label _notes;
    private readonly TextBox _preview;
    private readonly Label _selectedSummary;
    private readonly Label _status;
    private readonly DangerButton _apply;
    private readonly SecondaryButton _close;
    private readonly SecondaryButton _refresh;
    private readonly List<Control[]> _rows;
    private readonly HashSet<Control> _hidden = [];
    private PurgeScanView _scan = PurgeScanView.Empty;
    private PurgePlanView _plan = PurgePlanView.Invalid("nothing");
    private bool _busy;
    private bool _scanning;
    private bool _hasScan;
    internal Task ScanCompletion { get; private set; } = Task.CompletedTask;
    internal Task ApplyCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Whether apply finished and the main window should refresh.</summary>
    public bool Changed { get; private set; }

    /// <summary>Whether imported-account backups were kept.</summary>
    public bool AccountsKept { get; private set; } = true;

    public PurgeDialog(Engine engine) : this(
        () => PurgeData.Scan(engine), opts => PurgeData.Plan(engine, opts),
        opts => PurgeData.Apply(engine, opts)) { }

    internal PurgeDialog(Func<PurgeScanView> scan,
        Func<PurgeOptionsView, PurgePlanView> plan,
        Func<PurgeOptionsView, PurgeOutcomeView> apply)
    {
        _scanData = scan;
        _planData = plan;
        _applyData = apply;
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
        DoubleBuffered = true;

        var layout = new DialogLayout(this, textWidth: 520);

        var intro = layout.Text(Loc.T("purge.intro"), Theme.FontBody, Theme.TextSecondary);
        _required = layout.Text(Loc.T("purge.scan.busy"), Theme.FontBody, Theme.TextPrimary);

        _chkIde = PlaceCheck(layout, "purge.opt.ide", true, "purgeIde");
        _chkProject = PlaceCheck(layout, "purge.opt.project", false, "purgeProject");
        _chkDesktop = PlaceCheck(layout, "purge.opt.desktop", false, "purgeDesktop");
        _chkDesktopData = PlaceCheck(layout, "purge.opt.desktop-data", false, "purgeDesktopData");
        _chkBrowser = PlaceCheck(layout, "purge.opt.browser", false, "purgeBrowser");
        _chkThird = PlaceCheck(layout, "purge.opt.third", false, "purgeThird");
        _chkManaged = PlaceCheck(layout, "purge.opt.managed", false, "purgeManaged");

        _accountsHeading = layout.Text(" ", Theme.FontBody, Theme.TextPrimary);
        _keepAccounts = new RadioButton { Name = "purgeKeepAccounts", Checked = true };
        _deleteAccounts = new RadioButton { Name = "purgeDeleteAccounts" };
        layout.Radio(_keepAccounts, Loc.T("purge.accounts.keep"));
        var keepHint = layout.Text(Loc.T("purge.accounts.keepHint"), Theme.FontSmall, Theme.TextMuted);
        layout.Radio(_deleteAccounts, Loc.T("purge.accounts.delete"));
        var deleteHint = layout.Text(Loc.T("purge.accounts.deleteHint"), Theme.FontSmall, Theme.TextMuted);
        _accountChoices = [_keepAccounts, keepHint, _deleteAccounts, deleteHint];
        Controls.AddRange(_accountChoices);
        _deleteAccounts.CheckedChanged += (_, _) => RefreshPlan();

        _selectedSummary = layout.Text(" ", Theme.FontSmall, Theme.TextMuted);
        _preview = new TextBox
        {
            Name = "purgePreview",
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            BackColor = Theme.BgSurface,
            ForeColor = Theme.TextSecondary,
            Font = Theme.FontSmall,
            Location = new Point(layout.Left, layout.Y),
            Size = new Size(layout.TextWidth, layout.Scale(132)),
        };
        layout.Advance(_preview);
        _notes = layout.Text(" ", Theme.FontSmall, Theme.TextMuted);
        _status = layout.Text(" ", Theme.FontBody, Theme.TextPrimary);

        _apply = new DangerButton
        {
            Text = Loc.T("purge.apply"),
            Name = "purgeApply",
        };
        _close = new SecondaryButton { Text = Loc.T("common.cancel") };
        _refresh = new SecondaryButton { Text = Loc.T("purge.scan.refresh"), Name = "purgeRefresh" };

        Controls.AddRange([intro, _required, _chkIde, _chkProject, _chkDesktop,
            _chkDesktopData, _chkBrowser, _chkThird, _chkManaged, _accountsHeading,
            _selectedSummary, _preview, _notes, _status, _apply, _close, _refresh]);

        layout.ActionRow(_refresh, _close, _apply);
        _rows = Controls.Cast<Control>().GroupBy(c => c.Top).OrderBy(g => g.Key)
            .Select(g => g.ToArray()).ToList();
        _apply.Click += (_, _) => ApplyCompletion = ApplyAsync();
        _refresh.Click += (_, _) => ScanCompletion = RefreshScanAsync();
        _close.Click += (_, _) => Close();
        CancelButton = _close;
        Shown += (_, _) => ScanCompletion = RefreshScanAsync();
        FormClosing += (_, e) => e.Cancel = _busy;
        UpdateAvailability();
        Reflow();
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
        }
        return string.Join("\n", lines);
    }

    private PurgeOptionsView CurrentOptions() => new(
        _chkIde.Checked,
        _chkProject.Checked,
        _chkDesktop.Checked,
        _chkThird.Checked,
        _chkManaged.Checked,
        _deleteAccounts.Checked,
        _chkDesktopData.Checked,
        _chkBrowser.Checked);

    private async Task RefreshScanAsync()
    {
        if (_scanning || _busy || IsDisposed) return;
        _scanning = true;
        _required.Text = Loc.T("purge.scan.busy");
        _notes.Text = _selectedSummary.Text = " ";
        _preview.Clear();
        UpdateAvailability();
        Reflow();
        try
        {
            var scan = await Task.Run(_scanData).ConfigureAwait(true);
            if (IsDisposed || Disposing) return;
            _scan = scan;
            _hasScan = true;
            _required.Text = RequiredLines();
        }
        catch (Exception ex)
        {
            if (IsDisposed || Disposing) return;
            _hasScan = false;
            _required.Text = Loc.T("purge.scan.failed", ex.Message);
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                _scanning = false;
                UpdateAvailability();
                RefreshPlan();
                Reflow();
            }
        }
    }

    private void RefreshPlan()
    {
        if (_busy || _scanning || !_hasScan) return;
        _plan = PurgeData.PlanFromScan(_scan, CurrentOptions());
        PaintPlan();
    }

    private void PaintPlan()
    {
        var notes = new List<string>();
        foreach (var p in _plan.Problems)
            notes.Add(PurgeData.ProblemText(p, _plan));
        foreach (var w in _plan.Warnings)
            notes.Add(PurgeData.WarningText(w));
        if (_plan.Items.Any(i => PurgeElevation.TargetForPath(i.Path) is not null))
            notes.Add(Loc.T("purge.warn.admin"));
        _selectedSummary.Text = Loc.T("purge.selected", _plan.Items.Count, HistoryCleanup.FormatBytes(_plan.TotalBytes));
        _preview.Text = string.Join(Environment.NewLine, _plan.Items.Select(i =>
            i.Kind == "json-entries" ? Loc.T("purge.preview.entries", i.Path) : i.Path));
        _notes.Text = notes.Count == 0 ? " " : string.Join("\n\n", notes);
        _notes.ForeColor = _plan.Problems.Count > 0 ? Theme.UsageHigh : Theme.TextMuted;
        _apply.Enabled = !_busy && !_scanning && _hasScan && _plan.CanApply;
        Reflow();
    }

    private void Reflow()
    {
        SuspendLayout();
        int y = Theme.Scale(this, 20) + AutoScrollPosition.Y;
        foreach (var row in _rows)
        {
            var shown = row.Where(c => !_hidden.Contains(c)).ToArray();
            if (shown.Length == 0) continue;
            if (row.Contains(_apply)) y += Theme.Scale(this, 12);
            foreach (var control in shown)
            {
                if (control is Label label)
                    label.Size = label.GetPreferredSize(new Size(label.MaximumSize.Width, 0));
                control.Top = y;
            }
            y += shown.Max(c => c.Height) + Theme.Scale(this, 12);
        }
        FitToScreen();
        ResumeLayout();
    }

    private void FitToScreen()
    {
        int contentHeight = _apply.Bottom + Theme.Scale(this, 24) - AutoScrollPosition.Y;
        int maxHeight = Math.Max(Theme.Scale(this, 200), Screen.FromControl(this).WorkingArea.Height - Theme.Scale(this, 80));
        AutoScroll = true;
        AutoScrollMinSize = new Size(0, contentHeight);
        ClientSize = new Size(Theme.Scale(this, 568) + SystemInformation.VerticalScrollBarWidth,
            Math.Min(contentHeight, maxHeight));
        // The first scan can make an already centered loading window taller.
        // Keep its action row on screen after that resize.
        var area = Screen.FromControl(this).WorkingArea;
        if (Visible && Bounds.IntersectsWith(area))
            Location = new Point(
                Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
                Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    private async Task ApplyAsync()
    {
        if (_busy || _scanning || !_hasScan || !_plan.CanApply) return;
        var opts = CurrentOptions();
        bool attempted = false;
        SetBusy(true, Loc.T("purge.check.busy"));
        try
        {
            _plan = await Task.Run(() => _planData(opts)).ConfigureAwait(true);
            PaintPlan();
            if (!_plan.CanApply) return;
            var preview = string.Join("\n", _plan.Items.Take(12).Select(i => "• " + i.Path));
            if (_plan.Items.Count > 12) preview += "\n• …";
            if (MessageBox.Show(this, Loc.T("purge.confirm.body", preview), Loc.T("purge.title"),
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

            attempted = true;
            SetBusy(true, Loc.T("purge.apply.busy"));
            var outcome = await Task.Run(() => _applyData(opts)).ConfigureAwait(true);
            Changed = true;
            AccountsKept = !opts.RemoveImportedAccounts;
            var retryTargets = PurgeElevation.RetryTargets(outcome);
            if (retryTargets.Count > 0)
            {
                var paths = outcome.Failed.Where(f => f.Reason == "permission-denied" &&
                    PurgeElevation.TargetForPath(f.Path) is not null).Select(f => "• " + f.Path);
                if (MessageBox.Show(this, Loc.T("purge.admin.confirm", string.Join("\n", paths)),
                    Loc.T("purge.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                {
                    SetBusy(true, Loc.T("purge.admin.busy"));
                    var retry = await PurgeElevation.RetryAsync(retryTargets).ConfigureAwait(true);
                    outcome = PurgeElevation.Reconcile(outcome, retryTargets, retry);
                }
                else
                {
                    outcome = PurgeElevation.Reconcile(outcome, retryTargets, PurgeRetryResult.Cancelled);
                }
            }
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
                    string.Join("\n", outcome.Failed.Select(f => $"{f.Path}: {PurgeData.FailureText(f.Reason)}")),
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
            SetBusy(false, attempted ? _status.Text : " ");
            if (attempted)
            {
                ScanCompletion = RefreshScanAsync();
                await ScanCompletion.ConfigureAwait(true);
            }
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _status.Text = status;
        _close.Enabled = !busy;
        UpdateAvailability();
        Reflow();
    }

    private void UpdateAvailability()
    {
        bool ready = !_busy && !_scanning && _hasScan;
        void Option(Control control, string group, bool alwaysShow = false)
        {
            bool exists = _scan.Group(group) is { Items.Count: > 0 };
            control.Enabled = ready && exists;
            ShowRow(control, alwaysShow || (_hasScan && exists));
        }
        Option(_chkIde, "ide", true);
        Option(_chkProject, "project-locals", true);
        Option(_chkDesktop, "desktop");
        Option(_chkDesktopData, "desktop-data");
        Option(_chkBrowser, "browser");
        Option(_chkThird, "third-party");
        Option(_chkManaged, "managed");
        bool accounts = _scan.ImportedAccounts > 0 || _scan.Group("accounts") is { Items.Count: > 0 };
        _accountsHeading.Text = accounts ? Loc.T("purge.accounts.heading", _scan.ImportedAccounts) : Loc.T("purge.accounts.none");
        ShowRow(_accountsHeading, _hasScan);
        foreach (var control in _accountChoices)
        {
            control.Enabled = ready && accounts;
            ShowRow(control, _hasScan && accounts);
        }
        _refresh.Enabled = !_busy && !_scanning;
        _apply.Enabled = ready && _plan.CanApply;
    }

    private void ShowRow(Control control, bool show)
    {
        if (show) _hidden.Remove(control);
        else _hidden.Add(control);
        control.Visible = show;
    }
}
