using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>One journaled supervised run.</summary>
/// <remarks>
/// A read model over the engine's journal — the engine owns the file, the
/// staleness rule, and pruning. Mirroring those here would put a second opinion
/// on when a run counts as interrupted, which is the one thing the record exists
/// to settle.
/// </remarks>
internal sealed record AgentRunRecord(
    string Id,
    string? SessionId,
    string Cwd,
    int? AccountNumber,
    string? ConfigDir,
    string? Mode,
    string Prompt,
    string Status,
    long UpdatedMs,
    int Turns,
    int Continuations,
    string? StopCause,
    string? LastError)
{
    /// <summary>Can be handed to a resume: right status, and a session to re-attach.</summary>
    public bool IsResumable =>
        Status is "interrupted" or "failed" or "cancelled" && !string.IsNullOrEmpty(SessionId);

    public bool IsLive => Status == "running";

    /// <summary>First non-empty line of the prompt, bounded for a list row.</summary>
    public string Title
    {
        get
        {
            string first = Prompt
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0) ?? "";
            return first.Length <= 80 ? first : string.Concat(first.AsSpan(0, 79), "…");
        }
    }

    public DateTime UpdatedLocal =>
        DateTimeOffset.FromUnixTimeMilliseconds(UpdatedMs).LocalDateTime;

    public static AgentRunRecord? FromJson(JsonNode? n)
    {
        if (n?["id"]?.GetValue<string>() is not { Length: > 0 } id) return null;
        return new AgentRunRecord(
            id,
            n["sessionId"]?.GetValue<string>(),
            n["cwd"]?.GetValue<string>() ?? "",
            n["accountNumber"]?.GetValue<int>(),
            n["configDir"]?.GetValue<string>(),
            n["mode"]?.GetValue<string>(),
            n["prompt"]?.GetValue<string>() ?? "",
            n["status"]?.GetValue<string>() ?? "running",
            n["updatedMs"]?.GetValue<long>() ?? 0,
            n["turns"]?.GetValue<int>() ?? 0,
            n["continuations"]?.GetValue<int>() ?? 0,
            n["stopCause"]?.GetValue<string>(),
            n["lastError"]?.GetValue<string>());
    }
}

/// <summary>Reads and writes the engine's supervised-run journal.</summary>
internal sealed class AgentRunStore(Engine engine)
{
    private readonly Engine _engine = engine;

    /// <summary>
    /// Every journaled run, newest first.
    /// </summary>
    /// <remarks>
    /// The engine reaps records whose owning process is gone as part of this
    /// call, so a run cut short by a crash is already reported as interrupted
    /// rather than as a run that is somehow still going.
    /// </remarks>
    public IReadOnlyList<AgentRunRecord> List()
    {
        try
        {
            var v = _engine.Call("agent_run_list");
            if (v["runs"] is not JsonArray arr) return [];
            return [.. arr.Select(AgentRunRecord.FromJson).OfType<AgentRunRecord>()];
        }
        catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
        {
            // An engine without the journal is a feature that is simply absent,
            // not a reason to fail the window that asked. A disposed one means
            // the app is on its way out, which is not a reason either.
            return [];
        }
    }

    public IReadOnlyList<AgentRunRecord> Resumable() => [.. List().Where(r => r.IsResumable)];

    /// <summary>Record the current state of a run.</summary>
    public void Save(
        string id,
        string? sessionId,
        string cwd,
        int? accountNumber,
        string? configDir,
        string? mode,
        string prompt,
        string status,
        JsonObject? policy = null,
        int turns = 0,
        int continuations = 0,
        string? stopCause = null,
        string? lastError = null,
        long? createdMs = null)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = new JsonObject
        {
            ["id"] = id,
            ["cwd"] = cwd,
            ["prompt"] = prompt,
            ["status"] = status,
            // The owner is this process; the engine uses it to tell a live run
            // from one whose app died.
            ["ownerPid"] = Environment.ProcessId,
            ["createdMs"] = createdMs ?? now,
            ["updatedMs"] = now,
            ["turns"] = turns,
            ["continuations"] = continuations,
        };
        if (sessionId is { Length: > 0 }) payload["sessionId"] = sessionId;
        if (accountNumber is { } n) payload["accountNumber"] = n;
        if (configDir is { Length: > 0 }) payload["configDir"] = configDir;
        if (mode is { Length: > 0 }) payload["mode"] = mode;
        if (policy is not null) payload["policy"] = policy.DeepClone();
        if (stopCause is { Length: > 0 }) payload["stopCause"] = stopCause;
        if (lastError is { Length: > 0 }) payload["lastError"] = lastError;

        try
        {
            _engine.Call("agent_run_upsert", payload);
        }
        catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
        {
            // Journalling is a recovery aid. Losing a write must never take the
            // run itself down with it — least of all during shutdown, which is
            // exactly when the engine may already be gone.
        }
    }

    /// <summary>
    /// Correct a run's outcome without restating the rest of the record.
    /// </summary>
    /// <remarks>
    /// <see cref="Save"/> replaces a record wholesale, so a caller that only
    /// knows the new status would erase the account and profile by omitting
    /// them. Verification is exactly such a caller: it learns whether the run
    /// achieved anything long after it has stopped tracking how the run was
    /// launched.
    /// </remarks>
    public void Patch(string id, string? status = null, string? stopCause = null, string? lastError = null)
    {
        var payload = new JsonObject { ["id"] = id };
        if (status is { Length: > 0 }) payload["status"] = status;
        if (stopCause is { Length: > 0 }) payload["stopCause"] = stopCause;
        if (lastError is { Length: > 0 }) payload["lastError"] = lastError;

        try
        {
            _engine.Call("agent_run_patch", payload);
        }
        catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
        {
            // Same reasoning as Save: journalling is a recovery aid, and losing
            // a write must never take the run down with it.
        }
    }

    public void Remove(string id)
    {
        try
        {
            _engine.Call("agent_run_remove", new { id });
        }
        catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
        {
            // Same reasoning as Save.
        }
    }
}
