using System.Diagnostics;
using System.Text;

namespace ClaudeSwitch.App;

/// <summary>
/// Launching the Claude Code CLI in a real colour-capable terminal.
/// </summary>
/// <remarks>
/// Session mode needs <c>CLAUDE_CONFIG_DIR</c> on the child, so we cannot use
/// <c>UseShellExecute = true</c>. Starting <c>claude.exe</c> directly from a
/// WinForms process (no console of its own) used to hand it a bare console
/// without truecolor / WT profile → Claude Code's UI icons rendered monochrome.
///
/// Prefer Windows Terminal (<c>wt.exe</c>) when installed, and always seed the
/// colour env vars apps use to detect 24-bit colour. Fall back to a direct
/// spawn with the same env when WT is missing.
/// </remarks>
internal static class ClaudeCli
{
    /// <summary>Env vars that force colour-capable rendering when the host supports it.</summary>
    private static readonly (string Name, string Value)[] ColorEnv =
    [
        ("COLORTERM", "truecolor"),
        ("TERM", "xterm-256color"),
        ("FORCE_COLOR", "3"),
        ("CLICOLOR_FORCE", "1"),
    ];

    /// <summary>Full path to the CLI, or null when it is not installed / not on PATH.</summary>
    public static string? FindExecutable() => Find("claude");

    /// <summary>First Claude Code that understands <c>statusLine.refreshInterval</c>.</summary>
    private static readonly Version RefreshIntervalSince = new(2, 1, 97);

    private static Version? s_version;
    private static bool s_versionProbed;

