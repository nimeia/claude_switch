//! `acp-run` — drive a Claude Code session over ACP with auto-continue.
//!
//! Doubles as the end-to-end check for the ACP path: `--probe` performs only the
//! handshake and reports the three facts that decide whether this approach works
//! on a given machine (auth, proxy, resume support).
//!
//! ```text
//! acp-run --probe
//! acp-run --cwd D:\work\proj --prompt "run the test suite and fix failures"
//! acp-run --cwd D:\work\proj --resume <sessionId> --mode acceptEdits
//! acp-run --account-dir <profile> --prompt "..."   # run as a stored account
//! ```

use std::io::Write;
use std::path::PathBuf;
use std::process::ExitCode;
use std::time::Duration;

use serde_json::Value;

use claude_switch_acp::adapter::AdapterConfig;
use claude_switch_acp::autocontinue::{ContinuePolicy, RateLimitAction, Runner, StopCause};
use claude_switch_acp::client::{AcpClient, Handler, PermissionOutcome};

const USAGE: &str = "\
acp-run — drive Claude Code over ACP with auto-continue

  --cwd <dir>            project directory (default: current)
  --prompt <text>        instruction to run
  --resume <sessionId>   continue an existing conversation
  --account-dir <dir>    CLAUDE_CONFIG_DIR: run as a stored account profile
  --mode <id>            permission mode: default | acceptEdits | plan |
                         dontAsk | bypassPermissions | auto
  --allow-tools          auto-approve tool permission requests
  --max-attempts <n>     transient retries allowed (default 5)
  --base-delay <secs>    first backoff (default 10)
  --on-quota <action>    quota exhausted: stop | wait (default) | switch
                         switch needs the GUI/engine; the CLI falls back to wait
  --wait-for-quota <n>   quota waits allowed (default 3)
  --max-wait-hours <n>   longest single quota wait (default 6)
  --quota-delay <secs>   wait per quota hit (default 900)
  --turn-timeout <secs>  per-turn timeout (default 1800)
  --probe                handshake only: report auth/proxy/resume support
  --quiet                suppress streamed assistant text
";

/// Prints progress and applies the permission policy.
struct Cli {
    quiet: bool,
    allow_tools: bool,
    permission_requests: u32,
    permissions_denied: u32,
}

impl Handler for Cli {
    fn on_update(&mut self, _session_id: &str, update: &Value) {
        let kind = update
            .get("sessionUpdate")
            .and_then(Value::as_str)
            .unwrap_or("");
        match kind {
            "agent_message_chunk" => {
                if self.quiet {
                    return;
                }
                if let Some(text) = update.pointer("/content/text").and_then(Value::as_str) {
                    print!("{text}");
                    let _ = std::io::stdout().flush();
                }
            }
            "tool_call" => {
                if let Some(title) = update.get("title").and_then(Value::as_str) {
                    eprintln!("\n  [tool] {title}");
                }
            }
            _ => {}
        }
    }

    fn on_permission(&mut self, _session_id: &str, params: &Value) -> PermissionOutcome {
        self.permission_requests += 1;
        let title = params
            .pointer("/toolCall/title")
            .and_then(Value::as_str)
            .unwrap_or("(unnamed tool call)");

        if !self.allow_tools {
            self.permissions_denied += 1;
            eprintln!("\n  [permission DENIED] {title}  (pass --allow-tools to approve)");
            return PermissionOutcome::Cancelled;
        }

        let options = params.get("options").and_then(Value::as_array);
        let pick = options.and_then(|opts| {
            let by_kind = |want: &str| {
                opts.iter()
                    .find(|o| o.get("kind").and_then(Value::as_str) == Some(want))
            };
            by_kind("allow_always")
                .or_else(|| by_kind("allow_once"))
                .or_else(|| opts.first())
                .and_then(|o| o.get("optionId").and_then(Value::as_str))
                .map(String::from)
        });

        if let Some(id) = pick {
            eprintln!("\n  [permission allowed] {title}");
            PermissionOutcome::Selected(id)
        } else {
            self.permissions_denied += 1;
            PermissionOutcome::Cancelled
        }
    }

    fn on_read_text_file(&mut self, path: &str) -> Result<String, String> {
        std::fs::read_to_string(path).map_err(|e| format!("{path}: {e}"))
    }

    fn on_write_text_file(&mut self, path: &str, content: &str) -> Result<(), String> {
        std::fs::write(path, content).map_err(|e| format!("{path}: {e}"))
    }

    fn on_stderr(&mut self, line: &str) {
        eprintln!("  [adapter] {line}");
    }
}

struct Args {
    cwd: PathBuf,
    prompt: Option<String>,
    resume: Option<String>,
    account_dir: Option<PathBuf>,
    mode: Option<String>,
    allow_tools: bool,
    policy: ContinuePolicy,
    turn_timeout: Duration,
    probe: bool,
    quiet: bool,
}

