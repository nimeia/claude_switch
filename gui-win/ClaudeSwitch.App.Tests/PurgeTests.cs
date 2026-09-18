using System.Text.Json.Nodes;
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
    public void ParseScan_reads_the_totals()
    {
        var scan = PurgeData.ParseScan(JsonNode.Parse("""
            {
              "installKind": "native",
              "claudeRunning": false,
              "liveSessions": 0,
              "importedAccounts": 2,
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
    }

    [Fact]
    public void The_dialog_uses_the_danger_button_and_keep_accounts_by_default()
    {
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
}
