//! Cloud warmup: open the 5h window from a claude.ai routine, not this PC.
//!
//! The local guardian ([`crate::warmup`]) needs the machine awake at the
//! anchor. A Claude Code *routine* runs on Anthropic's cloud on a cron, and
//! its inference draws on the same subscription pool, so a tiny Haiku routine
//! at the anchor opens the window with the PC off.
//!
//! Routines are created through the official CLI (`claude -p "/schedule …"`)
//! under each account's session profile. The CLI owns auth and the routines
//! API; this module only decides *what* the routines should be, writes the
//! request, and checks the structured report that comes back.
//!
//! **Idempotency** is by name: every routine this app manages is named
//! `claude-switch-warmup-<k>`, and routines belong to one claude.ai account,
//! so a sync is "make the prefixed routines on this account match this list".
//! Routines cannot be deleted through the API, so surplus ones are disabled.

use std::io::{Read, Write};
use std::path::Path;
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use chrono::{FixedOffset, Local, NaiveDate, NaiveTime, Offset, TimeZone, Timelike};
use serde::{Deserialize, Serialize};

use crate::session;
use crate::settings::{CloudAccount, CloudRoutine, WarmupSettings};
use crate::warmup::{compute_base_anchor, parse_hhmm, stagger_anchor, DEFAULT_WARMUP_MODEL};

/// Name prefix of every routine this app owns; nothing else is touched.
pub const ROUTINE_PREFIX: &str = "claude-switch-warmup";

/// Extra wait after each 5h reset before the next routine fires.
///
/// A routine starts a few minutes after its cron time, and the delay differs
/// per routine and per fire (2.5, 5.2 and 6.1 minutes were measured on one
/// routine). Without a margin the next round could land just before the
/// previous window resets — opening nothing, and leaving the account idle
/// until real use starts it. 15 covers the observed spread twice over.
pub const RESET_MARGIN_MINUTES: u32 = 15;

/// Routine fires per account per day. Pro allows five routine runs a day in
/// total, so a longer plan would starve the account's other routines.
pub const MAX_ROUTINES_PER_ACCOUNT: usize = 5;

/// Model driving the one-off setup session. Setup is rare, and a stronger
/// model follows the `/schedule` workflow without stopping to ask questions.
pub const SETUP_MODEL: &str = "claude-sonnet-5";

/// Upper bound for one account's setup session.
pub const SYNC_TIMEOUT: Duration = Duration::from_secs(6 * 60);

/// What each routine run says. Kept trivial: any inference opens the window.
pub const RUN_PROMPT: &str =
    "Scheduled keep-alive from Claude Switch. Reply with the single word ok. Do not use any tools.";

/// Host every warmup request goes to; also what proxy rules are matched on.
pub const API_HOST: &str = "api.anthropic.com";

/// Env vars a Claude Code parent leaves behind. A child that inherits them
/// behaves as a nested session, which the API refuses.
const NESTED_SESSION_ENV_VARS: &[&str] = &[
    "CLAUDECODE",
    "CLAUDE_CODE_ENTRYPOINT",
    "CLAUDE_CODE_CHILD_SESSION",
    "CLAUDE_CODE_SESSION_ID",
    "CLAUDE_CODE_SESSION_ATTENDED",
    "CLAUDE_CODE_MESSAGING_SOCKET",
    "CLAUDE_CODE_MESSAGING_TOKEN",
    "CLAUDE_CODE_EXECPATH",
    "CLAUDE_EFFORT",
    "CLAUDE_PID",
];

/// JSON Schema the setup session must answer with (`--json-schema`).
pub const REPORT_SCHEMA: &str = r#"{"type":"object","properties":{"ok":{"type":"boolean"},"routines":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"id":{"type":"string"},"cron_expression":{"type":"string"},"enabled":{"type":"boolean"},"next_run_at":{"type":"string"}},"required":["name","id","enabled"]}},"error":{"type":"string"}},"required":["ok","routines"]}"#;

/// Outcome of a sync, or the current state for a status query.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CloudSyncReport {
    pub enabled: bool,
    /// Enabled, but the plan no longer matches what is on the accounts.
    pub stale: bool,
    pub accounts: Vec<CloudAccount>,
}

/// One routine the account should have.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct RoutineSpec {
    pub name: String,
    /// 5-field cron in UTC, daily.
    pub cron_expression: String,
    /// Local wall-clock time the cron stands for, `"HH:MM"` (display only).
    #[serde(skip)]
    pub local_time: String,
}

