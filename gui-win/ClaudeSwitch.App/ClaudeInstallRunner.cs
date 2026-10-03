using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSwitch.App;

internal sealed record ClaudeInstallProgress(string Key, bool CanCancel);
internal interface IClaudeInstallService
{
    Task<ClaudeInstallation> DetectAsync(CancellationToken token);
    Task<ClaudeInstallation> InstallAsync(ClaudeInstallation expected, Action<ClaudeInstallProgress> progress,
        Action<string> log, CancellationToken token);
}

internal sealed class ClaudeInstallRunner : IClaudeInstallService
{
    private readonly IClaudeCommandRunner _commands;
    private readonly Func<CancellationToken, Task<ClaudeInstallation>> _detect;
    private readonly SessionMode.ResolvedProxy? _proxy;
    private readonly Func<CancellationToken, Task<bool>> _hasSessions;
    private readonly Func<ClaudeInstallSource, string?> _policy;
    private readonly Func<CancellationToken, Task<string>> _download;

    internal ClaudeInstallRunner(SessionMode.ResolvedProxy? proxy = null)
    {
        _proxy = proxy;
        _commands = new ClaudeCommandRunner(proxy);
        _detect = token => new ClaudeInstallationDetector(_commands).DetectAsync(token);
        _hasSessions = HasSessionsAsync;
        _policy = ReadPolicy;
        _download = DownloadInstallerAsync;
    }

    internal ClaudeInstallRunner(IClaudeCommandRunner commands,
        Func<CancellationToken, Task<ClaudeInstallation>> detect,
        Func<CancellationToken, Task<bool>> hasSessions, Func<ClaudeInstallSource, string?> policy,
        Func<CancellationToken, Task<string>> download)
    { _commands = commands; _detect = detect; _hasSessions = hasSessions; _policy = policy; _download = download; }

    public Task<ClaudeInstallation> DetectAsync(CancellationToken token)
    {
        ClaudeInstallationDetector.RefreshPath();
        return _detect(token);
    }

