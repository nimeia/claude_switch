using System.Diagnostics;
using System.Globalization;

namespace ClaudeSwitch.App;

/// <summary>
/// Daily Windows scheduled task that starts Claude Switch headless at the
/// base warmup anchor so a closed GUI still opens the 5h windows.
/// </summary>
/// <remarks>
/// Uses <c>schtasks</c> under the current user (no elevation). The task runs
/// <c>ClaudeSwitch.exe --warmup-once</c>, which refreshes usage and fires the
/// guardian once, then exits. If the GUI is already open the second instance
/// exits silently — the live poll loop covers that case.
/// </remarks>
internal static class WarmupTaskHelper
{
    public const string TaskName = "ClaudeSwitch-Warmup";

    /// <summary>Whether a daily task with our name is registered.</summary>
    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks")
            {
                Arguments = $"/Query /TN \"{TaskName}\" /FO LIST",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Create or update the daily task at the given local wall-clock time
    /// (<paramref name="hour"/>:<paramref name="minute"/>), or remove it.
    /// </summary>
    /// <remarks>
    /// Disable path is the only cleanup: there is no settings.json / registry
    /// mirror. <c>schtasks /Delete /TN ClaudeSwitch-Warmup /F</c> is the whole
    /// teardown. Missing task is success (idempotent).
    /// </remarks>
    public static void SetEnabled(bool enabled, int hour, int minute)
    {
        if (!enabled)
        {
            // Idempotent: "not found" is fine; real failures still allowFail so a
            // missing schtasks binary does not brick the settings checkbox.
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F", allowFail: true);
            return;
        }

        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        string st = $"{hour:D2}:{minute:D2}";
        string exe = StartupHelper.ExePath;
        // /RL LIMITED keeps it interactive-user; /F overwrites an existing task
        // so changing work hours updates the clock without a delete step.
        // schtasks wants: /TR "\"C:\path\app.exe\" --warmup-once"
        string args =
            $"/Create /TN \"{TaskName}\" /SC DAILY /ST {st} " +
            $"/TR \"\\\"{exe}\\\" --warmup-once\" /F /RL LIMITED";
        RunSchtasks(args, allowFail: false);
    }

    /// <summary>
    /// Base (account-0) anchor hour/minute for a work window, matching core
    /// <c>compute_base_anchor</c>: <c>S + (W−5)/2 − 5</c>.
    /// </summary>
    public static (int Hour, int Minute) BaseAnchor(int workStartHour, int workEndHour)
    {
        workStartHour = Math.Clamp(workStartHour, 0, 22);
        workEndHour = Math.Clamp(workEndHour, workStartHour + 1, 23);
        double w = workEndHour - workStartHour;
        double offsetHours = (w - 5.0) / 2.0 - 5.0;
        double anchor = workStartHour + offsetHours;
        if (anchor < 0) anchor = 0;
        if (anchor >= 24) anchor = 23 + 59.0 / 60.0;
        int h = (int)Math.Floor(anchor);
        int m = (int)Math.Round((anchor - h) * 60.0);
        if (m >= 60)
        {
            h += 1;
            m = 0;
        }
        h = Math.Clamp(h, 0, 23);
        m = Math.Clamp(m, 0, 59);
        return (h, m);
    }

    /// <summary>Local "H:mm" for the base anchor of the given work hours.</summary>
    public static string FormatBaseAnchor(int workStartHour, int workEndHour)
    {
        var (h, m) = BaseAnchor(workStartHour, workEndHour);
        return string.Create(CultureInfo.InvariantCulture, $"{h}:{m:D2}");
    }

    static void RunSchtasks(string arguments, bool allowFail)
    {
        var psi = new ProcessStartInfo("schtasks")
        {
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException(Loc.T("settings.warmup.task.failed", "schtasks missing"));
        string stderr = p.StandardError.ReadToEnd();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        if (p.ExitCode != 0 && !allowFail)
        {
            string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (string.IsNullOrWhiteSpace(detail)) detail = $"exit {p.ExitCode}";
            throw new InvalidOperationException(Loc.T("settings.warmup.task.failed", detail.Trim()));
        }
    }
}
