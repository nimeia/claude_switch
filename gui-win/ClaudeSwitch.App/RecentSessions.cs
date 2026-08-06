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
    /// Menu text: the directory, when it was last touched, then what it was about.
    /// </summary>
    /// <remarks>
    /// The directory leads because that is what the reader is choosing between,
    /// and the time follows it so the only variable-length part — the prompt —
    /// sits last, where truncating it costs nothing. Ampersands are doubled or
    /// WinForms eats them as mnemonics.
    /// </remarks>
    public static string MenuLabel(RecentSession s, int titleCells = 26, int nameCells = 24)
    {
        string name = Truncate(s.Name, nameCells);
        string when = Theme.FormatCompactEpoch(s.LastActiveMs) is { } t ? $"  ·  {t}" : "";
        string title = Truncate((s.Title ?? "").Trim(), titleCells);
        string tail = title.Length > 0 ? $"    {title}" : "";
        return ($"{name}{when}{tail}").Replace("&", "&&");
    }

    /// <summary>
    /// Clip to a budget of display cells, counting wide glyphs as two.
    /// </summary>
    /// <remarks>
    /// Counting characters overflows the menu: 26 Chinese characters occupy the
    /// width of 52 Latin ones, which pushed rows past the window edge and cut
    /// off the timestamp that had been placed after them.
    /// </remarks>
    public static string Truncate(string text, int cells)
    {
        if (cells <= 0 || text.Length == 0) return "";
        int used = 0;
        for (int i = 0; i < text.Length; i++)
        {
            used += IsWide(text[i]) ? 2 : 1;
            if (used > cells) return text[..i] + "…";
        }
        return text;
    }

    /// <summary>Roughly: CJK, kana, and full-width forms render double-width.</summary>
    private static bool IsWide(char c) =>
        c is >= 'ᄀ' and (
            <= 'ᅟ'                       // Hangul Jamo
            or >= '⺀' and <= '꓏'    // CJK radicals … Yi
            or >= '가' and <= '힣'    // Hangul syllables
            or >= '豈' and <= '﫿'    // CJK compatibility ideographs
            or >= '︰' and <= '﹯'    // CJK compatibility forms
            or >= '＀' and <= '｠'    // full-width forms
            or >= '￠' and <= '￦');
}
