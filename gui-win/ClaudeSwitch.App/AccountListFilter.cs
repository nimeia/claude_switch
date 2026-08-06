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
            return Loc.T("filter.none", totalCount, q);
        return Loc.T("filter.matched", matchCount, totalCount, q);
    }
}
