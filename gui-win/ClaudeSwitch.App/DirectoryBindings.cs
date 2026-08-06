using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>One directory → account binding, as the core reports it.</summary>
/// <param name="Key">Normalised path the core stores it under.</param>
/// <param name="Path">Path as the user typed it, for display.</param>
/// <param name="Email">Bound account's email.</param>
/// <param name="Number">Slot currently holding that identity; null when it is gone.</param>
internal sealed record Binding(string Key, string Path, string Email, int? Number);

/// <summary>What a binding lookup found, and how.</summary>
/// <param name="Binding">The governing binding, or null when nothing covers the path.</param>
/// <param name="Inherited">
/// True when it came from an ancestor rather than the path itself. The two are
/// shown differently, and only an exact binding may be removed from its row.
/// </param>
internal readonly record struct BindingMatch(Binding? Binding, bool Inherited)
{
    public bool Found => Binding is not null;
}

/// <summary>
/// The directory → account bindings, resolved the way the core resolves them.
///
/// Loaded once per refresh rather than asked per row: the directory list redraws
/// whole and one FFI round-trip per directory would be dozens of calls to fill a
/// single column. That makes this a *copy* of the core's rule, so the rule is
/// kept here in one testable place instead of inline in the window.
/// </summary>
internal sealed class DirectoryBindings
{
    private readonly Dictionary<string, Binding> _byKey;

    private DirectoryBindings(Dictionary<string, Binding> byKey) => _byKey = byKey;

    public static DirectoryBindings Empty { get; } = new([]);

    public int Count => _byKey.Count;

    /// <summary>
    /// The same key the core stores bindings under: separators and case folded.
    /// </summary>
    /// <remarks>
    /// Claude Code writes the same directory both ways (<c>D:/x</c> and
    /// <c>D:\x</c>) and Windows compares paths case-insensitively, so a binding
    /// set from one spelling has to match the other.
    /// </remarks>
    public static string Key(string path) =>
        path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    /// <summary>Parse a <c>mapping_list</c> response.</summary>
    public static DirectoryBindings Parse(JsonNode? node)
    {
        var map = new Dictionary<string, Binding>(StringComparer.Ordinal);
        if (node?["mappings"] is not JsonArray arr) return new DirectoryBindings(map);

        foreach (var m in arr)
        {
            if (m is null) continue;
            string key = m["key"]?.GetValue<string>() ?? "";
            if (key.Length == 0) continue;
            // A binding whose account was removed keeps its entry with a null
            // slot, so the UI can say "removed" instead of showing a blank cell.
            int? number = m["number"] is { } n && n.GetValueKind() != JsonValueKind.Null
                ? n.GetValue<int>()
                : null;
            map[key] = new Binding(
                key,
                m["path"]?.GetValue<string>() ?? "",
                m["email"]?.GetValue<string>() ?? "",
                number);
        }
        return new DirectoryBindings(map);
    }

    /// <summary>Load from the engine; an unreadable list is simply empty.</summary>
    public static DirectoryBindings Load(Engine engine)
    {
        try
        {
            return Parse(engine.Call("mapping_list"));
        }
        catch (Exception)
        {
            // Bindings are a convenience; the directory list they annotate is not.
            return Empty;
        }
    }

    /// <summary>
    /// The binding governing a directory: its own, or its nearest bound ancestor.
    /// </summary>
    /// <remarks>
    /// The ancestor test checks for a separator after the prefix. Without it
    /// <c>D:/workshop</c> matches a binding on <c>D:/work</c> — a sibling
    /// directory silently inheriting an account it has nothing to do with.
    /// The deepest match wins, so a nested override beats its parent.
    /// </remarks>
    public BindingMatch Resolve(string path)
    {
        string key = Key(path);
        if (_byKey.TryGetValue(key, out var exact))
        {
            return new BindingMatch(exact, false);
        }

        Binding? best = null;
        foreach (var (candidate, binding) in _byKey)
        {
            if (!key.StartsWith(candidate, StringComparison.Ordinal)) continue;
            if (key.Length <= candidate.Length) continue;
            if (key[candidate.Length] != '/') continue;
            if (best is null || candidate.Length > best.Key.Length) best = binding;
        }
        return new BindingMatch(best, best is not null);
    }

    /// <summary>Column text for a directory: which account, and whether inherited.</summary>
    public string Label(string path)
    {
        var match = Resolve(path);
        if (match.Binding is not { } binding) return "";
        string who = binding.Number is { } n
            ? Loc.T("proj.boundTo", n)
            : Loc.T("proj.boundGone");
        return match.Inherited ? Loc.T("proj.boundInherited", who) : who;
    }
}
