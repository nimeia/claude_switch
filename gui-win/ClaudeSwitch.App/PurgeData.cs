using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

internal sealed record PurgeItemView(
    string Id,
    string Group,
    string Path,
    long Bytes,
    bool Exists,
    string Kind);

internal sealed record PurgeGroupView(
    string Id,
    bool Required,
    long Bytes,
    IReadOnlyList<PurgeItemView> Items);

internal sealed record PurgeScanView(
    string InstallKind,
    bool ClaudeRunning,
    int LiveSessions,
    int ImportedAccounts,
    IReadOnlyList<PurgeGroupView> Groups,
    IReadOnlyList<string>? AppProblems = null)
{
    public static readonly PurgeScanView Empty = new("none", false, 0, 0, []);

    public PurgeGroupView? Group(string id) =>
        Groups.FirstOrDefault(g => g.Id == id);
}

internal sealed record PurgePlanView(
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<PurgeItemView> Items,
    long TotalBytes,
    int LiveSessions,
    bool ClaudeRunning,
    int ImportedAccounts)
{
    public static PurgePlanView Invalid(string problem) =>
        new([problem], [], [], 0, 0, false, 0);

    public bool CanApply => Problems.Count == 0 && Items.Count > 0;
}

internal sealed record PurgeOptionsView(
    bool RemoveIdeExtension,
    bool RemoveProjectLocals,
    bool RemoveDesktopNested,
    bool RemoveThirdPartyShells,
    bool RemoveManaged,
    bool RemoveImportedAccounts,
    bool RemoveDesktopData = false,
    bool RemoveBrowserExtension = false);

internal sealed record PurgeOutcomeView(
    IReadOnlyList<(string Id, string Path)> Deleted,
    IReadOnlyList<(string Id, string Path, string Reason)> Skipped,
    IReadOnlyList<(string Id, string Path, string Reason)> Failed,
    long Bytes);

/// <summary>Shell side of uninstalling Claude Code via the engine whitelist.</summary>
internal static class PurgeData
{
    public static PurgeScanView Scan(Engine engine) =>
        ParseScan(engine.Call("purge_scan"));

    public static PurgePlanView Plan(Engine engine, PurgeOptionsView opts) =>
        ParsePlan(engine.Call("purge_plan", Wire(opts)));

    public static PurgeOutcomeView Apply(Engine engine, PurgeOptionsView opts) =>
        ParseOutcome(engine.Call("purge_apply", Wire(opts)));

    /// <summary>Preview only: selecting options never walks the disk. Apply revalidates in Rust.</summary>
    public static PurgePlanView PlanFromScan(PurgeScanView scan, PurgeOptionsView opts)
    {
        bool Selected(PurgeGroupView group) => group.Required || group.Id switch
        {
            "ide" => opts.RemoveIdeExtension,
            "project-locals" => opts.RemoveProjectLocals,
            "desktop" => opts.RemoveDesktopNested,
            "desktop-data" => opts.RemoveDesktopData,
            "browser" => opts.RemoveBrowserExtension,
            "third-party" => opts.RemoveThirdPartyShells,
            "managed" => opts.RemoveManaged,
            "accounts" => opts.RemoveImportedAccounts,
            _ => false,
        };
        static int Order(string group) => group switch
        {
            "runtime" => 0, "project-locals" => 1,
            "desktop" or "desktop-data" => 2, "browser" or "third-party" => 3,
            "managed" => 4, "ide" => 5, "program" => 6, "accounts" => 7, _ => 9,
        };
        var selected = scan.Groups.Where(Selected).SelectMany(g => g.Items)
            .Where(i => i.Exists).OrderBy(i => Order(i.Group))
            .ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        var roots = selected.Where(i => i.Kind == "dir")
            .Select(i => i.Path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).ToList();
        var items = selected.Where(i => i.Kind is "registry" or "json-entries" ||
                !roots.Any(root => i.Path.StartsWith(root, StringComparison.Ordinal)))
            .DistinctBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
        bool Has(string group) => items.Any(i => i.Group == group);
        var problems = new List<string>();
        if (scan.LiveSessions > 0) problems.Add("live-sessions");
        if (scan.ClaudeRunning) problems.Add("claude-running");
        if (items.Count == 0) problems.Add("nothing");
        foreach (var problem in scan.AppProblems ?? [])
        {
            bool relevant = problem switch
            {
                "ide-running" => Has("ide"),
                "desktop-running" => Has("desktop") || Has("desktop-data"),
                "browser-running" => Has("browser"),
                "process-check-failed" => Has("ide") || Has("desktop") || Has("desktop-data") || Has("browser"),
                _ => true,
            };
            if (relevant) problems.Add(problem);
        }
        var warnings = new List<string>();
        if (scan.ImportedAccounts > 0 && !opts.RemoveImportedAccounts)
            warnings.Add("accounts-kept-identity-remains");
        if (scan.Group("desktop-data") is { Items.Count: > 0 } && !opts.RemoveDesktopData)
            warnings.Add("desktop-left-installed");
        if (Has("desktop-data")) warnings.Add("desktop-data-login-removed");
        if (Has("browser")) warnings.Add("browser-sync-may-restore");
        if (items.Any(i => i.Id is "global-config" or "config-home"))
            warnings.Add("machine-id-may-regenerate");
        return new(problems.Distinct().Order(StringComparer.Ordinal).ToList(), warnings, items,
            items.Sum(i => i.Bytes), scan.LiveSessions, scan.ClaudeRunning, scan.ImportedAccounts);
    }

