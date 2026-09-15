using System.Text.Json.Nodes;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// "Open a terminal as this account": which directories are offered, in what
/// order, and when continuing their conversation can actually work.
/// </summary>
public class SessionLaunchDialogTests
{
    private static string Dir(string name, string? session, long modified = 0, int? profile = null, bool exists = true)
    {
        var o = new JsonObject
        {
            ["path"] = $"D:/dev/{name}",
            ["name"] = name,
            ["directoryExists"] = exists,
        };
        if (session is not null)
        {
            var resume = new JsonObject
            {
                ["sessionId"] = session,
                ["modifiedMs"] = modified,
                ["prompt"] = $"about {name}",
            };
            if (profile is not null) resume["profileNumber"] = profile;
            o["resume"] = resume;
        }
        return o.ToJsonString();
    }

    private static JsonNode Projects(params string[] dirs) =>
        JsonNode.Parse("{\"projects\":[" + string.Join(",", dirs) + "]}")!;

    [Fact]
    public void Only_directories_with_a_conversation_are_listed_most_recently_updated_first()
    {
        var rows = SessionLaunchDialog.ParseRows(
            Projects(
                Dir("older", "s-old", modified: 1_000),
                // Registered, or every session empty or running: nothing to continue.
                Dir("nothing-to-continue", null),
                // The conversation is intact but the directory is gone.
                Dir("moved-away", "s-gone", modified: 9_000, exists: false),
                Dir("newer", "s-new", modified: 2_000)),
            accountNumber: 1,
            accountIsDefault: true);

        Assert.Equal(new[] { "newer", "older" }, rows.Select(r => r.Name));
        Assert.Equal("s-new", rows[0].SessionId);
    }

    [Fact]
    public void A_conversation_continues_only_under_the_login_that_wrote_it()
    {
        var node = Projects(
            Dir("default-login", "s-d", modified: 2),
            Dir("in-profile-2", "s-p", modified: 1, profile: 2));

        // The default login's terminal reads the default config directory.
        var asDefault = SessionLaunchDialog.ParseRows(node, accountNumber: 1, accountIsDefault: true);
        Assert.True(asDefault.Single(r => r.Name == "default-login").ResumableHere);
        Assert.False(asDefault.Single(r => r.Name == "in-profile-2").ResumableHere);

        // Account 2's session-mode terminal reads only its own profile.
        var asAccount2 = SessionLaunchDialog.ParseRows(node, accountNumber: 2, accountIsDefault: false);
        Assert.False(asAccount2.Single(r => r.Name == "default-login").ResumableHere);
        Assert.True(asAccount2.Single(r => r.Name == "in-profile-2").ResumableHere);
    }
}
