using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>What a cleanup would do, as the dialogs read it.</summary>
/// <param name="DeletableIds">
/// The ids the user is shown. Applying passes them back so a session that
/// starts to qualify between preview and confirm is never deleted unseen.
/// </param>
internal sealed record CleanupPlanView(
    int DeletableCount,
    long DeletableBytes,
    int BlockedCount,
    long BlockedBytes,
    int RunRecords,
    IReadOnlyList<string> DeletableIds,
    IReadOnlyDictionary<string, (int Count, long Bytes)> ByReason)
{
    public static readonly CleanupPlanView Empty =
        new(0, 0, 0, 0, 0, [], new Dictionary<string, (int, long)>());

    /// <summary>Every item in the plan, blocked ones included.</summary>
    public IReadOnlyList<CleanupItemView> Items { get; init; } = [];

    public (int Count, long Bytes) For(string reason) =>
        ByReason.TryGetValue(reason, out var t) ? t : (0, 0);

    /// <summary>
    /// The part of this plan an item filter keeps, with its totals recomputed.
    /// </summary>
    /// <remarks>
    /// An item two conditions both select is one item here, so the totals of a
    /// union are never the sum of its parts' figures.
    /// </remarks>
    public CleanupPlanView Where(Func<CleanupItemView, bool> keep)
    {
        var kept = Items.Where(keep).ToList();
        var deletable = kept.Where(i => !i.Blocked).ToList();
        var blocked = kept.Where(i => i.Blocked).ToList();
        var byReason = new Dictionary<string, (int, long)>();
        foreach (var item in deletable)
        {
            foreach (var reason in item.Reasons)
            {
                var (count, bytes) = byReason.TryGetValue(reason, out var t) ? t : (0, 0L);
                byReason[reason] = (count + 1, bytes + item.Bytes);
            }
        }
        return new CleanupPlanView(
            deletable.Count,
            deletable.Sum(i => i.Bytes),
            blocked.Count,
            blocked.Sum(i => i.Bytes),
            deletable.Sum(i => i.RunRecords),
            [.. deletable.Select(i => i.Id)],
            byReason)
        {
            Items = kept,
        };
    }
}

/// <summary>One session or leftover in a plan.</summary>
internal sealed record CleanupItemView(
    string Id,
    long Bytes,
    bool Blocked,
    IReadOnlyList<string> Reasons,
    int RunRecords);

internal sealed record CleanupOutcomeView(
    int DeletedCount,
    long FreedBytes,
    int SkippedCount,
    int RemovedRuns,
    IReadOnlyList<string> Failures);

/// <summary>Conditions for a batch cleanup; null or false leaves a rule off.</summary>
internal sealed record CleanupRules(
    int? OlderThanDays,
    int? LargerThanMb,
    bool MissingDirectory,
    bool RemovedAccounts,
    bool Orphans);

internal sealed record RetentionView(int? Days, int DefaultDays, int MaxDays, string? Error)
{
    public int Effective => Days ?? DefaultDays;
}

/// <summary>A config home that holds state for a directory.</summary>
/// <param name="ConfigDir">
/// <c>CLAUDE_CONFIG_DIR</c> for the purge; null is the default home, which must
/// run with the variable removed rather than inherited.
/// </param>
internal sealed record PurgeRootView(string? ConfigDir, int? ProfileNumber, int Sessions, long Bytes, bool Registered);

internal sealed record PurgePreflightView(
    IReadOnlyList<PurgeRootView> Roots,
    int LiveSessions,
    int RunningRuns,
    IReadOnlyList<string> RunIds);

/// <summary>
/// The shell side of history cleanup: building requests, reading plans, and
/// running <c>claude project purge</c>.
/// </summary>
/// <remarks>
/// The engine owns every decision about what may be deleted. This class only
/// names sessions and conditions, and adds the one thing the engine cannot see:
/// supervised runs live in this process, which Claude Code writes no pid file
/// for.
/// </remarks>
internal static class HistoryCleanup
{
    public const string ReasonOlderThan = "olderThan";
    public const string ReasonLargerThan = "largerThan";
    public const string ReasonMissingDirectory = "missingDirectory";
    public const string ReasonRemovedAccount = "removedAccount";
    public const string ReasonOrphan = "orphan";

    public static JsonObject ForSessions(IEnumerable<(string TranscriptDir, string Id)> sessions)
    {
        var list = new JsonArray();
        foreach (var (dir, id) in sessions)
            list.Add(new JsonObject { ["transcriptDir"] = dir, ["id"] = id });
        return new JsonObject { ["sessions"] = list };
    }

    public static JsonObject ForDirectories(IEnumerable<string> transcriptDirs) =>
        new() { ["transcriptDirs"] = new JsonArray([.. transcriptDirs.Select(d => (JsonNode?)d)]) };

