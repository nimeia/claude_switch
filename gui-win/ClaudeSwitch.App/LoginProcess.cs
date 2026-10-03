using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSwitch.App;

internal sealed record LoginLaunch(string Id, string ConfigDir,
    IReadOnlyList<string> ScrubEnv, IReadOnlyDictionary<string, string> ExtraEnv)
{
    public static LoginLaunch Parse(JsonNode value) => new(
        value["id"]!.GetValue<string>(), value["configDir"]!.GetValue<string>(),
        value["scrubEnv"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray(),
        value["extraEnv"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()));
}

internal interface ILoginProcess
{
    Task CheckAvailableAsync(CancellationToken token);
    Task LoginAsync(LoginLaunch launch, CancellationToken token);
    Task<JsonNode> StatusAsync(LoginLaunch launch, CancellationToken token);
}

/// <summary>Owns the CLI process, not a Windows Terminal launcher that exits early.</summary>
internal sealed class LoginProcess : ILoginProcess
{
    private string? _exe;
    private readonly bool _showTerminal;
    private sealed record ProcessLease(int Pid, long Started);

    internal LoginProcess(string? executable = null, bool showTerminal = true)
    { _exe = executable; _showTerminal = showTerminal; }

    public async Task CheckAvailableAsync(CancellationToken token)
    {
        _exe ??= await Task.Run(ClaudeCli.FindExecutable, token)
            ?? throw new InvalidOperationException(ClaudeCli.NotFoundMessage);
        var info = BuildStartInfo(_exe, null, "auth", "login", "--help");
        var (code, output) = await CaptureAsync(info, token);
        if (code != 0 || !output.Contains("--claudeai", StringComparison.Ordinal))
            throw new InvalidOperationException(Loc.T("login.unsupported"));
    }

    internal static ProcessStartInfo BuildStartInfo(string exe, LoginLaunch? launch, params string[] args)
    {
        // Only internal, fixed CLI arguments reach cmd.exe. The executable path
        // is expanded once from an environment variable, so %, &, and spaces in
        // an installation path cannot become shell commands.
        bool script = Path.GetExtension(exe).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(exe).Equals(".bat", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(script
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : exe)
        {
            UseShellExecute = false,
            WorkingDirectory = launch?.ConfigDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (script)
        {
            if (args.Any(a => a.Length == 0 || a.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
                throw new ArgumentException("Only fixed CLI arguments are supported.");
            info.Environment["CSWITCH_LOGIN_CLI"] = exe;
            info.Arguments = "/d /v:off /s /c \"\"%CSWITCH_LOGIN_CLI%\" " + string.Join(' ', args) + "\"";
        }
        else foreach (string arg in args) info.ArgumentList.Add(arg);
        if (launch is not null)
        {
            foreach (string key in launch.ScrubEnv) info.Environment.Remove(key);
            foreach (var (key, value) in launch.ExtraEnv) info.Environment[key] = value;
            info.Environment["CLAUDE_CONFIG_DIR"] = launch.ConfigDir;
        }
        return info;
    }

    internal static void ProtectDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException(Loc.T("login.invalidDirectory"));
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new DirectorySecurity();
        acl.SetOwner(identity.User!);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }

    internal static void EnsureStopped(string configDir)
    {
        string marker = Path.Combine(configDir, ".login-process.json");
        if (!File.Exists(marker)) return;
        var lease = JsonSerializer.Deserialize<ProcessLease>(File.ReadAllText(marker))
            ?? throw new IOException(Loc.T("login.processUnknown"));
        Process process;
        try { process = Process.GetProcessById(lease.Pid); }
        catch (ArgumentException) { return; }
        using (process)
        {
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == lease.Started)
                throw new IOException(Loc.T("login.processRunning"));
        }
    }

    public async Task LoginAsync(LoginLaunch launch, CancellationToken token)
    {
        await Task.Run(() => { EnsureStopped(launch.ConfigDir); ProtectDirectory(launch.ConfigDir); }, token);
        token.ThrowIfCancellationRequested();
        var info = BuildStartInfo(_exe!, launch, "auth", "login", "--claudeai");
        info.CreateNoWindow = !_showTerminal;
        // The official terminal remains interactive for copy/paste login-code
        // fallback, while WinForms waits asynchronously and can cancel it.
        using var process = Process.Start(info) ?? throw new IOException(Loc.T("login.startFailed"));
        try
        {
            if (!process.HasExited)
            {
                var lease = new ProcessLease(process.Id, process.StartTime.ToUniversalTime().Ticks);
                File.WriteAllText(Path.Combine(launch.ConfigDir, ".login-process.json"), JsonSerializer.Serialize(lease));
            }
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0)
                throw new IOException(Loc.T("login.exitFailed", process.ExitCode));
        }
        finally { await StopAsync(process); }
    }

    public async Task<JsonNode> StatusAsync(LoginLaunch launch, CancellationToken token)
    {
        await Task.Run(() => { EnsureStopped(launch.ConfigDir); ProtectDirectory(launch.ConfigDir); }, token);
        var (code, output) = await CaptureAsync(BuildStartInfo(_exe!, launch, "auth", "status"), token);
        // Never echo CLI output: it is an untrusted stream that may include secrets.
        if (code != 0) throw new IOException(Loc.T("login.notComplete"));
        try
        {
            var status = JsonNode.Parse(output) ?? throw new JsonException();
            if (status["loggedIn"]?.GetValue<bool>() != true
                || status["authMethod"]?.GetValue<string>() != "claude.ai")
                throw new IOException(Loc.T("login.notComplete"));
            return status;
        }
        catch (JsonException) { throw new IOException(Loc.T("login.invalidStatus")); }
    }

    internal static async Task<(int Code, string Output)> CaptureAsync(ProcessStartInfo info, CancellationToken token)
    {
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = Process.Start(info) ?? throw new IOException(Loc.T("login.startFailed"));
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
            return (process.ExitCode, await stdout);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException(Loc.T("login.timeout")); }
        finally { await StopAsync(process); }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (text.Length + count <= 65536) text.Append(buffer, 0, count);
            // Continue draining a noisy CLI so it cannot deadlock on a full pipe.
        }
        return text.ToString();
    }

    private static async Task StopAsync(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { return; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);
    }
}
