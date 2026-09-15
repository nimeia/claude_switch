using System.Text.Json.Nodes;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The shell's reading of cleanup plans.
///
/// The engine decides what may be deleted; these check that the dialogs report
/// it faithfully — above all that a session two conditions both select is one
/// session in the total, not two.
/// </summary>
public class HistoryCleanupTests
{
    private const string PlanJson = """
    {
      "items": [
        {"id": "a", "bytes": 100, "reasons": ["olderThan", "orphan"], "runIds": ["r1"]},
        {"id": "b", "bytes": 50, "reasons": ["largerThan"]},
        {"id": "c", "bytes": 7, "reasons": ["olderThan"], "blocked": "live"}
      ],
      "deletable": {"count": 2, "bytes": 150},
      "blocked": {"count": 1, "bytes": 7},
      "runRecords": 1,
      "byReason": {
        "olderThan": {"count": 1, "bytes": 100},
        "orphan": {"count": 1, "bytes": 100},
        "largerThan": {"count": 1, "bytes": 50}
      }
    }
    """;

    [Fact]
    public void A_plan_reads_its_totals_and_leaves_blocked_ids_out()
    {
        var plan = HistoryCleanup.ParsePlan(JsonNode.Parse(PlanJson));

        Assert.Equal(2, plan.DeletableCount);
        Assert.Equal(150, plan.DeletableBytes);
        Assert.Equal(1, plan.BlockedCount);
        Assert.Equal(new[] { "a", "b" }, plan.DeletableIds);
        Assert.Equal((1, 100L), plan.For("olderThan"));
        Assert.Equal((0, 0L), plan.For("missingDirectory"));
        Assert.Equal(3, plan.Items.Count);
        Assert.True(plan.Items.Single(i => i.Id == "c").Blocked);
    }

    [Fact]
    public void A_union_of_conditions_counts_each_session_once()
    {
        var plan = HistoryCleanup.ParsePlan(JsonNode.Parse(PlanJson));
        var on = new HashSet<string> { "olderThan", "orphan" };

        var picked = plan.Where(i => i.Reasons.Any(on.Contains));

        // "a" matches both conditions and is still one 100-byte session.
        Assert.Equal(1, picked.DeletableCount);
        Assert.Equal(100, picked.DeletableBytes);
        Assert.Equal(1, picked.BlockedCount);
        Assert.Equal(1, picked.RunRecords);
        Assert.Equal(new[] { "a" }, picked.DeletableIds);
    }

    [Fact]
    public void An_empty_selection_deletes_nothing()
    {
        var plan = HistoryCleanup.ParsePlan(JsonNode.Parse(PlanJson));
        var none = plan.Where(_ => false);
        Assert.Equal(0, none.DeletableCount);
        Assert.Empty(none.DeletableIds);
    }

    [Fact]
    public void Conditions_that_are_off_are_not_sent()
    {
        var rules = HistoryCleanup.ForRules(new CleanupRules(null, 0, true, false, true))["rules"]!;

        Assert.Null(rules["olderThanDays"]);
        Assert.Null(rules["largerThanMb"]);
        Assert.True(rules["missingDirectory"]!.GetValue<bool>());
        Assert.False(rules["removedAccounts"]!.GetValue<bool>());
        Assert.True(rules["orphans"]!.GetValue<bool>());
    }

    [Fact]
    public void Sessions_are_named_by_folder_and_id()
    {
        var request = HistoryCleanup.ForSessions([(@"C:\u\.claude\projects\D--a", "s1")]);
        var first = request["sessions"]![0]!;
        Assert.Equal(@"C:\u\.claude\projects\D--a", first["transcriptDir"]!.GetValue<string>());
        Assert.Equal("s1", first["id"]!.GetValue<string>());
    }

    [Fact]
    public void Retention_without_a_value_is_the_default()
    {
        var unset = HistoryCleanup.ParseRetention(
            JsonNode.Parse("""{"days": null, "defaultDays": 30, "maxDays": 3650}"""));
        Assert.Null(unset.Days);
        Assert.Equal(30, unset.Effective);

        var set = HistoryCleanup.ParseRetention(
            JsonNode.Parse("""{"days": 14, "defaultDays": 30, "maxDays": 3650}"""));
        Assert.Equal(14, set.Effective);

        var unreadable = HistoryCleanup.ParseRetention(
            JsonNode.Parse("""{"days": null, "defaultDays": 30, "error": "bad json"}"""));
        Assert.Equal("bad json", unreadable.Error);
    }

    [Fact]
    public void A_purge_preflight_lists_each_home()
    {
        var pre = HistoryCleanup.ParsePreflight(JsonNode.Parse("""
        {
          "roots": [
            {"sessions": 3, "bytes": 900, "registered": true, "transcriptFolders": 1},
            {"configDir": "C:\\b\\sessions\\2-x", "profileNumber": 2, "sessions": 0, "bytes": 0, "registered": true, "transcriptFolders": 0}
          ],
          "liveSessions": 1, "runningRuns": 0, "runIds": ["r9"]
        }
        """));

        Assert.Equal(2, pre.Roots.Count);
        Assert.Null(pre.Roots[0].ConfigDir);
        Assert.Equal(2, pre.Roots[1].ProfileNumber);
        Assert.Equal(1, pre.LiveSessions);
        Assert.Equal(new[] { "r9" }, pre.RunIds);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    public void Sizes_read_in_the_largest_whole_unit(long bytes, string expected) =>
        Assert.Equal(expected, HistoryCleanup.FormatBytes(bytes));

    [Fact]
    public void A_size_column_sorts_by_value_not_by_text()
    {
        // As text, "640 KB" sorts above "12 MB" — exactly backwards.
        var rows = new[] { Row("640 KB", 640L * 1024), Row("12 MB", 12L << 20), Row("3 B", 3) };
        var comparer = new ThemedListView.CellComparer(1, descending: true);

        Array.Sort(rows, comparer.Compare);

        Assert.Equal(new[] { "12 MB", "640 KB", "3 B" }, rows.Select(r => r.SubItems[1].Text));
    }

    [Fact]
    public void An_empty_directory_plan_names_what_it_would_remove()
    {
        var plan = HistoryCleanup.ParseRegistryPlan(JsonNode.Parse("""
        {
          "entries": [],
          "removablePaths": ["D:/dev/a", "D:/dev/b"],
          "liveDirectories": 1,
          "withMcp": 0,
          "backupDir": "C:\\backup\\claude-json"
        }
        """));
        Assert.Equal(new[] { "D:/dev/a", "D:/dev/b" }, plan.Paths);
        Assert.Equal(1, plan.LiveDirectories);
        Assert.Equal(@"C:\backup\claude-json", plan.BackupDir);

        var outcome = HistoryCleanup.ParseRegistryOutcome(JsonNode.Parse("""
        {"removedDirectories": 2, "removedEntries": 3, "skippedDirectories": 0,
         "backups": ["C:\\backup\\claude-json\\default.json"], "failures": []}
        """));
        Assert.Equal(2, outcome.RemovedDirectories);
        Assert.Equal(3, outcome.RemovedEntries);
        Assert.Single(outcome.Backups);
        Assert.Empty(outcome.Failures);
    }

    private static ListViewItem Row(string text, long bytes)
    {
        var item = new ListViewItem("x");
        item.SubItems.Add(new ListViewItem.ListViewSubItem(item, text) { Tag = bytes });
        return item;
    }
}
