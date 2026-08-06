using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace ClaudeSwitch.App;

/// <summary>
/// UI strings, one embedded catalogue per language.
///
/// Plain JSON rather than .resx: a translator contributing a language should be
/// able to copy one readable file and diff it, not edit generated XML across
/// satellite assemblies — which would also add moving parts to the single-file
/// publish. Completeness is enforced by a test that every catalogue carries
/// exactly the base language's keys, so a missing translation is a build
/// failure rather than an English word appearing mid-sentence.
/// </summary>
internal static class Loc
{
    /// <summary>The catalogue every other one is checked against.</summary>
    public const string BaseLanguage = "en";

    /// <summary>Languages shipped, in the order the picker lists them.</summary>
    public static readonly (string Code, string Name)[] Available =
    [
        ("en", "English"),
        ("zh-Hans", "简体中文"),
    ];

    private static Dictionary<string, string> _strings = [];
    private static Dictionary<string, string> _fallback = [];

    /// <summary>Currently active language code.</summary>
    public static string Current { get; private set; } = BaseLanguage;

    /// <summary>Raised after <see cref="Use"/> changes the language.</summary>
    public static event EventHandler? Changed;

    static Loc() => Use(Detect());

    /// <summary>
    /// Language to start in: the saved choice, else the closest match to the OS.
    /// </summary>
    public static string Detect()
    {
        // A per-launch override, ahead of the saved choice: it is how the layout
        // screenshots are taken in each language, and how a user on a machine
        // whose OS language they cannot change forces one for a single run.
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_LANG") is { Length: > 0 } env
            && Available.Any(a => a.Code == env))
        {
            return env;
        }
        if (UiPrefs.Language is { Length: > 0 } saved
            && Available.Any(a => a.Code == saved))
        {
            return saved;
        }
        // "zh-CN", "zh-Hans-CN" and plain "zh" should all land on Chinese.
        string ui = CultureInfo.CurrentUICulture.Name;
        foreach (var (code, _) in Available)
        {
            if (ui.Equals(code, StringComparison.OrdinalIgnoreCase)) return code;
        }
        if (ui.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
        return BaseLanguage;
    }

    /// <summary>Switch language and notify listeners.</summary>
    public static void Use(string code)
    {
        if (!Available.Any(a => a.Code == code)) code = BaseLanguage;
        _fallback = Load(BaseLanguage);
        _strings = code == BaseLanguage ? _fallback : Load(code);
        bool changed = Current != code;
        Current = code;
        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Selects a language for the duration of a block, then restores the previous
    /// one. Exists for tests: the active language is process-wide state, and a
    /// test that asserts a translated string must not leak that choice into the
    /// next test.
    /// </summary>
    public static IDisposable Scoped(string code)
    {
        var previous = Current;
        Use(code);
        return new Restore(previous);
    }

    private sealed class Restore(string previous) : IDisposable
    {
        public void Dispose() => Use(previous);
    }

    /// <summary>Look up a string by key.</summary>
    /// <remarks>
    /// An unknown key returns the key itself rather than throwing or rendering
    /// blank: a typo should be obvious on screen and harmless at runtime.
    /// </remarks>
    public static string T(string key) =>
        _strings.TryGetValue(key, out var s) ? s
        : _fallback.TryGetValue(key, out var f) ? f
        : key;

    /// <summary>Look up a composite format string and fill it.</summary>
    public static string T(string key, params object?[] args)
    {
        string format = T(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            // A translation with a malformed placeholder must not crash the UI.
            return format;
        }
    }

    /// <summary>
    /// Look up a count-dependent string: <c>&lt;key&gt;.one</c> or <c>&lt;key&gt;.other</c>.
    /// </summary>
    /// <remarks>
    /// English needs the distinction and writing "1 terminal(s)" to avoid it
    /// looks like a placeholder that was never finished. Two keys is the whole
    /// mechanism: languages without a plural form (Chinese among them) simply
    /// give both the same text, and the key-parity test still requires both to
    /// exist so a new language cannot ship half of one.
    ///
    /// Deliberately not a full CLDR plural-category implementation — no shipped
    /// language needs "few"/"many", and inventing the machinery before there is
    /// a language that uses it would be untested code.
    /// </remarks>
    public static string Plural(string key, int count) =>
        T(count == 1 ? $"{key}.one" : $"{key}.other", count);

    /// <summary>Every key in a catalogue — used by the completeness test.</summary>
    public static IReadOnlyCollection<string> Keys(string code) => Load(code).Keys;

    private static Dictionary<string, string> Load(string code)
    {
        var asm = Assembly.GetExecutingAssembly();
        string name = $"ClaudeSwitch.App.Strings.{code}.json";
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
