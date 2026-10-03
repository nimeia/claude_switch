namespace ClaudeSwitch.App;

internal sealed class ClaudeInstallDialog : Form
{
    private readonly IClaudeInstallService _service;
    private readonly DialogLayout _layout;
    private readonly Label _summary, _status;
    private readonly TextBox _details;
    private readonly ProgressBar _progress;
    private readonly PrimaryButton _start;
    private readonly SecondaryButton _refresh, _login, _close;
    private readonly LinkLabel _expand, _copy;
    private readonly ClaudeInstallLog _log = new();
    private readonly System.Windows.Forms.Timer _logTimer = new() { Interval = 200 };
    private CancellationTokenSource? _cancellation;
    private ClaudeInstallation? _installation;
    private bool _busy, _canCancel = true, _closeWhenDone, _expanded;
    private string _inventory = "", _output = "";
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal bool Busy => _busy;
    public bool LoginRequested { get; private set; }

    internal ClaudeInstallDialog(IClaudeInstallService service)
    {
        _service = service;
        Name = "claudeInstallDialog";
        Text = Loc.T("install.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
        Font = Theme.FontBody;
        Icon = AppIcon.Get();
        _layout = new DialogLayout(this, 640);
        var intro = _layout.Text(Loc.T("install.intro"), Theme.FontBody, Theme.TextSecondary);
        _summary = new Label { Name = "installSummary", Location = new(_layout.Left, _layout.Y),
            Size = new(_layout.TextWidth, _layout.Scale(68)) };
        _layout.Advance(_summary);
        _status = new Label { Name = "installStatus", Location = new(_layout.Left, _layout.Y),
            Size = new(_layout.TextWidth, _layout.Scale(90)), ForeColor = Theme.TextSecondary };
        _layout.Advance(_status);
        _expand = new LinkLabel { Name = "installExpand", Text = Loc.T("install.details"), AutoSize = true,
            Location = new(_layout.Left, _layout.Y), LinkColor = Theme.TextSecondary };
        _copy = new LinkLabel { Text = Loc.T("install.copy"), AutoSize = true,
            Location = new(_layout.Left + _layout.Scale(230), _layout.Y), LinkColor = Theme.TextSecondary };
        _layout.Advance(_expand);
        _details = new TextBox { Name = "installDetails", ReadOnly = true, Multiline = true,
            ScrollBars = ScrollBars.Both, WordWrap = false, Visible = false,
            Location = new(_layout.Left, _layout.Y), Size = new(_layout.TextWidth, _layout.Scale(190)),
            BackColor = Theme.BgSurface, ForeColor = Theme.TextSecondary };
        _progress = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0,
            Location = new(_layout.Left, _layout.Y), Size = new(_layout.TextWidth, _layout.Scale(8)) };
        _layout.Advance(_progress);
        _start = new PrimaryButton { Name = "installStart", Text = Loc.T("install.update"), Enabled = false };
        _refresh = new SecondaryButton { Name = "installRefresh", Text = Loc.T("install.refresh") };
        _login = new SecondaryButton { Name = "installLogin", Text = Loc.T("install.login"), Enabled = false };
        _close = new SecondaryButton { Name = "installClose", Text = Loc.T("common.close") };
        _layout.ActionRow(_refresh, _start, _login, _close);
        Controls.AddRange([intro, _summary, _status, _expand, _copy, _details, _progress, _refresh, _start, _login, _close]);
        AcceptButton = _start;
        CancelButton = _close;
        _expand.LinkClicked += (_, _) => ToggleDetails();
        _copy.LinkClicked += (_, _) =>
        {
            try { FlushLog(); Clipboard.SetText(_details.Text.Length == 0 ? _status.Text : _details.Text); }
            catch (System.Runtime.InteropServices.ExternalException) { _status.Text = Loc.T("install.copyFailed"); }
        };
        _refresh.Click += (_, _) => Completion = RefreshAsync();
        _start.Click += (_, _) => Completion = RunAsync();
        _close.Click += (_, _) => Close();
        _login.Click += (_, _) => { LoginRequested = true; Close(); };
        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();
        Disposed += (_, _) => _logTimer.Dispose();
        Shown += (_, _) => Completion = RefreshAsync();
        FormClosing += (_, e) =>
        {
            if (!_busy) return;
            e.Cancel = true;
            _closeWhenDone = true;
            if (_canCancel) _cancellation?.Cancel();
            else _status.Text = Loc.T("install.waitToClose");
        };
    }

