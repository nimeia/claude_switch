//! Journal of supervised agent runs, so a run outlives the window that started
//! it — and can be picked up again after the app restarts.
//!
//! # What can and cannot survive
//!
//! An ACP agent is a **child process of the app**. When the app exits, the agent
//! goes with it; there is no way around that short of a detached supervisor, and
//! an agent editing files with no UI attached is not something to build by
//! accident. So "unattended" here means two specific things:
//!
//! - A run keeps going when its *window* closes, for as long as the app lives.
//! - Every run is journaled with its `sessionId`, so a run cut short by the app
//!   exiting can be **resumed** on the next launch — same conversation, via
//!   `session/load`, with the work already done still in it.
//!
//! The second is what makes a crash or a reboot survivable. It is recovery, not
//! continuity, and the distinction is worth keeping honest: after a restart the
//! agent has to be told to continue, it does not simply still be running.
//!
//! # Detecting a run that died with its app
//!
//! A run left in [`RunStatus::Running`] proves nothing on its own — the process
//! that wrote it may be gone. Each record carries the `owner_pid` that last
//! touched it, and [`RunJournal::reap`] demotes running records whose owner is
//! no longer alive to [`RunStatus::Interrupted`]. That is the same trick Claude
//! Code uses for `~/.claude/sessions/<pid>.json`, and it is why the journal is
//! written on every status change rather than only at the end.

use std::collections::BTreeMap;
use std::path::Path;

use serde::{Deserialize, Serialize};

use crate::autocontinue::ContinuePolicy;
use crate::errors::{Error, Result};
use crate::fsutil::atomic_write;

/// Journal file under the backup root.
pub const JOURNAL_FILENAME: &str = "agent-runs.json";

/// Schema version of the journal payload.
pub const JOURNAL_SCHEMA_VERSION: u32 = 1;

/// Completed records kept before the oldest are dropped.
///
/// The journal is a recovery aid, not history: what matters is the runs that
/// might still need action, plus enough recent context to see what happened.
pub const MAX_FINISHED_RECORDS: usize = 50;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum RunStatus {
    /// A live run, owned by `owner_pid`.
    Running,
    /// Was running when its owner disappeared. Resumable.
    Interrupted,
    /// Reached a normal end.
    Completed,
    /// Stopped for a reason that needs a human (auth, exhausted retries).
    Failed,
    /// Stopped by the user.
    Cancelled,
}

impl RunStatus {
    /// Whether the run could be picked up again where it left off.
    #[must_use]
    pub const fn is_resumable(self) -> bool {
        matches!(self, Self::Interrupted | Self::Failed | Self::Cancelled)
    }

    #[must_use]
    pub const fn is_live(self) -> bool {
        matches!(self, Self::Running)
    }
}

/// One supervised run.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentRun {
    /// Stable id for this run. The ACP `sessionId` once one exists, else a
    /// locally generated placeholder so a run that died before `session/new`
    /// still has a record.
    pub id: String,
    /// ACP session to resume. Absent when the run never got that far.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub session_id: Option<String>,
    /// Project directory the agent was rooted at.
    pub cwd: String,
    /// Managed account slot this run belongs to.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub account_number: Option<u32>,
    /// `CLAUDE_CONFIG_DIR` used; absent means the default login.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub config_dir: Option<String>,
    /// Permission mode the run was started with.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mode: Option<String>,
    /// The instruction that started it — what a resume prompt is about.
    pub prompt: String,
    /// Retry policy in force.
    #[serde(default)]
    pub policy: ContinuePolicy,
    pub status: RunStatus,
    /// Process that last wrote this record.
    pub owner_pid: u32,
    /// Epoch ms.
    pub created_ms: i64,
    pub updated_ms: i64,
    /// Turns taken so far.
    #[serde(default)]
    pub turns: u32,
    /// Automatic continuations issued so far.
    #[serde(default)]
    pub continuations: u32,
    /// Why it stopped, when it has.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub stop_cause: Option<String>,
    /// Last interruption detail, for the UI to explain itself.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_error: Option<String>,
}

