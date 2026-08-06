using System.Windows.Forms;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The search hint must never be part of the box's value.
///
/// It used to be: the hint was written into <c>Text</c> on blur and a flag said
/// "ignore this". Because assigning <c>Text</c> raises <c>TextChanged</c>
/// synchronously — before the flag was updated — a focus-then-blur with nothing
/// typed filtered the list by the hint itself and showed "no matches".
/// </summary>
public class SearchBoxTests
{
    private const string Hint = "搜索邮箱或别名…";

    private static List<AccountCardModel> Accounts() =>
    [
        new() { Number = 1, Email = "alice@example.com" },
        new() { Number = 2, Email = "bob@example.com" },
    ];

    [Fact]
    public void Cue_banner_never_becomes_the_value()
    {
        using var box = new TextBox();
        SearchBox.AttachCueBanner(box, Hint);
        Assert.Equal("", box.Text);

        // Handle creation is when the banner is pushed to the native control;
        // it must still not show up as text.
        _ = box.Handle;
        Assert.Equal("", box.Text);

        // Focus changes are exactly the moments the old design corrupted.
        box.Focus();
        Assert.Equal("", box.Text);
    }

    [Fact]
    public void An_empty_box_means_no_filter()
    {
        var all = Accounts();
        Assert.Equal(all.Count, AccountListFilter.Filter(all, "").Count);
        Assert.Null(AccountListFilter.FormatFilterMessage(all.Count, all.Count, ""));
    }

    [Fact]
    public void The_hint_as_a_query_would_match_nothing()
    {
        // Documents the consequence the design now makes unreachable: if the
        // hint ever reached the filter, every account would disappear.
        Assert.Empty(AccountListFilter.Filter(Accounts(), Hint));
    }

    [Fact]
    public void Attaching_to_a_missing_box_is_a_no_op()
    {
        // ToolStripTextBox.TextBox is nullable in principle; must not throw.
        SearchBox.AttachCueBanner(null, Hint);
    }
}
