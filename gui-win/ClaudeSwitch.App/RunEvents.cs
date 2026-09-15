using System.Text.Json.Nodes;

namespace ClaudeSwitch.App;

/// <summary>
/// Turns ACP <c>session/update</c> payloads into transcript lines.
/// </summary>
/// <remarks>
/// <para>
/// Pure, so the mapping can be tested without an agent. Every line keeps a
/// <see cref="RunLine.Text"/> the plain-text view can print, and — where there is
/// more to show than a line of text — a <see cref="RunLine.Data"/> object for
/// the web view: a tool call with its status, diff and command output, the
/// plan, the model's thinking.
/// </para>
/// <para>
/// Data objects are built fresh rather than lifted out of the update. A
/// <see cref="JsonNode"/> can only have one parent, and the backlog that holds
/// these lines is read by every window that attaches later; sharing nodes with
/// the update would tie the two together.
/// </para>
/// </remarks>
internal static class RunEvents
{
    /// <summary>Fields of a tool call the page draws; everything else stays behind.</summary>
    private static readonly string[] ToolFields = ["title", "kind", "status", "content", "locations"];

    /// <param name="update">The <c>update</c> object of a <c>session/update</c> notification.</param>
    /// <param name="streamingMessageId">
    /// Id of the assistant message currently being streamed. A chunk from a
    /// different message starts a new line; a tool call ends the message.
    /// </param>
    public static List<RunLine> FromUpdate(JsonNode update, ref string? streamingMessageId)
    {
        var lines = new List<RunLine>();
        if (update is not JsonObject u) return lines;

        switch (Str(u["sessionUpdate"]))
        {
            case "agent_message_chunk":
                if (Str(u["content"]?["text"]) is { } text)
                {
                    string? messageId = Str(u["messageId"]);
                    if (messageId != streamingMessageId)
                    {
                        streamingMessageId = messageId;
                        lines.Add(new RunLine(RunLineKind.Assistant, Environment.NewLine, MessageData(messageId)));
                    }
                    lines.Add(new RunLine(RunLineKind.Assistant, text, MessageData(messageId)));
                }
                break;
            case "agent_thought_chunk":
                if (Str(u["content"]?["text"]) is { } thought)
                {
                    lines.Add(new RunLine(RunLineKind.Thought, thought, MessageData(Str(u["messageId"]))));
                }
                break;
            case "tool_call":
                streamingMessageId = null;
                lines.Add(new RunLine(RunLineKind.Tool, $"  ⚙ {Str(u["title"]) ?? "tool"}", ToolData(u)));
                break;
            case "tool_call_update":
                // An update names the call it belongs to; one that does not has
                // nothing on screen to update.
                if (Str(u["toolCallId"]) is { Length: > 0 })
                {
                    lines.Add(new RunLine(RunLineKind.ToolUpdate, Str(u["status"]) ?? "", ToolData(u)));
                }
                break;
            case "plan":
                if (u["entries"] is JsonArray entries)
                {
                    lines.Add(new RunLine(RunLineKind.Plan, "", new JsonObject { ["entries"] = entries.DeepClone() }));
                }
                break;
            default:
                // Command lists, mode and usage updates describe the session, not
                // the work; nothing in the transcript would change.
                break;
        }
        return lines;
    }

    /// <summary>
    /// What the page needs of a tool call or an update to one.
    /// </summary>
    /// <remarks>
    /// Command output does not arrive as content. With the client's
    /// <c>terminal_output</c> capability the adapter sends a Bash call's output
    /// and exit code in <c>_meta</c> (<c>terminal_output</c>, then
    /// <c>terminal_exit</c>), which is what lets the page draw it in a terminal
    /// instead of a code block that has lost its colours.
    /// </remarks>
    private static JsonObject ToolData(JsonObject u)
    {
        var data = new JsonObject { ["id"] = Str(u["toolCallId"]) };
        foreach (string field in ToolFields)
        {
            if (u[field] is { } value) data[field] = value.DeepClone();
        }
        if (u["_meta"] is JsonObject meta)
        {
            if (meta["claudeCode"] is JsonObject claudeCode && Str(claudeCode["toolName"]) is { } toolName)
            {
                data["toolName"] = toolName;
            }
            if (meta["terminal_output"] is JsonObject output && Str(output["data"]) is { } bytes)
            {
                data["terminalOutput"] = bytes;
            }
            if (meta["terminal_exit"] is JsonObject exit)
            {
                data["exitCode"] = exit["exit_code"]?.DeepClone();
            }
        }
        return data;
    }

    private static JsonObject? MessageData(string? messageId) =>
        messageId is null ? null : new JsonObject { ["messageId"] = messageId };

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
