using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// Single place that runs the GUI poll cycle against the real engine.
/// MainForm timer and unit tests both call <see cref="RunTick"/>.
/// </summary>
public sealed class PollCoordinator
{
    /// <summary>How many successful engine poll ticks have completed.</summary>
    public int TickCount { get; private set; }

    /// <summary>Last planned next-poll interval reported by the engine (seconds).</summary>
    public double LastNextPollSeconds { get; private set; } = 60;

    /// <summary>True if the last tick used autoswitch_tick (when enabled).</summary>
    public bool LastUsedAutoswitchTick { get; private set; }

    /// <summary>Last raw JSON from the engine call (for diagnostics / tests).</summary>
    public string LastResponseJson { get; private set; } = "{}";

    /// <summary>
    /// Invoke core refresh path: <c>autoswitch_tick</c> when autoswitch is on
    /// (that method itself refresh_usage + decide), otherwise <c>refresh_usage</c>.
    /// </summary>
    public JsonNode RunTick(Engine engine, bool autoswitchEnabled)
    {
        ArgumentNullException.ThrowIfNull(engine);

        JsonNode result;
        if (autoswitchEnabled)
        {
            result = engine.Call("autoswitch_tick");
            LastUsedAutoswitchTick = true;
            // autoswitch_tick returns decision payload; next poll lives on snapshot.
            var snap = engine.Snapshot();
            LastNextPollSeconds = snap["nextPollSeconds"]?.GetValue<double>() ?? 60;
        }
        else
        {
            result = engine.Call("refresh_usage");
            LastUsedAutoswitchTick = false;
            LastNextPollSeconds = result["nextPollSeconds"]?.GetValue<double>()
                ?? engine.Snapshot()["nextPollSeconds"]?.GetValue<double>()
                ?? 60;
        }

        LastResponseJson = result.ToJsonString();
        TickCount++;
        return result;
    }

    /// <summary>
    /// Clamp engine-planned interval to a sane UI timer range (ms).
    /// Force override via env is applied by the form, not here.
    /// </summary>
    public static int IntervalMsFromSeconds(double seconds, int minMs = 5_000, int maxMs = 300_000)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
            seconds = 60;
        int ms = (int)Math.Round(seconds * 1000.0);
        return Math.Clamp(ms, minMs, maxMs);
    }
}
