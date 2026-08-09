//! Finding Claude Code sessions that **stalled** rather than finished.
//!
//! Auto-continue over ACP ([`crate::autocontinue`]) steers runs this app owns.
//! This module answers the other half: a session someone started in a terminal
//! hit the quota wall or a network failure, and has been sitting there ever
//! since. Nobody is coming back to press enter.
//!
//! # Why a transcript scan rather than a process check
//!
//! A stalled Claude Code is still **running** — it prints the limit and waits
//! for a human, it does not exit. So "is the process alive" (what
//! [`crate::session::is_pid_alive`] answers) says nothing about whether work is
//! progressing. The transcript does: its last record is either a finished turn
//! or an error the session never recovered from, and its mtime says how long
//! that has been true.
//!
//! # The three-part test
//!
//! A session is a takeover candidate only when all three hold:
//!
//! 1. **Inside the current quota window** — mtime within [`StallCriteria::window_hours`].
//!    Older than that and the 5h bucket it stalled in has long since rolled
//!    over; resuming it is archaeology, not recovery.
//! 2. **Idle** for at least [`StallCriteria::idle_minutes`]. A turn that is
//!    merely slow writes to its transcript as it goes.
//! 3. **Ended on a retryable failure.** This is the part that cannot be relaxed.
//!
//! # Why "idle for ten minutes" is not enough on its own
//!
//! Two tails look similar on disk and mean opposite things:
//!
//! - A turn that ended `end_turn` is the model **handing control back to a
//!   human**. Prompting it again is the exact mistake [`crate::autocontinue`]
//!   exists to avoid — most stops are deliberate. Idleness does not change that.
//! - A turn that ended on `usage limit reached` or a socket error stopped
//!   *against its will*, and is the only thing worth resuming.
//!
//! There is a third tail, and it is the dangerous one: a user message with no
//! reply after it. That means the turn was cut off mid-generation — either it is
//! still running right now, or the process died holding it. Verified the hard
//! way: resuming a session in that state makes the agent spend its turn closing
//! out the orphan with a synthetic `"No response requested."` reply instead of
//! answering, and the protocol still reports `stopReason: end_turn`. So
//! [`TailState::Dangling`] is never a candidate.

use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::autocontinue::{classify_failure, InterruptKind};
use crate::paths::PathEnv;

/// Bytes read from the end of a transcript.
///
/// Only the last few records decide the tail state, but a single record can be
/// large (a tool result carrying a whole file), so this is generous. Transcripts
/// reach tens of MB; reading them whole to answer one question about the end
/// would make the scan cost scale with history rather than with session count.
const TAIL_BYTES: u64 = 256 * 1024;

/// The model id Claude Code records for replies it generated itself rather than
/// receiving from the API.
///
/// These are not answers. They appear when a session is resumed while an
/// earlier turn was left unanswered — the SDK closes out the orphan with
/// `"No response requested."` — and treating one as a real reply would let a
/// takeover report success having accomplished nothing.
pub const SYNTHETIC_MODEL: &str = "<synthetic>";

/// How idle, and how recent, a session must be to count as stalled.
#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StallCriteria {
    /// Only consider sessions touched within this many hours — the quota window
    /// they stalled in must still be the current one.
    pub window_hours: u32,
    /// Silence required before a session counts as stalled rather than slow.
    pub idle_minutes: u32,
}

impl Default for StallCriteria {
    fn default() -> Self {
        Self {
            window_hours: 5,
            idle_minutes: 10,
        }
    }
}

impl StallCriteria {
    #[must_use]
    pub const fn window_ms(self) -> i64 {
        self.window_hours as i64 * 3_600_000
    }

    #[must_use]
    pub const fn idle_ms(self) -> i64 {
        self.idle_minutes as i64 * 60_000
    }

    /// Clamp caller-supplied values into a range that still means something.
    ///
    /// A zero idle window would treat a turn mid-flight as stalled; a window of
    /// days would offer up conversations whose quota bucket rolled over long ago.
    #[must_use]
    pub fn clamp(mut self) -> Self {
        self.window_hours = self.window_hours.clamp(1, 24);
        self.idle_minutes = self.idle_minutes.clamp(1, 24 * 60);
        self
    }
}

