//! Deleting conversation history to reclaim disk space.
//!
//! Claude Code already ages transcripts out on its own (`cleanupPeriodDays`, 30
//! days unless set), so what this module adds is what that sweep cannot do:
//! removing a *recent* conversation that happens to be huge, reaching session
//! profiles the sweep never visits because nobody launches them any more, and
//! collecting leftovers whose transcript is already gone.
//!
//! Every deletion is a [`plan`] first. The plan is what the user is shown, and
//! [`apply`] derives it again rather than trusting paths handed back by the
//! shell: a caller only ever names a session by its transcript folder and id,
//! and both are checked against the known config homes before anything is
//! touched.
//!
//! What one session owns, under its config home:
//!
//! - `projects/<folder>/<id>.jsonl` — the transcript
//! - `projects/<folder>/<id>/` — subagent transcripts and spilled tool output
//! - `file-history/<id>/`, `session-env/<id>`, `image-cache/<id>/`,
//!   `uploads/<id>/`, `tasks/<id>/` and `debug/<id>.txt`
//!
//! What it does not own, and is never deleted here: `projects/<folder>/memory/`
//! (auto memory belongs to the directory, not to a conversation), the project's
//! entry in `.claude.json` (trust and MCP servers), and `history.jsonl` (prompt
//! recall). Removing those is `claude project purge`, which the shell runs
//! separately after [`purge_preflight`].

use std::collections::{BTreeMap, HashSet};
use std::io;
use std::path::{Component, Path, PathBuf};

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

use crate::agentruns::AgentRun;
use crate::errors::{Error, Result};
use crate::fsutil::atomic_write;
use crate::paths::PathEnv;
use crate::projects::{self, path_key};
use crate::session;

/// Claude Code's own retention when `cleanupPeriodDays` is unset.
pub const DEFAULT_RETENTION_DAYS: u32 = 30;

/// Largest `cleanupPeriodDays` accepted here — ten years.
pub const MAX_RETENTION_DAYS: u32 = 3650;

/// A transcript written this recently is treated as in use.
///
/// The pid files are the primary evidence, but a host that does not write one
/// still appends to its transcript between turns, and deleting a file that is
/// about to be appended to just makes Claude Code recreate a headless fragment.
pub const RECENT_WRITE_GUARD_MS: i64 = 120_000;

const DAY_MS: i64 = 86_400_000;
const MIB: u64 = 1_048_576;
const RETENTION_KEY: &str = "cleanupPeriodDays";

/// Per-session entries under a config home, named by the session id.
const SESSION_STORES: &[&str] = &[
    "file-history",
    "session-env",
    "image-cache",
    "uploads",
    "tasks",
];

/// Per-session files under a config home, as `(directory, suffix)`.
const SESSION_FILE_STORES: &[(&str, &str)] = &[("debug", ".txt")];

/// Stores scanned for leftovers.
///
/// `tasks/` is left out on purpose: a task list can be shared by name across
/// sessions, so the absence of one transcript does not make it an orphan.
const ORPHAN_STORES: &[&str] = &["file-history", "session-env", "image-cache", "uploads"];

// --- roots -----------------------------------------------------------------------

/// One Claude config home whose history lives on this machine.
#[derive(Clone, Debug)]
pub struct HistoryRoot {
    pub env: PathEnv,
    /// Slot number when this home is a session-mode profile.
    pub profile_number: Option<u32>,
    /// A profile no managed account owns any more.
    pub account_removed: bool,
}

impl HistoryRoot {
    #[must_use]
    pub fn home(&self) -> PathBuf {
        self.env.claude_config_home()
    }
}

/// Every config home, the default one first.
///
/// `managed_profiles` are the profile directories current accounts own. `None`
/// means that could not be determined, and then no profile is called removed —
/// guessing there would offer a live account's history for deletion.
#[must_use]
pub fn history_roots(
    env: &PathEnv,
    backup_root: &Path,
    managed_profiles: Option<&[PathBuf]>,
) -> Vec<HistoryRoot> {
    let mut default = env.clone();
    default.claude_config_dir = None;
    let mut out = vec![HistoryRoot {
        env: default,
        profile_number: None,
        account_removed: false,
    }];

    let managed: Option<HashSet<String>> = managed_profiles.map(|dirs| {
        dirs.iter()
            .map(|d| path_key(&d.to_string_lossy()))
            .collect()
    });
    for profile in session::list_profiles(backup_root) {
        let key = path_key(&profile.dir.to_string_lossy());
        out.push(HistoryRoot {
            env: session::profile_env(env, &profile.dir),
            profile_number: Some(profile.number),
            account_removed: managed.as_ref().is_some_and(|m| !m.contains(&key)),
        });
    }
    out
}

// --- requests and plans ------------------------------------------------------------

/// One session, as the shell names it.
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionRef {
    pub transcript_dir: String,
    pub id: String,
}

/// Conditions that select sessions across every config home.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CleanupRules {
    /// No activity for more than this many days.
    pub older_than_days: Option<u32>,
    /// Session footprint at least this many MiB.
    pub larger_than_mb: Option<u64>,
    /// The recorded working directory no longer exists.
    pub missing_directory: bool,
    /// History left in a profile whose account is gone.
    pub removed_accounts: bool,
    /// Per-session data whose transcript is already gone.
    pub orphans: bool,
}

/// What to delete. The parts are unioned.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CleanupRequest {
    pub sessions: Vec<SessionRef>,
    /// Whole transcript folders: every session in them, never `memory/`.
    pub transcript_dirs: Vec<String>,
    pub rules: Option<CleanupRules>,
    /// Sessions the shell knows are live but Claude Code has no pid file for.
    pub protect_ids: Vec<String>,
    /// Apply only: restrict deletion to the ids the user was shown.
    pub only_ids: Option<Vec<String>>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum ItemKind {
    /// A transcript and everything that belongs to it.
    Session,
    /// Per-session data with no transcript left.
    Orphan,
}

/// Why an item is in a plan.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum Reason {
    Selected,
    OlderThan,
    LargerThan,
    MissingDirectory,
    RemovedAccount,
    Orphan,
}

impl Reason {
    const fn key(self) -> &'static str {
        match self {
            Self::Selected => "selected",
            Self::OlderThan => "olderThan",
            Self::LargerThan => "largerThan",
            Self::MissingDirectory => "missingDirectory",
            Self::RemovedAccount => "removedAccount",
            Self::Orphan => "orphan",
        }
    }
}

/// Why an item will be left alone.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum Blocker {
    /// A running Claude Code or supervised run owns it.
    Live,
    /// Written within [`RECENT_WRITE_GUARD_MS`].
    Recent,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CleanupItem {
    pub id: String,
    pub kind: ItemKind,
    pub config_home: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub profile_number: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub transcript_dir: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cwd: Option<String>,
    /// Last write, epoch ms.
    pub modified_ms: i64,
    pub bytes: u64,
    /// Transcript first for a session, so a failure to delete it stops the rest.
    pub paths: Vec<String>,
    pub reasons: Vec<Reason>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub blocked: Option<Blocker>,
    /// Supervised-run records that would point at nothing afterwards.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub run_ids: Vec<String>,
}

#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Tally {
    pub count: u32,
    pub bytes: u64,
}

impl Tally {
    fn add(&mut self, bytes: u64) {
        self.count = self.count.saturating_add(1);
        self.bytes = self.bytes.saturating_add(bytes);
    }
}

#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CleanupPlan {
    /// Largest first.
    pub items: Vec<CleanupItem>,
    pub deletable: Tally,
    pub blocked: Tally,
    /// Run records removed along with the deletable items.
    pub run_records: u32,
    /// Deletable items per reason; an item with two reasons counts in both.
    pub by_reason: BTreeMap<String, Tally>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CleanupFailure {
    pub path: String,
    pub error: String,
}

#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CleanupOutcome {
    pub deleted_count: u32,
    pub freed_bytes: u64,
    pub skipped_count: u32,
    pub failures: Vec<CleanupFailure>,
    /// Journal records the caller should drop — their sessions are gone.
    #[serde(skip)]
    pub removed_run_ids: Vec<String>,
    /// How many of those the caller dropped.
    pub removed_runs: u32,
}

/// Everything a plan is evaluated against.
pub struct PlanContext<'a> {
    pub roots: &'a [HistoryRoot],
    /// Supervised-run journal, already reaped.
    pub runs: &'a [AgentRun],
    pub now_ms: i64,
}

// --- planning ------------------------------------------------------------------------

/// What `req` would delete, and what it would have to leave alone.
///
/// # Errors
///
/// [`Error::Validation`] when a session id or transcript folder is not one this
/// machine's config homes could contain.
pub fn plan(ctx: &PlanContext<'_>, req: &CleanupRequest) -> Result<CleanupPlan> {
    let mut found = Found::default();

    for s in &req.sessions {
        if !is_safe_id(&s.id) {
            return Err(Error::Validation(format!("invalid session id: {}", s.id)));
        }
        let (ri, folder) = resolve_folder(ctx.roots, &s.transcript_dir)?;
        if let Some(item) = session_item(&ctx.roots[ri], &folder, &s.id) {
            found.add(item, Reason::Selected);
        }
    }

    for dir in &req.transcript_dirs {
        let (ri, folder) = resolve_folder(ctx.roots, dir)?;
        let root = &ctx.roots[ri];
        let known = root_transcript_ids(root);
        for t in transcripts_in(&folder) {
            if let Some(item) = session_item(root, &folder, &t.id) {
                found.add(item, Reason::Selected);
            }
        }
        for item in folder_orphans(root, &folder, &known) {
            found.add(item, Reason::Selected);
        }
    }

    if let Some(rules) = &req.rules {
        for root in ctx.roots {
            select_by_rules(ctx, root, rules, &mut found);
        }
    }

    Ok(finish(ctx, found, &req.protect_ids))
}

