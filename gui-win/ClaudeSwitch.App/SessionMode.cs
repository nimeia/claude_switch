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

        var scrub = (prepared["scrubEnv"] as JsonArray ?? [])
            .Select(n => n?.GetValue<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList();

        if (ClaudeCli.Resume(workingDirectory, sessionId, configDir, scrub) is { } problem)
        {
            return new Result(false, problem, null);
        }

        return new Result(true, null, Note(prepared));
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
