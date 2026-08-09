using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>How a supervised run ended, and what it did on the way.</summary>
internal sealed record AcpRunReport(
    string? SessionId,
    IReadOnlyList<AcpTurn> Turns,
    string StopCause,
    TimeSpan TotalWaited)
{
    /// <summary>Continuations issued — turns beyond the first.</summary>
    public int Continuations => Math.Max(0, Turns.Count - 1);

    public bool Succeeded => StopCause == "completed";
}

/// <summary>One turn's classified outcome.</summary>
/// <param name="State">"completed" or "interrupted".</param>
/// <param name="Value">The stopReason or interrupt kind.</param>
internal sealed record AcpTurn(string State, string Value, string? Detail, TimeSpan WaitedBefore)
{
    public bool IsInterruption => State == "interrupted";
}

/// <summary>
/// Drives one ACP session and keeps it alive across interruptions.
/// </summary>
/// <remarks>
/// <para>
/// Every retry decision is made by the Rust engine (<c>acp_decide</c>), not
/// here. The classification table is subtle in ways that would drift if it were
/// reimplemented — a quota hit arrives tagged <c>server_error</c> and must not
/// be retried on a ten-second backoff; an auth failure must never be retried at
/// all. One implementation, two callers (this and the <c>acp-run</c> CLI).
/// </para>
/// <para>
/// This class owns the reconnect: when the adapter dies mid-turn it respawns and
/// re-attaches the <i>same</i> session id with <c>session/load</c>, so the
/// continuation lands in the original conversation instead of forking a new one.
/// </para>
/// </remarks>
internal sealed class AcpRunner : IAsyncDisposable
{
    private readonly Engine _engine;
    private readonly JsonObject? _policy;
    private readonly string? _mode;

    /// <summary>
    /// How to launch the agent. Replaced when a run is handed to another
    /// account: the profile it authenticates as is part of the launch.
    /// </summary>
    private AcpLaunch _launch;

    /// <summary>Managed account slot this run is currently spending.</summary>
    private int? _accountNumber;

    /// <summary>
    /// Longest a single turn may take before it is treated as hung.
    /// </summary>
    /// <remarks>
    /// The safety net for the one failure the typed signals cannot catch: an
    /// agent that neither answers nor dies. Nothing else in the loop has a
    /// clock, so without this a wedged request waits forever — which is the
    /// exact hang auto-continue exists to remove.
    /// </remarks>
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(30);

    private AcpSession? _session;
    private string? _sessionId;

    /// <summary>Streamed <c>session/update</c> payloads from the live agent.</summary>
    public event Action<JsonNode>? Update;

    /// <summary>Adapter stderr.</summary>
    public event Action<string>? Diagnostic;

    /// <summary>
    /// Raised when a turn was interrupted and a retry is scheduled.
    /// Arguments: the interrupt kind, the detail text, and the wait before retrying.
    /// </summary>
    public event Action<string, string?, TimeSpan>? ContinueScheduled;

    /// <summary>Raised when the runner gives up. Argument is the stop cause.</summary>
    public event Action<string>? Stopped;

    /// <summary>
    /// Raised when the run moves to another account because quota ran out.
    /// Arguments: the account left behind, and the one taking over.
    /// </summary>
    public event Action<int?, int>? AccountSwitched;

    /// <summary>Answers <c>session/request_permission</c>; null denies.</summary>
    public Func<JsonNode, Task<string?>>? PermissionRequested;

    public string? SessionId => _sessionId;

    /// <summary>The account this run is currently spending, if it names one.</summary>
    public int? AccountNumber => _accountNumber;

    /// <summary>
    /// Profile this run currently authenticates as; null means the default login.
    /// </summary>
    /// <remarks>
    /// Read from the live launch rather than from the one passed in, because a
    /// handover replaces it. Journalling the original after a switch would pair
    /// the new account number with the old profile path — a record that resumes
    /// into a profile holding no such conversation.
    /// </remarks>
    public string? ConfigDir => _launch.ConfigDir;

    /// <summary>Process id of the agent currently backing this run, if any.</summary>
    public int? AdapterProcessId => _session?.ProcessId;

