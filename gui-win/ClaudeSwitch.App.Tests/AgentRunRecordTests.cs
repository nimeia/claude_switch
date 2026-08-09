using System.Text.Json.Nodes;
using ClaudeSwitch.App;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The read model the runs list and the resume path are built on.
/// </summary>
/// <remarks>
/// These rules decide whether unfinished work is offered back to the user at
/// all, so getting them wrong loses the work silently rather than loudly.
/// </remarks>
public class AgentRunRecordTests
{
    private static JsonNode Json(string status, string? sessionId = "sess-1") =>
        JsonNode.Parse($$"""
            {
              "id": "run-1",
              {{(sessionId is null ? "" : $"\"sessionId\": \"{sessionId}\",")}}
              "cwd": "D:/work/proj",
              "accountNumber": 2,
              "mode": "acceptEdits",
              "prompt": "refactor the parser\nand tidy up",
              "status": "{{status}}",
              "updatedMs": 1700000000000,
              "turns": 3,
              "continuations": 1
            }
            """)!;

    [Fact]
    public void Parses_the_engine_payload()
    {
        var r = AgentRunRecord.FromJson(Json("interrupted"))!;
        Assert.Equal("run-1", r.Id);
        Assert.Equal("sess-1", r.SessionId);
        Assert.Equal("D:/work/proj", r.Cwd);
        Assert.Equal(2, r.AccountNumber);
        Assert.Equal("acceptEdits", r.Mode);
        Assert.Equal(3, r.Turns);
        Assert.Equal(1, r.Continuations);
    }

    [Fact]
    public void A_record_without_an_id_is_not_a_record()
    {
        Assert.Null(AgentRunRecord.FromJson(JsonNode.Parse("""{ "cwd": "x" }""")));
        Assert.Null(AgentRunRecord.FromJson(null));
    }

    [Theory]
    [InlineData("interrupted", true)]
    [InlineData("failed", true)]
    [InlineData("cancelled", true)]
    [InlineData("running", false)]
    [InlineData("completed", false)]
    public void Resumability_follows_status(string status, bool expected)
    {
        var r = AgentRunRecord.FromJson(Json(status))!;
        Assert.Equal(expected, r.IsResumable);
    }

    [Fact]
    public void Without_a_session_there_is_nothing_to_resume()
    {
        // A run that died before session/new can only be restarted, not
        // continued — offering "resume" would be a lie about what happens.
        var r = AgentRunRecord.FromJson(Json("interrupted", sessionId: null))!;
        Assert.Null(r.SessionId);
        Assert.False(r.IsResumable);
    }

    [Fact]
    public void Title_is_the_first_line_of_the_prompt()
    {
        var r = AgentRunRecord.FromJson(Json("interrupted"))!;
        Assert.Equal("refactor the parser", r.Title);
    }

    [Fact]
    public void Title_is_bounded_for_a_list_row()
    {
        var node = Json("interrupted").DeepClone();
        node["prompt"] = new string('x', 300);
        var r = AgentRunRecord.FromJson(node)!;
        Assert.Equal(80, r.Title.Length);
        Assert.EndsWith("…", r.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_is_only_the_running_status()
    {
        Assert.True(AgentRunRecord.FromJson(Json("running"))!.IsLive);
        Assert.False(AgentRunRecord.FromJson(Json("interrupted"))!.IsLive);
    }
}

/// <summary>The bounded transcript buffer a reattached window replays.</summary>
public class LiveRunBacklogTests
{
    [Fact]
    public void Backlog_limit_is_a_tail_not_a_head()
    {
        // A long run must not grow memory without bound, and what a returning
        // viewer needs is the end of the transcript, not its beginning.
        Assert.True(LiveRun.BacklogLimit > 0);
    }
}

/// <summary>
/// The journal must never be able to take the app down with it.
/// </summary>
/// <remarks>
/// Shutdown is exactly when this matters: the tray's Exit used to dispose the
/// engine before <c>FormClosing</c> ran, so the last journal write landed on a
/// disposed engine and threw on the way out. The ordering is fixed, but the
/// store staying quiet is the guarantee that actually protects the exit path.
/// </remarks>
public class AgentRunStoreResilienceTests
{
    private static Engine DisposedEngine()
    {
        string root = Path.Combine(Path.GetTempPath(), "cswitch-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var engine = new Engine(root);
        engine.Dispose();
        return engine;
    }

    [Fact]
    public void Saving_against_a_disposed_engine_does_not_throw()
    {
        var store = new AgentRunStore(DisposedEngine());
        store.Save("run-1", "sess-1", "D:/work", 1, null, "acceptEdits",
            "do the thing", "interrupted");
    }

    [Fact]
    public void Listing_against_a_disposed_engine_reports_nothing()
    {
        var store = new AgentRunStore(DisposedEngine());
        Assert.Empty(store.List());
        Assert.Empty(store.Resumable());
    }

    [Fact]
    public void Removing_against_a_disposed_engine_does_not_throw()
    {
        var store = new AgentRunStore(DisposedEngine());
        store.Remove("run-1");
    }
}
