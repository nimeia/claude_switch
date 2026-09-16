using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// The parts of the status-line settings row that are not drawing: where this
/// build keeps the renderer, and how the engine's answers are turned into
/// something a settings row can show.
/// </summary>
/// <remarks>
/// What the line itself says is decided in the engine
/// (<c>crates/core/src/statusline</c>), not here — the preview and the
/// installed binary must never be able to disagree.
/// </remarks>
internal static class StatuslineHelper
{
    internal const string BinaryName = "cs-statusline.exe";

    /// <summary>Where this build keeps the renderer (see docs/statusline.md §5).</summary>
    /// <remarks>
    /// <see cref="AppContext.BaseDirectory"/>, not the folder holding the exe
    /// the user double-clicked: the single-file bundle runs from a self-extract
    /// directory, and that is where the binary travels with it. The engine
    /// cannot work this out from inside a DLL, so the host tells it.
    /// </remarks>
    public static string BinaryPath => Path.Combine(AppContext.BaseDirectory, BinaryName);

    /// <summary>
    /// Fit a rendered status line into the one row the settings strip has.
    /// </summary>
    /// <remarks>
    /// The <c>full</c> preset is two lines. A label would draw the break, push
    /// the row's neighbours down and leave the strip a line taller than it
    /// reserved, so the second line is folded onto the first for the preview.
    /// </remarks>
    public static string OneLine(string? rendered) =>
        string.IsNullOrEmpty(rendered)
            ? ""
            : rendered.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// The engine's own words, not the wire envelope around them.
    /// </summary>
    /// <remarks>
    /// These messages are the actionable half of this feature — "another status
    /// line is already configured: npx -y ccstatusline@latest" tells the user
    /// what to do; the error code and the JSON around it do not.
    /// </remarks>
    public static string Explain(Exception ex)
    {
        if (ex is EngineException engine)
        {
            try
            {
                if (JsonNode.Parse(engine.Json)?["message"]?.GetValue<string>()
                    is { Length: > 0 } message)
                {
                    return message;
                }
            }
            catch (Exception)
            {
                // A body that is not the error shape we expect: fall through to
                // the exception's own text rather than losing the report.
            }
        }
        return ex.Message;
    }
}
