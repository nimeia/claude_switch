using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// Card sub-line and plan-badge rules. Dates are shown as facts only — the card
/// never renders a renewal date, because none is obtainable from the stored data.
/// </summary>
public class AccountPlanDisplayTests
{
    // These assert wording, so they must name the language they are asserting.
    // Without this the result depends on whichever test last called Loc.Use.
    private static IDisposable Chinese() => Loc.Scoped("zh-Hans");

    private static AccountCardModel ProAccount() =>
        new()
        {
            Number = 1,
            Email = "alice@example.com",
            PlanLabel = "Pro",
            PlanTier = "pro",
            PlanPersonal = true,
            BillingType = "google_play_subscription",
            // Fixed offset so the local-time conversion is deterministic.
            SubscriptionCreatedAt = "2026-05-24T05:45:13.496010+00:00",
        };

    [Fact]
    public void Sub_line_appends_subscription_start_when_it_fits()
    {
        using var lang = Chinese();
        var line = AccountCard.BuildSubLine(ProAccount(), int.MaxValue);
        Assert.StartsWith("#1 · alice@example.com", line);
        Assert.Contains("订阅开始", line);
        // Whatever the local zone, the date is one of the two adjacent days.
        Assert.True(line.Contains("5/24") || line.Contains("5/23") || line.Contains("5/25"), line);
    }

    [Fact]
    public void Sub_line_carries_the_slot_number_not_the_card_position()
    {
        // The badge used to be a big numbered disc, which read as a rank — and
        // the list order is the user's own, so #2 could sit above #1. The number
        // is an identifier, so it belongs in the identity line beside the email.
        using var lang = Chinese();
        var line = AccountCard.BuildSubLine(
            new AccountCardModel { Number = 7, Email = "bob@example.com" }, int.MaxValue);
        Assert.StartsWith("#7 · ", line);
    }

    [Fact]
    public void Sub_line_drops_the_date_rather_than_truncating_the_email()
    {
        using var lang = Chinese();
        var line = AccountCard.BuildSubLine(ProAccount(), 10);
        Assert.Equal("#1 · alice@example.com", line);
        Assert.DoesNotContain("订阅开始", line);
    }

    [Fact]
    public void Sub_line_is_just_the_email_without_plan_data()
    {
        var line = AccountCard.BuildSubLine(
            new AccountCardModel { Number = 2, Email = "bob@example.com" },
            int.MaxValue);
        Assert.Equal("#2 · bob@example.com", line);
    }

    [Fact]
    public void Unparseable_subscription_date_is_dropped_not_shown_raw()
    {
        var line = AccountCard.BuildSubLine(
            new AccountCardModel { Number = 3, Email = "bob@example.com", SubscriptionCreatedAt = "soon" },
            int.MaxValue);
        Assert.Equal("#3 · bob@example.com", line);
    }

    [Fact]
    public void Badge_shows_only_with_a_label()
    {
        Assert.True(ProAccount().HasPlanBadge);
        Assert.False(new AccountCardModel { PlanLabel = "" }.HasPlanBadge);
        Assert.False(new AccountCardModel().HasPlanBadge);
    }

    [Fact]
    public void Detail_section_appears_for_dates_even_with_an_unnamed_tier()
    {
        // Tier unresolved but the dates are still facts worth a section.
        var m = new AccountCardModel { SubscriptionCreatedAt = "2026-05-24T05:45:13Z" };
        Assert.False(m.HasPlanBadge);
        Assert.True(m.HasPlanInfo);
        // Pre-plan-fields slot: no section at all.
        Assert.False(new AccountCardModel { Email = "a@b.c" }.HasPlanInfo);
    }

    [Fact]
    public void Billing_type_is_localized_and_unknown_values_pass_through()
    {
        // Asserts wording, so it must name the language it asserts — the
        // active one is process-wide and another test may have changed it.
        using var lang = Loc.Scoped("zh-Hans");
        Assert.Equal("Google Play 订阅", Theme.BillingTypeLabel("google_play_subscription"));
        Assert.Equal("Stripe 订阅", Theme.BillingTypeLabel("stripe_subscription"));
        Assert.Equal("some_new_channel", Theme.BillingTypeLabel("some_new_channel"));
        Assert.Equal("未知", Theme.BillingTypeLabel(null));
    }

