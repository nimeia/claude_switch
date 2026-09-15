using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeSwitch.App;

/// <summary>
/// The transcript area of a supervised-run window.
/// </summary>
/// <remarks>
/// <para>
/// A local web page when WebView2 is available. A run's output is structured —
/// Markdown replies, tool calls with diffs and command output, a plan — and a
/// text box can only flatten it. Plain text when WebView2 is not available: the
/// runtime ships with Windows 11 but can be missing or broken, and a run must
/// stay watchable either way.
/// </para>
/// <para>
/// Keeps every line it was given, so whichever view is showing can be rebuilt
/// from nothing — after the page reloads, or when the web view fails and the
/// text view takes over mid-run.
/// </para>
/// </remarks>
internal sealed class TranscriptHost : Panel
{
    private readonly List<RunLine> _history = [];
    private WebTranscriptView? _web;
    private PlainTranscriptView? _plain;

    public TranscriptHost(string workDir)
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.BgSurface;

        // Forcing the text view is also how to look at it without breaking the runtime.
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PLAIN_TRANSCRIPT") == "1")
        {
            ShowPlain(reason: null);
        }
        else
        {
            _web = new WebTranscriptView(workDir, () => _history);
            // Deferred: the failure can be raised from inside a WebView2 callback,
            // and the control must not be disposed underneath its own event.
            _web.Failed += ex =>
            {
                if (IsHandleCreated) BeginInvoke(() => ShowPlain(ex.Message));
                else ShowPlain(ex.Message);
            };
            Controls.Add(_web.Control);
            _web.Start();
        }

