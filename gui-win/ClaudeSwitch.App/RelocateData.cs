using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

internal sealed record RelocateTreeView(
    string Id,
    string Source,
    long Bytes,
    bool Exists,
    bool Linked,
    string? LinkTarget,
    string BackupPath,
    bool BackupExists,
    long BackupBytes);

internal sealed record RelocateScanView(
    IReadOnlyList<RelocateTreeView> Trees,
    long TotalBytes,
    int LiveSessions,
    string? ClaudeConfigDir,
    long ClaudeJsonBytes,
    bool Relocated)
{
    public static readonly RelocateScanView Empty =
        new([], 0, 0, null, 0, false);
}

internal sealed record RelocatePlanView(
    string DestRoot,
    long? DestFreeBytes,
    bool SameVolume,
    int LiveSessions,
    long CopyBytes,
    int Links,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Problems,
    IReadOnlyList<RelocatePlanTreeView> Trees)
{
    public static RelocatePlanView Invalid(string problem) =>
        new("", null, false, 0, 0, 0, [], [problem], []);

    public bool CanApply => Problems.Count == 0 && Trees.Any(t => !t.Skip);
}

internal sealed record RelocateRestoreView(
    IReadOnlyList<string> Restored,
    IReadOnlyList<string> Stale,
    IReadOnlyList<string> Leftover);

internal sealed record RelocatePlanTreeView(
    string Id,
    string Source,
    string Dest,
    string Backup,
    long Bytes,
    bool Skip);

/// <summary>Shell side of moving Claude data to another drive via directory links.</summary>
internal static class RelocateData
{
    public static RelocateScanView Scan(Engine engine) =>
        ParseScan(engine.Call("relocate_scan"));

