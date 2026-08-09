using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>What a transcript line is, so a view can colour it.</summary>
internal enum RunLineKind
{
    Prompt,
    Assistant,
    Tool,
    Notice,
    Warning,
    Error,
}

internal readonly record struct RunLine(RunLineKind Kind, string Text);

/// <summary>
/// A run that keeps going without a window attached.
/// </summary>
/// <remarks>
/// <para>
/// The window is a view, not the owner. Closing it detaches; the run continues
/// and can be reopened, which is the whole point — a supervised run that dies
/// because someone closed a window is not supervised, it is just slower to lose.
/// </para>
/// <para>
/// Output is buffered so a reattached view can show what it missed. The buffer
/// is bounded: a long run would otherwise grow it without limit, and the
/// interesting part of a transcript is always its tail.
/// </para>
/// </remarks>
internal sealed class LiveRun
{
    /// <summary>Transcript lines retained for a view that attaches later.</summary>
    public const int BacklogLimit = 2000;

    private readonly List<RunLine> _backlog = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Journal key for this run.
    /// </summary>
    /// <remarks>
    /// Starts as a local placeholder and becomes the ACP session id once one
    /// exists — see <see cref="BackgroundRuns"/>. Settable for that one reason:
    /// a record stuck under the placeholder could never be matched to the
    /// conversation it belongs to after a restart.
    /// </remarks>
    public string Id { get; internal set; }

    public AcpRunner Runner { get; }
    public string Cwd { get; }

    /// <summary>Account this run is spending, updated when it is handed over.</summary>
    public string AccountLabel { get; internal set; }

    /// <summary>
    /// Account this run started on, once it has moved off it.
    /// </summary>
    /// <remarks>
    /// Kept so the UI can say <c>a@x.com → b@x.com</c>. A run that changed
    /// account did so because the first one hit its quota wall — which is the
    /// single most useful thing to know about a long run in an app whose whole
    /// job is juggling accounts.
    /// </remarks>
    public string? PreviousAccountLabel { get; internal set; }

    public string Prompt { get; }

    /// <summary>When this run started, for an elapsed-time readout.</summary>
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>
    /// A permission dialog is open and the turn cannot proceed until it is answered.
    /// </summary>
    /// <remarks>
    /// The one kind of stall the app causes itself. Without surfacing it, a run
    /// blocked on a question nobody has seen is indistinguishable from a run
    /// that is simply slow — and the answer is sitting behind a window the user
    /// may have dismissed.
    /// </remarks>
    public bool AwaitingPermission { get; internal set; }

    /// <summary>
    /// What it takes to launch this run again, kept so a shutdown can journal it.
    /// </summary>
    /// <remarks>
    /// The engine's journal upsert replaces a record wholesale, so every write
    /// has to carry these or it erases them. Leaving them off
    /// <see cref="BackgroundRuns.ShutdownAll"/> made a *clean* exit lose the
    /// account and profile — the resumed run then authenticated as the default
    /// login and could not find its own conversation. A crash, which skips that
    /// path entirely and relies on the engine's reaper, kept them.
    /// </remarks>
    public string? Mode { get; init; }

    public JsonObject? Policy { get; init; }

    /// <summary>Account this run started on; the runner's own value wins once it has one.</summary>
    public int? AccountNumber { get; init; }

    /// <summary>Latest status text, for a list row or a status strip.</summary>
    public string Status { get; private set; } = "";

    public bool Finished { get; private set; }
    public AcpRunReport? Report { get; private set; }

    /// <summary>
    /// Message id of the assistant chunk stream currently being appended.
    /// </summary>
    /// <remarks>
    /// Tracked here rather than in a view because the backlog is replayed to
    /// windows that attach later: any separation between messages has to be
    /// baked into the buffered lines, or a reopened window renders every
    /// assistant message as one run-on paragraph.
    /// </remarks>
    internal string? StreamingMessageId { get; set; }

    /// <summary>New transcript output. Raised on whatever thread produced it.</summary>
    public event Action<RunLine>? Line;

