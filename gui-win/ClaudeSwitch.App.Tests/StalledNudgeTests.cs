using System.Text.Json.Nodes;
using ClaudeSwitch.App;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The nudge-first half of resuming a stalled session, driven end to end
/// against a real engine and a real transcript — with only the keystroke
/// delivery and the ACP takeover replaced by fakes.
/// </summary>
/// <remarks>
/// The transcript the fake "terminal" writes is the same file the engine's
/// <c>session_tail</c> reads, so the watch's polling loop runs exactly as in
/// production; what is faked is only the two edges that would otherwise need a
/// live console (delivery) and a live agent (takeover).
/// </remarks>
public class StalledNudgeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cswitch-nudge-" + Guid.NewGuid().ToString("N"));

    private readonly Engine _engine;
    private readonly AgentRunStore _store;

    public StalledNudgeTests()
    {
        Directory.CreateDirectory(_root);
        _engine = new Engine(_root);
        _store = new AgentRunStore(_engine);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // A session id that is also a valid transcript file stem.
    private const string Sid = "11111111-2222-3333-4444-555555555555";

    private string TranscriptDir =>
        Path.Combine(_root, ".claude", "projects", "D--work");

    private string TranscriptPath => Path.Combine(TranscriptDir, Sid + ".jsonl");

    private static string UserLine(string text) => new JsonObject
    {
        ["type"] = "user",
        ["cwd"] = "D:/work",
        ["message"] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        },
    }.ToJsonString();

    private static readonly string RateLimitLine = new JsonObject
    {
        ["type"] = "assistant",
        ["cwd"] = "D:/work",
        ["isApiErrorMessage"] = true,
        ["message"] = new JsonObject
        {
            ["role"] = "assistant",
            ["model"] = "claude-opus-5",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = "Claude AI usage limit reached|1786900000",
            }),
        },
    }.ToJsonString();

    /// <summary>Plant a stalled transcript and answer with the record describing it.</summary>
    /// <remarks>
    /// The mtime is backdated the way a real stall looks — idle for twenty
    /// minutes — so the "did the transcript move" comparison has the same
    /// margin it has in production, where the scan's mtime is minutes old.
    /// </remarks>
    private StalledRecord PlantStalled(int pid)
    {
        Directory.CreateDirectory(TranscriptDir);
        File.WriteAllText(TranscriptPath, UserLine("do the thing") + "\n" + RateLimitLine + "\n");
        File.SetLastWriteTimeUtc(TranscriptPath, DateTime.UtcNow.AddMinutes(-20));
        var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(TranscriptPath)).ToUnixTimeMilliseconds();
        return new StalledRecord(Sid, "D:/work", 1, null, mtime, 20 * 60_000, "rateLimit",
            "Claude AI usage limit reached", TranscriptPath, pid);
    }

    private void AppendToTranscript(string line) =>
        File.AppendAllText(TranscriptPath, line + "\n");

    /// <summary>The delivery fake: records the send, then plays the scripted part.</summary>
    private sealed class FakeTerminal
    {
        private readonly Queue<Action> _script = new();
        public List<(int Pid, string Text)> Sends { get; } = [];

        /// <summary>What the "terminal" does with the next line it receives.</summary>
        public void OnNextSend(Action act) => _script.Enqueue(act);

        public NudgeResult Send(int pid, string text)
        {
            Sends.Add((pid, text));
            if (_script.Count > 0) _script.Dequeue().Invoke();
            return new NudgeResult(NudgeOutcome.Delivered, "fake");
        }
    }

    private sealed class Watch
    {
        public required StalledWatch Inner;
        public required FakeTerminal Terminal;
        public required List<StalledRecord> Takeovers;
        public required List<StalledRecord> NudgeWins;
    }

    /// <summary>A watch with test timings: a 3s window, resend at 600ms, polling at 50ms.</summary>
    private Watch NewWatch()
    {
        var terminal = new FakeTerminal();
        var takeovers = new List<StalledRecord>();
        var wins = new List<StalledRecord>();
        var inner = new StalledWatch(
            _engine, _store, _ => "acct", null,
            terminal.Send,
            r => { takeovers.Add(r); return true; },
            TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(50));
        inner.NudgeSucceeded += r => wins.Add(r);
        return new Watch { Inner = inner, Terminal = terminal, Takeovers = takeovers, NudgeWins = wins };
    }

    [Fact]
    public void A_delivered_line_that_starts_a_turn_needs_no_takeover()
    {
        var record = PlantStalled(pid: 4242);
        var w = NewWatch();
        // The terminal accepts the line and does what Claude Code does first:
        // append the submitted user message to the transcript.
        w.Terminal.OnNextSend(() => AppendToTranscript(UserLine(w.Terminal.Sends[0].Text)));

        Assert.True(w.Inner.TryResume(record));

        Assert.Single(w.NudgeWins);
        Assert.Empty(w.Takeovers);
        var sent = Assert.Single(w.Terminal.Sends);
        Assert.Equal(4242, sent.Pid);
        Assert.Contains("Continue from where you left off", sent.Text);
    }

    [Fact]
    public void A_line_swallowed_on_the_way_is_sent_again_once()
    {
        var record = PlantStalled(pid: 4242);
        var w = NewWatch();
        // First send vanishes (a transient dialog ate it); the second lands.
        w.Terminal.OnNextSend(() => { /* swallowed */ });
        w.Terminal.OnNextSend(() => AppendToTranscript(UserLine(w.Terminal.Sends[1].Text)));

        Assert.True(w.Inner.TryResume(record));

        Assert.Equal(2, w.Terminal.Sends.Count);
        Assert.Empty(w.Takeovers);
        Assert.Single(w.NudgeWins);
    }

    [Fact]
    public void Keystrokes_nobody_reads_end_in_a_takeover()
    {
        var record = PlantStalled(pid: 4242);
        var w = NewWatch();
        // Every send reports delivered — the console buffer accepts anything —
        // but nothing ever reads: the transcript never moves.

        Assert.True(w.Inner.TryResume(record)); // the takeover half ran

        // Initial send plus exactly one resend — bounded, then the takeover.
        Assert.Equal(2, w.Terminal.Sends.Count);
        Assert.Single(w.Takeovers);
        Assert.Empty(w.NudgeWins);
    }

    [Fact]
    public void A_retry_into_the_same_wall_goes_straight_to_takeover()
    {
        var record = PlantStalled(pid: 4242);
        var w = NewWatch();
        // The terminal takes the line and retries immediately — into the wall
        // that has not lifted. The transcript moves, and still reads failed.
        w.Terminal.OnNextSend(() => AppendToTranscript(UserLine(w.Terminal.Sends[0].Text) + "\n" + RateLimitLine));

        Assert.True(w.Inner.TryResume(record)); // takeover ran

        // No resend, no waiting out the window: a fresh failure is a verdict.
        Assert.Single(w.Terminal.Sends);
        Assert.Single(w.Takeovers);
        Assert.Empty(w.NudgeWins);
    }

    [Fact]
    public void An_undeliverable_nudge_takes_over_at_once()
    {
        var record = PlantStalled(pid: 4242);
        var takeovers = new List<StalledRecord>();
        var sends = 0;
        var watch = new StalledWatch(
            _engine, _store, _ => "acct", null,
            (_, _) => { sends++; return new NudgeResult(NudgeOutcome.AttachFailed, "no console"); },
            r => { takeovers.Add(r); return true; },
            TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(50));

        Assert.True(watch.TryResume(record));
        Assert.Equal(1, sends); // never polled, never resent
        Assert.Single(takeovers);
    }

    [Fact]
    public void An_unreadable_tail_is_neither_success_nor_failure()
    {
        var record = PlantStalled(pid: 4242);
        var w = NewWatch();
        // Replace the transcript with one that classifies as "unknown": the
        // old code read any non-failed state as life and claimed a wakeup.
        File.WriteAllText(TranscriptPath, "{\"type\":\"queue-operation\"}\n");

        Assert.True(w.Inner.TryResume(record)); // takeover ran

        Assert.Single(w.Takeovers);
        Assert.Empty(w.NudgeWins);
    }

    [Fact]
    public void A_session_that_stalls_again_after_a_successful_nudge_is_taken_over()
    {
        var w = NewWatch();
        var pid = Environment.ProcessId; // must be alive for the pid mapping

        // First sweep: stalled, terminal takes the line and starts a turn.
        PlantForSweep(pid);
        w.Terminal.OnNextSend(() => AppendToTranscript(UserLine(w.Terminal.Sends[0].Text)));
        Assert.Equal(1, w.Inner.SweepAndResume());
        Assert.Single(w.NudgeWins);
        Assert.Empty(w.Takeovers);

        // The wall had not lifted: the retried turn failed again, and the
        // session has been sitting idle long enough to qualify once more.
        File.WriteAllText(TranscriptPath,
            UserLine("do the thing") + "\n" + UserLine("continue") + "\n" + RateLimitLine + "\n");
        File.SetLastWriteTimeUtc(TranscriptPath, DateTime.UtcNow.AddMinutes(-20));

        // Second sweep: straight to a takeover — the terminal was already
        // given its chance, and prodding it again would burn another turn
        // against the same wall.
        Assert.Equal(1, w.Inner.SweepAndResume());
        Assert.Single(w.Takeovers);
        Assert.Single(w.Terminal.Sends); // no second nudge was attempted
        Assert.Single(w.NudgeWins);
    }

    /// <summary>
    /// Plant the sweep-visible fixtures: the stalled transcript backdated past
    /// the idle threshold, and the pid file Claude Code writes for itself.
    /// </summary>
    private void PlantForSweep(int pid)
    {
        PlantStalled(pid);
        var sessions = Path.Combine(_root, ".claude", "sessions");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(sessions, pid + ".json"),
            $$"""{"pid":{{pid}},"sessionId":"{{Sid}}","cwd":"D:/work","startedAt":{{now}}}""");
    }
}