/// Delete what [`plan`] selects, skipping anything blocked.
///
/// Per-path failures are collected rather than fatal: one locked file must not
/// leave the rest of a large cleanup undone.
///
/// # Errors
///
/// Whatever [`plan`] refuses.
pub fn apply(ctx: &PlanContext<'_>, req: &CleanupRequest) -> Result<CleanupOutcome> {
    let plan = plan(ctx, req)?;
    let only: Option<HashSet<&str>> = req
        .only_ids
        .as_ref()
        .map(|ids| ids.iter().map(String::as_str).collect());

    let mut out = CleanupOutcome::default();
    for item in &plan.items {
        if only
            .as_ref()
            .is_some_and(|set| !set.contains(item.id.as_str()))
        {
            continue;
        }
        if item.blocked.is_some() {
            out.skipped_count = out.skipped_count.saturating_add(1);
            continue;
        }
        if delete_item(item, &mut out) {
            out.deleted_count = out.deleted_count.saturating_add(1);
            out.removed_run_ids.extend(item.run_ids.iter().cloned());
        }
    }
    Ok(out)
}

/// Delete one item's paths; `true` when nothing was left behind.
fn delete_item(item: &CleanupItem, out: &mut CleanupOutcome) -> bool {
    let mut clean = true;
    for (i, p) in item.paths.iter().enumerate() {
        let path = Path::new(p);
        let bytes = tree_bytes(path);
        if let Err(e) = delete_path(path) {
            out.failures.push(CleanupFailure {
                path: p.clone(),
                error: e.to_string(),
            });
            clean = false;
            // A transcript that cannot be removed is most likely held open by
            // a Claude Code this check could not see. Stripping its checkpoints
            // out from under that session is worse than leaving it whole.
            if i == 0 && item.kind == ItemKind::Session {
                return false;
            }
        } else {
            out.freed_bytes = out.freed_bytes.saturating_add(bytes);
        }
    }
    clean
}

#[derive(Default)]
struct Found {
    items: BTreeMap<String, CleanupItem>,
}

impl Found {
    fn add(&mut self, item: CleanupItem, reason: Reason) {
        let key = format!(
            "{}|{:?}|{}",
            path_key(&item.config_home),
            item.kind,
            item.id
        );
        let slot = self.items.entry(key).or_insert(item);
        if !slot.reasons.contains(&reason) {
            slot.reasons.push(reason);
            slot.reasons.sort();
        }
    }

    /// Fold another item for the same id into an existing one — used for
    /// leftovers, which can be spread over several stores.
    fn merge(&mut self, item: CleanupItem, reason: Reason) {
        let key = format!(
            "{}|{:?}|{}",
            path_key(&item.config_home),
            item.kind,
            item.id
        );
        if let Some(slot) = self.items.get_mut(&key) {
            for p in item.paths {
                if !slot.paths.contains(&p) {
                    slot.paths.push(p);
                }
            }
            slot.bytes = slot.bytes.saturating_add(item.bytes);
            slot.modified_ms = slot.modified_ms.max(item.modified_ms);
            slot.transcript_dir = slot.transcript_dir.take().or(item.transcript_dir);
            if !slot.reasons.contains(&reason) {
                slot.reasons.push(reason);
                slot.reasons.sort();
            }
        } else {
            self.add(item, reason);
        }
    }
}

fn select_by_rules(
    ctx: &PlanContext<'_>,
    root: &HistoryRoot,
    rules: &CleanupRules,
    found: &mut Found,
) {
    let known = root_transcript_ids(root);
    for folder in subdirectories(&projects::transcripts_root(&root.env)) {
        let transcripts = transcripts_in(&folder);
        let cwd = if rules.missing_directory {
            folder_cwd(&transcripts)
        } else {
            None
        };
        let gone = cwd.as_deref().is_some_and(directory_is_gone);

        for t in &transcripts {
            let Some(mut item) = session_item(root, &folder, &t.id) else {
                continue;
            };
            let mut reasons = Vec::new();
            if rules.removed_accounts && root.account_removed {
                reasons.push(Reason::RemovedAccount);
            }
            if gone {
                reasons.push(Reason::MissingDirectory);
            }
            if let Some(days) = rules.older_than_days.filter(|d| *d > 0) {
                if ctx.now_ms.saturating_sub(item.modified_ms) > i64::from(days) * DAY_MS {
                    reasons.push(Reason::OlderThan);
                }
            }
            if let Some(mb) = rules.larger_than_mb.filter(|m| *m > 0) {
                if item.bytes >= mb.saturating_mul(MIB) {
                    reasons.push(Reason::LargerThan);
                }
            }
            if reasons.is_empty() {
                continue;
            }
            item.cwd.clone_from(&cwd);
            for reason in reasons {
                found.add(item.clone(), reason);
            }
        }

        if rules.orphans {
            for item in folder_orphans(root, &folder, &known) {
                found.merge(item, Reason::Orphan);
            }
        }
    }

    if rules.orphans {
        for item in store_orphans(root, &known) {
            found.merge(item, Reason::Orphan);
        }
    }
}

/// Mark what must be left alone, attach run references, and total it up.
fn finish(ctx: &PlanContext<'_>, found: Found, protect_ids: &[String]) -> CleanupPlan {
    let mut live: HashSet<String> = protect_ids.iter().cloned().collect();
    for root in ctx.roots {
        live.extend(
            session::live_sessions_for(&root.home())
                .into_iter()
                .map(|s| s.session_id)
                .filter(|id| !id.is_empty()),
        );
    }

    let mut plan = CleanupPlan::default();
    for mut item in found.items.into_values() {
        let mut running = false;
        for run in ctx
            .runs
            .iter()
            .filter(|r| r.session_id.as_deref() == Some(item.id.as_str()))
        {
            if run.status.is_live() {
                running = true;
            } else {
                item.run_ids.push(run.id.clone());
            }
        }

        item.blocked = if running || live.contains(&item.id) {
            Some(Blocker::Live)
        } else if ctx.now_ms.saturating_sub(item.modified_ms) < RECENT_WRITE_GUARD_MS {
            Some(Blocker::Recent)
        } else {
            None
        };

        if item.blocked.is_some() {
            plan.blocked.add(item.bytes);
        } else {
            plan.deletable.add(item.bytes);
            plan.run_records = plan
                .run_records
                .saturating_add(u32::try_from(item.run_ids.len()).unwrap_or(u32::MAX));
            for reason in &item.reasons {
                plan.by_reason
                    .entry(reason.key().to_string())
                    .or_default()
                    .add(item.bytes);
            }
        }
        plan.items.push(item);
    }
    plan.items
        .sort_by(|a, b| b.bytes.cmp(&a.bytes).then_with(|| a.id.cmp(&b.id)));
    plan
}

// --- what belongs to a session -------------------------------------------------------------

/// Everything on disk that belongs to one session, existing entries only.
///
/// The transcript comes first; see [`CleanupItem::paths`].
#[must_use]
pub fn session_paths(home: Option<&Path>, folder: &Path, id: &str) -> Vec<PathBuf> {
    let mut candidates = vec![folder.join(format!("{id}.jsonl"))];
    // `memory/` is the directory's auto memory, whatever a transcript is named.
    if !id.eq_ignore_ascii_case("memory") {
        candidates.push(folder.join(id));
    }
    if let Some(home) = home {
        candidates.extend(SESSION_STORES.iter().map(|s| home.join(s).join(id)));
        candidates.extend(
            SESSION_FILE_STORES
                .iter()
                .map(|(dir, suffix)| home.join(dir).join(format!("{id}{suffix}"))),
        );
    }
    candidates
        .into_iter()
        .filter(|p| p.symlink_metadata().is_ok())
        .collect()
}

/// Disk used by one transcript and what belongs to it.
#[must_use]
pub fn session_bytes(transcript: &Path) -> u64 {
    let (Some(folder), Some(id)) = (
        transcript.parent(),
        transcript.file_stem().and_then(|s| s.to_str()),
    ) else {
        return tree_bytes(transcript);
    };
    session_paths(home_of(folder), folder, id)
        .iter()
        .map(|p| tree_bytes(p))
        .sum()
}

/// Disk used by a transcript folder's history: its sessions, what belongs to
/// them, and leftover sidecar folders.
///
/// Auto memory is not counted. It is not history, and nothing here deletes it,
/// so including it would promise space a cleanup cannot free.
#[must_use]
pub fn folder_bytes(folder: &Path) -> u64 {
    let home = home_of(folder);
    let transcripts = transcripts_in(folder);
    let ids: HashSet<&str> = transcripts.iter().map(|t| t.id.as_str()).collect();
    let sessions: u64 = transcripts
        .iter()
        .flat_map(|t| session_paths(home, folder, &t.id))
        .map(|p| tree_bytes(&p))
        .sum();
    let leftovers: u64 = subdirectories(folder)
        .iter()
        .filter(|d| {
            d.file_name()
                .and_then(|n| n.to_str())
                .is_some_and(|n| is_uuid(n) && !ids.contains(n))
        })
        .map(|d| tree_bytes(d))
        .sum();
    sessions.saturating_add(leftovers)
}

