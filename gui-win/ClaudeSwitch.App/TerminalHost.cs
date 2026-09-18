using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace ClaudeSwitch.App;

/// <summary>Which app should host a Claude Code the user asked this tool to start.</summary>
internal enum TerminalKind
{
    Auto,
    WindowsTerminal,
    Warp,
    WezTerm,
    Ghostty,
    Alacritty,
    Tabby,
    ConEmu,
    ConsoleHost,
    SystemDefault,
}

/// <summary>One row in the terminal picker, including whether it is installed.</summary>
internal sealed record TerminalChoice(
    TerminalKind Kind,
    string GroupKey,
    bool Available,
    string? Path);

/// <summary>Everything a host needs to spawn Claude Code as the named account.</summary>
internal readonly record struct TerminalLaunch(
    string ClaudeExe,
    string WorkingDirectory,
    string? SessionId,
    string? ConfigDir,
    IReadOnlyList<string> ScrubEnv,
    IReadOnlyDictionary<string, string> ExtraEnv);

/// <summary>
/// Finding and launching the terminal that will run Claude Code.
/// </summary>
/// <remarks>
/// Windows Terminal does not inherit this process's environment into a new
/// pane, and several other hosts are the same, so every host that runs a
/// command goes through <see cref="ClaudeCli.BuildCmdEnvScript"/> (a <c>cmd
/// /c</c> line that sets proxy, <c>CLAUDE_CONFIG_DIR</c>, and colour). Direct
/// spawn of <c>claude.exe</c> is the one path that can set the environment on
/// the process itself — used for Automatic-without-WT and System default.
///
/// Wave Terminal is omitted on purpose: as of 2026 it still has no external
/// CLI that can start a block with a command and a working directory.
/// </remarks>
internal static class TerminalHost
{
    public const string AutoId = "auto";
    public const string WarpTabConfigStem = "claude-switch";

    /// <summary>The user's saved choice, or Automatic when the line is missing or unknown.</summary>
    public static TerminalKind Preferred => Parse(UiPrefs.Terminal);

    public static TerminalKind Parse(string? id) => id switch
    {
        "windows-terminal" => TerminalKind.WindowsTerminal,
        "warp" => TerminalKind.Warp,
        "wezterm" => TerminalKind.WezTerm,
        "ghostty" => TerminalKind.Ghostty,
        "alacritty" => TerminalKind.Alacritty,
        "tabby" => TerminalKind.Tabby,
        "conemu" => TerminalKind.ConEmu,
        "conhost" => TerminalKind.ConsoleHost,
        "system" => TerminalKind.SystemDefault,
        _ => TerminalKind.Auto,
    };

    public static string Id(this TerminalKind kind) => kind switch
    {
        TerminalKind.WindowsTerminal => "windows-terminal",
        TerminalKind.Warp => "warp",
        TerminalKind.WezTerm => "wezterm",
        TerminalKind.Ghostty => "ghostty",
        TerminalKind.Alacritty => "alacritty",
        TerminalKind.Tabby => "tabby",
        TerminalKind.ConEmu => "conemu",
        TerminalKind.ConsoleHost => "conhost",
        TerminalKind.SystemDefault => "system",
        _ => AutoId,
    };

    public static string DisplayName(TerminalKind kind) => Loc.T("terminal.choice." + ChoiceKey(kind));

    public static string Note(TerminalKind kind) => kind switch
    {
        TerminalKind.Auto => Loc.T("terminal.note.auto"),
        TerminalKind.Warp => Loc.T("terminal.note.warp"),
        TerminalKind.Tabby => Loc.T("terminal.note.tabby"),
        TerminalKind.SystemDefault => Loc.T("terminal.note.system"),
        _ => "",
    };

