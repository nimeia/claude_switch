//! Directories Claude Code has worked in, and their session transcripts.
//!
//! Two independent sources, deliberately kept distinguishable:
//!
//! - `~/.claude.json` → `projects`: every directory Claude Code has *registered*,
//!   with facts about its **most recent** session only. Nothing here is
//!   cumulative — `lastCost`, `lastTotalInputTokens` and friends are overwritten
//!   each session, so they must never be presented as totals.
//! - `<claude home>/projects/<encoded>/…jsonl`: the actual transcripts. This is
//!   the only source of history, and reading it costs real I/O (tens of MB), so
//!   totals are computed on demand via [`scan_stats`] rather than on every list.
//!
//! Neither source records *which account* did the work, and a switch leaves the
//! registry alone apart from `oauthAccount`. A directory therefore belongs to
//! the machine, not to a managed slot.
//!
//! Session mode (see [`crate::session`]) breaks the single-root assumption: a
//! session-mode Claude Code writes its registry and transcripts inside its own
//! profile, so history accumulates in several places at once. Every scan here
//! therefore takes a *list* of roots ([`scan_envs`]) — the default `~/.claude`
//! plus one per profile — and merges them per directory. Merging rather than
//! copying is deliberate: a copy would fork the history it was meant to unify,
//! which is exactly why session profiles do not share `projects/` on disk.

use std::collections::BTreeMap;
use std::io::{BufRead, BufReader, Read};
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::errors::{Error, Result};
use crate::paths::PathEnv;

/// Bytes of a transcript read when only its header is wanted.
const HEAD_BYTES: u64 = 64 * 1024;

/// A directory Claude Code has been used in.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectSummary {
    /// Path as recorded, for display.
    pub path: String,
    /// Last path segment — what the UI shows as a name.
    pub name: String,
    /// Number of transcript files for this directory.
    pub session_count: u32,
    /// Newest activity in epoch ms, from transcripts or the registry.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_active_ms: Option<i64>,
    /// Newest session id, resumable with `claude --resume`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_session_id: Option<String>,
    /// Opening prompt of the most recent session (registry-provided).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_prompt: Option<String>,
    /// Present in the global config's `projects` map.
    pub registered: bool,
    /// Directory holding this project's transcripts, when it has any.
    ///
    /// The newest one when several roots hold history for this directory; see
    /// [`ProjectSummary::transcript_dirs`] for the complete set.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub transcript_dir: Option<String>,
    /// Every transcript folder for this directory, newest first.
    ///
    /// More than one once session mode is used: the default profile and each
    /// session profile keep their own transcripts, and the same directory can
    /// have been worked in under several accounts. Reading only the first would
    /// silently drop whole conversations from the totals.
    #[serde(default)]
    pub transcript_dirs: Vec<String>,
    /// Total size of this directory's transcripts — what a scan would cost.
    pub transcript_bytes: u64,

    // ── Most recent session only. Never totals. ──────────────────────────
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_cost_usd: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_duration_ms: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_lines_added: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_lines_removed: Option<i64>,
}

/// One resumable conversation.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionSummary {
    /// Session id — the argument to `claude --resume`.
    pub id: String,
    pub file: String,
    pub bytes: u64,
    /// File mtime in epoch ms (always available, unlike in-file timestamps).
    pub modified_ms: i64,
    /// First timestamp inside the transcript, when one could be read.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub started_ms: Option<i64>,
    /// Opening user message, trimmed for display.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub first_prompt: Option<String>,
    /// Working directory recorded inside the transcript.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cwd: Option<String>,
}

/// Cumulative totals for one directory — the expensive answer.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectStats {
    pub sessions: u32,
    pub user_messages: u32,
    pub assistant_messages: u32,
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub cache_creation_tokens: u64,
    pub cache_read_tokens: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub first_ms: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_ms: Option<i64>,
    /// Bytes actually read, so the UI can explain what the wait bought.
    pub scanned_bytes: u64,
    /// Transcript lines that failed to parse — a non-zero count means the
    /// totals are a floor, not an exact figure.
    pub skipped_lines: u32,

    /// Per-session breakdown, newest first. Free: the scan already reads it all.
    pub sessions_detail: Vec<SessionStats>,
    /// Local calendar days from first to last activity, gaps filled with zeros.
    ///
    /// Filled rather than sparse so a time axis stays honest — a chart of only
    /// the active days silently closes the gaps and turns an idle fortnight into
    /// a neighbouring column.
    pub daily: Vec<DayStats>,
}

/// One session's share of the totals.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionStats {
    pub id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub first_ms: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_ms: Option<i64>,
    pub user_messages: u32,
    pub output_tokens: u64,
    pub bytes: u64,
    /// Opening user message, for labelling the session.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub first_prompt: Option<String>,
}

/// One local calendar day.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DayStats {
    /// `YYYY-MM-DD` in local time — the reader's "what did I do on Tuesday".
    pub day: String,
    pub user_messages: u32,
    pub output_tokens: u64,
}

/// Identity key for a directory path.
///
/// Claude Code writes the same directory both ways (`D:/dev/x` and `D:\dev\x`),
/// which would otherwise count one project twice. Case is folded because Windows
/// paths are case-insensitive; on other platforms the risk of merging two real
/// directories that differ only in case is far smaller than the certainty of
/// double-counting here.
#[must_use]
pub fn path_key(path: &str) -> String {
    path.replace('\\', "/").trim_end_matches('/').to_lowercase()
}

/// Last path segment, falling back to the whole path.
#[must_use]
pub fn display_name(path: &str) -> String {
    path.replace('\\', "/")
        .trim_end_matches('/')
        .rsplit('/')
        .next()
        .filter(|s| !s.is_empty())
        .unwrap_or(path)
        .to_string()
}

fn i64_field(v: &Value, key: &str) -> Option<i64> {
    v.get(key).and_then(Value::as_i64).filter(|n| *n > 0)
}

fn mtime_ms(meta: &std::fs::Metadata) -> i64 {
    meta.modified()
        .ok()
        .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
        // Milliseconds since 1970 fit i64 until the year 292 million.
        .map_or(0, |d| i64::try_from(d.as_millis()).unwrap_or(i64::MAX))
}

/// Root holding per-directory transcript folders.
#[must_use]
pub fn transcripts_root(env: &PathEnv) -> PathBuf {
    env.claude_config_home().join("projects")
}

