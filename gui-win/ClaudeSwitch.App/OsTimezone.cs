namespace ClaudeSwitch.App;

/// <summary>This machine's timezone as an IANA name, when Windows can say.</summary>
internal static class OsTimezone
{
    public static string? Iana()
    {
        try
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out string? iana)
                && !string.IsNullOrWhiteSpace(iana))
            {
                return iana;
            }
        }
        catch (TimeZoneNotFoundException)
        {
            // Fall through to the Windows id.
        }
        catch (InvalidTimeZoneException)
        {
            // Fall through to the Windows id.
        }

        string id = TimeZoneInfo.Local.Id;
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }
}
