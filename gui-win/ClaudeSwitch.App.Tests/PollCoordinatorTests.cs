using ClaudeSwitch.App;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// Drives the real PollCoordinator + Engine (claude_switch.dll) fixture path —
/// same call surface the GUI timer uses.
/// </summary>
public class PollCoordinatorTests
{
    private static string SeedFixtureDir()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "cswitch-poll-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".claude"));
        using var eng = new Engine(root);
        string Cred(string email, string token) =>
            "{\"claudeAiOauth\":{\"accessToken\":\"" + token + "\",\"refreshToken\":\"r\",\"emailAddress\":\"" + email + "\"}}";
        string Cfg(string email) =>
            "{\"oauthAccount\":{\"emailAddress\":\"" + email + "\",\"accountUuid\":\"u\",\"organizationUuid\":\"o\",\"organizationName\":\"O\",\"displayName\":\"" + email + "\"}}";
        eng.Call("add_raw", new
        {
            number = 1,
            email = "alice@example.com",
            credentials = Cred("alice@example.com", "tok-a"),
            config = Cfg("alice@example.com"),
        });
        eng.Call("add_raw", new
        {
            number = 2,
            email = "bob@example.com",
            credentials = Cred("bob@example.com", "tok-b"),
            config = Cfg("bob@example.com"),
        });
        try { eng.Call("switch_to", new { id = "1" }); } catch { /* first boot */ }
        return root;
    }

    [Fact]
    public void RunTick_refresh_usage_increments_and_returns_next_poll()
    {
        var root = SeedFixtureDir();
        try
        {
            using var eng = new Engine(root);
            var poll = new PollCoordinator();
            Assert.Equal(0, poll.TickCount);

            var r = poll.RunTick(eng, autoswitchEnabled: false);
            Assert.Equal(1, poll.TickCount);
            Assert.False(poll.LastUsedAutoswitchTick);
            Assert.True(poll.LastNextPollSeconds > 0);
            // Engine fixture mock seeds tok-a at 25% → adaptive 180s band typically.
            Assert.True(poll.LastNextPollSeconds >= 30, $"next={poll.LastNextPollSeconds}");
            Assert.Contains("nextPollSeconds", r.ToJsonString(), StringComparison.OrdinalIgnoreCase);

            poll.RunTick(eng, autoswitchEnabled: false);
            Assert.Equal(2, poll.TickCount);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void RunTick_autoswitch_enabled_calls_autoswitch_tick()
    {
        var root = SeedFixtureDir();
        try
        {
            using var eng = new Engine(root);
            eng.Call("set_autoswitch", new
            {
                threshold = 90.0,
                intervalSeconds = 60.0,
                cooldownSeconds = 300.0,
                hysteresisPct = 10.0,
                strategy = "best",
                includeApiKeyAccounts = false,
                unhealthyTicks = 3,
                enabled = true,
            });

            var poll = new PollCoordinator();
            poll.RunTick(eng, autoswitchEnabled: true);
            Assert.Equal(1, poll.TickCount);
            Assert.True(poll.LastUsedAutoswitchTick);
            Assert.True(poll.LastNextPollSeconds > 0);
            // Decision payload (switched / detail) — not a fake stub.
            Assert.False(string.IsNullOrWhiteSpace(poll.LastResponseJson));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void IntervalMsFromSeconds_clamps()
    {
        Assert.Equal(5_000, PollCoordinator.IntervalMsFromSeconds(1));
        Assert.Equal(300_000, PollCoordinator.IntervalMsFromSeconds(9999));
        Assert.Equal(60_000, PollCoordinator.IntervalMsFromSeconds(60));
    }

    [Fact]
    public void UsageLabel_means_used_share()
    {
        // Asserts wording, so it must name the language it asserts — the
        // active one is process-wide and another test may have changed it.
        using var lang = Loc.Scoped("zh-Hans");
        Assert.Equal("暂无", Theme.UsageLabel(null));
        Assert.Equal("已用 25%", Theme.UsageLabel(25));
        Assert.Equal("已用 80%", Theme.UsageLabel(80));
        Assert.Contains("已用", UsageDrawer.UsageLegendText);
        Assert.Contains("临界", UsageDrawer.UsageLegendText);
    }

    [Fact]
    public void UsageLevel_three_bands()
    {
        // Asserts wording, so it must name the language it asserts — the
        // active one is process-wide and another test may have changed it.
        using var lang = Loc.Scoped("zh-Hans");
        Assert.Equal("未知", Theme.UsageLevel(null));
        Assert.Equal("充足", Theme.UsageLevel(25));
        Assert.Equal("注意", Theme.UsageLevel(75));
        Assert.Equal("临界", Theme.UsageLevel(92));
        Assert.Equal("健康", Theme.UsageHealthTag(25, 10));
        Assert.Equal("注意", Theme.UsageHealthTag(75, 10));
        Assert.Equal("临界", Theme.UsageHealthTag(50, 95));
    }

    [Fact]
    public void FormatResetsIn_parses_iso()
    {
        // Asserts wording, so it must name the language it asserts — the
        // active one is process-wide and another test may have changed it.
        using var lang = Loc.Scoped("zh-Hans");
        var future = DateTimeOffset.UtcNow.AddHours(2).AddMinutes(18)
            .ToString("o");
        var s = Theme.FormatResetsIn(future);
        Assert.NotNull(s);
        Assert.Contains("小时", s);
        Assert.Null(Theme.FormatResetsIn(null));
        Assert.Null(Theme.FormatResetsIn("not-a-date"));
    }
}
