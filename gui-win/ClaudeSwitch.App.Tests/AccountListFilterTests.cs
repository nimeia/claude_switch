using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

public class AccountListFilterTests
{
    private static List<AccountCardModel> DemoAccounts() =>
    [
        new() { Number = 1, Email = "alice@example.com", Alias = null },
        new() { Number = 2, Email = "bob@example.com", Alias = null },
        new() { Number = 3, Email = "team@example.com", Alias = "work" },
        new() { Number = 4, Email = "carol@example.com", Alias = "side" },
    ];

    [Fact]
    public void Empty_query_returns_all()
    {
        var all = DemoAccounts();
        var got = AccountListFilter.Filter(all, "");
        Assert.Equal(all.Count, got.Count);
        Assert.Null(AccountListFilter.FormatFilterMessage(got.Count, all.Count, "  "));
    }

    [Fact]
    public void Filters_by_email_substring()
    {
        // Asserts wording, so it must name the language it asserts — the
        // active one is process-wide and another test may have changed it.
        using var lang = Loc.Scoped("zh-Hans");
        var all = DemoAccounts();
        var got = AccountListFilter.Filter(all, "alice");
        Assert.Single(got);
        Assert.Equal("alice@example.com", got[0].Email);
        var msg = AccountListFilter.FormatFilterMessage(got.Count, all.Count, "alice");
        Assert.NotNull(msg);
        Assert.Contains("匹配 1", msg);
    }

    [Fact]
    public void Filters_by_alias()
    {
        var all = DemoAccounts();
        var got = AccountListFilter.Filter(all, "work");
        Assert.Single(got);
        Assert.Equal("work", got[0].Alias);
    }

    [Fact]
    public void Zero_match_message_is_explicit()
    {
        // Asserts wording, so it must name the language it asserts — the
        // active one is process-wide and another test may have changed it.
        using var lang = Loc.Scoped("zh-Hans");
        var all = DemoAccounts();
        var got = AccountListFilter.Filter(all, "zzz-no-such");
        Assert.Empty(got);
        var msg = AccountListFilter.FormatFilterMessage(0, all.Count, "zzz-no-such");
        Assert.NotNull(msg);
        Assert.Contains("无匹配", msg);
        Assert.Contains("0 / 4", msg);
        Assert.Contains("清除", msg);
    }

    [Fact]
    public void Filters_by_slot_number()
    {
        var all = DemoAccounts();
        var got = AccountListFilter.Filter(all, "2");
        Assert.Single(got);
        Assert.Equal(2, got[0].Number);
    }
}
