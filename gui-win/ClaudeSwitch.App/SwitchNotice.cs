using System.Text.Json.Nodes;

namespace ClaudeSwitch.App;

/// <summary>
/// The text shown when autoswitch moves accounts on its own.
///
/// Pulled out of the form because it is the one message that has to be right:
/// it is the product's only proof that the feature did anything, and it is read
/// once, in a balloon, by someone who is mid-sentence in an editor.
/// </summary>
internal static class SwitchNotice
{
    public const string Title = "已自动切换账号";

    /// <summary>
    /// Windows on-disk credentials are re-read by Claude Code when the file
    /// changes, so a running session picks the new account up on its next
    /// message. Saying so is the difference between "it switched" and "you can
    /// keep working".
    /// </summary>
    public const string NoRestartLine = "下一条消息即生效，无需重启 Claude Code。";

    /// <summary>Whether an <c>autoswitch_tick</c> payload reports a real switch.</summary>
    public static bool DidSwitch(JsonNode? result) =>
        result?["switched"]?.GetValue<bool>() == true;

    /// <summary>Slot the engine says it switched to, if the payload names one.</summary>
    public static int? TargetNumber(JsonNode? result) =>
        result?["to"]?["number"]?.GetValue<int>();

    /// <summary>
    /// Balloon body: where it landed, what pushed it, and that work continues.
    /// </summary>
    /// <param name="target">Display label of the account now in use.</param>
    /// <param name="fromLabel">Label of the account left behind, or null.</param>
    /// <param name="fromUsagePct">
    /// The reading that triggered the move — the pre-switch figure, since the
    /// post-switch snapshot no longer shows why anything happened.
    /// </param>
    public static string Body(string target, string? fromLabel, double? fromUsagePct)
    {
        string why = fromLabel is { Length: > 0 } && fromUsagePct is { } pct
            ? $"（{fromLabel} 用量已达 {pct:0.#}%）"
            : "";
        return $"现在使用 {target}{why}\n{NoRestartLine}";
    }
}
