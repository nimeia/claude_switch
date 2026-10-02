using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;
using ClaudeSwitch.App;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>Uninstall Claude Code: JSON the dialog reads, and layout.</summary>
public class PurgeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cswitch-purge-" + Guid.NewGuid().ToString("N"));
    private readonly Engine _engine;

    public PurgeTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "settings.json"), "{\"a\":1}");
        File.WriteAllText(Path.Combine(_root, ".claude.json"), "{\"userID\":\"u\"}");
        Directory.CreateDirectory(Path.Combine(_root, ".local", "bin"));
        File.WriteAllText(Path.Combine(_root, ".local", "bin", "claude.exe"), "cli");
        File.WriteAllText(Path.Combine(_root, ".local", "bin", "uv.exe"), "uv");
        _engine = new Engine(_root);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_scan_lists_the_native_program_and_runtime()
    {
        var scan = PurgeData.Scan(_engine);
        Assert.Equal("native", scan.InstallKind);
        Assert.Contains(scan.Groups, g => g.Id == "program" && g.Required);
        Assert.Contains(scan.Groups, g => g.Id == "runtime" && g.Required);
        Assert.Contains(scan.Groups.SelectMany(g => g.Items), i => i.Id == "native-bin");
        Assert.False(scan.ClaudeRunning);
    }

    [Fact]
    public void Apply_keeps_uv_and_the_account_backup_by_default()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude-swap-backup", "credentials"));
        File.WriteAllText(
            Path.Combine(_root, ".claude-swap-backup", "sequence.json"),
            "{\"accounts\":{\"1\":{\"email\":\"a@x.com\"}},\"sequence\":[1]}");
        File.WriteAllText(
            Path.Combine(_root, ".claude-swap-backup", "credentials", ".creds-a.enc"),
            "tok");

        var opts = new PurgeOptionsView(false, false, false, false, false, false);
        var plan = PurgeData.Plan(_engine, opts);
        Assert.True(plan.CanApply, string.Join(",", plan.Problems));
        Assert.Contains("accounts-kept-identity-remains", plan.Warnings);

        var outcome = PurgeData.Apply(_engine, opts);
        Assert.Empty(outcome.Failed);
        Assert.False(File.Exists(Path.Combine(_root, ".local", "bin", "claude.exe")));
        Assert.True(File.Exists(Path.Combine(_root, ".local", "bin", "uv.exe")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".claude")));
        Assert.True(File.Exists(Path.Combine(_root, ".claude-swap-backup", "credentials", ".creds-a.enc")));
    }

    [Fact]
    public void Permission_denied_is_reported_without_rolling_back_other_deletions()
    {
        var log = Seed("ProgramData/Claude/Logs/service.log", "fixture");
        var managed = Seed("ProgramData/ClaudeCode/managed.json", "{}");
        var directory = new DirectoryInfo(Path.Combine(_root, "ProgramData", "Claude"));
        var original = directory.GetAccessControl(AccessControlSections.Access);
        var denied = directory.GetAccessControl(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!,
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Deny));
        directory.SetAccessControl(denied);
        try
        {
            var outcome = PurgeData.Apply(_engine,
                new(false, false, false, false, true, false, RemoveDesktopData: true));
            Assert.Single(outcome.Failed);
            Assert.Equal("permission-denied", outcome.Failed[0].Reason);
            Assert.Equal(directory.FullName, outcome.Failed[0].Path);
            Assert.True(File.Exists(log));
            Assert.False(File.Exists(managed));
            Assert.False(Directory.Exists(Path.Combine(_root, ".claude")));
        }
        finally
        {
            var restore = new DirectorySecurity();
            restore.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            directory.SetAccessControl(restore);
        }
    }

    [Fact]
    public void ParseScan_reads_the_totals()
    {
        var scan = PurgeData.ParseScan(JsonNode.Parse("""
            {
              "installKind": "native",
              "claudeRunning": false,
              "liveSessions": 0,
              "importedAccounts": 2,
              "appProblems": ["browser-running"],
              "groups": [
                {"id":"program","required":true,"bytes":10,"items":[
                  {"id":"native-bin","group":"program","path":"C:\\a\\claude.exe",
                   "bytes":10,"exists":true,"kind":"file"}
                ]}
              ]
            }
            """));
        Assert.Equal("native", scan.InstallKind);
        Assert.Equal(2, scan.ImportedAccounts);
        Assert.Equal("program", scan.Groups[0].Id);
        Assert.Equal("native-bin", scan.Groups[0].Items[0].Id);
        Assert.Equal(new[] { "browser-running" }, scan.AppProblems);
    }

    [Fact]
    public void The_dialog_uses_the_danger_button_and_keep_accounts_by_default()
    {
        using var ui = new UiScope();
        using var lang = Loc.Scoped("zh-Hans");
        using var dlg = new PurgeDialog(_engine);
        Assert.Equal(Loc.T("purge.title"), dlg.Text);
        var apply = dlg.Controls.OfType<Button>().Single(b => b.Name == "purgeApply");
        Assert.Equal(Loc.T("purge.apply"), apply.Text);
        var ide = dlg.Controls.OfType<CheckBox>().Single(c => c.Name == "purgeIde");
        Assert.True(ide.Checked);
        var keep = dlg.Controls.OfType<RadioButton>().FirstOrDefault(r => r.Name == "purgeKeepAccounts");
        if (keep is not null)
            Assert.True(keep.Checked);
    }

    [Fact]
    public void Optional_residuals_are_previewed_and_removed_through_the_engine()
    {
        const string extensionId = "fcoeoabgfenejglbffodgkkbkcdhcgfn";
        var desktop = Seed("AppData/Roaming/Claude/config.json", "{\"setting\":true}");
        var browser = Seed($"AppData/Local/Google/Chrome/User Data/Default/Extensions/{extensionId}/1/manifest.json", "{}");
        var cookies = Seed("AppData/Local/Google/Chrome/User Data/Default/Network/Cookies", "keep");
        var sdk = Seed("AppData/Roaming/Code/agent-host/sdk-cache/claude/0.3.220/claude.exe", "sdk");
        var index = Seed(".vscode/extensions/extensions.json", """
            [{"identifier":{"id":"anthropic.claude-code"}}, {"identifier":{"id":"other.extension"}}]
            """);
        var backup = Seed(".yunyi-cli/backups/claude/settings.json", "{}");
        var opts = new PurgeOptionsView(true, false, false, true, false, false,
            RemoveDesktopData: true, RemoveBrowserExtension: true);
        var plan = PurgeData.Plan(_engine, opts);
        Assert.True(plan.CanApply);
        Assert.Contains(plan.Items, i => Path.GetFullPath(i.Path) == Path.GetDirectoryName(desktop) && i.Group == "desktop-data");
        Assert.Contains(plan.Items, i => i.Group == "browser");
        Assert.Contains(plan.Items, i => i.Kind == "json-entries");
        var outcome = PurgeData.Apply(_engine, opts);
        Assert.Empty(outcome.Failed);
        Assert.False(File.Exists(desktop));
        Assert.False(File.Exists(browser));
        Assert.False(File.Exists(sdk));
        Assert.False(File.Exists(backup));
        Assert.True(File.Exists(cookies));
        var remaining = JsonNode.Parse(File.ReadAllText(index))!.AsArray();
        Assert.Single(remaining);
        Assert.Equal("other.extension", remaining[0]!["identifier"]!["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("en")]
    public void Residual_options_default_off_and_update_the_visible_plan(string language)
    {
        Seed("AppData/Roaming/Claude/config.json", "{}");
        Seed("AppData/Local/Google/Chrome/User Data/Default/Extensions/fcoeoabgfenejglbffodgkkbkcdhcgfn/1/manifest.json", "{}");
        Seed(".yunyi-cli/backups/claude/settings.json", "{}");
        using var lang = Loc.Scoped(language);
        using var ui = new UiScope();
        using var dlg = new PurgeDialog(_engine);
        Show(dlg);
        PumpUntil(() => dlg.ScanCompletion.IsCompleted);
        var desktop = dlg.Controls.OfType<CheckBox>().Single(c => c.Name == "purgeDesktopData");
        var browser = dlg.Controls.OfType<CheckBox>().Single(c => c.Name == "purgeBrowser");
        var third = dlg.Controls.OfType<CheckBox>().Single(c => c.Name == "purgeThird");
        Assert.False(desktop.Checked);
        Assert.False(browser.Checked);
        Assert.False(third.Checked);
        Assert.True(desktop.Visible && browser.Visible && third.Visible);
        var preview = dlg.Controls.OfType<TextBox>().Single(c => c.Name == "purgePreview");
        Assert.DoesNotContain("fcoeoabgfenejglbffodgkkbkcdhcgfn", preview.Text);
        var area = Screen.FromControl(dlg).WorkingArea;
        dlg.Location = new System.Drawing.Point(area.Left, area.Bottom - dlg.Height);
        desktop.Checked = browser.Checked = third.Checked = true;
        Assert.True(dlg.Bottom <= area.Bottom, "Growing warnings must keep the action row on screen");
        dlg.Location = new System.Drawing.Point(-32000, -32000);
        Assert.Contains("fcoeoabgfenejglbffodgkkbkcdhcgfn", preview.Text);
        Assert.Contains(".yunyi-cli", preview.Text);
        Assert.Contains("Claude", preview.Text);
        Assert.True(dlg.AutoScroll);
        var apply = dlg.Controls.OfType<Button>().Single(b => b.Name == "purgeApply");
        Assert.True(apply.Enabled);
        Assert.True(apply.Top > preview.Bottom);
        var notes = dlg.Controls.OfType<Label>().Single(l => l.Text.Contains(Loc.T("purge.warn.desktop-data-login-removed")));
        Assert.True(apply.Top > notes.Bottom, "Warnings must not overlap the action row");
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PURGE_SHOTS") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new System.Drawing.Point(-32000, -32000);
            dlg.PerformLayout();
            Application.DoEvents();
            using var bitmap = new System.Drawing.Bitmap(dlg.Width, dlg.Height);
            dlg.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, dlg.Width, dlg.Height));
            bitmap.Save(Path.Combine(shots, $"purge-{language}.png"));
            dlg.Hide();
        }
    }

    [Fact]
    public void Slow_scan_keeps_message_loop_responsive_and_can_be_closed()
    {
        using var ui = new UiScope();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var dlg = new PurgeDialog(() =>
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return FixtureScan();
        }, _ => throw new InvalidOperationException("Preview must not call the engine"),
           _ => throw new InvalidOperationException("Unexpected deletion"));
        Assert.False(started.IsSet); // Constructor must not do I/O.
        try
        {
            Show(dlg);
            PumpUntil(() => started.IsSet);
            int ticks = 0;
            using var timer = new System.Windows.Forms.Timer { Interval = 10 };
            timer.Tick += (_, _) => ticks++;
            timer.Start();
            PumpUntil(() => ticks >= 3);
            Assert.False(dlg.ScanCompletion.IsCompleted);
            Assert.False(Button(dlg, "purgeApply").Enabled);
            dlg.Close();
            Assert.True(dlg.IsDisposed);
        }
        finally
        {
            release.Set();
            PumpUntil(() => dlg.ScanCompletion.IsCompleted);
        }
        Assert.True(dlg.ScanCompletion.IsCompletedSuccessfully, dlg.ScanCompletion.Exception?.ToString());
    }

    [Fact]
    public void Repeated_option_changes_reuse_scan_and_refresh_is_explicit()
    {
        using var ui = new UiScope();
        int scans = 0;
        using var dlg = new PurgeDialog(() => { Interlocked.Increment(ref scans); return FixtureScan(); },
            _ => throw new InvalidOperationException("Preview must not call the engine"),
            _ => throw new InvalidOperationException("Unexpected deletion"));
        Show(dlg);
        PumpUntil(() => dlg.ScanCompletion.IsCompleted);
        var browser = dlg.Controls.OfType<CheckBox>().Single(c => c.Name == "purgeBrowser");
        for (int i = 0; i < 100; i++) browser.Checked = !browser.Checked;
        Assert.Equal(1, scans);
        Button(dlg, "purgeRefresh").PerformClick();
        PumpUntil(() => dlg.ScanCompletion.IsCompleted);
        Assert.Equal(2, scans);
    }

    [Fact]
    public void Failed_scan_can_be_retried_without_enabling_delete()
    {
        using var ui = new UiScope();
        int scans = 0;
        using var dlg = new PurgeDialog(() => ++scans == 1 ? throw new IOException("fixture scan failed") : FixtureScan(),
            _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
        Show(dlg);
        PumpUntil(() => dlg.ScanCompletion.IsCompleted);
        Assert.False(Button(dlg, "purgeApply").Enabled);
        Assert.Contains(dlg.Controls.OfType<Label>(), l => l.Text.Contains("fixture scan failed"));
        Button(dlg, "purgeRefresh").PerformClick();
        PumpUntil(() => dlg.ScanCompletion.IsCompleted);
        Assert.True(Button(dlg, "purgeApply").Enabled);
    }

    [Fact]
    public void Apply_rechecks_in_background_and_blocks_newly_running_apps()
    {
        using var ui = new UiScope();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var dlg = new PurgeDialog(FixtureScan, _ =>
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return PurgePlanView.Invalid("claude-running");
        }, _ => throw new InvalidOperationException("Must not delete after failed preflight"));
        Show(dlg);
        PumpUntil(() => dlg.ScanCompletion.IsCompleted);
        try
        {
            Button(dlg, "purgeApply").PerformClick();
            PumpUntil(() => started.IsSet);
            int ticks = 0;
            using var timer = new System.Windows.Forms.Timer { Interval = 10 };
            timer.Tick += (_, _) => ticks++;
            timer.Start();
            PumpUntil(() => ticks >= 3);
            dlg.Close();
            Assert.False(dlg.IsDisposed);
        }
        finally
        {
            release.Set();
            PumpUntil(() => dlg.ApplyCompletion.IsCompleted);
        }
        Assert.True(dlg.ApplyCompletion.IsCompletedSuccessfully, dlg.ApplyCompletion.Exception?.ToString());
        Assert.False(Button(dlg, "purgeApply").Enabled);
        Assert.Contains(dlg.Controls.OfType<Label>(), l => l.Text.Contains(Loc.T("purge.warn.claude-running")));
    }

    [Fact]
    public void Cached_preview_matches_native_plan_for_every_option_combination()
    {
        Seed("AppData/Roaming/Claude/config.json", "{}");
        Seed("AppData/Roaming/Claude/claude-code/data.json", "nested");
        Seed("AppData/Local/Google/Chrome/User Data/Default/Extensions/fcoeoabgfenejglbffodgkkbkcdhcgfn/1/manifest.json", "{}");
        Seed(".vscode/extensions/extensions.json", """[{"identifier":{"id":"anthropic.claude-code"}}]""");
        Seed(".yunyi-cli/backups/claude/settings.json", "{}");
        var scan = PurgeData.Scan(_engine);
        for (int flags = 0; flags < 256; flags++)
        {
            bool Flag(int bit) => (flags & (1 << bit)) != 0;
            var options = new PurgeOptionsView(Flag(0), Flag(1), Flag(2), Flag(3), Flag(4), Flag(5), Flag(6), Flag(7));
            var native = PurgeData.Plan(_engine, options);
            // Process/file locks can change between scans (including antivirus
            // checking the fixture executable). Compare option selection using
            // the same current process state for both previews.
            var cached = PurgeData.PlanFromScan(scan with
            {
                ClaudeRunning = native.ClaudeRunning,
                LiveSessions = native.LiveSessions,
            }, options);
            Assert.Equal(native.Items.ToArray(), cached.Items.ToArray());
            Assert.Equal(native.TotalBytes, cached.TotalBytes);
            Assert.Equal(native.Problems, cached.Problems);
            Assert.Equal(native.Warnings, cached.Warnings);
        }
    }

    [Fact]
    public void Cached_process_guards_only_block_selected_groups()
    {
        var scan = FixtureScan() with { AppProblems = ["browser-running", "process-check-failed"] };
        var options = new PurgeOptionsView(false, false, false, false, false, false);
        Assert.Empty(PurgeData.PlanFromScan(scan, options).Problems);
        Assert.Equal(new[] { "browser-running", "process-check-failed" },
            PurgeData.PlanFromScan(scan, options with { RemoveBrowserExtension = true }).Problems);
    }

    private static PurgeScanView FixtureScan() => new("native", false, 0, 0,
        [new("program", true, 1, [new("native-bin", "program", @"C:\fixture\claude.exe", 1, true, "file")]),
         new("browser", false, 2, [new("extension", "browser", @"C:\fixture\extension", 2, true, "dir")])]);

    private static Button Button(PurgeDialog dlg, string name) =>
        dlg.Controls.OfType<Button>().Single(b => b.Name == name);

    private static void Show(PurgeDialog dlg)
    {
        dlg.StartPosition = FormStartPosition.Manual;
        dlg.Location = new System.Drawing.Point(-32000, -32000);
        dlg.Show();
        Application.DoEvents(); // WinForms posts Shown after Show returns.
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var watch = Stopwatch.StartNew();
        while (!complete())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "UI work did not finish");
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    private sealed class UiScope : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;
        private readonly UiContext _context = new();
        private readonly bool _checkThreads = Control.CheckForIllegalCrossThreadCalls;
        public UiScope()
        {
            SynchronizationContext.SetSynchronizationContext(_context);
            Control.CheckForIllegalCrossThreadCalls = true;
        }
        public void Dispose()
        {
            Control.CheckForIllegalCrossThreadCalls = _checkThreads;
            SynchronizationContext.SetSynchronizationContext(_previous);
            _context.Dispose();
        }
    }

    // Keep the dispatcher alive after the dialog closes, just like the main
    // application window does while an abandoned background scan finishes.
    private sealed class UiContext : SynchronizationContext, IDisposable
    {
        private readonly Control _dispatcher = new();
        public UiContext() => _ = _dispatcher.Handle;
        public override void Post(SendOrPostCallback callback, object? state) =>
            _dispatcher.BeginInvoke(() => callback(state));
        public void Dispose() => _dispatcher.Dispose();
    }

    private string Seed(string relative, string text)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
}
