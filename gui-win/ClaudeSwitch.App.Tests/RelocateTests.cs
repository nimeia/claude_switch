using System.Text.Json.Nodes;
using System.Windows.Forms;
using ClaudeSwitch.App;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>Moving Claude data to another drive: JSON the dialog reads, and layout.</summary>
public class RelocateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cswitch-reloc-" + Guid.NewGuid().ToString("N"));
    private readonly Engine _engine;

    public RelocateTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "settings.json"), "{\"a\":1}");
        _engine = new Engine(_root);
    }

    public void Dispose()
    {
        try { RelocateData.Restore(_engine); } catch (Exception) { /* not linked */ }
        _engine.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_scan_lists_both_trees()
    {
        var scan = RelocateData.Scan(_engine);
        Assert.Equal(2, scan.Trees.Count);
        Assert.Contains(scan.Trees, t => t.Id == "claude");
        Assert.Contains(scan.Trees, t => t.Id == "swap");
        Assert.False(scan.Relocated);
        Assert.True(scan.Trees.First(t => t.Id == "claude").Bytes > 0);
    }

    [Fact]
    public void A_relative_destination_is_rejected_before_the_engine()
    {
        var plan = RelocateData.Plan(_engine, "ClaudeData");
        Assert.Contains("dest-not-absolute", plan.Problems);
        Assert.False(plan.CanApply);
    }

    [Fact]
    public void Apply_then_scan_shows_the_live_path_as_a_link()
    {
        string dest = Path.Combine(_root, "offload");
        RelocateData.Apply(_engine, dest);

        var scan = RelocateData.Scan(_engine);
        Assert.True(scan.Relocated);
        var claude = scan.Trees.Single(t => t.Id == "claude");
        Assert.True(claude.Linked);
        Assert.True(claude.BackupExists);
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(claude.Source, "settings.json")));

        RelocateData.Restore(_engine);
        var after = RelocateData.Scan(_engine);
        Assert.False(after.Relocated);
        Assert.False(after.Trees.Single(t => t.Id == "claude").Linked);
    }

    [Fact]
    public void ParseScan_reads_the_totals()
    {
        var scan = RelocateData.ParseScan(JsonNode.Parse("""
            {
              "trees": [
                {"id":"claude","source":"C:\\a\\.claude","bytes":100,"exists":true,
                 "linked":false,"backupPath":"C:\\a\\.claude.reloc-backup",
                 "backupExists":false,"backupBytes":0}
              ],
              "totalBytes": 100,
              "liveSessions": 2,
              "claudeJsonBytes": 12,
              "relocated": false
            }
            """));
        Assert.Equal(100, scan.TotalBytes);
        Assert.Equal(2, scan.LiveSessions);
        Assert.Equal("claude", scan.Trees[0].Id);
    }

    [Fact]
    public void The_dialog_stacks_the_destination_row()
    {
        using var lang = Loc.Scoped("zh-Hans");
        using var dlg = new RelocateDialog(_engine);
        var dest = dlg.Controls.OfType<TextBox>().Single(t => t.Name == "relocateDest");
        var browse = dlg.Controls.OfType<Button>().Single(b => b.Name == "relocateBrowse");
        Assert.Equal(Loc.T("relocate.title"), dlg.Text);
        Assert.True(dest.Width > 0);
        Assert.True(browse.Left >= dest.Right);
        Assert.True(Math.Abs(dest.Top - browse.Top) < dest.Height);
        Assert.True(Path.IsPathRooted(dest.Text));
    }
}
