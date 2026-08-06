namespace ClaudeSwitch.App;

/// <summary>Mask emails / identifiers for tray balloons and status chrome.</summary>
internal static class Pii
{
    /// <summary>
    /// a***@example.com — keeps domain for recognition, hides local-part bulk.
    /// </summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email == "—")
            return email ?? "—";

        var at = email.IndexOf('@');
        if (at <= 0)
            return MaskGeneric(email);

        var local = email[..at];
        var domain = email[(at + 1)..];
        if (local.Length <= 1)
            return $"*@{domain}";
        if (local.Length == 2)
            return $"{local[0]}*@{domain}";
        return $"{local[0]}***@{domain}";
    }

    public static string MaskAccountLabel(string? alias, string? email)
    {
        if (!string.IsNullOrWhiteSpace(alias))
            return alias!;
        return MaskEmail(email);
    }

    public static string MaskGeneric(string s)
    {
        if (s.Length <= 2) return "**";
        if (s.Length <= 4) return s[0] + "**";
        return s[0] + "***" + s[^1];
    }
}