/// Read the head of a transcript to learn its `cwd`, opening prompt and start.
///
/// Only the first [`HEAD_BYTES`] are read: a transcript can be tens of MB, and
/// everything wanted here is written near the top.
fn read_head(file: &Path) -> SessionSummary {
    let meta = std::fs::metadata(file).ok();
    let mut out = SessionSummary {
        id: file
            .file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or_default()
            .to_string(),
        file: file.to_string_lossy().to_string(),
        bytes: meta.as_ref().map_or(0, std::fs::Metadata::len),
        modified_ms: meta.as_ref().map_or(0, mtime_ms),
        ..Default::default()
    };

    let Ok(f) = std::fs::File::open(file) else {
        return out;
    };
    let mut reader = BufReader::new(f.take(HEAD_BYTES));
    let mut line = String::new();
    while out.first_prompt.is_none() || out.cwd.is_none() {
        line.clear();
        match reader.read_line(&mut line) {
            Ok(0) | Err(_) => break,
            Ok(_) => {}
        }
        let Ok(rec) = serde_json::from_str::<Value>(&line) else {
            continue;
        };
        if out.cwd.is_none() {
            if let Some(c) = rec
                .get("cwd")
                .and_then(Value::as_str)
                .filter(|s| !s.is_empty())
            {
                out.cwd = Some(c.to_string());
            }
        }
        if out.started_ms.is_none() {
            out.started_ms = rec
                .get("timestamp")
                .and_then(Value::as_str)
                .and_then(parse_iso_ms);
        }
        if out.first_prompt.is_none() && is_real_user_turn(&rec) {
            out.first_prompt = message_text(&rec)
                .filter(|t| !is_synthetic_prompt(t))
                .map(|t| trim_prompt(&t));
        }
    }
    out
}

/// Wrappers Claude Code injects into the user turn that are not things the user
/// typed: hook output, slash-command plumbing, and reminders.
///
/// The opening records of a transcript are usually several of these, so taking
/// the literal first user message as a session title yields
/// `<local-command-caveat>Caveat: The messages…` for nearly every session —
/// useless for picking which conversation to resume.
const SYNTHETIC_PREFIXES: &[&str] = &[
    "<local-command-caveat>",
    "<local-command-stdout>",
    "<command-name>",
    "<command-message>",
    "<command-args>",
    "<system-reminder>",
    "<user-prompt-submit-hook>",
    "<task-notification>",
    "Caveat: The messages below",
];

/// Whether this text is machinery rather than something the user said.
#[must_use]
pub fn is_synthetic_prompt(text: &str) -> bool {
    let t = text.trim_start();
    t.is_empty() || SYNTHETIC_PREFIXES.iter().any(|p| t.starts_with(p))
}

/// Whether a record is the user actually speaking (not injected, not a subagent).
fn is_real_user_turn(rec: &Value) -> bool {
    rec.get("type").and_then(Value::as_str) == Some("user")
        && rec.get("isMeta").and_then(Value::as_bool) != Some(true)
        && rec.get("isSidechain").and_then(Value::as_bool) != Some(true)
}

/// Plain text of a message record, joining text blocks.
fn message_text(rec: &Value) -> Option<String> {
    let content = rec.get("message")?.get("content")?;
    if let Some(s) = content.as_str() {
        return Some(s.to_string());
    }
    let joined = content
        .as_array()?
        .iter()
        .filter_map(|b| b.get("text").and_then(Value::as_str))
        .collect::<Vec<_>>()
        .join(" ");
    (!joined.trim().is_empty()).then_some(joined)
}

/// Collapse whitespace and cap length for a one-line title.
///
/// Control characters are dropped: transcripts capture terminal output verbatim,
/// ANSI escapes included, and those render as garbage in a list.
fn trim_prompt(text: &str) -> String {
    let cleaned: String = text
        .chars()
        .map(|c| if c.is_control() { ' ' } else { c })
        .collect();
    cleaned
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
        .chars()
        .take(120)
        .collect()
}

fn parse_iso_ms(s: &str) -> Option<i64> {
    chrono::DateTime::parse_from_rfc3339(s)
        .ok()
        .map(|d| d.timestamp_millis())
}

/// Transcript folders on disk, mapped to the directory they belong to.
///
/// The folder name is an encoding of the path, but the encoding is lossy
/// (separators and underscores all collapse to `-`) and is Claude Code's to
/// change. The `cwd` recorded inside the transcripts is authoritative, so it is
/// used instead of decoding the name.
fn scan_transcript_dirs(root: &Path) -> Vec<(String, TranscriptDir)> {
    let Ok(entries) = std::fs::read_dir(root) else {
        return Vec::new();
    };
    let mut out = Vec::new();
    for entry in entries.flatten() {
        if !entry.file_type().is_ok_and(|t| t.is_dir()) {
            continue;
        }
        let dir = entry.path();
        let mut files: Vec<PathBuf> = std::fs::read_dir(&dir)
            .into_iter()
            .flatten()
            .flatten()
            .map(|e| e.path())
            .filter(|p| p.extension().is_some_and(|e| e == "jsonl"))
            .collect();
        if files.is_empty() {
            continue;
        }
        // Newest first: the head read below then describes the latest session.
        files.sort_by_key(|p| std::cmp::Reverse(std::fs::metadata(p).map_or(0, |m| mtime_ms(&m))));

        let bytes = files
            .iter()
            .filter_map(|p| std::fs::metadata(p).ok())
            .map(|m| m.len())
            .sum();
        let newest = read_head(&files[0]);
        // Fall back to the folder name only when no transcript names a cwd.
        let path = newest.cwd.clone().unwrap_or_else(|| {
            dir.file_name()
                .unwrap_or_default()
                .to_string_lossy()
                .to_string()
        });

        out.push((
            path_key(&path),
            TranscriptDir {
                path,
                dir: dir.to_string_lossy().to_string(),
                session_count: u32::try_from(files.len()).unwrap_or(u32::MAX),
                bytes,
                newest,
            },
        ));
    }
    out
}

struct TranscriptDir {
    path: String,
    dir: String,
    session_count: u32,
    bytes: u64,
    newest: SessionSummary,
}

/// Every Claude config home whose history belongs to this machine.
///
/// The default profile first, then one per session profile. Session profiles
/// are discovered from disk rather than from the account list so a removed
/// account's conversations stay visible until its profile is deleted.
#[must_use]
pub fn scan_envs(env: &PathEnv, backup_root: &Path) -> Vec<PathEnv> {
    let mut out = vec![default_env(env)];
    for profile in crate::session::list_profiles(backup_root) {
        out.push(crate::session::profile_env(env, &profile.dir));
    }
    out
}