    public static JsonObject ForRules(CleanupRules rules)
    {
        var r = new JsonObject
        {
            ["missingDirectory"] = rules.MissingDirectory,
            ["removedAccounts"] = rules.RemovedAccounts,
            ["orphans"] = rules.Orphans,
        };
        if (rules.OlderThanDays is > 0 and var days) r["olderThanDays"] = days;
        if (rules.LargerThanMb is > 0 and var mb) r["largerThanMb"] = mb;
        return new JsonObject { ["rules"] = r };
    }

    public static CleanupPlanView Plan(Engine engine, JsonObject request) =>
        ParsePlan(engine.Call("history_cleanup_plan", WithProtection(request, onlyIds: null)));

    public static CleanupOutcomeView Apply(Engine engine, JsonObject request, IEnumerable<string> onlyIds) =>
        ParseOutcome(engine.Call("history_cleanup_apply", WithProtection(request, onlyIds)));

    /// <summary>A copy of the request carrying the live-run guard, and the shown ids when applying.</summary>
    private static JsonObject WithProtection(JsonObject request, IEnumerable<string>? onlyIds)
    {
        var copy = (JsonObject)request.DeepClone();
        copy["protectIds"] = new JsonArray([.. ProtectedIds().Select(id => (JsonNode?)id)]);
        if (onlyIds is not null)
            copy["onlyIds"] = new JsonArray([.. onlyIds.Select(id => (JsonNode?)id)]);
        return copy;
    }

    /// <summary>Sessions supervised by this process, which have no pid file of their own.</summary>
    private static IEnumerable<string> ProtectedIds() =>
        BackgroundRuns.Active
            .Select(r => r.Runner.SessionId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!);

    public static CleanupPlanView ParsePlan(JsonNode? node)
    {
        if (node is null) return CleanupPlanView.Empty;
        var ids = new List<string>();
        var parsed = new List<CleanupItemView>();
        if (node["items"] is JsonArray items)
        {
            foreach (var item in items)
            {
                if (item?["id"]?.GetValue<string>() is not { Length: > 0 } id) continue;
                bool blocked = item["blocked"] is not null;
                if (!blocked) ids.Add(id);
                parsed.Add(new CleanupItemView(
                    id,
                    Long(item["bytes"]),
                    blocked,
                    [.. (item["reasons"] as JsonArray ?? []).Select(r => r?.GetValue<string>()).OfType<string>()],
                    (item["runIds"] as JsonArray)?.Count ?? 0));
            }
        }

        var byReason = new Dictionary<string, (int, long)>();
        if (node["byReason"] is JsonObject reasons)
        {
            foreach (var (key, value) in reasons)
                byReason[key] = (Int(value?["count"]), Long(value?["bytes"]));
        }

        return new CleanupPlanView(
            Int(node["deletable"]?["count"]),
            Long(node["deletable"]?["bytes"]),
            Int(node["blocked"]?["count"]),
            Long(node["blocked"]?["bytes"]),
            Int(node["runRecords"]),
            ids,
            byReason)
        {
            Items = parsed,
        };
    }

    public static CleanupOutcomeView ParseOutcome(JsonNode? node)
    {
        var failures = new List<string>();
        if (node?["failures"] is JsonArray arr)
        {
            foreach (var f in arr)
            {
                if (f is null) continue;
                failures.Add($"{f["path"]?.GetValue<string>()}: {f["error"]?.GetValue<string>()}");
            }
        }
        return new CleanupOutcomeView(
            Int(node?["deletedCount"]),
            Long(node?["freedBytes"]),
            Int(node?["skippedCount"]),
            Int(node?["removedRuns"]),
            failures);
    }

    public static RetentionView GetRetention(Engine engine) => ParseRetention(engine.Call("history_retention_get"));

    /// <summary>Set <c>cleanupPeriodDays</c>; null returns it to Claude Code's default.</summary>
    public static RetentionView SetRetention(Engine engine, int? days) =>
        ParseRetention(engine.Call(
            "history_retention_set",
            new JsonObject { ["days"] = days is { } d ? JsonValue.Create(d) : null }));

    public static RetentionView ParseRetention(JsonNode? node) =>
        new(
            node?["days"] is JsonValue v && v.TryGetValue<int>(out var days) ? days : null,
            node?["defaultDays"] is null ? 30 : Int(node["defaultDays"]),
            node?["maxDays"] is null ? 3650 : Int(node["maxDays"]),
            node?["error"]?.GetValue<string>());

    public static PurgePreflightView Preflight(Engine engine, string path) =>
        ParsePreflight(engine.Call("history_purge_preflight", new { path }));