    /// <summary>Picker rows, in display order. Automatic is always available.</summary>
    public static IReadOnlyList<TerminalChoice> Catalog()
    {
        static TerminalChoice Found(TerminalKind kind, string group, string? path) =>
            new(kind, group, path is not null, path);

        return
        [
            new(TerminalKind.Auto, "terminal.group.auto", true, null),
            Found(TerminalKind.Warp, "terminal.group.agent", FindWarp()),
            Found(TerminalKind.WindowsTerminal, "terminal.group.common", ClaudeCli.FindWindowsTerminal()),
            Found(TerminalKind.WezTerm, "terminal.group.common", FindWezTerm()),
            Found(TerminalKind.Ghostty, "terminal.group.common", FindGhostty()),
            Found(TerminalKind.Alacritty, "terminal.group.common", FindAlacritty()),
            Found(TerminalKind.Tabby, "terminal.group.common", FindTabby()),
            Found(TerminalKind.ConEmu, "terminal.group.common", FindConEmu()),
            Found(TerminalKind.ConsoleHost, "terminal.group.fallback", FindConhost()),
            new(TerminalKind.SystemDefault, "terminal.group.fallback", true, null),
        ];
    }

    /// <summary>
    /// Start Claude Code in the preferred host. Automatic still prefers
    /// Windows Terminal, then a direct spawn. A named host that is missing
    /// or refuses to start is a failure, not a silent fallback — the user
    /// picked it.
    /// </summary>
    public static bool TryLaunch(in TerminalLaunch launch)
    {
        var kind = Preferred;
        return kind == TerminalKind.Auto
            ? TryLaunchAuto(launch)
            : TryLaunchKind(kind, launch);
    }

    internal static bool TryLaunchKind(TerminalKind kind, in TerminalLaunch launch) => kind switch
    {
        TerminalKind.Auto => TryLaunchAuto(launch),
        TerminalKind.WindowsTerminal =>
            ClaudeCli.FindWindowsTerminal() is { } wt
            && ClaudeCli.TryStartViaWindowsTerminal(
                wt, launch.ClaudeExe, launch.WorkingDirectory, launch.SessionId,
                launch.ConfigDir, launch.ScrubEnv, launch.ExtraEnv),
        TerminalKind.Warp => TryStartWarp(launch),
        TerminalKind.WezTerm => TryStartWezTerm(launch),
        TerminalKind.Ghostty => TryStartGhostty(launch),
        TerminalKind.Alacritty => TryStartAlacritty(launch),
        TerminalKind.Tabby => TryStartTabby(launch),
        TerminalKind.ConEmu => TryStartConEmu(launch),
        TerminalKind.ConsoleHost => TryStartConhost(launch),
        TerminalKind.SystemDefault => TryStartDirect(launch),
        _ => TryLaunchAuto(launch),
    };

    private static bool TryLaunchAuto(in TerminalLaunch launch)
    {
        if (ClaudeCli.FindWindowsTerminal() is { } wt
            && ClaudeCli.TryStartViaWindowsTerminal(
                wt, launch.ClaudeExe, launch.WorkingDirectory, launch.SessionId,
                launch.ConfigDir, launch.ScrubEnv, launch.ExtraEnv))
        {
            return true;
        }
        return TryStartDirect(launch);
    }