/// What the end of a transcript says about the session's state.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum TailState {
    /// Last turn finished normally. The model is waiting for a human, which is
    /// not something to resume automatically.
    AwaitingUser,
    /// Last turn ended on an error the session never got past.
    Failed { message: String },
    /// A user message with nothing after it: cut off mid-generation, or still
    /// generating right now. Never safe to take over.
    Dangling,
    /// No message records at all, or the file could not be read.
    Unknown,
}

impl TailState {
    /// Wire name for the FFI surface.
    #[must_use]
    pub const fn label(&self) -> &'static str {
        match self {
            Self::AwaitingUser => "awaitingUser",
            Self::Failed { .. } => "failed",
            Self::Dangling => "dangling",
            Self::Unknown => "unknown",
        }
    }

    /// Whether the last turn produced a genuine model reply.
    ///
    /// The question a completed takeover has to ask about itself. ACP reports
    /// `stopReason: end_turn` for a turn whose only output was a synthesised
    /// close-out, so trusting the stop reason alone lets a run report success
    /// having produced nothing. Only [`Self::AwaitingUser`] means real work
    /// landed.
    #[must_use]
    pub const fn produced_real_reply(&self) -> bool {
        matches!(self, Self::AwaitingUser)
    }
}

/// Why a session stopped, when it stopped against its will.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum StallReason {
    /// Quota exhausted; time or another account moves it.
    RateLimit,
    /// Network or backend failure; a retry could work.
    Network,
}

/// A session that stopped against its will and has been idle since.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StalledSession {
    /// Session id — what `session/load` re-attaches to.
    pub session_id: String,
    pub file: String,
    /// Directory the session was working in, read from the transcript.
    ///
    /// The encoded folder name is lossy (`_` and `-` both become `-`), so it
    /// cannot be recovered from the path; only the record inside is authoritative.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cwd: Option<String>,
    /// `CLAUDE_CONFIG_DIR` this session lives under; absent means the default login.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub config_dir: Option<String>,
    /// Managed account slot, when the profile maps to one.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub account_number: Option<u32>,
    pub modified_ms: i64,
    /// How long it has been silent.
    pub idle_ms: i64,
    pub reason: StallReason,
    /// The failure text, for a UI that has to explain itself.
    pub last_error: String,
}

/// Whether an assistant record is one Claude Code synthesised rather than
/// received from the model.
///
/// See [`SYNTHETIC_MODEL`]. Exposed because a takeover must apply the same test
/// to the reply it gets back: a run that "succeeded" into a synthetic reply did
/// no work, whatever `stopReason` claimed.
#[must_use]
pub fn is_synthetic_assistant(rec: &Value) -> bool {
    rec.get("message")
        .and_then(|m| m.get("model"))
        .and_then(Value::as_str)
        == Some(SYNTHETIC_MODEL)
}

/// Whether a record is an API error Claude Code recorded in the conversation.
fn is_api_error(rec: &Value) -> bool {
    rec.get("isApiErrorMessage").and_then(Value::as_bool) == Some(true)
}

/// Message records only — the bookkeeping entries around them say nothing about
/// whether the conversation is waiting on anyone.
fn record_role(rec: &Value) -> Option<&str> {
    if rec.get("isSidechain").and_then(Value::as_bool) == Some(true) {
        return None;
    }
    match rec.get("type").and_then(Value::as_str) {
        Some("assistant") => Some("assistant"),
        // `isMeta` user records are plumbing Claude Code injects, not a turn
        // anyone is waiting on.
        Some("user") if rec.get("isMeta").and_then(Value::as_bool) != Some(true) => Some("user"),
        _ => None,
    }
}

/// Plain text of a message record, joining its text blocks.
fn message_text(rec: &Value) -> String {
    let Some(content) = rec.get("message").and_then(|m| m.get("content")) else {
        return String::new();
    };
    if let Some(s) = content.as_str() {
        return s.to_string();
    }
    content
        .as_array()
        .map(|blocks| {
            blocks
                .iter()
                .filter_map(|b| b.get("text").and_then(Value::as_str))
                .collect::<Vec<_>>()
                .join(" ")
        })
        .unwrap_or_default()
}

