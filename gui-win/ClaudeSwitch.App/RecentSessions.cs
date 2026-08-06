using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>One resumable conversation, as offered in a menu.</summary>
/// <param name="Path">Directory the session belongs to.</param>
/// <param name="Name">Directory's last segment — how the menu names it.</param>
/// <param name="SessionId">Argument for <c>claude --resume</c>.</param>
/// <param name="Title">Opening prompt, when the transcript had a usable one.</param>
/// <param name="LastActiveMs">Newest activity, for ordering and for the label.</param>
internal sealed record RecentSession(
    string Path,
    string Name,
    string SessionId,
    string? Title,
    long? LastActiveMs);

/// <summary>
/// The most recent conversation per directory — a one-click way back in.
///
/// Reaching a session used to take four steps (open the directory window, pick a
/// directory, pick a session, press resume). The frequent case is only ever
/// "back to where I left off in project X", and that fits in a menu.
///
/// The data is free: `list_projects` already reads each directory's newest
/// session id and opening prompt (~10 ms for 21 directories), so this needs no
/// scan of its own.
/// </summary>
internal static class RecentSessions
{
    /// <summary>Newest first, one entry per directory, only those resumable.</summary>
    public static List<RecentSession> Parse(JsonNode? projects, int max)
    {
        var list = new List<RecentSession>();
        if (projects?["projects"] is not JsonArray arr) return list;

        foreach (var p in arr)
        {
            if (p is null) continue;
            string? id = p["lastSessionId"]?.GetValue<string>();
            // A directory registered but never worked in has no session to resume.
            if (string.IsNullOrWhiteSpace(id)) continue;
            if ((p["sessionCount"]?.GetValue<int>() ?? 0) == 0) continue;

            list.Add(new RecentSession(
                p["path"]?.GetValue<string>() ?? "",
                p["name"]?.GetValue<string>() ?? "",
                id!,
                p["lastPrompt"]?.GetValue<string>(),
                p["lastActiveMs"]?.GetValue<long>()));
            if (list.Count >= max) break;
        }
        return list;
    }

    /// <summary>Load straight from the engine; returns empty on any failure.</summary>
    public static List<RecentSession> Load(Engine engine, int max)
    {
        try
        {
            return Parse(engine.Call("list_projects"), max);
        }
        catch
        {
            // A menu that cannot be built is simply empty; the directory window
            // is still there to report the real error.
            return [];
        }
    }

    /// <summary>
    /// Menu text: the directory, then what the conversation was about.
    /// </summary>
    /// <remarks>
    /// The directory leads because that is what the user is choosing between;
    /// the prompt disambiguates and is clipped, since a menu row cannot carry a
    /// paragraph. Ampersands are doubled or WinForms eats them as mnemonics.
    /// </remarks>
    public static string MenuLabel(RecentSession s, int maxTitle = 42)
    {
        string when = Theme.FormatCompactEpoch(s.LastActiveMs) is { } t ? $"  ·  {t}" : "";
        string title = (s.Title ?? "").Trim();
        if (title.Length > maxTitle) title = title[..maxTitle] + "…";
        string tail = title.Length > 0 ? $"    {title}" : "";
        return ($"{s.Name}{tail}{when}").Replace("&", "&&");
    }
}
