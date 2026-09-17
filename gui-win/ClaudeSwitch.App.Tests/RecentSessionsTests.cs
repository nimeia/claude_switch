using System.Collections.Generic;
using System.Text.Json.Nodes;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The one-click way back into a conversation. The engine picks and orders what
/// can be resumed; the rules that matter here are that its order and flags
/// survive, and how rows read in a menu.
/// </summary>
public class RecentSessionsTests
{
    private static JsonNode Recent(params string[] sessions) =>
        JsonNode.Parse("{\"sessions\":[" + string.Join(",", sessions) + "]}")!;

    private static string Session(
        string directory, string id, string? prompt = null, long? modified = null, int? profile = null, bool live = false,
        string? title = null, string? file = null, long? bytes = null)
    {
        var o = new JsonObject
        {
            ["sessionId"] = id,
            ["path"] = $"D:/dev/{directory}",
            ["name"] = directory,
            ["configHome"] = profile is null ? @"C:\u\.claude" : $@"C:\b\sessions\{profile}-x",
            ["live"] = live,
        };
        if (prompt is not null) o["prompt"] = prompt;
        if (title is not null) o["title"] = title;
        if (file is not null) o["file"] = file;
        if (bytes is not null) o["totalBytes"] = bytes;
        if (modified is not null) o["modifiedMs"] = modified;
        if (profile is not null) o["profileNumber"] = profile;
        return o.ToJsonString();
    }

    [Fact]
    public void Every_recent_conversation_is_listed_not_one_per_directory()
    {
        // Two conversations touched today in one directory, one last week in
        // another: all three, newest first, as the engine ordered them.
        var got = RecentSessions.Parse(
            Recent(
                Session("busy", "s-afternoon", modified: 3_000),
                Session("busy", "s-morning", modified: 2_000),
                Session("quiet", "s-last-week", modified: 1_000)),
            8);

        Assert.Equal(new[] { "s-afternoon", "s-morning", "s-last-week" }, got.Select(s => s.SessionId));
        Assert.Equal("busy", got[1].Name);
    }

    [Fact]
    public void The_list_is_capped()
    {
        var many = Enumerable.Range(0, 20).Select(i => Session($"p{i}", $"s{i}")).ToArray();
        var got = RecentSessions.Parse(Recent(many), 5);
        Assert.Equal(5, got.Count);
        // The cap must not reshuffle the engine's order.
        Assert.Equal("s0", got[0].SessionId);
        Assert.Equal("s4", got[4].SessionId);
    }

    [Fact]
    public void Nothing_to_offer_is_an_empty_list_not_a_crash()
    {
        Assert.Empty(RecentSessions.Parse(null, 8));
        Assert.Empty(RecentSessions.Parse(JsonNode.Parse("{}"), 8));
        Assert.Empty(RecentSessions.Parse(Recent(), 8));
        Assert.Empty(RecentSessions.Parse(Recent("{\"path\":\"D:/x\"}"), 8));
    }

    [Fact]
    public void A_running_conversation_is_listed_and_flagged()
    {
        // The newest update is usually the conversation still open. It is shown
        // so the list is not stale, and flagged so the menu will not resume it.
        var got = RecentSessions.Parse(
            Recent(Session("here", "s-open", live: true), Session("here", "s-before")),
            8);
        Assert.True(got[0].Live);
        Assert.False(got[1].Live);
    }

    [Fact]
    public void A_profile_conversation_carries_its_profile()
    {
        var got = RecentSessions.Parse(
            Recent(Session("in-profile", "sess-p", profile: 2), Session("in-default", "sess-d")),
            8);
        Assert.Equal(2, got.Single(s => s.Name == "in-profile").ProfileNumber);
        Assert.Null(got.Single(s => s.Name == "in-default").ProfileNumber);
    }

    [Fact]
    public void Claude_Codes_own_title_wins_over_the_opening_prompt()
    {
        // A continued conversation's opening record is the compaction summary;
        // the title Claude Code keeps for it says what it is about.
        var got = RecentSessions.Parse(
            Recent(
                Session("titled", "s-titled", prompt: "帮我看看这个项目", title: "查看项目情况"),
                Session("untitled", "s-plain", prompt: "修一下构建")),
            8);
        Assert.Equal("查看项目情况", got[0].Title);
        Assert.Equal("修一下构建", got[1].Title);
    }

    [Fact]
    public void A_row_carries_its_transcript_and_size()
    {
        // The conversations window deletes by transcript and sorts by size.
        var got = RecentSessions.Parse(
            Recent(Session("p", "s1", file: @"C:\u\.claude\projects\p\s1.jsonl", bytes: 4096)),
            8);
        Assert.Equal(@"C:\u\.claude\projects\p\s1.jsonl", got[0].File);
        Assert.Equal(4096, got[0].Bytes);
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
    public void The_cmd_script_sets_proxy_env_before_launching()
    {
        string script = ClaudeCli.BuildCmdEnvScript(
            @"C:\claude.exe",
            sessionId: null,
            configDir: null,
            scrubEnv: [],
            extraEnv: new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://127.0.0.1:7897" });
        Assert.Contains("HTTPS_PROXY=http://127.0.0.1:7897", script);
        Assert.Contains(@"C:\claude.exe", script);
    }

    [Fact]
    public void Resuming_into_a_missing_directory_reports_that_not_a_launch_failure()
    {
        // Asserts wording, so it must name the language it asserts — the
        // active one is process-wide and another test may have changed it.
        using var lang = Loc.Scoped("zh-Hans");
        var problem = ClaudeCli.Resume(
            Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid()), "sess");
        Assert.NotNull(problem);
        Assert.Contains("目录不存在", problem);
    }

    [Fact]
    public void The_not_found_message_says_what_to_do()
    {
        // Asserts wording, so it must name the language it asserts — the
        // active one is process-wide and another test may have changed it.
        using var lang = Loc.Scoped("zh-Hans");
        Assert.Contains("安装 Claude Code", ClaudeCli.NotFoundMessage);
        Assert.Contains("PATH", ClaudeCli.NotFoundMessage);
    }
}