/// The default profile's env, ignoring any inherited `CLAUDE_CONFIG_DIR`.
///
/// The tray process could itself have been launched from inside a session
/// terminal, and the default profile's history must not be missed because of it.
fn default_env(env: &PathEnv) -> PathEnv {
    let mut base = env.clone();
    base.claude_config_dir = None;
    base
}

/// Every directory Claude Code has worked in, newest activity first.
///
/// Registered-but-never-used directories are included and flagged: "opened once"
/// and "worked in" are different questions, and collapsing them would make the
/// count unexplainable.
pub fn list_projects(env: &PathEnv) -> Result<Vec<ProjectSummary>> {
    list_projects_in(std::slice::from_ref(env))
}

/// [`list_projects`] merged across several config homes.
pub fn list_projects_in(envs: &[PathEnv]) -> Result<Vec<ProjectSummary>> {
    let mut by_key: BTreeMap<String, ProjectSummary> = BTreeMap::new();
    // How fresh the stored "most recent session" facts are, per directory. With
    // several roots the last one scanned is not the most recent one, so every
    // overwrite has to earn it.
    let mut facts_ms: BTreeMap<String, i64> = BTreeMap::new();

    // 1. The registries: paths plus last-session facts.
    for env in envs {
        merge_registry(env, &mut by_key, &mut facts_ms)?;
    }

    // 2. The transcripts: the only evidence a directory was actually used.
    for env in envs {
        merge_transcripts(env, &mut by_key, &mut facts_ms);
    }

    let mut out: Vec<ProjectSummary> = by_key.into_values().collect();
    for item in &mut out {
        // Newest first, so `transcript_dir` (the single-folder answer) is the
        // one a caller that ignores the list would most want.
        item.transcript_dir = item.transcript_dirs.first().cloned();
    }
    out.sort_by(|a, b| {
        b.last_active_ms
            .cmp(&a.last_active_ms)
            .then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase()))
    });
    Ok(out)
}

fn merge_registry(
    env: &PathEnv,
    by_key: &mut BTreeMap<String, ProjectSummary>,
    facts_ms: &mut BTreeMap<String, i64>,
) -> Result<()> {
    let Ok(text) = std::fs::read_to_string(env.global_config_path()) else {
        return Ok(());
    };
    let cfg: Value = serde_json::from_str(&text)
        .map_err(|e| Error::Config(format!("parse global config: {e}")))?;
    let Some(projects) = cfg.get("projects").and_then(Value::as_object) else {
        return Ok(());
    };

    for (path, entry) in projects {
        let key = path_key(path);
        let item = by_key.entry(key.clone()).or_insert_with(|| ProjectSummary {
            path: path.clone(),
            name: display_name(path),
            ..Default::default()
        });
        item.registered = true;
        // Prefer whichever spelling of the path looks native.
        if path.contains('\\') && !item.path.contains('\\') {
            // keep the forward-slash form already stored
        } else if item.path.contains('\\') && !path.contains('\\') {
            item.path.clone_from(path);
            item.name = display_name(path);
        }

        let registry_time =
            i64_field(entry, "lastSessionModified").max(i64_field(entry, "lastStartTime"));
        item.last_active_ms = item.last_active_ms.max(registry_time);

        // Only the freshest root gets to describe "the last session".
        let stamp = registry_time.unwrap_or(0);
        if stamp < *facts_ms.get(&key).unwrap_or(&i64::MIN) {
            continue;
        }
        facts_ms.insert(key, stamp);
        item.last_session_id = entry
            .get("lastSessionId")
            .and_then(Value::as_str)
            .filter(|s| !s.is_empty())
            .map(str::to_string);
        // The registry's own copy is captured before the injected
        // preamble is stripped, so it needs the same filter.
        item.last_prompt = entry
            .get("lastSessionFirstPrompt")
            .and_then(Value::as_str)
            .filter(|s| !is_synthetic_prompt(s))
            .map(trim_prompt);
        item.last_cost_usd = entry
            .get("lastCost")
            .and_then(Value::as_f64)
            .filter(|c| *c > 0.0);
        item.last_duration_ms = i64_field(entry, "lastDuration");
        item.last_lines_added = i64_field(entry, "lastLinesAdded");
        item.last_lines_removed = i64_field(entry, "lastLinesRemoved");
    }
    Ok(())
}

fn merge_transcripts(
    env: &PathEnv,
    by_key: &mut BTreeMap<String, ProjectSummary>,
    facts_ms: &mut BTreeMap<String, i64>,
) {
    for (key, t) in scan_transcript_dirs(&transcripts_root(env)) {
        let item = by_key.entry(key.clone()).or_insert_with(|| ProjectSummary {
            path: t.path.clone(),
            name: display_name(&t.path),
            ..Default::default()
        });
        // Sums, not assignments: this root is one of several.
        item.session_count = item.session_count.saturating_add(t.session_count);
        item.transcript_bytes = item.transcript_bytes.saturating_add(t.bytes);
        item.transcript_dirs.push(t.dir);

        // Last *activity* is the transcript's mtime. The timestamp inside the
        // file marks when the session started, which for a long conversation can
        // be days before the last thing said in it.
        let newest = if t.newest.modified_ms > 0 {
            Some(t.newest.modified_ms)
        } else {
            t.newest.started_ms
        };
        item.last_active_ms = item.last_active_ms.max(newest);

        let stamp = newest.unwrap_or(0);
        if stamp >= *facts_ms.get(&key).unwrap_or(&i64::MIN) {
            facts_ms.insert(key, stamp);
            if !t.newest.id.is_empty() {
                item.last_session_id = Some(t.newest.id.clone());
            }
            if t.newest.first_prompt.is_some() {
                item.last_prompt.clone_from(&t.newest.first_prompt);
            }
        }
    }

    // Newest folder first, so the primary `transcript_dir` names the live one.
    for item in by_key.values_mut() {
        if item.transcript_dirs.len() > 1 {
            item.transcript_dirs
                .sort_by_key(|d| std::cmp::Reverse(newest_mtime_in(Path::new(d))));
            item.transcript_dirs.dedup();
        }
    }
}

fn newest_mtime_in(dir: &Path) -> i64 {
    std::fs::read_dir(dir)
        .into_iter()
        .flatten()
        .flatten()
        .filter(|e| e.path().extension().is_some_and(|x| x == "jsonl"))
        .filter_map(|e| e.metadata().ok())
        .map(|m| mtime_ms(&m))
        .max()
        .unwrap_or(0)
}

