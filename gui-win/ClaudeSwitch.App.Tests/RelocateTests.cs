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
    public void ParsePlan_reads_the_link_count()
    {
        var plan = RelocateData.ParsePlan(JsonNode.Parse("""
            {
              "destRoot": "D:\\ClaudeData",
              "sameVolume": false,
              "liveSessions": 0,
              "copyBytes": 10,
              "links": 3,
              "warnings": ["links"],
              "problems": [],
              "trees": [{"id":"claude","source":"C:\\a\\.claude","dest":"D:\\ClaudeData\\claude",
                         "backup":"C:\\a\\.claude.reloc-backup","bytes":10,"skip":false}]
            }
            """));
        Assert.Equal(3, plan.Links);
        Assert.True(plan.CanApply);
        Assert.Contains("3", RelocateData.WarningText("links", plan));
    }

    [Fact]
    public void Engine_errors_read_as_sentences()
    {
        using var lang = Loc.Scoped("en");
        var plan = RelocatePlanView.Invalid("nothing");
        var inUse = new EngineException(9, """
            {"error":{"code":"session-in-use","message":"session in use: relocate: in-use C:\\Users\\a\\.claude"}}
            """);
        Assert.Equal(
            Loc.T("relocate.error.in-use", @"C:\Users\a\.claude"),
            RelocateData.Describe(inUse, plan));

        var space = new EngineException(3, """
            {"error":{"code":"validation-failed","message":"validation failed: relocate: not-enough-space need 2048 have 1024"}}
            """);
        Assert.Equal(
            Loc.T("relocate.problem.no-space", HistoryCleanup.FormatBytes(2048), HistoryCleanup.FormatBytes(1024)),
            RelocateData.Describe(space, plan));

        var live = new EngineException(9, """
            {"error":{"code":"session-in-use","message":"session in use: 2 live Claude Code session(s)"}}
            """);
        Assert.Equal(Loc.T("relocate.problem.live-sessions"), RelocateData.Describe(live, plan));
    }

    [Fact]
    public void Undo_stays_available_when_only_one_tree_is_linked()
    {
        string swap = Path.Combine(_root, ".claude-swap-backup");
        Directory.CreateDirectory(swap);
        File.WriteAllText(Path.Combine(swap, "sequence.json"), "{}");
        RelocateData.Apply(_engine, Path.Combine(_root, "offload"));

        // Put the swap tree back by hand, as a move that stopped halfway would.
        Directory.Delete(swap);
        Directory.Move(swap + ".reloc-backup", swap);
        var scan = RelocateData.Scan(_engine);
        Assert.False(scan.Relocated);

        using var dlg = new RelocateDialog(_engine);
        var undo = dlg.Controls.OfType<Button>().Single(b => b.Name == "relocateRestore");
        Assert.True(undo.Enabled);
    }

    [Fact]
    public void Rows_do_not_overlap_once_the_backup_lines_appear()
    {
        RelocateData.Apply(_engine, Path.Combine(_root, "offload"));
        using var lang = Loc.Scoped("zh-Hans");
        using var dlg = new RelocateDialog(_engine);
        var labels = dlg.Controls.OfType<Label>().OrderBy(l => l.Top).ToList();
        for (int i = 1; i < labels.Count; i++)
            Assert.True(labels[i - 1].Bottom <= labels[i].Top, $"'{labels[i - 1].Text}' runs into '{labels[i].Text}'");
        var move = dlg.Controls.OfType<Button>().Single(b => b.Name == "relocateApply");
        Assert.True(labels[^1].Bottom <= move.Top);
        Assert.True(move.Bottom <= dlg.ClientSize.Height);
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
