namespace ClaudeSwitch.App;

/// <summary>
/// Pure account-list filter used by <see cref="MainForm"/> rebuild path.
/// Kept free of WinForms so tests can drive the real matching rules.
/// </summary>
public static class AccountListFilter
{
    /// <summary>
    /// Filter by email / alias substring (case-insensitive) or exact slot number.
    /// Empty or whitespace query returns all accounts (same order).
    /// </summary>
    public static IReadOnlyList<AccountCardModel> Filter(
        IReadOnlyList<AccountCardModel> models,
        string? query)
    {
        string q = (query ?? "").Trim();
        if (q.Length == 0)
            return models;

        return models.Where(m =>
                m.Email.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (m.Alias?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || m.Number.ToString() == q)
            .ToList();
    }

    /// <summary>
    /// Status-line / chrome copy for an active filter.
    /// Empty query → null (caller keeps normal status).
    /// </summary>
    public static string? FormatFilterMessage(int matchCount, int totalCount, string? query)
    {
        string q = (query ?? "").Trim();
        if (q.Length == 0)
            return null;
        if (matchCount == 0)
            return $"无匹配账号（0 / {totalCount}）· 关键词「{q}」· 点「清除」或清空搜索框";
        return $"匹配 {matchCount} / {totalCount} 个账号 · 「{q}」";
    }
}
