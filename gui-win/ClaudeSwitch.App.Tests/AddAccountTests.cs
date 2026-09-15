using System.Text.Json.Nodes;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// "Add account" captures whoever Claude Code is signed in as.
/// </summary>
/// <remarks>
/// It used to take a new slot every time, so adding twice — or "sign in again"
/// on a card, which adds — listed one account twice. These go through the same
/// engine call the button makes, so the fields the window reads are covered too.
/// </remarks>
public class AddAccountTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cswitch-add-" + Guid.NewGuid().ToString("N"));

    private readonly Engine _engine;

    public AddAccountTests()
    {
        Directory.CreateDirectory(_root);
        _engine = new Engine(_root);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Sign Claude Code in as <paramref name="email"/>, the way it leaves the files.</summary>
    /// <remarks>
    /// The isolated engine runs with <c>CLAUDE_CONFIG_DIR</c> at <c>.claude</c>, so
    /// the global config lives inside it too. Written beside it instead, the login
    /// has no identity and every add looks like a stranger.
    /// </remarks>
    private void LoginAs(string email, string token)
    {
        var configHome = Path.Combine(_root, ".claude");
        Directory.CreateDirectory(configHome);
        File.WriteAllText(
            Path.Combine(configHome, ".credentials.json"),
            new JsonObject
            {
                ["claudeAiOauth"] = new JsonObject
                {
                    ["accessToken"] = token,
                    ["refreshToken"] = "refresh-" + token,
                    ["expiresAt"] = 4_102_444_800_000,
                    ["subscriptionType"] = "pro",
                },
            }.ToJsonString());
        File.WriteAllText(
            Path.Combine(configHome, ".claude.json"),
            new JsonObject
            {
                ["oauthAccount"] = new JsonObject
                {
                    ["emailAddress"] = email,
                    ["accountUuid"] = "uuid-" + email,
                    ["organizationUuid"] = "org-" + email,
                    ["organizationName"] = "Org",
                    ["displayName"] = email,
                },
            }.ToJsonString());
    }

    private int AccountCount() => _engine.Snapshot()["accounts"]?.AsArray().Count ?? 0;

    [Fact]
    public void Adding_a_login_that_is_already_listed_does_not_list_it_twice()
    {
        LoginAs("a@x.com", "tok-a");
        var first = _engine.Call("add_current", new { });
        Assert.Equal(1, first["number"]?.GetValue<int>());
        Assert.False(first["existing"]?.GetValue<bool>());

        // Signed in again — a fresh token for the same account.
        LoginAs("a@x.com", "tok-a-fresh");
        var again = _engine.Call("add_current", new { });

        Assert.Equal(1, again["number"]?.GetValue<int>());
        Assert.True(again["existing"]?.GetValue<bool>());
        // The window names the account in its message.
        Assert.Equal("a@x.com", again["email"]?.GetValue<string>());
        Assert.Equal(1, AccountCount());
    }

    [Fact]
    public void A_different_login_still_gets_its_own_slot()
    {
        LoginAs("a@x.com", "tok-a");
        _engine.Call("add_current", new { });
        LoginAs("b@x.com", "tok-b");
        var second = _engine.Call("add_current", new { });

        Assert.Equal(2, second["number"]?.GetValue<int>());
        Assert.False(second["existing"]?.GetValue<bool>());
        Assert.Equal(2, AccountCount());
    }
}
