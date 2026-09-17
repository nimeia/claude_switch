using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// A view onto a supervised Claude Code run.
/// </summary>
/// <remarks>
/// <para>
/// The other launch paths in this app hand a session to a terminal and lose
/// sight of it. This one keeps the agent as an ACP subprocess, so an
/// interruption is a typed event rather than a stalled pane — and can be
/// answered by resuming the same conversation instead of asking the user to
/// notice and retype.
/// </para>
/// <para>
/// <b>The window does not own the run.</b> <see cref="BackgroundRuns"/> does.
/// Closing this detaches: the run keeps going and can be reopened, because a
/// supervised run that dies when someone closes a window is not supervised.
/// </para>
/// <para>
/// The status strip is the point: it names what interrupted the run and what is
/// being done about it. A silent retry is indistinguishable from a hang, which
/// is the failure mode this feature exists to remove.
/// </para>
/// </remarks>
internal sealed class AgentWindow : Form
{
    private readonly Engine _engine;
    private readonly AgentRunStore _store;
    private readonly string _workDir;
    private readonly string? _configDir;
    private readonly int? _accountNumber;
    private readonly string _accountLabel;
    private readonly Func<JsonNode, Task<string?>> _permission;

    private readonly TranscriptHost _transcript;
    private readonly TextBox _prompt = new();
    private readonly PrimaryButton _run = new();
    private readonly SecondaryButton _stop = new();
    private readonly ComboBox _mode = new();
    private readonly CheckBox _autoContinue = new();
    private readonly ComboBox _onQuota = new();
    private readonly Label _status = new();
    private readonly Panel _statusBar = new();

    private LiveRun? _live;
    /// <summary>Session to continue when the next run starts, if any.</summary>
    private string? _resumeSessionId;

    public AgentWindow(
        Engine engine,
        AgentRunStore store,
        string workDir,
        string? configDir,
        int? accountNumber,
        string accountLabel,
        Func<JsonNode, Task<string?>> permission)
    {
        _engine = engine;
        _store = store;
        _workDir = workDir;
        _configDir = configDir;
        _accountNumber = accountNumber;
        _accountLabel = accountLabel;
        _permission = permission;
        _transcript = new TranscriptHost(workDir);

        Text = Loc.T("acp.window.title", Path.GetFileName(workDir.TrimEnd('\\', '/')));
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(Theme.Scale(this, 640), Theme.Scale(this, 460));
        ClientSize = new Size(Theme.Scale(this, 860), Theme.Scale(this, 620));
        BackColor = Theme.BgApp;
        Font = Theme.FontBody;
        ForeColor = Theme.TextPrimary;
        Icon = AppIcon.Get();

        BuildLayout();
        UpdateStatus(Loc.T("acp.status.idle"), Theme.TextSecondary);
        UpdateBusy(false);
    }

    /// <summary>Whether the transcript shows everything it was given. For the layout probe.</summary>
    internal bool IsTranscriptSettled => _transcript.IsSettled;

    /// <summary>Show the window already watching a run that is in flight.</summary>
    public void Attach(LiveRun live)
    {
        _live = live;
        _resumeSessionId = live.Runner.SessionId;

        foreach (var line in live.Backlog) _transcript.Append(line);
        UpdateStatus(live.Status, Theme.TextSecondary);
        UpdateBusy(!live.Finished);

        live.Line += OnLine;
        live.StatusChanged += OnStatus;
        live.Completed += OnCompleted;

        if (live.Finished && live.Report is { } report) OnCompleted(report);
    }

    /// <summary>
    /// Prefill for resuming a journaled run: the original prompt, and the
    /// session it will continue.
    /// </summary>
    public void PrepareResume(AgentRunRecord record)
    {
        _resumeSessionId = record.SessionId;
        _prompt.Text = Loc.T("acp.resume.prompt");
        _transcript.Append(new RunLine(RunLineKind.Notice, Loc.T("acp.resume.banner", record.Title, record.UpdatedLocal)));
        if (record.LastError is { Length: > 0 } why)
        {
            _transcript.Append(new RunLine(RunLineKind.Notice, $"  {why}"));
        }
    }

