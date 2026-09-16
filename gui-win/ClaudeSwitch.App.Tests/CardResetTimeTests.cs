using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The reset line under each card meter. Absolute times, so the card does not
/// have to repaint to stay honest.
/// </summary>
public class CardResetTimeTests
{
    private static IDisposable Chinese() => Loc.Scoped("zh-Hans");

    /// <summary>
    /// A local wall-clock moment as an ISO string, built in the machine's own
    /// zone so the assertions hold wherever the suite runs.
    /// </summary>
    private static (string Iso, DateTimeOffset At) Local(double hoursFromMidnight, int dayOffset = 0)
    {
        var naive = DateTime.Today.AddDays(dayOffset).AddHours(hoursFromMidnight);
        var at = new DateTimeOffset(naive, TimeZoneInfo.Local.GetUtcOffset(naive));
        return (at.ToString("o"), at);
    }

    [Fact]
    public void Today_shows_the_clock_alone()
    {
        using var lang = Chinese();
        var reset = Local(11.25);
        var now = Local(9).At;
        Assert.Equal("11:15", Theme.FormatResetsAt(reset.Iso, now));
    }

    [Fact]
    public void Tomorrow_is_named()
    {
        using var lang = Chinese();
        var reset = Local(6, dayOffset: 1);
        var now = Local(9).At;
        Assert.Equal("明天 6:00", Theme.FormatResetsAt(reset.Iso, now));
    }

    [Fact]
    public void Further_out_carries_the_date()
    {
        using var lang = Chinese();
        var reset = Local(14, dayOffset: 4);
        var now = Local(9).At;
        var day = DateTime.Today.AddDays(4);
        Assert.Equal($"{day.Month}/{day.Day} 14:00", Theme.FormatResetsAt(reset.Iso, now));
    }

    [Fact]
    public void A_reset_already_due_says_so_instead_of_printing_a_past_time()
    {
        using var lang = Chinese();
        var reset = Local(8);
        var now = Local(9).At;
        Assert.Equal(Loc.T("resets.soon"), Theme.FormatResetsAt(reset.Iso, now));
    }

    [Fact]
    public void Unknown_resets_draw_nothing()
    {
        using var lang = Chinese();
        var now = Local(9).At;
        Assert.Null(Theme.FormatResetsAt(null, now));
        Assert.Null(Theme.FormatResetsAt("", now));
        Assert.Null(Theme.FormatResetsAt("not a timestamp", now));
    }

    [Fact]
    public void The_card_line_labels_the_time_as_a_reset()
    {
        using var lang = Chinese();
        Assert.Equal("重置 11:15", Loc.T("card.reset.at", "11:15"));
    }
}