fn session_item(root: &HistoryRoot, folder: &Path, id: &str) -> Option<CleanupItem> {
    let transcript = folder.join(format!("{id}.jsonl"));
    let meta = std::fs::metadata(&transcript).ok()?;
    let home = root.home();
    let paths = session_paths(Some(&home), folder, id);
    Some(CleanupItem {
        id: id.to_string(),
        kind: ItemKind::Session,
        config_home: home.to_string_lossy().to_string(),
        profile_number: root.profile_number,
        transcript_dir: Some(folder.to_string_lossy().to_string()),
        cwd: None,
        // The transcript's own write time: a session's sidecar data is written
        // alongside its messages, never on its own.
        modified_ms: projects::mtime_ms(&meta),
        bytes: paths.iter().map(|p| tree_bytes(p)).sum(),
        paths: paths
            .iter()
            .map(|p| p.to_string_lossy().to_string())
            .collect(),
        reasons: Vec::new(),
        blocked: None,
        run_ids: Vec::new(),
    })
}

fn orphan_item(
    root: &HistoryRoot,
    id: &str,
    path: &Path,
    transcript_dir: Option<&Path>,
) -> CleanupItem {
    CleanupItem {
        id: id.to_string(),
        kind: ItemKind::Orphan,
        config_home: root.home().to_string_lossy().to_string(),
        profile_number: root.profile_number,
        transcript_dir: transcript_dir.map(|d| d.to_string_lossy().to_string()),
        cwd: None,
        modified_ms: path
            .symlink_metadata()
            .map_or(0, |m| projects::mtime_ms(&m)),
        bytes: tree_bytes(path),
        paths: vec![path.to_string_lossy().to_string()],
        reasons: Vec::new(),
        blocked: None,
        run_ids: Vec::new(),
    }
}

/// Session-named folders beside the transcripts that have no transcript left.
fn folder_orphans(root: &HistoryRoot, folder: &Path, known: &HashSet<String>) -> Vec<CleanupItem> {
    subdirectories(folder)
        .into_iter()
        .filter_map(|dir| {
            let name = dir.file_name()?.to_str()?.to_string();
            (is_uuid(&name) && !known.contains(&name))
                .then(|| orphan_item(root, &name, &dir, Some(folder)))
        })
        .collect()
}

/// Per-session store entries whose session has no transcript in this home.
fn store_orphans(root: &HistoryRoot, known: &HashSet<String>) -> Vec<CleanupItem> {
    let home = root.home();
    let mut out = Vec::new();
    for store in ORPHAN_STORES {
        for entry in std::fs::read_dir(home.join(store))
            .into_iter()
            .flatten()
            .flatten()
        {
            let Some(name) = entry.file_name().to_str().map(str::to_string) else {
                continue;
            };
            if is_uuid(&name) && !known.contains(&name) {
                out.push(orphan_item(root, &name, &entry.path(), None));
            }
        }
    }
    for (store, suffix) in SESSION_FILE_STORES {
        for entry in std::fs::read_dir(home.join(store))
            .into_iter()
            .flatten()
            .flatten()
        {
            let name = entry.file_name();
            let Some(id) = name.to_str().and_then(|n| n.strip_suffix(suffix)) else {
                continue;
            };
            if is_uuid(id) && !known.contains(id) {
                out.push(orphan_item(root, id, &entry.path(), None));
            }
        }
    }
    out
}

struct Transcript {
    id: String,
    path: PathBuf,
}

/// Transcripts in one folder, newest first.
fn transcripts_in(folder: &Path) -> Vec<Transcript> {
    let mut files: Vec<(i64, Transcript)> = std::fs::read_dir(folder)
        .into_iter()
        .flatten()
        .flatten()
        .filter_map(|e| {
            let path = e.path();
            if !path.extension().is_some_and(|x| x == "jsonl") {
                return None;
            }
            let id = path.file_stem()?.to_str()?.to_string();
            if !is_safe_id(&id) {
                return None;
            }
            let modified = e.metadata().map_or(0, |m| projects::mtime_ms(&m));
            Some((modified, Transcript { id, path }))
        })
        .collect();
    files.sort_by(|a, b| b.0.cmp(&a.0));
    files.into_iter().map(|(_, t)| t).collect()
}

/// Every session id with a transcript anywhere in this home.
fn root_transcript_ids(root: &HistoryRoot) -> HashSet<String> {
    subdirectories(&projects::transcripts_root(&root.env))
        .iter()
        .flat_map(|f| transcripts_in(f))
        .map(|t| t.id)
        .collect()
}

/// The working directory a folder's transcripts record, newest that names one.
fn folder_cwd(transcripts: &[Transcript]) -> Option<String> {
    transcripts
        .iter()
        .find_map(|t| projects::read_head(&t.path).cwd)
}

fn subdirectories(dir: &Path) -> Vec<PathBuf> {
    std::fs::read_dir(dir)
        .into_iter()
        .flatten()
        .flatten()
        .filter(|e| e.file_type().is_ok_and(|t| t.is_dir()))
        .map(|e| e.path())
        .collect()
}

/// `<home>` for `<home>/projects/<folder>`.
fn home_of(folder: &Path) -> Option<&Path> {
    folder
        .parent()
        .filter(|p| {
            p.file_name()
                .is_some_and(|n| n.eq_ignore_ascii_case("projects"))
        })
        .and_then(Path::parent)
}

/// The config home a transcript folder belongs to.
///
/// Compared by [`path_key`] rather than canonicalised: canonical Windows paths
/// come back as `\\?\` verbatim paths that never equal the configured ones.
fn resolve_folder(roots: &[HistoryRoot], folder: &str) -> Result<(usize, PathBuf)> {
    let refuse = || Error::Validation(format!("not a transcript folder on this machine: {folder}"));
    let path = PathBuf::from(folder);
    if path
        .components()
        .any(|c| matches!(c, Component::ParentDir | Component::CurDir))
        || path.file_name().is_none()
    {
        return Err(refuse());
    }
    let home = home_of(&path).ok_or_else(refuse)?;
    let key = path_key(&home.to_string_lossy());
    roots
        .iter()
        .position(|r| path_key(&r.home().to_string_lossy()) == key)
        .map(|i| (i, path.clone()))
        .ok_or_else(refuse)
}

/// Whether a recorded working directory is known to no longer exist.
///
/// A path on a volume that is not there right now — an unplugged disk, a
/// disconnected share — is unreachable, not gone, so it never counts.
pub(crate) fn directory_is_gone(cwd: &str) -> bool {
    let path = Path::new(cwd);
    if !path.is_absolute() || path.exists() {
        return false;
    }
    let mut anchor = PathBuf::new();
    for c in path.components() {
        match c {
            Component::Prefix(_) | Component::RootDir => anchor.push(c.as_os_str()),
            _ => break,
        }
    }
    !anchor.as_os_str().is_empty() && anchor.exists()
}

/// A transcript file stem safe to join onto a directory.
fn is_safe_id(id: &str) -> bool {
    !id.is_empty()
        && id.len() <= 128
        && id
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
}

/// Claude Code's session ids. Leftovers are only ever recognised by this shape,
/// so nothing a user named themselves can be mistaken for one.
fn is_uuid(name: &str) -> bool {
    let b = name.as_bytes();
    b.len() == 36
        && b.iter().enumerate().all(|(i, c)| match i {
            8 | 13 | 18 | 23 => *c == b'-',
            _ => c.is_ascii_hexdigit(),
        })
}

/// Bytes under a path, not following links — a junction inside a session folder
/// must not make it look as large as whatever it points at.
fn tree_bytes(path: &Path) -> u64 {
    let Ok(meta) = path.symlink_metadata() else {
        return 0;
    };
    if meta.file_type().is_symlink() {
        return 0;
    }
    if !meta.is_dir() {
        return meta.len();
    }
    std::fs::read_dir(path)
        .into_iter()
        .flatten()
        .flatten()
        .map(|e| tree_bytes(&e.path()))
        .sum()
}

/// Remove a file or directory tree. A link is removed itself, never its target.
fn delete_path(path: &Path) -> io::Result<()> {
    let meta = match path.symlink_metadata() {
        Ok(m) => m,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(e),
    };
    let result = if meta.file_type().is_symlink() {
        std::fs::remove_dir(path).or_else(|_| std::fs::remove_file(path))
    } else if meta.is_dir() {
        std::fs::remove_dir_all(path)
    } else {
        std::fs::remove_file(path)
    };
    match result {
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
        other => other,
    }
}

// --- retention -------------------------------------------------------------------------

/// `cleanupPeriodDays` from a settings file; `None` when unset or the file is absent.
///
/// # Errors
///
/// [`Error::Config`] when the file exists but is not a JSON object.
pub fn read_retention(settings: &Path) -> Result<Option<u32>> {
    let text = match std::fs::read_to_string(settings) {
        Ok(t) => t,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(Error::Io(e)),
    };
    let map = parse_settings(&text)?;
    Ok(map
        .get(RETENTION_KEY)
        .and_then(Value::as_u64)
        .and_then(|d| u32::try_from(d).ok()))
}