/// Read the tail of a transcript as whole lines.
///
/// When the file is longer than [`TAIL_BYTES`] the read starts mid-record, so
/// the first (partial) line is dropped — parsing it would fail anyway, but
/// dropping it deliberately keeps a truncated record from being mistaken for a
/// corrupt one.
pub fn read_tail_lines(file: &Path) -> Vec<String> {
    let Ok(mut f) = std::fs::File::open(file) else {
        return Vec::new();
    };
    let len = f.metadata().map(|m| m.len()).unwrap_or(0);
    let from_start = len <= TAIL_BYTES;
    if !from_start && f.seek(SeekFrom::End(-(TAIL_BYTES as i64))).is_err() {
        return Vec::new();
    }

    let mut buf = Vec::new();
    if f.take(TAIL_BYTES).read_to_end(&mut buf).is_err() {
        return Vec::new();
    }
    let text = String::from_utf8_lossy(&buf);
    let mut lines: Vec<String> = text
        .lines()
        .filter(|l| !l.trim().is_empty())
        .map(String::from)
        .collect();
    if !from_start && !lines.is_empty() {
        lines.remove(0);
    }
    lines
}

/// Decide what the end of a transcript means. Pure, so the rules are testable
/// without writing files.
///
/// Walks backwards to the last record that is part of the conversation,
/// ignoring the bookkeeping entries (`queue-operation`, `ai-title`, attachments)
/// that surround it.
#[must_use]
pub fn classify_tail(lines: &[String]) -> TailState {
    for line in lines.iter().rev() {
        let Ok(rec) = serde_json::from_str::<Value>(line) else {
            // A truncated final line is normal — a transcript is appended to
            // live. Keep walking back rather than calling the file unknown.
            continue;
        };
        match record_role(&rec) {
            Some("assistant") => {
                let text = message_text(&rec);
                if is_api_error(&rec) {
                    return TailState::Failed { message: text };
                }
                // A synthesised reply closed out an orphaned turn; it is not an
                // answer, and the turn it "answered" is still unfinished work.
                if is_synthetic_assistant(&rec) {
                    return TailState::Dangling;
                }
                return TailState::AwaitingUser;
            }
            Some("user") => return TailState::Dangling,
            _ => {}
        }
    }
    TailState::Unknown
}

/// Whether a tail state and the clock together make a takeover candidate.
///
/// Split out from the scan so the rule can be tested against a table of times
/// without touching the filesystem.
#[must_use]
pub fn stall_reason(
    tail: &TailState,
    modified_ms: i64,
    now_ms: i64,
    criteria: StallCriteria,
) -> Option<StallReason> {
    let TailState::Failed { message } = tail else {
        return None;
    };

    let idle = now_ms.saturating_sub(modified_ms);
    // A clock skew that puts the file in the future must not read as "idle for
    // negative time"; treat it as freshly touched.
    if idle < criteria.idle_ms() || idle > criteria.window_ms() {
        return None;
    }

    match classify_failure(None, message) {
        InterruptKind::RateLimit => Some(StallReason::RateLimit),
        InterruptKind::Network => Some(StallReason::Network),
        // Auth failures need a human, and an unrecognised error is not
        // something to prompt blindly through.
        _ => None,
    }
}

/// Every stalled session under one Claude config home.
///
/// `account_number` and `config_dir` are carried through untouched so a takeover
/// can authenticate as the account that owns the conversation — resuming it
/// under the default login would fail to find the transcript at all.
#[must_use]
pub fn scan_root(
    env: &PathEnv,
    account_number: Option<u32>,
    config_dir: Option<&Path>,
    now_ms: i64,
    criteria: StallCriteria,
) -> Vec<StalledSession> {
    let mut out = Vec::new();
    let root = crate::projects::transcripts_root(env);
    let Ok(dirs) = std::fs::read_dir(&root) else {
        return out;
    };

    for dir in dirs.flatten() {
        let path = dir.path();
        if !path.is_dir() {
            continue;
        }
        let Ok(files) = std::fs::read_dir(&path) else {
            continue;
        };
        for file in files.flatten() {
            let f = file.path();
            if f.extension().is_some_and(|e| e == "jsonl") {
                if let Some(found) = inspect(&f, account_number, config_dir, now_ms, criteria) {
                    out.push(found);
                }
            }
        }
    }

    out.sort_by_key(|s| std::cmp::Reverse(s.modified_ms));
    out
}