    public static string GroupTitle(string id) => id switch
    {
        "program" => Loc.T("purge.group.program"),
        "runtime" => Loc.T("purge.group.runtime"),
        "ide" => Loc.T("purge.group.ide"),
        "project-locals" => Loc.T("purge.group.project-locals"),
        "desktop" => Loc.T("purge.group.desktop"),
        "desktop-data" => Loc.T("purge.group.desktop-data"),
        "browser" => Loc.T("purge.group.browser"),
        "third-party" => Loc.T("purge.group.third-party"),
        "managed" => Loc.T("purge.group.managed"),
        "accounts" => Loc.T("purge.group.accounts"),
        _ => id,
    };

    public static string ProblemText(string code, PurgePlanView plan) => code switch
    {
        "live-sessions" => Loc.T("purge.warn.live-sessions", plan.LiveSessions),
        "claude-running" => Loc.T("purge.warn.claude-running"),
        "desktop-running" => Loc.T("purge.warn.desktop-running"),
        "browser-running" => Loc.T("purge.warn.browser-running"),
        "ide-running" => Loc.T("purge.warn.ide-running"),
        "process-check-failed" => Loc.T("purge.warn.process-check-failed"),
        "nothing" => Loc.T("purge.warn.nothing"),
        _ => code,
    };

    public static string WarningText(string code) => code switch
    {
        "accounts-kept-identity-remains" => Loc.T("purge.warn.accounts-kept-identity-remains"),
        "desktop-left-installed" => Loc.T("purge.warn.desktop-left-installed"),
        "desktop-data-login-removed" => Loc.T("purge.warn.desktop-data-login-removed"),
        "browser-sync-may-restore" => Loc.T("purge.warn.browser-sync-may-restore"),
        "machine-id-may-regenerate" => Loc.T("purge.warn.machine-id-may-regenerate"),
        _ => code,
    };

    public static string FailureText(string reason) => reason switch
    {
        "permission-denied" => Loc.T("purge.error.permission"),
        "file-in-use" => Loc.T("purge.error.in-use"),
        "admin-cancelled" => Loc.T("purge.error.admin-cancelled"),
        "admin-failed" => Loc.T("purge.error.admin-failed"),
        "claude-running" => Loc.T("purge.warn.claude-running"),
        _ => reason,
    };

    public static PurgeScanView ParseScan(JsonNode? node)
    {
        if (node is null) return PurgeScanView.Empty;
        var groups = new List<PurgeGroupView>();
        if (node["groups"] is JsonArray arr)
        {
            foreach (var g in arr)
            {
                if (g is null) continue;
                groups.Add(new PurgeGroupView(
                    g["id"]?.GetValue<string>() ?? "",
                    g["required"]?.GetValue<bool>() ?? false,
                    Long(g["bytes"]),
                    Items(g["items"])));
            }
        }
        return new PurgeScanView(
            node["installKind"]?.GetValue<string>() ?? "none",
            node["claudeRunning"]?.GetValue<bool>() ?? false,
            Int(node["liveSessions"]),
            Int(node["importedAccounts"]),
            groups,
            Strings(node["appProblems"]));
    }

    public static PurgePlanView ParsePlan(JsonNode? node)
    {
        if (node is null) return PurgePlanView.Invalid("nothing");
        return new PurgePlanView(
            Strings(node["problems"]),
            Strings(node["warnings"]),
            Items(node["items"]),
            Long(node["totalBytes"]),
            Int(node["liveSessions"]),
            node["claudeRunning"]?.GetValue<bool>() ?? false,
            Int(node["importedAccounts"]));
    }

    public static PurgeOutcomeView ParseOutcome(JsonNode? node)
    {
        if (node is null) return new PurgeOutcomeView([], [], [], 0);
        return new PurgeOutcomeView(
            Actions(node["deleted"]).Select(a => (a.Id, a.Path)).ToList(),
            Actions(node["skipped"]).Select(a => (a.Id, a.Path, a.Reason ?? "")).ToList(),
            Actions(node["failed"]).Select(a => (a.Id, a.Path, a.Reason ?? "")).ToList(),
            Long(node["bytes"]));
    }

    private static object Wire(PurgeOptionsView o) => new
    {
        removeIdeExtension = o.RemoveIdeExtension,
        removeProjectLocals = o.RemoveProjectLocals,
        removeDesktopNested = o.RemoveDesktopNested,
        removeThirdPartyShells = o.RemoveThirdPartyShells,
        removeManaged = o.RemoveManaged,
        removeImportedAccounts = o.RemoveImportedAccounts,
        removeDesktopData = o.RemoveDesktopData,
        removeBrowserExtension = o.RemoveBrowserExtension,
    };

    private static List<PurgeItemView> Items(JsonNode? node)
    {
        var list = new List<PurgeItemView>();
        if (node is not JsonArray arr) return list;
        foreach (var t in arr)
        {
            if (t is null) continue;
            list.Add(new PurgeItemView(
                t["id"]?.GetValue<string>() ?? "",
                t["group"]?.GetValue<string>() ?? "",
                t["path"]?.GetValue<string>() ?? "",
                Long(t["bytes"]),
                t["exists"]?.GetValue<bool>() ?? false,
                t["kind"]?.GetValue<string>() ?? ""));
        }
        return list;
    }

    private sealed record ActionRow(string Id, string Path, string? Reason);

    private static List<ActionRow> Actions(JsonNode? node)
    {
        var list = new List<ActionRow>();
        if (node is not JsonArray arr) return list;
        foreach (var t in arr)
        {
            if (t is null) continue;
            list.Add(new ActionRow(
                t["id"]?.GetValue<string>() ?? "",
                t["path"]?.GetValue<string>() ?? "",
                t["reason"]?.GetValue<string>()));
        }
        return list;
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