        Theme.Changed += OnThemeChanged;
    }

    /// <summary>Whether everything appended so far is on screen. For the layout probe.</summary>
    public bool IsSettled => _plain is not null || _web is { IsSettled: true };

    public void Append(RunLine line)
    {
        _history.Add(line);
        if (_history.Count > LiveRun.BacklogLimit)
        {
            // Same bound as the run's own backlog, and for the same reason.
            int removed = _history.Count - LiveRun.BacklogLimit;
            _history.RemoveRange(0, removed);
            _web?.OnHistoryTrimmed(removed);
        }

        if (_plain is not null) _plain.Render(line);
        else _web?.Notify();
    }

    public void SetBusy(bool busy) => _web?.SetBusy(busy);

    private void ShowPlain(string? reason)
    {
        if (_plain is not null) return;
        if (_web is not null)
        {
            Controls.Remove(_web.Control);
            _web.Dispose();
            _web = null;
        }

        _plain = new PlainTranscriptView();
        Padding = new Padding(Theme.Scale(this, 12));
        Controls.Add(_plain);
        if (reason is not null)
        {
            _plain.Render(new RunLine(RunLineKind.Notice, $"{Loc.T("acp.view.fallback")} ({reason})"));
        }
        foreach (var line in _history) _plain.Render(line);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        BackColor = Theme.BgSurface;
        _plain?.ApplyTheme();
        _web?.ApplyTheme();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            _web?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The transcript as coloured text — what the window showed before the web view,
/// and what it falls back to without one.
/// </summary>
internal sealed class PlainTranscriptView : RichTextBox
{
    public PlainTranscriptView()
    {
        Dock = DockStyle.Fill;
        ReadOnly = true;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
        Font = new Font(FontFamily.GenericMonospace, Theme.FontBody.SizeInPoints);
        DetectUrls = false;
        // The transcript is a log, not an editor: keep the newest line in view
        // without stealing focus from the prompt box.
        HideSelection = true;
    }

    public void Render(RunLine line)
    {
        switch (line.Kind)
        {
            case RunLineKind.Assistant:
                // Chunks arrive mid-sentence; they must not each start a line.
                Append(line.Text, Theme.TextPrimary);
                return;
            case RunLineKind.Prompt:
                AppendLine("", Theme.TextPrimary);
                AppendLine(line.Text, Theme.Primary);
                return;
            case RunLineKind.Tool:
                AppendLine(line.Text, Theme.TextSecondary);
                return;
            case RunLineKind.Warning:
                AppendLine(line.Text, Theme.Warning);
                return;
            case RunLineKind.Error:
                AppendLine(line.Text, Theme.Danger);
                return;
            case RunLineKind.Thought:
            case RunLineKind.ToolUpdate:
            case RunLineKind.Plan:
                // Structure a line of text cannot lay out; only the web view draws it.
                return;
            default:
                AppendLine(line.Text, Theme.TextMuted);
                return;
        }
    }

    public void ApplyTheme()
    {
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
    }

    private void Append(string text, Color color)
    {
        SelectionStart = TextLength;
        SelectionLength = 0;
        SelectionColor = color;
        AppendText(text);
        ScrollToCaret();
    }

    private void AppendLine(string text, Color color) => Append(text + Environment.NewLine, color);
}

/// <summary>
/// The transcript as a local web page in WebView2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is fetched and nothing is unpacked.</b> The page, its script and
/// xterm.js are embedded resources, answered from memory for every request under
/// <see cref="Origin"/> — a host that is never resolved. Navigation anywhere else
/// is cancelled; a link in a reply goes to the browser through a message.
/// </para>
/// <para>
/// <b>The page holds no state the app does not.</b> Lines are posted in batches
/// as they arrive, and a page that (re)loads says so and is sent everything from
/// the start. A reload, a crashed renderer and a first load are the same case.
/// </para>
/// </remarks>
internal sealed class WebTranscriptView : IDisposable
{
    /// <summary>Where the page is served from. Every request under it is answered locally.</summary>
    internal const string Origin = "https://agentview.example/";

    /// <summary>One browser environment per app; every window's view shares it.</summary>
    private static Task<CoreWebView2Environment>? s_environment;

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly string _workDir;
    private readonly Func<IReadOnlyList<RunLine>> _history;
    private readonly System.Windows.Forms.Timer _flush = new() { Interval = 40 };

    private bool _ready;
    private int _sent;
    private int _postedSinceReady;
    private bool _busy;
    private bool _disposed;

    /// <summary>The page could not be brought up, or its browser died.</summary>
    public event Action<Exception>? Failed;

    public WebTranscriptView(string workDir, Func<IReadOnlyList<RunLine>> history)
    {
        _workDir = workDir;
        _history = history;
        // Painted before the page exists; without it a dark window flashes white.
        _web.DefaultBackgroundColor = Theme.BgSurface;
        _flush.Tick += (_, _) =>
        {
            _flush.Stop();
            Flush();
        };
    }

    public Control Control => _web;

    /// <summary>Whether the page has drawn everything posted to it. For the layout probe.</summary>
    public bool IsSettled { get; private set; }

    private static bool DevToolsEnabled =>
        Environment.GetEnvironmentVariable("CLAUDE_SWITCH_WEBVIEW_DEVTOOLS") == "1";

    /// <summary>Begin loading once the control has a window to load into.</summary>
    public void Start()
    {
        if (_web.IsHandleCreated) _ = InitializeAsync();
        else _web.HandleCreated += OnHandleCreated;
    }

    /// <summary>More lines are waiting; post them shortly, in one batch.</summary>
    public void Notify()
    {
        if (_ready && !_flush.Enabled) _flush.Start();
    }

    /// <summary>The host dropped lines from the front of its history.</summary>
    public void OnHistoryTrimmed(int removed) => _sent = Math.Max(0, _sent - removed);

    public void SetBusy(bool busy)
    {
        _busy = busy;
        if (_ready) Post(TranscriptPayload.Busy(busy));
    }

    public void ApplyTheme()
    {
        _web.DefaultBackgroundColor = Theme.BgSurface;
        if (_ready) Post(TranscriptPayload.ThemeMessage());
    }

    private void OnHandleCreated(object? sender, EventArgs e)
    {
        _web.HandleCreated -= OnHandleCreated;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await (s_environment ??= CreateEnvironmentAsync());
            if (_disposed) return;
            await _web.EnsureCoreWebView2Async(environment);
            if (_disposed) return;

            var core = _web.CoreWebView2;
            var settings = core.Settings;
            settings.AreDevToolsEnabled = DevToolsEnabled;
            settings.AreHostObjectsAllowed = false;
            settings.IsStatusBarEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;

            core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnResourceRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += OnWebMessage;
            core.ProcessFailed += OnProcessFailed;
            core.Navigate(Origin + "agent-view.html");
        }
        catch (Exception ex)
        {
            // Every way of failing to bring the page up — no runtime, a broken
            // one, an unwritable profile folder — ends the same way: the text view
            // takes over and the run stays watchable. Narrowing this would turn
            // the failure nobody anticipated into a crash in a window mid-run.
            s_environment = null;
            if (!_disposed) Failed?.Invoke(ex);
        }
    }

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeSwitch",
            "WebView2");
        return CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: folder);
    }

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var environment = _web.CoreWebView2.Environment;
        string path = Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.TrimStart('/')
            : "";
        if (AgentViewAssets.Get(path) is not { } bytes)
        {
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        e.Response = environment.CreateWebResourceResponse(
            new MemoryStream(bytes, writable: false),
            200,
            "OK",
            $"Content-Type: {AgentViewAssets.ContentType(path)}\r\nCache-Control: no-store");
    }

    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return;

        JsonNode? message;
        try
        {
            message = JsonNode.Parse(e.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }

        switch (Str(message?["type"]))
        {
            case "ready":
                // A page that has just loaded knows nothing, whether this is the
                // first load or a reload: send everything.
                _ready = true;
                _sent = 0;
                _postedSinceReady = 0;
                IsSettled = false;
                Post(TranscriptPayload.Init(_workDir, _busy));
                Flush();
                break;
            case "settled":
                int count = message?["count"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : -1;
                IsSettled = count >= _postedSinceReady && _sent >= _history().Count;
                break;
            case "openUrl":
                OpenUrl(Str(message?["url"]));
                break;
            case "copy":
                if (Str(message?["text"]) is { Length: > 0 } text)
                {
                    try
                    {
                        Clipboard.SetText(text);
                    }
                    catch (ExternalException)
                    {
                        // Another process holds the clipboard; the click did nothing.
                    }
                }
                break;
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _ready = false;
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                // The page is gone but the browser is not: reload, and the
                // page's "ready" replays the whole transcript into it.
                try
                {
                    _web.CoreWebView2?.Reload();
                }
                catch (InvalidOperationException)
                {
                    Failed?.Invoke(new InvalidOperationException("WebView2 could not reload the transcript."));
                }
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                s_environment = null;
                Failed?.Invoke(new InvalidOperationException("The WebView2 browser process exited."));
                break;
        }
    }

    private void Flush()
    {
        if (!_ready || _disposed || _web.CoreWebView2 is not { } core) return;
        var history = _history();
        if (_sent >= history.Count) return;

        core.PostWebMessageAsJson(TranscriptPayload.Events(history, _sent));
        _postedSinceReady += history.Count - _sent;
        _sent = history.Count;
        IsSettled = false;
    }

    private void Post(JsonObject message)
    {
        if (_disposed || _web.CoreWebView2 is not { } core) return;
        core.PostWebMessageAsJson(message.ToJsonString());
    }

    private static void OpenUrl(string? url)
    {
        // Only web links: a reply is model output, and a file: or custom-scheme
        // link from one must not become something the click launches.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser registered; nothing useful to do from a transcript.
        }
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flush.Dispose();
        _web.HandleCreated -= OnHandleCreated;
        _web.Dispose();
    }
}