/// Test one transcript, cheaply rejecting it on mtime before reading any of it.
fn inspect(
    file: &Path,
    account_number: Option<u32>,
    config_dir: Option<&Path>,
    now_ms: i64,
    criteria: StallCriteria,
) -> Option<StalledSession> {
    let meta = std::fs::metadata(file).ok()?;
    let modified_ms = meta
        .modified()
        .ok()
        .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
        .map(|d| i64::try_from(d.as_millis()).unwrap_or(i64::MAX))?;

    // mtime alone rules out most files, and costs no read.
    let idle = now_ms.saturating_sub(modified_ms);
    if idle < criteria.idle_ms() || idle > criteria.window_ms() {
        return None;
    }

    let lines = read_tail_lines(file);
    let tail = classify_tail(&lines);
    let reason = stall_reason(&tail, modified_ms, now_ms, criteria)?;
    let TailState::Failed { message } = tail else {
        return None;
    };

    Some(StalledSession {
        session_id: file.file_stem()?.to_str()?.to_string(),
        file: file.to_string_lossy().to_string(),
        cwd: cwd_from_tail(&lines),
        config_dir: config_dir.map(|p| p.to_string_lossy().to_string()),
        account_number,
        modified_ms,
        idle_ms: idle,
        reason,
        last_error: message,
    })
}

/// The `cwd` recorded inside the transcript.
///
/// Read from the tail, which is already in hand: every record carries it, and
/// `/cd` means a session can have moved, so the most recent one is the one that
/// matters.
fn cwd_from_tail(lines: &[String]) -> Option<String> {
    lines.iter().rev().find_map(|l| {
        serde_json::from_str::<Value>(l)
            .ok()?
            .get("cwd")
            .and_then(Value::as_str)
            .filter(|s| !s.is_empty())
            .map(String::from)
    })
}

/// Every config home to scan, tagged with the account that owns it.
///
/// The default login first, then one per session profile. Built here rather
/// than reusing [`crate::projects::scan_envs`] because a takeover needs more
/// than the environment: without the slot number and profile directory it
/// cannot authenticate as the account holding the conversation, and a session
/// is only visible to the profile that holds its transcript.
///
/// The default entry deliberately drops any inherited `CLAUDE_CONFIG_DIR` — the
/// app may itself have been launched from inside a session terminal, and the
/// default login's stalled sessions must not be invisible because of it.
#[must_use]
pub fn scan_roots(env: &PathEnv, backup_root: &Path) -> Vec<(PathEnv, Option<u32>, Option<PathBuf>)> {
    let mut base = env.clone();
    base.claude_config_dir = None;
    let mut out = vec![(base, None, None)];
    for profile in crate::session::list_profiles(backup_root) {
        out.push((
            crate::session::profile_env(env, &profile.dir),
            Some(profile.number),
            Some(profile.dir),
        ));
    }
    out
}

