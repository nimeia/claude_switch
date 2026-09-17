using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// Opening a terminal that runs one specific account, alongside the default login.
///
/// The core prepares a per-account profile directory and hands back the value for
/// <c>CLAUDE_CONFIG_DIR</c>; this class is only the shell around that — launch and
/// report. Directory choice lives in <see cref="SessionLaunchDialog"/> (recent
/// work dirs + continue/new). Everything that decides *what* the profile contains
/// lives in the core so the CLI-shaped rules stay in one place.
/// </summary>
internal static class SessionMode
{
    /// <summary>Where the last "open a terminal" browse landed, for the next one.</summary>
    private static string? s_lastDirectory;

    /// <summary>Outcome of a launch attempt, for the caller to display.</summary>
    internal sealed record Result(bool Launched, string? Problem, string? Note);

    /// <summary>Proxy and region the engine resolved for a launch.</summary>
    internal sealed record ResolvedProxy(
        string? Url,
        bool Scrub,
        Dictionary<string, string> Env,
        List<string> ScrubKeys);

    /// <summary>
    /// Prepare account <paramref name="id"/>'s profile and open a terminal in it.
    /// </summary>
    /// <remarks>
    /// Preparation can be slow the first time (it writes the profile), but never
    /// touches the network, so it stays on the caller's thread rather than
    /// introducing a progress dialog for a few milliseconds of I/O.
    /// </remarks>
    public static Result Launch(Engine engine, string id, string workingDirectory, string? sessionId = null)
    {
        JsonNode prepared;
        try
        {
            prepared = engine.Call("session_prepare", new { id });
        }
        catch (EngineException ex)
        {
            return new Result(false, Describe(ex), null);
        }

        // The core says "no profile needed" when the account already is the
        // default login — a second copy of one account's token would drift.
        bool useDefault = prepared["useDefaultLogin"]?.GetValue<bool>() ?? false;
        string? configDir = useDefault ? null : prepared["configDir"]?.GetValue<string>();
        if (!useDefault && string.IsNullOrEmpty(configDir))
        {
            return new Result(false, Loc.T("session.prepareFailed"), null);
        }

        var scrub = StringList(prepared["scrubEnv"]);
        var extra = StringMap(prepared["extraEnv"]);

        if (ClaudeCli.Resume(workingDirectory, sessionId, configDir, scrub, extra) is { } problem)
        {
            return new Result(false, problem, null);
        }

        return new Result(true, null, Note(prepared));
    }

    /// <summary>
    /// Resume under the default login, still injecting the machine (or current
    /// account) proxy so a Windows Terminal pane does not drop <c>HTTPS_PROXY</c>.
    /// </summary>
    public static string? ResumeDefault(Engine engine, string workingDirectory, string? sessionId = null)
    {
        Dictionary<string, string> extra = [];
        List<string> scrub = [];
        try
        {
            string? id = null;
            try
            {
                if (engine.Call("snapshot")["activeAccountNumber"] is JsonValue active)
                    id = active.GetValue<int>().ToString();
            }
            catch (EngineException)
            {
                // Snapshot is optional; machine proxy still applies.
            }
            var resolved = id is null
                ? engine.Call("proxy_resolve")
                : engine.Call("proxy_resolve", new { id });
            extra = StringMap(resolved["env"]);
            scrub = StringList(resolved["scrub"]);
        }
        catch (EngineException)
        {
            // Older engine without the method: inherit whatever we have.
        }
        return ClaudeCli.Resume(workingDirectory, sessionId, scrubEnv: scrub, extraEnv: extra);
    }

    /// <summary>Machine or per-account proxy for an ACP / adapter spawn.</summary>
    public static ResolvedProxy ResolveProxy(Engine engine, int? accountNumber)
    {
        try
        {
            var node = accountNumber is { } n
                ? engine.Call("proxy_resolve", new { id = n.ToString() })
                : engine.Call("proxy_resolve");
            return new(
                node["proxy"]?.GetValue<string>(),
                node["choice"]?.GetValue<string>() == "direct",
                StringMap(node["env"]),
                StringList(node["scrub"]));
        }
        catch (EngineException)
        {
            return new(null, false, [], []);
        }
    }

    internal static List<string> StringList(JsonNode? node) =>
        (node as JsonArray ?? [])
            .Select(n => n?.GetValue<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList();

    internal static Dictionary<string, string> StringMap(JsonNode? node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is not JsonObject obj) return map;
        foreach (var (key, value) in obj)
        {
            if (value?.GetValue<string>() is { Length: > 0 } s)
                map[key] = s;
        }
        return map;
    }

    /// <summary>
    /// The engine's own message, unwrapped from its JSON envelope.
    /// </summary>
    /// <remarks>
    /// Showing the raw <see cref="Exception.Message"/> would put the numeric
    /// code and the whole envelope in front of the user. These strings come from
    /// the core and are English regardless of the interface language — a known
    /// gap, but a readable English sentence beats a readable JSON blob.
    /// </remarks>
    private static string Describe(EngineException ex)
    {
        try
        {
            if (JsonNode.Parse(ex.Json)?["error"]?["message"]?.GetValue<string>() is { Length: > 0 } m)
            {
                return m;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not an envelope; fall through to the exception's own text.
        }
        return ex.Message;
    }

    /// <summary>
    /// Anything worth saying about a launch that otherwise succeeded.
    /// </summary>
    /// <remarks>
    /// Only genuinely surprising outcomes qualify. A profile being reused, or
    /// seeded for the first time, is the feature working — announcing it every
    /// time would train the user to dismiss the notice that actually matters.
    /// </remarks>
    private static string? Note(JsonNode prepared)
    {
        var problems = (prepared["shareProblems"] as JsonArray ?? [])
            .Select(n => n?.GetValue<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
        if (problems.Count > 0)
        {
            return Loc.T("session.shareSkipped", string.Join(", ", problems));
        }
        return null;
    }

    /// <summary>
    /// Ask for the directory to open, seeded with the last one used.
    /// </summary>
    /// <returns>The chosen path, or null when the user cancelled.</returns>
    public static string? AskDirectory(IWin32Window owner, string? suggested = null)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Loc.T("session.pickDir"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        string? start = suggested ?? s_lastDirectory;
        if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
        {
            dialog.SelectedPath = start;
        }
        if (dialog.ShowDialog(owner) != DialogResult.OK)
        {
            return null;
        }
        s_lastDirectory = dialog.SelectedPath;
        return dialog.SelectedPath;
    }

    /// <summary>The account bound to a directory, or null when none governs it.</summary>
    /// <remarks>
    /// Resolution walks to the nearest bound ancestor in the core, so a bound
    /// repo root covers everything under it.
    /// </remarks>
    public static (int Number, string Email)? BoundAccount(Engine engine, string directory)
    {
        try
        {
            var node = engine.Call("mapping_resolve", new { path = directory });
            if (node["matched"]?.GetValue<bool>() != true)
            {
                return null;
            }
            // A binding whose account was removed resolves to no slot. It is
            // reported rather than dropped so the UI can explain itself, but it
            // cannot be launched.
            if (node["number"] is not { } number || number.GetValueKind() == System.Text.Json.JsonValueKind.Null)
            {
                return null;
            }
            return (number.GetValue<int>(), node["email"]?.GetValue<string>() ?? "");
        }
        catch (EngineException)
        {
            return null;
        }
    }
}