impl AgentRun {
    /// Whether this record can be handed to a resume.
    ///
    /// Needs both a status that invites it and a session to re-attach to;
    /// without a `sessionId` there is nothing to continue, only to restart.
    #[must_use]
    pub fn is_resumable(&self) -> bool {
        self.status.is_resumable() && self.session_id.is_some()
    }

    /// Short label for a list row: the first line of the prompt, trimmed.
    #[must_use]
    pub fn title(&self) -> String {
        let first = self
            .prompt
            .lines()
            .map(str::trim)
            .find(|l| !l.is_empty())
            .unwrap_or("");
        if first.chars().count() <= 80 {
            return first.to_string();
        }
        let mut s: String = first.chars().take(79).collect();
        s.push('…');
        s
    }
}

/// The journal file's contents.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RunJournal {
    #[serde(default = "default_schema")]
    pub schema_version: u32,
    #[serde(default)]
    pub runs: Vec<AgentRun>,
}

fn default_schema() -> u32 {
    JOURNAL_SCHEMA_VERSION
}

impl Default for RunJournal {
    fn default() -> Self {
        Self {
            schema_version: JOURNAL_SCHEMA_VERSION,
            runs: Vec::new(),
        }
    }
}

impl RunJournal {
    /// Read the journal, treating a missing or unreadable file as empty.
    ///
    /// A corrupt journal must not stop the app from starting: it holds recovery
    /// hints, and losing them is worse handled by refusing to launch than by
    /// starting clean.
    #[must_use]
    pub fn load(path: &Path) -> Self {
        let Ok(text) = std::fs::read_to_string(path) else {
            return Self::default();
        };
        serde_json::from_str(&text).unwrap_or_default()
    }

    pub fn save(&self, path: &Path) -> Result<()> {
        let json = serde_json::to_vec_pretty(self).map_err(|e| Error::Internal(e.to_string()))?;
        atomic_write(path, &json).map_err(Error::Io)
    }

    /// Insert or replace a run by id, keeping newest-updated first.
    pub fn upsert(&mut self, run: AgentRun) {
        if let Some(slot) = self.runs.iter_mut().find(|r| r.id == run.id) {
            *slot = run;
        } else {
            self.runs.push(run);
        }
        self.runs.sort_by(|a, b| b.updated_ms.cmp(&a.updated_ms));
    }

    pub fn remove(&mut self, id: &str) -> bool {
        let before = self.runs.len();
        self.runs.retain(|r| r.id != id);
        before != self.runs.len()
    }

    #[must_use]
    pub fn get(&self, id: &str) -> Option<&AgentRun> {
        self.runs.iter().find(|r| r.id == id)
    }

    /// Demote running records whose owning process is gone.
    ///
    /// `is_alive` decides whether a pid is still running; injected so the rule
    /// can be tested without spawning processes. Returns the ids demoted.
    pub fn reap(&mut self, now_ms: i64, is_alive: &dyn Fn(u32) -> bool) -> Vec<String> {
        let mut demoted = Vec::new();
        for run in &mut self.runs {
            if run.status.is_live() && !is_alive(run.owner_pid) {
                run.status = RunStatus::Interrupted;
                run.updated_ms = now_ms;
                if run.last_error.is_none() {
                    run.last_error = Some("the app closed while this run was in progress".into());
                }
                demoted.push(run.id.clone());
            }
        }
        demoted
    }

    /// Drop the oldest finished records beyond [`MAX_FINISHED_RECORDS`].
    ///
    /// Live and interrupted runs are never pruned — those are the ones a user
    /// might still act on.
    pub fn prune(&mut self) -> usize {
        let mut kept_finished = 0;
        let before = self.runs.len();
        self.runs.retain(|r| {
            if r.status.is_live() || r.status == RunStatus::Interrupted {
                return true;
            }
            kept_finished += 1;
            kept_finished <= MAX_FINISHED_RECORDS
        });
        before - self.runs.len()
    }

    /// Runs that could be resumed, newest first.
    #[must_use]
    pub fn resumable(&self) -> Vec<&AgentRun> {
        self.runs.iter().filter(|r| r.is_resumable()).collect()
    }