    internal async Task RefreshAsync()
    {
        if (_busy) return;
        BeginWork();
        using var cancel = new CancellationTokenSource();
        _cancellation = cancel;
        try
        {
            _status.Text = Loc.T("install.checking");
            _installation = await Task.Run(() => _service.DetectAsync(cancel.Token));
            ShowInstallation();
            _status.Text = _installation.Problem ?? Loc.T(_installation.Copies.Count > 1
                ? "install.multiple" : "install.ready");
        }
        catch (OperationCanceledException) { _status.Text = Loc.T("install.cancelled"); }
        catch (Exception ex) { _installation = null; ShowError(ex); }
        finally { EndWork(); }
    }

    internal async Task RunAsync()
    {
        if (_busy || _installation?.CanChange != true) return;
        BeginWork();
        using var cancel = new CancellationTokenSource();
        _cancellation = cancel;
        var before = _installation;
        try
        {
            // Progress is marshalled only for the handful of phase changes;
            // child output is buffered separately and sampled by the UI timer.
            var progress = new Progress<ClaudeInstallProgress>(p =>
            {
                if (IsDisposed || !_busy) return;
                _canCancel = p.CanCancel;
                _status.Text = Loc.T(p.Key);
                _close.Text = Loc.T(p.CanCancel ? "common.cancel" : "common.close");
                LayoutActions();
            });
            _installation = await Task.Run(() => _service.InstallAsync(before,
                p => ((IProgress<ClaudeInstallProgress>)progress).Report(p), _log.Append, cancel.Token));
            ShowInstallation();
            _status.Text = Loc.T(before.Version == _installation.Version ? "install.upToDate" : "install.success",
                _installation.Version?.ToString() ?? "?");
        }
        catch (OperationCanceledException) { _status.Text = Loc.T("install.cancelled"); await RescanAfterFailure(); }
        catch (Exception ex) { ShowError(ex); await RescanAfterFailure(); }
        finally { FlushLog(); EndWork(); }
    }

    private async Task RescanAfterFailure()
    {
        ClaudeCli.InvalidateVersionCache();
        try
        {
            _installation = await Task.Run(() => _service.DetectAsync(CancellationToken.None));
            ShowInstallation();
        }
        catch { _installation = null; }
    }
    private void BeginWork()
    {
        _busy = true; _canCancel = true; _closeWhenDone = false;
        _start.Enabled = _refresh.Enabled = _login.Enabled = false;
        _progress.MarqueeAnimationSpeed = 25;
    }
    private void EndWork()
    {
        _busy = false; _cancellation = null;
        _refresh.Enabled = true;
        _start.Enabled = _installation?.CanChange == true;
        _login.Enabled = _installation?.Version is not null;
        _progress.MarqueeAnimationSpeed = 0;
        _close.Text = Loc.T("common.close");
        LayoutActions();
        if (_closeWhenDone) Close();
    }
    private void ShowInstallation()
    {
        if (_installation is not { } value) return;
        _summary.Text = value.Source == ClaudeInstallSource.None ? Loc.T("install.absent")
            : Loc.T("install.summary", value.Version?.ToString() ?? Loc.T("install.broken"),
                Loc.T("install.source." + value.Source.ToString().ToLowerInvariant()));
        _start.Text = Loc.T(value.Source == ClaudeInstallSource.None ? "install.install" : "install.update");
        _inventory = Loc.T("install.paths") + Environment.NewLine + string.Join(Environment.NewLine, value.Copies);
        if (value.Manager is not null) _inventory += Environment.NewLine + value.Manager;
        UpdateDetails();
    }
    private void ShowError(Exception ex)
    {
        var safe = new ClaudeInstallLog();
        safe.Append(ex.Message);
        _status.Text = Loc.T("install.failed", safe.Take() ?? "");
        _log.Append(Environment.NewLine + _status.Text + Environment.NewLine);
    }
    private void FlushLog()
    {
        if (_log.Take() is not { } value) return;
        _output = value;
        UpdateDetails();
    }
    private void UpdateDetails() => _details.Text = _inventory + Environment.NewLine + Environment.NewLine + _output;
    internal void ToggleDetails()
    {
        _expanded = !_expanded;
        int offset = (_details.Height + _layout.Scale(12)) * (_expanded ? 1 : -1);
        _details.Visible = _expanded;
        _progress.Top += offset;
        _layout.Y += offset;
        LayoutActions();
    }
    private void LayoutActions() => _layout.ActionRow(_refresh, _start, _login, _close);
}
