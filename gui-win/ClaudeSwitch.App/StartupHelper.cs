using Microsoft.Win32;

namespace ClaudeSwitch.App;

/// <summary>
/// Per-user logon startup via HKCU Run key (no admin required).
/// </summary>
internal static class StartupHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeSwitch";

    public static string ExePath =>
        Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "ClaudeSwitch.exe");

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var val = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new InvalidOperationException("无法打开注册表 Run 项");

        if (enabled)
        {
            // Quote path for spaces; optional -- minimized later.
            var path = ExePath;
            key.SetValue(ValueName, $"\"{path}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
