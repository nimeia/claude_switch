using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The guards around typing into somebody else's terminal.
/// </summary>
/// <remarks>
/// The delivery itself needs a live console and cannot be unit tested; what can
/// be pinned down is that the dangerous inputs are refused before any Win32 call
/// is made.
/// </remarks>
public class TerminalNudgeTests
{
    [Theory]
    [InlineData("Continue from where you left off. Do not repeat work that is already done.")]
    [InlineData("plain ascii 123 !@#")]
    [InlineData("")]
    public void Ascii_text_is_sendable(string text) =>
        Assert.True(TerminalNudge.IsSendable(text));

    [Theory]
    [InlineData("继续之前的工作")]
    [InlineData("mixed 中文 text")]
    [InlineData("café")]
    public void Non_ascii_is_refused(string text)
    {
        // The target console's input code page is the user's, not ours, and a
        // measured injection turned every CJK glyph into a replacement char.
        Assert.False(TerminalNudge.IsSendable(text));
    }

    [Fact]
    public void A_bad_pid_is_refused_without_touching_win32()
    {
        Assert.Equal(NudgeOutcome.NoProcess, TerminalNudge.Send(0, "hi").Outcome);
        Assert.Equal(NudgeOutcome.NoProcess, TerminalNudge.Send(-5, "hi").Outcome);
    }

    [Fact]
    public void Non_ascii_is_refused_before_a_process_is_even_looked_up()
    {
        // Order matters: the encoding guard is cheaper and its failure is the
        // one the caller can actually do something about.
        Assert.Equal(NudgeOutcome.NotAscii, TerminalNudge.Send(999999, "继续").Outcome);
    }

    [Fact]
    public void A_pid_that_does_not_exist_reports_no_process()
    {
        // 0x7FFFFFFF is not a live pid on any Windows box.
        Assert.Equal(NudgeOutcome.NoProcess, TerminalNudge.Send(int.MaxValue, "hi").Outcome);
    }
}
