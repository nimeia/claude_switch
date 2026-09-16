using System.Text.Json.Nodes;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The status-line row talking to the engine.
/// </summary>
/// <remarks>
/// These go through the same calls, with the same field names, that the
/// settings row builds. A renamed or misspelled field would otherwise compile
/// fine and only fail when a user clicks the checkbox.
/// </remarks>
public class StatuslineInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cswitch-statusline-" + Guid.NewGuid().ToString("N"));

    private readonly Engine _engine;

    public StatuslineInstallTests()
    {
        Directory.CreateDirectory(_root);
        _engine = new Engine(_root);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Claude Code's own settings file, as the isolated engine sees it.</summary>
    private string ClaudeSettingsPath => Path.Combine(_root, ".claude", "settings.json");

    private string ClaudeSettings() =>
        File.Exists(ClaudeSettingsPath) ? File.ReadAllText(ClaudeSettingsPath) : "";

    /// <summary>Stands in for the renderer the bundle unpacks beside the app.</summary>
    private string FakeRenderer()
    {
        string path = Path.Combine(_root, "cs-statusline.exe");
        File.WriteAllText(path, "renderer");
        return path;
    }

    [Fact]
    public void Turning_it_on_and_off_writes_and_restores_claude_codes_settings()
    {
        var on = _engine.Call("statusline_set", new
        {
            enabled = true,
            preset = "full",
            binaryPath = FakeRenderer(),
            takeover = false,
            refreshInterval = (int?)10,
        });

        Assert.True(on["enabled"]!.GetValue<bool>());
        Assert.Equal("full", on["preset"]!.GetValue<string>());
        Assert.Equal("settled", on["standing"]!.GetValue<string>());

        var written = JsonNode.Parse(ClaudeSettings())!["statusLine"]!;
        Assert.Equal("command", written["type"]!.GetValue<string>());
        Assert.Contains("cs-statusline", written["command"]!.GetValue<string>());
        Assert.Contains("--preset full", written["command"]!.GetValue<string>());
        Assert.Equal(10, written["refreshInterval"]!.GetValue<int>());

        // Off needs no binary: there is nothing left to install.
        _engine.Call("statusline_set", new { enabled = false });
        Assert.Null(JsonNode.Parse(ClaudeSettings())?["statusLine"]);
        Assert.False(_engine.Call("statusline_status")["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void An_older_claude_code_gets_no_refresh_interval_key()
    {
        // What the row passes when `claude --version` predates the key, or when
        // Claude Code could not be found at all.
        _engine.Call("statusline_set", new
        {
            enabled = true,
            preset = "standard",
            binaryPath = FakeRenderer(),
            takeover = false,
            refreshInterval = (int?)null,
        });

        var written = JsonNode.Parse(ClaudeSettings())!["statusLine"]!;
        Assert.Null(written["refreshInterval"]);
        Assert.Contains("--preset standard", written["command"]!.GetValue<string>());
    }

    [Fact]
    public void Another_tools_status_line_is_reported_before_it_is_replaced()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(
            ClaudeSettingsPath,
            """{"statusLine":{"type":"command","command":"npx -y ccstatusline@latest"}}""");

        // What the row reads before it offers the dialog.
        var status = _engine.Call("statusline_status");
        Assert.Equal("foreign", status["standing"]!.GetValue<string>());
        Assert.Equal("npx -y ccstatusline@latest", status["command"]!.GetValue<string>());

        // Saying no is simply not calling set: the refusal is the backstop.
        var refused = Assert.Throws<EngineException>(() => _engine.Call("statusline_set", new
        {
            enabled = true,
            preset = "lean",
            binaryPath = FakeRenderer(),
            takeover = false,
            refreshInterval = (int?)null,
        }));
        Assert.Contains("ccstatusline", StatuslineHelper.Explain(refused));
        Assert.Contains("ccstatusline", ClaudeSettings());

        // Saying yes replaces it, and turning the feature off hands it back.
        _engine.Call("statusline_set", new
        {
            enabled = true,
            preset = "lean",
            binaryPath = FakeRenderer(),
            takeover = true,
            refreshInterval = (int?)null,
        });
        Assert.Contains("cs-statusline", ClaudeSettings());

        _engine.Call("statusline_set", new { enabled = false });
        Assert.Contains("ccstatusline", ClaudeSettings());
    }

    [Fact]
    public void The_preview_is_a_plain_line_for_every_preset()
    {
        foreach (string preset in new[] { "lean", "standard", "full" })
        {
            string line = _engine.Call("statusline_preview", new { preset })["line"]!.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(line), preset);
            // The settings row draws it in a Label: no escape codes, and the
            // row has height for one line.
            Assert.DoesNotContain('', line);
            Assert.DoesNotContain('\n', StatuslineHelper.OneLine(line));
        }
    }

    [Fact]
    public void Healing_is_harmless_when_nothing_was_installed()
    {
        // Runs on every start, including the first one ever.
        var status = _engine.Call("statusline_heal", new
        {
            binaryPath = FakeRenderer(),
            refreshInterval = (int?)10,
        });
        Assert.False(status["enabled"]!.GetValue<bool>());
        Assert.Equal("", ClaudeSettings());
    }
}