/// Local start times for one account: the staggered anchor, then one start
/// per reset (+ [`RESET_MARGIN_MINUTES`]) while it is still before work end.
#[must_use]
pub fn local_start_times(
    settings: &WarmupSettings,
    account_index: usize,
    account_count: usize,
) -> Vec<NaiveTime> {
    let (Some(start), Some(end)) = (
        parse_hhmm(&settings.work_start),
        parse_hhmm(&settings.work_end),
    ) else {
        return Vec::new();
    };
    let Some(base) = compute_base_anchor(start, end) else {
        return Vec::new();
    };
    let anchor = stagger_anchor(base, account_index, account_count);
    let end_min = end.num_seconds_from_midnight() / 60;
    let step = 5 * 60 + RESET_MARGIN_MINUTES;
    // Minutes since midnight: a chain never wraps past the work day.
    let mut t = anchor.num_seconds_from_midnight() / 60;
    let mut out = Vec::new();
    while t < end_min && out.len() < MAX_ROUTINES_PER_ACCOUNT {
        if let Some(time) = NaiveTime::from_hms_opt(t / 60, t % 60, 0) {
            out.push(time);
        }
        t += step;
    }
    out
}

/// Daily UTC cron for a local wall-clock time at a given UTC offset.
#[must_use]
pub fn utc_cron(local: NaiveTime, offset: FixedOffset) -> String {
    let local_min = i64::from(local.num_seconds_from_midnight() / 60);
    let utc_min = (local_min - i64::from(offset.local_minus_utc()) / 60).rem_euclid(24 * 60);
    format!("{} {} * * *", utc_min % 60, utc_min / 60)
}

/// The local zone's offset on `date` at `time` (DST-aware for that day).
fn local_offset(date: NaiveDate, time: NaiveTime) -> FixedOffset {
    Local
        .from_local_datetime(&date.and_time(time))
        .earliest()
        .map_or_else(|| Local::now().offset().fix(), |dt| dt.offset().fix())
}

/// Routines one account should have today (empty → disable all of ours).
#[must_use]
pub fn plan_routines(
    settings: &WarmupSettings,
    account_index: usize,
    account_count: usize,
    today: NaiveDate,
) -> Vec<RoutineSpec> {
    local_start_times(settings, account_index, account_count)
        .into_iter()
        .enumerate()
        .map(|(k, t)| RoutineSpec {
            name: format!("{ROUTINE_PREFIX}-{}", k + 1),
            cron_expression: utc_cron(t, local_offset(today, t)),
            local_time: t.format("%H:%M").to_string(),
        })
        .collect()
}

/// Whether stored routines still match the plan (same names and crons).
///
/// A DST change or a different number of eligible accounts moves the plan
/// without anyone touching the settings; this is how the shell notices.
#[must_use]
pub fn routines_match(stored: &[CloudRoutine], planned: &[RoutineSpec]) -> bool {
    stored.len() == planned.len()
        && planned.iter().all(|p| {
            stored
                .iter()
                .any(|s| s.name == p.name && s.cron_expression == p.cron_expression)
        })
}

