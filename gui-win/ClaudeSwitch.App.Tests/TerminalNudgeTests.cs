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

    [Fact]
    public void Letters_and_enter_carry_real_virtual_keys()
    {
        // ConPTY / ink readers that look at vk rather than UnicodeChar drop
        // events with vk=0. The continue line must not be made of those.
        var plan = TerminalNudge.PlanKeys(
            "Continue from where you left off. Do not repeat work that is already done.\r");

        var enter = Assert.Single(plan, k => k.Char == '\r');
        Assert.Equal(0x0D, enter.VirtualKey);
        Assert.All(plan.Where(k => k.Char is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')),
            k => Assert.True(k.VirtualKey != 0, $"vk=0 for {k.Char}"));
        // Layout-independent: every letter maps to the matching VK, shift aside.
        Assert.All(plan.Where(k => k.Char is >= 'A' and <= 'Z'),
            k => Assert.Equal(char.ToUpperInvariant(k.Char), (char)k.VirtualKey));
        Assert.All(plan.Where(k => k.Char is >= 'a' and <= 'z'),
            k => Assert.Equal(char.ToUpperInvariant(k.Char), (char)k.VirtualKey));
    }

    [Fact]
    public void Ctrl_and_alt_are_not_applied_from_the_layout()
    {
        // '@' is Ctrl+Alt+something on some layouts. Sending those modifiers
        // would fire a shortcut instead of typing the character.
        foreach (var k in TerminalNudge.PlanKeys("@#$"))
        {
            Assert.Equal(0u, k.Mods & ~0x0010u);
        }
    }

    [Fact]
    public void Candidates_start_at_the_pid_and_do_not_walk_into_the_system()
    {
        int me = Environment.ProcessId;
        var list = TerminalNudge.ConsoleCandidates(me);
        Assert.Equal(me, list[0]);
        Assert.DoesNotContain(0, list);
        Assert.DoesNotContain(4, list);
        // The walk stops before GUI hosts; explorer must never be a target.
        foreach (int pid in list)
        {
            string name = System.Diagnostics.Process.GetProcessById(pid).ProcessName;
            Assert.False(name.Equals("explorer", StringComparison.OrdinalIgnoreCase), name);
            Assert.False(name.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase), name);
        }
    }

    [Fact]
    public void A_pipe_stdin_is_recognised_and_does_not_retarget_the_test_host()
    {
        // The child's stdin is the read end of a pipe — we cannot write it.
        // Walking from there into the testhost and typing into *our* console
        // is the failure this pins down.
        var node = FindNode();
        Assert.False(string.IsNullOrEmpty(node), "node is required");

        using var child = new System.Diagnostics.Process();
        child.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = node,
            ArgumentList = { "-e", "setInterval(() => {}, 1000)" },
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        Assert.True(child.Start());
        try
        {
            Assert.Equal(RemoteStdinKind.Pipe, TerminalNudge.PeekStdin(child.Id));
            var candidates = TerminalNudge.ConsoleCandidates(child.Id);
            Assert.Equal(child.Id, candidates[0]);
            Assert.DoesNotContain(Environment.ProcessId, candidates);

            var result = TerminalNudge.Send(child.Id, "Continue from where you left off.");
            // Either the child has no console (AttachFailed) or it inherited
            // one we can write — never a silent retarget at the testhost.
            if (result.Delivered)
                Assert.Contains(child.Id.ToString(), result.Detail);
            else
                Assert.Equal(NudgeOutcome.AttachFailed, result.Outcome);
        }
        finally
        {
            try { child.Kill(entireProcessTree: true); } catch { /* gone */ }
        }
    }

    private static string? FindNode()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var c = Path.Combine(dir, "node.exe");
                if (File.Exists(c)) return c;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
