using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSwitch.App;

/// <summary>
/// A representative supervised run, for the layout probe.
/// </summary>
/// <remarks>
/// Written as the updates the ACP adapter sends — field for field, including the
/// <c>_meta</c> that carries command output — and passed through
/// <see cref="RunEvents"/>, so a capture exercises the same mapping a live run
/// does. The alternative is spending quota to get a picture of a window.
/// </remarks>
internal static class AgentSampleRun
{
    private const string Prompt = "把 crates/ 下每个 crate 的用途整理成表格，再把 README 里过期的构建命令改掉。";

    /// <summary>The ANSI escape character that starts a colour sequence.</summary>
    private const char Esc = (char)27;

    public static List<RunLine> Lines(string workDir)
    {
        string readme = JsonSerializer.Serialize(Path.Combine(workDir, "README.md"));
        var lines = new List<RunLine>
        {
            new(RunLineKind.Prompt, $"› {Prompt}", new JsonObject { ["text"] = Prompt }),
        };
        string? streaming = null;
        void Update(string json) => lines.AddRange(RunEvents.FromUpdate(JsonNode.Parse(json)!, ref streaming));

        Update("""{"sessionUpdate":"agent_thought_chunk","messageId":"msg_01","content":{"type":"text","text":"先找出 crates 目录下的 Cargo.toml，读各自的 description；README 的构建命令要和实际包名对一下。"}}""");

        Update("""{"sessionUpdate":"tool_call","toolCallId":"toolu_01","title":"Find `crates` `*/Cargo.toml`","kind":"search","status":"pending","content":[],"locations":[],"_meta":{"claudeCode":{"toolName":"Glob"}}}""");
        Update("""{"sessionUpdate":"tool_call_update","toolCallId":"toolu_01","status":"completed","content":[{"type":"content","content":{"type":"text","text":"crates/acp/Cargo.toml\ncrates/core/Cargo.toml\ncrates/ffi/Cargo.toml"}}],"_meta":{"claudeCode":{"toolName":"Glob"}}}""");

        Update("""{"sessionUpdate":"tool_call","toolCallId":"toolu_02","title":"Read docs/build.md","kind":"read","status":"pending","content":[],"locations":[],"_meta":{"claudeCode":{"toolName":"Read"}}}""");
        Update("""{"sessionUpdate":"tool_call_update","toolCallId":"toolu_02","status":"failed","content":[{"type":"content","content":{"type":"text","text":"```\n<tool_use_error>File does not exist.</tool_use_error>\n```"}}],"_meta":{"claudeCode":{"toolName":"Read"}}}""");

        Update("""{"sessionUpdate":"tool_call","toolCallId":"toolu_03","title":"cargo tree --workspace --depth 0","kind":"execute","status":"pending","content":[{"type":"terminal","terminalId":"toolu_03"}],"_meta":{"claudeCode":{"toolName":"Bash"},"terminal_info":{"terminal_id":"toolu_03"}}}""");
        // Built as an object: the output carries real escape sequences, which a
        // JSON literal would have to spell as escapes of escapes.
        string output =
            $"{Esc}[1;32mclaude-switch-acp{Esc}[0m v0.1.0 (crates/acp)\n"
            + $"{Esc}[1;32mclaude-switch-core{Esc}[0m v0.1.0 (crates/core)\n"
            + $"{Esc}[1;32mclaude-switch-ffi{Esc}[0m v0.1.0 (crates/ffi)\n"
            + $"{Esc}[33mwarning{Esc}[0m: profile `release` is set in a member manifest and will be ignored";
        lines.AddRange(RunEvents.FromUpdate(
            new JsonObject
            {
                ["sessionUpdate"] = "tool_call_update",
                ["toolCallId"] = "toolu_03",
                ["_meta"] = new JsonObject
                {
                    ["terminal_output"] = new JsonObject { ["terminal_id"] = "toolu_03", ["data"] = output },
                },
            },
            ref streaming));
        Update("""{"sessionUpdate":"tool_call_update","toolCallId":"toolu_03","status":"completed","content":[{"type":"terminal","terminalId":"toolu_03"}],"_meta":{"claudeCode":{"toolName":"Bash"},"terminal_exit":{"terminal_id":"toolu_03","exit_code":0,"signal":null}}}""");

        // Tools called in parallel, as a real run does: every call is announced
        // before any finishes, so the command's output lands after later calls
        // exist. The grep title carries an absolute path, as the adapter sends it.
        Update("""{"sessionUpdate":"tool_call","toolCallId":"toolu_10","title":"git log --oneline -3","kind":"execute","status":"pending","content":[{"type":"terminal","terminalId":"toolu_10"}],"_meta":{"claudeCode":{"toolName":"Bash"},"terminal_info":{"terminal_id":"toolu_10"}}}""");
        lines.AddRange(RunEvents.FromUpdate(
            new JsonObject
            {
                ["sessionUpdate"] = "tool_call",
                ["toolCallId"] = "toolu_11",
                ["title"] = $"grep \"^pub mod \" {Path.Combine(workDir, "crates", "core", "src", "lib.rs")}",
                ["kind"] = "search",
                ["status"] = "pending",
                ["content"] = new JsonArray(),
                ["_meta"] = new JsonObject { ["claudeCode"] = new JsonObject { ["toolName"] = "Grep" } },
            },
            ref streaming));
        Update("""{"sessionUpdate":"tool_call","toolCallId":"toolu_12","title":"Read crates\\acp\\Cargo.toml","kind":"read","status":"pending","content":[],"locations":[],"_meta":{"claudeCode":{"toolName":"Read"}}}""");
        Update("""{"sessionUpdate":"tool_call","toolCallId":"toolu_13","title":"Read crates\\ffi\\Cargo.toml","kind":"read","status":"pending","content":[],"locations":[],"_meta":{"claudeCode":{"toolName":"Read"}}}""");
        Update("""{"sessionUpdate":"tool_call_update","toolCallId":"toolu_11","status":"completed","content":[{"type":"content","content":{"type":"text","text":"pub mod agentruns;\npub mod autocontinue;\npub mod engine;"}}],"_meta":{"claudeCode":{"toolName":"Grep"}}}""");
        Update("""{"sessionUpdate":"tool_call_update","toolCallId":"toolu_12","status":"completed","content":[{"type":"content","content":{"type":"text","text":"[package]\nname = \"claude-switch-acp\""}}],"_meta":{"claudeCode":{"toolName":"Read"}}}""");
        Update("""{"sessionUpdate":"tool_call_update","toolCallId":"toolu_13","status":"completed","content":[{"type":"content","content":{"type":"text","text":"[package]\nname = \"claude-switch-ffi\""}}],"_meta":{"claudeCode":{"toolName":"Read"}}}""");
        lines.AddRange(RunEvents.FromUpdate(
            new JsonObject
            {
                ["sessionUpdate"] = "tool_call_update",
                ["toolCallId"] = "toolu_10",
                ["_meta"] = new JsonObject
                {
                    ["terminal_output"] = new JsonObject
                    {
                        ["terminal_id"] = "toolu_10",
                        ["data"] = $"{Esc}[33m147584d{Esc}[m feat(stalled): raise the success rate of waking a stalled terminal\n"
                            + $"{Esc}[33me4ea594{Esc}[m feat(stalled): try waking the original terminal before taking a session over\n"
                            + $"{Esc}[33m05feb0b{Esc}[m fix(ui): round the toolbar, theme the search box, and fix a clock-dependent test",
                    },
                },
            },
            ref streaming));
        Update("""{"sessionUpdate":"tool_call_update","toolCallId":"toolu_10","status":"completed","content":[{"type":"terminal","terminalId":"toolu_10"}],"_meta":{"claudeCode":{"toolName":"Bash"},"terminal_exit":{"terminal_id":"toolu_10","exit_code":0,"signal":null}}}""");

        // A table whose last column is long and whose first holds code — the
        // shape that squeezed a real reply's first columns to a letter per line.
        Update("""{"sessionUpdate":"agent_message_chunk","messageId":"msg_02","content":{"type":"text","text":"三个 crate 的分工：\n\n| crate | 路径 | 用途 |\n|---|---|---|\n| `claude-switch-core` | `crates/core` | 账号切换、用量缓存、续跑策略——纯逻辑，没有异步运行时；凭据与 keychain、会话与项目、用量与套餐、停滞检测和历史清理也都在这里，用 ureq 做同步 HTTP |\n"}}""");
        Update("""{"sessionUpdate":"agent_message_chunk","messageId":"msg_02","content":{"type":"text","text":"| `claude-switch-acp` | `crates/acp` | ACP 客户端，以及 `acp-run` 命令行 |\n| `claude-switch-ffi` | `crates/ffi` | 给 Windows 界面调用的 C ABI |\n\nREADME 里的构建命令还是 **`cargo build -p ffi`**，包名早就改了，下面修正。"}}""");

        // A token rather than interpolation: the JSON's own closing braces run
        // too deep for an interpolated raw string.
        Update("""{"sessionUpdate":"tool_call","toolCallId":"toolu_04","title":"Edit README.md","kind":"edit","status":"pending","content":[{"type":"diff","path":README_PATH,"oldText":"## Build\n\n```sh\ncargo build -p ffi --release\n```","newText":"## Build\n\n```sh\ncargo build -p claude-switch-ffi --release\ndotnet build gui-win/ClaudeSwitch.App\n```"}],"locations":[{"path":README_PATH}],"_meta":{"claudeCode":{"toolName":"Edit"}}}"""
            .Replace("README_PATH", readme, StringComparison.Ordinal));
        Update("""{"sessionUpdate":"tool_call_update","toolCallId":"toolu_04","status":"completed","_meta":{"claudeCode":{"toolName":"Edit"}}}""");

        Update("""{"sessionUpdate":"plan","entries":[{"content":"列出 crates 并读取描述","status":"completed","priority":"medium"},{"content":"整理用途表格","status":"completed","priority":"medium"},{"content":"修正 README 的构建命令","status":"completed","priority":"high"},{"content":"运行 cargo check 确认构建没坏","status":"in_progress","priority":"medium"}]}""");

        // What a network blip looks like: the runner's own warning, the adapter's
        // stderr, then the continuation carrying on with the next tool call.
        lines.Add(new RunLine(RunLineKind.Warning, Loc.T("acp.status.retryIn", Loc.T("acp.kind.network"), 10)));
        lines.Add(new RunLine(
            RunLineKind.Notice,
            "[adapter] API Error: Connection closed mid-response.",
            new JsonObject { ["source"] = "adapter" }));
        streaming = null;

        Update("""{"sessionUpdate":"tool_call","toolCallId":"toolu_05","title":"cargo check --workspace","kind":"execute","status":"pending","content":[{"type":"terminal","terminalId":"toolu_05"}],"_meta":{"claudeCode":{"toolName":"Bash"},"terminal_info":{"terminal_id":"toolu_05"}}}""");

        return lines;
    }
}