/// Set `cleanupPeriodDays`, or remove it with `None` to return to the default.
///
/// The file is hand-edited, so only that one value changes: the rest keeps its
/// order, spacing and anything else the user put there. A file that cannot be
/// parsed is refused rather than replaced.
///
/// # Errors
///
/// [`Error::Validation`] for a value outside 1..=[`MAX_RETENTION_DAYS`],
/// [`Error::Config`] for an unreadable file, [`Error::Io`] when writing fails.
pub fn write_retention(settings: &Path, days: Option<u32>) -> Result<()> {
    if let Some(d) = days {
        if !(1..=MAX_RETENTION_DAYS).contains(&d) {
            return Err(Error::Validation(format!(
                "retention must be between 1 and {MAX_RETENTION_DAYS} days"
            )));
        }
    }
    let original = match std::fs::read_to_string(settings) {
        Ok(t) => t,
        Err(e) if e.kind() == io::ErrorKind::NotFound => String::new(),
        Err(e) => return Err(Error::Io(e)),
    };
    let current = parse_settings(&original)?;
    let mut expected = current.clone();
    match days {
        Some(d) => {
            expected.insert(RETENTION_KEY.into(), Value::from(d));
        }
        None => {
            expected.remove(RETENTION_KEY);
        }
    }
    if expected == current {
        return Ok(());
    }

    let value = days.map(|d| d.to_string());
    let text = edit_top_level(&original, RETENTION_KEY, value.as_deref())
        .filter(|t| parse_settings(t).is_ok_and(|m| m == expected))
        .unwrap_or_else(|| {
            let body = serde_json::to_string_pretty(&Value::Object(expected))
                .unwrap_or_else(|_| "{}".into());
            format!("{body}\n")
        });

    if let Some(parent) = settings.parent() {
        std::fs::create_dir_all(parent).map_err(Error::Io)?;
    }
    atomic_write(settings, text.as_bytes()).map_err(Error::Io)
}

fn parse_settings(text: &str) -> Result<Map<String, Value>> {
    let body = text.trim_start_matches('\u{feff}');
    if body.trim().is_empty() {
        return Ok(Map::new());
    }
    match serde_json::from_str::<Value>(body) {
        Ok(Value::Object(map)) => Ok(map),
        Ok(_) => Err(Error::Config("settings.json is not a JSON object".into())),
        Err(e) => Err(Error::Config(format!(
            "settings.json is not valid JSON: {e}"
        ))),
    }
}

struct Entry {
    key: String,
    key_start: usize,
    value_start: usize,
    value_end: usize,
}

struct ObjectScan {
    open: usize,
    close: usize,
    entries: Vec<Entry>,
}

/// Set or remove one top-level key, keeping every other byte of the text.
///
/// Returns `None` when the text is not shaped the way this scanner understands;
/// the caller then rewrites the file whole, after checking the result parses to
/// what it should.
fn edit_top_level(text: &str, key: &str, value: Option<&str>) -> Option<String> {
    let parsed = top_level_entries(text)?;
    let entries = &parsed.entries;

    if let Some(i) = entries.iter().position(|e| e.key == key) {
        let e = &entries[i];
        if let Some(v) = value {
            return Some(format!(
                "{}{v}{}",
                &text[..e.value_start],
                &text[e.value_end..]
            ));
        }
        // Take the pair with one neighbouring comma, so the object stays valid.
        let (start, end) = if let Some(next) = entries.get(i + 1) {
            (e.key_start, next.key_start)
        } else if i > 0 {
            (entries[i - 1].value_end, e.value_end)
        } else {
            (e.key_start, e.value_end)
        };
        return Some(format!("{}{}", &text[..start], &text[end..]));
    }

    let v = value?;
    let pair = format!("\"{key}\": {v}");
    Some(if let Some(last) = entries.last() {
        let indent = line_indent(text, last.key_start).unwrap_or("  ");
        format!(
            "{},\n{indent}{pair}{}",
            &text[..last.value_end],
            &text[last.value_end..]
        )
    } else {
        format!(
            "{}\n  {pair}\n{}",
            &text[..=parsed.open],
            &text[parsed.close..]
        )
    })
}

fn top_level_entries(text: &str) -> Option<ObjectScan> {
    let b = text.as_bytes();
    let start = if b.starts_with(&[0xEF, 0xBB, 0xBF]) {
        3
    } else {
        0
    };
    let mut i = skip_ws(b, start);
    if b.get(i) != Some(&b'{') {
        return None;
    }
    let open = i;
    let mut entries = Vec::new();
    i += 1;
    loop {
        i = skip_ws(b, i);
        match b.get(i)? {
            b'}' => {
                return Some(ObjectScan {
                    open,
                    close: i,
                    entries,
                })
            }
            b'"' => {}
            _ => return None,
        }
        let key_start = i;
        let key_end = string_end(b, i)?;
        let key = text[key_start + 1..key_end - 1].to_string();
        i = skip_ws(b, key_end);
        if b.get(i) != Some(&b':') {
            return None;
        }
        let value_start = skip_ws(b, i + 1);
        let value_end = value_end(b, value_start)?;
        entries.push(Entry {
            key,
            key_start,
            value_start,
            value_end,
        });
        i = skip_ws(b, value_end);
        match b.get(i)? {
            b',' => i += 1,
            b'}' => {}
            _ => return None,
        }
    }
}

fn skip_ws(b: &[u8], mut i: usize) -> usize {
    while b
        .get(i)
        .is_some_and(|c| matches!(c, b' ' | b'\t' | b'\n' | b'\r'))
    {
        i += 1;
    }
    i
}

/// Index just past the closing quote of the string starting at `i`.
fn string_end(b: &[u8], i: usize) -> Option<usize> {
    let mut j = i + 1;
    while j < b.len() {
        match b[j] {
            b'\\' => j += 2,
            b'"' => return Some(j + 1),
            _ => j += 1,
        }
    }
    None
}

/// Index just past the value starting at `start`, trailing whitespace excluded.
fn value_end(b: &[u8], start: usize) -> Option<usize> {
    let mut depth = 0usize;
    let mut i = start;
    while i < b.len() {
        match b[i] {
            b'"' => {
                i = string_end(b, i)?;
                if depth == 0 {
                    return Some(i);
                }
                continue;
            }
            b'{' | b'[' => depth += 1,
            b'}' | b']' => {
                if depth == 0 {
                    return Some(trim_back(b, start, i));
                }
                depth -= 1;
                if depth == 0 {
                    return Some(i + 1);
                }
            }
            b',' if depth == 0 => return Some(trim_back(b, start, i)),
            _ => {}
        }
        i += 1;
    }
    None
}

fn trim_back(b: &[u8], start: usize, mut end: usize) -> usize {
    while end > start && matches!(b[end - 1], b' ' | b'\t' | b'\n' | b'\r') {
        end -= 1;
    }
    end
}

/// The whitespace a line starts with, when nothing else precedes `pos` on it.
fn line_indent(text: &str, pos: usize) -> Option<&str> {
    let line_start = text[..pos].rfind('\n').map_or(0, |n| n + 1);
    let lead = &text[line_start..pos];
    lead.chars().all(|c| c == ' ' || c == '\t').then_some(lead)
}

// --- purge ------------------------------------------------------------------------------

/// A config home holding state for a directory `claude project purge` would remove.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PurgeRoot {
    /// `CLAUDE_CONFIG_DIR` to run the purge under; absent for the default home.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub config_dir: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub profile_number: Option<u32>,
    pub transcript_folders: u32,
    pub sessions: u32,
    /// History footprint, auto memory excluded.
    pub bytes: u64,
    /// The directory has an entry in this home's `.claude.json`.
    pub registered: bool,
}

#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PurgePreflight {
    pub roots: Vec<PurgeRoot>,
    /// Claude Code instances working in the directory right now.
    pub live_sessions: u32,
    /// Supervised runs still going there.
    pub running_runs: u32,
    /// Finished run records for the directory, to drop once it is purged.
    pub run_ids: Vec<String>,
}

/// Where a directory has state, and whether anything is using it.
#[must_use]
pub fn purge_preflight(
    roots: &[HistoryRoot],
    runs: &[AgentRun],
    directory: &str,
) -> PurgePreflight {
    let key = path_key(directory);
    let nested = format!("{key}/");
    let mut out = PurgePreflight::default();

    for root in roots {
        let mut folders = 0u32;
        let mut sessions = 0u32;
        let mut bytes = 0u64;
        for folder in subdirectories(&projects::transcripts_root(&root.env)) {
            let transcripts = transcripts_in(&folder);
            if folder_cwd(&transcripts).is_some_and(|c| path_key(&c) == key) {
                folders += 1;
                sessions =
                    sessions.saturating_add(u32::try_from(transcripts.len()).unwrap_or(u32::MAX));
                bytes = bytes.saturating_add(folder_bytes(&folder));
            }
        }
        let registered = registry_has(&root.env, &key);
        if folders > 0 || registered {
            out.roots.push(PurgeRoot {
                config_dir: root
                    .profile_number
                    .map(|_| root.home().to_string_lossy().to_string()),
                profile_number: root.profile_number,
                transcript_folders: folders,
                sessions,
                bytes,
                registered,
            });
        }

        let live = session::live_sessions_for(&root.home())
            .into_iter()
            .filter(|s| {
                let k = path_key(&s.cwd);
                k == key || k.starts_with(&nested)
            })
            .count();
        out.live_sessions = out
            .live_sessions
            .saturating_add(u32::try_from(live).unwrap_or(u32::MAX));
    }

    for run in runs.iter().filter(|r| path_key(&r.cwd) == key) {
        if run.status.is_live() {
            out.running_runs = out.running_runs.saturating_add(1);
        } else {
            out.run_ids.push(run.id.clone());
        }
    }
    out
}