/// Sessions of one directory, newest first.
///
/// `transcript_dir` is the folder from [`ProjectSummary::transcript_dir`].
pub fn list_sessions(transcript_dir: &Path) -> Result<Vec<SessionSummary>> {
    list_sessions_in(std::slice::from_ref(&transcript_dir.to_path_buf()))
}

/// [`list_sessions`] merged across every folder holding this directory's history.
///
/// A missing folder is skipped rather than fatal: profiles come and go, and a
/// stale path in the caller's list must not hide the conversations that remain.
pub fn list_sessions_in(transcript_dirs: &[PathBuf]) -> Result<Vec<SessionSummary>> {
    let mut files: Vec<PathBuf> = Vec::new();
    let mut any = false;
    for dir in transcript_dirs {
        let Ok(entries) = std::fs::read_dir(dir) else {
            continue;
        };
        any = true;
        files.extend(
            entries
                .flatten()
                .map(|e| e.path())
                .filter(|p| p.extension().is_some_and(|e| e == "jsonl")),
        );
    }
    if !any {
        // Every folder was unreadable — report it rather than an empty history.
        let first = transcript_dirs.first().cloned().unwrap_or_default();
        std::fs::read_dir(&first).map_err(Error::Io)?;
    }
    files.sort();

    let mut out: Vec<SessionSummary> = files.iter().map(|p| read_head(p)).collect();
    out.sort_by_key(|s| std::cmp::Reverse(s.started_ms.unwrap_or(s.modified_ms)));
    Ok(out)
}

/// Cumulative totals for one directory — reads every transcript byte.
///
/// Deliberately not called while listing: a directory here can hold tens of MB,
/// and most people never ask for the totals.
pub fn scan_stats(transcript_dir: &Path) -> Result<ProjectStats> {
    scan_stats_in(std::slice::from_ref(&transcript_dir.to_path_buf()))
}

/// [`scan_stats`] over every folder holding this directory's history.
pub fn scan_stats_in(transcript_dirs: &[PathBuf]) -> Result<ProjectStats> {
    let mut files: Vec<PathBuf> = Vec::new();
    let mut any = false;
    for dir in transcript_dirs {
        let Ok(entries) = std::fs::read_dir(dir) else {
            continue;
        };
        any = true;
        files.extend(
            entries
                .flatten()
                .map(|e| e.path())
                .filter(|p| p.extension().is_some_and(|e| e == "jsonl")),
        );
    }
    if !any {
        let first = transcript_dirs.first().cloned().unwrap_or_default();
        std::fs::read_dir(&first).map_err(Error::Io)?;
    }

    let mut stats = ProjectStats {
        sessions: u32::try_from(files.len()).unwrap_or(u32::MAX),
        ..Default::default()
    };

    let mut days: BTreeMap<String, DayStats> = BTreeMap::new();

    for file in &files {
        let Ok(f) = std::fs::File::open(file) else {
            continue;
        };
        let bytes = std::fs::metadata(file).map_or(0, |m| m.len());
        stats.scanned_bytes += bytes;

        let mut session = SessionStats {
            id: file
                .file_stem()
                .and_then(|s| s.to_str())
                .unwrap_or_default()
                .to_string(),
            bytes,
            ..Default::default()
        };

        for line in BufReader::new(f).lines() {
            let Ok(line) = line else {
                stats.skipped_lines += 1;
                continue;
            };
            if line.trim().is_empty() {
                continue;
            }
            let Ok(rec) = serde_json::from_str::<Value>(&line) else {
                stats.skipped_lines += 1;
                continue;
            };
            accumulate(&rec, &mut stats, &mut session, &mut days);
        }
        stats.sessions_detail.push(session);
    }

    stats
        .sessions_detail
        .sort_by_key(|s| std::cmp::Reverse(s.last_ms.unwrap_or(0)));
    stats.daily = fill_days(&days);
    Ok(stats)
}

/// Fold one transcript record into the running totals.
///
/// Split out of [`scan_stats`] so the file/line plumbing and the meaning of a
/// record stay separately readable — the loop is about iteration, this is about
/// what each record counts as.
fn accumulate(
    rec: &Value,
    stats: &mut ProjectStats,
    session: &mut SessionStats,
    days: &mut BTreeMap<String, DayStats>,
) {
    let stamp = rec
        .get("timestamp")
        .and_then(Value::as_str)
        .and_then(parse_iso_ms);
    if let Some(ms) = stamp {
        stats.first_ms = Some(stats.first_ms.map_or(ms, |f: i64| f.min(ms)));
        stats.last_ms = Some(stats.last_ms.map_or(ms, |l: i64| l.max(ms)));
        session.first_ms = Some(session.first_ms.map_or(ms, |f: i64| f.min(ms)));
        session.last_ms = Some(session.last_ms.map_or(ms, |l: i64| l.max(ms)));
    }
    let bucket = stamp.and_then(local_day);

    match rec.get("type").and_then(Value::as_str) {
        Some("user") => {
            // Injected preamble is machinery, not something the user asked;
            // counting it would inflate every session's first day.
            // `is_none_or` would read better but is newer than this crate's MSRV.
            let real = rec.get("isMeta").and_then(Value::as_bool) != Some(true)
                && !message_text(rec).is_some_and(|t| is_synthetic_prompt(&t));
            if real {
                stats.user_messages += 1;
                session.user_messages += 1;
                if let Some(ref d) = bucket {
                    days.entry(d.clone())
                        .or_insert_with(|| DayStats {
                            day: d.clone(),
                            ..Default::default()
                        })
                        .user_messages += 1;
                }
                if session.first_prompt.is_none() {
                    session.first_prompt = message_text(rec).map(|t| trim_prompt(&t));
                }
            }
        }
        Some("assistant") => stats.assistant_messages += 1,
        _ => {}
    }

    // Synthetic records carry an all-zero usage block; counting them would
    // inflate the message count without adding tokens.
    let Some(message) = rec.get("message") else {
        return;
    };
    if message.get("model").and_then(Value::as_str) == Some("<synthetic>") {
        return;
    }
    let Some(usage) = message.get("usage") else {
        return;
    };
    let take = |k: &str| usage.get(k).and_then(Value::as_u64).unwrap_or(0);
    let output = take("output_tokens");
    stats.input_tokens += take("input_tokens");
    stats.output_tokens += output;
    stats.cache_creation_tokens += take("cache_creation_input_tokens");
    stats.cache_read_tokens += take("cache_read_input_tokens");
    session.output_tokens += output;
    if let Some(d) = bucket {
        days.entry(d.clone())
            .or_insert_with(|| DayStats {
                day: d,
                ..Default::default()
            })
            .output_tokens += output;
    }
}