    private static bool TryStartDirect(in TerminalLaunch launch)
    {
        try
        {
            ClaudeCli.StartDirect(
                launch.ClaudeExe, launch.WorkingDirectory, launch.SessionId,
                launch.ConfigDir, launch.ScrubEnv, launch.ExtraEnv);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Warp has no <c>-e</c>. A tab config opens a pane on a directory and
    /// runs a command. Warp's default shell on Windows is PowerShell, which
    /// rejects <c>"claude.EXE" --resume</c> as a parse error — so the env
    /// script lives in a <c>.cmd</c> file, and the typed command is only
    /// <c>cmd.exe /d /s /c "that file"</c>, valid in both shells.
    /// </summary>
    internal static bool TryStartWarp(in TerminalLaunch launch)
    {
        try
        {
            string batch = WarpLaunchBatchPath();
            Directory.CreateDirectory(Path.GetDirectoryName(batch)!);
            File.WriteAllText(batch, LaunchBatchContents(launch), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            string dir = WarpTabConfigDir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, WarpTabConfigStem + ".toml");
            File.WriteAllText(path, WarpTabConfigToml(launch), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            // The protocol handler starts Warp when it is not running. Opening
            // the exe by itself would leave a shell with no Claude Code.
            return StartUri(WarpTabConfigUri());
        }
        catch
        {
            return false;
        }
    }

    internal static string WarpTabConfigUri() =>
        $"warp://tab_config/{WarpTabConfigStem}?new_window=true";

    internal static string WarpTabConfigDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "warp", "Warp", "data", "tab_configs");

    internal static string WarpLaunchBatchPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeSwitch",
            "warp-launch.cmd");

    /// <summary>
    /// The one line Warp types into the pane. Must parse as PowerShell and as cmd.
    /// </summary>
    internal static string WarpLaunchCommand(string batchPath) =>
        "cmd.exe /d /s /c " + '"' + batchPath + '"';

    /// <summary>Tab config body Warp reads from <see cref="WarpTabConfigDir"/>.</summary>
    internal static string WarpTabConfigToml(in TerminalLaunch launch)
    {
        string directory = launch.WorkingDirectory.Replace('\\', '/');
        string command = WarpLaunchCommand(WarpLaunchBatchPath());
        var sb = new StringBuilder();
        sb.Append("name = ").AppendLine(TomlString("Claude Switch"));
        sb.Append("title = ").AppendLine(TomlString("Claude Code"));
        sb.AppendLine();
        sb.AppendLine("[[panes]]");
        sb.AppendLine("id = \"main\"");
        sb.AppendLine("type = \"terminal\"");
        sb.AppendLine("shell = \"cmd\"");
        sb.Append("directory = ").AppendLine(TomlString(directory));
        sb.Append("commands = [").Append(TomlString(command)).AppendLine("]");
        return sb.ToString();
    }

    /// <summary>
    /// Batch file Warp's <c>cmd /c</c> runs. One statement per line so the
    /// PowerShell pane never sees <c>--resume</c>.
    /// </summary>
    internal static string LaunchBatchContents(in TerminalLaunch launch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        string dir = launch.WorkingDirectory.Replace("\"", "");
        sb.Append("cd /d \"").Append(dir).AppendLine("\"");

        void Set(string name, string value)
        {
            sb.Append("set \"").Append(name).Append('=').Append(value.Replace("\"", "")).AppendLine("\"");
        }

        void Clear(string name)
        {
            sb.Append("set \"").Append(name).AppendLine("=\"");
        }

        foreach (var (name, value) in ClaudeCli.ColorEnv)
            Set(name, value);
        Clear("NO_COLOR");
        if (launch.ConfigDir is not null)
            Set("CLAUDE_CONFIG_DIR", launch.ConfigDir);
        foreach (var (name, value) in launch.ExtraEnv)
            Set(name, value);
        foreach (var name in launch.ScrubEnv)
        {
            if (!string.IsNullOrEmpty(name))
                Clear(name);
        }

        sb.Append('"').Append(launch.ClaudeExe).Append('"');
        if (!string.IsNullOrEmpty(launch.SessionId))
        {
            sb.Append(" --resume \"");
            sb.Append(launch.SessionId.Replace("\"", ""));
            sb.Append('"');
        }
        sb.AppendLine();
        return sb.ToString();
    }

    internal static string TomlString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Child process every emulator other than Warp and System default runs.
    /// Env and <c>--resume</c> stay inside the <c>/c</c> script so a PowerShell
    /// pane never tokenises them.
    /// </summary>
    internal static string[] CmdWrap(string script) =>
        ["cmd.exe", "/d", "/s", "/c", script];

    /// <summary>
    /// <c>wt -w 0 nt -d dir cmd /d /s /c script</c> — window option, then
    /// new-tab, then <c>-d</c>, then the command line (Microsoft docs).
    /// </summary>
    internal static string[] WindowsTerminalArgs(string workDir, string script) =>
        ["-w", "0", "nt", "-d", workDir, ..CmdWrap(script)];

    internal static string[] WezTermArgs(string workDir, string script) =>
        ["start", "--new-tab", "--cwd", workDir, "--", ..CmdWrap(script)];

    internal static string[] AlacrittyArgs(string workDir, string script) =>
        ["--working-directory", workDir, "-e", ..CmdWrap(script)];

    internal static string[] GhosttyArgs(string workDir, string script) =>
        [$"--working-directory={workDir}", "-e", ..CmdWrap(script)];

    internal static string[] ConEmuArgs(string workDir, string script) =>
        ["-Dir", workDir, "-run", ..CmdWrap(script)];

    internal static string[] ConhostArgs(string script) =>
        ["--", ..CmdWrap(script)];

    internal static string[] TabbyArgs(string script) =>
        ["run", ..CmdWrap(script)];

    internal static string CmdScript(in TerminalLaunch launch, bool cd)
    {
        string script = ClaudeCli.BuildCmdEnvScript(
            launch.ClaudeExe, launch.SessionId, launch.ConfigDir,
            launch.ScrubEnv, launch.ExtraEnv);
        if (!cd) return script;
        string dir = launch.WorkingDirectory.Replace("\"", "");
        return $"cd /d \"{dir}\" && {script}";
    }

    private static bool TryStartWezTerm(in TerminalLaunch launch) =>
        StartExe(FindWezTerm(), WezTermArgs(launch.WorkingDirectory, CmdScript(launch, cd: false)), launch.WorkingDirectory);

    private static bool TryStartAlacritty(in TerminalLaunch launch) =>
        StartExe(FindAlacritty(), AlacrittyArgs(launch.WorkingDirectory, CmdScript(launch, cd: false)), launch.WorkingDirectory);

    private static bool TryStartGhostty(in TerminalLaunch launch) =>
        StartExe(FindGhostty(), GhosttyArgs(launch.WorkingDirectory, CmdScript(launch, cd: false)), launch.WorkingDirectory);

    private static bool TryStartConEmu(in TerminalLaunch launch) =>
        StartExe(FindConEmu(), ConEmuArgs(launch.WorkingDirectory, CmdScript(launch, cd: false)), launch.WorkingDirectory);

    private static bool TryStartConhost(in TerminalLaunch launch) =>
        StartExe(FindConhost(), ConhostArgs(CmdScript(launch, cd: false)), launch.WorkingDirectory);

    private static bool TryStartTabby(in TerminalLaunch launch) =>
        StartExe(FindTabby(), TabbyArgs(CmdScript(launch, cd: true)), launch.WorkingDirectory);

    internal static string? FindWarp()
    {
        // PATH `warp` is often the Agent CLI, not the GUI. Only the installer
        // folders (and App Paths that point at them) count as Warp Terminal.
        string? registered = FirstExisting(AppPath("Warp.exe"), AppPath("warp.exe"));
        if (registered is not null && LooksLikeWarpGui(registered))
            return registered;
        return FirstExisting(
            UnderUser("Programs", "Warp", "Warp.exe"),
            UnderUser("Programs", "Warp", "warp.exe"),
            UnderProgramFiles("Warp", "Warp.exe"),
            UnderProgramFiles("Warp", "warp.exe"));
    }

    internal static string? FindWezTerm() =>
        PreferWezTermGui(FirstExisting(
            ClaudeCli.Find("wezterm-gui"),
            ClaudeCli.Find("wezterm"),
            AppPath("wezterm-gui.exe"),
            AppPath("wezterm.exe"),
            UnderProgramFiles("WezTerm", "wezterm-gui.exe"),
            UnderProgramFiles("WezTerm", "wezterm.exe"),
            UnderUser("Programs", "WezTerm", "wezterm-gui.exe"),
            UnderProfile("scoop", "apps", "wezterm", "current", "wezterm-gui.exe")));

    /// <summary>
    /// Desktop launchers must use <c>wezterm-gui.exe</c> so Windows does not
    /// attach a console host to the CLI binary (wezterm.org CLI overview).
    /// </summary>
    internal static string? PreferWezTermGui(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (Path.GetFileName(path).StartsWith("wezterm-gui", StringComparison.OrdinalIgnoreCase))
            return path;
        string? dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return path;
        string gui = Path.Combine(dir, "wezterm-gui.exe");
        return File.Exists(gui) ? gui : path;
    }

    internal static string? FindGhostty() =>
        FirstExisting(
            ClaudeCli.Find("ghostty"),
            AppPath("ghostty.exe"),
            UnderProgramFiles("Ghostty", "ghostty.exe"),
            UnderUser("Programs", "Ghostty", "ghostty.exe"),
            UnderProfile("scoop", "apps", "ghostty", "current", "ghostty.exe"));

    internal static string? FindAlacritty() =>
        FirstExisting(
            ClaudeCli.Find("alacritty"),
            AppPath("alacritty.exe"),
            UnderProgramFiles("Alacritty", "alacritty.exe"),
            UnderUser("Programs", "Alacritty", "alacritty.exe"),
            UnderProfile("scoop", "apps", "alacritty", "current", "alacritty.exe"));

    internal static string? FindTabby() =>
        FirstExisting(
            AppPath("Tabby.exe"),
            UnderUser("Programs", "Tabby", "Tabby.exe"),
            UnderProgramFiles("Tabby", "Tabby.exe"))
        ?? GuiNamed(ClaudeCli.Find("tabby"), "Tabby");

    internal static string? FindConEmu() =>
        FirstExisting(
            ClaudeCli.Find("ConEmu64"),
            ClaudeCli.Find("ConEmu"),
            AppPath("ConEmu64.exe"),
            AppPath("ConEmu.exe"),
            UnderProgramFiles("ConEmu", "ConEmu64.exe"),
            UnderProgramFiles("cmder", "vendor", "conemu-maximus5", "ConEmu64.exe"),
            UnderUser("cmder", "vendor", "conemu-maximus5", "ConEmu64.exe"),
            UnderUser("Programs", "cmder", "vendor", "conemu-maximus5", "ConEmu64.exe"),
            UnderProfile("cmder", "vendor", "conemu-maximus5", "ConEmu64.exe"),
            UnderProfile("scoop", "apps", "cmder", "current", "vendor", "conemu-maximus5", "ConEmu64.exe"));

    internal static string? FindConhost()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "conhost.exe");
        return File.Exists(path) ? path : ClaudeCli.Find("conhost");
    }

    private static bool LooksLikeWarpGui(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        return dir.EndsWith($"{Path.DirectorySeparatorChar}Warp", StringComparison.OrdinalIgnoreCase)
            || dir.Contains($"{Path.DirectorySeparatorChar}Warp{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GuiNamed(string? path, string folder)
    {
        if (path is null) return null;
        string dir = Path.GetDirectoryName(path) ?? "";
        return dir.EndsWith($"{Path.DirectorySeparatorChar}{folder}", StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }

    private static string ChoiceKey(TerminalKind kind) => kind switch
    {
        TerminalKind.WindowsTerminal => "windowsTerminal",
        TerminalKind.Warp => "warp",
        TerminalKind.WezTerm => "wezterm",
        TerminalKind.Ghostty => "ghostty",
        TerminalKind.Alacritty => "alacritty",
        TerminalKind.Tabby => "tabby",
        TerminalKind.ConEmu => "conemu",
        TerminalKind.ConsoleHost => "conhost",
        TerminalKind.SystemDefault => "system",
        _ => "auto",
    };

    private static bool StartUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool StartExe(string? exe, IReadOnlyList<string> args, string workDir)
    {
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Directory.Exists(workDir)
                    ? workDir
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            foreach (string arg in args)
                psi.ArgumentList.Add(arg);
            ClaudeCli.ApplyColorEnv(psi);
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FirstExisting(params string?[] paths)
    {
        foreach (string? p in paths)
        {
            if (!string.IsNullOrEmpty(p) && File.Exists(p))
                return p;
        }
        return null;
    }

    private static string UnderUser(params string[] parts) =>
        Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }.Concat(parts).ToArray());

    private static string UnderProfile(params string[] parts) =>
        Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }.Concat(parts).ToArray());

    private static string UnderProgramFiles(params string[] parts)
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string a = Path.Combine(new[] { pf }.Concat(parts).ToArray());
        if (File.Exists(a)) return a;
        return Path.Combine(new[] { x86 }.Concat(parts).ToArray());
    }

    private static string? AppPath(string exe)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (k?.GetValue(null) is string raw)
                {
                    string path = raw.Trim().Trim('"');
                    if (path.Length > 0 && File.Exists(path))
                        return path;
                }
            }
            catch (System.Security.SecurityException)
            {
                // A locked hive is not a missing install.
            }
        }
        return null;
    }
}