fn registry_has(env: &PathEnv, key: &str) -> bool {
    std::fs::read_to_string(env.global_config_path())
        .ok()
        .and_then(|t| serde_json::from_str::<Value>(&t).ok())
        .and_then(|cfg| {
            cfg.get("projects")
                .and_then(Value::as_object)
                .map(|p| p.keys().any(|k| path_key(k) == key))
        })
        .unwrap_or(false)
}

// --- registered-only directories ---------------------------------------------------

/// How many config backups are kept per home.
const REGISTRY_BACKUPS_KEPT: usize = 10;

/// A directory's entry in one home's `.claude.json` with no conversation behind it.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RegistryEntry {
    /// The key as Claude Code wrote it — the spelling that is removed.
    pub path: String,
    pub config_home: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub profile_number: Option<u32>,
    /// The folder was trusted; Claude Code asks again after removal.
    pub trusted: bool,
    /// Carries project-scoped MCP servers.
    pub has_mcp: bool,
    /// Claude Code is running in this directory right now.
    pub live: bool,
}

#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RegistryPlan {
    pub entries: Vec<RegistryEntry>,
    /// One path per removable directory, however many spellings it is
    /// registered under — the list shows those as one row.
    pub removable_paths: Vec<String>,
    pub live_directories: u32,
    /// Removable directories whose entries carry MCP servers.
    pub with_mcp: u32,
    pub backup_dir: String,
}

#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RegistryOutcome {
    pub removed_entries: u32,
    pub removed_directories: u32,
    /// Requested but no longer removable: history or a session appeared.
    pub skipped_directories: u32,
    pub backups: Vec<String>,
    pub failures: Vec<String>,
}

/// Which directories have conversations anywhere on this machine.
struct HistoryIndex {
    /// [`path_key`]s of the directories transcripts record.
    directories: HashSet<String>,
    /// Transcript folder names, lower-cased.
    folders: HashSet<String>,
}

impl HistoryIndex {
    fn build(roots: &[HistoryRoot]) -> Self {
        let mut index = Self {
            directories: HashSet::new(),
            folders: HashSet::new(),
        };
        for root in roots {
            for folder in subdirectories(&projects::transcripts_root(&root.env)) {
                let transcripts = transcripts_in(&folder);
                if transcripts.is_empty() {
                    continue;
                }
                if let Some(name) = folder.file_name().and_then(|n| n.to_str()) {
                    index.folders.insert(name.to_lowercase());
                }
                if let Some(cwd) = folder_cwd(&transcripts) {
                    index.directories.insert(path_key(&cwd));
                }
            }
        }
        index
    }

    /// Whether a registered directory has history.
    ///
    /// Checked by folder name as well as by recorded directory: a transcript
    /// that never names its directory within the part that is read is still
    /// that directory's history. A path too long to encode cannot be ruled out,
    /// so it counts as having history.
    fn covers(&self, path: &str) -> bool {
        self.directories.contains(&path_key(path))
            || projects::project_slug(path)
                .map_or(true, |s| self.folders.contains(&s.to_lowercase()))
    }
}

fn read_registry(env: &PathEnv) -> Option<Map<String, Value>> {
    let text = std::fs::read_to_string(env.global_config_path()).ok()?;
    let cfg: Value = serde_json::from_str(text.trim_start_matches('\u{feff}')).ok()?;
    cfg.get("projects").and_then(Value::as_object).cloned()
}

/// Directories Claude Code has registered but holds no conversation for — the
/// rows that list as having no sessions.
///
/// `only` restricts the plan to the named directories. A directory Claude Code
/// is running in, in any home, is reported but never removable: a session that
/// has just started has registered its directory before it has written a line.
#[must_use]
pub fn registry_plan(
    roots: &[HistoryRoot],
    only: Option<&[String]>,
    backup_dir: &Path,
) -> RegistryPlan {
    let history = HistoryIndex::build(roots);
    let wanted: Option<HashSet<String>> = only.map(|p| p.iter().map(|s| path_key(s)).collect());
    let live: HashSet<String> = roots
        .iter()
        .flat_map(|r| session::live_sessions_for(&r.home()))
        .map(|s| path_key(&s.cwd))
        .collect();

    let mut plan = RegistryPlan {
        backup_dir: backup_dir.to_string_lossy().to_string(),
        ..Default::default()
    };
    // key → (first spelling seen, carries MCP servers)
    let mut removable: BTreeMap<String, (String, bool)> = BTreeMap::new();
    let mut live_directories: HashSet<String> = HashSet::new();

    for root in roots {
        let Some(registry) = read_registry(&root.env) else {
            continue;
        };
        for (path, entry) in &registry {
            let key = path_key(path);
            if wanted.as_ref().is_some_and(|w| !w.contains(&key)) || history.covers(path) {
                continue;
            }
            let has_mcp = entry
                .get("mcpServers")
                .and_then(Value::as_object)
                .is_some_and(|m| !m.is_empty());
            let is_live = live.contains(&key);
            plan.entries.push(RegistryEntry {
                path: path.clone(),
                config_home: root.home().to_string_lossy().to_string(),
                profile_number: root.profile_number,
                trusted: entry.get("hasTrustDialogAccepted").and_then(Value::as_bool) == Some(true),
                has_mcp,
                live: is_live,
            });
            if is_live {
                live_directories.insert(key);
            } else {
                removable
                    .entry(key)
                    .or_insert_with(|| (path.clone(), false))
                    .1 |= has_mcp;
            }
        }
    }

    plan.live_directories = u32::try_from(live_directories.len()).unwrap_or(u32::MAX);
    plan.with_mcp =
        u32::try_from(removable.values().filter(|(_, mcp)| *mcp).count()).unwrap_or(u32::MAX);
    plan.removable_paths = removable.into_values().map(|(path, _)| path).collect();
    plan
}

/// Remove the registry entries of the named directories that still qualify.
///
/// Each home is changed under Claude Code's own config lock and re-read inside
/// it, then backed up and edited in place: only the removed entries' text goes,
/// so the rest of a file Claude Code rewrites constantly stays exactly as it was.
/// A directory that gained history or a running session since the plan was shown
/// is skipped.
#[must_use]
pub fn registry_remove(
    roots: &[HistoryRoot],
    paths: &[String],
    backup_dir: &Path,
    lock_timeout: std::time::Duration,
) -> RegistryOutcome {
    let plan = registry_plan(roots, Some(paths), backup_dir);
    let removable: HashSet<String> = plan.removable_paths.iter().map(|p| path_key(p)).collect();
    let requested: HashSet<String> = paths.iter().map(|p| path_key(p)).collect();

    let mut out = RegistryOutcome {
        skipped_directories: u32::try_from(requested.difference(&removable).count())
            .unwrap_or(u32::MAX),
        ..Default::default()
    };
    let mut removed_directories: HashSet<String> = HashSet::new();

    for root in roots {
        let home = root.home().to_string_lossy().to_string();
        let touches = plan
            .entries
            .iter()
            .any(|e| e.config_home == home && removable.contains(&path_key(&e.path)));
        if !touches {
            continue;
        }
        match remove_from_home(root, &removable, backup_dir, lock_timeout) {
            Ok(Some((removed, backup))) => {
                out.removed_entries = out
                    .removed_entries
                    .saturating_add(u32::try_from(removed.len()).unwrap_or(u32::MAX));
                removed_directories.extend(removed.iter().map(|p| path_key(p)));
                out.backups.push(backup.to_string_lossy().to_string());
            }
            Ok(None) => {}
            Err(e) => out
                .failures
                .push(format!("{}: {e}", root.env.global_config_path().display())),
        }
    }
    out.removed_directories = u32::try_from(removed_directories.len()).unwrap_or(u32::MAX);
    out
}

/// Remove matching entries from one home's config; the removed keys and the backup.
fn remove_from_home(
    root: &HistoryRoot,
    removable: &HashSet<String>,
    backup_dir: &Path,
    lock_timeout: std::time::Duration,
) -> Result<Option<(Vec<String>, PathBuf)>> {
    let path = root.env.global_config_path();
    let _lock = crate::locks::ClaudeCodeLocks::acquire_config(&root.env, lock_timeout)?;
    let original = std::fs::read_to_string(&path).map_err(Error::Io)?;
    let Some((edited, removed)) =
        remove_project_entries(&original, |key| removable.contains(&path_key(key)))?
    else {
        return Ok(None);
    };
    let backup = write_registry_backup(backup_dir, root, &original)?;
    atomic_write(&path, edited.as_bytes()).map_err(Error::Io)?;
    Ok(Some((removed, backup)))
}

