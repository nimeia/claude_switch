using System.Text.Json.Nodes;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The autoswitch balloon. It fires once, unprompted, while the user is working
/// in another window — so it has to say where they landed, why, and whether they
/// need to do anything, in the space of a glance.
/// </summary>
public class SwitchNoticeTests
{
    private static JsonNode Payload(bool switched, int toNumber = 2) =>
        JsonNode.Parse(
            switched
                ? "{\"switched\":true,\"from\":{\"number\":1,\"email\":\"a@x.com\"},"
                  + "\"to\":{\"number\":" + toNumber + ",\"email\":\"b@x.com\"}}"
                : "{\"switched\":false,\"detail\":\"below threshold\"}")!;

    [Fact]
    public void A_tick_that_changed_nothing_raises_no_notice()
    {
        // The poll runs every few minutes; a balloon per tick would be spam.
        Assert.False(SwitchNotice.DidSwitch(Payload(switched: false)));
        Assert.False(SwitchNotice.DidSwitch(null));
        Assert.False(SwitchNotice.DidSwitch(JsonNode.Parse("{}")));
    }

    [Fact]
    public void A_real_switch_names_its_target()
    {
        var payload = Payload(switched: true);
        Assert.True(SwitchNotice.DidSwitch(payload));
        Assert.Equal(2, SwitchNotice.TargetNumber(payload));
    }

    [Fact]
    public void The_body_says_where_why_and_that_work_continues()
    {
        // Asserts wording, so it must name the language it asserts — the
        // active one is process-wide and another test may have changed it.
        using var lang = Loc.Scoped("zh-Hans");
        var body = SwitchNotice.Body("work", "personal", 92.4);
        Assert.Contains("work", body);
        Assert.Contains("personal", body);
        Assert.Contains("92.4%", body);
        // The claim that makes the feature worth having.
        Assert.Contains("无需重启", body);
    }

    [Fact]
    public void An_unknown_trigger_is_omitted_rather_than_guessed()
    {
        // Failover fires when the old account's usage could not be read at all;
        // inventing a percentage there would be a lie.
        var body = SwitchNotice.Body("work", "personal", null);
        Assert.Contains("work", body);
        Assert.DoesNotContain("用量已达", body);
        Assert.Contains(SwitchNotice.NoRestartLine, body);

        var noSource = SwitchNotice.Body("work", null, 92.4);
        Assert.DoesNotContain("用量已达", noSource);
    }

    [Fact]
    public void The_body_stays_short_enough_for_a_balloon()
    {
        // Asserts wording, so it must name the language it asserts — the
        // active one is process-wide and another test may have changed it.
        using var lang = Loc.Scoped("zh-Hans");
        // Windows truncates balloon text; two lines is the budget.
        var body = SwitchNotice.Body("someone@example.com", "another@example.com", 100);
        Assert.Equal(2, body.Split('\n').Length);
        Assert.True(body.Length < 120, $"len={body.Length}: {body}");
    }
}