    public static PurgePreflightView ParsePreflight(JsonNode? node)
    {
        var roots = new List<PurgeRootView>();
        if (node?["roots"] is JsonArray arr)
        {
            foreach (var r in arr)
            {
                if (r is null) continue;
                roots.Add(new PurgeRootView(
                    r["configDir"]?.GetValue<string>(),
                    r["profileNumber"]?.GetValue<int>(),
                    Int(r["sessions"]),
                    Long(r["bytes"]),
                    r["registered"]?.GetValue<bool>() ?? false));
            }
        }
        var runIds = (node?["runIds"] as JsonArray ?? [])
            .Select(n => n?.GetValue<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList();
        return new PurgePreflightView(roots, Int(node?["liveSessions"]), Int(node?["runningRuns"]), runIds);
    }

    /// <summary>
    /// Run <c>claude project purge &lt;path&gt; --yes</c> against one config home.
    /// </summary>
    /// <remarks>
    /// Delegated rather than reimplemented: what a project's state consists of
    /// — prompt history lines, the <c>.claude.json</c> entry under whichever
    /// spelling of the path, task lists — is Claude Code's format to change, and
    /// its own command follows those changes. The engine's preflight has already
    /// refused a directory anything is still running in, which the command
    /// itself does not check.
    /// </remarks>
    /// <returns>Null on success, otherwise what went wrong.</returns>
    public static async Task<string?> RunPurgeAsync(string claudeExe, string path, string? configDir)
    {
        var psi = new ProcessStartInfo(claudeExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        psi.ArgumentList.Add("project");
        psi.ArgumentList.Add("purge");
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("--yes");
        if (configDir is null)
            psi.Environment.Remove("CLAUDE_CONFIG_DIR");
        else
            psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment.Remove("FORCE_COLOR");

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return Loc.T("history.purge.failed.start");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return Loc.T("history.purge.failed.timeout");
            }
            if (process.ExitCode == 0) return null;
            string output = StripAnsi($"{await stdout}\n{await stderr}").Trim();
            return output.Length > 0 ? output : Loc.T("history.purge.failed.exit", process.ExitCode);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string StripAnsi(string text) => Regex.Replace(text, @"\x1B\[[0-9;?]*[A-Za-z]", "");

    /// <summary>Human size: 1.5 GB, 12.3 MB, 640 KB, 12 B.</summary>
    public static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB"
        : bytes >= 1L << 10 ? $"{bytes / 1024.0:0} KB"
        : $"{bytes} B";

    private static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    /// <summary>
    /// Directories only registered with Claude Code — every one, or just those named.
    /// </summary>
    public static RegistryPlanView PlanEmptyDirectories(Engine engine, IEnumerable<string>? paths = null) =>
        ParseRegistryPlan(engine.Call("history_registry_plan", RegistryRequest(paths)));

    /// <summary>Remove the registry entries of the directories the user was shown.</summary>
    public static RegistryOutcomeView RemoveEmptyDirectories(Engine engine, IEnumerable<string> paths) =>
        ParseRegistryOutcome(engine.Call("history_registry_remove", RegistryRequest(paths)));

    private static JsonObject RegistryRequest(IEnumerable<string>? paths)
    {
        var request = new JsonObject();
        if (paths is not null)
            request["paths"] = new JsonArray([.. paths.Select(p => (JsonNode?)p)]);
        return request;
    }

    public static RegistryPlanView ParseRegistryPlan(JsonNode? node) =>
        new(
            StringList(node?["removablePaths"]),
            Int(node?["liveDirectories"]),
            Int(node?["withMcp"]),
            node?["backupDir"]?.GetValue<string>());

    public static RegistryOutcomeView ParseRegistryOutcome(JsonNode? node) =>
        new(
            Int(node?["removedDirectories"]),
            Int(node?["removedEntries"]),
            Int(node?["skippedDirectories"]),
            StringList(node?["backups"]),
            StringList(node?["failures"]));

    private static List<string> StringList(JsonNode? node) =>
        (node as JsonArray ?? [])
            .Select(n => n?.GetValue<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList();
}

/// <summary>Registered-only directories a removal would take off the list.</summary>
/// <param name="Paths">One per directory, however many spellings its entries use.</param>
/// <param name="LiveDirectories">Left alone because Claude Code is running there.</param>
/// <param name="WithMcp">Of <paramref name="Paths"/>, how many carry MCP server settings.</param>
/// <param name="BackupDir">Where the config file is copied before it is changed.</param>
internal sealed record RegistryPlanView(
    IReadOnlyList<string> Paths,
    int LiveDirectories,
    int WithMcp,
    string? BackupDir);

internal sealed record RegistryOutcomeView(
    int RemovedDirectories,
    int RemovedEntries,
    int SkippedDirectories,
    IReadOnlyList<string> Backups,
    IReadOnlyList<string> Failures);