/// Drop entries from the `projects` object of a `.claude.json`, keeping every
/// other byte of the file.
///
/// `Ok(None)` when nothing matched. The edit is refused, not replaced by a
/// reformatted rewrite, when the text is not shaped the way the scanner
/// understands or does not parse back to exactly the original minus the removed
/// entries: this is Claude Code's file, and it is rewritten while we look at it.
fn remove_project_entries(
    text: &str,
    remove: impl Fn(&str) -> bool,
) -> Result<Option<(String, Vec<String>)>> {
    let refuse =
        |why: &str| Error::Config(format!(".claude.json could not be edited safely: {why}"));
    let outer = top_level_entries(text).ok_or_else(|| refuse("not a JSON object"))?;
    let Some(field) = outer.entries.iter().find(|e| e.key == "projects") else {
        return Ok(None);
    };
    let inner_text = &text[field.value_start..field.value_end];
    let inner = top_level_entries(inner_text).ok_or_else(|| refuse("projects is not an object"))?;

    let mut kept: Vec<&Entry> = Vec::new();
    let mut removed: Vec<String> = Vec::new();
    for entry in &inner.entries {
        // The scanner keeps keys as written; the real key may contain escapes.
        let quoted = &inner_text[entry.key_start..entry.key_start + entry.key.len() + 2];
        let key: String = serde_json::from_str(quoted).map_err(|_| refuse("unreadable key"))?;
        if remove(&key) {
            removed.push(key);
        } else {
            kept.push(entry);
        }
    }
    if removed.is_empty() {
        return Ok(None);
    }

    let (Some(first), Some(last)) = (inner.entries.first(), inner.entries.last()) else {
        return Ok(None);
    };
    let rebuilt = if kept.is_empty() {
        "{}".to_string()
    } else {
        let lead = &inner_text[inner.open + 1..first.key_start];
        let tail = &inner_text[last.value_end..inner.close];
        let body: Vec<&str> = kept
            .iter()
            .map(|e| &inner_text[e.key_start..e.value_end])
            .collect();
        format!("{{{lead}{}{tail}}}", body.join(&format!(",{lead}")))
    };
    let edited = format!(
        "{}{rebuilt}{}",
        &text[..field.value_start],
        &text[field.value_end..]
    );

    let parse =
        |t: &str| serde_json::from_str::<Map<String, Value>>(t.trim_start_matches('\u{feff}'));
    let mut expected = parse(text).map_err(|_| refuse("not valid JSON"))?;
    if let Some(Value::Object(projects)) = expected.get_mut("projects") {
        for key in &removed {
            projects.remove(key);
        }
    }
    let actual = parse(&edited).map_err(|_| refuse("the edit did not parse"))?;
    if actual != expected {
        return Err(refuse("the edit changed more than the removed entries"));
    }
    Ok(Some((edited, removed)))
}