/// <summary>
/// The messages the transcript page is sent.
/// </summary>
/// <remarks>
/// Separate from <see cref="WebTranscriptView"/> so they can be tested without
/// loading WebView2.
/// </remarks>
internal static class TranscriptPayload
{
    /// <summary>
    /// Strings the page labels itself with. Resolved here, so there is still one
    /// catalogue and the page follows the app's language.
    /// </summary>
    public static readonly string[] StringKeys =
    [
        "acp.view.copied",
        "acp.view.copy",
        "acp.view.diag",
        "acp.view.empty",
        "acp.view.exit",
        "acp.view.jump",
        "acp.view.more",
        "acp.view.noOutput",
        "acp.view.plan",
        "acp.view.status.completed",
        "acp.view.status.failed",
        "acp.view.status.in_progress",
        "acp.view.status.pending",
        "acp.view.status.stopped",
        "acp.view.thinking",
    ];

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Parsed as JSON by WebView2, never spliced into markup, so non-ASCII
        // can travel as itself instead of as \u escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Lines <paramref name="from"/> onwards, as one <c>events</c> message.</summary>
    public static string Events(IReadOnlyList<RunLine> lines, int from)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "events");
            writer.WriteStartArray("events");
            for (int i = from; i < lines.Count; i++)
            {
                var line = lines[i];
                writer.WriteStartObject();
                writer.WriteString("k", KindName(line.Kind));
                writer.WriteString("t", line.Text);
                if (line.Data is not null)
                {
                    writer.WritePropertyName("d");
                    line.Data.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static JsonObject Init(string workDir, bool busy)
    {
        var strings = new JsonObject();
        foreach (string key in StringKeys) strings[key] = Loc.T(key);
        return new JsonObject
        {
            ["type"] = "init",
            ["strings"] = strings,
            ["theme"] = ThemeJson(),
            ["workDir"] = workDir,
            ["busy"] = busy,
        };
    }

    public static JsonObject ThemeMessage() => new() { ["type"] = "theme", ["theme"] = ThemeJson() };

    public static JsonObject Busy(bool busy) => new() { ["type"] = "busy", ["busy"] = busy };

    public static string KindName(RunLineKind kind) => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    /// <summary>The app's palette, so the page reads as part of the window rather than a browser in it.</summary>
    private static JsonObject ThemeJson() => new()
    {
        ["mode"] = Theme.Mode == ThemeMode.Dark ? "dark" : "light",
        ["font"] = Theme.FontFamily,
        ["colors"] = new JsonObject
        {
            ["bg"] = Hex(Theme.BgSurface),
            ["bgApp"] = Hex(Theme.BgApp),
            ["hover"] = Hex(Theme.BgHover),
            ["selected"] = Hex(Theme.BgSelected),
            ["border"] = Hex(Theme.Border),
            ["borderSoft"] = Hex(Theme.BorderSoft),
            ["text"] = Hex(Theme.TextPrimary),
            ["text2"] = Hex(Theme.TextSecondary),
            ["muted"] = Hex(Theme.TextMuted),
            ["primary"] = Hex(Theme.Primary),
            ["primarySoft"] = Hex(Theme.PrimarySoft),
            ["onPrimary"] = Hex(Theme.TextOnPrimary),
            ["success"] = Hex(Theme.Success),
            ["warning"] = Hex(Theme.Warning),
            ["warningSoft"] = Hex(Theme.WarningSoft),
            ["danger"] = Hex(Theme.Danger),
            ["dangerSoft"] = Hex(Theme.DangerSoft),
            ["focus"] = Hex(Theme.SelectionBorder),
        },
    };

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>The transcript page's files, embedded in the app.</summary>
internal static class AgentViewAssets
{
    private const string Prefix = "AgentView/";

    private static readonly Lazy<Dictionary<string, byte[]>> Assets = new(Load);

    /// <summary>Every asset path, relative to the page.</summary>
    public static IEnumerable<string> Paths => Assets.Value.Keys;

    public static byte[]? Get(string path) =>
        Assets.Value.GetValueOrDefault(path.Replace('\\', '/'));

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private static Dictionary<string, byte[]> Load()
    {
        var assembly = typeof(AgentViewAssets).Assembly;
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            // MSBuild writes the sub-folder part of the logical name with the
            // platform's separator.
            map[name[Prefix.Length..].Replace('\\', '/')] = copy.ToArray();
        }
        return map;
    }
}
