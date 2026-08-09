using System.Text.Json.Nodes;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The read model behind the automatic takeover of stalled terminal sessions.
/// </summary>
/// <remarks>
/// A misread field here points a takeover at the wrong account or the wrong
/// directory, so the resumed conversation either fails to load or lands under a
/// profile that does not hold it.
/// </remarks>
public class StalledRecordTests
{
    private static JsonNode Json(string extra = "") =>
        JsonNode.Parse($$"""
            {
              "sessionId": "11111111-2222-3333-4444-555555555555",
              "file": "C:/Users/x/.claude/projects/D--work/11111111.jsonl",
              "cwd": "D:/work/proj",
              "accountNumber": 2,
              "configDir": "D:/backup/sessions/2-a_x.com",
              "modifiedMs": 1700000000000,
              "idleMs": 1800000,
              "reason": "rateLimit",
              "lastError": "Claude AI usage limit reached|1700000000"
              {{extra}}
            }
            """)!;

    [Fact]
    public void Parses_the_engine_payload()
    {
        var r = StalledRecord.FromJson(Json())!;
        Assert.Equal("11111111-2222-3333-4444-555555555555", r.SessionId);
        Assert.Equal("D:/work/proj", r.Cwd);
        Assert.Equal(2, r.AccountNumber);
        Assert.Equal("D:/backup/sessions/2-a_x.com", r.ConfigDir);
        Assert.Equal("rateLimit", r.Reason);
        Assert.Contains("usage limit", r.LastError);
    }

    [Fact]
    public void Idle_is_reported_in_whole_minutes()
    {
        // What the dropdown tooltip shows; 30 minutes, not 1800000.
        var r = StalledRecord.FromJson(Json())!;
        Assert.Equal(30, r.IdleMinutes);
    }

    [Fact]
    public void A_default_login_session_has_no_account_or_profile()
    {
        // Absent rather than zero: account 0 does not exist, and a config dir of
        // "" would send the agent looking for a profile directory named nothing.
        var json = JsonNode.Parse("""
            {
              "sessionId": "abc",
              "cwd": "D:/work",
              "modifiedMs": 1,
              "idleMs": 1,
              "reason": "network",
              "lastError": "fetch failed"
            }
            """)!;
        var r = StalledRecord.FromJson(json)!;
        Assert.Null(r.AccountNumber);
        Assert.Null(r.ConfigDir);
    }

    [Fact]
    public void A_record_without_a_session_id_is_rejected()
    {
        // There would be nothing to resume: the id is the whole point.
        Assert.Null(StalledRecord.FromJson(JsonNode.Parse("""{ "cwd": "D:/work" }""")));
        Assert.Null(StalledRecord.FromJson(null));
    }

    [Fact]
    public void Negative_idle_does_not_become_a_negative_minute_count()
    {
        // A clock skew must not render as "stalled -3 minutes ago".
        var json = JsonNode.Parse("""
            {
              "sessionId": "abc", "cwd": "D:/work",
              "modifiedMs": 1, "idleMs": -180000,
              "reason": "network", "lastError": "x"
            }
            """)!;
        Assert.Equal(0, StalledRecord.FromJson(json)!.IdleMinutes);
    }
}