/// Copy a config file aside before changing it, keeping the newest few per home.
fn write_registry_backup(backup_dir: &Path, root: &HistoryRoot, text: &str) -> Result<PathBuf> {
    std::fs::create_dir_all(backup_dir).map_err(Error::Io)?;
    let label = root
        .profile_number
        .map_or_else(|| "default".to_string(), |n| format!("profile-{n}"));
    let prefix = format!("claude.json.{label}.");
    let stamp = chrono::Local::now().format("%Y%m%d-%H%M%S%3f");
    let path = backup_dir.join(format!("{prefix}{stamp}.bak"));
    atomic_write(&path, text.as_bytes()).map_err(Error::Io)?;

    let mut existing: Vec<PathBuf> = std::fs::read_dir(backup_dir)
        .into_iter()
        .flatten()
        .flatten()
        .map(|e| e.path())
        .filter(|p| {
            p.file_name()
                .and_then(|n| n.to_str())
                .is_some_and(|n| n.starts_with(&prefix))
        })
        .collect();
    existing.sort();
    let excess = existing.len().saturating_sub(REGISTRY_BACKUPS_KEPT);
    for old in existing.into_iter().take(excess) {
        let _ = std::fs::remove_file(old);
    }
    Ok(path)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::Platform;
    use serde_json::json;

    const S1: &str = "11111111-1111-4111-8111-111111111111";
    const S2: &str = "22222222-2222-4222-8222-222222222222";
    const S3: &str = "33333333-3333-4333-8333-333333333333";

    /// Ten days after every file written here, so nothing counts as recent.
    fn later() -> i64 {
        crate::agentruns::now_ms() + 10 * DAY_MS
    }

    fn default_root(home: &Path) -> HistoryRoot {
        HistoryRoot {
            env: PathEnv {
                home: home.to_path_buf(),
                claude_config_dir: None,
                xdg_data_home: None,
                platform: Platform::Windows,
            },
            profile_number: None,
            account_removed: false,
        }
    }

    /// `<home>/.claude/projects/<folder>/<id>.jsonl`, recording `cwd`, padded.
    fn transcript(home: &Path, folder: &str, id: &str, cwd: &str, padding: usize) -> PathBuf {
        let dir = home.join(".claude").join("projects").join(folder);
        std::fs::create_dir_all(&dir).unwrap();
        let file = dir.join(format!("{id}.jsonl"));
        let line = json!({"type": "user", "cwd": cwd, "message": {"content": "hi"}});
        std::fs::write(&file, format!("{line}\n{}", "x".repeat(padding))).unwrap();
        file
    }

    fn write(path: &Path, bytes: usize) {
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(path, vec![b'x'; bytes]).unwrap();
    }

    fn ctx(roots: &[HistoryRoot], now_ms: i64) -> PlanContext<'_> {
        PlanContext {
            roots,
            runs: &[],
            now_ms,
        }
    }

    fn folder_of(file: &Path) -> String {
        file.parent().unwrap().to_string_lossy().to_string()
    }

    #[test]
    fn deleting_a_session_takes_what_it_owns_and_keeps_memory() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let claude = home.join(".claude");
        let file = transcript(home, "D--work", S1, "D:\\work", 100);
        let folder = file.parent().unwrap().to_path_buf();
        write(&folder.join(S1).join("tool-results").join("a.txt"), 50);
        write(&claude.join("file-history").join(S1).join("snap"), 70);
        write(&claude.join("session-env").join(S1).join("env"), 5);
        write(&folder.join("memory").join("MEMORY.md"), 9);
        transcript(home, "D--work", S2, "D:\\work", 10);

        let roots = vec![default_root(home)];
        let req = CleanupRequest {
            sessions: vec![SessionRef {
                transcript_dir: folder_of(&file),
                id: S1.into(),
            }],
            ..Default::default()
        };
        let p = plan(&ctx(&roots, later()), &req).unwrap();
        assert_eq!(p.items.len(), 1);
        assert_eq!(p.deletable.count, 1);
        assert!(p.items[0].bytes >= 225, "{:?}", p.items[0]);
        assert!(
            Path::new(&p.items[0].paths[0])
                .extension()
                .is_some_and(|x| x == "jsonl"),
            "the transcript goes first"
        );

        let out = apply(&ctx(&roots, later()), &req).unwrap();
        assert_eq!(out.deleted_count, 1);
        assert!(out.failures.is_empty(), "{:?}", out.failures);
        assert!(!file.exists());
        assert!(!folder.join(S1).exists());
        assert!(!claude.join("file-history").join(S1).exists());
        assert!(!claude.join("session-env").join(S1).exists());
        assert!(
            folder.join("memory").join("MEMORY.md").exists(),
            "auto memory belongs to the directory"
        );
        assert!(folder.join(format!("{S2}.jsonl")).exists());
    }

    #[test]
    fn live_recent_and_protected_sessions_are_left_alone() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let live = transcript(home, "D--a", S1, "D:\\a", 10);
        let idle = transcript(home, "D--a", S2, "D:\\a", 10);
        // Claude Code's own pid file names S1 as running in this very process.
        let me = std::process::id();
        let pid_file = home
            .join(".claude")
            .join("sessions")
            .join(format!("{me}.json"));
        write(&pid_file, 0);
        std::fs::write(
            &pid_file,
            json!({"pid": me, "sessionId": S1, "cwd": "D:\\a",
                   "startedAt": crate::agentruns::now_ms()})
            .to_string(),
        )
        .unwrap();

        let roots = vec![default_root(home)];
        let req = CleanupRequest {
            transcript_dirs: vec![folder_of(&live)],
            ..Default::default()
        };

        let p = plan(&ctx(&roots, later()), &req).unwrap();
        let s1 = p.items.iter().find(|i| i.id == S1).unwrap();
        assert_eq!(s1.blocked, Some(Blocker::Live));
        assert_eq!(p.deletable.count, 1);

        // At the real time both transcripts were only just written.
        let now = plan(&ctx(&roots, crate::agentruns::now_ms()), &req).unwrap();
        assert_eq!(now.deletable.count, 0);
        assert!(now
            .items
            .iter()
            .any(|i| i.id == S2 && i.blocked == Some(Blocker::Recent)));

        // A run the shell is supervising has no pid file of its own.
        let protected = CleanupRequest {
            protect_ids: vec![S2.into()],
            ..req.clone()
        };
        assert_eq!(
            plan(&ctx(&roots, later()), &protected)
                .unwrap()
                .deletable
                .count,
            0
        );

        let out = apply(&ctx(&roots, later()), &req).unwrap();
        assert_eq!((out.deleted_count, out.skipped_count), (1, 1));
        assert!(live.exists());
        assert!(!idle.exists());
    }

    #[test]
    fn apply_deletes_only_what_was_shown() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let first = transcript(home, "D--a", S1, "D:\\a", 1);
        let second = transcript(home, "D--a", S2, "D:\\a", 1);
        let roots = vec![default_root(home)];
        let req = CleanupRequest {
            transcript_dirs: vec![folder_of(&first)],
            only_ids: Some(vec![S2.into()]),
            ..Default::default()
        };
        let out = apply(&ctx(&roots, later()), &req).unwrap();
        assert_eq!(out.deleted_count, 1);
        assert!(first.exists());
        assert!(!second.exists());
    }

    #[test]
    fn requests_outside_known_homes_are_refused() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let file = transcript(home, "D--a", S1, "D:\\a", 1);
        let folder = file.parent().unwrap();
        let roots = vec![default_root(home)];
        let elsewhere = home.join("other").join("projects").join("x");
        for (dir, id) in [
            (elsewhere.to_string_lossy().to_string(), S1),
            (
                folder.join("..").join("..").to_string_lossy().to_string(),
                S1,
            ),
            (folder_of(&file), "..\\evil"),
            (folder_of(&file), ""),
        ] {
            let req = CleanupRequest {
                sessions: vec![SessionRef {
                    transcript_dir: dir.clone(),
                    id: id.into(),
                }],
                ..Default::default()
            };
            assert!(
                matches!(plan(&ctx(&roots, later()), &req), Err(Error::Validation(_))),
                "{dir} / {id}"
            );
        }
        assert!(file.exists());
    }

    #[test]
    fn rules_select_by_age_size_missing_directory_and_leftovers() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let claude = home.join(".claude");
        let present = home.join("present");
        std::fs::create_dir_all(&present).unwrap();
        let gone = home.join("gone");

        transcript(home, "present", S1, &present.to_string_lossy(), 10);
        transcript(home, "big", S2, &present.to_string_lossy(), 3 * 1_048_576);
        transcript(home, "gone", S3, &gone.to_string_lossy(), 10);
        let orphan = "44444444-4444-4444-8444-444444444444";
        write(&claude.join("file-history").join(orphan).join("f"), 20);
        write(
            &claude
                .join("projects")
                .join("present")
                .join(orphan)
                .join("subagents")
                .join("a.jsonl"),
            20,
        );
        // Not session-shaped, so never a leftover.
        write(&claude.join("file-history").join("keep-me").join("f"), 20);

        let roots = vec![default_root(home)];
        let now = later();
        let select = |rules: CleanupRules| -> Vec<CleanupItem> {
            let req = CleanupRequest {
                rules: Some(rules),
                ..Default::default()
            };
            plan(&ctx(&roots, now), &req).unwrap().items
        };
        let ids = |items: Vec<CleanupItem>| -> Vec<String> {
            let mut v: Vec<String> = items.into_iter().map(|i| i.id).collect();
            v.sort();
            v
        };

        assert!(
            select(CleanupRules {
                older_than_days: Some(30),
                ..Default::default()
            })
            .is_empty(),
            "ten days is not thirty"
        );
        assert_eq!(
            select(CleanupRules {
                older_than_days: Some(5),
                ..Default::default()
            })
            .len(),
            3
        );
        assert_eq!(
            ids(select(CleanupRules {
                larger_than_mb: Some(2),
                ..Default::default()
            })),
            vec![S2.to_string()]
        );
        assert_eq!(
            ids(select(CleanupRules {
                missing_directory: true,
                ..Default::default()
            })),
            vec![S3.to_string()]
        );

        let leftovers = select(CleanupRules {
            orphans: true,
            ..Default::default()
        });
        assert_eq!(leftovers.len(), 1, "one id over two stores: {leftovers:?}");
        assert_eq!(leftovers[0].id, orphan);
        assert_eq!(leftovers[0].kind, ItemKind::Orphan);
        assert_eq!(leftovers[0].paths.len(), 2);
    }

    #[test]
    fn history_in_a_profile_without_an_account_is_offered() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let backup = home.join("backup");
        let kept = crate::session::session_dir_for(&backup, 1, "a@x.com");
        let abandoned = crate::session::session_dir_for(&backup, 2, "b@x.com");
        for (profile, id) in [(&kept, S1), (&abandoned, S2)] {
            let dir = profile.join("projects").join("D--w");
            std::fs::create_dir_all(&dir).unwrap();
            std::fs::write(dir.join(format!("{id}.jsonl")), "{}\n").unwrap();
        }

        let env = default_root(home).env;
        let roots = history_roots(&env, &backup, Some(std::slice::from_ref(&kept)));
        assert_eq!(roots.len(), 3);
        assert!(roots
            .iter()
            .any(|r| r.profile_number == Some(2) && r.account_removed));

        let req = CleanupRequest {
            rules: Some(CleanupRules {
                removed_accounts: true,
                ..Default::default()
            }),
            ..Default::default()
        };
        let p = plan(&ctx(&roots, later()), &req).unwrap();
        let ids: Vec<&str> = p.items.iter().map(|i| i.id.as_str()).collect();
        assert_eq!(ids, vec![S2]);

        // Not knowing the accounts must never make a profile look abandoned.
        assert!(history_roots(&env, &backup, None)
            .iter()
            .all(|r| !r.account_removed));
    }

    #[test]
    fn run_records_follow_their_session() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let file = transcript(home, "D--a", S1, "D:\\a", 1);
        transcript(home, "D--a", S2, "D:\\a", 1);
        let run = |id: &str, session: &str, status: &str| -> AgentRun {
            serde_json::from_value(json!({
                "id": id, "sessionId": session, "cwd": "D:\\a", "prompt": "p",
                "status": status, "ownerPid": 1, "createdMs": 1, "updatedMs": 1
            }))
            .unwrap()
        };
        let runs = vec![run("r1", S1, "interrupted"), run("r2", S2, "running")];
        let roots = vec![default_root(home)];
        let c = PlanContext {
            roots: &roots,
            runs: &runs,
            now_ms: later(),
        };
        let req = CleanupRequest {
            transcript_dirs: vec![folder_of(&file)],
            ..Default::default()
        };

        let p = plan(&c, &req).unwrap();
        let s1 = p.items.iter().find(|i| i.id == S1).unwrap();
        assert_eq!(s1.run_ids, vec!["r1".to_string()]);
        assert_eq!(p.run_records, 1);
        let s2 = p.items.iter().find(|i| i.id == S2).unwrap();
        assert_eq!(
            s2.blocked,
            Some(Blocker::Live),
            "a running supervised run owns it"
        );

        let out = apply(&c, &req).unwrap();
        assert_eq!(out.removed_run_ids, vec!["r1".to_string()]);
    }

    #[test]
    fn footprint_counts_sidecars_and_snapshots_but_not_memory() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let file = transcript(home, "D--a", S1, "D:\\a", 0);
        let folder = file.parent().unwrap();
        let base = std::fs::metadata(&file).unwrap().len();
        write(&folder.join(S1).join("x"), 100);
        write(
            &home.join(".claude").join("file-history").join(S1).join("y"),
            1000,
        );
        write(&folder.join("memory").join("MEMORY.md"), 10_000);

        assert_eq!(session_bytes(&file), base + 1100);
        assert_eq!(folder_bytes(folder), base + 1100);
    }

    #[test]
    fn only_session_shaped_names_count_as_leftovers() {
        assert!(is_uuid(S1));
        assert!(!is_uuid("memory"));
        assert!(!is_uuid("11111111-1111-4111-8111-11111111111g"));
        assert!(is_safe_id("sess-1_x"));
        assert!(!is_safe_id("../x") && !is_safe_id("a/b") && !is_safe_id(""));
    }

    #[test]
    fn retention_changes_one_value_and_keeps_the_rest_of_the_file() {
        let tmp = tempfile::tempdir().unwrap();
        let path = tmp.path().join("settings.json");
        let original = "{\n    \"model\": \"opus\",\n    \"env\": { \"A\": \"1\" }\n}\n";
        std::fs::write(&path, original).unwrap();

        write_retention(&path, Some(14)).unwrap();
        assert_eq!(
            std::fs::read_to_string(&path).unwrap(),
            "{\n    \"model\": \"opus\",\n    \"env\": { \"A\": \"1\" },\n    \"cleanupPeriodDays\": 14\n}\n"
        );
        assert_eq!(read_retention(&path).unwrap(), Some(14));

        write_retention(&path, Some(90)).unwrap();
        assert!(std::fs::read_to_string(&path)
            .unwrap()
            .contains("\"cleanupPeriodDays\": 90\n"));

        write_retention(&path, None).unwrap();
        assert_eq!(std::fs::read_to_string(&path).unwrap(), original);
        assert_eq!(read_retention(&path).unwrap(), None);
    }

    #[test]
    fn retention_refuses_bad_values_and_files_it_cannot_read() {
        let tmp = tempfile::tempdir().unwrap();
        let path = tmp.path().join("settings.json");
        assert!(matches!(
            write_retention(&path, Some(0)),
            Err(Error::Validation(_))
        ));

        write_retention(&path, Some(7)).unwrap();
        assert_eq!(read_retention(&path).unwrap(), Some(7));

        std::fs::write(&path, "{\"cleanupPeriodDays\": 3, \"a\": 1}").unwrap();
        write_retention(&path, None).unwrap();
        assert_eq!(std::fs::read_to_string(&path).unwrap(), "{\"a\": 1}");

        let commented = "{ // mine\n \"a\": 1 }";
        std::fs::write(&path, commented).unwrap();
        assert!(matches!(
            write_retention(&path, Some(5)),
            Err(Error::Config(_))
        ));
        assert_eq!(std::fs::read_to_string(&path).unwrap(), commented);
    }

    #[test]
    fn purge_preflight_finds_every_home_with_state() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let backup = home.join("backup");
        transcript(home, "D--work", S1, "D:\\work", 10);
        let profile = crate::session::session_dir_for(&backup, 2, "b@x.com");
        std::fs::create_dir_all(&profile).unwrap();
        std::fs::write(
            profile.join(".claude.json"),
            json!({"projects": {"D:/work": {}}}).to_string(),
        )
        .unwrap();

        let env = default_root(home).env;
        let roots = history_roots(&env, &backup, None);
        let pre = purge_preflight(&roots, &[], "D:\\Work\\");
        assert_eq!(pre.roots.len(), 2, "{pre:?}");
        assert!(pre.roots[0].config_dir.is_none());
        assert_eq!(pre.roots[0].sessions, 1);
        assert_eq!(pre.roots[1].profile_number, Some(2));
        assert!(pre.roots[1].registered);
        assert_eq!(pre.roots[1].sessions, 0);
        assert_eq!(pre.live_sessions, 0);
    }

    /// Laid out the way Claude Code writes the file. `D:\work\empty` is
    /// registered under both spellings, one of them carrying an MCP server.
    const REGISTRY: &str = "{\n  \"numStartups\": 3,\n  \"projects\": {\n    \"D:/work/used\": {\n      \"hasTrustDialogAccepted\": true\n    },\n    \"D:\\\\work\\\\empty\": {\n      \"allowedTools\": []\n    },\n    \"D:/dev/dht-Crawler\": {\n      \"hasTrustDialogAccepted\": true\n    },\n    \"D:/work/empty\": {\n      \"mcpServers\": {\n        \"x\": {\"command\": \"y\"}\n      }\n    }\n  },\n  \"theme\": \"dark\"\n}\n";

    /// A registry with history behind two of its directories: one recorded in
    /// its transcript, one only recognisable by Claude Code's folder name.
    fn registry_home(home: &Path) {
        std::fs::write(home.join(".claude.json"), REGISTRY).unwrap();
        transcript(home, "D--work-used", S1, "D:\\work\\used", 1);
        let dht = home
            .join(".claude")
            .join("projects")
            .join("D--dev-dht-Crawler");
        std::fs::create_dir_all(&dht).unwrap();
        std::fs::write(dht.join(format!("{S2}.jsonl")), "{\"type\":\"mode\"}\n").unwrap();
    }

    #[test]
    fn registered_only_directories_are_planned_and_history_is_protected() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        registry_home(home);

        let roots = vec![default_root(home)];
        let plan = registry_plan(&roots, None, &home.join("backups"));

        assert_eq!(plan.removable_paths.len(), 1, "{plan:?}");
        assert_eq!(path_key(&plan.removable_paths[0]), "d:/work/empty");
        assert_eq!(plan.entries.len(), 2, "both spellings are listed");
        assert_eq!(plan.with_mcp, 1);
        assert_eq!(plan.live_directories, 0);
    }

    #[test]
    fn removing_registry_entries_keeps_the_rest_of_the_file() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        registry_home(home);
        let roots = vec![default_root(home)];

        let out = registry_remove(
            &roots,
            &["d:/WORK/empty/".to_string()],
            &home.join("backups"),
            std::time::Duration::from_secs(5),
        );

        assert!(out.failures.is_empty(), "{:?}", out.failures);
        assert_eq!(
            (
                out.removed_entries,
                out.removed_directories,
                out.skipped_directories
            ),
            (2, 1, 0)
        );
        let expected = "{\n  \"numStartups\": 3,\n  \"projects\": {\n    \"D:/work/used\": {\n      \"hasTrustDialogAccepted\": true\n    },\n    \"D:/dev/dht-Crawler\": {\n      \"hasTrustDialogAccepted\": true\n    }\n  },\n  \"theme\": \"dark\"\n}\n";
        assert_eq!(
            std::fs::read_to_string(home.join(".claude.json")).unwrap(),
            expected
        );
        assert_eq!(out.backups.len(), 1);
        assert_eq!(std::fs::read_to_string(&out.backups[0]).unwrap(), REGISTRY);
    }

    #[test]
    fn a_directory_claude_code_is_running_in_stays_registered() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        std::fs::write(home.join(".claude.json"), REGISTRY).unwrap();
        // A session that has just started has registered its directory but not
        // yet written a transcript line — only its pid file says it exists.
        let me = std::process::id();
        let pid_file = home
            .join(".claude")
            .join("sessions")
            .join(format!("{me}.json"));
        write(&pid_file, 0);
        std::fs::write(
            &pid_file,
            json!({"pid": me, "sessionId": S3, "cwd": "D:\\work\\empty",
                   "startedAt": crate::agentruns::now_ms()})
            .to_string(),
        )
        .unwrap();

        let roots = vec![default_root(home)];
        let backups = home.join("backups");
        let plan = registry_plan(&roots, None, &backups);
        assert!(!plan
            .removable_paths
            .iter()
            .any(|p| path_key(p) == "d:/work/empty"));
        assert_eq!(plan.live_directories, 1);

        let out = registry_remove(
            &roots,
            &["D:/work/empty".to_string()],
            &backups,
            std::time::Duration::from_secs(5),
        );
        assert_eq!((out.removed_entries, out.skipped_directories), (0, 1));
        assert!(out.backups.is_empty());
        assert_eq!(
            std::fs::read_to_string(home.join(".claude.json")).unwrap(),
            REGISTRY
        );
    }

    #[test]
    fn removing_every_entry_leaves_an_empty_object() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        std::fs::write(
            home.join(".claude.json"),
            "{\"projects\": {\"D:/a\": {}}, \"x\": 1}",
        )
        .unwrap();

        let out = registry_remove(
            &[default_root(home)],
            &["D:/a".to_string()],
            &home.join("backups"),
            std::time::Duration::from_secs(5),
        );
        assert_eq!(out.removed_entries, 1);
        assert_eq!(
            std::fs::read_to_string(home.join(".claude.json")).unwrap(),
            "{\"projects\": {}, \"x\": 1}"
        );
    }

    #[test]
    fn registry_backups_are_capped_per_home() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let backups = home.join("backups");
        std::fs::create_dir_all(&backups).unwrap();
        for i in 0..12 {
            std::fs::write(
                backups.join(format!("claude.json.default.20000101-0000{i:02}000.bak")),
                "old",
            )
            .unwrap();
        }
        let other_home = backups.join("claude.json.profile-2.20000101-000000000.bak");
        std::fs::write(&other_home, "other").unwrap();

        let newest = write_registry_backup(&backups, &default_root(home), "new").unwrap();

        let kept = std::fs::read_dir(&backups)
            .unwrap()
            .flatten()
            .filter(|e| {
                e.file_name()
                    .to_str()
                    .is_some_and(|n| n.starts_with("claude.json.default."))
            })
            .count();
        assert_eq!(kept, REGISTRY_BACKUPS_KEPT);
        assert!(newest.exists());
        assert!(other_home.exists(), "another home's backups are its own");
    }

    #[test]
    fn a_folder_whose_newest_transcript_does_not_name_its_directory_is_still_that_directory() {
        // The newest session opens with more snapshot data than the head read
        // covers; an older transcript names the directory.
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let older = transcript(home, "D--work-big", S1, "D:\\work\\big", 1);
        let folder = older.parent().unwrap().to_path_buf();
        let snapshot = json!({"type": "file-history-snapshot", "blob": "x".repeat(70_000)});
        std::fs::write(folder.join(format!("{S2}.jsonl")), format!("{snapshot}\n")).unwrap();
        std::fs::File::options()
            .write(true)
            .open(&older)
            .unwrap()
            .set_modified(std::time::SystemTime::now() - std::time::Duration::from_secs(3600))
            .unwrap();

        let listed = projects::list_projects(&default_root(home).env).unwrap();
        assert_eq!(listed.len(), 1, "{listed:?}");
        assert_eq!(path_key(&listed[0].path), "d:/work/big");

        // No transcript names it at all: the registered directory Claude Code
        // would have filed the folder under takes it.
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let dht = home
            .join(".claude")
            .join("projects")
            .join("D--dev-dht-Crawler");
        std::fs::create_dir_all(&dht).unwrap();
        std::fs::write(dht.join(format!("{S2}.jsonl")), format!("{snapshot}\n")).unwrap();
        std::fs::write(
            home.join(".claude.json"),
            json!({"projects": {"D:/dev/dht-Crawler": {}}}).to_string(),
        )
        .unwrap();

        let listed = projects::list_projects(&default_root(home).env).unwrap();
        assert_eq!(listed.len(), 1, "one row, not two: {listed:?}");
        assert_eq!(listed[0].session_count, 1);
        assert!(listed[0].registered);
    }

    #[test]
    fn project_slugs_match_claude_codes_folder_names() {
        assert_eq!(
            projects::project_slug("D:/dev/claude_switch").as_deref(),
            Some("D--dev-claude-switch")
        );
        assert_eq!(
            projects::project_slug(r"D:\dev\dht-Crawler").as_deref(),
            Some("D--dev-dht-Crawler")
        );
        assert_eq!(projects::project_slug("D:/项目").as_deref(), Some("D----"));
        assert!(projects::project_slug(&"a".repeat(201)).is_none());
    }
}
