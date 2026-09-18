using System.Windows.Forms;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// Host adapters for starting Claude Code: argument shape, Warp's tab config,
/// and the picker dialog. No process is launched — those paths need a real
/// terminal install and would open a window on the developer's machine.
/// </summary>
public class TerminalHostTests
{
    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("auto", "auto")]
    [InlineData("warp", "warp")]
    [InlineData("windows-terminal", "windows-terminal")]
    [InlineData("wezterm", "wezterm")]
    [InlineData("ghostty", "ghostty")]
    [InlineData("alacritty", "alacritty")]
    [InlineData("tabby", "tabby")]
    [InlineData("conemu", "conemu")]
    [InlineData("conhost", "conhost")]
    [InlineData("system", "system")]
    [InlineData("nope", "auto")]
    public void Preference_ids_round_trip_and_unknown_is_automatic(string? id, string stored)
    {
        Assert.Equal(stored, TerminalHost.Parse(id).Id());
    }

    [Fact]
    public void Toml_escapes_quotes_and_backslashes()
    {
        Assert.Equal("\"a\\\\b\\\"c\"", TerminalHost.TomlString("a\\b\"c"));
    }

    [Fact]
    public void Warp_tab_config_carries_directory_env_and_claude()
    {
        var launch = SampleLaunch();
        string toml = TerminalHost.WarpTabConfigToml(launch);
        string batch = TerminalHost.LaunchBatchContents(launch);
        string command = TerminalHost.WarpLaunchCommand(TerminalHost.WarpLaunchBatchPath());

        Assert.Contains("shell = \"cmd\"", toml);
        Assert.Contains("type = \"terminal\"", toml);
        Assert.Contains("D:/work/my project", toml);
        Assert.Contains("cmd.exe /d /s /c", toml);
        Assert.Contains("warp-launch.cmd", toml);
        Assert.DoesNotContain("--resume", command);
        Assert.Contains("CLAUDE_CONFIG_DIR", batch);
        Assert.Contains("HTTPS_PROXY=http://127.0.0.1:7897", batch);
        Assert.Contains(@"C:\Tools\claude.exe", batch);
        Assert.Contains("--resume", batch);
        Assert.Contains("sess-1", batch);
        Assert.Contains("ANTHROPIC_API_KEY=", batch);
        Assert.Contains(@"cd /d ""D:\work\my project""", batch);
        Assert.Equal(
            $"warp://tab_config/{TerminalHost.WarpTabConfigStem}?new_window=true",
            TerminalHost.WarpTabConfigUri());
    }

    [Fact]
    public void Warp_typed_command_parses_in_powershell()
    {
        // The failure the user hit: Warp types the launch line into PowerShell,
        // and `"claude.EXE" --resume` is a parse error. The typed line must be
        // only `cmd.exe /d /s /c "batch"`, with --resume inside the file.
        string dir = Path.Combine(Path.GetTempPath(), "cs-warp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string marker = Path.Combine(dir, "ok.txt");
        string batch = Path.Combine(dir, "launch.cmd");
        File.WriteAllText(batch, $"@echo off\r\necho FILE_OK> \"{marker}\"\r\n");
        string command = TerminalHost.WarpLaunchCommand(batch);
        Assert.DoesNotContain("--resume", command);

        var psi = new System.Diagnostics.ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        using var p = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(p);
        p!.WaitForExit(10_000);
        string stderr = p.StandardError.ReadToEnd();
        Assert.True(p.ExitCode == 0, stderr);
        Assert.True(File.Exists(marker), "batch did not run: " + stderr);
        Assert.Contains("FILE_OK", File.ReadAllText(marker));
    }

    [Fact]
    public void Non_warp_hosts_spawn_cmd_and_never_type_resume_as_an_argv_token()
    {
        // Vendor CLIs take a program to exec. Typing `"claude.EXE" --resume`
        // into PowerShell is the Warp bug; these hosts must pass cmd.exe as
        // the child and keep --resume inside the /c script string only.
        var launch = SampleLaunch();
        string script = TerminalHost.CmdScript(launch, cd: false);
        Assert.Contains("--resume", script);
        Assert.Contains("sess-1", script);

        var hosts = new (string Name, string[] Args)[]
        {
            ("windows-terminal", TerminalHost.WindowsTerminalArgs(launch.WorkingDirectory, script)),
            ("wezterm", TerminalHost.WezTermArgs(launch.WorkingDirectory, script)),
            ("alacritty", TerminalHost.AlacrittyArgs(launch.WorkingDirectory, script)),
            ("ghostty", TerminalHost.GhosttyArgs(launch.WorkingDirectory, script)),
            ("conemu", TerminalHost.ConEmuArgs(launch.WorkingDirectory, script)),
            ("conhost", TerminalHost.ConhostArgs(script)),
            ("tabby", TerminalHost.TabbyArgs(TerminalHost.CmdScript(launch, cd: true))),
        };

        foreach (var (name, args) in hosts)
        {
            int cmd = Array.IndexOf(args, "cmd.exe");
            Assert.True(cmd >= 0, name + " must exec cmd.exe as the child");
            Assert.DoesNotContain("--resume", args);
            Assert.Equal("/d", args[cmd + 1]);
            Assert.Equal("/s", args[cmd + 2]);
            Assert.Equal("/c", args[cmd + 3]);
            Assert.True(
                args[cmd + 4].Contains("--resume", StringComparison.Ordinal),
                $"{name}: --resume must live in the /c script, not as its own token");
        }
    }

    [Fact]
    public void Windows_Terminal_puts_minus_d_before_the_command()
    {
        string[] args = TerminalHost.WindowsTerminalArgs(@"D:\work", "set FOO=1 && claude");
        int d = Array.IndexOf(args, "-d");
        int cmd = Array.IndexOf(args, "cmd.exe");
        Assert.Equal("nt", args[2]);
        Assert.True(d >= 0 && d < cmd, "wt -d must precede the command line");
        Assert.Equal(@"D:\work", args[d + 1]);
        Assert.Equal("cmd.exe", args[cmd]);
    }

    [Fact]
    public void WezTerm_puts_cwd_before_the_command()
    {
        string[] args = TerminalHost.WezTermArgs(@"D:\work", "set FOO=1 && claude");
        Assert.Equal("start", args[0]);
        Assert.Contains("--new-tab", args);
        int cwd = Array.IndexOf(args, "--cwd");
        Assert.Equal(@"D:\work", args[cwd + 1]);
        int dash = Array.IndexOf(args, "--");
        Assert.True(dash > cwd);
        Assert.Equal("cmd.exe", args[dash + 1]);
        Assert.Equal("/c", args[^2]);
        Assert.Equal("set FOO=1 && claude", args[^1]);
    }

    [Fact]
    public void Alacritty_keeps_minus_e_last_among_its_own_flags()
    {
        string[] args = TerminalHost.AlacrittyArgs(@"D:\work", "claude");
        int e = Array.IndexOf(args, "-e");
        Assert.True(Array.IndexOf(args, "--working-directory") < e);
        Assert.Equal(@"D:\work", args[Array.IndexOf(args, "--working-directory") + 1]);
        Assert.Equal(TerminalHost.CmdWrap("claude"), args[(e + 1)..]);
    }

    [Fact]
    public void Ghostty_keeps_minus_e_last()
    {
        string[] args = TerminalHost.GhosttyArgs(@"D:\work", "claude");
        Assert.Equal(@"--working-directory=D:\work", args[0]);
        Assert.Equal("-e", args[1]);
        Assert.Equal(TerminalHost.CmdWrap("claude"), args[2..]);
    }

    [Fact]
    public void ConEmu_run_is_the_last_gui_switch()
    {
        string[] args = TerminalHost.ConEmuArgs(@"D:\work", "claude");
        int run = Array.IndexOf(args, "-run");
        Assert.Equal("-Dir", args[0]);
        Assert.Equal(@"D:\work", args[1]);
        Assert.Equal(2, run);
        Assert.Equal("cmd.exe", args[run + 1]);
        Assert.DoesNotContain("-new_console", args);
    }

    [Fact]
    public void Conhost_does_not_eat_the_command()
    {
        string[] args = TerminalHost.ConhostArgs("claude");
        Assert.Equal("--", args[0]);
        Assert.Equal("cmd.exe", args[1]);
    }

    [Fact]
    public void Tabby_run_cds_into_the_project_because_run_has_no_directory_flag()
    {
        string script = TerminalHost.CmdScript(SampleLaunch(), cd: true);
        Assert.StartsWith(@"cd /d ""D:\work\my project"" && ", script);
        string[] args = TerminalHost.TabbyArgs(script);
        Assert.Equal("run", args[0]);
        Assert.Equal("cmd.exe", args[1]);
        Assert.Equal(script, args[^1]);
        Assert.DoesNotContain("open", args);
        Assert.False(string.IsNullOrWhiteSpace(TerminalHost.Note(TerminalKind.Tabby)));
    }

    [Fact]
    public void WezTerm_desktop_launch_prefers_the_gui_binary_beside_the_cli()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cs-wez-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string cli = Path.Combine(dir, "wezterm.exe");
        string gui = Path.Combine(dir, "wezterm-gui.exe");
        File.WriteAllBytes(cli, [0]);
        File.WriteAllBytes(gui, [0]);
        Assert.Equal(gui, TerminalHost.PreferWezTermGui(cli));
        Assert.Equal(gui, TerminalHost.PreferWezTermGui(gui));

        string onlyCliDir = Path.Combine(Path.GetTempPath(), "cs-wez-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(onlyCliDir);
        string onlyCli = Path.Combine(onlyCliDir, "wezterm.exe");
        File.WriteAllBytes(onlyCli, [0]);
        Assert.Equal(onlyCli, TerminalHost.PreferWezTermGui(onlyCli));
        Assert.Null(TerminalHost.PreferWezTermGui(null));
    }

    [Fact]
    public void Catalog_always_offers_automatic_and_system_default()
    {
        var catalog = TerminalHost.Catalog();
        Assert.Equal(TerminalKind.Auto, catalog[0].Kind);
        Assert.True(catalog[0].Available);
        Assert.Contains(catalog, c => c.Kind == TerminalKind.SystemDefault && c.Available);
        Assert.Contains(catalog, c => c.Kind == TerminalKind.Warp);
        Assert.Equal("terminal.group.agent", catalog.First(c => c.Kind == TerminalKind.Warp).GroupKey);
    }

    [Fact]
    public void Picker_stacks_choices_and_keeps_automatic_enabled()
    {
        string saved = UiPrefs.Terminal;
        try
        {
            UiPrefs.Terminal = "";
            using var lang = Loc.Scoped("zh-Hans");
            using var dlg = new TerminalDialog();
            var radios = dlg.Controls.OfType<RadioButton>().OrderBy(c => c.Top).ToList();
            Assert.True(radios.Count >= 8, $"got {radios.Count} radios");
            for (int i = 1; i < radios.Count; i++)
            {
                Assert.True(
                    radios[i].Top >= radios[i - 1].Bottom,
                    $"radio {i} overlaps the one above");
            }

            var auto = radios.Single(r => r.Name == "kind-auto");
            Assert.True(auto.Enabled);
            Assert.True(auto.Checked);
        }
        finally
        {
            UiPrefs.Terminal = saved;
        }
    }

    private static TerminalLaunch SampleLaunch() => new(
        @"C:\Tools\claude.exe",
        @"D:\work\my project",
        "sess-1",
        @"C:\Users\a\.claude-swap-backup\sessions\1-a@x.com",
        ["ANTHROPIC_API_KEY"],
        new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://127.0.0.1:7897" });
}