    /// <summary>
    /// Fill the transcript with a representative run, for the layout probe.
    /// </summary>
    /// <remarks>
    /// An empty transcript says nothing about how a run reads. The sample goes
    /// through the same mapping a live run does; see <see cref="AgentSampleRun"/>.
    /// </remarks>
    internal void ShowSampleRun()
    {
        foreach (var line in AgentSampleRun.Lines(_workDir)) _transcript.Append(line);
        UpdateStatus(Loc.T("acp.status.running"), Theme.TextSecondary);
        UpdateBusy(true);
    }

    private void BuildLayout()
    {
        int pad = Theme.Scale(this, 12);

        // ── status strip ────────────────────────────────────────────────
        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = Theme.FontSmall;
        _status.Padding = new Padding(pad, 0, pad, 0);
        _status.BackColor = Theme.BgHeader;

        _statusBar.Dock = DockStyle.Top;
        _statusBar.Height = Theme.Scale(this, 26);
        // The header above uses the same fill, so without a rule the two bands
        // read as one and the run state stops looking like a live indicator.
        // Drawn as a one-pixel gap the docked label cannot cover — a Paint
        // handler here would be painted over by that label.
        _statusBar.BackColor = Theme.Border;
        _statusBar.Padding = new Padding(0, 0, 0, 1);
        _statusBar.Controls.Add(_status);

        // ── controls row ────────────────────────────────────────────────
        _mode.DropDownStyle = ComboBoxStyle.DropDownList;
        _mode.Width = Theme.Scale(this, 150);
        _mode.Items.AddRange(["default", "acceptEdits", "plan", "dontAsk", "bypassPermissions"]);
        _mode.SelectedIndex = 0;

        _autoContinue.Text = Loc.T("acp.autoContinue");
        _autoContinue.Checked = true;
        _autoContinue.AutoSize = true;
        _autoContinue.ForeColor = Theme.TextPrimary;

        // Quota is the one interruption a retry cannot fix — only time or a
        // different account moves it, so it gets its own choice rather than
        // riding on the auto-continue toggle.
        _onQuota.DropDownStyle = ComboBoxStyle.DropDownList;
        _onQuota.Width = Theme.Scale(this, 180);
        _onQuota.Items.AddRange([
            Loc.T("acp.onQuota.wait"),
            Loc.T("acp.onQuota.switch"),
            Loc.T("acp.onQuota.stop"),
        ]);
        _onQuota.SelectedIndex = 0;

        _run.Text = Loc.T("acp.run");
        _run.Click += (_, _) => StartRun();

        _stop.Text = Loc.T("acp.stop");
        _stop.Click += (_, _) => StopRun();

        _prompt.Multiline = true;
        _prompt.ScrollBars = ScrollBars.Vertical;
        _prompt.Dock = DockStyle.Fill;
        _prompt.BackColor = Theme.BgSurface;
        _prompt.ForeColor = Theme.TextPrimary;
        _prompt.BorderStyle = BorderStyle.FixedSingle;
        // Ctrl+Enter submits; plain Enter keeps writing, because prompts are
        // routinely several lines long.
        _prompt.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                StartRun();
            }
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        buttons.Controls.AddRange([_mode, _onQuota, _autoContinue, _stop, _run]);

