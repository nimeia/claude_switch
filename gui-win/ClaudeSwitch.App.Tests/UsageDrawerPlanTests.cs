using System.Drawing;
using System.Windows.Forms;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// Detail-drawer subscription section: present when the snapshot carried plan
/// data, absent otherwise, and laid out without colliding with the rules link.
/// </summary>
public class UsageDrawerPlanTests
{
    private static AccountCardModel ProAccount() =>
        new()
        {
            Number = 1,
            Email = "alice@example.com",
            Active = true,
            FiveHour = 25,
            SevenDay = 10,
            PlanLabel = "Pro",
            PlanTier = "pro",
            PlanPersonal = true,
            BillingType = "google_play_subscription",
            SubscriptionCreatedAt = "2026-05-24T05:45:13+00:00",
            AccountCreatedAt = "2025-07-29T10:45:21+00:00",
            OrganizationName = "alice@example.com's Organization",
            OrganizationRole = "admin",
            RateLimitTier = "default_claude_ai",
            ProfileFetchedAt = 1_785_899_866_810,
        };

    private static UsageDrawer MakeDrawer()
    {
        var d = new UsageDrawer { Size = new Size(UsageDrawer.DrawerWidth, 700) };
        d.Relayout();
        return d;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var g in Descendants(c))
                yield return g;
        }
    }

    private static Label? LabelWithText(Control root, string text) =>
        Descendants(root).OfType<Label>().FirstOrDefault(l => l.Text == text);

    [Fact]
    public void Binds_subscription_rows_from_plan_data()
    {
        using var drawer = MakeDrawer();
        drawer.Bind(ProAccount());

        Assert.NotNull(LabelWithText(drawer, "订阅"));
        Assert.NotNull(LabelWithText(drawer, "订阅开始"));
        Assert.NotNull(LabelWithText(drawer, "2026-05-24"));
        Assert.NotNull(LabelWithText(drawer, "付费方式"));
        Assert.NotNull(LabelWithText(drawer, "Google Play 订阅"));
        Assert.NotNull(LabelWithText(drawer, "额外用量"));
        Assert.NotNull(LabelWithText(drawer, "未开启"));

        // No renewal date is ever rendered — none is obtainable.
        Assert.DoesNotContain(
            Descendants(drawer).OfType<Label>(),
            l => l.Text.Contains("续费", StringComparison.Ordinal));
    }

    [Fact]
    public void Hides_the_personal_organization_name()
    {
        using var drawer = MakeDrawer();
        drawer.Bind(ProAccount());
        // "<email>'s Organization" is synthesized noise on personal plans.
        Assert.Null(LabelWithText(drawer, "组织"));
    }

    [Fact]
    public void Shows_the_organization_for_a_team_plan()
    {
        using var drawer = MakeDrawer();
        drawer.Bind(new AccountCardModel
        {
            Number = 1,
            Email = "dev@acme.com",
            PlanLabel = "Team",
            PlanTier = "team",
            PlanPersonal = false,
            OrganizationName = "Acme",
            OrganizationRole = "admin",
            SeatTier = "standard",
            SubscriptionCreatedAt = "2026-05-24T05:45:13+00:00",
        });
        Assert.NotNull(LabelWithText(drawer, "组织"));
        Assert.NotNull(LabelWithText(drawer, "Acme（admin）"));
        Assert.NotNull(LabelWithText(drawer, "席位"));
        Assert.NotNull(LabelWithText(drawer, "standard"));
    }

    [Fact]
    public void Section_is_absent_for_slots_without_plan_data()
    {
        using var drawer = MakeDrawer();
        drawer.Bind(new AccountCardModel { Number = 1, Email = "old@example.com" });

        var title = LabelWithText(drawer, "订阅");
        Assert.True(title is null || !title.Visible);
        Assert.Null(LabelWithText(drawer, "订阅开始"));
    }

    [Fact]
    public void Section_adds_height_and_stays_above_the_rules_link()
    {
        using var bare = MakeDrawer();
        bare.Bind(new AccountCardModel { Number = 1, Email = "old@example.com" });
        int bareHeight = bare.ContentHeight;

        using var full = MakeDrawer();
        full.Bind(ProAccount());
        Assert.True(
            full.ContentHeight > bareHeight,
            $"plan section should grow content: {full.ContentHeight} vs {bareHeight}");

        var start = LabelWithText(full, "订阅开始");
        var rules = Descendants(full)
            .OfType<Label>()
            .First(l => l.Text.Contains("自动切换规则", StringComparison.Ordinal));
        Assert.NotNull(start);
        Assert.True(
            start!.Bottom <= rules.Top,
            $"subscription rows must sit above the rules link: {start.Bottom} vs {rules.Top}");
    }

    [Fact]
    public void Rebinding_a_bare_account_clears_the_previous_rows()
    {
        using var drawer = MakeDrawer();
        drawer.Bind(ProAccount());
        Assert.NotNull(LabelWithText(drawer, "订阅开始"));

        drawer.Bind(new AccountCardModel { Number = 2, Email = "old@example.com" });
        var start = LabelWithText(drawer, "订阅开始");
        Assert.True(start is null || !start.Visible);
    }
}