    /// <summary>
    /// Installed Claude Code version, or null when it cannot be asked.
    /// </summary>
    /// <remarks>
    /// Probed once per run, and only when something actually needs the answer:
    /// this spawns the Node CLI, which takes the better part of a second.
    /// </remarks>
    public static Version? InstalledVersion()
    {
        if (s_versionProbed) return s_version;
        s_versionProbed = true;
        try
        {
            if (FindExecutable() is not { } exe) return null;
            using var p = Process.Start(new ProcessStartInfo(exe, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) return null;
            s_version = ParseVersion(output);
        }
        catch (Exception)
        {
            // No Claude Code, no permission, a hung CLI: the caller's feature
            // has a "do not write that key" path, which is the safe answer.
        }
        return s_version;
    }

    /// <summary>
    /// The version out of <c>claude --version</c> output, or null if there is
    /// none to find.
    /// </summary>
    /// <remarks>
    /// The line reads <c>2.1.223 (Claude Code)</c> today, and has carried other
    /// trailers before. Take the first three-part number and ignore the rest
    /// rather than matching the whole line, which would start returning null
    /// the next time the wording changes.
    /// </remarks>
    internal static Version? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(output, @"(\d+)\.(\d+)\.(\d+)");
        if (!m.Success) return null;
        try
        {
            return new Version(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value));
        }
        catch (Exception)
        {
            // A number too long for int: not a version we can compare.
            return null;
        }
    }

    /// <summary>Whether that version understands <c>statusLine.refreshInterval</c>.</summary>
    internal static bool SupportsRefreshInterval(Version? version) =>
        version is not null && version >= RefreshIntervalSince;

    /// <summary>
    /// The status-line refresh interval to write, or null to leave the key out.
    /// </summary>
    /// <remarks>
    /// Older Claude Code versions do not know the key, and an unknown key in
    /// <c>settings.json</c> is something they complain about at the user.
    /// </remarks>
    public static int? StatusLineRefreshInterval() =>
        SupportsRefreshInterval(InstalledVersion()) ? 10 : null;

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

    /// <summary>
    /// Windows Terminal launcher, if the app execution alias or install is present.
    /// </summary>
    public static string? FindWindowsTerminal()
    {
        if (Find("wt") is { } onPath)
            return onPath;

        // Store / App Installer alias (often not yet on PATH for this process).
        string apps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(apps)) return apps;

        return null;
    }

    /// <summary>Message to show when the CLI cannot be found.</summary>
    public static string NotFoundMessage => Loc.T("resume.notFound");

    /// <summary>
    /// Open a session in the user's terminal.
    /// </summary>
    /// <param name="workingDirectory">Decides which project Claude Code attaches to.</param>
    /// <param name="sessionId">Conversation to resume, or null to start fresh.</param>
    /// <param name="configDir">
    /// Session-mode profile for <c>CLAUDE_CONFIG_DIR</c>; null uses the default login.
    /// </param>
    /// <param name="scrubEnv">
    /// Variables to drop from the child environment. An inherited
    /// <c>ANTHROPIC_API_KEY</c> would override the account this launch names,
    /// which is the one thing a session launch must not allow.
    /// </param>
    /// <returns>Null on success, or a message describing why it could not start.</returns>
    public static string? Resume(
        string workingDirectory,
        string? sessionId = null,
        string? configDir = null,
        IEnumerable<string>? scrubEnv = null,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        if (!Directory.Exists(workingDirectory))
        {
            return Loc.T("resume.missingDir", workingDirectory);
        }
        if (FindExecutable() is not { } claudeExe)
        {
            return NotFoundMessage;
        }

        var scrub = (scrubEnv ?? []).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var extra = extraEnv ?? new Dictionary<string, string>();

        try
        {
            // Prefer Windows Terminal: full colour profile + ConPTY truecolor.
            if (FindWindowsTerminal() is { } wt)
            {
                if (TryStartViaWindowsTerminal(wt, claudeExe, workingDirectory, sessionId, configDir, scrub, extra))
                    return null;
            }

            // Fallback: direct spawn (default-terminal handoff) with colour env.
            StartDirect(claudeExe, workingDirectory, sessionId, configDir, scrub, extra);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// <c>wt -d dir -- cmd /d /s /c "set … &amp;&amp; claude …"</c> so env vars
    /// actually reach Claude (WT does not reliably inherit our Process env into the pane).
    /// </summary>
    private static bool TryStartViaWindowsTerminal(
        string wtExe,
        string claudeExe,
        string workingDirectory,
        string? sessionId,
        string? configDir,
        IReadOnlyList<string> scrubEnv,
        IReadOnlyDictionary<string, string> extraEnv)
    {
        try
        {
            string script = BuildCmdEnvScript(claudeExe, sessionId, configDir, scrubEnv, extraEnv);
            var psi = new ProcessStartInfo(wtExe)
            {
                UseShellExecute = false,
                // wt itself does not need the work dir; -d sets the tab's.
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            // Open as a tab when a window exists; otherwise a new window.
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("nt");
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(workingDirectory);
            // Explicit commandline after options: cmd carries env then execs claude.
            psi.ArgumentList.Add("cmd.exe");
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(script);
            ApplyColorEnv(psi);
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void StartDirect(
        string claudeExe,
        string workingDirectory,
        string? sessionId,
        string? configDir,
        IReadOnlyList<string> scrubEnv,
        IReadOnlyDictionary<string, string> extraEnv)
    {
        var psi = new ProcessStartInfo(claudeExe)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        if (!string.IsNullOrEmpty(sessionId))
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(sessionId);
        }
        if (configDir is not null)
        {
            psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;
        }
        ApplyLaunchEnv(psi, extraEnv, scrubEnv);
        ApplyColorEnv(psi);
        Process.Start(psi);
    }

    /// <summary>
    /// cmd.exe script: set colour + session env, clear scrubbed keys, run claude.
    /// </summary>
    /// <summary>
    /// cmd.exe script: set colour + session + proxy env, clear scrubbed keys, run claude.
    /// </summary>
    internal static string BuildCmdEnvScript(
        string claudeExe,
        string? sessionId,
        string? configDir,
        IReadOnlyList<string> scrubEnv,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var sb = new StringBuilder();
        // /s /c expects one command string; chain with &&.
        void Set(string name, string value)
        {
            if (sb.Length > 0) sb.Append(" && ");
            // Escape embedded quotes in values (paths rarely have them).
            string v = value.Replace("\"", "");
            sb.Append("set \"").Append(name).Append('=').Append(v).Append('"');
        }

        void Clear(string name)
        {
            if (sb.Length > 0) sb.Append(" && ");
            sb.Append("set \"").Append(name).Append('=').Append('"');
        }

        foreach (var (name, value) in ColorEnv)
            Set(name, value);
        // Explicitly clear any NO_COLOR inherited into cmd's parent chain.
        Clear("NO_COLOR");

        if (configDir is not null)
            Set("CLAUDE_CONFIG_DIR", configDir);

        if (extraEnv is not null)
        {
            foreach (var (name, value) in extraEnv)
                Set(name, value);
        }

        foreach (var name in scrubEnv)
            Clear(name);

        if (sb.Length > 0) sb.Append(" && ");
        // Quoted exe path; optional --resume id.
        sb.Append('"').Append(claudeExe).Append('"');
        if (!string.IsNullOrEmpty(sessionId))
        {
            sb.Append(" --resume ");
            sb.Append('"').Append(sessionId.Replace("\"", "")).Append('"');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Mark the child as truecolor-capable and drop NO_COLOR so icons stay coloured.
    /// </summary>
    private static void ApplyColorEnv(ProcessStartInfo psi)
    {
        foreach (var (name, value) in ColorEnv)
            psi.Environment[name] = value;
        psi.Environment.Remove("NO_COLOR");
    }

    /// <summary>Set extra vars then drop scrubbed keys (scrub wins on overlap).</summary>
    internal static void ApplyLaunchEnv(
        ProcessStartInfo psi,
        IReadOnlyDictionary<string, string>? extraEnv,
        IEnumerable<string>? scrubEnv)
    {
        if (extraEnv is not null)
        {
            foreach (var (name, value) in extraEnv)
                psi.Environment[name] = value;
        }
        if (scrubEnv is null) return;
        foreach (var name in scrubEnv)
        {
            if (!string.IsNullOrEmpty(name))
                psi.Environment.Remove(name);
        }
    }
}