    public async Task<ClaudeInstallation> InstallAsync(ClaudeInstallation expected,
        Action<ClaudeInstallProgress> progress, Action<string> log, CancellationToken token)
    {
        using var gate = new Semaphore(1, 1, @"Local\CCAccountSwitcher.ClaudeInstall");
        if (!gate.WaitOne(0)) throw new IOException(Loc.T("install.otherInstance"));
        string? installer = null;
        try
        {
            progress(new("install.checking", true));
            var current = await _detect(token);
            EnsureSameTarget(expected, current);
            if (!current.CanChange) throw new IOException(current.Problem ?? Loc.T("install.unknownSource"));
            if (_policy(current.Source) is { } policy) throw new IOException(policy);
            if (await _hasSessions(token)) throw new IOException(Loc.T("install.sessionsRunning"));
            Version? target = null;
            if (current.Source is ClaudeInstallSource.Npm or ClaudeInstallSource.Volta)
            {
                progress(new("install.resolving", true));
                string[] query = ["view", "@anthropic-ai/claude-code", "dist-tags.latest", "--json",
                    "--prefer-online", "--fetch-timeout=60000", "--fetch-retries=1"];
                if (current.Source == ClaudeInstallSource.Volta) query = ["run", "npm", .. query];
                var latest = await _commands.RunAsync(new(current.Manager!, query), token, log,
                    timeout: TimeSpan.FromSeconds(90));
                if (latest.ExitCode != 0 || !Version.TryParse(latest.Output.Trim().Trim('"'), out target)
                    || target.Build < 0 || target.Revision >= 0)
                    throw new IOException(Loc.T("install.versionQueryFailed"));
                if (current.Version is not null && target < current.Version)
                    throw new IOException(Loc.T("install.wouldDowngrade"));
            }
            var command = BuildCommand(current, target);
            if (current.Source == ClaudeInstallSource.WinGet)
                command = WithWinGetProxy(command!, _proxy);
            if (command is null)
            {
                progress(new("install.downloading", true));
                installer = await _download(token);
                command = NativeInstallCommand(installer, _proxy);
            }
            // Downloads may take minutes: recheck the target and sessions before
            // invoking a tool that writes files.
            var rechecked = await _detect(token);
            EnsureSameTarget(current, rechecked);
            if (target is not null && rechecked.Version is not null && target < rechecked.Version)
                throw new IOException(Loc.T("install.wouldDowngrade"));
            if (_policy(current.Source) is { } changedPolicy) throw new IOException(changedPolicy);
            if (await _hasSessions(token)) throw new IOException(Loc.T("install.sessionsRunning"));
            token.ThrowIfCancellationRequested();
            // The package manager owns replacement/rollback. Do not kill it in
            // the middle of a write, even if the user requests to close the window.
            progress(new("install.applying", false));
            var result = await _commands.RunAsync(command, CancellationToken.None, log);
            progress(new("install.verifying", false));
            ClaudeInstallationDetector.RefreshPath();
            ClaudeCli.InvalidateVersionCache();
            var installed = await _detect(CancellationToken.None);
            // WinGet returns UPDATE_NOT_APPLICABLE when there is no newer release.
            // Still verify the active executable below before treating that as success.
            bool noWinGetUpdate = current.Source == ClaudeInstallSource.WinGet
                && result.ExitCode == unchecked((int)0x8A15002B);
            if (result.ExitCode != 0 && !noWinGetUpdate) throw new IOException(Loc.T("install.exitFailed", result.ExitCode));
            VerifyResult(current, installed);
            if (target is not null && installed.Version < target)
                throw new IOException(Loc.T("install.verifyFailed"));
            ClaudeCli.SetInstalledVersion(installed.Version);
            return installed;
        }
        finally
        {
            // Only the exact temporary file created for this operation is removed.
            if (installer is not null)
                try { File.Delete(installer); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            gate.Release();
        }
    }

    internal static void EnsureSameTarget(ClaudeInstallation expected, ClaudeInstallation actual)
    {
        if (expected.Source != actual.Source || !EqualPath(expected.Executable, actual.Executable)
            || !EqualPath(expected.Manager, actual.Manager))
            throw new IOException(Loc.T("install.targetChanged"));
    }
    private static bool EqualPath(string? a, string? b) => a is null || b is null
        ? a == b : ClaudeInstallationDetector.SamePath(a, b);

    internal static void VerifyResult(ClaudeInstallation before, ClaudeInstallation after)
    {
        if (after.Version is null || after.Executable is null || !after.CanChange)
            throw new IOException(Loc.T("install.verifyFailed"));
        if (before.Source == ClaudeInstallSource.None)
        {
            if (after.Source != ClaudeInstallSource.Native) throw new IOException(Loc.T("install.targetChanged"));
        }
        else EnsureSameTarget(before, after);
        if (before.Version is not null && after.Version < before.Version)
            throw new IOException(Loc.T("install.downgrade"));
    }

    internal static ClaudeCommand? BuildCommand(ClaudeInstallation installation, Version? target = null) => installation.Source switch
    {
        ClaudeInstallSource.None => null,
        ClaudeInstallSource.Native => new(installation.Executable!, "update"),
        ClaudeInstallSource.Volta => new(installation.Manager!, "install", "@anthropic-ai/claude-code@" + (target?.ToString() ?? "latest")),
        ClaudeInstallSource.Npm => new(installation.Manager!, "install", "-g", "@anthropic-ai/claude-code@" + (target?.ToString() ?? "latest"),
            "--fetch-timeout=60000", "--fetch-retries=1"),
        ClaudeInstallSource.WinGet => new(installation.Manager!, "upgrade", "--id", "Anthropic.ClaudeCode",
            "--exact", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity"),
        _ => throw new IOException(Loc.T("install.unknownSource")),
    };

    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    internal static ClaudeCommand WithWinGetProxy(ClaudeCommand command, SessionMode.ResolvedProxy? proxy) =>
        proxy?.Scrub == true ? command with { Arguments = [.. command.Arguments, "--no-proxy"] }
        : proxy?.Url is { } url ? command with { Arguments = [.. command.Arguments, "--proxy", url] }
        : command;

    internal static ClaudeCommand NativeInstallCommand(string installer, SessionMode.ResolvedProxy? proxy)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            if ($env:CSWITCH_INSTALL_DIRECT -eq '1') { [Net.WebRequest]::DefaultWebProxy = $null }
            elseif ($env:CSWITCH_INSTALL_PROXY) {
                $uri = [Uri]$env:CSWITCH_INSTALL_PROXY
                $builder = [UriBuilder]::new($uri); $builder.UserName = ''; $builder.Password = ''
                $p = [Net.WebProxy]::new($builder.Uri)
                if ($uri.UserInfo) {
                    $parts = $uri.UserInfo.Split(':', 2)
                    $password = ''; if ($parts.Length -gt 1) { $password = [Uri]::UnescapeDataString($parts[1]) }
                    $p.Credentials = [Net.NetworkCredential]::new([Uri]::UnescapeDataString($parts[0]), $password)
                }
                [Net.WebRequest]::DefaultWebProxy = $p
            }
            $global:LASTEXITCODE = 0
            & $env:CSWITCH_INSTALL_SCRIPT
            $ok = $?
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            if (-not $ok) { exit 1 }
            """;
        return new(PowerShell, "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            Environment = new Dictionary<string, string> {
                ["CSWITCH_INSTALL_SCRIPT"] = installer,
                ["CSWITCH_INSTALL_PROXY"] = proxy?.Url ?? "",
                ["CSWITCH_INSTALL_DIRECT"] = proxy?.Scrub == true ? "1" : "0",
            }
        };
    }

    private async Task<bool> HasSessionsAsync(CancellationToken token)
    {
        // Also catches older npm releases running under node.exe. Query once
        // before mutation, not on the UI thread or on a recurring timer.
        const string script = """
            $ErrorActionPreference = 'Stop'
            $found = @(Get-CimInstance Win32_Process -Filter "Name='claude.exe' OR Name='node.exe'" | Where-Object {
                $_.Name -eq 'claude.exe' -or $_.CommandLine -match '@anthropic-ai[\\/]claude-code[\\/]'
            })
            if ($found.Count -gt 0) { 'busy' } else { 'idle' }
            """;
        var result = await _commands.RunAsync(new(PowerShell, "-NoLogo", "-NoProfile", "-NonInteractive",
            "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))), token,
            timeout: TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0 || result.Output.Trim() is not ("busy" or "idle"))
            throw new IOException(Loc.T("install.processCheckFailed"));
        return result.Output.Trim() == "busy";
    }

    private async Task<string> DownloadInstallerAsync(CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(90));
        try { return await DownloadInstallerCoreAsync(limit.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException(Loc.T("install.downloadTimeout")); }
    }

    private async Task<string> DownloadInstallerCoreAsync(CancellationToken token)
    {
        using var handler = new HttpClientHandler();
        if (_proxy?.Scrub == true) handler.UseProxy = false;
        else if (_proxy?.Url is { } url)
        {
            var uri = new Uri(url);
            var builder = new UriBuilder(uri) { UserName = "", Password = "" };
            var webProxy = new WebProxy(builder.Uri);
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                webProxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]),
                    parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            }
            handler.Proxy = webProxy;
        }
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("https://claude.ai/install.ps1",
            HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        using var input = await response.Content.ReadAsStreamAsync(token);
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while ((n = await input.ReadAsync(buffer, token)) > 0)
        {
            if (content.Length + n > 2 * 1024 * 1024) throw new IOException(Loc.T("install.downloadFailed"));
            content.Write(buffer, 0, n);
        }
        if (content.Length == 0) throw new IOException(Loc.T("install.downloadFailed"));
        string path = Path.Combine(Path.GetTempPath(), "cswitch-claude-install-" + Guid.NewGuid().ToString("N") + ".ps1");
        try { await File.WriteAllBytesAsync(path, content.ToArray(), token); return path; }
        catch { File.Delete(path); throw; }
    }

    internal static string? ReadPolicy(ClaudeInstallSource source)
    {
        if (Environment.GetEnvironmentVariable("DISABLE_UPDATES") is "1" or "true") return Loc.T("install.policy");
        string config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        foreach (string path in new[] { Path.Combine(config, "settings.json"), Path.Combine(config, "settings.local.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClaudeCode", "managed-settings.json") })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions {
                    AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (node?["env"]?["DISABLE_UPDATES"]?.ToString() is "1" or "true") return Loc.T("install.policy");
                // Package managers cannot enforce Claude's managed version cap.
                if (source != ClaudeInstallSource.Native && node?["requiredMaximumVersion"] is not null)
                    return Loc.T("install.policy");
            }
            catch (Exception) { return Loc.T("install.policyUnreadable"); }
        }
        return null;
    }
}
