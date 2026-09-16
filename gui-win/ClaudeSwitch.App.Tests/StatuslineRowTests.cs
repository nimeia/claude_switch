using System.Drawing;
using System.Windows.Forms;
using ClaudeSwitch.App;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The status-line settings row: the parts that can be wrong without anyone
/// noticing on screen.
/// </summary>
public class StatuslineRowTests
{
    [Fact]
    public void The_full_presets_second_line_is_folded_into_the_preview()
    {
        // The strip reserves one row per setting. A preview that draws a line
        // break pushes everything under it down by a line.
        const string twoLines =
            "#2 work · 5h 38% · Sonnet 5\n5h resets 14:00 · auto 90%";
        string folded = StatuslineHelper.OneLine(twoLines);

        Assert.DoesNotContain('\n', folded);
        Assert.DoesNotContain('\r', folded);
        Assert.Contains("5h resets 14:00", folded);
        Assert.Equal("", StatuslineHelper.OneLine(null));
        Assert.Equal("one line", StatuslineHelper.OneLine("one line"));
        Assert.DoesNotContain('\n', StatuslineHelper.OneLine("windows\r\nline"));
    }

    [Fact]
    public void An_engine_refusal_is_shown_in_its_own_words()
    {
        // "another status line is already configured: npx -y ccstatusline@latest"
        // is the whole point of the dialog; the code and the JSON around it are
        // not something a user can act on.
        var refusal = new EngineException(
            2,
            """{"code":"validation-failed","message":"another status line is already configured: npx -y ccstatusline@latest"}""");

        Assert.Equal(
            "another status line is already configured: npx -y ccstatusline@latest",
            StatuslineHelper.Explain(refusal));
    }

    [Fact]
    public void A_report_is_never_lost_to_an_unexpected_error_body()
    {
        Assert.Equal(
            new EngineException(16, "not json at all").Message,
            StatuslineHelper.Explain(new EngineException(16, "not json at all")));
        Assert.Equal(
            new EngineException(16, """{"code":"internal"}""").Message,
            StatuslineHelper.Explain(new EngineException(16, """{"code":"internal"}""")));
        Assert.Equal("plain", StatuslineHelper.Explain(new InvalidOperationException("plain")));
    }

    [Fact]
    public void The_renderer_is_looked_for_where_the_bundle_unpacks_it()
    {
        // Not beside the exe the user clicked: a single-file build runs from a
        // self-extract directory, and the binary travels there with it.
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "cs-statusline.exe"),
            StatuslineHelper.BinaryPath);
    }

    [Fact]
    public void The_preview_font_is_monospaced()
    {
        // The preview exists to show how wide the line ends up, and it draws a
        // bar out of block characters. A proportional fallback would lie about
        // both, quietly, on whichever machine lacks the font.
        int narrow = TextRenderer.MeasureText("iiii", Theme.FontMono, Size.Empty, TextFormatFlags.NoPadding).Width;
        int wide = TextRenderer.MeasureText("WWWW", Theme.FontMono, Size.Empty, TextFormatFlags.NoPadding).Width;
        Assert.Equal(wide, narrow);
    }

    [Fact]
    public void The_preset_choice_answers_by_key_and_only_reports_the_users_own_picks()
    {
        using var choice = new SegmentedChoice(
            ("lean", "Lean"), ("standard", "Standard"), ("full", "Full"));
        int raised = 0;
        choice.SelectionChanged += (_, _) => raised++;

        Assert.Equal("lean", choice.Selected);

        // Loading the installed state must not look like the user choosing.
        choice.Selected = "full";
        Assert.Equal("full", choice.Selected);
        Assert.Equal(0, raised);

        // An option that is not there leaves the choice alone.
        choice.Selected = "powerline";
        Assert.Equal("full", choice.Selected);

        // A language change re-labels without changing what is selected.
        choice.SetText("full", "完整");
        Assert.Equal("full", choice.Selected);
        Assert.Equal("完整", choice.AccessibleDescription);
        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData("2.1.223 (Claude Code)", 2, 1, 223)]
    [InlineData("1.0.44", 1, 0, 44)]
    [InlineData("  2.1.97\n", 2, 1, 97)]
    public void The_claude_code_version_is_read_out_of_whatever_it_prints(
        string output, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), ClaudeCli.ParseVersion(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("command not found")]
    [InlineData("2.1")]
    public void An_unreadable_version_is_no_version(string? output)
    {
        Assert.Null(ClaudeCli.ParseVersion(output));
    }

    [Fact]
    public void Refresh_interval_is_only_written_for_versions_that_know_the_key()
    {
        // Older Claude Code complains at the user about keys it does not know.
        Assert.False(ClaudeCli.SupportsRefreshInterval(null));
        Assert.False(ClaudeCli.SupportsRefreshInterval(new Version(2, 1, 96)));
        Assert.True(ClaudeCli.SupportsRefreshInterval(new Version(2, 1, 97)));
        Assert.True(ClaudeCli.SupportsRefreshInterval(new Version(2, 2, 0)));
    }
}
