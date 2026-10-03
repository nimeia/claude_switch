using System.Diagnostics;
using System.Drawing;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using ClaudeSwitch.App;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

public sealed class LoginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cswitch-login-" + Guid.NewGuid().ToString("N"));
    private readonly Engine _engine;

    public LoginTests()
    {
        Directory.CreateDirectory(_root);
        _engine = new Engine(_root);
    }

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static string Creds(string token) => new JsonObject
    {
        ["claudeAiOauth"] = new JsonObject { ["accessToken"] = token, ["refreshToken"] = "refresh-" + token }
    }.ToJsonString();

    private static string Config(string email) => new JsonObject
    {
        ["oauthAccount"] = new JsonObject { ["emailAddress"] = email, ["accountUuid"] = "uuid-" + email,
            ["organizationUuid"] = "org-" + email }
    }.ToJsonString();

    private void SeedCurrent()
    {
        File.WriteAllText(Path.Combine(_root, ".claude", ".credentials.json"), Creds("original"));
        File.WriteAllText(Path.Combine(_root, ".claude", ".claude.json"), Config("old@x.com"));
    }

    private sealed class FakeProcess : ILoginProcess
    {
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public bool WaitForCancellation { get; init; }
        public bool FailStatus { get; set; }
        public bool FailCheck { get; init; }
        public Action<LoginLaunch>? OnLogin { get; init; }
        public Task CheckAvailableAsync(CancellationToken token) => FailCheck
            ? Task.FromException(new IOException("missing-cli")) : Task.CompletedTask;
        public async Task LoginAsync(LoginLaunch launch, CancellationToken token)
        {
            Started = true;
            OnLogin?.Invoke(launch);
            try
            {
                if (WaitForCancellation) await Task.Delay(Timeout.Infinite, token);
                File.WriteAllText(Path.Combine(launch.ConfigDir, ".credentials.json"), Creds("new"));
                File.WriteAllText(Path.Combine(launch.ConfigDir, ".claude.json"), Config("new@x.com"));
            }
            finally { Stopped = true; }
        }
        public Task<JsonNode> StatusAsync(LoginLaunch launch, CancellationToken token)
        {
            if (FailStatus) throw new IOException("status-failed");
            return Task.FromResult<JsonNode>(new JsonObject { ["loggedIn"] = true, ["authMethod"] = "claude.ai",
                ["email"] = "new@x.com", ["orgId"] = "org-new@x.com", ["configDirectory"] = launch.ConfigDir });
        }
    }

    [Fact]
    public async Task Workflow_backs_up_before_launch_and_imports_without_manual_capture()
    {
        SeedCurrent();
        var steps = new List<string>();
        var process = new FakeProcess { OnLogin = launch =>
        {
            Assert.Single(_engine.Snapshot()["accounts"]!.AsArray());
            Assert.False(File.Exists(Path.Combine(launch.ConfigDir, ".credentials.json")));
            Assert.Contains("ANTHROPIC_API_KEY", launch.ScrubEnv);
        }};
        var result = await new LoginWorkflow(_engine, process).RunAsync(true, null, steps.Add, CancellationToken.None);
        Assert.True(result["switched"]!.GetValue<bool>());
        Assert.Equal(2, _engine.Snapshot()["accounts"]!.AsArray().Count);
        Assert.True(process.Stopped);
        Assert.Equal(new[] { "login.checking", "login.backingUp", "login.waitingBackedUp", "login.verifying", "login.saving" }, steps);
    }

    [Fact]
    public async Task Missing_cli_does_not_begin_backup_or_login()
    {
        SeedCurrent();
        var process = new FakeProcess { FailCheck = true };
        await Assert.ThrowsAsync<IOException>(() => new LoginWorkflow(_engine, process)
            .RunAsync(true, null, _ => { }, CancellationToken.None));
        Assert.False(process.Started);
        Assert.Empty(_engine.Snapshot()["accounts"]!.AsArray());
        Assert.Empty(_engine.Call("login_pending")["pending"]!.AsArray());
    }

    [Fact]
    public async Task Failed_verification_is_recoverable_without_a_second_browser_login()
    {
        SeedCurrent();
        var process = new FakeProcess { FailStatus = true };
        var workflow = new LoginWorkflow(_engine, process);
        await Assert.ThrowsAsync<IOException>(() => workflow.RunAsync(true, null, _ => { }, CancellationToken.None));
        var pending = _engine.Call("login_pending")["pending"]!.AsArray().Single()!.DeepClone();
        Assert.Contains("original", File.ReadAllText(Path.Combine(_root, ".claude", ".credentials.json")));
        var recoveryProcess = new FakeProcess();
        var result = await new LoginWorkflow(_engine, recoveryProcess).RunAsync(false, pending, _ => { }, CancellationToken.None);
        Assert.False(recoveryProcess.Started);
        Assert.False(result["switched"]!.GetValue<bool>());
        Assert.Equal(2, _engine.Snapshot()["accounts"]!.AsArray().Count);
        Assert.Empty(_engine.Call("login_pending")["pending"]!.AsArray());
    }

    [Fact]
    public void Waiting_dialog_keeps_pumping_and_cancel_waits_for_process_cleanup()
    {
        using var ui = new UiScope();
        SeedCurrent();
        var process = new FakeProcess { WaitForCancellation = true };
        using var dialog = new LoginDialog(_engine, process);
        Show(dialog);
        var completion = dialog.RunAsync(false);
        PumpUntil(() => process.Started);
        int ticks = 0;
        using var timer = new System.Windows.Forms.Timer { Interval = 10 };
        timer.Tick += (_, _) => ticks++;
        timer.Start();
        PumpUntil(() => ticks >= 3);
        Assert.False(completion.IsCompleted);
        Assert.False(dialog.Controls["loginStart"]!.Enabled);
        dialog.Close();
        PumpUntil(() => completion.IsCompleted);
        Assert.True(completion.IsCompletedSuccessfully, completion.Exception?.ToString());
        Assert.True(process.Stopped);
        Assert.True(dialog.IsDisposed);
        Assert.Empty(_engine.Call("login_pending")["pending"]!.AsArray());
        Assert.Contains("original", File.ReadAllText(Path.Combine(_root, ".claude", ".credentials.json")));
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("en")]
    public void Dialog_actions_fit_and_default_to_switching(string language)
    {
        using var ui = new UiScope();
        using var loc = Loc.Scoped(language);
        using var dialog = new LoginDialog(_engine, new FakeProcess());
        Show(dialog);
        Assert.True(((ThemedCheckBox)dialog.Controls["loginActivate"]!).Checked);
        var buttons = dialog.Controls.OfType<Button>().Where(b => b.Visible).OrderBy(b => b.Left).ToArray();
        foreach (var button in buttons) Assert.True(dialog.ClientRectangle.Contains(button.Bounds));
        for (int i = 1; i < buttons.Length; i++) Assert.True(buttons[i - 1].Right < buttons[i].Left);
        foreach (var label in dialog.Controls.OfType<Label>()) Assert.True(dialog.ClientRectangle.Contains(label.Bounds));
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_LOGIN_SHOTS") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(Path.Combine(shots, $"login-{language}.png"));
        }
    }

    [Fact]
    public async Task Cmd_launch_handles_spaces_ampersands_percent_and_scrubs_only_child_environment()
    {
        string directory = Path.Combine(_root, "space & 中文 %PATH%");
        Directory.CreateDirectory(directory);
        string exe = Path.Combine(directory, "claude.cmd");
        await File.WriteAllTextAsync(exe, "@echo off\r\nchcp 65001 >nul\r\nif defined ANTHROPIC_API_KEY exit /b 9\r\necho \"%CLAUDE_CONFIG_DIR%\"\r\nexit /b 0\r\n");
        var launch = new LoginLaunch("fixture", directory, ["ANTHROPIC_API_KEY"], new Dictionary<string, string>());
        var info = LoginProcess.BuildStartInfo(exe, launch, "auth", "status");
        // Inject then remove on a separate start-info object: no process-wide env mutation.
        Assert.False(info.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        var result = await LoginProcess.CaptureAsync(info, CancellationToken.None);
        Assert.Equal(0, result.Code);
        Assert.Equal(directory, result.Output.Trim().Trim('"'));
    }

    [Fact]
    public async Task Real_owned_process_is_stopped_on_cancel_and_its_profile_can_be_removed()
    {
        string exe = Path.Combine(_root, "fake.cmd");
        await File.WriteAllTextAsync(exe, "@echo off\r\nif \"%~3\"==\"--help\" (echo --claudeai & exit /b 0)\r\npowershell.exe -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"\r\n");
        var process = new LoginProcess(exe, showTerminal: false);
        using var cancel = new CancellationTokenSource();
        var run = new LoginWorkflow(_engine, process).RunAsync(true, null, step =>
        {
            if (step == "login.waiting") cancel.CancelAfter(1000);
        }, cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(_engine.Call("login_pending")["pending"]!.AsArray());
        // A new flow can acquire the lease immediately.
        var next = _engine.Call("login_begin");
        _engine.Call("login_cancel", new { id = next["id"]!.GetValue<string>(), discard = true });
    }

    [Fact]
    public void Recovery_refuses_a_process_with_matching_pid_and_start_time()
    {
        using var current = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(_root, ".login-process.json"), new JsonObject {
            ["Pid"] = current.Id, ["Started"] = current.StartTime.ToUniversalTime().Ticks }.ToJsonString());
        Assert.Throws<IOException>(() => LoginProcess.EnsureStopped(_root));
    }

    private static void Show(Form dialog)
    {
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(-32000, -32000);
        dialog.Show();
        Application.DoEvents();
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "UI did not finish");
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    private sealed class UiScope : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _previous = Current;
        private readonly Control _dispatcher = new();
        private readonly bool _checkThreads = Control.CheckForIllegalCrossThreadCalls;
        public UiScope()
        {
            _ = _dispatcher.Handle;
            SetSynchronizationContext(this);
            Control.CheckForIllegalCrossThreadCalls = true;
        }
        public override void Post(SendOrPostCallback callback, object? state) => _dispatcher.BeginInvoke(() => callback(state));
        public void Dispose()
        {
            Application.DoEvents();
            SetSynchronizationContext(_previous);
            Control.CheckForIllegalCrossThreadCalls = _checkThreads;
            _dispatcher.Dispose();
        }
    }
}
