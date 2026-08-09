using System.Text.Json.Nodes;

namespace ClaudeSwitch.App;

/// <summary>
/// Tray balloon when the window guardian (or a manual warm) opens 5h buckets.
/// </summary>
/// <remarks>
/// Mirrors <see cref="SwitchNotice"/>: unprompted, mid-work, glanceable. Only
/// successful fires are announced — skips (already open, cooldown) stay quiet
/// so a morning poll does not spam "nothing to do".
/// </remarks>
internal static class WarmupNotice
{
    public static string Title => Loc.T("notice.warmup.title");

    /// <summary>
    /// Successful fires from a <c>warmup_now</c> / <c>warmup_tick</c> payload,
    /// or from a nested <c>warmup</c> object on refresh / autoswitch results.
    /// </summary>
    public static IReadOnlyList<WarmupFire> SuccessfulFires(JsonNode? result)
    {
        var node = result?["warmup"] ?? result;
        if (node?["fired"] is not JsonArray arr) return [];
        var list = new List<WarmupFire>();
        foreach (var item in arr)
        {
            if (item?["ok"]?.GetValue<bool>() != true) continue;
            int number = item?["number"]?.GetValue<int>() ?? 0;
            string email = item?["email"]?.GetValue<string>() ?? "";
            if (number <= 0 && string.IsNullOrEmpty(email)) continue;
            list.Add(new WarmupFire(number, email));
        }
        return list;
    }

    public static bool AnySuccess(JsonNode? result) => SuccessfulFires(result).Count > 0;

    /// <summary>
    /// Balloon body: which slots opened a window. Labels are already masked by
    /// the caller when emails must stay private.
    /// </summary>
    public static string Body(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0) return Loc.T("notice.warmup.body.empty");
        if (labels.Count == 1)
            return Loc.T("notice.warmup.body.single", labels[0]);
        // Keep the list short enough for a balloon: first few + "and N more".
        const int maxNamed = 3;
        if (labels.Count <= maxNamed)
            return Loc.T("notice.warmup.body.list", string.Join(Loc.T("notice.warmup.sep"), labels));
        var head = labels.Take(maxNamed).ToList();
        string named = string.Join(Loc.T("notice.warmup.sep"), head);
        return Loc.T("notice.warmup.body.more", named, labels.Count - maxNamed);
    }

    /// <summary>Display label for one fire: "#2 · alias-or-email".</summary>
    public static string LabelFor(int number, string? alias, string email) =>
        Loc.T("notice.warmup.slot", number, Pii.MaskAccountLabel(alias, email));
}

/// <summary>One successful warmup fire from the engine payload.</summary>
internal readonly record struct WarmupFire(int Number, string Email);
