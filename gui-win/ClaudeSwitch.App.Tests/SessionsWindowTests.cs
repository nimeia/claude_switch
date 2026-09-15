using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>What the conversations window's search box finds.</summary>
public class SessionsWindowTests
{
    private static readonly RecentSession Row = new(
        @"D:\dev\claude_switch",
        "claude_switch",
        "3f2a9c1e-0000-4000-8000-000000000000",
        "深模式下拉菜单文字对比度",
        1_785_000_000_000,
        ProfileNumber: 2);

    private const string Account = "#2 liutong.pub";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("对比度")]
    [InlineData("CLAUDE_SWITCH")] // the directory, in any case
    [InlineData(@"dev\claude")] // part of the path
    [InlineData("liutong")] // the account column
    [InlineData("3f2a9c")] // a session id, as pasted from a terminal
    [InlineData("  对比度  ")] // stray spaces around a query
    public void A_query_finds_the_row_by_anything_it_shows(string query) =>
        Assert.True(SessionsWindow.Matches(Row, Account, query));

    [Fact]
    public void A_query_that_appears_nowhere_hides_the_row() =>
        Assert.False(SessionsWindow.Matches(Row, Account, "payments"));

    [Fact]
    public void A_row_without_a_title_is_still_found_by_its_directory()
    {
        var untitled = Row with { Title = null };
        Assert.True(SessionsWindow.Matches(untitled, Account, "claude_switch"));
        Assert.False(SessionsWindow.Matches(untitled, Account, "对比度"));
    }
}
