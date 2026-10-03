using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

internal sealed class LoginDialog : Form
{
    private readonly Engine _engine;
    private readonly ILoginProcess _process;
    private readonly Label _status;
    private readonly ThemedCheckBox _activate;
    private readonly PrimaryButton _start;
    private readonly SecondaryButton _recover;
    private readonly SecondaryButton _cancel;
    private readonly ProgressBar _progress;
    private readonly LinkLabel _discard;
    private CancellationTokenSource? _cancellation;
    private JsonNode? _pending;
    private bool _busy;
    private bool _committing;
    private bool _closeAfterCancel;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    public bool Changed { get; private set; }

    public LoginDialog(Engine engine) : this(engine, new LoginProcess()) { }

    internal LoginDialog(Engine engine, ILoginProcess process)
    {
        _engine = engine;
        _process = process;
        Text = Loc.T("login.title");
        Name = "loginDialog";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
        Font = Theme.FontBody;
        Icon = AppIcon.Get();
        var layout = new DialogLayout(this, 540);
        var intro = layout.Text(Loc.T("login.intro"), Theme.FontBody, Theme.TextPrimary);
        var hint = layout.Text(Loc.T("login.browserHint"), Theme.FontSmall, Theme.TextSecondary);
        _activate = new ThemedCheckBox { Name = "loginActivate", Text = Loc.T("login.activate"), Checked = true,
            Location = new Point(layout.Left, layout.Y), Size = new Size(layout.TextWidth, layout.Scale(32)) };
        layout.Advance(_activate);
        _status = new Label { Name = "loginStatus", Text = Loc.T("login.ready"), ForeColor = Theme.TextSecondary,
            Location = new Point(layout.Left, layout.Y), Size = new Size(layout.TextWidth, layout.Scale(104)) };
        layout.Advance(_status);
        _discard = new LinkLabel { Name = "loginDiscard", Text = Loc.T("login.discard"),
            Location = new Point(layout.Left, layout.Y), AutoSize = true, Enabled = false,
            LinkColor = Theme.TextSecondary, ActiveLinkColor = Theme.TextPrimary };
        layout.Advance(_discard);
        _progress = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0,
            Location = new Point(layout.Left, layout.Y), Size = new Size(layout.TextWidth, layout.Scale(8)) };
        layout.Advance(_progress);
        _start = new PrimaryButton { Name = "loginStart", Text = Loc.T("login.start") };
        _recover = new SecondaryButton { Name = "loginRecover", Text = Loc.T("login.recover"), Enabled = false };
        _cancel = new SecondaryButton { Name = "loginCancel", Text = Loc.T("common.cancel") };
        layout.ActionRow(_recover, _start, _cancel);
        Controls.AddRange([intro, hint, _activate, _status, _discard, _progress, _recover, _start, _cancel]);
        AcceptButton = _start;
        CancelButton = _cancel;
        _start.Click += (_, _) => Completion = RunAsync(recover: false);
        _recover.Click += (_, _) => Completion = RunAsync(recover: true);
        _cancel.Click += (_, _) => Close();
        _discard.LinkClicked += async (_, _) => await DiscardAsync();
        Shown += async (_, _) => await FindPendingAsync(showNotice: true);
        FormClosing += (_, e) =>
        {
            if (!_busy) return;
            e.Cancel = true;
            _closeAfterCancel = true;
            if (_committing) return;
            _status.Text = Loc.T("login.cancelling");
            _cancel.Enabled = false;
            _cancellation?.Cancel();
        };
    }

    private async Task FindPendingAsync(bool showNotice = false)
    {
        try
        {
            var pending = await Task.Run(() => _engine.Call("login_pending"));
            if (IsDisposed || _busy) return;
            _pending = pending["pending"]?.AsArray().FirstOrDefault()?.DeepClone();
            _recover.Enabled = _pending is not null && _pending["committed"]?.GetValue<bool>() != true;
            _discard.Enabled = _pending is not null;
            if (_pending is not null && showNotice) _status.Text = Loc.T("login.pending");
        }
        catch (Exception ex) { if (!IsDisposed && !_busy) _status.Text = ErrorText(ex); }
    }

    internal async Task RunAsync(bool recover)
    {
        if (_busy || (recover && _pending is null)) return;
        _busy = true;
        _committing = false;
        _closeAfterCancel = false;
        _start.Enabled = _recover.Enabled = _activate.Enabled = _discard.Enabled = false;
        _progress.MarqueeAnimationSpeed = 25;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            var workflow = new LoginWorkflow(_engine, _process);
            var result = await workflow.RunAsync(_activate.Checked && !recover, recover ? _pending : null, key =>
            {
                _status.Text = Loc.T(key);
                _committing = key == "login.saving";
                _cancel.Enabled = !_committing;
            }, cancellation.Token);
            Changed = true;
            string who = Pii.MaskEmail(result["email"]?.GetValue<string>() ?? "");
            _status.Text = Loc.T(result["existing"]?.GetValue<bool>() == true ? "login.updated" : "login.added", who);
            if (result["switched"]?.GetValue<bool>() == true) _status.Text += "\n" + Loc.T("login.switched");
            else if (result["currentChanged"]?.GetValue<bool>() == true) _status.Text += "\n" + Loc.T("login.currentChanged");
            if (result["switchError"]?.GetValue<string>() is { Length: > 0 } error)
                _status.Text += "\n" + Loc.T("login.switchFailed", error);
            if (result["cleanupPending"]?.GetValue<bool>() == true) _status.Text += "\n" + Loc.T("login.cleanupPending");
            _start.Visible = _recover.Visible = _discard.Visible = false;
            _cancel.Text = Loc.T("login.done");
        }
        catch (OperationCanceledException) { _status.Text = Loc.T("login.cancelled"); }
        catch (Exception ex) { _status.Text = ErrorText(ex); }
        finally
        {
            // Backup may have added the original account even if authorization
            // failed, so the owner must refresh after every attempt.
            Changed = true;
            _cancellation = null;
            _busy = false;
            _progress.MarqueeAnimationSpeed = 0;
            _cancel.Enabled = true;
            _activate.Enabled = _start.Enabled = true;
            if (_closeAfterCancel) Close();
            else if (_start.Visible) await FindPendingAsync();
        }
    }

    private async Task DiscardAsync()
    {
        if (_busy || _pending is null) return;
        if (MessageBox.Show(this, Loc.T("login.discardConfirm"), Text,
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _busy = true;
        _committing = true;
        _start.Enabled = _recover.Enabled = _cancel.Enabled = _discard.Enabled = false;
        try
        {
            var pending = _pending;
            await Task.Run(() =>
            {
                LoginProcess.EnsureStopped(pending["configDir"]!.GetValue<string>());
                _engine.Call("login_discard", new { id = pending["id"]!.GetValue<string>() });
            });
            _status.Text = Loc.T("login.discarded");
        }
        catch (Exception ex) { _status.Text = ErrorText(ex); }
        finally
        {
            _busy = _committing = false;
            _start.Enabled = _cancel.Enabled = true;
            if (_closeAfterCancel) Close();
            else await FindPendingAsync();
        }
    }

    internal static string ErrorText(Exception exception)
    {
        string message = exception.Message;
        foreach (string code in new[] { "login-current-unreadable", "login-incomplete", "login-invalid-identity",
            "login-identity-mismatch", "login-in-progress", "login-import-rollback-failed" })
            if (message.Contains(code, StringComparison.Ordinal)) return Loc.T("login.error." + code);
        return Loc.T("login.failed", message);
    }
}
