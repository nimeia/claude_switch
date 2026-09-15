using Xunit;

namespace ClaudeSwitch.App.Tests;

public class WarmupTaskHelperTests
{
    [Fact]
    public void BaseAnchor_nine_to_eighteen_is_six()
    {
        var (h, m) = WarmupTaskHelper.BaseAnchor(9, 18);
        Assert.Equal(6, h);
        Assert.Equal(0, m);
    }

    [Fact]
    public void BaseAnchor_nine_to_nineteen_is_six_thirty()
    {
        var (h, m) = WarmupTaskHelper.BaseAnchor(9, 19);
        Assert.Equal(6, h);
        Assert.Equal(30, m);
    }

    [Fact]
    public void ResetTimes_nine_to_eighteen_are_eleven_and_sixteen()
    {
        Assert.Equal(new[] { "11:00", "16:00" }, WarmupTaskHelper.ResetTimes(9, 18));
    }

    [Fact]
    public void ResetTimes_follow_a_half_hour_anchor()
    {
        Assert.Equal(new[] { "11:30", "16:30" }, WarmupTaskHelper.ResetTimes(9, 19));
    }

    [Fact]
    public void ResetTimes_leave_out_a_reset_on_work_end()
    {
        // 9–14: anchor 4:00, resets 9:00 and 14:00 — the second begins nothing.
        Assert.Equal(new[] { "9:00" }, WarmupTaskHelper.ResetTimes(9, 14));
    }

    [Fact]
    public void PlanText_names_the_start_and_every_reset()
    {
        // Language-neutral on purpose: the active language is process-wide and
        // test classes run in parallel, so switching it here races other tests.
        string plan = WarmupTaskHelper.PlanText(9, 18);
        Assert.Contains("6:00", plan, StringComparison.Ordinal);
        Assert.Contains("11:00", plan, StringComparison.Ordinal);
        Assert.Contains("16:00", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatWarmupAnchor_now_within_two_minutes()
    {
        string iso = DateTimeOffset.Now.ToString("o");
        Assert.Equal(Loc.T("warmup.when.now"), Theme.FormatWarmupAnchor(iso));
    }

    [Fact]
    public void FormatWarmupAnchor_tomorrow()
    {
        var when = DateTimeOffset.Now.Date.AddDays(1).AddHours(6);
        // Local midnight + 6h may need offset: use local kind.
        var local = new DateTimeOffset(when, TimeZoneInfo.Local.GetUtcOffset(when));
        string? label = Theme.FormatWarmupAnchor(local.ToString("o"));
        Assert.NotNull(label);
        Assert.Contains("6:00", label, StringComparison.Ordinal);
    }
}
