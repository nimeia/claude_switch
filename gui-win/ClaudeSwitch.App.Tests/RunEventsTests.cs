using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// What a supervised run's ACP updates become in the transcript.
/// </summary>
/// <remarks>
/// The shapes here are the claude-agent-acp adapter's own — including the
/// <c>_meta</c> fields that carry a Bash call's output. The web view draws
/// whatever these lines carry, so a field dropped here is a panel that silently
/// stays empty.
/// </remarks>
public class RunEventsTests
{
    private static List<RunLine> Map(string json, ref string? streaming) =>
        RunEvents.FromUpdate(JsonNode.Parse(json)!, ref streaming);

    private static List<RunLine> Map(string json)
    {
        string? streaming = null;
        return Map(json, ref streaming);
    }

    [Fact]
    public void A_new_message_starts_on_a_line_of_its_own()
    {
        string? streaming = "msg_01";
        var lines = Map("""{"sessionUpdate":"agent_message_chunk","messageId":"msg_02","content":{"type":"text","text":"hello"}}""", ref streaming);

        Assert.Equal(2, lines.Count);
        Assert.Equal(Environment.NewLine, lines[0].Text);
        Assert.Equal("hello", lines[1].Text);
        Assert.All(lines, l => Assert.Equal("msg_02", l.Data?["messageId"]?.GetValue<string>()));
        Assert.Equal("msg_02", streaming);
    }

    [Fact]
    public void More_of_the_same_message_does_not_repeat_the_separator()
    {
        string? streaming = "msg_02";
        var lines = Map("""{"sessionUpdate":"agent_message_chunk","messageId":"msg_02","content":{"type":"text","text":" world"}}""", ref streaming);

        var line = Assert.Single(lines);
        Assert.Equal(RunLineKind.Assistant, line.Kind);
        Assert.Equal(" world", line.Text);
    }

    [Fact]
    public void A_tool_call_keeps_its_id_status_and_diff_and_ends_the_message()
    {
        string? streaming = "msg_02";
        var lines = Map("""
            {"sessionUpdate":"tool_call","toolCallId":"toolu_1","title":"Edit README.md","kind":"edit","status":"pending",
             "content":[{"type":"diff","path":"D:/w/README.md","oldText":"a","newText":"b"}],
             "rawInput":{"file_path":"D:/w/README.md"},"_meta":{"claudeCode":{"toolName":"Edit"}}}
            """, ref streaming);

        var line = Assert.Single(lines);
        Assert.Equal(RunLineKind.Tool, line.Kind);
        Assert.Equal("  ⚙ Edit README.md", line.Text);
        var data = line.Data!;
        Assert.Equal("toolu_1", data["id"]!.GetValue<string>());
        Assert.Equal("edit", data["kind"]!.GetValue<string>());
        Assert.Equal("pending", data["status"]!.GetValue<string>());
        Assert.Equal("Edit", data["toolName"]!.GetValue<string>());
        Assert.Equal("b", data["content"]![0]!["newText"]!.GetValue<string>());
        // The raw input is the model's arguments, not something the page draws.
        Assert.Null(data["rawInput"]);
        Assert.Null(streaming);
    }

    [Fact]
    public void Command_output_and_exit_code_come_from_the_adapter_meta()
    {
        var output = Assert.Single(Map("""
            {"sessionUpdate":"tool_call_update","toolCallId":"toolu_2",
             "_meta":{"terminal_output":{"terminal_id":"toolu_2","data":"\u001b[32mok\u001b[0m\n"}}}
            """));
        Assert.Equal(RunLineKind.ToolUpdate, output.Kind);
        Assert.Equal("\u001b[32mok\u001b[0m\n", output.Data!["terminalOutput"]!.GetValue<string>());
        Assert.Null(output.Data["status"]);

        var exit = Assert.Single(Map("""
            {"sessionUpdate":"tool_call_update","toolCallId":"toolu_2","status":"failed",
             "_meta":{"claudeCode":{"toolName":"Bash"},"terminal_exit":{"terminal_id":"toolu_2","exit_code":2,"signal":null}}}
            """));
        Assert.Equal("failed", exit.Text);
        Assert.Equal(2, exit.Data!["exitCode"]!.GetValue<int>());
    }