    /// Count of runs by status, for a status line.
    #[must_use]
    pub fn tally(&self) -> BTreeMap<String, usize> {
        let mut out = BTreeMap::new();
        for run in &self.runs {
            let key = serde_json::to_value(run.status)
                .ok()
                .and_then(|v| v.as_str().map(String::from))
                .unwrap_or_else(|| "unknown".into());
            *out.entry(key).or_insert(0) += 1;
        }
        out
    }
}

/// Whether a process id is currently running.
///
/// Used as the default `is_alive` for [`RunJournal::reap`].
#[must_use]
pub fn process_is_alive(pid: u32) -> bool {
    if pid == 0 {
        return false;
    }
    #[cfg(windows)]
    {
        crate::agentruns::windows_process_is_alive(pid)
    }
    #[cfg(not(windows))]
    {
        // /proc is the portable-enough answer on the platforms we build for.
        std::path::Path::new(&format!("/proc/{pid}")).exists()
    }
}

#[cfg(windows)]
fn windows_process_is_alive(pid: u32) -> bool {
    // Reading the process list avoids linking a Win32 crate into core for one
    // question, and is accurate enough: a pid absent from the list is gone.
    // A recycled pid would make a dead run look alive, which fails safe — the
    // run is simply not offered for resume until the next launch.
    let Ok(output) = std::process::Command::new("tasklist")
        .args(["/FI", &format!("PID eq {pid}"), "/NH", "/FO", "CSV"])
        .output()
    else {
        // Cannot tell: assume gone, so a stuck record still becomes resumable.
        return false;
    };
    let text = String::from_utf8_lossy(&output.stdout);
    text.contains(&format!("\"{pid}\""))
}