/// Everything across every directory — the "what have I done here" answer.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OverviewStats {
    /// Directories that actually hold transcripts (registered-only ones aren't work).
    pub projects: u32,
    pub sessions: u32,
    pub user_messages: u32,
    pub output_tokens: u64,
    pub cache_read_tokens: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub first_ms: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_ms: Option<i64>,
    /// Days with at least one question — not the same as the calendar span.
    pub active_days: u32,
    /// Longest run of consecutive active days.
    pub longest_streak: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub busiest_day: Option<DayStats>,
    /// Merged daily series, gaps filled — the calendar heatmap's source.
    pub daily: Vec<DayStats>,
    /// Per-directory totals, heaviest first.
    pub per_project: Vec<ProjectTotal>,
    pub scanned_bytes: u64,
    pub skipped_lines: u32,
}

/// One directory's share of the overview.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectTotal {
    pub path: String,
    pub name: String,
    pub sessions: u32,
    pub user_messages: u32,
    pub output_tokens: u64,
}

/// Scan every directory and merge the results.
///
/// This reads every transcript on the machine, so it is strictly an on-demand
/// action — the same reason [`scan_stats`] is behind a button, multiplied by the
/// number of directories.
pub fn scan_overview(env: &PathEnv) -> Result<OverviewStats> {
    scan_overview_in(std::slice::from_ref(env))
}

/// [`scan_overview`] across every config home on the machine.
pub fn scan_overview_in(envs: &[PathEnv]) -> Result<OverviewStats> {
    let mut out = OverviewStats::default();
    let mut merged: BTreeMap<String, DayStats> = BTreeMap::new();

    for project in list_projects_in(envs)? {
        if project.transcript_dirs.is_empty() {
            continue;
        }
        let dirs: Vec<PathBuf> = project.transcript_dirs.iter().map(PathBuf::from).collect();
        let Ok(stats) = scan_stats_in(&dirs) else {
            continue;
        };

        out.projects += 1;
        out.sessions += stats.sessions;
        out.user_messages += stats.user_messages;
        out.output_tokens += stats.output_tokens;
        out.cache_read_tokens += stats.cache_read_tokens;
        out.scanned_bytes += stats.scanned_bytes;
        out.skipped_lines += stats.skipped_lines;
        out.first_ms = min_opt(out.first_ms, stats.first_ms);
        out.last_ms = max_opt(out.last_ms, stats.last_ms);

        for day in &stats.daily {
            // Only real activity merges; the filler zeros are re-derived below
            // against the *combined* range, which is wider than any one project's.
            if day.user_messages == 0 && day.output_tokens == 0 {
                continue;
            }
            let slot = merged.entry(day.day.clone()).or_insert_with(|| DayStats {
                day: day.day.clone(),
                ..Default::default()
            });
            slot.user_messages += day.user_messages;
            slot.output_tokens += day.output_tokens;
        }

        out.per_project.push(ProjectTotal {
            path: project.path.clone(),
            name: project.name.clone(),
            sessions: stats.sessions,
            user_messages: stats.user_messages,
            output_tokens: stats.output_tokens,
        });
    }

    out.active_days = u32::try_from(merged.len()).unwrap_or(u32::MAX);
    out.longest_streak = longest_streak(&merged);
    out.busiest_day = merged.values().max_by_key(|d| d.user_messages).cloned();
    out.daily = fill_days(&merged);
    out.per_project
        .sort_by_key(|p| std::cmp::Reverse(p.output_tokens));
    Ok(out)
}

/// Fingerprint of every transcript on disk: file count, total bytes, newest mtime.
///
/// Only `stat` calls, so it costs a few milliseconds against the ~500 ms a full
/// scan takes. Any new message rewrites its transcript, which moves both the size
/// and the mtime, so a stale cache cannot survive a change.
#[must_use]
pub fn overview_key(env: &PathEnv) -> String {
    overview_key_in(std::slice::from_ref(env))
}

/// [`overview_key`] over every config home the overview covers.
///
/// The root count is part of the key: starting to use session mode adds
/// transcripts the previous key never saw, and a cache written before that must
/// not survive it.
#[must_use]
pub fn overview_key_in(envs: &[PathEnv]) -> String {
    let mut files = 0u64;
    let mut bytes = 0u64;
    let mut newest = 0i64;
    for env in envs {
        let root = transcripts_root(env);
        for dir in std::fs::read_dir(&root).into_iter().flatten().flatten() {
            for entry in std::fs::read_dir(dir.path())
                .into_iter()
                .flatten()
                .flatten()
            {
                // `is_none_or` reads better but postdates this crate's MSRV.
                if !entry.path().extension().is_some_and(|e| e == "jsonl") {
                    continue;
                }
                let Ok(meta) = entry.metadata() else { continue };
                files += 1;
                bytes += meta.len();
                newest = newest.max(mtime_ms(&meta));
            }
        }
    }
    format!("v2:{}:{files}:{bytes}:{newest}", envs.len())
}

/// Overview served from `<cache_dir>/overview.json` when the transcripts have
/// not changed since it was written.
///
/// The scan reads every transcript on the machine; doing that on each launch to
/// draw one strip would be rude to the disk. The key makes a stale answer
/// impossible, so the cache is safe to trust rather than merely fast.
pub fn scan_overview_cached(env: &PathEnv, cache_dir: &Path) -> Result<OverviewStats> {
    scan_overview_cached_in(std::slice::from_ref(env), cache_dir)
}

/// [`scan_overview_cached`] across every config home on the machine.
pub fn scan_overview_cached_in(envs: &[PathEnv], cache_dir: &Path) -> Result<OverviewStats> {
    let key = overview_key_in(envs);
    let path = cache_dir.join("overview.json");

    if let Ok(text) = std::fs::read_to_string(&path) {
        if let Ok(cached) = serde_json::from_str::<CachedOverview>(&text) {
            if cached.key == key {
                return Ok(cached.stats);
            }
        }
    }

    let stats = scan_overview_in(envs)?;
    // A cache that cannot be written is a performance loss, not a failure.
    if std::fs::create_dir_all(cache_dir).is_ok() {
        if let Ok(text) = serde_json::to_string(&CachedOverview {
            key,
            stats: stats.clone(),
        }) {
            let _ = crate::fsutil::atomic_write(&path, text.as_bytes());
        }
    }
    Ok(stats)
}

#[derive(Serialize, Deserialize)]
struct CachedOverview {
    key: String,
    stats: OverviewStats,
}

fn min_opt(a: Option<i64>, b: Option<i64>) -> Option<i64> {
    match (a, b) {
        (Some(x), Some(y)) => Some(x.min(y)),
        (x, y) => x.or(y),
    }
}

