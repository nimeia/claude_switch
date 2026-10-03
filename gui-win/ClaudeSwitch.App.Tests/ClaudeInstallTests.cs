using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

public sealed class ClaudeInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cswitch-install-test-" + Guid.NewGuid().ToString("N"));
    public ClaudeInstallTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        ClaudeCli.InvalidateVersionCache();
        // This directory is generated here and contains only test fixtures.
        Directory.Delete(_root, true);
    }
    private string FileAt(string relative, string content = "fixture")
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
    private ClaudeInstallPaths Paths(string search = "") => new(Path.Combine(_root, "home"), Path.Combine(_root, "local"),
        Path.Combine(_root, "roaming"), Path.Combine(_root, "programs"), Path.Combine(_root, "local", "Volta"), search);
    private sealed class Commands : IClaudeCommandRunner
    {
        internal List<ClaudeCommand> Calls { get; } = [];
        internal Func<ClaudeCommand, CancellationToken, Task<ClaudeCommandResult>> Handler { get; set; }
            = (_, _) => Task.FromResult(new ClaudeCommandResult(0, "2.1.300 (Claude Code)", ""));
        public Task<ClaudeCommandResult> RunAsync(ClaudeCommand command, CancellationToken token,
            Action<string>? log = null, TimeSpan? timeout = null)
        { Calls.Add(command); return Handler(command, token); }
    }
    private static Task<ClaudeCommandResult> Output(string value, int code = 0) => Task.FromResult(new ClaudeCommandResult(code, value, ""));

    [Fact]
    public async Task Empty_installation_offers_native_install()
    {
        var found = await new ClaudeInstallationDetector(new Commands(), Paths()).DetectAsync(default);
        Assert.Equal(ClaudeInstallSource.None, found.Source);
        Assert.True(found.CanChange);
        Assert.Null(ClaudeInstallRunner.BuildCommand(found));
    }

    [Fact]
    public async Task Native_installation_works_without_path_entry()
    {
        string exe = FileAt(@"home\.local\bin\claude.exe");
        var found = await new ClaudeInstallationDetector(new Commands(), Paths()).DetectAsync(default);
        Assert.Equal(ClaudeInstallSource.Native, found.Source);
        Assert.Equal(exe, found.Executable);
        Assert.Equal(new Version(2, 1, 300), found.Version);
    }

    [Fact]
    public async Task Volta_shim_is_verified_and_wins_over_other_copies()
    {
        string shim = FileAt(@"local\Volta\bin\claude.cmd");
        string volta = FileAt(@"tools\volta.exe");
        string real = FileAt(@"local\Volta\tools\image\packages\@anthropic-ai\claude-code\claude");
        FileAt(@"home\.local\bin\claude.exe");
        var commands = new Commands { Handler = (c, _) => Output(c.Arguments[0] == "which" ? real : "2.1.287 (Claude Code)") };
        var p = Paths(Path.GetDirectoryName(shim) + ";" + Path.GetDirectoryName(volta));
        var found = await new ClaudeInstallationDetector(commands, p).DetectAsync(default);
        Assert.Equal(ClaudeInstallSource.Volta, found.Source);
        Assert.Equal(shim, found.Executable);
        Assert.Equal(volta, found.Manager);
        Assert.Equal(2, found.Copies.Count);
        Assert.Equal("install", ClaudeInstallRunner.BuildCommand(found)!.Arguments[0]);
    }

    [Fact]
    public async Task Volta_metadata_pointing_outside_owned_package_is_not_updated()
    {
        string shim = FileAt(@"local\Volta\bin\claude.cmd");
        string volta = FileAt(@"tools\volta.exe");
        var commands = new Commands { Handler = (c, _) => Output(c.Arguments[0] == "which" ? FileAt("unrelated.exe") : "2.1.287") };
        var found = await new ClaudeInstallationDetector(commands,
            Paths(Path.GetDirectoryName(shim) + ";" + Path.GetDirectoryName(volta))).DetectAsync(default);
        Assert.Equal(ClaudeInstallSource.Unknown, found.Source);
        Assert.False(found.CanChange);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Npm_update_requires_matching_prefix_and_package_metadata(bool matching)
    {
        string shim = FileAt(@"npm-custom\claude.cmd");
        string npm = FileAt(@"tools\npm.cmd");
        FileAt(@"npm-custom\node_modules\@anthropic-ai\claude-code\package.json", "{\"name\":\"@anthropic-ai/claude-code\"}");
        var commands = new Commands { Handler = (c, _) => Output(c.Arguments[0] == "prefix"
            ? Path.Combine(_root, matching ? "npm-custom" : "other-prefix") : "2.1.287") };
        var found = await new ClaudeInstallationDetector(commands,
            Paths(Path.GetDirectoryName(shim) + ";" + Path.GetDirectoryName(npm))).DetectAsync(default);
        Assert.Equal(matching ? ClaudeInstallSource.Npm : ClaudeInstallSource.Unknown, found.Source);
        Assert.Equal(matching, found.CanChange);
    }

    [Fact]
    public async Task Npm_copy_outside_launch_paths_is_reported_without_claiming_it_is_usable()
    {
        string npm = FileAt(@"tools\npm.cmd");
        FileAt(@"npm-custom\claude.cmd");
        FileAt(@"npm-custom\node_modules\@anthropic-ai\claude-code\package.json", "{\"name\":\"@anthropic-ai/claude-code\"}");
        var commands = new Commands { Handler = (c, _) => Output(c.Arguments[0] == "prefix" ? Path.Combine(_root, "npm-custom") : "2.1.287") };
        var found = await new ClaudeInstallationDetector(commands, Paths(Path.GetDirectoryName(npm)!)).DetectAsync(default);
        Assert.Equal(ClaudeInstallSource.Npm, found.Source);
        Assert.False(found.CanChange);
    }

    [Fact]
    public async Task Winget_package_directory_selects_winget_without_mutating_sources()
    {
        string exe = FileAt(@"local\Microsoft\WinGet\Packages\Anthropic.ClaudeCode_source\claude.exe");
        string manager = FileAt(@"tools\winget.exe");
        var commands = new Commands();
        var found = await new ClaudeInstallationDetector(commands,
            Paths(Path.GetDirectoryName(exe) + ";" + Path.GetDirectoryName(manager))).DetectAsync(default);
        Assert.Equal(ClaudeInstallSource.WinGet, found.Source);
        Assert.Equal(manager, found.Manager);
        Assert.DoesNotContain(commands.Calls, c => c.Executable == manager);
    }

    [Fact]
    public async Task Failed_version_probe_preserves_source_and_allows_repair_by_that_manager()
    {
        FileAt(@"home\.local\bin\claude.exe");
        var found = await new ClaudeInstallationDetector(new Commands { Handler = (_, _) => Output("", 1) }, Paths()).DetectAsync(default);
        Assert.Equal(ClaudeInstallSource.Native, found.Source);
        Assert.Null(found.Version);
    }

    private ClaudeInstallation Installation(ClaudeInstallSource source = ClaudeInstallSource.Native, int build = 287) =>
        new(source, Path.Combine(_root, "claude.exe"), source == ClaudeInstallSource.Native ? null : Path.Combine(_root, "manager.exe"),
            new Version(2, 1, build), [Path.Combine(_root, "claude.exe")]);

    private ClaudeInstallRunner Service(Commands commands, Func<ClaudeInstallation> detect,
        bool sessions = false, string? policy = null) => new(commands, _ => Task.FromResult(detect()),
            _ => Task.FromResult(sessions), _ => policy, _ => throw new Exception("Unexpected download"));

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "policy")]
    public async Task Running_sessions_and_policy_block_before_any_write(bool sessions, string? policy)
    {
        var before = Installation();
        var commands = new Commands();
        await Assert.ThrowsAsync<IOException>(() => Service(commands, () => before, sessions, policy)
            .InstallAsync(before, _ => { }, _ => { }, default));
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task Changed_source_is_not_overwritten()
    {
        var before = Installation();
        var commands = new Commands();
        await Assert.ThrowsAsync<IOException>(() => Service(commands, () => before with { Source = ClaudeInstallSource.Unknown })
            .InstallAsync(before, _ => { }, _ => { }, default));
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task Successful_update_refreshes_cached_version_and_keeps_account_files()
    {
        string credentials = FileAt(@"home\.claude\.credentials.json", "original-credentials");
        string backup = FileAt(@"home\.claude-swap-backup\account.enc", "original-backup");
        var before = Installation();
        var after = Installation(build: 300);
        bool wrote = false;
        var commands = new Commands { Handler = (_, token) => { Assert.False(token.CanBeCanceled); wrote = true; return Output(""); } };
        ClaudeCli.SetInstalledVersion(before.Version);
        var result = await Service(commands, () => wrote ? after : before).InstallAsync(before, _ => { }, _ => { }, default);
        Assert.Equal(after.Version, result.Version);
        Assert.Equal(after.Version, ClaudeCli.InstalledVersion());
        Assert.Equal("original-credentials", File.ReadAllText(credentials));
        Assert.Equal("original-backup", File.ReadAllText(backup));
    }

    [Theory]
    [InlineData((int)ClaudeInstallSource.Npm)]
    [InlineData((int)ClaudeInstallSource.Volta)]
    public async Task Package_update_pins_checked_version_and_rejects_a_downgrade(int sourceValue)
    {
        var source = (ClaudeInstallSource)sourceValue;
        var before = Installation(source);
        var commands = new Commands { Handler = (_, _) => Output("\"2.1.200\"") };
        await Assert.ThrowsAsync<IOException>(() => Service(commands, () => before)
            .InstallAsync(before, _ => { }, _ => { }, default));
        Assert.Single(commands.Calls);
        Assert.Contains("view", commands.Calls[0].Arguments);

        bool wrote = false;
        commands.Calls.Clear();
        commands.Handler = (c, _) =>
        {
            if (c.Arguments.Contains("view")) return Output("\"2.1.300\"");
            Assert.Contains("@anthropic-ai/claude-code@2.1.300", c.Arguments);
            wrote = true;
            return Output("");
        };
        await Service(commands, () => wrote ? Installation(source, 300) : before)
            .InstallAsync(before, _ => { }, _ => { }, default);
        Assert.Equal(2, commands.Calls.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Exit_code_alone_never_claims_success(int exitCode)
    {
        var before = Installation();
        bool wrote = false;
        var commands = new Commands { Handler = (_, _) => { wrote = true; return Output("", exitCode); } };
        await Assert.ThrowsAsync<IOException>(() => Service(commands, () => wrote ? before with { Version = null } : before)
            .InstallAsync(before, _ => { }, _ => { }, default));
    }

    [Fact]
    public async Task Winget_no_update_code_still_verifies_current_installation()
    {
        var before = Installation(ClaudeInstallSource.WinGet);
        var commands = new Commands { Handler = (_, _) => Output("", unchecked((int)0x8A15002B)) };
        var result = await Service(commands, () => before).InstallAsync(before, _ => { }, _ => { }, default);
        Assert.Equal(before.Version, result.Version);
    }

    [Fact]
    public async Task Successful_package_exit_with_old_binary_does_not_claim_update_success()
    {
        var before = Installation(ClaudeInstallSource.Volta);
        var commands = new Commands { Handler = (c, _) => Output(c.Arguments.Contains("view") ? "\"2.1.300\"" : "") };
        await Assert.ThrowsAsync<IOException>(() => Service(commands, () => before)
            .InstallAsync(before, _ => { }, _ => { }, default));
        Assert.Equal(2, commands.Calls.Count);
    }

    [Fact]
    public async Task A_background_update_during_version_query_prevents_downgrade()
    {
        var before = Installation(ClaudeInstallSource.Volta);
        bool queried = false;
        var commands = new Commands { Handler = (_, _) => { queried = true; return Output("\"2.1.300\""); } };
        await Assert.ThrowsAsync<IOException>(() => Service(commands, () => queried ? Installation(ClaudeInstallSource.Volta, 301) : before)
            .InstallAsync(before, _ => { }, _ => { }, default));
        Assert.Single(commands.Calls);
    }

    [Fact]
    public async Task Cancellation_before_mutation_never_starts_installer()
    {
        var before = Installation();
        var commands = new Commands();
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(commands, () => before)
            .InstallAsync(before, _ => cancel.Cancel(), _ => { }, cancel.Token));
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task Real_batch_handles_special_characters_in_executable_path_and_drains_both_pipes()
    {
        string script = FileAt(@"space & percent% !\probe.cmd", "@echo off\r\necho 2.1.300\r\necho diagnostic 1>&2\r\nexit /b 0\r\n");
        var result = await new ClaudeCommandRunner().RunAsync(new(script, "--version"), default, timeout: TimeSpan.FromSeconds(5));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("2.1.300", result.Output);
        Assert.Contains("diagnostic", result.Error);
        Assert.Throws<ArgumentException>(() => ClaudeCommandRunner.StartInfo(new(script, "x&echo-bad")));
    }

    [Fact]
    public async Task Real_probe_timeout_covers_a_stalled_stdout_read()
    {
        string script = FileAt("slow.cmd", "@echo off\r\nping -n 30 127.0.0.1 >nul\r\n");
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => new ClaudeCommandRunner().RunAsync(new(script), default,
            timeout: TimeSpan.FromMilliseconds(250)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task Large_stdout_and_stderr_are_drained_without_unbounded_retention()
    {
        string script = FileAt("noisy.cmd", "@echo off\r\nfor /L %%i in (1,1,7000) do (\r\necho abcdefghijklmnopqrstuvwxyz\r\necho abcdefghijklmnopqrstuvwxyz 1>&2\r\n)\r\n");
        var result = await new ClaudeCommandRunner().RunAsync(new(script), default, timeout: TimeSpan.FromSeconds(15));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(65536, result.Output.Length);
        Assert.Equal(65536, result.Error.Length);
    }

    [Fact]
    public void Log_is_bounded_and_redacts_proxy_and_token_credentials()
    {
        var log = new ClaudeInstallLog();
        log.Append(new string('x', 100000));
        log.Append(" https://user:password@");
        log.Append("localhost:8080 Bearer super-secret\n");
        string result = log.Take()!;
        Assert.True(result.Length <= 32768);
        Assert.DoesNotContain("password", result);
        Assert.DoesNotContain("super-secret", result);
        Assert.Null(log.Take());
    }

    [Fact]
    public void Proxy_settings_apply_to_children_without_changing_parent_environment()
    {
        string? previous = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        var proxy = new SessionMode.ResolvedProxy("http://localhost:7890", false,
            new() { ["HTTPS_PROXY"] = "http://localhost:7890" }, []);
        var info = ClaudeCommandRunner.StartInfo(new("volta.exe", "install", "@anthropic-ai/claude-code@latest"), proxy);
        Assert.Equal(proxy.Url, info.Environment["HTTPS_PROXY"]);
        Assert.Equal(proxy.Url, info.Environment["npm_config_https_proxy"]);
        Assert.Equal(previous, Environment.GetEnvironmentVariable("HTTPS_PROXY"));
        var winget = ClaudeInstallRunner.WithWinGetProxy(new("winget.exe", "upgrade"), proxy);
        Assert.Equal(new[] { "upgrade", "--proxy", proxy.Url }, winget.Arguments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task Native_launcher_runs_only_the_downloaded_script_and_preserves_exit_code(int exit)
    {
        string installer = FileAt(@"安装 & space%\setup.ps1", "Write-Output 'fixture-native-installer'\nexit " + exit);
        var command = ClaudeInstallRunner.NativeInstallCommand(installer, new(null, true, [], []));
        var result = await new ClaudeCommandRunner().RunAsync(command, default, timeout: TimeSpan.FromSeconds(10));
        Assert.Equal(exit, result.ExitCode);
        Assert.Contains("fixture-native-installer", result.Output);
    }

    [Fact]
    public async Task New_native_install_verifies_and_removes_only_its_temporary_script()
    {
        var before = new ClaudeInstallation(ClaudeInstallSource.None, null, null, null, []);
        var after = Installation();
        string installer = FileAt("download.ps1");
        string other = FileAt("unrelated.txt");
        bool wrote = false;
        var commands = new Commands { Handler = (_, _) => { wrote = true; return Output(""); } };
        var service = new ClaudeInstallRunner(commands, _ => Task.FromResult(wrote ? after : before),
            _ => Task.FromResult(false), _ => null, _ => Task.FromResult(installer));
        var result = await service.InstallAsync(before, _ => { }, _ => { }, default);
        Assert.Equal(after, result);
        Assert.False(File.Exists(installer));
        Assert.True(File.Exists(other));
    }

    private sealed class FakeService(ClaudeInstallation installation) : IClaudeInstallService
    {
        internal TaskCompletionSource<ClaudeInstallation> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Started;
        internal bool Fail;
        public Task<ClaudeInstallation> DetectAsync(CancellationToken token) => Task.FromResult(installation);
        public async Task<ClaudeInstallation> InstallAsync(ClaudeInstallation expected, Action<ClaudeInstallProgress> progress,
            Action<string> log, CancellationToken token)
        {
            progress(new("install.applying", false));
            for (int i = 0; i < 10000; i++) log("installer output\n");
            Started = true;
            var result = await Finish.Task;
            if (Fail) throw new IOException("simulated-network-error");
            return result;
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    public void Dialog_keeps_pumping_and_defers_close_until_installer_finishes(string language)
    {
        using var ui = new UiScope();
        using var loc = Loc.Scoped(language);
        var service = new FakeService(Installation());
        using var dialog = new ClaudeInstallDialog(service);
        Show(dialog);
        PumpUntil(() => dialog.Completion.IsCompleted);
        dialog.ToggleDetails();
        foreach (var button in dialog.Controls.OfType<Button>()) Assert.True(dialog.ClientRectangle.Contains(button.Bounds));
        var buttons = dialog.Controls.OfType<Button>().OrderBy(b => b.Left).ToArray();
        for (int i = 1; i < buttons.Length; i++) Assert.True(buttons[i - 1].Right < buttons[i].Left);
        Assert.True(dialog.ClientRectangle.Contains(dialog.Controls["installDetails"]!.Bounds));
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_INSTALL_SHOTS") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(Path.Combine(shots, "install-" + language + ".png"));
        }
        var completion = dialog.RunAsync();
        PumpUntil(() => service.Started);
        int ticks = 0;
        using var timer = new System.Windows.Forms.Timer { Interval = 10 };
        timer.Tick += (_, _) => ticks++;
        timer.Start();
        PumpUntil(() => ticks >= 3);
        Assert.False(completion.IsCompleted);
        Assert.False(dialog.Controls["installStart"]!.Enabled);
        dialog.Close();
        Assert.False(dialog.IsDisposed);
        service.Finish.SetResult(Installation(build: 300));
        PumpUntil(() => completion.IsCompleted);
        Assert.True(completion.IsCompletedSuccessfully, completion.Exception?.ToString());
        Assert.True(dialog.IsDisposed);
    }

    [Fact]
    public void Failed_installation_returns_to_retryable_state()
    {
        using var ui = new UiScope();
        var service = new FakeService(Installation()) { Fail = true };
        using var dialog = new ClaudeInstallDialog(service);
        Show(dialog);
        PumpUntil(() => dialog.Completion.IsCompleted);
        var completion = dialog.RunAsync();
        PumpUntil(() => service.Started);
        service.Finish.SetResult(Installation());
        PumpUntil(() => completion.IsCompleted);
        Assert.True(dialog.Controls["installStart"]!.Enabled);
        Assert.Contains("simulated-network-error", dialog.Controls["installStatus"]!.Text);
        dialog.Close();
    }

    [SkippableFact]
    public async Task Optional_read_only_probe_of_this_machine()
    {
        Skip.If(Environment.GetEnvironmentVariable("CLAUDE_SWITCH_INSTALL_PROBE") != "1", "Opt-in local installation probe");
        var result = await new ClaudeInstallRunner().DetectAsync(default);
        Assert.NotNull(result.Version);
        Assert.NotEqual(ClaudeInstallSource.Unknown, result.Source);
        Assert.Equal(ClaudeCli.FindExecutable(), result.Executable, ignoreCase: true);
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_INSTALL_REPORT") is { } report)
            File.WriteAllText(report, $"{result.Source}\n{result.Version}\n{result.Executable}\n{result.Manager}\n{result.Problem}");
    }

    private static void Show(Form dialog)
    {
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(-32000, -32000);
        dialog.Show();
        Application.DoEvents();
    }
    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(12), "UI did not finish");
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }
    private sealed class UiScope : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _previous = Current;
        private readonly Control _dispatcher = new();
        private readonly bool _check = Control.CheckForIllegalCrossThreadCalls;
        public UiScope() { _ = _dispatcher.Handle; SetSynchronizationContext(this); Control.CheckForIllegalCrossThreadCalls = true; }
        public override void Post(SendOrPostCallback d, object? state) => _dispatcher.BeginInvoke(() => d(state));
        public void Dispose()
        {
            Application.DoEvents(); SetSynchronizationContext(_previous);
            Control.CheckForIllegalCrossThreadCalls = _check; _dispatcher.Dispose();
        }
    }
}
