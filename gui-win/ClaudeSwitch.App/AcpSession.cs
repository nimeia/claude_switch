using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSwitch.App;

/// <summary>
/// An ACP (Agent Client Protocol) connection to one Claude Code agent process.
/// </summary>
/// <remarks>
/// <para>
/// The agent runs as a subprocess speaking newline-delimited JSON-RPC 2.0 over
/// stdio. That is what makes a supervised run possible at all: a turn ends with
/// a typed <c>stopReason</c>, or fails with an error carrying an
/// <c>errorKind</c>, instead of leaving us to scrape a transcript and guess
/// whether a quiet terminal is thinking or dead.
/// </para>
/// <para>
/// <b>Why the transport lives in C# and not behind the Rust FFI.</b> The FFI is
/// a synchronous request/response surface; ACP is bidirectional and streaming.
/// While a prompt is in flight the agent sends <i>us</i> requests — most
/// importantly <c>session/request_permission</c>, which has to become a modal
/// dialog and cannot resolve until the user answers. Pumping that through a
/// blocking call would deadlock the UI thread. The retry <i>policy</i> still
/// lives in Rust (<see cref="AcpPolicy"/>); only the plumbing is here.
/// </para>
/// <para>
/// Cannot attach to a <c>claude</c> TUI the user started themselves — ACP has no
/// such notion, an agent is always a subprocess of its client.
/// </para>
/// </remarks>
internal sealed class AcpSession : IAsyncDisposable
{
    /// <summary>ACP protocol version this client speaks.</summary>
    public const int ProtocolVersion = 1;

    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private readonly Dictionary<long, TaskCompletionSource<JsonNode>> _pending = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private long _nextId = 1;
    private volatile bool _disposed;

    /// <summary>Streamed <c>session/update</c> payloads.</summary>
    public event Action<JsonNode>? Update;

    /// <summary>
    /// Tool-call authorization. Returning null denies the call.
    /// </summary>
    /// <remarks>
    /// Async because the honest implementation is a dialog: the agent's turn
    /// stays parked until a human answers.
    /// </remarks>
    public Func<JsonNode, Task<string?>>? PermissionRequested;

    /// <summary>Adapter diagnostics (its stderr).</summary>
    public event Action<string>? Diagnostic;

    /// <summary>Raised once the adapter process exits.</summary>
    public event Action? Exited;

    public JsonNode? AgentCapabilities { get; private set; }
    public string AgentName { get; private set; } = "unknown";

    /// <summary>Whether the agent can resume a previous conversation.</summary>
    public bool SupportsLoadSession =>
        AgentCapabilities?["loadSession"]?.GetValue<bool>() ?? false;

    public bool HasExited
    {
        get
        {
            try { return _proc.HasExited; }
            catch { return true; }
        }
    }

    /// <summary>
    /// The adapter's process id, or null once it has exited.
    /// </summary>
    /// <remarks>
    /// Worth exposing: it identifies exactly which agent a run is talking to,
    /// which matters when several supervised runs are open and when a fault test
    /// needs to kill this one rather than every <c>node.exe</c> on the machine.
    /// </remarks>
    public int? ProcessId
    {
        get
        {
            try { return _proc.HasExited ? null : _proc.Id; }
            catch (Exception ex) when (ex is InvalidOperationException or SystemException)
            {
                return null;
            }
        }
    }

    private AcpSession(Process proc, StreamWriter stdin)
    {
        _proc = proc;
        _stdin = stdin;
    }

    /// <summary>Spawn the adapter and complete the <c>initialize</c> handshake.</summary>
    /// <param name="launch">Working directory, account profile and proxy.</param>
    public static async Task<AcpSession> ConnectAsync(AcpLaunch launch, CancellationToken ct = default)
    {
        var psi = launch.BuildStartInfo();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!proc.Start())
            throw new InvalidOperationException(Loc.T("acp.err.spawn"));

        var session = new AcpSession(proc, proc.StandardInput);
        proc.Exited += (_, _) => session.OnProcessExit();

