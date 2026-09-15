using System.Text.Json.Nodes;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The detection rules, as the engine this app is built against exposes them.
/// </summary>
/// <remarks>
/// Also a guard on that engine. The native DLL is copied in from Cargo's target
/// directory (ClaudeSwitch.Native.targets); should that lookup ever pick up a
/// copy frozen at an older build, every other test still passes while the app
/// ships an old engine. Calling a method only the current engine has makes that
/// stale copy fail here instead.
/// </remarks>
public sealed class DetectionRulesEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cswitch-rules-tests", Guid.NewGuid().ToString("N"));

    private readonly Engine _engine;

    public DetectionRulesEngineTests()
    {
        Directory.CreateDirectory(_root);
        _engine = new Engine(_root);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder the OS cleans up eventually.
        }
    }

    [Fact]
    public void The_engine_reports_its_built_in_rules()
    {
        var v = _engine.Call("detection_rules_get");

        Assert.Equal("builtin", v["source"]?.GetValue<string>());
        Assert.Equal(1, v["schemaVersion"]?.GetValue<int>());
        var kinds = v["rules"]!["classification"]!["rateLimitKinds"]!.AsArray()
            .Select(k => k!.GetValue<string>());
        Assert.Contains("rate_limit", kinds);
    }

    [Fact]
    public void An_installed_update_changes_the_next_decision()
    {
        var before = _engine.Call("acp_decide", new { message = "Quota paused until 3pm" });
        Assert.Equal("unknown", before["outcome"]?["value"]?.GetValue<string>());

        _engine.Call("detection_rules_set", new JsonObject
        {
            ["rules"] = JsonNode.Parse("""{ "revision": 1, "classification": { "rateLimitText": [["quota paused"]] } }"""),
        });

        var after = _engine.Call("acp_decide", new { message = "Quota paused until 3pm" });
        Assert.Equal("rateLimit", after["outcome"]?["value"]?.GetValue<string>());
    }
}