    [Theory]
    [InlineData("needs-login", "需重新登录")]
    [InlineData("no-credential", "无凭据")]
    [InlineData("no-subscription", "无订阅额度")]
    [InlineData("api-key", "API Key")]
    [InlineData("unavailable", "获取失败")]
    [InlineData("unknown", "暂无")]
    [InlineData(null, "暂无")]
    public void Missing_usage_says_why_on_the_card(string? status, string expected)
    {
        using var lang = Chinese();
        // A blank cell can't be told apart from a broken app — always give a reason.
        Assert.Equal(expected, Theme.UsageLabelRemain(null, status));
        Assert.Equal(expected, Theme.UsageStatusShort(status));
    }

    [Theory]
    [InlineData("needs-login", true)]
    [InlineData("no-credential", true)]
    [InlineData("no-subscription", true)]
    [InlineData("api-key", false)]
    [InlineData("unavailable", false)]
    [InlineData("ok", false)]
    [InlineData(null, false)]
    public void Attention_is_only_for_faults_the_user_can_clear(string? status, bool expected)
    {
        // "unavailable" retries itself and "api-key" is a permanent property of
        // the account — flagging either would train the user to ignore amber.
        Assert.Equal(expected, new AccountCardModel { UsageStatus = status }.NeedsAttention);
    }

    [Fact]
    public void Attention_outranks_every_other_state_on_the_badge()
    {
        using var lang = Chinese();
        // A broken account that also happens to be the live login must read as
        // broken. Showing "in use" would be true and useless — the user cannot
        // act on it, and the thing they can act on would be the one hidden.
        var broken = new AccountCardModel { UsageStatus = "needs-login", Active = true };
        Assert.Equal("需重新登录", AccountCard.StateBadge(broken).Text);
        Assert.Equal(Theme.Warning, AccountCard.StateBadge(broken).Fg);

        Assert.Equal("当前", AccountCard.StateBadge(new AccountCardModel { Active = true }).Text);
        Assert.Equal(
            "已停用",
            AccountCard.StateBadge(new AccountCardModel { Active = true, Disabled = true }).Text);
        Assert.Equal("就绪", AccountCard.StateBadge(new AccountCardModel()).Text);
    }

    [Fact]
    public void Numbers_not_in_yet_are_told_apart_from_numbers_that_will_never_come()
    {
        Assert.True(new AccountCardModel().UsageUnknownYet);
        Assert.True(new AccountCardModel { UsageStatus = "ok" }.UsageUnknownYet);
        // A stated reason is an answer, so the cell shows it rather than a
        // skeleton that would imply something is still on its way.
        Assert.False(new AccountCardModel { UsageStatus = "needs-login" }.UsageUnknownYet);
        Assert.False(new AccountCardModel { FiveHour = 0 }.UsageUnknownYet);
    }

    [Fact]
    public void Actionable_statuses_tell_the_user_what_to_do()
    {
        using var lang = Chinese();
        Assert.Contains("重新登录", Theme.UsageStatusLong("needs-login"));
        Assert.Contains("重新添加", Theme.UsageStatusLong("no-credential"));
        Assert.Contains("没有订阅额度", Theme.UsageStatusLong("api-key"));
        Assert.Contains("自动重试", Theme.UsageStatusLong("unavailable"));
        // Expired vs never-subscribed is indistinguishable from the API, so the
        // wording must cover both rather than assert one.
        var noSub = Theme.UsageStatusLong("no-subscription");
        Assert.Contains("已到期", noSub);
        Assert.Contains("从未订阅", noSub);
    }

    [Fact]
    public void Real_numbers_ignore_the_status_entirely()
    {
        using var lang = Chinese();
        // Remaining only: the bar beside it already draws the spent share.
        Assert.Equal("剩 58%", Theme.UsageLabelRemain(42, "ok"));
        // Even a stale status string must never mask a number we actually have.
        Assert.Equal("剩 58%", Theme.UsageLabelRemain(42, "needs-login"));
        Assert.Equal("剩 0%", Theme.UsageLabelRemain(100));
    }

    [Fact]
    public void Epoch_and_iso_formatting_reject_junk()
    {
        Assert.Null(Theme.FormatDate(null));
        Assert.Null(Theme.FormatDate("not a date"));
        Assert.Null(Theme.FormatShortDate(""));
        Assert.Null(Theme.FormatEpochMs(null));
        Assert.Null(Theme.FormatEpochMs(0));
        Assert.NotNull(Theme.FormatEpochMs(1_785_899_866_810));
        Assert.Equal("2026-05-24", Theme.FormatDate("2026-05-24T12:00:00+00:00"));
    }
}