        _ = Task.Run(() => session.ReadLoopAsync(proc.StandardOutput), session._life.Token);
        _ = Task.Run(() => session.ReadStderrAsync(proc.StandardError), session._life.Token);

        var init = await session.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = true, ["writeTextFile"] = true },
                ["terminal"] = false,
            },
        }, ct).ConfigureAwait(false);

        session.AgentCapabilities = init["agentCapabilities"];
        session.AgentName = init["agentInfo"]?["name"]?.GetValue<string>() ?? "unknown";
        return session;
    }

    /// <summary>Start a fresh conversation rooted at <paramref name="cwd"/>.</summary>
    public async Task<AcpSessionInfo> NewSessionAsync(string cwd, CancellationToken ct = default)
    {
        var res = await RequestAsync("session/new", new JsonObject
        {
            ["cwd"] = cwd,
            ["mcpServers"] = new JsonArray(),
        }, ct).ConfigureAwait(false);

        string id = res["sessionId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("session/new returned no sessionId");

        var modes = new List<string>();
        if (res["modes"]?["availableModes"] is JsonArray arr)
        {
            foreach (var m in arr)
            {
                if (m?["id"]?.GetValue<string>() is { } mid) modes.Add(mid);
            }
        }
        return new AcpSessionInfo(id, modes, res["modes"]?["currentModeId"]?.GetValue<string>());
    }

    /// <summary>
    /// Re-attach to an existing conversation. History replays through
    /// <see cref="Update"/> before this returns.
    /// </summary>
    public Task LoadSessionAsync(string sessionId, string cwd, CancellationToken ct = default) =>
        RequestAsync("session/load", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["cwd"] = cwd,
            ["mcpServers"] = new JsonArray(),
        }, ct);

    /// <summary>Set the permission mode (<c>acceptEdits</c>, <c>plan</c>, …).</summary>
    public Task SetModeAsync(string sessionId, string modeId, CancellationToken ct = default) =>
        RequestAsync("session/set_mode", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["modeId"] = modeId,
        }, ct);

    /// <summary>Run one turn. The result carries <c>stopReason</c> and <c>usage</c>.</summary>
    public Task<JsonNode> PromptAsync(string sessionId, string text, CancellationToken ct = default) =>
        RequestAsync("session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        }, ct);

    /// <summary>Interrupt an in-flight turn. Fire-and-forget notification.</summary>
    public async Task CancelAsync(string sessionId)
    {
        try
        {
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "session/cancel",
                ["params"] = new JsonObject { ["sessionId"] = sessionId },
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Cancelling a session on a dead adapter is already the outcome we wanted.
        }
    }

    private async Task<JsonNode> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;

        await SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        }).ConfigureAwait(false);

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task SendAsync(JsonObject frame)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stdin.WriteAsync(frame.ToJsonString()).ConfigureAwait(false);
            await _stdin.WriteAsync('\n').ConfigureAwait(false);
            await _stdin.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(StreamReader stdout)
    {
        try
        {
            while (await stdout.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                JsonNode? msg;
                try
                {
                    msg = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    // The adapter occasionally prints non-JSON noise; it is not
                    // a protocol frame and must not tear the connection down.
                    continue;
                }
                if (msg is null) continue;
                Dispatch(msg);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Adapter died; OnProcessExit completes the waiters.
        }
        finally
        {
            OnProcessExit();
        }
    }

    private void Dispatch(JsonNode msg)
    {
        string? method = msg["method"]?.GetValue<string>();
        var idNode = msg["id"];

        // Response to one of our requests.
        if (idNode is not null && method is null)
        {
            if (!TryGetId(idNode, out long id)) return;
            TaskCompletionSource<JsonNode>? tcs;
            lock (_pending)
            {
                if (!_pending.Remove(id, out tcs)) return;
            }
            if (msg["error"] is { } err)
            {
                tcs.TrySetException(AcpRpcException.FromErrorObject(err));
            }
            else
            {
                tcs.TrySetResult(msg["result"] ?? new JsonObject());
            }
            return;
        }

        // Request from the agent: must be answered or the turn never completes.
        if (idNode is not null && method is not null)
        {
            _ = Task.Run(() => ServeAsync(idNode.DeepClone(), method, msg["params"]));
            return;
        }

        if (method == "session/update" && msg["params"]?["update"] is { } update)
        {
            Update?.Invoke(update);
        }
    }

    private static bool TryGetId(JsonNode node, out long id)
    {
        try
        {
            id = node.GetValue<long>();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            id = 0;
            return false;
        }
    }

    private async Task ServeAsync(JsonNode id, string method, JsonNode? parameters)
    {
        JsonObject reply;
        try
        {
            switch (method)
            {
                case "session/request_permission":
                {
                    string? optionId = PermissionRequested is null || parameters is null
                        ? null
                        : await PermissionRequested(parameters).ConfigureAwait(false);
                    reply = Ok(id, optionId is null
                        ? new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } }
                        : new JsonObject
                        {
                            ["outcome"] = new JsonObject
                            {
                                ["outcome"] = "selected",
                                ["optionId"] = optionId,
                            },
                        });
                    break;
                }
                case "fs/read_text_file":
                {
                    string path = parameters?["path"]?.GetValue<string>() ?? "";
                    string content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                    reply = Ok(id, new JsonObject { ["content"] = content });
                    break;
                }
                case "fs/write_text_file":
                {
                    string path = parameters?["path"]?.GetValue<string>() ?? "";
                    string content = parameters?["content"]?.GetValue<string>() ?? "";
                    await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
                    reply = Ok(id, new JsonObject());
                    break;
                }
                default:
                    reply = Error(id, -32601, $"method not supported by this client: {method}");
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            reply = Error(id, -32603, ex.Message);
        }

        try
        {
            await SendAsync(reply).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Adapter is gone; the pending turn fails on its own.
        }
    }

    private static JsonObject Ok(JsonNode id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private async Task ReadStderrAsync(StreamReader stderr)
    {
        try
        {
            while (await stderr.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 0) Diagnostic?.Invoke(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Process gone.
        }
    }

    /// <summary>
    /// Fail every in-flight request once the adapter is gone.
    /// </summary>
    /// <remarks>
    /// Without this a killed adapter leaves the caller awaiting forever, which
    /// is exactly the hang auto-continue exists to recover from.
    /// </remarks>
    private void OnProcessExit()
    {
        List<TaskCompletionSource<JsonNode>> waiters;
        lock (_pending)
        {
            waiters = [.. _pending.Values];
            _pending.Clear();
        }
        foreach (var w in waiters)
        {
            w.TrySetException(new AcpDisconnectedException());
        }
        Exited?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _life.CancelAsync().ConfigureAwait(false);
        try { _stdin.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }

        try
        {
            if (!_proc.HasExited)
            {
                // Closing stdin asks the adapter to exit; kill if it will not.
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await _proc.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _proc.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            // Already gone.
        }

        _proc.Dispose();
        _life.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>What <c>session/new</c> reported.</summary>
internal sealed record AcpSessionInfo(string SessionId, IReadOnlyList<string> AvailableModes, string? CurrentMode);

/// <summary>The agent answered a request with a JSON-RPC error object.</summary>
internal sealed class AcpRpcException : Exception
{
    public int Code { get; }

    /// <summary>ACP's machine-readable failure class, e.g. <c>authentication_failed</c>.</summary>
    public string? ErrorKind { get; }

    private AcpRpcException(int code, string message, string? errorKind) : base(message)
    {
        Code = code;
        ErrorKind = errorKind;
    }

    public static AcpRpcException FromErrorObject(JsonNode err)
    {
        int code = 0;
        try { code = err["code"]?.GetValue<int>() ?? 0; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { }

        string message = err["message"]?.GetValue<string>() ?? err.ToJsonString();
        string? kind = err["data"]?["errorKind"]?.GetValue<string>();
        return new AcpRpcException(code, message, kind);
    }
}

/// <summary>The adapter exited or closed stdio before answering.</summary>
internal sealed class AcpDisconnectedException : Exception
{
    public AcpDisconnectedException() : base("adapter disconnected") { }
}

/// <summary>
/// How to launch the adapter subprocess.
/// </summary>
/// <remarks>
/// Two environment facts decide whether a launched session works at all:
/// <list type="bullet">
/// <item><description>
/// <b>The proxy must be injected explicitly.</b> Claude Code's own CLI picks up
/// the Windows Internet Settings proxy; the Agent SDK under the adapter does
/// not. On a proxied network its first API call fails with
/// <c>403 "Request not allowed"</c> — which reads exactly like an auth failure
/// and is nothing of the sort.
/// </description></item>
/// <item><description>
/// <b><c>CLAUDE_CONFIG_DIR</c> selects the account.</b> Pointing it at a session
/// profile makes the agent authenticate as that stored account and keep its
/// transcripts inside the profile.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class AcpLaunch
{
    /// <summary>npm package providing the adapter.</summary>
    public const string AdapterPackage = "@agentclientprotocol/claude-agent-acp";

    /// <summary>Variables that would override the account this launch names.</summary>
    private static readonly string[] AuthOverrideEnv =
    [
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "CLAUDE_CODE_OAUTH_TOKEN",
        "CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR",
        "CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR",
    ];

    public required string WorkingDirectory { get; init; }

    /// <summary>Session profile for <c>CLAUDE_CONFIG_DIR</c>; null uses the default login.</summary>
    public string? ConfigDir { get; init; }

    /// <summary>Proxy URL to inject, or null for a direct connection.</summary>
    public string? Proxy { get; init; }

    public ProcessStartInfo BuildStartInfo()
    {
        if (!Directory.Exists(WorkingDirectory))
            throw new DirectoryNotFoundException(Loc.T("acp.err.workDir", WorkingDirectory));

        string node = ClaudeCli.Find("node")
            ?? throw new FileNotFoundException(Loc.T("acp.err.node"));
        string adapter = FindAdapter()
            ?? throw new FileNotFoundException(Loc.T("acp.err.adapter", AdapterPackage));

        var psi = new ProcessStartInfo(node)
        {
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(adapter);

        foreach (var name in AuthOverrideEnv) psi.Environment.Remove(name);
        if (ConfigDir is { Length: > 0 }) psi.Environment["CLAUDE_CONFIG_DIR"] = ConfigDir;
        if (Proxy is { Length: > 0 })
        {
            // Node's fetch honours the lowercase forms too; set both so the
            // adapter and anything it shells out to agree.
            psi.Environment["HTTPS_PROXY"] = Proxy;
            psi.Environment["HTTP_PROXY"] = Proxy;
            psi.Environment["https_proxy"] = Proxy;
            psi.Environment["http_proxy"] = Proxy;
        }
        return psi;
    }

    /// <summary>
    /// Locate the adapter's entry point: explicit override, then a repo-local
    /// install, then the usual global npm roots.
    /// </summary>
    public static string? FindAdapter()
    {
        foreach (var root in CandidateModuleRoots())
        {
            string p = Path.Combine(root, "@agentclientprotocol", "claude-agent-acp", "dist", "index.js");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Search roots, most specific first. Public so tests can assert the order.</summary>
    public static IEnumerable<string> CandidateModuleRoots()
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_ACP_ADAPTER") is { Length: > 0 } overridePath)
        {
            // An explicit full path to index.js wins; yield its node_modules so
            // the joined lookup below still finds it.
            string? dir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(
                Path.GetDirectoryName(overridePath))));
            if (dir is { Length: > 0 }) yield return dir;
        }

        // Walk up from the app directory, then the working directory: covers a
        // dev checkout's tools/acp/node_modules from either location.
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                yield return Path.Combine(dir.FullName, "tools", "acp", "node_modules");
                yield return Path.Combine(dir.FullName, "node_modules");
                dir = dir.Parent;
            }
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, "AppData", "Roaming", "npm", "node_modules");
        yield return Path.Combine(home, ".npm-global", "lib", "node_modules");
    }
}
