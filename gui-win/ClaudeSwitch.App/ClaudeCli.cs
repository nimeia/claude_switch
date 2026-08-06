using System.Diagnostics;

namespace ClaudeSwitch.App;

/// <summary>
/// Launching the Claude Code CLI.
///
/// Sessions are resumed by running <c>claude</c> directly rather than through
/// <c>cmd /k claude …</c>. The shell was a middle layer that cost four things:
/// it forced cmd as the shell whatever the user prefers, left a stray cmd prompt
/// behind when Claude exited, put every path through cmd's quoting rules, and
/// decided the console host itself. Launching the executable lets Windows honour
/// the user's own "default terminal application" setting — which is exactly the
/// "use whatever the system uses" behaviour, with no terminal detection at all.
/// </summary>
internal static class ClaudeCli
{
    /// <summary>Full path to the CLI, or null when it is not installed / not on PATH.</summary>
    /// <remarks>
    /// Resolved before launching rather than letting <see cref="Process.Start"/>
    /// fail: without a shell there is no window left on screen to read an error
    /// from, so an unavailable CLI has to be reported by us.
    /// </remarks>
    public static string? FindExecutable() => Find("claude");

    /// <summary>PATH lookup honouring PATHEXT, plus the usual per-user install dir.</summary>
    public static string? Find(string command)
    {
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        // Claude Code's own installer puts it here and does not always get PATH
        // refreshed into an already-running process.
        dirs.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));

        foreach (var dir in dirs)
        {
            string trimmed = dir.Trim('"');
            if (trimmed.Length == 0) continue;
            foreach (var ext in exts)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(trimmed, command + ext);
                }
                catch (ArgumentException)
                {
                    break; // malformed PATH entry
                }
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>Message to show when the CLI cannot be found.</summary>
    public static string NotFoundMessage => Loc.T("resume.notFound");

    /// <summary>
    /// Open a session in the user's terminal.
    /// </summary>
    /// <returns>Null on success, or a message describing why it could not start.</returns>
    public static string? Resume(string workingDirectory, string sessionId)
    {
        if (!Directory.Exists(workingDirectory))
        {
            return Loc.T("resume.missingDir", workingDirectory);
        }
        if (FindExecutable() is not { } exe)
        {
            return NotFoundMessage;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe, $"--resume {sessionId}")
            {
                // The directory decides which project Claude Code attaches to,
                // so it is set on the process rather than prepended as a `cd`.
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
            });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
