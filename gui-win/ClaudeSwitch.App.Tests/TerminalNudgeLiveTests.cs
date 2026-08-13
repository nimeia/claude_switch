using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ClaudeSwitch.App;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// Console attachment is process-wide, so two live nudge tests must never
/// overlap inside the test host.
/// </summary>
[CollectionDefinition("NudgeLive", DisableParallelization = true)]
public class NudgeLiveCollection;

/// <summary>
/// The keystroke-delivery half of waking a stalled terminal, against real
/// console processes on this machine.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in: these open real (short-lived) console windows. Set
/// <c>CLAUDE_SWITCH_NUDGE_E2E=1</c> to run them; <c>CLAUDE_SWITCH_NUDGE_WT=1</c>
/// additionally hosts a reader inside Windows Terminal, the topology the app
/// itself launches. Every test spawns a throwaway reader that appends each
/// line it receives to a transcript file, the way Claude Code appends a
/// submitted prompt — so the whole <see cref="StalledWatch"/> polling loop can
/// run for real.
/// </para>
/// <para>
/// The node reader sets raw mode, which is how Claude Code (node/ink) actually
/// reads its console; the PowerShell reader covers the cooked path. If either
/// topology breaks on a Windows or Windows Terminal update, these are the
/// tests that say so.
/// </para>
/// </remarks>
[Collection("NudgeLive")]
public class TerminalNudgeLiveTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("CLAUDE_SWITCH_NUDGE_E2E") == "1";

    private static bool WithWindowsTerminal =>
        Environment.GetEnvironmentVariable("CLAUDE_SWITCH_NUDGE_WT") == "1"
        || Environment.GetEnvironmentVariable("CLAUDE_SWITCH_NUDGE_E2E") == "1";

    private static bool WithWarp =>
        Environment.GetEnvironmentVariable("CLAUDE_SWITCH_NUDGE_WARP") == "1"
        || Environment.GetEnvironmentVariable("CLAUDE_SWITCH_NUDGE_E2E") == "1";

    private const string Message =
        "Continue from where you left off. Do not repeat work that is already done.";

    private const string UserRecord = """
        {"type":"user","cwd":"D:/work","message":{"role":"user","content":[{"type":"text","text":"do the thing"}]}}
        """;

    private const string WallRecord = """
        {"type":"assistant","cwd":"D:/work","isApiErrorMessage":true,"message":{"role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"Claude AI usage limit reached|1786900000"}]}}
        """;

    // ── the fake terminals ───────────────────────────────────────────────

    /// <summary>A cooked-mode reader: one user transcript record per line.</summary>
    private const string PowerShellReader = """
        param([string]$Transcript, [string]$Ready)
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($Ready, "$PID", $utf8)
        while ($true) {
            $line = [Console]::In.ReadLine()
            if ($null -eq $line) { Start-Sleep -Milliseconds 100; continue }
            $rec = @{ type = "user"; cwd = "D:/work"; message = @{ role = "user"; content = @(@{ type = "text"; text = $line }) } }
            [System.IO.File]::AppendAllText($Transcript, ($rec | ConvertTo-Json -Compress -Depth 5) + "`n", $utf8)
        }
        """;

    /// <summary>A raw-mode reader, the way node (and so Claude Code) reads a console.</summary>
    private const string NodeReader = """
        const fs = require('fs');
        const [transcript, ready] = process.argv.slice(2);
        fs.writeFileSync(ready, String(process.pid));
        process.stdin.setRawMode(true);
        let buf = '';
        process.stdin.on('data', (d) => {
          for (const ch of d.toString('utf8')) {
            if (ch === '\r') {
              const rec = { type: 'user', cwd: 'D:/work', message: { role: 'user', content: [{ type: 'text', text: buf }] } };
              fs.appendFileSync(transcript, JSON.stringify(rec) + '\n');
              buf = '';
            } else {
              buf += ch;
            }
          }
        });
        """;

    /// <summary>Reports its pid, then sleeps without ever reading its console.</summary>
    private const string Sleeper = """
        param([string]$Ready)
        [System.IO.File]::WriteAllText($Ready, "$PID")
        Start-Sleep -Seconds 300
        """;

    private sealed class Reader : IDisposable
    {
        public required int Pid;
        public required string Transcript;

        public void Dispose()
        {
            try { Process.GetProcessById(Pid).Kill(entireProcessTree: true); }
            catch { /* already gone is fine */ }
        }
    }

    private enum Host { Conhost, Cmd, WindowsTerminal, WarpOpenConsole }

    private static string? WarpOpenConsolePath()
    {
        var p = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Warp", "x64", "OpenConsole.exe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    /// Launch a reader process under a specific host and wait for it to report
    /// its pid (which, under Windows Terminal or Warp, is not the process we
    /// spawned). <c>{transcript}</c> and <c>{ready}</c> in the command line
    /// are filled in.
    /// </summary>
    private static Reader StartReader(string dir, string commandTemplate, Host host)
    {
        var transcript = Path.Combine(dir, "in.jsonl");
        var ready = Path.Combine(dir, "ready-" + Guid.NewGuid().ToString("N"));
        var inner = commandTemplate
            .Replace("{transcript}", transcript)
            .Replace("{ready}", ready);

        string launch = host switch
        {
            // /s /c wants one quoted command string; the inner already quotes
            // each argument, so wrap the whole thing.
            Host.Cmd => $"cmd.exe /d /s /c \"{inner}\"",
            Host.WindowsTerminal => $"\"{FindOnPath("wt.exe")}\" -w new -- {inner}",
            Host.WarpOpenConsole => $"\"{WarpOpenConsolePath()}\" --headless {inner}",
            _ => inner,
        };
        uint flags = host is Host.WindowsTerminal or Host.WarpOpenConsole ? 0 : CreateNewConsole;
        var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
        if (!CreateProcessW(null, new StringBuilder(launch), IntPtr.Zero, IntPtr.Zero, false,
                flags, IntPtr.Zero, dir, ref si, out var pi))
        {
            throw new InvalidOperationException($"CreateProcess err={Marshal.GetLastWin32Error()}: {launch}");
        }
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);

        WaitFor(() => File.Exists(ready), "the reader to start", timeoutMs: 30_000);
        var pid = int.Parse(File.ReadAllText(ready).Trim());
        return new Reader { Pid = pid, Transcript = transcript };
    }

    private static string WriteScript(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* a PATH entry that is not a path */ }
        }
        return null;
    }

    private static void WaitFor(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(200);
        }
        throw new TimeoutException($"timed out waiting for {what}");
    }

    private static void WaitForLine(string transcript)
    {
        WaitFor(
            () => File.Exists(transcript) && File.ReadAllText(transcript).Contains(Message),
            "the line to land in the transcript");
        // Delivery is not enough — the whole sentence must survive intact.
        Assert.Contains(Message, File.ReadAllText(transcript));
    }

    // ── delivery, topology by topology ───────────────────────────────────

    [SkippableFact]
    public void Keystrokes_reach_a_cooked_console_reader()
    {
        Skip.IfNot(Enabled, "set CLAUDE_SWITCH_NUDGE_E2E=1 to run");
        using var dir = new TempDir();
        var script = WriteScript(dir.Path, "reader.ps1", PowerShellReader);
        using var reader = StartReader(dir.Path,
            $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{script}\" \"{{transcript}}\" \"{{ready}}\"",
            Host.Conhost);

        var result = TerminalNudge.Send(reader.Pid, Message);

        Assert.True(result.Delivered, result.Detail);
        WaitForLine(reader.Transcript);
    }

    [SkippableFact]
    public void Keystrokes_reach_a_raw_mode_node_reader()
    {
        Skip.IfNot(Enabled, "set CLAUDE_SWITCH_NUDGE_E2E=1 to run");
        var node = FindOnPath("node.exe");
        Skip.If(node is null, "node is not on PATH");
        using var dir = new TempDir();
        var script = WriteScript(dir.Path, "reader.cjs", NodeReader);
        using var reader = StartReader(dir.Path,
            $"\"{node}\" \"{script}\" \"{{transcript}}\" \"{{ready}}\"",
            Host.Conhost);

        var result = TerminalNudge.Send(reader.Pid, Message);

        Assert.True(result.Delivered, result.Detail);
        WaitForLine(reader.Transcript);
    }

    [SkippableFact]
    public void Keystrokes_reach_a_reader_hosted_in_windows_terminal()
    {
        Skip.IfNot(WithWindowsTerminal, "set CLAUDE_SWITCH_NUDGE_WT=1 to run");
        Skip.If(FindOnPath("wt.exe") is null, "Windows Terminal is not installed");
        using var dir = new TempDir();

        // Claude Code is a node program, so prefer the raw-mode reader here.
        var node = FindOnPath("node.exe");
        string commandLine;
        if (node is not null)
        {
            var script = WriteScript(dir.Path, "reader.cjs", NodeReader);
            commandLine = $"\"{node}\" \"{script}\" \"{{transcript}}\" \"{{ready}}\"";
        }
        else
        {
            var script = WriteScript(dir.Path, "reader.ps1", PowerShellReader);
            commandLine = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{script}\" \"{{transcript}}\" \"{{ready}}\"";
        }
        using var reader = StartReader(dir.Path, commandLine, Host.WindowsTerminal);

        var result = TerminalNudge.Send(reader.Pid, Message);

        Assert.True(result.Delivered, result.Detail);
        WaitForLine(reader.Transcript);
    }

    [SkippableFact]
    public void Keystrokes_reach_a_reader_hosted_in_cmd()
    {
        Skip.IfNot(Enabled, "set CLAUDE_SWITCH_NUDGE_E2E=1 to run");
        var node = FindOnPath("node.exe");
        Skip.If(node is null, "node is not on PATH");
        using var dir = new TempDir();
        var script = WriteScript(dir.Path, "reader.cjs", NodeReader);
        using var reader = StartReader(dir.Path,
            $"\"{node}\" \"{script}\" \"{{transcript}}\" \"{{ready}}\"",
            Host.Cmd);

        var result = TerminalNudge.Send(reader.Pid, Message);

        Assert.True(result.Delivered, result.Detail);
        WaitForLine(reader.Transcript);
    }

    [SkippableFact]
    public void Keystrokes_reach_a_reader_hosted_in_warp_openconsole()
    {
        // Warp GUI's own OpenConsole (ConPTY) — the topology a Claude TUI
        // inside Warp actually reads. The GUI shell prompt is a different
        // miss and falls through to a takeover.
        Skip.IfNot(WithWarp, "set CLAUDE_SWITCH_NUDGE_WARP=1 to run");
        Skip.If(WarpOpenConsolePath() is null, "Warp OpenConsole is not installed");
        var node = FindOnPath("node.exe");
        Skip.If(node is null, "node is not on PATH");
        using var dir = new TempDir();
        var script = WriteScript(dir.Path, "reader.cjs", NodeReader);
        using var reader = StartReader(dir.Path,
            $"\"{node}\" \"{script}\" \"{{transcript}}\" \"{{ready}}\"",
            Host.WarpOpenConsole);

        var result = TerminalNudge.Send(reader.Pid, Message);

        Assert.True(result.Delivered, result.Detail);
        WaitForLine(reader.Transcript);
    }

    // ── the whole wakeup flow, with only the takeover faked ──────────────

    /// <summary>Plant a transcript stopped against the quota wall.</summary>
    private static (string Transcript, long MtimeMs) PlantStalledTranscript(string root, string sid)
    {
        var projects = Path.Combine(root, ".claude", "projects", "D--work");
        Directory.CreateDirectory(projects);
        var transcript = Path.Combine(projects, sid + ".jsonl");
        File.WriteAllText(transcript, UserRecord + "\n" + WallRecord + "\n");
        var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(transcript)).ToUnixTimeMilliseconds();
        return (transcript, mtime);
    }

    [SkippableFact]
    public void A_stalled_session_is_woken_in_its_own_terminal()
    {
        Skip.IfNot(Enabled, "set CLAUDE_SWITCH_NUDGE_E2E=1 to run");
        using var dir = new TempDir();
        using var engine = new Engine(dir.Path);
        var store = new AgentRunStore(engine);

        const string sid = "99999999-8888-7777-6666-555555555555";
        var (transcript, mtime) = PlantStalledTranscript(dir.Path, sid);

        // The reader appends into the *session transcript*: exactly what
        // Claude Code does with a submitted prompt, and what the watch polls.
        var script = WriteScript(dir.Path, "reader.ps1", PowerShellReader);
        using var reader = StartReader(dir.Path,
            $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{script}\" \"{transcript}\" \"{{ready}}\"",
            Host.Conhost);

        var takeovers = new List<StalledRecord>();
        StalledRecord? woken = null;
        var watch = new StalledWatch(
            engine, store, _ => "acct", null,
            send: null, // the real TerminalNudge.Send — that is the thing under test
            takeover: r => { takeovers.Add(r); return true; },
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(300));
        watch.NudgeSucceeded += r => woken = r;

        var record = new StalledRecord(sid, "D:/work", 1, null, mtime, 20 * 60_000,
            "rateLimit", "Claude AI usage limit reached", transcript, reader.Pid);
        Assert.True(watch.TryResume(record));

        Assert.NotNull(woken);
        Assert.Empty(takeovers);
        Assert.Contains("Continue from where you left off", File.ReadAllText(transcript));
    }

    [SkippableFact]
    public void A_stalled_session_is_woken_inside_cmd()
    {
        // The launch path the app itself uses: wt → cmd → claude. cmd is the
        // console owner; the recorded pid is the child.
        Skip.IfNot(Enabled, "set CLAUDE_SWITCH_NUDGE_E2E=1 to run");
        var node = FindOnPath("node.exe");
        Skip.If(node is null, "node is not on PATH");
        using var dir = new TempDir();
        using var engine = new Engine(dir.Path);
        var store = new AgentRunStore(engine);

        const string sid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var (transcript, mtime) = PlantStalledTranscript(dir.Path, sid);
        var script = WriteScript(dir.Path, "reader.cjs", NodeReader);
        using var reader = StartReader(dir.Path,
            $"\"{node}\" \"{script}\" \"{transcript}\" \"{{ready}}\"",
            Host.Cmd);

        var takeovers = new List<StalledRecord>();
        StalledRecord? woken = null;
        var watch = new StalledWatch(
            engine, store, _ => "acct", null,
            send: null,
            takeover: r => { takeovers.Add(r); return true; },
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(300));
        watch.NudgeSucceeded += r => woken = r;

        var record = new StalledRecord(sid, "D:/work", 1, null, mtime, 20 * 60_000,
            "rateLimit", "Claude AI usage limit reached", transcript, reader.Pid);
        Assert.True(watch.TryResume(record));

        Assert.NotNull(woken);
        Assert.Empty(takeovers);
        Assert.Contains("Continue from where you left off", File.ReadAllText(transcript));
    }

    [SkippableFact]
    public void A_terminal_that_never_reads_is_taken_over()
    {
        Skip.IfNot(Enabled, "set CLAUDE_SWITCH_NUDGE_E2E=1 to run");
        using var dir = new TempDir();
        using var engine = new Engine(dir.Path);
        var store = new AgentRunStore(engine);

        const string sid = "99999999-8888-7777-6666-555555555555";
        var (transcript, mtime) = PlantStalledTranscript(dir.Path, sid);

        // A live console that accepts input and never reads it: the keystrokes
        // land in the buffer and achieve nothing.
        var script = WriteScript(dir.Path, "sleeper.ps1", Sleeper);
        using var reader = StartReader(dir.Path,
            $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{script}\" \"{{ready}}\"",
            Host.Conhost);

        var takeovers = new List<StalledRecord>();
        StalledRecord? woken = null;
        var watch = new StalledWatch(
            engine, store, _ => "acct", null,
            send: null,
            takeover: r => { takeovers.Add(r); return true; },
            TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200));
        watch.NudgeSucceeded += r => woken = r;

        var record = new StalledRecord(sid, "D:/work", 1, null, mtime, 20 * 60_000,
            "rateLimit", "Claude AI usage limit reached", transcript, reader.Pid);
        Assert.True(watch.TryResume(record)); // the takeover half ran

        Assert.Null(woken);
        Assert.Single(takeovers);
    }

    // ── console spawning ─────────────────────────────────────────────────

    private const uint CreateNewConsole = 0x00000010;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cswitch-nudge-live-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
