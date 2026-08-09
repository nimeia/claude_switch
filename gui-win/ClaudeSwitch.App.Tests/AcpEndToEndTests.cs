using System.Diagnostics;
using System.Text.Json.Nodes;
using ClaudeSwitch.App;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// Drives the GUI's own ACP client against a real Claude Code agent.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in: these spawn the adapter, reach Anthropic, and spend quota. Set
/// <c>CLAUDE_SWITCH_ACP_E2E=1</c> to run them. Everything else in the suite is
/// hermetic and stays that way.
/// </para>
/// <para>
/// They exist because the parts that broke during bring-up cannot be reached by
/// unit tests: whether the proxy reaches the child, whether a killed agent is
/// resumed rather than forked, whether a permission request is actually
/// answerable. Each of those was a real failure, not a hypothetical.
/// </para>
/// </remarks>
public class AcpEndToEndTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("CLAUDE_SWITCH_ACP_E2E") == "1";

    private static string MakeWorkDir(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cswitch-acp-e2e", name);
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        return dir;
    }

    private static AcpLaunch LaunchFor(Engine engine, string workDir) => new()
    {
        WorkingDirectory = workDir,
        ConfigDir = null,
        Proxy = engine.Call("proxy_resolve")["proxy"]?.GetValue<string>(),
    };

    /// <summary>
    /// Policy every end-to-end run starts from.
    /// </summary>
    /// <remarks>
    /// <c>onRateLimit: stop</c> is not optional here. The engine's default is to
    /// wait for the window to reset — right for a supervised run a person
    /// started, and a hang for a test, which would sleep for hours instead of
    /// failing. A test that meets a quota wall must say so and stop.
    /// </remarks>
    private static JsonObject TestPolicy(int maxAttempts = 5, int baseDelaySeconds = 10) => new()
    {
        ["onRateLimit"] = "stop",
        ["maxAttempts"] = maxAttempts,
        ["baseDelaySeconds"] = baseDelaySeconds,
    };

    /// <summary>Approves every tool call, as an unattended run would.</summary>
    private static Func<JsonNode, Task<string?>> AllowAll => request =>
    {
        string? id = null;
        if (request["options"] is JsonArray options)
        {
            foreach (var kind in new[] { "allow_always", "allow_once" })
            {
                id ??= options
                    .FirstOrDefault(o => o?["kind"]?.GetValue<string>() == kind)
                    ?["optionId"]?.GetValue<string>();
            }
            id ??= options.FirstOrDefault()?["optionId"]?.GetValue<string>();
        }
        return Task.FromResult(id);
    };

    [SkippableFact]
    public async Task A_plain_run_completes_and_uses_tools()
    {
        Skip.IfNot(Enabled, "set CLAUDE_SWITCH_ACP_E2E=1 to run");

        string dir = MakeWorkDir("plain");
        using var engine = new Engine();
        await using var runner = new AcpRunner(
            engine, LaunchFor(engine, dir), TestPolicy(), mode: "acceptEdits")
        {
            PermissionRequested = AllowAll,
        };

        var report = await runner.RunAsync(
            "Create a file named gui.txt containing exactly: GUI-OK. Then reply DONE.");

        Assert.True(report.Succeeded, $"stop cause was {report.StopCause}");
        Assert.Equal(0, report.Continuations);
        Assert.Equal("GUI-OK", File.ReadAllText(Path.Combine(dir, "gui.txt")).Trim());
    }

    [SkippableFact]
    public async Task A_killed_agent_is_resumed_in_the_same_conversation()
    {
        Skip.IfNot(Enabled, "set CLAUDE_SWITCH_ACP_E2E=1 to run");

        string dir = MakeWorkDir("crash");
        using var engine = new Engine();

        // Short backoff: the point is the reconnect, not the wait.
        var policy = TestPolicy(maxAttempts: 3, baseDelaySeconds: 3);
        await using var runner = new AcpRunner(
            engine, LaunchFor(engine, dir), policy, mode: "acceptEdits")
        {
            PermissionRequested = AllowAll,
        };

        string? sessionBeforeCrash = null;
        var crashed = new TaskCompletionSource();
        runner.ContinueScheduled += (kind, _, _) =>
        {
            if (kind == "adapterCrash") crashed.TrySetResult();
        };

        var run = runner.RunAsync(
            "Do these one at a time with a separate Write call each, pausing to think between: " +
            "step1.txt containing STEP1, then step2.txt STEP2, then step3.txt STEP3, " +
            "then step4.txt STEP4, then step5.txt STEP5. Finally reply DONE.");

        // Let the first turn get going, then kill this run's own agent out from
        // under it. Waiting for a pid rather than sleeping a fixed time keeps the
        // kill inside the turn even when the adapter is slow to start.
        int pid = await WaitForAdapterAsync(runner);
        sessionBeforeCrash = runner.SessionId;
        using (var victim = Process.GetProcessById(pid))
        {
            victim.Kill(entireProcessTree: true);
        }

        // The crash must be observed while the turn is live; if the turn had
        // already finished, this test would silently prove nothing.
        await Task.WhenAny(crashed.Task, Task.Delay(TimeSpan.FromSeconds(60)));

        var report = await run;

        Assert.NotNull(sessionBeforeCrash);
        Assert.True(
            crashed.Task.IsCompleted,
            "the kill did not land while a turn was in flight — nothing was tested");
        Assert.True(report.Succeeded, $"stop cause was {report.StopCause}");
        Assert.True(report.Continuations >= 1);
        Assert.Contains(report.Turns, t => t is { State: "interrupted", Value: "adapterCrash" });

        // Resumed, not forked: a new session id would mean the continuation
        // landed in a fresh conversation with none of the work in it.
        Assert.Equal(sessionBeforeCrash, report.SessionId);
        Assert.Equal("STEP5", File.ReadAllText(Path.Combine(dir, "step5.txt")).Trim());
    }

    [SkippableFact]
    public async Task Denying_permission_does_not_spin_on_retries()
    {
        Skip.IfNot(Enabled, "set CLAUDE_SWITCH_ACP_E2E=1 to run");

        string dir = MakeWorkDir("deny");
        using var engine = new Engine();

        int asked = 0;
        await using var runner = new AcpRunner(
            engine, LaunchFor(engine, dir), TestPolicy(), mode: "default")
        {
            // Deny everything, the way the dialog's Esc path does.
            PermissionRequested = _ =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult<string?>(null);
            },
        };

        var report = await runner.RunAsync(
            "Create a file named denied.txt containing NOPE. If you cannot, say so and stop.");

        // The agent is free to give up or explain; what must not happen is the
        // run treating a refusal as a transient fault and looping on it.
        Assert.Equal(0, report.Continuations);
        Assert.False(File.Exists(Path.Combine(dir, "denied.txt")));
    }

    [SkippableFact]
    public async Task A_run_orphaned_by_an_app_crash_resumes_with_its_context()
    {
        Skip.IfNot(Enabled, "set CLAUDE_SWITCH_ACP_E2E=1 to run");

        string dir = MakeWorkDir("restart");
        using var engine = new Engine();
        var store = new AgentRunStore(engine);

        // ── first "app session": do some work, then vanish ──────────────
        string? sessionId;
        await using (var first = new AcpRunner(
            engine, LaunchFor(engine, dir), TestPolicy(), mode: "acceptEdits")
        {
            PermissionRequested = AllowAll,
        })
        {
            var report = await first.RunAsync(
                "Create a file named alpha.txt containing the word PELICAN. " +
                "Remember that word; I will ask about it. Then reply DONE.");

            Assert.True(report.Succeeded, $"first run stopped at {report.StopCause}");
            sessionId = report.SessionId;
            Assert.NotNull(sessionId);
            Assert.Equal("PELICAN", File.ReadAllText(Path.Combine(dir, "alpha.txt")).Trim());
        }

        // The app dies without a clean shutdown: the record is left "running"
        // owned by a pid that no longer exists. Pid 0 is never a live process,
        // so this is the deterministic stand-in for a killed app.
        engine.Call("agent_run_upsert", new JsonObject
        {
            ["id"] = sessionId!,
            ["sessionId"] = sessionId!,
            ["cwd"] = dir,
            ["prompt"] = "create alpha.txt and remember the word",
            ["status"] = "running",
            ["ownerPid"] = 0,
            ["createdMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["updatedMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        // ── next launch: the orphan is found and offered back ───────────
        var resumable = store.Resumable();
        var record = Assert.Single(resumable, r => r.Id == sessionId);
        Assert.Equal("interrupted", record.Status);
        Assert.Equal(sessionId, record.SessionId);
        Assert.Equal(dir, record.Cwd);

        // ── resume: same conversation, prior work still in it ───────────
        await using var second = new AcpRunner(
            engine, LaunchFor(engine, dir), TestPolicy(), mode: "acceptEdits",
            resumeSessionId: record.SessionId)
        {
            PermissionRequested = AllowAll,
        };

        var resumed = await second.RunAsync(
            "Without reading any files, write the word you were asked to remember " +
            "into a new file named beta.txt. Then reply DONE.");

        Assert.True(resumed.Succeeded, $"resume stopped at {resumed.StopCause}");

        // Continuity, not just a fresh session in the same folder: the agent
        // could only know the word from the conversation it resumed.
        Assert.Equal("PELICAN", File.ReadAllText(Path.Combine(dir, "beta.txt")).Trim());
        Assert.Equal(sessionId, resumed.SessionId);
    }

    [SkippableFact]
    public async Task A_finished_run_is_journaled_as_completed()
    {
        Skip.IfNot(Enabled, "set CLAUDE_SWITCH_ACP_E2E=1 to run");

        string dir = MakeWorkDir("journal");
        using var engine = new Engine();
        var store = new AgentRunStore(engine);

        var live = BackgroundRuns.Start(
            engine, store, LaunchFor(engine, dir),
            "Reply with exactly: JOURNAL-OK",
            accountLabel: "test", accountNumber: null, mode: "acceptEdits",
            policy: TestPolicy(), permissionRequested: AllowAll);

        var done = new TaskCompletionSource<AcpRunReport>();
        live.Completed += r => done.TrySetResult(r);
        var report = await done.Task.WaitAsync(TimeSpan.FromMinutes(5));

        Assert.True(report.Succeeded, $"stopped at {report.StopCause}");

        // Journalled under the real session id, not the pending placeholder —
        // otherwise the record could never be matched to its conversation.
        var record = Assert.Single(store.List(), r => r.Id == report.SessionId);
        Assert.Equal("completed", record.Status);
        Assert.False(record.IsResumable);
        Assert.False(record.IsLive);

        store.Remove(record.Id);
        Assert.DoesNotContain(store.List(), r => r.Id == record.Id);
    }

    /// <summary>
    /// Wait until the runner has an agent process, and return its pid.
    /// </summary>
    /// <remarks>
    /// The runner reports the pid of the process it is actually talking to, so
    /// the fault test kills exactly that one — no scanning every node.exe on the
    /// machine and hoping the command line matches.
    /// </remarks>
    private static async Task<int> WaitForAdapterAsync(AcpRunner runner)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (runner.AdapterProcessId is { } pid)
            {
                // Let the turn get properly under way before pulling the rug.
                await Task.Delay(TimeSpan.FromSeconds(6));
                return runner.AdapterProcessId ?? pid;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException("the runner never reported an agent process");
    }
}
