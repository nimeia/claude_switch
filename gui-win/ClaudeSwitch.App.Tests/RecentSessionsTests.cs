using System.Text.Json.Nodes;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The one-click way back into a conversation. Built from the directory listing
/// the app already computes, so the rules that matter are about which rows can
/// actually be resumed and how they read in a menu.
/// </summary>
public class RecentSessionsTests
{
    private static JsonNode Projects(string inner) =>
        JsonNode.Parse("{\"projects\":[" + inner + "]}")!;

    private static string Project(
        string name, string? sessionId, int sessionCount, string? prompt = null, long? active = null)
    {
        var o = new JsonObject
        {
            ["path"] = $"D:/dev/{name}",
            ["name"] = name,
            ["sessionCount"] = sessionCount,
        };
        if (sessionId is not null) o["lastSessionId"] = sessionId;
        if (prompt is not null) o["lastPrompt"] = prompt;
        if (active is not null) o["lastActiveMs"] = active;
        return o.ToJsonString();
    }

    [Fact]
    public void Only_directories_with_a_resumable_session_are_offered()
    {
        var node = Projects(string.Join(",", [
            Project("worked-in", "sess-1", 3, "修一下构建"),
            // Registered but never used: there is no conversation to resume.
            Project("registered-only", null, 0),
            // A stale id with no transcripts behind it would fail on launch.
            Project("no-transcripts", "sess-ghost", 0, "old"),
        ]));

        var got = RecentSessions.Parse(node, 8);
        Assert.Single(got);
        Assert.Equal("worked-in", got[0].Name);
        Assert.Equal("sess-1", got[0].SessionId);
    }

    [Fact]
    public void The_list_is_capped()
    {
        var many = Enumerable.Range(0, 20).Select(i => Project($"p{i}", $"s{i}", 1));
        var got = RecentSessions.Parse(Projects(string.Join(",", many)), 5);
        Assert.Equal(5, got.Count);
        // Order is the engine's (newest first); the cap must not reshuffle it.
        Assert.Equal("p0", got[0].Name);
        Assert.Equal("p4", got[4].Name);
    }

    [Fact]
    public void Nothing_to_offer_is_an_empty_list_not_a_crash()
    {
        Assert.Empty(RecentSessions.Parse(null, 8));
        Assert.Empty(RecentSessions.Parse(JsonNode.Parse("{}"), 8));
        Assert.Empty(RecentSessions.Parse(Projects(""), 8));
    }

    [Fact]
    public void The_label_leads_with_the_directory()
    {
        // The directory is what the reader is choosing between; the prompt only
        // disambiguates.
        var s = new RecentSession("D:/dev/adsense", "adsense", "id", "帮我分析浏览器隔离", null);
        var label = RecentSessions.MenuLabel(s);
        Assert.StartsWith("adsense", label);
        Assert.Contains("帮我分析浏览器隔离", label);
    }

    [Fact]
    public void A_long_prompt_is_clipped_rather_than_stretching_the_menu()
    {
        var s = new RecentSession("D:/x", "x", "id", new string('长', 200), null);
        var label = RecentSessions.MenuLabel(s, titleCells: 20);
        Assert.True(label.Length < 40, $"len={label.Length}: {label}");
        Assert.EndsWith("…", label);
    }

    [Fact]
    public void Clipping_counts_display_width_not_characters()
    {
        // 26 Chinese characters take the width of 52 Latin ones. Counting
        // characters let rows overflow the window and pushed the timestamp off
        // the right edge.
        Assert.Equal("abcdef", RecentSessions.Truncate("abcdef", 6));
        Assert.Equal("abcde…", RecentSessions.Truncate("abcdefgh", 5));
        // Five wide glyphs fill a 10-cell budget exactly; the sixth overflows.
        Assert.Equal("看看看看看", RecentSessions.Truncate("看看看看看", 10));
        Assert.Equal("看看看看看…", RecentSessions.Truncate("看看看看看看", 10));
        // A mixed string spends the budget proportionally.
        Assert.Equal("ab看看…", RecentSessions.Truncate("ab看看看看", 6));
        Assert.Equal("", RecentSessions.Truncate("anything", 0));
    }

    [Fact]
    public void The_time_sits_before_the_prompt_so_clipping_cannot_eat_it()
    {
        var s = new RecentSession(
            "D:/x", "proj", "id", new string('长', 200), 1_785_000_000_000);
        var label = RecentSessions.MenuLabel(s);
        int timeAt = label.IndexOf('·');
        int titleAt = label.IndexOf('长');
        Assert.True(timeAt > 0 && titleAt > timeAt, label);
    }

    [Fact]
    public void A_session_with_no_usable_prompt_still_gets_a_row()
    {
        // Some transcripts open with nothing but injected machinery; the entry is
        // still resumable, so it must not vanish from the menu.
        var s = new RecentSession("D:/x", "my-proj", "id", null, null);
        Assert.Equal("my-proj", RecentSessions.MenuLabel(s));
    }

    [Fact]
    public void Ampersands_survive_as_text()
    {
        // WinForms reads a single & as a mnemonic and swallows it.
        var s = new RecentSession("D:/x", "a&b", "id", "c&d", null);
        Assert.Contains("a&&b", RecentSessions.MenuLabel(s));
        Assert.Contains("c&&d", RecentSessions.MenuLabel(s));
    }
}

/// <summary>
/// Locating the Claude Code CLI. Resolved before launching because, without a
/// shell wrapping the call, a failed start leaves no window to read an error in.
/// </summary>
public class ClaudeCliTests
{
    [Fact]
    public void A_missing_command_resolves_to_null()
    {
        Assert.Null(ClaudeCli.Find("definitely-not-a-real-command-xyzzy"));
    }

    [Fact]
    public void A_command_on_PATH_resolves_with_its_extension()
    {
        // cmd.exe is on PATH on every Windows machine and exercises PATHEXT.
        var found = ClaudeCli.Find("cmd");
        Assert.NotNull(found);
        Assert.True(File.Exists(found), found);
        Assert.EndsWith(".exe", found, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resuming_into_a_missing_directory_reports_that_not_a_launch_failure()
    {
        var problem = ClaudeCli.Resume(
            Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid()), "sess");
        Assert.NotNull(problem);
        Assert.Contains("目录不存在", problem);
    }

    [Fact]
    public void The_not_found_message_says_what_to_do()
    {
        Assert.Contains("安装 Claude Code", ClaudeCli.NotFoundMessage);
        Assert.Contains("PATH", ClaudeCli.NotFoundMessage);
    }
}
