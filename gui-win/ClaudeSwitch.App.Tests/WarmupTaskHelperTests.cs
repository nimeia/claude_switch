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
