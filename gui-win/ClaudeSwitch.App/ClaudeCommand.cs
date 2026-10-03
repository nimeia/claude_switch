using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeSwitch.App;

internal sealed record ClaudeCommand(string Executable, params string[] Arguments)
{
    internal IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}
internal sealed record ClaudeCommandResult(int ExitCode, string Output, string Error);

internal interface IClaudeCommandRunner
{
    Task<ClaudeCommandResult> RunAsync(ClaudeCommand command, CancellationToken token,
        Action<string>? log = null, TimeSpan? timeout = null);
}

/// <summary>Drains both pipes even after the output limit, and owns only its child tree.</summary>
internal sealed class ClaudeCommandRunner(SessionMode.ResolvedProxy? proxy = null) : IClaudeCommandRunner
{
    internal static ProcessStartInfo StartInfo(ClaudeCommand command, SessionMode.ResolvedProxy? proxy = null)
    {
        bool batch = Path.GetExtension(command.Executable).ToLowerInvariant() is ".cmd" or ".bat";
        var info = new ProcessStartInfo(batch
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
            : command.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (batch)
        {
            // Batch providers only accept fixed package/flag arguments. Paths are
            // passed once through an environment variable, never interpolated as code.
            if (command.Arguments.Any(a => !Regex.IsMatch(a, @"\A[a-zA-Z0-9@_./=:-]+\z")))
                throw new ArgumentException("Unsupported batch argument.");
            info.Environment["CSWITCH_CLAUDE_COMMAND"] = command.Executable;
            info.Arguments = "/d /v:off /s /c \"\"%CSWITCH_CLAUDE_COMMAND%\" "
                + string.Join(' ', command.Arguments) + "\"";
        }
        else foreach (string arg in command.Arguments) info.ArgumentList.Add(arg);
        foreach (var (key, value) in command.Environment) info.Environment[key] = value;
        if (proxy is not null)
        {
            foreach (string key in proxy.ScrubKeys) info.Environment.Remove(key);
            foreach (var (key, value) in proxy.Env) info.Environment[key] = value;
            // npm/Volta do not all read the same proxy environment variables.
            if (proxy.Scrub || proxy.Url is not null)
            {
                info.Environment["npm_config_proxy"] = proxy.Url ?? "";
                info.Environment["npm_config_https_proxy"] = proxy.Url ?? "";
            }
        }
        return info;
    }

    public async Task<ClaudeCommandResult> RunAsync(ClaudeCommand command, CancellationToken token,
        Action<string>? log = null, TimeSpan? timeout = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (timeout is { } duration) limit.CancelAfter(duration);
        limit.Token.ThrowIfCancellationRequested();
        using var process = Process.Start(StartInfo(command, proxy))
            ?? throw new IOException(Loc.T("install.startFailed"));
        try
        {
            var stdout = DrainAsync(process.StandardOutput, limit.Token, log);
            var stderr = DrainAsync(process.StandardError, limit.Token, log);
            await Task.WhenAll(process.WaitForExitAsync(limit.Token), stdout, stderr).ConfigureAwait(false);
            return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException(Loc.T("install.timeout")); }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken token, Action<string>? log)
    {
        var text = new StringBuilder();
        var buffer = new char[2048];
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            int keep = Math.Min(n, Math.Max(0, 65536 - text.Length));
            text.Append(buffer, 0, keep);
            log?.Invoke(new string(buffer, 0, n));
        }
        return text.ToString();
    }
}

/// <summary>Bounded buffer polled by the UI; a noisy installer cannot flood BeginInvoke.</summary>
internal sealed class ClaudeInstallLog
{
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private bool _changed;
    internal void Append(string text)
    {
        lock (_gate)
        {
            _text.Append(text);
            if (_text.Length > 32768) _text.Remove(0, _text.Length - 32768);
            _changed = true;
        }
    }
    internal string? Take()
    {
        lock (_gate)
        {
            if (!_changed) return null;
            _changed = false;
            // Redact after assembling chunks, including credentials split across reads.
            string value = Regex.Replace(_text.ToString(), @"\x1B\[[0-?]*[ -/]*[@-~]", "");
            value = Regex.Replace(value, @"(?i)(https?://)[^\s/@]+(?::[^\s/@]*)?@", "$1***@");
            return Regex.Replace(value, @"(?i)(bearer\s+|(?:token|password|secret|api[_-]?key)[\s:=]+)[^\s,;]+", "$1***");
        }
    }
}