/// Epoch milliseconds now.
#[must_use]
pub fn now_ms() -> i64 {
    chrono::Utc::now().timestamp_millis()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn run(id: &str, status: RunStatus, pid: u32, updated: i64) -> AgentRun {
        AgentRun {
            id: id.into(),
            session_id: Some(format!("sess-{id}")),
            cwd: "D:/work".into(),
            account_number: Some(1),
            config_dir: None,
            mode: Some("acceptEdits".into()),
            prompt: "do the thing".into(),
            policy: ContinuePolicy::default(),
            status,
            owner_pid: pid,
            created_ms: 1_000,
            updated_ms: updated,
            turns: 1,
            continuations: 0,
            stop_cause: None,
            last_error: None,
        }
    }

    #[test]
    fn upsert_replaces_by_id_and_sorts_newest_first() {
        let mut j = RunJournal::default();
        j.upsert(run("a", RunStatus::Running, 1, 10));
        j.upsert(run("b", RunStatus::Running, 1, 20));
        assert_eq!(j.runs.len(), 2);
        assert_eq!(j.runs[0].id, "b");

        let mut updated = run("a", RunStatus::Completed, 1, 30);
        updated.turns = 7;
        j.upsert(updated);
        assert_eq!(j.runs.len(), 2, "same id must replace, not duplicate");
        assert_eq!(j.runs[0].id, "a");
        assert_eq!(j.get("a").unwrap().turns, 7);
    }

    #[test]
    fn reap_demotes_runs_whose_owner_is_gone() {
        let mut j = RunJournal::default();
        j.upsert(run("alive", RunStatus::Running, 100, 10));
        j.upsert(run("dead", RunStatus::Running, 200, 10));

        let demoted = j.reap(999, &|pid| pid == 100);

        assert_eq!(demoted, vec!["dead".to_string()]);
        assert_eq!(j.get("alive").unwrap().status, RunStatus::Running);
        assert_eq!(j.get("dead").unwrap().status, RunStatus::Interrupted);
        assert_eq!(j.get("dead").unwrap().updated_ms, 999);
        assert!(j.get("dead").unwrap().last_error.is_some());
    }

    #[test]
    fn reap_leaves_finished_runs_alone() {
        // A completed run's owner is always gone; that is not an interruption.
        let mut j = RunJournal::default();
        j.upsert(run("done", RunStatus::Completed, 200, 10));
        let demoted = j.reap(999, &|_| false);
        assert!(demoted.is_empty());
        assert_eq!(j.get("done").unwrap().status, RunStatus::Completed);
    }

    #[test]
    fn resumable_needs_both_a_status_and_a_session() {
        let mut interrupted = run("x", RunStatus::Interrupted, 1, 1);
        assert!(interrupted.is_resumable());

        // Died before session/new: there is nothing to continue.
        interrupted.session_id = None;
        assert!(!interrupted.is_resumable());

        assert!(!run("y", RunStatus::Running, 1, 1).is_resumable());
        assert!(!run("z", RunStatus::Completed, 1, 1).is_resumable());
        assert!(run("w", RunStatus::Failed, 1, 1).is_resumable());
        assert!(run("v", RunStatus::Cancelled, 1, 1).is_resumable());
    }

    #[test]
    fn prune_keeps_live_and_interrupted_regardless_of_age() {
        let mut j = RunJournal::default();
        j.upsert(run("live", RunStatus::Running, 1, 1));
        j.upsert(run("stuck", RunStatus::Interrupted, 1, 2));
        for i in 0..(MAX_FINISHED_RECORDS + 10) {
            let ts = 100 + i64::try_from(i).expect("loop index fits");
            j.upsert(run(&format!("done{i}"), RunStatus::Completed, 1, ts));
        }

        let dropped = j.prune();

        assert_eq!(dropped, 10);
        assert!(j.get("live").is_some(), "a live run must never be pruned");
        assert!(j.get("stuck").is_some(), "a resumable run must never be pruned");
        assert_eq!(
            j.runs.iter().filter(|r| r.status == RunStatus::Completed).count(),
            MAX_FINISHED_RECORDS
        );
    }

    #[test]
    fn prune_drops_the_oldest_finished_first() {
        let mut j = RunJournal::default();
        // Newest updated sorts first, so the survivors are the high timestamps.
        for i in 0..=MAX_FINISHED_RECORDS {
            let ts = i64::try_from(i).expect("loop index fits");
            j.upsert(run(&format!("done{i}"), RunStatus::Completed, 1, ts));
        }
        j.prune();
        assert!(j.get("done0").is_none(), "oldest should have been dropped");
        assert!(j.get(&format!("done{MAX_FINISHED_RECORDS}")).is_some());
    }

    #[test]
    fn roundtrips_through_disk() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(JOURNAL_FILENAME);

        let mut j = RunJournal::default();
        j.upsert(run("a", RunStatus::Running, 42, 10));
        j.save(&path).unwrap();

        let back = RunJournal::load(&path);
        assert_eq!(back.schema_version, JOURNAL_SCHEMA_VERSION);
        assert_eq!(back.runs.len(), 1);
        assert_eq!(back.runs[0].owner_pid, 42);
        assert_eq!(back.runs[0].session_id.as_deref(), Some("sess-a"));
    }

    #[test]
    fn a_missing_or_corrupt_journal_reads_as_empty() {
        let dir = tempfile::tempdir().unwrap();
        let missing = dir.path().join("nope.json");
        assert!(RunJournal::load(&missing).runs.is_empty());

        let corrupt = dir.path().join("bad.json");
        std::fs::write(&corrupt, b"{ not json").unwrap();
        // Recovery hints are not worth refusing to start over.
        assert!(RunJournal::load(&corrupt).runs.is_empty());
    }

    #[test]
    fn title_is_the_first_nonempty_line_bounded() {
        let mut r = run("a", RunStatus::Running, 1, 1);
        r.prompt = "\n\n  refactor the parser  \nand then some".into();
        assert_eq!(r.title(), "refactor the parser");

        r.prompt = "x".repeat(200);
        let t = r.title();
        assert_eq!(t.chars().count(), 80);
        assert!(t.ends_with('…'));
    }

    #[test]
    fn title_truncation_counts_characters_not_bytes() {
        // A byte-based cut would slice a multi-byte character and panic.
        let mut r = run("a", RunStatus::Running, 1, 1);
        r.prompt = "整理项目功能清单".repeat(20);
        let t = r.title();
        assert_eq!(t.chars().count(), 80);
    }

    #[test]
    fn pid_zero_is_never_alive() {
        assert!(!process_is_alive(0));
    }

    #[test]
    fn the_current_process_is_alive() {
        assert!(process_is_alive(std::process::id()));
    }
}
