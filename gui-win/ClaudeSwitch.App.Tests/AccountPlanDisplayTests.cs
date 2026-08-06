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
        Assert.StartsWith("alice@example.com", line);
        Assert.Contains("订阅开始", line);
        // Whatever the local zone, the date is one of the two adjacent days.
        Assert.True(line.Contains("5/24") || line.Contains("5/23") || line.Contains("5/25"), line);
    }

    [Fact]
    public void Sub_line_drops_the_date_rather_than_truncating_the_email()
    {
        using var lang = Chinese();
        var line = AccountCard.BuildSubLine(ProAccount(), 10);
        Assert.Equal("alice@example.com", line);
        Assert.DoesNotContain("订阅开始", line);
    }

    [Fact]
    public void Sub_line_is_just_the_email_without_plan_data()
    {
        var line = AccountCard.BuildSubLine(
            new AccountCardModel { Number = 2, Email = "bob@example.com" },
            int.MaxValue);
        Assert.Equal("bob@example.com", line);
    }

    [Fact]
    public void Unparseable_subscription_date_is_dropped_not_shown_raw()
    {
        var line = AccountCard.BuildSubLine(
            new AccountCardModel { Email = "bob@example.com", SubscriptionCreatedAt = "soon" },
            int.MaxValue);
        Assert.Equal("bob@example.com", line);
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
        Assert.Equal(expected, Theme.UsageLabelCompact(null, status));
        Assert.Equal(expected, Theme.UsageStatusShort(status));
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
        Assert.Equal("42% · 剩58%", Theme.UsageLabelCompact(42, "ok"));
        // Even a stale status string must never mask a number we actually have.
        Assert.Equal("42% · 剩58%", Theme.UsageLabelCompact(42, "needs-login"));
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