fn parse_args() -> Result<Args, String> {
    let mut args = Args {
        cwd: std::env::current_dir().map_err(|e| e.to_string())?,
        prompt: None,
        resume: None,
        account_dir: None,
        mode: None,
        allow_tools: false,
        policy: ContinuePolicy::default(),
        turn_timeout: Duration::from_secs(1800),
        probe: false,
        quiet: false,
    };

    let flags: Vec<String> = std::env::args().skip(1).collect();
    let mut i = 0;
    while i < flags.len() {
        let flag = flags[i].as_str();
        let next = |i: &mut usize| -> Result<String, String> {
            *i += 1;
            flags
                .get(*i)
                .cloned()
                .ok_or_else(|| format!("{flag} needs a value"))
        };
        match flag {
            "--cwd" => args.cwd = PathBuf::from(next(&mut i)?),
            "--prompt" => args.prompt = Some(next(&mut i)?),
            "--resume" => args.resume = Some(next(&mut i)?),
            "--account-dir" => args.account_dir = Some(PathBuf::from(next(&mut i)?)),
            "--mode" => args.mode = Some(next(&mut i)?),
            "--allow-tools" => args.allow_tools = true,
            "--probe" => args.probe = true,
            "--quiet" => args.quiet = true,
            "--max-attempts" => {
                args.policy.max_attempts =
                    next(&mut i)?.parse().map_err(|_| "bad --max-attempts")?;
            }
            "--base-delay" => {
                args.policy.base_delay_seconds =
                    next(&mut i)?.parse().map_err(|_| "bad --base-delay")?;
            }
            "--on-quota" => {
                args.policy.on_rate_limit = match next(&mut i)?.as_str() {
                    "stop" => RateLimitAction::Stop,
                    "wait" => RateLimitAction::Wait,
                    "switch" => RateLimitAction::Switch,
                    other => return Err(format!("bad --on-quota: {other}")),
                };
            }
            "--max-wait-hours" => {
                args.policy.max_wait_hours =
                    next(&mut i)?.parse().map_err(|_| "bad --max-wait-hours")?;
            }
            "--wait-for-quota" => {
                args.policy.max_rate_limit_waits =
                    next(&mut i)?.parse().map_err(|_| "bad --wait-for-quota")?;
            }
            "--quota-delay" => {
                args.policy.rate_limit_delay_seconds =
                    next(&mut i)?.parse().map_err(|_| "bad --quota-delay")?;
            }
            "--turn-timeout" => {
                let s: u64 = next(&mut i)?.parse().map_err(|_| "bad --turn-timeout")?;
                args.turn_timeout = Duration::from_secs(s);
            }
            "-h" | "--help" => {
                print!("{USAGE}");
                std::process::exit(0);
            }
            other => return Err(format!("unknown flag: {other}")),
        }
        i += 1;
    }
    Ok(args)
}

fn main() -> ExitCode {
    let args = match parse_args() {
        Ok(a) => a,
        Err(e) => {
            eprintln!("error: {e}\n\n{USAGE}");
            return ExitCode::from(2);
        }
    };

    let mut config = AdapterConfig::new(&args.cwd);
    if let Some(dir) = &args.account_dir {
        config = config.config_dir(dir);
    }

    println!("cwd:        {}", args.cwd.display());
    println!(
        "account:    {}",
        args.account_dir.as_ref().map_or_else(
            || "(default login)".to_string(),
            |d| d.display().to_string()
        )
    );
    println!(
        "proxy:      {}",
        config.resolved_proxy().unwrap_or_else(|| "(direct)".into())
    );

    let mut handler = Cli {
        quiet: args.quiet,
        allow_tools: args.allow_tools,
        permission_requests: 0,
        permissions_denied: 0,
    };

    if args.probe {
        return probe(&config, &mut handler);
    }

    let Some(prompt) = args.prompt.clone() else {
        eprintln!("error: --prompt is required (or use --probe)\n\n{USAGE}");
        return ExitCode::from(2);
    };

    let mut runner = Runner::new(config, args.policy).turn_timeout(args.turn_timeout);
    if let Some(mode) = args.mode {
        runner = runner.mode(mode);
    }
    if let Some(sid) = args.resume {
        runner = runner.resume(sid);
    }

    println!("--- run ---");
    let report = runner.run(&prompt, &mut handler);

    println!("\n--- report ---");
    println!(
        "session:       {}",
        report.session_id.as_deref().unwrap_or("(none)")
    );
    println!("turns:         {}", report.turns.len());
    println!("continuations: {}", report.continuations());
    println!("waited:        {:?}", report.total_waited);
    println!("stop cause:    {:?}", report.stop_cause);
    for (n, t) in report.turns.iter().enumerate() {
        println!(
            "  turn {n}: {:?}{}",
            t.outcome,
            t.detail
                .as_ref()
                .map_or_else(String::new, |d| format!(" — {d}"))
        );
    }
    if handler.permission_requests > 0 {
        println!(
            "permissions:   {} requested, {} denied",
            handler.permission_requests, handler.permissions_denied
        );
    }

    match report.stop_cause {
        StopCause::Completed => ExitCode::SUCCESS,
        _ => ExitCode::FAILURE,
    }
}

/// Handshake only: answers "will this machine work" without burning a turn.
fn probe(config: &AdapterConfig, handler: &mut dyn Handler) -> ExitCode {
    match AcpClient::connect(config, Duration::from_secs(120)) {
        Ok(mut client) => {
            println!("--- probe ---");
            println!("agent:        {}", client.agent_name());
            println!("loadSession:  {}", client.supports_load_session());
            println!(
                "capabilities: {}",
                serde_json::to_string(&client.agent_capabilities).unwrap_or_default()
            );
            let cwd = config.work_dir.to_string_lossy().into_owned();
            match client.new_session(&cwd, handler, Duration::from_secs(120)) {
                Ok(info) => {
                    println!("session:      {}", info.session_id);
                    println!("modes:        {}", info.available_modes.join(", "));
                    println!("\nPROBE OK");
                    ExitCode::SUCCESS
                }
                Err(e) => {
                    eprintln!("session/new failed: {e}");
                    ExitCode::FAILURE
                }
            }
        }
        Err(e) => {
            eprintln!("connect failed: {e}");
            ExitCode::FAILURE
        }
    }
}