fn max_opt(a: Option<i64>, b: Option<i64>) -> Option<i64> {
    match (a, b) {
        (Some(x), Some(y)) => Some(x.max(y)),
        (x, y) => x.or(y),
    }
}

/// Longest run of consecutive calendar days present in `days`.
fn longest_streak(days: &BTreeMap<String, DayStats>) -> u32 {
    let mut best = 0u32;
    let mut run = 0u32;
    let mut previous: Option<chrono::NaiveDate> = None;
    for key in days.keys() {
        let Ok(date) = chrono::NaiveDate::parse_from_str(key, "%Y-%m-%d") else {
            continue;
        };
        run = match previous {
            Some(p) if (date - p).num_days() == 1 => run + 1,
            _ => 1,
        };
        best = best.max(run);
        previous = Some(date);
    }
    best
}

/// Local calendar date of an instant, as `YYYY-MM-DD`.
fn local_day(ms: i64) -> Option<String> {
    chrono::DateTime::from_timestamp_millis(ms).map(|utc| {
        chrono::DateTime::<chrono::Local>::from(utc)
            .format("%Y-%m-%d")
            .to_string()
    })
}

/// Expand recorded days into a contiguous run so a time axis has no false gaps.
///
/// Capped so a directory spanning years cannot return an unbounded series; past
/// the cap only the recorded days come back, and the shell aggregates.
fn fill_days(days: &BTreeMap<String, DayStats>) -> Vec<DayStats> {
    const MAX_DAYS: i64 = 400;
    let (Some(first), Some(last)) = (days.keys().next(), days.keys().next_back()) else {
        return Vec::new();
    };
    let parse = |s: &str| chrono::NaiveDate::parse_from_str(s, "%Y-%m-%d").ok();
    let (Some(start), Some(end)) = (parse(first), parse(last)) else {
        return days.values().cloned().collect();
    };
    if (end - start).num_days() > MAX_DAYS {
        return days.values().cloned().collect();
    }

    let mut out = Vec::new();
    let mut cursor = start;
    while cursor <= end {
        let key = cursor.format("%Y-%m-%d").to_string();
        out.push(days.get(&key).cloned().unwrap_or(DayStats {
            day: key,
            ..Default::default()
        }));
        let Some(next) = cursor.succ_opt() else { break };
        cursor = next;
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::Platform;

    fn env_at(home: &Path) -> PathEnv {
        PathEnv {
            home: home.to_path_buf(),
            claude_config_dir: None,
            xdg_data_home: None,
            platform: Platform::Windows,
        }
    }

    /// Write a transcript with the given records under `<home>/.claude/projects/<dir>`.
    fn write_transcript(home: &Path, dir: &str, id: &str, lines: &[Value]) -> PathBuf {
        let d = home.join(".claude").join("projects").join(dir);
        std::fs::create_dir_all(&d).unwrap();
        let file = d.join(format!("{id}.jsonl"));
        let body: String = lines
            .iter()
            .map(|v| format!("{v}\n"))
            .collect::<Vec<_>>()
            .concat();
        std::fs::write(&file, body).unwrap();
        file
    }

    fn write_config(home: &Path, projects: &Value) {
        std::fs::create_dir_all(home.join(".claude")).unwrap();
        std::fs::write(
            home.join(".claude.json"),
            serde_json::json!({ "projects": projects }).to_string(),
        )
        .unwrap();
    }

    #[test]
    fn both_path_spellings_are_one_project() {
        // Claude Code records the same directory both ways; counting it twice
        // makes "how many directories" wrong by however many are duplicated.
        assert_eq!(path_key("D:/dev/x"), path_key(r"D:\dev\x"));
        assert_eq!(path_key("D:/dev/x/"), path_key("D:/dev/X"));
        assert_ne!(path_key("D:/dev/x"), path_key("D:/dev/y"));
    }

    #[test]
    fn display_name_is_the_last_segment() {
        assert_eq!(display_name(r"D:\dev\claude_switch"), "claude_switch");
        assert_eq!(display_name("D:/dev/auto-site/"), "auto-site");
        assert_eq!(display_name("plain"), "plain");
    }

    #[test]
    fn duplicate_registry_paths_collapse_to_one_project() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        write_config(
            home,
            &serde_json::json!({
                "D:/dev/x": { "lastSessionModified": 1_700_000_000_000i64 },
                "D:\\dev\\x": { "lastSessionModified": 1_700_000_001_000i64 },
                "D:/dev/y": { "lastSessionModified": 1_600_000_000_000i64 },
            }),
        );

        let list = list_projects(&env_at(home)).unwrap();
        assert_eq!(list.len(), 2, "{list:#?}");
        let x = list.iter().find(|p| p.name == "x").unwrap();
        // The newer of the two spellings wins the timestamp.
        assert_eq!(x.last_active_ms, Some(1_700_000_001_000));
        assert!(x.registered);
        assert_eq!(x.session_count, 0, "registered but never used");
    }

    #[test]
    fn transcripts_supply_sessions_and_the_real_cwd() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        write_config(home, &serde_json::json!({}));
        // Folder name is lossy; the cwd inside the file is authoritative.
        write_transcript(
            home,
            "D--dev-my-proj",
            "sess-1",
            &[
                serde_json::json!({ "type": "mode", "sessionId": "sess-1" }),
                serde_json::json!({
                    "type": "user",
                    "cwd": r"D:\dev\my_proj",
                    "timestamp": "2026-08-01T10:00:00.000Z",
                    "message": { "content": "  帮我  修一下   构建  " }
                }),
            ],
        );

        let list = list_projects(&env_at(home)).unwrap();
        assert_eq!(list.len(), 1);
        let p = &list[0];
        assert_eq!(p.path, r"D:\dev\my_proj", "cwd wins over the folder name");
        assert_eq!(p.name, "my_proj");
        assert_eq!(p.session_count, 1);
        assert!(!p.registered, "transcripts alone do not register a project");
        assert_eq!(p.last_session_id.as_deref(), Some("sess-1"));
        // Whitespace collapsed for a one-line title.
        assert_eq!(p.last_prompt.as_deref(), Some("帮我 修一下 构建"));
        assert!(p.transcript_bytes > 0);
    }

    #[test]
    fn registry_and_transcripts_merge_into_one_row() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        write_config(
            home,
            &serde_json::json!({
                r"D:\dev\my_proj": {
                    "lastSessionModified": 1_000i64,
                    "lastCost": 0.42,
                    "lastLinesAdded": 10,
                }
            }),
        );
        write_transcript(
            home,
            "D--dev-my-proj",
            "sess-1",
            &[serde_json::json!({
                "type": "user",
                "cwd": "D:/dev/my_proj",
                "timestamp": "2026-08-01T10:00:00.000Z",
                "message": { "content": "hi" }
            })],
        );

        let list = list_projects(&env_at(home)).unwrap();
        assert_eq!(list.len(), 1, "same directory, two spellings, two sources");
        let p = &list[0];
        assert!(p.registered && p.session_count == 1);
        assert_eq!(p.last_cost_usd, Some(0.42));
        assert_eq!(p.last_lines_added, Some(10));
        // The transcript is the newer evidence, so it wins over the registry's
        // stale figure. Its mtime is "now" here, hence a bound rather than an
        // equality.
        assert!(p.last_active_ms.unwrap() > 1_000, "{:?}", p.last_active_ms);
        assert!(p.last_active_ms >= parse_iso_ms("2026-08-01T10:00:00.000Z"));
    }

    #[test]
    fn session_titles_skip_the_injected_preamble() {
        // A real transcript opens with hook output and slash-command plumbing;
        // taking the literal first user record titles every session
        // "<local-command-caveat>Caveat: The messages…".
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        write_transcript(
            home,
            "proj",
            "s1",
            &[
                serde_json::json!({
                    "type": "user", "isMeta": true, "cwd": "/p",
                    "timestamp": "2026-01-01T00:00:00.000Z",
                    "message": { "content": "<local-command-caveat>Caveat: The messages below…" }
                }),
                serde_json::json!({
                    "type": "user", "cwd": "/p",
                    "message": { "content": "<command-name>/model</command-name>" }
                }),
                serde_json::json!({
                    "type": "user", "cwd": "/p",
                    "message": { "content": "<local-command-stdout>Set model to \u{1b}[1mOpus 5\u{1b}[22m" }
                }),
                // A subagent's turn is not this session's opening prompt.
                serde_json::json!({
                    "type": "user", "isSidechain": true, "cwd": "/p",
                    "message": { "content": "subagent chatter" }
                }),
                serde_json::json!({
                    "type": "user", "cwd": "/p",
                    "message": { "content": "帮我修一下构建" }
                }),
            ],
        );

        let dir = home.join(".claude").join("projects").join("proj");
        let sessions = list_sessions(&dir).unwrap();
        assert_eq!(sessions[0].first_prompt.as_deref(), Some("帮我修一下构建"));
    }

    #[test]
    fn control_characters_never_reach_a_title() {
        // Transcripts capture terminal output verbatim, escapes included.
        assert_eq!(trim_prompt("a\u{1b}[1mb\tc\nd"), "a [1mb c d");
        assert!(is_synthetic_prompt("   "));
        assert!(is_synthetic_prompt("<system-reminder>x"));
        assert!(!is_synthetic_prompt("正常的提问"));
        // A prompt that merely mentions a tag is still a prompt.
        assert!(!is_synthetic_prompt("怎么用 <command-name> 这个标签？"));
    }

    #[test]
    fn sessions_list_newest_first_with_titles() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        write_transcript(
            home,
            "proj",
            "old",
            &[serde_json::json!({
                "type": "user", "cwd": "/p", "timestamp": "2026-01-01T00:00:00.000Z",
                "message": { "content": "first" }
            })],
        );
        write_transcript(
            home,
            "proj",
            "new",
            &[serde_json::json!({
                "type": "user", "cwd": "/p", "timestamp": "2026-02-01T00:00:00.000Z",
                "message": { "content": [{ "type": "text", "text": "second" }] }
            })],
        );

        let dir = home.join(".claude").join("projects").join("proj");
        let sessions = list_sessions(&dir).unwrap();
        assert_eq!(sessions.len(), 2);
        assert_eq!(sessions[0].id, "new");
        assert_eq!(sessions[0].first_prompt.as_deref(), Some("second"));
        assert_eq!(sessions[1].id, "old");
        assert!(sessions[0].bytes > 0 && sessions[0].modified_ms > 0);
    }

    #[test]
    fn stats_sum_tokens_and_ignore_synthetic_records() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        write_transcript(
            home,
            "proj",
            "s1",
            &[
                serde_json::json!({
                    "type": "user", "timestamp": "2026-01-01T00:00:00.000Z",
                    "message": { "content": "q" }
                }),
                serde_json::json!({
                    "type": "assistant", "timestamp": "2026-01-01T00:01:00.000Z",
                    "message": {
                        "model": "claude-opus-5",
                        "usage": {
                            "input_tokens": 100, "output_tokens": 20,
                            "cache_creation_input_tokens": 5, "cache_read_input_tokens": 7
                        }
                    }
                }),
                // Synthetic: all-zero usage that must not be mistaken for a turn.
                serde_json::json!({
                    "type": "assistant", "timestamp": "2026-01-01T00:02:00.000Z",
                    "message": {
                        "model": "<synthetic>",
                        "usage": { "input_tokens": 999, "output_tokens": 999 }
                    }
                }),
            ],
        );
        // A genuinely malformed line — a truncated write, which a transcript
        // being appended to while we read it will produce.
        let dir = home.join(".claude").join("projects").join("proj");
        std::fs::write(
            dir.join("s1.jsonl"),
            format!(
                "{}{{\"type\":\"assistant\",\"message\":{{\"usa\n",
                std::fs::read_to_string(dir.join("s1.jsonl")).unwrap()
            ),
        )
        .unwrap();
        let s = scan_stats(&dir).unwrap();
        assert_eq!(s.sessions, 1);
        assert_eq!(s.user_messages, 1);
        assert_eq!(s.assistant_messages, 2);
        assert_eq!(s.input_tokens, 100, "synthetic tokens excluded");
        assert_eq!(s.output_tokens, 20);
        assert_eq!(s.cache_creation_tokens, 5);
        assert_eq!(s.cache_read_tokens, 7);
        assert_eq!(
            s.skipped_lines, 1,
            "unparseable lines are reported, not hidden"
        );
        assert_eq!(s.first_ms, parse_iso_ms("2026-01-01T00:00:00.000Z"));
        assert_eq!(s.last_ms, parse_iso_ms("2026-01-01T00:02:00.000Z"));
        assert!(s.scanned_bytes > 0);
    }

    #[test]
    fn stats_break_down_per_session_and_per_day() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let day1 = "2026-01-01T10:00:00.000Z";
        let day3 = "2026-01-03T10:00:00.000Z";
        write_transcript(
            home,
            "proj",
            "s1",
            &[
                serde_json::json!({ "type": "user", "timestamp": day1,
                    "message": { "content": "第一天" } }),
                serde_json::json!({ "type": "assistant", "timestamp": day1,
                    "message": { "model": "m", "usage": { "output_tokens": 100 } } }),
            ],
        );
        write_transcript(
            home,
            "proj",
            "s2",
            &[
                // Injected preamble must not count as a question.
                serde_json::json!({ "type": "user", "isMeta": true, "timestamp": day3,
                    "message": { "content": "<local-command-caveat>x" } }),
                serde_json::json!({ "type": "user", "timestamp": day3,
                    "message": { "content": "第三天" } }),
                serde_json::json!({ "type": "assistant", "timestamp": day3,
                    "message": { "model": "m", "usage": { "output_tokens": 50 } } }),
            ],
        );

        let dir = home.join(".claude").join("projects").join("proj");
        let s = scan_stats(&dir).unwrap();

        assert_eq!(s.user_messages, 2, "the meta record is not a question");
        assert_eq!(s.sessions_detail.len(), 2);
        let by_id = |id: &str| s.sessions_detail.iter().find(|x| x.id == id).unwrap();
        assert_eq!(by_id("s1").output_tokens, 100);
        assert_eq!(by_id("s1").user_messages, 1);
        assert_eq!(by_id("s2").output_tokens, 50);
        assert_eq!(by_id("s2").first_prompt.as_deref(), Some("第三天"));
        // Newest first.
        assert_eq!(s.sessions_detail[0].id, "s2");

        // The idle 2nd is present with zeros — a chart that skipped it would
        // put the two active days side by side and hide the gap.
        assert_eq!(s.daily.len(), 3, "{:?}", s.daily);
        assert_eq!(s.daily[1].user_messages, 0);
        assert_eq!(s.daily[1].output_tokens, 0);
        assert_eq!(s.daily[0].user_messages + s.daily[2].user_messages, 2);
        assert_eq!(s.daily.iter().map(|d| d.output_tokens).sum::<u64>(), 150);
    }

    #[test]
    fn overview_merges_projects_and_measures_the_streak() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let msg = |ts: &str, text: &str| {
            serde_json::json!({
                "type": "user", "cwd": "/a", "timestamp": ts, "message": { "content": text }
            })
        };
        let out = |ts: &str, n: u64| {
            serde_json::json!({
                "type": "assistant", "timestamp": ts,
                "message": { "model": "m", "usage": { "output_tokens": n } }
            })
        };

        // Project A: 1st and 2nd (consecutive).
        write_transcript(
            home,
            "a",
            "a1",
            &[
                msg("2026-03-01T10:00:00Z", "q"),
                out("2026-03-01T10:01:00Z", 10),
                msg("2026-03-02T10:00:00Z", "q"),
                out("2026-03-02T10:01:00Z", 20),
            ],
        );
        // Project B: 3rd (extends the run to three) and 9th (a separate day).
        let b = home.join(".claude").join("projects").join("b");
        std::fs::create_dir_all(&b).unwrap();
        std::fs::write(
            b.join("b1.jsonl"),
            format!(
                "{}\n{}\n{}\n{}\n",
                serde_json::json!({ "type": "user", "cwd": "/b",
                    "timestamp": "2026-03-03T10:00:00Z", "message": { "content": "q" } }),
                out("2026-03-03T10:01:00Z", 100),
                serde_json::json!({ "type": "user", "cwd": "/b",
                    "timestamp": "2026-03-09T10:00:00Z", "message": { "content": "q" } }),
                out("2026-03-09T10:01:00Z", 5),
            ),
        )
        .unwrap();
        write_config(home, &serde_json::json!({}));

        let o = scan_overview(&env_at(home)).unwrap();
        assert_eq!(o.projects, 2);
        assert_eq!(o.sessions, 2);
        assert_eq!(o.user_messages, 4);
        assert_eq!(o.output_tokens, 135);
        assert_eq!(o.active_days, 4, "one day per question, none doubled");
        assert_eq!(
            o.longest_streak, 3,
            "1st–3rd, broken by the gap before the 9th"
        );
        assert_eq!(o.busiest_day.as_ref().unwrap().user_messages, 1);

        // Filled across the *combined* range, so the idle 4th–8th are present.
        assert_eq!(o.daily.len(), 9, "{:?}", o.daily);
        assert_eq!(o.daily.iter().filter(|d| d.user_messages > 0).count(), 4);

        // Heaviest project first.
        assert_eq!(o.per_project.len(), 2);
        assert_eq!(o.per_project[0].output_tokens, 105);
    }

    #[test]
    fn overview_cache_is_reused_but_never_stale() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let cache = home.join("cache");
        write_config(home, &serde_json::json!({}));
        write_transcript(
            home,
            "a",
            "a1",
            &[serde_json::json!({
                "type": "user", "cwd": "/a", "timestamp": "2026-03-01T10:00:00Z",
                "message": { "content": "q" }
            })],
        );

        let env = env_at(home);
        let first = scan_overview_cached(&env, &cache).unwrap();
        assert_eq!(first.user_messages, 1);
        assert!(cache.join("overview.json").exists());

        // Corrupt the stored stats but keep the key: a hit must serve them, which
        // is how we know the second call did not rescan.
        let text = std::fs::read_to_string(cache.join("overview.json")).unwrap();
        let mut node: Value = serde_json::from_str(&text).unwrap();
        node["stats"]["userMessages"] = serde_json::json!(999);
        std::fs::write(cache.join("overview.json"), node.to_string()).unwrap();
        assert_eq!(
            scan_overview_cached(&env, &cache).unwrap().user_messages,
            999
        );

        // A new message changes size and mtime, so the key no longer matches and
        // the doctored cache must be discarded rather than served.
        write_transcript(
            home,
            "a",
            "a2",
            &[serde_json::json!({
                "type": "user", "cwd": "/a", "timestamp": "2026-03-02T10:00:00Z",
                "message": { "content": "q2" }
            })],
        );
        let fresh = scan_overview_cached(&env, &cache).unwrap();
        assert_eq!(
            fresh.user_messages, 2,
            "stale cache must not survive a change"
        );
    }

    #[test]
    fn missing_sources_are_empty_not_errors() {
        let tmp = tempfile::tempdir().unwrap();
        // No config, no transcripts — a fresh machine, not a failure.
        assert!(list_projects(&env_at(tmp.path())).unwrap().is_empty());
        assert!(list_sessions(&tmp.path().join("nope")).is_err());
    }
}