    public AcpRunner(
        Engine engine,
        AcpLaunch launch,
        JsonObject? policy = null,
        string? mode = null,
        string? resumeSessionId = null,
        int? accountNumber = null)
    {
        _engine = engine;
        _launch = launch;
        _policy = policy;
        _mode = mode;
        _sessionId = resumeSessionId;
        _accountNumber = accountNumber;
    }

    /// <summary>
    /// Send <paramref name="prompt"/>, then continue the run per the engine's policy.
    /// </summary>
    /// <param name="ct">
    /// Cancels the run. A user-initiated stop is reported as <c>cancelled</c>,
    /// never retried.
    /// </param>
    public async Task<AcpRunReport> RunAsync(string prompt, CancellationToken ct = default)
    {
        var turns = new List<AcpTurn>();
        int attempt = 0;
        int rateLimitWaits = 0;
        var totalWaited = TimeSpan.Zero;
        var waitedBefore = TimeSpan.Zero;
        string text = prompt;
        string stopCause;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            JsonObject query;
            string? detail = null;

            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            turnCts.CancelAfter(TurnTimeout);

            try
            {
                string sessionId = await EnsureReadyAsync(turnCts.Token).ConfigureAwait(false);
                var result = await _session!.PromptAsync(sessionId, text, turnCts.Token)
                    .ConfigureAwait(false);
                query = new JsonObject
                {
                    ["stopReason"] = result["stopReason"]?.GetValue<string>() ?? "",
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The user stopped the run; that is an answer, not a fault.
                turns.Add(new AcpTurn("completed", "cancelled", null, waitedBefore));
                stopCause = "cancelled";
                Stopped?.Invoke(stopCause);
                return new AcpRunReport(_sessionId, turns, stopCause, totalWaited);
            }
            catch (OperationCanceledException)
            {
                // Our own deadline, not the user's: the turn is hung. Tell the
                // agent to stop before dropping it, so a still-live one does not
                // keep working on a turn nobody is listening to.
                detail = Loc.T("acp.err.turnTimeout", (int)TurnTimeout.TotalMinutes);
                await CancelTurnAsync().ConfigureAwait(false);
                query = new JsonObject { ["transport"] = "timeout" };
                await DropSessionAsync().ConfigureAwait(false);
            }
            catch (AcpRpcException rpc)
            {
                detail = rpc.Message;
                query = new JsonObject { ["message"] = rpc.Message };
                if (rpc.ErrorKind is { Length: > 0 } kind) query["errorKind"] = kind;
            }
            catch (AcpDisconnectedException)
            {
                detail = Loc.T("acp.err.disconnected");
                query = new JsonObject { ["transport"] = "disconnected" };
                await DropSessionAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException
                                        or FileNotFoundException or DirectoryNotFoundException)
            {
                // Could not even start the agent. Classify it like any other
                // failure so a transient cause still gets its retry.
                detail = ex.Message;
                query = new JsonObject { ["message"] = ex.Message };
                await DropSessionAsync().ConfigureAwait(false);
            }

            query["attempt"] = attempt;
            query["rateLimitWaits"] = rateLimitWaits;
            // Naming the account lets the engine answer from its own usage
            // cache: the real reset time instead of a blind backoff, and whether
            // another account could take over.
            if (_accountNumber is { } acct) query["accountNumber"] = acct;
            if (_policy is not null) query["policy"] = _policy.DeepClone();

            var decision = _engine.Call("acp_decide", query);
            string state = decision["outcome"]?["state"]?.GetValue<string>() ?? "interrupted";
            string value = decision["outcome"]?["value"]?.GetValue<string>() ?? "unknown";
            turns.Add(new AcpTurn(state, value, detail, waitedBefore));

            if (decision["action"]?.GetValue<string>() != "continue")
            {
                stopCause = decision["cause"]?.GetValue<string>() ?? "failed";
                Stopped?.Invoke(stopCause);
                return new AcpRunReport(_sessionId, turns, stopCause, totalWaited);
            }

            bool switching = decision["switchAccount"]?.GetValue<bool>() == true;

            // A handover costs no wait, so it must not spend a wait either;
            // only an actual sleep counts against the quota budget.
            if (value == "rateLimit" && !switching) rateLimitWaits++;
            else if (value != "rateLimit") attempt++;

            if (decision["needsFreshProcess"]?.GetValue<bool>() == true)
            {
                await DropSessionAsync().ConfigureAwait(false);
            }

            if (switching && !await TryHandOverAsync().ConfigureAwait(false))
            {
                // Nothing to hand to after all — the account list can change
                // between the decision and the attempt. Stopping is honest;
                // silently carrying on would burn the retry budget on the same
                // wall the switch was meant to get around.
                stopCause = "rateLimited";
                Stopped?.Invoke(stopCause);
                return new AcpRunReport(_sessionId, turns, stopCause, totalWaited);
            }

            // Resume rather than repeat: re-sending the original instruction
            // would make the agent redo work it finished before the interruption.
            text = decision["continueMessage"]?.GetValue<string>()
                ?? "Continue from where you left off.";

            var delay = TimeSpan.FromSeconds(decision["delaySeconds"]?.GetValue<long>() ?? 0);
            ContinueScheduled?.Invoke(value, detail, delay);
            totalWaited += delay;
            waitedBefore = delay;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Move this run to an account that still has quota.
    /// </summary>
    /// <remarks>
    /// The engine does the real work: it picks the target, makes sure that
    /// profile exists, and <b>moves the session transcript into it</b>. That
    /// move is not optional — a session is only visible to the profile that
    /// holds its transcript, so loading it under another account without the
    /// move answers <c>Resource not found</c>.
    /// </remarks>
    private async Task<bool> TryHandOverAsync()
    {
        if (_sessionId is not { Length: > 0 } sessionId) return false;

        JsonNode result;
        try
        {
            var args = new JsonObject { ["sessionId"] = sessionId };
            if (_accountNumber is { } from) args["fromAccount"] = from;
            result = _engine.Call("agent_run_handoff", args);
        }
        catch (EngineException)
        {
            return false;
        }

        if (result["accountNumber"]?.GetValue<int>() is not { } target) return false;

        int? previous = _accountNumber;
        _accountNumber = target;
        _launch = new AcpLaunch
        {
            WorkingDirectory = _launch.WorkingDirectory,
            ConfigDir = result["configDir"]?.GetValue<string>(),
            Proxy = _launch.Proxy,
        };

        // The new account is a different login, so the old agent cannot be
        // reused whatever the decision said.
        await DropSessionAsync().ConfigureAwait(false);
        AccountSwitched?.Invoke(previous, target);
        return true;
    }

    /// <summary>Interrupt the in-flight turn without tearing down the session.</summary>
    public async Task CancelTurnAsync()
    {
        if (_session is not null && _sessionId is not null)
        {
            await _session.CancelAsync(_sessionId).ConfigureAwait(false);
        }
    }

    /// <summary>Connect if needed, and make sure a session exists on this connection.</summary>
    private async Task<string> EnsureReadyAsync(CancellationToken ct)
    {
        if (_session is not null && _session.HasExited)
        {
            await DropSessionAsync().ConfigureAwait(false);
        }

        if (_session is null)
        {
            var session = await AcpSession.ConnectAsync(_launch, ct).ConfigureAwait(false);
            session.Update += u => Update?.Invoke(u);
            session.Diagnostic += d => Diagnostic?.Invoke(d);
            session.PermissionRequested = p =>
                PermissionRequested is null ? Task.FromResult<string?>(null) : PermissionRequested(p);
            _session = session;

            if (_sessionId is { Length: > 0 } existing)
            {
                if (!session.SupportsLoadSession)
                    throw new InvalidOperationException(Loc.T("acp.err.noResume"));
                await session.LoadSessionAsync(existing, _launch.WorkingDirectory, ct).ConfigureAwait(false);
            }
            else
            {
                var info = await session.NewSessionAsync(_launch.WorkingDirectory, ct).ConfigureAwait(false);
                _sessionId = info.SessionId;
            }

            if (_mode is { Length: > 0 })
            {
                await session.SetModeAsync(_sessionId!, _mode, ct).ConfigureAwait(false);
            }
        }

        return _sessionId ?? throw new InvalidOperationException("no session id after connect");
    }

    private async Task DropSessionAsync()
    {
        if (_session is null) return;
        var s = _session;
        _session = null;
        await s.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(DropSessionAsync());
}