/// The `/schedule` request for one account. `planned` empty disables ours.
#[must_use]
pub fn sync_prompt(planned: &[RoutineSpec], run_model: Option<&str>) -> String {
    let model = run_model.unwrap_or(DEFAULT_WARMUP_MODEL);
    let desired = serde_json::to_string(planned).unwrap_or_else(|_| "[]".into());
    let run_prompt = serde_json::to_string(RUN_PROMPT).unwrap_or_default();
    format!(
        "/schedule This request comes from the Claude Switch desktop app and runs unattended. \
The user has already confirmed everything below: do not ask questions, do not wait for \
confirmation, and do not change anything that is not listed. Use only the RemoteTrigger tool.

Goal: on this account, the routines whose name starts with \"{ROUTINE_PREFIX}\" must end up \
exactly matching this list:
{desired}

Steps:
1. List routines.
2. For each entry in the list: if a routine with exactly that name exists, update it with that \
cron_expression, enabled=true and the job_config below; otherwise create it with them.
3. Every other routine whose name starts with \"{ROUTINE_PREFIX}\" and is not in the list: \
update it with enabled=false. Never modify routines with any other name.
4. List routines again and report every routine whose name starts with \"{ROUTINE_PREFIX}\".

job_config for every routine in the list (cron_expression values are already UTC; use them as is):
- environment_id: the environment named \"Default\"; if there are several, the first one; \
if there is none, the first available environment
- session_context: {{\"model\": \"{model}\", \"sources\": []}}
- events: one user message with a fresh lowercase v4 uuid, session_id \"\", \
parent_tool_use_id null, message {{\"role\": \"user\", \"content\": {run_prompt}}}
- no mcp_connections

Report: ok=true only if every listed routine exists with its cron_expression and enabled=true \
and every other \"{ROUTINE_PREFIX}\" routine is disabled. Otherwise ok=false with the exact \
error message in error."
    )
}

#[derive(Debug, Deserialize)]
struct Report {
    ok: bool,
    #[serde(default)]
    routines: Vec<ReportedRoutine>,
    #[serde(default)]
    error: Option<String>,
}

#[derive(Debug, Deserialize)]
struct ReportedRoutine {
    name: String,
    #[serde(default)]
    id: String,
    #[serde(default)]
    cron_expression: String,
    #[serde(default)]
    enabled: bool,
    #[serde(default)]
    next_run_at: Option<String>,
}

/// Read the CLI's `--output-format json` result and check it against the plan.
///
/// The model's own `ok` is not trusted alone: every planned routine must be
/// reported enabled with its cron, and no other prefixed routine may be left
/// enabled. Returns the routines as they now stand on the account.
pub fn verify_report(stdout: &str, planned: &[RoutineSpec]) -> Result<Vec<CloudRoutine>, String> {
    let envelope: serde_json::Value =
        serde_json::from_str(stdout.trim()).map_err(|e| format!("unreadable CLI output: {e}"))?;
    let result_text = envelope
        .get("result")
        .and_then(serde_json::Value::as_str)
        .unwrap_or_default();
    if envelope
        .get("is_error")
        .and_then(serde_json::Value::as_bool)
        == Some(true)
    {
        return Err(if result_text.is_empty() {
            "claude reported an error".into()
        } else {
            result_text.to_string()
        });
    }
    let report_value = match envelope.get("structured_output") {
        Some(v) if !v.is_null() => v.clone(),
        _ => serde_json::from_str(result_text)
            .map_err(|_| format!("no structured report: {}", truncate(result_text, 300)))?,
    };
    let report: Report =
        serde_json::from_value(report_value).map_err(|e| format!("bad report: {e}"))?;

    let ours: Vec<&ReportedRoutine> = report
        .routines
        .iter()
        .filter(|r| r.name.starts_with(ROUTINE_PREFIX))
        .collect();
    for p in planned {
        let found = ours.iter().find(|r| r.name == p.name);
        match found {
            Some(r) if r.enabled && r.cron_expression == p.cron_expression => {}
            Some(r) if !r.enabled => return Err(format!("{} is still disabled", p.name)),
            Some(r) => {
                return Err(format!(
                    "{} has cron {:?}, expected {:?}",
                    p.name, r.cron_expression, p.cron_expression
                ))
            }
            None => {
                return Err(report
                    .error
                    .clone()
                    .filter(|e| !e.is_empty())
                    .unwrap_or_else(|| format!("{} was not created", p.name)))
            }
        }
    }
    if let Some(extra) = ours
        .iter()
        .find(|r| r.enabled && !planned.iter().any(|p| p.name == r.name))
    {
        return Err(format!("{} is still enabled", extra.name));
    }
    if !report.ok {
        // Everything checks out on paper, but the session itself doubted it.
        return Err(report
            .error
            .filter(|e| !e.is_empty())
            .unwrap_or_else(|| "setup session reported failure".into()));
    }
    Ok(planned
        .iter()
        .filter_map(|p| ours.iter().find(|r| r.name == p.name))
        .map(|r| CloudRoutine {
            name: r.name.clone(),
            id: r.id.clone(),
            cron_expression: r.cron_expression.clone(),
            next_run_at: r.next_run_at.clone().filter(|s| !s.is_empty()),
        })
        .collect())
}

fn truncate(s: &str, max: usize) -> String {
    if s.chars().count() <= max {
        return s.to_string();
    }
    let mut out: String = s.chars().take(max).collect();
    out.push('…');
    out
}

/// Run one account's `/schedule` session and return its stdout.
///
/// - The prompt goes in on stdin: it is multi-line and quoted, which a
///   `claude.cmd` shim would mangle as an argument.
/// - `CLAUDE_CONFIG_DIR` selects the account; auth overrides and the env of
///   a Claude Code parent are scrubbed so neither can hijack the session.
/// - Killed after `timeout`; a hung session must not hold the sync forever.
pub fn run_sync(
    claude: &Path,
    config_dir: &Path,
    work_dir: &Path,
    prompt: &str,
    timeout: Duration,
) -> Result<String, String> {
    std::fs::create_dir_all(work_dir).map_err(|e| format!("cloud warmup work dir: {e}"))?;

    let mut cmd = Command::new(claude);
    cmd.arg("-p")
        .arg("--model")
        .arg(SETUP_MODEL)
        .arg("--allowedTools")
        .arg("RemoteTrigger")
        .arg("--strict-mcp-config")
        .arg("--no-session-persistence")
        .arg("--output-format")
        .arg("json")
        .arg("--json-schema")
        .arg(REPORT_SCHEMA)
        .current_dir(work_dir)
        .env("CLAUDE_CONFIG_DIR", config_dir)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    for key in session::AUTH_OVERRIDE_ENV_VARS
        .iter()
        .chain(NESTED_SESSION_ENV_VARS)
    {
        cmd.env_remove(key);
    }
    // Without this a machine whose proxy lives in Windows Internet Settings
    // sends the session straight out, and Anthropic answers 403.
    for (key, value) in crate::proxy::child_env(API_HOST) {
        cmd.env(key, value);
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        cmd.creation_flags(CREATE_NO_WINDOW);
    }

    let mut child = cmd
        .spawn()
        .map_err(|e| format!("spawn claude /schedule: {e}"))?;
    if let Some(mut stdin) = child.stdin.take() {
        stdin
            .write_all(prompt.as_bytes())
            .map_err(|e| format!("write prompt: {e}"))?;
    }
    // Drain both pipes on their own threads so a full pipe cannot stall the child.
    let drain = |pipe: Option<Box<dyn Read + Send>>| {
        std::thread::spawn(move || {
            let mut buf = String::new();
            if let Some(mut p) = pipe {
                let _ = p.read_to_string(&mut buf);
            }
            buf
        })
    };
    let out = drain(
        child
            .stdout
            .take()
            .map(|p| Box::new(p) as Box<dyn Read + Send>),
    );
    let err = drain(
        child
            .stderr
            .take()
            .map(|p| Box::new(p) as Box<dyn Read + Send>),
    );

    let started = Instant::now();
    let status = loop {
        match child.try_wait() {
            Ok(Some(status)) => break status,
            Ok(None) if started.elapsed() >= timeout => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(format!(
                    "claude /schedule timed out after {}s",
                    timeout.as_secs()
                ));
            }
            Ok(None) => std::thread::sleep(Duration::from_millis(250)),
            Err(e) => return Err(format!("wait for claude: {e}")),
        }
    };
    let stdout = out.join().unwrap_or_default();
    let stderr = err.join().unwrap_or_default();
    // A failed session still prints its JSON envelope; let the caller read it.
    if stdout.trim().is_empty() {
        let detail = stderr.trim();
        return Err(if detail.is_empty() {
            format!("claude exited with {status} and no output")
        } else {
            truncate(detail, 500)
        });
    }
    Ok(stdout)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn t(h: u32, m: u32) -> NaiveTime {
        NaiveTime::from_hms_opt(h, m, 0).unwrap()
    }

    fn settings(start: &str, end: &str) -> WarmupSettings {
        WarmupSettings {
            enabled: true,
            work_start: start.into(),
            work_end: end.into(),
            ..Default::default()
        }
    }

    fn shanghai() -> FixedOffset {
        FixedOffset::east_opt(8 * 3600).unwrap()
    }

    #[test]
    fn nine_to_eighteen_chains_three_starts_with_margin() {
        // Anchor 06:00; each later start waits out the reset plus the margin.
        assert_eq!(
            local_start_times(&settings("09:00", "18:00"), 0, 1),
            vec![t(6, 0), t(11, 15), t(16, 30)]
        );
    }

    #[test]
    fn second_account_is_staggered_and_stops_before_work_end() {
        // Base 06:00 + 5/2 h → 08:30, 13:45; 19:00 is past work end.
        assert_eq!(
            local_start_times(&settings("09:00", "18:00"), 1, 2),
            vec![t(8, 30), t(13, 45)]
        );
    }

    #[test]
    fn a_whole_day_stays_within_the_daily_routine_allowance() {
        // 5h15m rounds fit four times into a day, under Pro's five runs.
        let times = local_start_times(&settings("00:00", "23:59"), 0, 1);
        assert_eq!(times.len(), 4);
        assert!(times.len() <= MAX_ROUTINES_PER_ACCOUNT);
    }

    #[test]
    fn cron_is_converted_to_utc_and_wraps_the_day() {
        assert_eq!(utc_cron(t(6, 0), shanghai()), "0 22 * * *");
        assert_eq!(utc_cron(t(11, 15), shanghai()), "15 3 * * *");
        let west = FixedOffset::west_opt(5 * 3600).unwrap();
        assert_eq!(utc_cron(t(21, 30), west), "30 2 * * *");
    }

    #[test]
    fn plan_names_routines_in_order() {
        let today = NaiveDate::from_ymd_opt(2026, 9, 15).unwrap();
        let plan = plan_routines(&settings("09:00", "18:00"), 0, 1, today);
        let names: Vec<_> = plan.iter().map(|r| r.name.as_str()).collect();
        assert_eq!(
            names,
            [
                "claude-switch-warmup-1",
                "claude-switch-warmup-2",
                "claude-switch-warmup-3"
            ]
        );
        assert_eq!(plan[1].local_time, "11:15");
    }

    fn spec(name: &str, cron: &str) -> RoutineSpec {
        RoutineSpec {
            name: name.into(),
            cron_expression: cron.into(),
            local_time: String::new(),
        }
    }

    fn envelope(report: &serde_json::Value) -> String {
        serde_json::json!({"type": "result", "is_error": false, "result": "", "structured_output": report})
            .to_string()
    }

    #[test]
    fn prompt_lists_the_plan_and_the_prefix() {
        let p = sync_prompt(&[spec("claude-switch-warmup-1", "0 22 * * *")], None);
        assert!(p.starts_with("/schedule "));
        assert!(p.contains(r#""cron_expression":"0 22 * * *""#));
        assert!(p.contains(DEFAULT_WARMUP_MODEL));
        assert!(!p.contains("local_time"));
    }

    #[test]
    fn verify_accepts_a_matching_report() {
        let planned = [spec("claude-switch-warmup-1", "0 22 * * *")];
        let out = envelope(&serde_json::json!({
            "ok": true,
            "routines": [
                {"name": "claude-switch-warmup-1", "id": "trig_1", "cron_expression": "0 22 * * *",
                 "enabled": true, "next_run_at": "2026-09-15T22:02:31Z"},
                {"name": "claude-switch-warmup-2", "id": "trig_2", "cron_expression": "10 3 * * *", "enabled": false},
                {"name": "someone-elses", "id": "trig_3", "enabled": true}
            ]
        }));
        let got = verify_report(&out, &planned).unwrap();
        assert_eq!(got.len(), 1);
        assert_eq!(got[0].id, "trig_1");
        assert_eq!(got[0].next_run_at.as_deref(), Some("2026-09-15T22:02:31Z"));
    }

    #[test]
    fn verify_rejects_a_claimed_success_that_does_not_match() {
        let planned = [spec("claude-switch-warmup-1", "0 22 * * *")];
        let wrong_cron = envelope(&serde_json::json!({
            "ok": true,
            "routines": [{"name": "claude-switch-warmup-1", "id": "t", "cron_expression": "0 6 * * *", "enabled": true}]
        }));
        assert!(verify_report(&wrong_cron, &planned)
            .unwrap_err()
            .contains("expected"));

        let leftover = envelope(&serde_json::json!({
            "ok": true,
            "routines": [
                {"name": "claude-switch-warmup-1", "id": "t", "cron_expression": "0 22 * * *", "enabled": true},
                {"name": "claude-switch-warmup-2", "id": "u", "cron_expression": "10 3 * * *", "enabled": true}
            ]
        }));
        assert!(verify_report(&leftover, &planned)
            .unwrap_err()
            .contains("still enabled"));
    }

    #[test]
    fn verify_surfaces_cli_errors() {
        let out = serde_json::json!({
            "type": "result", "is_error": true,
            "result": "Failed to authenticate. API Error: 403 Request not allowed"
        })
        .to_string();
        assert!(verify_report(&out, &[]).unwrap_err().contains("403"));
    }

    #[test]
    fn verify_reads_a_json_result_when_structured_output_is_absent() {
        let report = r#"{"ok":true,"routines":[]}"#;
        let out =
            serde_json::json!({"type": "result", "is_error": false, "result": report}).to_string();
        assert!(verify_report(&out, &[]).unwrap().is_empty());
    }

    #[test]
    fn stored_routines_match_only_the_same_plan() {
        let planned = [spec("claude-switch-warmup-1", "0 22 * * *")];
        let stored = vec![CloudRoutine {
            name: "claude-switch-warmup-1".into(),
            id: "t".into(),
            cron_expression: "0 22 * * *".into(),
            next_run_at: None,
        }];
        assert!(routines_match(&stored, &planned));
        assert!(!routines_match(
            &stored,
            &[spec("claude-switch-warmup-1", "0 21 * * *")]
        ));
        assert!(!routines_match(&[], &planned));
    }
}