/// Stalled sessions across every config home this machine has history in.
///
/// `roots` pairs each environment with the account slot and profile directory it
/// belongs to; see [`scan_roots`].
#[must_use]
pub fn scan_all(
    roots: &[(PathEnv, Option<u32>, Option<PathBuf>)],
    now_ms: i64,
    criteria: StallCriteria,
) -> Vec<StalledSession> {
    let mut out = Vec::new();
    for (env, number, dir) in roots {
        out.extend(scan_root(env, *number, dir.as_deref(), now_ms, criteria));
    }
    out.sort_by_key(|s| std::cmp::Reverse(s.modified_ms));
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn line(v: Value) -> String {
        v.to_string()
    }

    fn user(text: &str) -> String {
        line(json!({
            "type": "user",
            "cwd": "D:/work",
            "message": { "role": "user", "content": [{ "type": "text", "text": text }] }
        }))
    }

    fn assistant(text: &str) -> String {
        line(json!({
            "type": "assistant",
            "cwd": "D:/work",
            "message": { "role": "assistant", "model": "claude-opus-5",
                         "content": [{ "type": "text", "text": text }] }
        }))
    }

    fn api_error(text: &str) -> String {
        line(json!({
            "type": "assistant",
            "cwd": "D:/work",
            "isApiErrorMessage": true,
            "message": { "role": "assistant", "model": "claude-opus-5",
                         "content": [{ "type": "text", "text": text }] }
        }))
    }

    fn synthetic() -> String {
        line(json!({
            "type": "assistant",
            "cwd": "D:/work",
            "message": { "role": "assistant", "model": SYNTHETIC_MODEL,
                         "content": [{ "type": "text", "text": "No response requested." }] }
        }))
    }

    fn noise() -> String {
        line(json!({ "type": "queue-operation" }))
    }

    #[test]
    fn a_finished_turn_is_awaiting_a_human() {
        let t = classify_tail(&[user("hi"), assistant("done"), noise()]);
        assert_eq!(t, TailState::AwaitingUser);
    }

    #[test]
    fn an_api_error_is_a_failure_carrying_its_text() {
        let t = classify_tail(&[user("hi"), api_error("Claude AI usage limit reached|1234")]);
        assert_eq!(
            t,
            TailState::Failed {
                message: "Claude AI usage limit reached|1234".into()
            }
        );
    }

    #[test]
    fn a_user_message_with_no_reply_is_dangling() {
        // Cut off mid-generation, or generating right now. Either way, hands off.
        let t = classify_tail(&[assistant("ok"), user("do the thing"), noise()]);
        assert_eq!(t, TailState::Dangling);
    }

    #[test]
    fn a_synthetic_reply_does_not_count_as_an_answer() {
        // The SDK writes this when it closes out an orphaned turn. Reading it as
        // a real reply would make a stalled session look finished.
        let t = classify_tail(&[user("do the thing"), synthetic()]);
        assert_eq!(t, TailState::Dangling);
    }

    #[test]
    fn bookkeeping_records_are_walked_past() {
        let t = classify_tail(&[user("hi"), assistant("done"), noise(), noise(), noise()]);
        assert_eq!(t, TailState::AwaitingUser);
    }

    #[test]
    fn a_truncated_last_line_does_not_hide_the_state() {
        // Transcripts are appended to live; a half-written final line is normal.
        let mut lines = vec![user("hi"), assistant("done")];
        lines.push("{ \"type\": \"assist".into());
        assert_eq!(classify_tail(&lines), TailState::AwaitingUser);
    }

    #[test]
    fn sidechain_records_are_ignored() {
        let sub = line(json!({
            "type": "assistant", "isSidechain": true,
            "message": { "role": "assistant", "model": "claude-opus-5",
                         "content": [{ "type": "text", "text": "subagent" }] }
        }));
        // The subagent's reply must not mask the fact that the main thread is
        // still waiting on its own turn.
        assert_eq!(classify_tail(&[user("go"), sub]), TailState::Dangling);
    }

    #[test]
    fn meta_user_records_are_ignored() {
        let meta = line(json!({
            "type": "user", "isMeta": true,
            "message": { "role": "user", "content": [{ "type": "text", "text": "plumbing" }] }
        }));
        assert_eq!(
            classify_tail(&[user("hi"), assistant("done"), meta]),
            TailState::AwaitingUser
        );
    }

    #[test]
    fn an_empty_transcript_is_unknown() {
        assert_eq!(classify_tail(&[]), TailState::Unknown);
        assert_eq!(classify_tail(&[noise()]), TailState::Unknown);
    }

    // ── the clock rules ──────────────────────────────────────────────────

    const NOW: i64 = 1_000_000_000;

    fn failed(text: &str) -> TailState {
        TailState::Failed {
            message: text.into(),
        }
    }

    #[test]
    fn a_quota_wall_inside_the_window_is_a_candidate() {
        let c = StallCriteria::default();
        let modified = NOW - 20 * 60_000; // 20 minutes ago
        assert_eq!(
            stall_reason(&failed("Claude AI usage limit reached"), modified, NOW, c),
            Some(StallReason::RateLimit)
        );
    }

    #[test]
    fn a_network_failure_inside_the_window_is_a_candidate() {
        let c = StallCriteria::default();
        let modified = NOW - 30 * 60_000;
        assert_eq!(
            stall_reason(&failed("API Error: Connection closed mid-response."), modified, NOW, c),
            Some(StallReason::Network)
        );
    }

    #[test]
    fn a_session_still_working_is_not_stalled() {
        // Two minutes of silence is a turn in progress, not a stall.
        let c = StallCriteria::default();
        assert_eq!(
            stall_reason(&failed("usage limit reached"), NOW - 2 * 60_000, NOW, c),
            None
        );
    }

    #[test]
    fn a_session_older_than_the_window_is_not_offered() {
        // The 5h bucket it stalled in has rolled over; this is archaeology.
        let c = StallCriteria::default();
        assert_eq!(
            stall_reason(&failed("usage limit reached"), NOW - 6 * 3_600_000, NOW, c),
            None
        );
    }

    #[test]
    fn a_finished_conversation_is_never_resumed_however_idle() {
        // The rule this module exists to protect: `end_turn` means the model
        // handed control back to a human, and time does not change that.
        let c = StallCriteria::default();
        assert_eq!(stall_reason(&TailState::AwaitingUser, NOW - 3_600_000, NOW, c), None);
    }

    #[test]
    fn a_dangling_turn_is_never_taken_over() {
        // It may still be generating right now; resuming it burns the turn on a
        // synthetic close-out instead of doing the work.
        let c = StallCriteria::default();
        assert_eq!(stall_reason(&TailState::Dangling, NOW - 3_600_000, NOW, c), None);
        assert_eq!(stall_reason(&TailState::Unknown, NOW - 3_600_000, NOW, c), None);
    }

    #[test]
    fn an_auth_failure_needs_a_human_not_a_retry() {
        let c = StallCriteria::default();
        assert_eq!(
            stall_reason(&failed("Failed to authenticate. API Error: 403"), NOW - 3_600_000, NOW, c),
            None
        );
    }

    #[test]
    fn a_future_mtime_does_not_read_as_stalled() {
        // Clock skew between the writer and us must not manufacture a candidate.
        let c = StallCriteria::default();
        assert_eq!(
            stall_reason(&failed("usage limit reached"), NOW + 60_000, NOW, c),
            None
        );
    }

    #[test]
    fn criteria_clamp_rejects_nonsense() {
        let c = StallCriteria {
            window_hours: 0,
            idle_minutes: 0,
        }
        .clamp();
        assert!(c.window_hours >= 1);
        assert!(c.idle_minutes >= 1);

        let c = StallCriteria {
            window_hours: 9999,
            idle_minutes: 9999,
        }
        .clamp();
        assert_eq!(c.window_hours, 24);
        assert_eq!(c.idle_minutes, 24 * 60);
    }

    // ── end to end over a real file ──────────────────────────────────────

    #[test]
    fn scans_a_real_transcript_tree() {
        let dir = tempfile::tempdir().unwrap();
        let home = dir.path();
        let projects = home.join(".claude").join("projects").join("D--work");
        std::fs::create_dir_all(&projects).unwrap();

        let stalled = projects.join("11111111-1111-1111-1111-111111111111.jsonl");
        std::fs::write(
            &stalled,
            format!("{}\n{}\n", user("do the thing"), api_error("Claude AI usage limit reached")),
        )
        .unwrap();

        let finished = projects.join("22222222-2222-2222-2222-222222222222.jsonl");
        std::fs::write(&finished, format!("{}\n{}\n", user("hi"), assistant("done"))).unwrap();

        // Backdate both well past the idle threshold.
        let old = std::time::SystemTime::now() - std::time::Duration::from_secs(30 * 60);
        for f in [&stalled, &finished] {
            let file = std::fs::File::options().write(true).open(f).unwrap();
            file.set_modified(old).unwrap();
        }

        let env = PathEnv::isolated(home);
        let now = chrono::Utc::now().timestamp_millis();
        let found = scan_root(&env, Some(2), Some(Path::new("D:/profile")), now, StallCriteria::default());

        assert_eq!(found.len(), 1, "only the stalled session qualifies: {found:?}");
        let s = &found[0];
        assert_eq!(s.session_id, "11111111-1111-1111-1111-111111111111");
        assert_eq!(s.reason, StallReason::RateLimit);
        assert_eq!(s.account_number, Some(2));
        assert_eq!(s.config_dir.as_deref(), Some("D:/profile"));
        assert_eq!(s.cwd.as_deref(), Some("D:/work"));
        assert!(s.idle_ms >= StallCriteria::default().idle_ms());
    }

    #[test]
    fn reads_the_tail_of_a_file_larger_than_the_window() {
        let dir = tempfile::tempdir().unwrap();
        let f = dir.path().join("big.jsonl");

        // Pad past TAIL_BYTES so the read starts mid-record.
        let filler = line(json!({
            "type": "user", "isMeta": true,
            "message": { "role": "user", "content": [{ "type": "text", "text": "x".repeat(4096) }] }
        }));
        let mut text = String::new();
        while text.len() < (TAIL_BYTES as usize) + 8192 {
            text.push_str(&filler);
            text.push('\n');
        }
        text.push_str(&api_error("Claude AI usage limit reached"));
        text.push('\n');
        std::fs::write(&f, text).unwrap();

        let lines = read_tail_lines(&f);
        assert!(!lines.is_empty());
        assert_eq!(
            classify_tail(&lines),
            TailState::Failed {
                message: "Claude AI usage limit reached".into()
            }
        );
    }
}
