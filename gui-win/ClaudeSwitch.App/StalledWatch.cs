using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>One terminal session that stopped against its will.</summary>
/// <param name="Reason">"rateLimit" or "network" — why it stopped.</param>
internal sealed record StalledRecord(
    string SessionId,
    string Cwd,
    int? AccountNumber,
    string? ConfigDir,
    long ModifiedMs,
    long IdleMs,
    string Reason,
    string LastError)
{
    public DateTime ModifiedLocal =>
        DateTimeOffset.FromUnixTimeMilliseconds(ModifiedMs).LocalDateTime;

    public int IdleMinutes => (int)Math.Max(0, IdleMs / 60_000);

    public static StalledRecord? FromJson(JsonNode? n)
    {
        if (n?["sessionId"]?.GetValue<string>() is not { Length: > 0 } id) return null;
        return new StalledRecord(
            id,
            n["cwd"]?.GetValue<string>() ?? "",
            n["accountNumber"]?.GetValue<int>(),
            n["configDir"]?.GetValue<string>(),
            n["modifiedMs"]?.GetValue<long>() ?? 0,
            n["idleMs"]?.GetValue<long>() ?? 0,
            n["reason"]?.GetValue<string>() ?? "network",
            n["lastError"]?.GetValue<string>() ?? "");
    }
}

/// <summary>Why an automatic takeover was not carried through.</summary>
/// <param name="Record">The session it was attempted on.</param>
/// <param name="Detail">What went wrong, for the row's tooltip.</param>
internal sealed record TakeoverFailure(StalledRecord Record, string Detail);

/// <summary>
/// Watches for terminal sessions that stalled, and resumes them.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="BackgroundRuns"/>: that owns runs this app
/// started, this one picks up conversations someone left in a terminal. A
/// stalled Claude Code is still <i>running</i> — it prints the limit and waits
/// for a human — so process liveness says nothing, and the engine's transcript
/// scan (<c>stalled_scan</c>) is the only thing that can tell stuck from busy.
/// </para>
/// <para>
/// <b>Taking over does not touch the original terminal.</b> It is left running.
/// The resumed conversation moves on underneath it, so what that window shows
/// goes stale — which is why every takeover is announced rather than silent.
/// Killing it was tested and rejected: it makes no difference to whether the
/// resume works, and a hard kill in the middle of a turn is precisely what
/// leaves a session in the state that cannot be resumed cleanly.
/// </para>
/// <para>
/// <b>Success is verified, not assumed.</b> ACP answers
/// <c>stopReason: end_turn</c> even for a turn whose only output was a
/// synthesised <c>"No response requested."</c>, so the protocol's own success
/// signal cannot distinguish work done from work swallowed. Every takeover is
/// checked against the transcript afterwards (<c>session_tail</c>); one that
/// produced no real reply is reported as a failure, not a success.
/// </para>
/// </remarks>
internal sealed class StalledWatch
{
    /// <summary>
    /// Sessions already attempted, so a session that cannot be resumed is not
    /// retried on every sweep.
    /// </summary>
    /// <remarks>
    /// Keyed by session id and never cleared while the app runs: the failure
    /// modes that get here (a directory that no longer exists, an agent that
    /// will not resume) do not fix themselves, and a sweep every couple of
    /// minutes would otherwise spend quota rediscovering them.
    /// </remarks>
    private readonly HashSet<string> _attempted = [];

    /// <summary>
    /// Extra turns spent on a session whose takeover turn was swallowed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resuming a conversation that was interrupted mid-turn costs a turn before
    /// any work happens: the SDK closes the orphaned turn out with a synthesised
    /// <c>"No response requested."</c> and the prompt that triggered it gets no
    /// answer. That is the normal shape of a stalled session — the quota wall
    /// lands in the middle of a turn, not between them — so treating the first
    /// swallowed turn as a failure would report "needs attention" on exactly the
    /// common case.
    /// </para>
    /// <para>
    /// Bounded, because the other reason a turn produces nothing is that it
    /// genuinely cannot proceed, and that must not become a loop.
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, int> _swallowed = [];

    /// <summary>Turns allowed to be swallowed before a session is handed to a human.</summary>
    private const int MaxSwallowedTurns = 2;

    private readonly List<TakeoverFailure> _failures = [];
    private readonly object _gate = new();

    private readonly Engine _engine;
    private readonly AgentRunStore _runs;
    private readonly Func<JsonNode, Task<string?>>? _permission;
    private readonly Func<int?, string> _accountLabel;

    /// <summary>Raised when a takeover starts, succeeds, or fails.</summary>
    public event Action? Changed;

    /// <summary>A takeover finished. Arguments: the session, and whether it worked.</summary>
    public event Action<StalledRecord, bool, string?>? TakeoverFinished;

    public StalledWatch(
        Engine engine,
        AgentRunStore runs,
        Func<int?, string> accountLabel,
        Func<JsonNode, Task<string?>>? permission)
    {
        _engine = engine;
        _runs = runs;
        _accountLabel = accountLabel;
        _permission = permission;
    }

    /// <summary>Sessions a takeover could not rescue; the user has to look.</summary>
    public IReadOnlyList<TakeoverFailure> Failures
    {
        get { lock (_gate) return [.. _failures]; }
    }

    public void Forget(string sessionId)
    {
        lock (_gate) _failures.RemoveAll(f => f.Record.SessionId == sessionId);
        Changed?.Invoke();
    }