    /// <summary>Status text changed.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>The run reached an end. Argument is the stop cause.</summary>
    public event Action<AcpRunReport>? Completed;

    internal LiveRun(string id, AcpRunner runner, string cwd, string accountLabel, string prompt)
    {
        Id = id;
        Runner = runner;
        Cwd = cwd;
        AccountLabel = accountLabel;
        Prompt = prompt;
    }

    internal CancellationToken Token => _cts.Token;

    /// <summary>Everything produced so far, for a view that just attached.</summary>
    public IReadOnlyList<RunLine> Backlog
    {
        get
        {
            lock (_gate) return [.. _backlog];
        }
    }

    internal void Emit(RunLineKind kind, string text)
    {
        var line = new RunLine(kind, text);
        lock (_gate)
        {
            _backlog.Add(line);
            if (_backlog.Count > BacklogLimit)
            {
                // Drop from the front: the tail is what a returning viewer needs.
                _backlog.RemoveRange(0, _backlog.Count - BacklogLimit);
            }
        }
        Line?.Invoke(line);
    }

    internal void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke(text);
    }

    internal void Finish(AcpRunReport report)
    {
        Report = report;
        Finished = true;
        Completed?.Invoke(report);
    }

    /// <summary>Ask the run to stop. Safe to call more than once.</summary>
    public void Cancel()
    {
        try
        {
            _ = Runner.CancelTurnAsync();
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    internal void DisposeToken() => _cts.Dispose();
}

/// <summary>
/// Process-wide registry of supervised runs.
/// </summary>
/// <remarks>
/// <para>
/// Holds runs that are not currently on screen, journals their progress, and
/// stops them cleanly on shutdown. Static because there is one agent fleet per
/// app instance and every window needs to find it.
/// </para>
/// <para>
/// <b>A run does not survive the app.</b> The ACP agent is a child process, so
/// exiting kills it; what survives is the journal entry, which lets the same
/// conversation be resumed on the next launch. That is recovery, not continuity,
/// and <see cref="AgentRunStore"/> is what makes it possible.
/// </para>
/// </remarks>
internal static class BackgroundRuns
{
    private static readonly Dictionary<string, LiveRun> Runs = [];
    private static readonly object Gate = new();

    /// <summary>A run started, finished, or changed status.</summary>
    public static event Action? Changed;

    public static IReadOnlyList<LiveRun> Active
    {
        get
        {
            lock (Gate) return [.. Runs.Values.Where(r => !r.Finished)];
        }
    }

    public static int ActiveCount => Active.Count;

    public static LiveRun? Find(string id)
    {
        lock (Gate) return Runs.GetValueOrDefault(id);
    }

    /// <summary>
    /// Start a supervised run and keep it alive independently of any window.
    /// </summary>
    /// <param name="resumeSessionId">
    /// Continue an existing conversation instead of starting one. The run keeps
    /// that session id, so its journal entry stays the same record across
    /// restarts rather than accumulating one per resume.
    /// </param>
    public static LiveRun Start(
        Engine engine,
        AgentRunStore store,
        AcpLaunch launch,
        string prompt,
        string accountLabel,
        int? accountNumber,
        string? mode,
        JsonObject? policy,
        Func<JsonNode, Task<string?>>? permissionRequested,
        string? resumeSessionId = null,
        Func<int, string>? labelFor = null)
    {
        var runner = new AcpRunner(engine, launch, policy, mode, resumeSessionId, accountNumber);

        // Until session/new answers there is no ACP id to key on, so a run that
        // dies during startup still needs a record. The placeholder is replaced
        // by the real session id as soon as one exists.
        string id = resumeSessionId ?? $"pending-{Guid.NewGuid():N}";
        var live = new LiveRun(id, runner, launch.WorkingDirectory, accountLabel, prompt)
        {
            Mode = mode,
            Policy = policy,
            AccountNumber = accountNumber,
        };

        lock (Gate) Runs[id] = live;

        // Wrapped rather than passed through, so a run parked on a question
        // says so instead of looking like a slow one. The flag is cleared in a
        // finally: a dialog that is cancelled, or throws, must not leave the run
        // permanently marked as waiting.
        if (permissionRequested is not null)
        {
            runner.PermissionRequested = async request =>
            {
                live.AwaitingPermission = true;
                live.SetStatus(Loc.T("acp.status.awaitingPermission"));
                Changed?.Invoke();
                try
                {
                    return await permissionRequested(request).ConfigureAwait(false);
                }
                finally
                {
                    live.AwaitingPermission = false;
                    live.SetStatus(Loc.T("acp.status.running"));
                    Changed?.Invoke();
                }
            };
        }

        long createdMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        void Journal(string status, AcpRunReport? report = null, string? lastError = null) =>
            store.Save(
                live.Id,
                runner.SessionId,
                launch.WorkingDirectory,
                runner.AccountNumber ?? accountNumber,
                launch.ConfigDir,
                mode,
                prompt,
                status,
                policy,
                report?.Turns.Count ?? 0,
                report?.Continuations ?? 0,
                report?.StopCause,
                lastError,
                createdMs);

        Journal("running");

        // The session id only exists after session/new answers. Re-key at the
        // first sign of life rather than at the end, so a crash seconds into a
        // run still leaves a record that names its conversation.
        runner.Update += update =>
        {
            Rekey(live, runner.SessionId, store, status => Journal(status));
            OnUpdate(live, update);
        };
        runner.Diagnostic += d => live.Emit(RunLineKind.Notice, $"[adapter] {d}");
        runner.AccountSwitched += (from, to) =>
        {
            // Remember where it came from: a handover happens because an account
            // ran out, and that is the story the row has to be able to tell.
            live.PreviousAccountLabel = live.AccountLabel;
            live.AccountLabel = labelFor?.Invoke(to) ?? live.AccountLabel;

            string text = Loc.T("acp.status.switched", to);
            live.StreamingMessageId = null;
            live.Emit(RunLineKind.Warning, text);
            live.SetStatus(text);
            Changed?.Invoke();
        };
        runner.ContinueScheduled += (kind, detail, delay) =>
        {
            string kindText = Loc.T($"acp.kind.{kind}");
            string text = delay > TimeSpan.Zero
                ? Loc.T("acp.status.retryIn", kindText, (int)delay.TotalSeconds)
                : Loc.T("acp.status.retryNow", kindText);
            live.StreamingMessageId = null;
            live.Emit(RunLineKind.Warning, text);
            live.SetStatus(text);
            Journal("running", lastError: detail);
            Changed?.Invoke();
        };

        live.Emit(RunLineKind.Prompt, $"› {prompt}");
        live.SetStatus(Loc.T("acp.status.running"));

        _ = Task.Run(async () =>
        {
            AcpRunReport report;
            try
            {
                report = await runner.RunAsync(prompt, live.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                report = new AcpRunReport(runner.SessionId, [], "cancelled", TimeSpan.Zero);
                live.Emit(RunLineKind.Notice, Loc.T("acp.status.stopped"));
            }
            catch (Exception ex)
            {
                report = new AcpRunReport(runner.SessionId, [], "failed", TimeSpan.Zero);
                live.Emit(RunLineKind.Error, Loc.T("acp.err.run", ex.Message));
                Rekey(live, runner.SessionId, store, status => Journal(status));
                Journal("failed", report, ex.Message);
                live.SetStatus(Loc.T("acp.status.failed"));
                live.Finish(report);
                Changed?.Invoke();
                await runner.DisposeAsync().ConfigureAwait(false);
                return;
            }

            Rekey(live, runner.SessionId, store, status => Journal(status));
            Journal(StatusFor(report), report);

            string summary = SummaryFor(report);
            live.Emit(report.Succeeded ? RunLineKind.Notice : RunLineKind.Warning, summary);
            live.SetStatus(summary);
            live.Finish(report);
            Changed?.Invoke();

            // The agent process is only worth keeping while a turn might follow.
            await runner.DisposeAsync().ConfigureAwait(false);
        });

        Changed?.Invoke();
        return live;
    }

    /// <summary>
    /// Journal status for a finished run.
    /// </summary>
    /// <remarks>
    /// Only <c>cancelled</c> and <c>completed</c> are terminal in the user's
    /// sense. Everything else — exhausted retries, an auth failure — leaves work
    /// half-done and is recorded as resumable, because it is.
    /// </remarks>
    private static string StatusFor(AcpRunReport report) => report.StopCause switch
    {
        "completed" => "completed",
        "cancelled" => "cancelled",
        _ => "failed",
    };

    private static string SummaryFor(AcpRunReport report)
    {
        string cause = Loc.T($"acp.cause.{report.StopCause}");
        return report.Continuations == 0
            ? cause
            : Loc.T("acp.status.resumed", cause, report.Continuations);
    }

    /// <summary>
    /// Move a run from its placeholder key to the real ACP session id.
    /// </summary>
    /// <remarks>
    /// Without this a run journaled before <c>session/new</c> answered keeps its
    /// <c>pending-…</c> id forever: the record can never be matched to the
    /// conversation it created, so the run is unresumable and the placeholder
    /// lingers in the journal beside the real entry. Idempotent — it is called
    /// on every update.
    /// </remarks>
    private static void Rekey(LiveRun live, string? sessionId, AgentRunStore store, Action<string> journal)
    {
        if (sessionId is not { Length: > 0 } || sessionId == live.Id) return;

        string stale = live.Id;
        lock (Gate)
        {
            if (live.Id == sessionId) return;
            Runs.Remove(stale);
            live.Id = sessionId;
            Runs[sessionId] = live;
        }
        // Drop the placeholder record, then write the run under its real id.
        store.Remove(stale);
        journal("running");
    }

    private static void OnUpdate(LiveRun live, JsonNode update)
    {
        switch (update["sessionUpdate"]?.GetValue<string>())
        {
            case "agent_message_chunk":
                if (update["content"]?["text"]?.GetValue<string>() is { } text)
                {
                    string? messageId = update["messageId"]?.GetValue<string>();
                    if (messageId != live.StreamingMessageId)
                    {
                        live.StreamingMessageId = messageId;
                        live.Emit(RunLineKind.Assistant, Environment.NewLine);
                    }
                    live.Emit(RunLineKind.Assistant, text);
                }
                break;
            case "tool_call":
                live.StreamingMessageId = null;
                live.Emit(RunLineKind.Tool, $"  ⚙ {update["title"]?.GetValue<string>() ?? "tool"}");
                break;
            default:
                // Thinking and command lists are deliberately not surfaced: the
                // point of this view is outcomes and interruptions.
                break;
        }
    }

    /// <summary>
    /// Stop every run and mark the journal, before the app exits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recording <c>interrupted</c> here is what turns a clean shutdown into a
    /// resumable one. A crash skips this, which is why the engine also reaps
    /// records whose owner process is gone.
    /// </para>
    /// <para>
    /// The account, profile, mode and policy must be written with it. The
    /// engine's upsert replaces a record whole, so omitting them does not leave
    /// the stored values alone — it clears them, and the run then resumes as the
    /// default login instead of as its own account.
    /// </para>
    /// </remarks>
    public static void ShutdownAll(AgentRunStore store)
    {
        List<LiveRun> live;
        lock (Gate) live = [.. Runs.Values.Where(r => !r.Finished)];

        foreach (var run in live)
        {
            store.Save(
                run.Id,
                run.Runner.SessionId,
                run.Cwd,
                // A run handed to another account mid-flight belongs to that
                // one now, so the runner's view outranks the launch value.
                run.Runner.AccountNumber ?? run.AccountNumber,
                run.Runner.ConfigDir,
                run.Mode,
                run.Prompt,
                status: "interrupted",
                policy: run.Policy,
                lastError: "the app closed while this run was in progress");
            run.Cancel();
        }
    }
}