    public static RelocatePlanView Plan(Engine engine, string dest)
    {
        dest = dest.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(dest) || !Path.IsPathRooted(dest))
            return RelocatePlanView.Invalid("dest-not-absolute");
        return ParsePlan(engine.Call("relocate_plan", new { destRoot = dest }));
    }

    public static void Apply(Engine engine, string dest) =>
        engine.Call("relocate_apply", new { destRoot = dest.Trim().Trim('"') });

    public static RelocateRestoreView Restore(Engine engine)
    {
        var node = engine.Call("relocate_restore");
        return new RelocateRestoreView(
            Strings(node["restored"]), Strings(node["stale"]), Strings(node["leftover"]));
    }

    public static long DeleteBackups(Engine engine)
    {
        var node = engine.Call("relocate_delete_backups");
        return node["bytes"]?.GetValue<long>() ?? 0;
    }

    public static string TreeTitle(string id) => id switch
    {
        "claude" => Loc.T("relocate.tree.claude"),
        "swap" => Loc.T("relocate.tree.swap"),
        _ => id,
    };

    public static string ProblemText(string code, RelocatePlanView plan) => code switch
    {
        "drive-root" => Loc.T("relocate.problem.drive-root"),
        "dest-inside-source" => Loc.T("relocate.problem.dest-inside-source"),
        "dest-not-absolute" => Loc.T("relocate.problem.dest-not-absolute"),
        "dest-not-empty" => Loc.T("relocate.problem.dest-not-empty", PathList(plan, t => t.Dest, NonEmpty)),
        "dest-unreachable" => Loc.T("relocate.problem.dest-unreachable"),
        "dest-not-local" => Loc.T("relocate.problem.dest-not-local"),
        "dest-filesystem" => Loc.T("relocate.problem.dest-filesystem"),
        "backup-exists" => Loc.T("relocate.problem.backup-exists", PathList(plan, t => t.Backup, Present)),
        "linked-elsewhere" => Loc.T("relocate.problem.linked-elsewhere"),
        "link-unreadable" => Loc.T("relocate.problem.link-unreadable"),
        "symlink-privilege" => Loc.T("relocate.problem.symlink-privilege"),
        "not-enough-space" => Loc.T(
            "relocate.problem.no-space",
            HistoryCleanup.FormatBytes(plan.CopyBytes),
            HistoryCleanup.FormatBytes(plan.DestFreeBytes ?? 0)),
        "live-sessions" => Loc.T("relocate.problem.live-sessions"),
        "nothing" => Loc.T("relocate.problem.nothing"),
        _ => code,
    };

    public static string WarningText(string code, RelocatePlanView plan) => code switch
    {
        "same-volume" => Loc.T("relocate.sameVolume"),
        "links" => Loc.T("relocate.warn.links", plan.Links),
        _ => code,
    };

    /// <summary>
    /// Readable text for a failed relocate call. The engine reports
    /// <c>relocate: &lt;code&gt; [detail]</c> inside its error message.
    /// </summary>
    public static string Describe(Exception ex, RelocatePlanView plan)
    {
        if (ex is not EngineException ee) return ex.Message;
        string? code = null;
        string? message = null;
        try
        {
            var err = JsonNode.Parse(ee.Json)?["error"];
            code = err?["code"]?.GetValue<string>();
            message = err?["message"]?.GetValue<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            // Fall through to the exception text.
        }
        if (string.IsNullOrEmpty(message)) return ex.Message;

        const string tag = "relocate: ";
        int at = message.IndexOf(tag, StringComparison.Ordinal);
        if (at < 0)
        {
            return code == "session-in-use"
                ? Loc.T("relocate.problem.live-sessions")
                : message;
        }
        string rest = message[(at + tag.Length)..];
        int space = rest.IndexOf(' ', StringComparison.Ordinal);
        string relocateCode = space < 0 ? rest : rest[..space];
        string detail = space < 0 ? "" : rest[(space + 1)..].Trim();
        return relocateCode switch
        {
            "in-use" => Loc.T("relocate.error.in-use", detail),
            "unreachable" => Loc.T("relocate.error.unreachable", detail),
            "not-linked" => Loc.T("relocate.error.not-linked"),
            "no-backup" => Loc.T("relocate.error.no-backup"),
            "not-enough-space" => NoSpace(detail),
            _ => ProblemText(relocateCode, plan),
        };
    }

    /// <summary>"need N have M" from the engine, in readable sizes.</summary>
    private static string NoSpace(string detail)
    {
        var parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long need = 0, have = 0;
        for (int i = 0; i + 1 < parts.Length; i++)
        {
            if (parts[i] == "need") _ = long.TryParse(parts[i + 1], out need);
            if (parts[i] == "have") _ = long.TryParse(parts[i + 1], out have);
        }
        return Loc.T(
            "relocate.problem.no-space",
            HistoryCleanup.FormatBytes(need),
            HistoryCleanup.FormatBytes(have));
    }

    private static string PathList(
        RelocatePlanView plan,
        Func<RelocatePlanTreeView, string> pick,
        Func<string, bool> keep)
    {
        var paths = plan.Trees.Where(t => !t.Skip).Select(pick).Where(keep).ToList();
        return paths.Count == 0 ? plan.DestRoot : string.Join(", ", paths);
    }

    private static bool Present(string path) => Directory.Exists(path) || File.Exists(path);

    private static bool NonEmpty(string path)
    {
        try
        {
            return File.Exists(path)
                || (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static RelocateScanView ParseScan(JsonNode? node)
    {
        if (node is null) return RelocateScanView.Empty;
        var trees = new List<RelocateTreeView>();
        if (node["trees"] is JsonArray arr)
        {
            foreach (var t in arr)
            {
                if (t is null) continue;
                trees.Add(new RelocateTreeView(
                    t["id"]?.GetValue<string>() ?? "",
                    t["source"]?.GetValue<string>() ?? "",
                    Long(t["bytes"]),
                    t["exists"]?.GetValue<bool>() ?? false,
                    t["linked"]?.GetValue<bool>() ?? false,
                    t["linkTarget"]?.GetValue<string>(),
                    t["backupPath"]?.GetValue<string>() ?? "",
                    t["backupExists"]?.GetValue<bool>() ?? false,
                    Long(t["backupBytes"])));
            }
        }
        return new RelocateScanView(
            trees,
            Long(node["totalBytes"]),
            Int(node["liveSessions"]),
            node["claudeConfigDir"]?.GetValue<string>(),
            Long(node["claudeJsonBytes"]),
            node["relocated"]?.GetValue<bool>() ?? false);
    }

    public static RelocatePlanView ParsePlan(JsonNode? node)
    {
        if (node is null) return RelocatePlanView.Invalid("nothing");
        var trees = new List<RelocatePlanTreeView>();
        if (node["trees"] is JsonArray arr)
        {
            foreach (var t in arr)
            {
                if (t is null) continue;
                trees.Add(new RelocatePlanTreeView(
                    t["id"]?.GetValue<string>() ?? "",
                    t["source"]?.GetValue<string>() ?? "",
                    t["dest"]?.GetValue<string>() ?? "",
                    t["backup"]?.GetValue<string>() ?? "",
                    Long(t["bytes"]),
                    t["skip"]?.GetValue<bool>() ?? false));
            }
        }
        return new RelocatePlanView(
            node["destRoot"]?.GetValue<string>() ?? "",
            node["destFreeBytes"] is JsonValue v && v.TryGetValue<long>(out var free) ? free : null,
            node["sameVolume"]?.GetValue<bool>() ?? false,
            Int(node["liveSessions"]),
            Long(node["copyBytes"]),
            Int(node["links"]),
            Strings(node["warnings"]),
            Strings(node["problems"]),
            trees);
    }

    private static List<string> Strings(JsonNode? node)
    {
        var list = new List<string>();
        if (node is JsonArray arr)
        {
            foreach (var x in arr)
            {
                var s = x?.GetValue<string>();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
        }
        return list;
    }

    private static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;
}
