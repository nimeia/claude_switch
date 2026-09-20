using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClaudeSwitch.App;

/// <summary>
/// Per-user logon startup. Which mechanism is used depends on how the app is
/// installed:
///
/// - Portable (the download from Releases): the HKCU Run key. No admin needed.
/// - Packaged (the MSIX from the Microsoft Store): the package's startup task.
///   An MSIX process writes HKCU into its own private overlay, so a Run entry
///   written there is never read at logon — it looks like it worked and then
///   silently does nothing. Windows reads the manifest's startupTask instead,
///   and the user can also veto it in Settings → Apps → Startup.
/// </summary>
internal static class StartupHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeSwitch";

    /// <summary>Must match TaskId in the startupTask extension in AppxManifest.xml.</summary>
    private const string TaskId = "ClaudeSwitchStartup";

    public static string ExePath =>
        Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "ClaudeSwitch.exe");

    /// <summary>True when running from an MSIX package.</summary>
    public static bool IsPackaged { get; } = HasPackageIdentity();

    public static bool IsEnabled()
    {
        try
        {
            return IsPackaged ? StartupTaskEnabled() : RunKeyEnabled();
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (IsPackaged)
        {
            SetStartupTask(enabled);
        }
        else
        {
            SetRunKey(enabled);
        }
    }

    // ── portable: HKCU Run ──────────────────────────────────────────────

    private static bool RunKeyEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var val = key?.GetValue(ValueName) as string;
        return !string.IsNullOrWhiteSpace(val);
    }

    private static void SetRunKey(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new InvalidOperationException(Loc.T("startup.registryFailed"));

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

    // ── packaged: startup task ──────────────────────────────────────────

    private static bool StartupTaskEnabled()
    {
        var task = Windows.ApplicationModel.StartupTask.GetAsync(TaskId).GetAwaiter().GetResult();
        return task.State is Windows.ApplicationModel.StartupTaskState.Enabled
            or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
    }

    private static void SetStartupTask(bool enabled)
    {
        var task = Windows.ApplicationModel.StartupTask.GetAsync(TaskId).GetAwaiter().GetResult();

        if (!enabled)
        {
            // DisabledByUser stays as it is: Disable() cannot clear the user's
            // own veto, and pretending otherwise would leave the box unticked
            // for a reason the app did not cause.
            if (task.State != Windows.ApplicationModel.StartupTaskState.DisabledByPolicy)
            {
                task.Disable();
            }
            return;
        }

        var state = task.RequestEnableAsync().GetAwaiter().GetResult();
        if (state is Windows.ApplicationModel.StartupTaskState.Enabled
            or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy)
        {
            return;
        }

        // Windows refused. Say which of the two refusals it was, because the
        // fix is different: the user can undo their own veto in Settings,
        // an administrator's policy they cannot.
        throw new InvalidOperationException(
            state == Windows.ApplicationModel.StartupTaskState.DisabledByPolicy
                ? Loc.T("startup.blockedByPolicy")
                : Loc.T("startup.blockedByUser"));
    }

    // ── packaged-or-not ─────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    private const int AppModelErrorNoPackage = 15700;

    private static bool HasPackageIdentity()
    {
        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-Windows 8. There is no package, so there is no startup task.
            return false;
        }
    }
}
