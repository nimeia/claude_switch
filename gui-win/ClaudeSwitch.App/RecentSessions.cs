using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>One recently updated conversation, as offered in a menu.</summary>
/// <param name="Path">Directory the session belongs to.</param>
/// <param name="Name">Directory's last segment — how the menu names it.</param>
/// <param name="SessionId">Argument for <c>claude --resume</c>.</param>
/// <param name="Title">Claude Code's own title for it, else its opening prompt.</param>
/// <param name="LastActiveMs">Last write to that conversation, for the label.</param>
/// <param name="ProfileNumber">
/// Session-mode profile holding the transcript, or null for the default home.
/// Resuming has to happen there: Claude Code only looks in its own config
/// directory, and anywhere else reports that the conversation does not exist.
/// </param>
/// <param name="Live">
/// Running in a terminal right now. Shown, because it is the newest work; not
/// resumable, because two terminals would then write into one transcript.
/// </param>
/// <param name="File">The transcript — how a deletion names the conversation.</param>
/// <param name="Bytes">The transcript plus what belongs to it: what deleting it frees.</param>
internal sealed record RecentSession(
    string Path,
    string Name,
    string SessionId,
    string? Title,
    long? LastActiveMs,
    int? ProfileNumber = null,
    bool Live = false,
    string? File = null,
    long Bytes = 0);

/// <summary>
/// The most recently updated conversations — a one-click way back in.
///
/// Reaching a session used to take four steps (open the directory window, pick a
/// directory, pick a session, press resume). The frequent case is "back to what I
/// was just doing", and that fits in a menu.
/// </summary>
/// <remarks>
/// Per conversation, newest update first, whichever directory it is in. The menu
/// used to hold one entry per directory, which hid most of a busy day — three
/// conversations touched this afternoon in one project showed as one, while last
/// week's elsewhere took the remaining rows. What counts as offerable is the
/// engine's call (core <c>resume.rs</c>).
/// </remarks>
internal static class RecentSessions
{
    /// <summary>The engine's list, in its order (newest update first), capped.</summary>
    public static List<RecentSession> Parse(JsonNode? recent, int max)
    {
        var list = new List<RecentSession>();
        if (recent?["sessions"] is not JsonArray arr) return list;

        foreach (var s in arr)
        {
            if (s?["sessionId"]?.GetValue<string>() is not { Length: > 0 } id) continue;
            string path = s["path"]?.GetValue<string>() ?? "";
            list.Add(new RecentSession(
                path,
                s["name"]?.GetValue<string>() ?? System.IO.Path.GetFileName(path.TrimEnd('/', '\\')),
                id,
                s["title"]?.GetValue<string>() ?? s["prompt"]?.GetValue<string>(),
                s["modifiedMs"]?.GetValue<long>(),
                s["profileNumber"]?.GetValue<int>(),
                s["live"]?.GetValue<bool>() ?? false,
                s["file"]?.GetValue<string>(),
                s["totalBytes"]?.GetValue<long>() ?? 0));
            if (list.Count >= max) break;
        }
        return list;
    }

    /// <summary>Load straight from the engine; returns empty on any failure.</summary>
    public static List<RecentSession> Load(Engine engine, int max)
    {
        try
        {
            return Parse(engine.Call("recent_sessions", new { limit = max }), max);
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
            or >= '豈' and <= '﫿'    // CJK compatibility ideographs
            or >= '︰' and <= '﹯'    // CJK compatibility forms
            or >= '＀' and <= '｠'    // full-width forms
            or >= '￠' and <= '￦');
}