        var promptRow = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scale(this, 108),
            Padding = new Padding(pad),
            BackColor = Theme.BgApp,
        };
        var controlRow = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scale(this, 40),
            BackColor = Theme.BgApp,
        };
        controlRow.Controls.Add(buttons);
        promptRow.Controls.Add(_prompt);
        promptRow.Controls.Add(controlRow);

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Scale(this, 34),
            Text = Loc.T("acp.header", _accountLabel, _workDir),
            Font = Theme.FontSmall,
            ForeColor = Theme.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(pad, 0, pad, 0),
            BackColor = Theme.BgHeader,
        };

        Controls.Add(_transcript);
        Controls.Add(promptRow);
        Controls.Add(_statusBar);
        Controls.Add(header);
    }

    private void StartRun()
    {
        if (_live is { Finished: false }) return;
        string prompt = _prompt.Text.Trim();
        if (prompt.Length == 0) return;

        var proxy = SessionMode.ResolveProxy(_engine, _accountNumber);
        var launch = new AcpLaunch
        {
            WorkingDirectory = _workDir,
            ConfigDir = _configDir,
            Proxy = proxy.Url,
            ScrubProxy = proxy.Scrub,
            ExtraEnv = proxy.Env,
            ScrubEnv = proxy.ScrubKeys,
        };

        var policy = BuildPolicy();

        DetachFromLive();
        _prompt.Clear();

        var live = BackgroundRuns.Start(
            _engine,
            _store,
            launch,
            prompt,
            _accountLabel,
            _accountNumber,
            _mode.SelectedItem as string,
            policy,
            _permission,
            _resumeSessionId);

        Attach(live);
    }

    /// <summary>
    /// Turn the two on-screen choices into a policy override.
    /// </summary>
    /// <remarks>
    /// Returns null when nothing is overridden, so the engine's defaults stay
    /// the single definition of normal behaviour.
    /// </remarks>
    private JsonObject? BuildPolicy() => BuildPolicy(_autoContinue.Checked, _onQuota.SelectedIndex);

    /// <summary>
    /// Policy JSON the window sends for the given control values.
    /// </summary>
    /// <remarks>
    /// Auto-continue off is a one-shot: the blip ladder, the long-retry
    /// follow-on, and quota waits all have to be zeroed. <c>maxAttempts: 0</c>
    /// alone still climbs the long-retry ladder (three 10-minute waits).
    /// </remarks>
    internal static JsonObject? BuildPolicy(bool autoContinue, int onQuotaIndex)
    {
        var policy = new JsonObject();

        if (!autoContinue)
        {
            policy["maxAttempts"] = 0;
            policy["continueOnTruncation"] = false;
            policy["maxLongRetries"] = 0;
            policy["maxRateLimitWaits"] = 0;
        }

        string onQuota = onQuotaIndex switch
        {
            1 => "switch",
            2 => "stop",
            _ => "wait",
        };
        if (onQuota != "wait") policy["onRateLimit"] = onQuota;

        return policy.Count == 0 ? null : policy;
    }

    private void StopRun()
    {
        if (_live is not { Finished: false } live) return;
        UpdateStatus(Loc.T("acp.status.stopping"), Theme.Warning);
        live.Cancel();
    }

    private void OnLine(RunLine line) => RunOnUi(() => _transcript.Append(line));

    private void OnStatus(string text) =>
        RunOnUi(() => UpdateStatus(text, Theme.TextSecondary));

    private void OnCompleted(AcpRunReport report) => RunOnUi(() =>
    {
        UpdateBusy(false);
        // Follow-up prompts continue the same conversation.
        _resumeSessionId = report.SessionId;
        UpdateStatus(
            _status.Text,
            report.Succeeded ? Theme.Success : Theme.Warning);
    });

    private void UpdateBusy(bool busy)
    {
        _run.Enabled = !busy;
        _stop.Enabled = busy;
        _prompt.Enabled = !busy;
        _mode.Enabled = !busy;
        _autoContinue.Enabled = !busy;
        _onQuota.Enabled = !busy;

        // The themed buttons paint their own foreground, so a flat Enabled=false
        // leaves the label at full contrast and the button still looks clickable.
        _run.ForeColor = busy ? Theme.TextDisabled : Theme.TextOnPrimary;
        _stop.ForeColor = busy ? Theme.TextPrimary : Theme.TextDisabled;

        // Spinners on unfinished tool calls turn into "not finished" once the run ends.
        _transcript.SetBusy(busy);
    }

    private void UpdateStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    /// <summary>
    /// Marshal onto the UI thread. Run events arrive on background tasks, and a
    /// cross-thread control touch is a crash rather than a glitch.
    /// </summary>
    private void RunOnUi(Action action)
    {
        if (IsDisposed || Disposing) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Window closed while an update was in flight.
        }
    }

    private void DetachFromLive()
    {
        if (_live is not { } live) return;
        live.Line -= OnLine;
        live.StatusChanged -= OnStatus;
        live.Completed -= OnCompleted;
        _live = null;
    }

    /// <summary>
    /// Closing detaches; it does not stop the run.
    /// </summary>
    /// <remarks>
    /// Deliberate, and the reason this window is a view: work started here is
    /// meant to outlive a moment of inattention. The run stays reachable through
    /// the main window, and <see cref="BackgroundRuns.ShutdownAll"/> is what
    /// stops it when the app itself goes away.
    /// </remarks>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        DetachFromLive();
        base.OnFormClosing(e);
    }
}