    [Fact]
    public void An_update_that_names_no_tool_call_is_dropped()
    {
        Assert.Empty(Map("""{"sessionUpdate":"tool_call_update","status":"completed"}"""));
    }

    [Fact]
    public void Plan_entries_are_carried_whole()
    {
        var line = Assert.Single(Map("""
            {"sessionUpdate":"plan","entries":[{"content":"a","status":"completed","priority":"high"},{"content":"b","status":"in_progress","priority":"low"}]}
            """));
        Assert.Equal(RunLineKind.Plan, line.Kind);
        var entries = line.Data!["entries"]!.AsArray();
        Assert.Equal(2, entries.Count);
        Assert.Equal("in_progress", entries[1]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void Thinking_is_kept_apart_from_the_reply()
    {
        string? streaming = null;
        var line = Assert.Single(Map("""{"sessionUpdate":"agent_thought_chunk","messageId":"msg_01","content":{"type":"text","text":"hmm"}}""", ref streaming));
        Assert.Equal(RunLineKind.Thought, line.Kind);
        // Thinking must not claim the message: the reply that follows still
        // needs its own separator.
        Assert.Null(streaming);
    }

    [Theory]
    [InlineData("""{"sessionUpdate":"available_commands_update","availableCommands":[]}""")]
    [InlineData("""{"sessionUpdate":"usage_update","used":1}""")]
    [InlineData("""{"sessionUpdate":"agent_message_chunk","content":{"type":"image","data":"x"}}""")]
    [InlineData("""[1,2,3]""")]
    public void Updates_with_nothing_to_draw_produce_no_lines(string json)
    {
        Assert.Empty(Map(json));
    }

    [Fact]
    public void The_client_asks_for_command_output_without_offering_terminals()
    {
        var caps = AcpSession.ClientCapabilities();
        Assert.False(caps["terminal"]!.GetValue<bool>());
        Assert.True(caps["_meta"]!["terminal_output"]!.GetValue<bool>());
    }

    [Fact]
    public void Events_are_sent_with_camel_case_kinds_and_their_data()
    {
        var lines = new List<RunLine>
        {
            new(RunLineKind.Prompt, "› 你好"),
            new(RunLineKind.ToolUpdate, "completed", new JsonObject { ["id"] = "toolu_1" }),
        };

        var message = JsonNode.Parse(TranscriptPayload.Events(lines, 0))!;
        Assert.Equal("events", message["type"]!.GetValue<string>());
        var events = message["events"]!.AsArray();
        Assert.Equal("prompt", events[0]!["k"]!.GetValue<string>());
        Assert.Equal("› 你好", events[0]!["t"]!.GetValue<string>());
        Assert.Null(events[0]!["d"]);
        Assert.Equal("toolUpdate", events[1]!["k"]!.GetValue<string>());
        Assert.Equal("toolu_1", events[1]!["d"]!["id"]!.GetValue<string>());

        // Only the lines not yet sent.
        Assert.Single(JsonNode.Parse(TranscriptPayload.Events(lines, 1))!["events"]!.AsArray());
    }

    [Fact]
    public void Every_file_the_page_loads_is_embedded()
    {
        var page = AgentViewAssets.Get("agent-view.html");
        Assert.NotNull(page);

        var references = Regex.Matches(Encoding.UTF8.GetString(page!), "(?:src|href)=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.Contains("vendor/xterm.js", references);
        Assert.All(references, r => Assert.NotNull(AgentViewAssets.Get(r)));
    }

    [Fact]
    public void Every_string_the_page_asks_for_exists()
    {
        var keys = Loc.Keys(Loc.BaseLanguage).ToHashSet();
        Assert.All(TranscriptPayload.StringKeys, key => Assert.Contains(key, keys));
    }

    [Fact]
    public void The_sample_run_covers_everything_the_page_draws()
    {
        var kinds = AgentSampleRun.Lines(@"D:\work").Select(l => l.Kind).ToHashSet();
        foreach (var kind in new[]
                 {
                     RunLineKind.Prompt, RunLineKind.Thought, RunLineKind.Assistant, RunLineKind.Tool,
                     RunLineKind.ToolUpdate, RunLineKind.Plan, RunLineKind.Warning, RunLineKind.Notice,
                 })
        {
            Assert.Contains(kind, kinds);
        }
    }
}