    /// <summary>Stalled sessions the engine can currently see.</summary>
    public IReadOnlyList<StalledRecord> Scan(int windowHours = 5, int idleMinutes = 10)
    {
        try
        {
            var args = new JsonObject
            {
                ["criteria"] = new JsonObject
                {
                    ["windowHours"] = windowHours,
                    ["idleMinutes"] = idleMinutes,
                },
            };
            if (_engine.Call("stalled_scan", args)["sessions"] is not JsonArray arr) return [];
            return [.. arr.Select(StalledRecord.FromJson).OfType<StalledRecord>()];
        }
        catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
        {
            // An engine without the method is a feature that is simply absent;
            // a disposed one means the app is on its way out. Neither is worth
            // taking the caller's timer tick down over.
            return [];
        }
    }

    /// <summary>
    /// Resume every stalled session not already attempted.
    /// </summary>
    /// <returns>How many takeovers were started.</returns>
    public int SweepAndResume(int windowHours = 5, int idleMinutes = 10)
    {
        int started = 0;
        foreach (var record in Scan(windowHours, idleMinutes))
        {
            lock (_gate)
            {
                if (!_attempted.Add(record.SessionId)) continue;
            }

            // A run this app already owns is not an abandoned terminal — it has
            // its own auto-continue and would otherwise be resumed twice, with
            // both copies writing the same transcript.
            if (BackgroundRuns.Find(record.SessionId) is not null) continue;

            if (TryResume(record)) started++;
        }
        return started;
    }

    /// <summary>Start one takeover. False when it could not even be attempted.</summary>
    public bool TryResume(StalledRecord record)
    {
        if (!Directory.Exists(record.Cwd))
        {
            // The conversation is fine, but an agent has to be rooted somewhere.
            Fail(record, Loc.T("acp.err.workDir", record.Cwd));
            return false;
        }

        var launch = new AcpLaunch
        {
            WorkingDirectory = record.Cwd,
            ConfigDir = record.ConfigDir,
            Proxy = ResolveProxy(),
        };

        // What the run should do about the wall it hit. A quota stall is exactly
        // the case `onRateLimit` exists for; leaving it at the engine's default
        // would make the takeover stop on the same wall that stalled it.
        var policy = new JsonObject
        {
            ["onRateLimit"] = "switch",
            ["maxRateLimitWaits"] = 3,
        };

        var live = BackgroundRuns.Start(
            _engine,
            _runs,
            launch,
            Loc.T("stalled.continuePrompt"),
            _accountLabel(record.AccountNumber),
            record.AccountNumber,
            mode: null,
            policy,
            _permission,
            resumeSessionId: record.SessionId);

        live.Completed += report => Verify(record, report);
        // The run starts before Start returns, so a turn that fails immediately
        // (a missing adapter, an expired login) can already be finished by the
        // time the handler is attached. Without this the verification — and the
        // failure row it produces — would simply never happen.
        if (live is { Finished: true, Report: { } finished }) Verify(record, finished);

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Decide whether a finished takeover actually did anything.
    /// </summary>
    /// <remarks>
    /// The stop cause is necessary but not sufficient. A turn that was swallowed
    /// still reports <c>completed</c>, so the transcript is consulted: only a
    /// reply carrying a real model id proves the resumed conversation moved.
    /// </remarks>
    private void Verify(StalledRecord record, AcpRunReport report)
    {
        if (!report.Succeeded)
        {
            Fail(record, Loc.T($"acp.cause.{report.StopCause}"));
            return;
        }

        try
        {
            var tail = _engine.Call("session_tail", new JsonObject { ["sessionId"] = record.SessionId });
            if (tail["producedRealReply"]?.GetValue<bool>() != true)
            {
                // Spent on closing out the interrupted turn rather than on the
                // work. That is the expected first turn of a resumed stall, so
                // try again before calling it a failure.
                int spent;
                lock (_gate)
                {
                    _swallowed.TryGetValue(record.SessionId, out spent);
                    if (spent < MaxSwallowedTurns) _swallowed[record.SessionId] = spent + 1;
                }
                if (spent < MaxSwallowedTurns)
                {
                    TryResume(record);
                    return;
                }
                Fail(record, Loc.T("stalled.noRealReply"));
                return;
            }
        }
        catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
        {
            // Cannot verify. Reporting success on an unverified turn is the one
            // outcome this whole check exists to prevent, so say so instead.
            Fail(record, Loc.T("stalled.unverified"));
            return;
        }

        TakeoverFinished?.Invoke(record, true, null);
        Changed?.Invoke();
    }

    /// <summary>
    /// Record that a takeover did not rescue this session.
    /// </summary>
    /// <remarks>
    /// The journal is corrected as well as the UI. <see cref="BackgroundRuns"/>
    /// writes the outcome from the ACP stop cause, which reports
    /// <c>completed</c> for a turn that was swallowed — so without this the
    /// journal would claim the work was finished while the dropdown said it
    /// needed attention, and the run would never be offered for resume.
    /// <c>interrupted</c> is the honest status: the conversation is intact and
    /// picking it up again is exactly what is left to do.
    /// </remarks>
    private void Fail(StalledRecord record, string detail)
    {
        lock (_gate)
        {
            _failures.RemoveAll(f => f.Record.SessionId == record.SessionId);
            _failures.Add(new TakeoverFailure(record, detail));
        }
        _runs.Patch(record.SessionId, status: "interrupted", lastError: detail);
        TakeoverFinished?.Invoke(record, false, detail);
        Changed?.Invoke();
    }

    /// <summary>
    /// The proxy the agent must use, resolved by the engine so the env-then-
    /// registry precedence has one implementation.
    /// </summary>
    private string? ResolveProxy()
    {
        try
        {
            return _engine.Call("proxy_resolve")["proxy"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
        {
            return null;
        }
    }
}
