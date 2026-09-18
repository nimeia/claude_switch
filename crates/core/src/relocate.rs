//! Move Claude Code user data and this app's backup tree onto another drive.
//!
//! The original paths stay (`~/.claude`, `~/.claude-swap-backup`). After a
//! successful apply they become directory links (a junction on Windows, a
//! symlink elsewhere) pointing at `<dest>/claude` and `<dest>/swap`. That is
//! why this does **not** set `CLAUDE_CONFIG_DIR`: session isolation in this
//! app already uses that variable, and the VS Code lock files still write
//! under `~/.claude\ide`.
//!
//! Apply copies while the originals stay put, then renames every original
//! aside (Windows refuses while anything holds a file open inside, so the
//! rename doubles as the "nobody is using it" check), copies what changed
//! during the first pass, and creates the links. A failure anywhere up to the
//! last link puts every original back, so the two trees move together or not
//! at all. The renamed original is kept as a backup; deleting it is a
//! separate step.
//!
//! Undo copies the current data back before unlinking. The backup is only a
//! head start for that copy, and the fallback when the new drive is gone.
//!
//! Links found inside a tree are recreated at the destination, never
//! followed and never dropped.

use std::collections::{BTreeMap, HashSet};
use std::ffi::OsStr;
use std::fs;
use std::io;
use std::path::{Component, Path, PathBuf};

use serde::{Deserialize, Serialize};

use crate::cleanup;
use crate::errors::{Error, Result};
use crate::paths::PathEnv;
use crate::session;

/// Sibling suffix for the pre-link original (`~/.claude.reloc-backup`).
pub const BACKUP_SUFFIX: &str = ".reloc-backup";

/// Written into the destination folder; names the trees this app put there.
pub const MARKER_NAME: &str = ".claude-switch-relocate.json";

const TREE_CLAUDE: &str = "claude";
const TREE_SWAP: &str = "swap";
const DEST_CLAUDE: &str = "claude";
const DEST_SWAP: &str = "swap";
const LOCK_NAME: &str = ".lock";
const SPACE_MARGIN: u64 = 64 * 1024 * 1024;

// --- public snapshots ------------------------------------------------------------

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RelocateTree {
    pub id: String,
    pub source: String,
    pub bytes: u64,
    pub exists: bool,
    pub linked: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub link_target: Option<String>,
    pub backup_path: String,
    pub backup_exists: bool,
    pub backup_bytes: u64,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RelocateScan {
    pub trees: Vec<RelocateTree>,
    pub total_bytes: u64,
    pub live_sessions: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub claude_config_dir: Option<String>,
    pub claude_json_bytes: u64,
    /// Every tree that exists is already a link.
    pub relocated: bool,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RelocatePlanTree {
    pub id: String,
    pub source: String,
    pub dest: String,
    pub backup: String,
    /// Bytes this tree still has to copy (0 when skipped).
    pub bytes: u64,
    /// Nothing to do: already linked here, or there is no source.
    pub skip: bool,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RelocatePlan {
    pub dest_root: String,
    pub dest_free_bytes: Option<u64>,
    pub same_volume: bool,
    pub live_sessions: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub claude_config_dir: Option<String>,
    pub trees: Vec<RelocatePlanTree>,
    pub copy_bytes: u64,
    /// Links inside the trees that will be recreated at the destination.
    pub links: u32,
    pub warnings: Vec<String>,
    pub problems: Vec<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RelocateApply {
    pub copied: Vec<String>,
    pub linked: Vec<String>,
    pub skipped: Vec<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RelocateRestore {
    pub restored: Vec<String>,
    /// The new location was unreachable, so these came back from the backup
    /// taken at move time.
    pub stale: Vec<String>,
    /// Copies on the other drive that nothing uses any more.
    pub leftover: Vec<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RelocateDeleteBackups {
    pub deleted: Vec<String>,
    pub bytes: u64,
}

// --- scan / plan -----------------------------------------------------------------

/// Current size and link state of both trees.
#[must_use]
pub fn scan(env: &PathEnv) -> RelocateScan {
    let trees: Vec<RelocateTree> = specs(env).iter().map(scan_tree).collect();
    let total_bytes = trees.iter().map(|t| t.bytes).sum();
    let any_exist = trees.iter().any(|t| t.exists);
    let relocated = any_exist && trees.iter().filter(|t| t.exists).all(|t| t.linked);
    let claude_json = env.home.join(".claude.json");
    RelocateScan {
        live_sessions: live_session_count(env),
        claude_config_dir: env
            .claude_config_dir
            .as_ref()
            .map(|p| p.to_string_lossy().into_owned()),
        claude_json_bytes: file_len(&claude_json),
        trees,
        total_bytes,
        relocated,
    }
}

/// What apply would do for `dest_root`. Problems block apply; warnings do not.
pub fn plan(env: &PathEnv, dest_root: &Path) -> Result<RelocatePlan> {
    let dest_root = normalize_dest(dest_root)?;
    let live_sessions = live_session_count(env);
    let marker = Marker::load(&dest_root);
    let mut problems: Vec<String> = Vec::new();
    let mut warnings: Vec<String> = Vec::new();

    if dest_root.file_name().is_none() {
        problems.push("drive-root".into());
    }

    let mut trees = Vec::new();
    let mut copy_bytes = 0u64;
    let mut links = 0u32;
    let mut file_links = 0u32;
    let mut any_work = false;
    let mut any_here = false;
    let mut same_vol = false;
    for spec in specs(env) {
        let dest = dest_root.join(spec.dest_name);
        if is_under(&dest_root, &spec.source)
            || is_under(&dest_root, &spec.backup)
            || is_under(&spec.source, &dest)
        {
            problems.push("dest-inside-source".into());
        }
        let linked = is_dir_link(&spec.source);
        let here = linked && links_to(&spec.source, &dest);
        let work = !linked && spec.source.is_dir();
        let mut bytes = 0u64;
        if linked && !here {
            problems.push("linked-elsewhere".into());
        }
        if here {
            any_here = true;
        }
        if work {
            any_work = true;
            if path_present(&spec.backup) {
                problems.push("backup-exists".into());
            }
            let found = survey(&spec.source);
            links = links.saturating_add(found.links);
            file_links = file_links.saturating_add(found.file_links);
            if found.unreadable > 0 {
                problems.push("link-unreadable".into());
            }
            bytes = match dest_state(&dest, spec.id, &spec.source, &marker) {
                DestState::Empty => found.bytes,
                DestState::Ours => found.bytes.saturating_sub(dir_size(&dest)),
                DestState::Foreign => {
                    problems.push("dest-not-empty".into());
                    found.bytes
                }
            };
            copy_bytes = copy_bytes.saturating_add(bytes);
            same_vol |= same_volume(&spec.source, &dest_root);
        }
        trees.push(RelocatePlanTree {
            id: spec.id.to_string(),
            source: spec.source.to_string_lossy().into_owned(),
            dest: dest.to_string_lossy().into_owned(),
            backup: spec.backup.to_string_lossy().into_owned(),
            bytes,
            skip: !work,
        });
    }

    if !any_work && !any_here {
        problems.push("nothing".into());
    }

    let mut dest_free_bytes = None;
    if any_work {
        dest_free_bytes = check_destination(&dest_root, copy_bytes, &mut problems);
        if file_links > 0 && !can_create_file_symlinks() {
            problems.push("symlink-privilege".into());
        }
        if same_vol {
            warnings.push("same-volume".into());
        }
        if links > 0 {
            warnings.push("links".into());
        }
        if live_sessions > 0 {
            problems.push("live-sessions".into());
        }
    }

    problems.sort();
    problems.dedup();

    Ok(RelocatePlan {
        dest_root: dest_root.to_string_lossy().into_owned(),
        dest_free_bytes,
        same_volume: same_vol,
        live_sessions,
        claude_config_dir: env
            .claude_config_dir
            .as_ref()
            .map(|p| p.to_string_lossy().into_owned()),
        trees,
        copy_bytes,
        links,
        warnings,
        problems,
    })
}

/// Volume checks for the destination; returns its free space when known.
fn check_destination(dest_root: &Path, copy_bytes: u64, problems: &mut Vec<String>) -> Option<u64> {
    let Some(probe) = nearest_existing(dest_root) else {
        problems.push("dest-unreachable".into());
        return None;
    };
    if let Some(vol) = volume_info(&probe) {
        if !vol.fixed {
            problems.push("dest-not-local".into());
        } else if !vol.acl_fs {
            problems.push("dest-filesystem".into());
        }
    }
    let free = disk_free_existing(&probe);
    if let Some(free) = free {
        if copy_bytes > 0 && free < copy_bytes.saturating_add(SPACE_MARGIN) {
            problems.push("not-enough-space".into());
        }
    }
    free
}

/// Refuse while any Claude Code instance is running against these trees.
fn ensure_idle(env: &PathEnv) -> Result<()> {
    let live = live_session_count(env);
    if live > 0 {
        return Err(Error::SessionInUse(format!(
            "{live} live Claude Code session(s)"
        )));
    }
    Ok(())
}

// --- apply -----------------------------------------------------------------------

/// Copy each tree, then replace the original paths with links — both or neither.
pub fn apply(env: &PathEnv, dest_root: &Path) -> Result<RelocateApply> {
    let planned = plan(env, dest_root)?;
    if let Some(code) = planned.problems.first() {
        return Err(apply_problem(code, &planned));
    }
    let dest_root = PathBuf::from(&planned.dest_root);
    let skipped: Vec<String> = planned
        .trees
        .iter()
        .filter(|t| t.skip)
        .map(|t| t.id.clone())
        .collect();
    let moves: Vec<Move> = planned
        .trees
        .iter()
        .filter(|t| !t.skip)
        .map(|t| Move {
            id: t.id.clone(),
            source: PathBuf::from(&t.source),
            dest: PathBuf::from(&t.dest),
            backup: PathBuf::from(&t.backup),
        })
        .collect();
    let ids: Vec<String> = moves.iter().map(|m| m.id.clone()).collect();
    if moves.is_empty() {
        return Ok(RelocateApply {
            copied: Vec::new(),
            linked: Vec::new(),
            skipped,
        });
    }

    fs::create_dir_all(&dest_root).map_err(Error::Io)?;
    let mut marker = Marker::load(&dest_root);
    for m in &moves {
        marker
            .trees
            .insert(m.id.clone(), m.source.to_string_lossy().into_owned());
    }
    marker
        .save(&dest_root)
        .map_err(|e| Error::Migration(format!("write {MARKER_NAME}: {e}")))?;

    // First pass while everything is still in place; this is the long one.
    for m in &moves {
        fs::create_dir_all(&m.dest).map_err(Error::Io)?;
        restrict_acl(&m.dest)
            .map_err(|e| Error::Migration(format!("restrict access to {}: {e}", m.id)))?;
        mirror(&m.source, &m.source, &m.dest)
            .map_err(|e| Error::Migration(format!("copy {}: {e}", m.id)))?;
    }

    // Anything that started during the copy would keep writing to the original.
    ensure_idle(env)?;
    cut_over(&moves)?;
    Ok(RelocateApply {
        copied: ids.clone(),
        linked: ids,
        skipped,
    })
}

struct Move {
    id: String,
    source: PathBuf,
    dest: PathBuf,
    backup: PathBuf,
}

fn cut_over(moves: &[Move]) -> Result<()> {
    let mut renamed: Vec<&Move> = Vec::new();
    for m in moves {
        if let Err(e) = rename_patiently(&m.source, &m.backup) {
            let note = put_back(&renamed);
            return Err(rename_error(&m.source, &e, &note));
        }
        renamed.push(m);
    }

    // Catch up on whatever changed during the first pass. Nothing can write
    // through the original path now: it is gone until the link appears.
    for m in moves {
        if let Err(e) = mirror(&m.backup, &m.source, &m.dest) {
            let note = put_back(&renamed);
            return Err(Error::Migration(format!("copy {}: {e}{note}", m.id)));
        }
    }

    let mut linked: Vec<&Move> = Vec::new();
    for m in moves {
        let made = fs::canonicalize(&m.dest).and_then(|abs| create_dir_link(&m.source, &abs));
        if let Err(e) = made {
            for done in linked.iter().rev() {
                let _ = remove_dir_link(&done.source);
            }
            let note = put_back(&renamed);
            return Err(Error::Migration(format!("link {}: {e}{note}", m.id)));
        }
        linked.push(m);
    }
    Ok(())
}

/// Rename originals back into place; names any that could not be.
fn put_back(renamed: &[&Move]) -> String {
    let mut stuck = Vec::new();
    for m in renamed.iter().rev() {
        if rename_patiently(&m.backup, &m.source).is_err() {
            stuck.push(format!(
                "{} is at {}",
                m.source.display(),
                m.backup.display()
            ));
        }
    }
    if stuck.is_empty() {
        String::new()
    } else {
        format!(" (could not put back: {})", stuck.join("; "))
    }
}

/// Rename, retrying briefly: a virus scanner can hold a just-written file for
/// a moment, which Windows reports the same way as a program keeping it open.
fn rename_patiently(from: &Path, to: &Path) -> io::Result<()> {
    let mut tries = 0;
    loop {
        match fs::rename(from, to) {
            Err(e) if e.kind() == io::ErrorKind::PermissionDenied && tries < 4 => {
                tries += 1;
                std::thread::sleep(std::time::Duration::from_millis(250));
            }
            other => return other,
        }
    }
}

fn rename_error(path: &Path, e: &io::Error, note: &str) -> Error {
    // 5 = access denied, 32 = sharing violation: something has a file open.
    let in_use =
        e.kind() == io::ErrorKind::PermissionDenied || matches!(e.raw_os_error(), Some(5 | 32));
    if in_use && note.is_empty() {
        Error::SessionInUse(format!("relocate: in-use {}", path.display()))
    } else {
        Error::Migration(format!("rename {}: {e}{note}", path.display()))
    }
}

// --- restore / delete backups ----------------------------------------------------

/// Point the original paths back at real folders holding the current data.
pub fn restore(env: &PathEnv) -> Result<RelocateRestore> {
    let backs: Vec<Back> = specs(env)
        .into_iter()
        .filter(|s| is_dir_link(&s.source))
        .map(|s| Back {
            target: link_target(&s.source).filter(|t| t.is_dir()),
            id: s.id,
            source: s.source,
            backup: s.backup,
        })
        .collect();
    if backs.is_empty() {
        return Err(Error::Validation("relocate: not-linked".into()));
    }
    ensure_idle(env)?;
    check_restore(env, &backs)?;

    // First pass with the links still in place; this is the long one.
    for b in &backs {
        if let Some(target) = &b.target {
            mirror(target, &b.source, &b.backup)
                .map_err(|e| Error::Migration(format!("copy {} back: {e}", b.id)))?;
        }
    }
    ensure_idle(env)?;
    swap_back(&backs)?;

    Ok(RelocateRestore {
        restored: backs.iter().map(|b| b.id.to_string()).collect(),
        stale: backs
            .iter()
            .filter(|b| b.target.is_none())
            .map(|b| b.id.to_string())
            .collect(),
        leftover: backs
            .iter()
            .filter_map(|b| b.target.as_ref())
            .map(|t| strip_verbatim(t).to_string_lossy().into_owned())
            .collect(),
    })
}

struct Back {
    id: &'static str,
    source: PathBuf,
    backup: PathBuf,
    /// Where the link points, when that folder is still reachable.
    target: Option<PathBuf>,
}

/// Every tree has somewhere to come back from, and the home volume has room.
fn check_restore(env: &PathEnv, backs: &[Back]) -> Result<()> {
    for b in backs {
        if b.target.is_none() && !b.backup.is_dir() {
            let shown = link_target(&b.source).unwrap_or_default();
            return Err(Error::Migration(format!(
                "relocate: unreachable {}",
                strip_verbatim(&shown).display()
            )));
        }
    }
    let needed: u64 = backs
        .iter()
        .filter_map(|b| {
            b.target
                .as_ref()
                .map(|t| dir_size(t).saturating_sub(dir_size(&b.backup)))
        })
        .fold(0, u64::saturating_add);
    if needed == 0 {
        return Ok(());
    }
    let needed = needed.saturating_add(SPACE_MARGIN);
    match disk_free(&env.home) {
        Some(free) if free < needed => Err(Error::Validation(format!(
            "relocate: not-enough-space need {needed} have {free}"
        ))),
        _ => Ok(()),
    }
}

/// Unlink, catch up on the last changes, and rename the copies into place —
/// every tree or none.
fn swap_back(backs: &[Back]) -> Result<()> {
    let relink = |done: &[&Back]| {
        for b in done.iter().rev() {
            if let Some(target) = &b.target {
                let _ = create_dir_link(&b.source, target);
            }
        }
    };
    let mut unlinked: Vec<&Back> = Vec::new();
    for b in backs {
        if let Err(e) = remove_dir_link(&b.source) {
            relink(&unlinked);
            return Err(Error::Migration(format!("unlink {}: {e}", b.id)));
        }
        unlinked.push(b);
    }
    for b in backs {
        if let Some(target) = &b.target {
            if let Err(e) = mirror(target, &b.source, &b.backup) {
                relink(&unlinked);
                return Err(Error::Migration(format!("copy {} back: {e}", b.id)));
            }
        }
    }
    let mut placed: Vec<&Back> = Vec::new();
    for b in backs {
        if let Err(e) = rename_patiently(&b.backup, &b.source) {
            for p in placed.iter().rev() {
                let _ = rename_patiently(&p.source, &p.backup);
            }
            relink(&unlinked);
            return Err(Error::Migration(format!(
                "rename {}: {e}",
                b.backup.display()
            )));
        }
        placed.push(b);
    }
    Ok(())
}

/// Delete the pre-link originals. Refused unless every live path is still a link.
pub fn delete_backups(env: &PathEnv) -> Result<RelocateDeleteBackups> {
    let doomed: Vec<TreeSpec> = specs(env)
        .into_iter()
        .filter(|s| path_present(&s.backup))
        .collect();
    if doomed.is_empty() {
        return Err(Error::Validation("relocate: no-backup".into()));
    }
    // Check them all first, so a refusal never follows a partial delete.
    if doomed.iter().any(|s| !is_dir_link(&s.source)) {
        return Err(Error::Validation("relocate: not-linked".into()));
    }
    let mut deleted = Vec::new();
    let mut bytes = 0u64;
    for spec in doomed {
        let size = dir_size(&spec.backup);
        remove_tree(&spec.backup).map_err(Error::Io)?;
        bytes = bytes.saturating_add(size);
        deleted.push(spec.id.to_string());
    }
    Ok(RelocateDeleteBackups { deleted, bytes })
}

// --- internals -------------------------------------------------------------------

#[derive(Clone)]
struct TreeSpec {
    id: &'static str,
    source: PathBuf,
    dest_name: &'static str,
    backup: PathBuf,
}

fn specs(env: &PathEnv) -> Vec<TreeSpec> {
    // Default login is always `~/.claude`, not `$CLAUDE_CONFIG_DIR`. That
    // variable is how this app isolates a parallel session; linking the
    // default path is what Route A promised.
    let claude = env.home.join(".claude");
    let swap = env.backup_root();
    vec![
        TreeSpec {
            id: TREE_CLAUDE,
            backup: backup_of(&claude),
            source: claude,
            dest_name: DEST_CLAUDE,
        },
        TreeSpec {
            id: TREE_SWAP,
            backup: backup_of(&swap),
            source: swap,
            dest_name: DEST_SWAP,
        },
    ]
}

fn backup_of(source: &Path) -> PathBuf {
    let name = match source.file_name() {
        Some(n) => {
            let mut s = n.to_os_string();
            s.push(BACKUP_SUFFIX);
            s
        }
        None => BACKUP_SUFFIX.into(),
    };
    match source.parent() {
        Some(parent) if !parent.as_os_str().is_empty() => parent.join(name),
        _ => PathBuf::from(name),
    }
}

fn scan_tree(spec: &TreeSpec) -> RelocateTree {
    let linked = is_dir_link(&spec.source);
    let exists = linked || spec.source.exists();
    let link_target = linked
        .then(|| link_target(&spec.source))
        .flatten()
        .map(|p| strip_verbatim(&p).to_string_lossy().into_owned());
    let backup_exists = spec.backup.exists();
    RelocateTree {
        id: spec.id.to_string(),
        source: spec.source.to_string_lossy().into_owned(),
        bytes: if exists { dir_size(&spec.source) } else { 0 },
        exists,
        linked,
        link_target,
        backup_path: spec.backup.to_string_lossy().into_owned(),
        backup_exists,
        backup_bytes: if backup_exists {
            dir_size(&spec.backup)
        } else {
            0
        },
    }
}

fn live_session_count(env: &PathEnv) -> u32 {
    cleanup::history_roots(env, &env.backup_root(), None)
        .iter()
        .map(|r| u32::try_from(session::live_sessions_for(&r.home()).len()).unwrap_or(u32::MAX))
        .fold(0, u32::saturating_add)
}

fn normalize_dest(dest_root: &Path) -> Result<PathBuf> {
    let raw = dest_root.to_string_lossy();
    let trimmed = raw.trim().trim_matches('"');
    if trimmed.is_empty() {
        return Err(Error::Validation("relocate: dest-not-absolute".into()));
    }
    let path = PathBuf::from(trimmed);
    if !path.is_absolute() {
        return Err(Error::Validation("relocate: dest-not-absolute".into()));
    }
    Ok(path)
}

fn apply_problem(code: &str, plan: &RelocatePlan) -> Error {
    match code {
        "live-sessions" => Error::SessionInUse(format!(
            "{} live Claude Code session(s)",
            plan.live_sessions
        )),
        "not-enough-space" => Error::Validation(format!(
            "relocate: not-enough-space need {} have {}",
            plan.copy_bytes.saturating_add(SPACE_MARGIN),
            plan.dest_free_bytes.unwrap_or(0)
        )),
        other => Error::Validation(format!("relocate: {other}")),
    }
}

// --- destination ownership -------------------------------------------------------

/// Which trees this app copied into a destination folder, and from where.
/// Lets a retry (or a move after undo) reuse its own earlier copy instead of
/// refusing a non-empty folder.
#[derive(Default, Serialize, Deserialize)]
struct Marker {
    #[serde(default)]
    trees: BTreeMap<String, String>,
}

impl Marker {
    fn load(dest_root: &Path) -> Self {
        fs::read_to_string(dest_root.join(MARKER_NAME))
            .ok()
            .and_then(|s| serde_json::from_str(&s).ok())
            .unwrap_or_default()
    }

    fn save(&self, dest_root: &Path) -> io::Result<()> {
        let body = serde_json::to_string_pretty(self).map_err(io::Error::other)?;
        fs::write(dest_root.join(MARKER_NAME), body)
    }

    fn owns(&self, id: &str, source: &Path) -> bool {
        self.trees
            .get(id)
            .is_some_and(|s| same_path(Path::new(s), source))
    }
}

enum DestState {
    Empty,
    Ours,
    Foreign,
}

fn dest_state(dest: &Path, id: &str, source: &Path, marker: &Marker) -> DestState {
    let Ok(meta) = fs::symlink_metadata(dest) else {
        return DestState::Empty;
    };
    if is_link(&meta) || !meta.is_dir() {
        return DestState::Foreign;
    }
    let empty = fs::read_dir(dest).is_ok_and(|mut rd| rd.next().is_none());
    if empty {
        DestState::Empty
    } else if marker.owns(id, source) {
        DestState::Ours
    } else {
        DestState::Foreign
    }
}

// --- copying ---------------------------------------------------------------------

/// Make `dst` an exact copy of the directory `src`.
///
/// Files with the same size and modified time are left alone, so a second
/// pass only copies what changed since the first. Links are recreated rather
/// than followed. `logical` is where `src` normally lives, so a relative link
/// still names the same place when `src` is a renamed copy.
fn mirror(src: &Path, logical: &Path, dst: &Path) -> io::Result<()> {
    let meta = fs::symlink_metadata(src)?;
    if is_link(&meta) || !meta.is_dir() {
        return Err(io::Error::other("refusing to copy a link as a source tree"));
    }
    mirror_dir(src, logical, dst)
}

fn mirror_dir(src: &Path, logical: &Path, dst: &Path) -> io::Result<()> {
    match fs::symlink_metadata(dst) {
        Ok(m) if m.is_dir() && !is_link(&m) => {}
        Ok(_) => {
            remove_tree(dst)?;
            fs::create_dir(dst)?;
        }
        Err(e) if e.kind() == io::ErrorKind::NotFound => fs::create_dir_all(dst)?,
        Err(e) => return Err(e),
    }
    let mut keep = HashSet::new();
    for entry in fs::read_dir(src).map_err(at(src))? {
        let entry = entry?;
        let name = entry.file_name();
        if name == LOCK_NAME {
            continue;
        }
        keep.insert(name_key(&name));
        let from = entry.path();
        let to = dst.join(&name);
        let meta = fs::symlink_metadata(&from)?;
        if is_link(&meta) {
            mirror_link(&from, &logical.join(&name), &meta, &to)?;
        } else if meta.is_dir() {
            mirror_dir(&from, &logical.join(&name), &to)?;
        } else {
            mirror_file(&from, &meta, &to)?;
        }
    }
    for entry in fs::read_dir(dst)? {
        let entry = entry?;
        let name = entry.file_name();
        if name == LOCK_NAME || keep.contains(&name_key(&name)) {
            continue;
        }
        remove_tree(&entry.path())?;
    }
    Ok(())
}

fn mirror_file(src: &Path, meta: &fs::Metadata, dst: &Path) -> io::Result<()> {
    match fs::symlink_metadata(dst) {
        Ok(d) if is_link(&d) || d.is_dir() => remove_tree(dst)?,
        Ok(d) => {
            let same_time = match (d.modified(), meta.modified()) {
                (Ok(a), Ok(b)) => a == b,
                _ => false,
            };
            if same_time && d.len() == meta.len() {
                return Ok(());
            }
            // CopyFile refuses to overwrite a read-only or hidden file.
            remove_file_force(dst)?;
        }
        Err(e) if e.kind() == io::ErrorKind::NotFound => {}
        Err(e) => return Err(e),
    }
    fs::copy(src, dst).map_err(at(src))?;
    keep_modified(dst, meta);
    Ok(())
}

/// Name the file an I/O error is about; `fs::copy` errors do not.
fn at(path: &Path) -> impl FnOnce(io::Error) -> io::Error + '_ {
    move |e| io::Error::new(e.kind(), format!("{}: {e}", path.display()))
}

fn mirror_link(src: &Path, logical: &Path, meta: &fs::Metadata, dst: &Path) -> io::Result<()> {
    let target = copy_target(src, logical)?;
    if let Ok(d) = fs::symlink_metadata(dst) {
        if is_link(&d) && raw_target(dst).is_some_and(|t| same_path(&t, &target)) {
            return Ok(());
        }
        remove_tree(dst)?;
    }
    create_link_like(src, meta, &target, dst)
}

/// Where a link inside a tree points, as an absolute path that stays right
/// wherever the copy lands.
fn copy_target(src: &Path, logical: &Path) -> io::Result<PathBuf> {
    let raw = raw_target(src)
        .ok_or_else(|| io::Error::other(format!("cannot read the link {}", src.display())))?;
    if raw.is_absolute() {
        return Ok(raw);
    }
    let base = logical.parent().unwrap_or_else(|| Path::new(""));
    Ok(normalize_lexical(&base.join(raw)))
}

/// A link's stored target, not resolved, without a verbatim prefix.
fn raw_target(path: &Path) -> Option<PathBuf> {
    #[cfg(windows)]
    {
        if let Ok(t) = junction::get_target(path) {
            return Some(strip_verbatim(&t));
        }
    }
    fs::read_link(path).ok().map(|t| strip_verbatim(&t))
}

#[cfg(windows)]
fn create_link_like(src: &Path, meta: &fs::Metadata, target: &Path, dst: &Path) -> io::Result<()> {
    use std::os::windows::fs::{symlink_dir, symlink_file, FileTypeExt};
    if junction::exists(src).unwrap_or(false) {
        return junction::create(target, dst);
    }
    if meta.file_type().is_symlink_file() {
        return symlink_file(target, dst);
    }
    // A directory symlink, or a junction whose target is gone. Without the
    // symlink privilege, a junction to a local folder behaves the same.
    symlink_dir(target, dst).or_else(|e| {
        if has_drive_letter(target) {
            junction::create(target, dst)
        } else {
            Err(e)
        }
    })
}

#[cfg(unix)]
fn create_link_like(
    _src: &Path,
    _meta: &fs::Metadata,
    target: &Path,
    dst: &Path,
) -> io::Result<()> {
    std::os::unix::fs::symlink(target, dst)
}

#[cfg(windows)]
fn has_drive_letter(path: &Path) -> bool {
    use std::path::Prefix;
    matches!(
        path.components().next(),
        Some(Component::Prefix(p)) if matches!(p.kind(), Prefix::Disk(_) | Prefix::VerbatimDisk(_))
    )
}

/// Windows file names compare case-insensitively.
fn name_key(name: &OsStr) -> String {
    #[cfg(windows)]
    {
        name.to_string_lossy().to_lowercase()
    }
    #[cfg(not(windows))]
    {
        name.to_string_lossy().into_owned()
    }
}

#[cfg(windows)]
fn keep_modified(_dst: &Path, _meta: &fs::Metadata) {
    // CopyFileExW already carries the last-write time over.
}

#[cfg(not(windows))]
fn keep_modified(dst: &Path, meta: &fs::Metadata) {
    // Without this every later pass would copy every file again.
    if let Ok(t) = meta.modified() {
        let _ = fs::File::options()
            .write(true)
            .open(dst)
            .and_then(|f| f.set_modified(t));
    }
}

fn remove_file_force(path: &Path) -> io::Result<()> {
    match fs::remove_file(path) {
        Err(e) if e.kind() == io::ErrorKind::PermissionDenied => {
            #[cfg(windows)]
            {
                let mut perms = fs::symlink_metadata(path)?.permissions();
                if perms.readonly() {
                    // Clears FILE_ATTRIBUTE_READONLY; the unix mode concern
                    // behind this lint does not apply on Windows.
                    #[allow(clippy::permissions_set_readonly_false)]
                    perms.set_readonly(false);
                    fs::set_permissions(path, perms)?;
                    return fs::remove_file(path);
                }
            }
            Err(e)
        }
        other => other,
    }
}

/// Remove a file, a link (never its target) or a whole directory.
fn remove_tree(path: &Path) -> io::Result<()> {
    let meta = match fs::symlink_metadata(path) {
        Ok(m) => m,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(e),
    };
    if is_link(&meta) {
        return remove_link(path, &meta);
    }
    if !meta.is_dir() {
        return remove_file_force(path);
    }
    for entry in fs::read_dir(path)? {
        remove_tree(&entry?.path())?;
    }
    fs::remove_dir(path)
}

/// Remove a link found inside a tree. `is_dir` is false for every link, so
/// ask the file type: Windows needs `RemoveDirectory` for a folder link.
fn remove_link(path: &Path, meta: &fs::Metadata) -> io::Result<()> {
    #[cfg(windows)]
    {
        use std::os::windows::fs::FileTypeExt;
        if meta.file_type().is_symlink_dir() {
            return fs::remove_dir(path);
        }
    }
    #[cfg(not(windows))]
    {
        let _ = meta;
    }
    fs::remove_file(path)
}

// --- sizes and links -------------------------------------------------------------

#[derive(Default)]
struct Survey {
    bytes: u64,
    links: u32,
    /// File symlinks; Windows needs a privilege (or Developer Mode) to make one.
    file_links: u32,
    unreadable: u32,
}

/// Size of a tree plus the links inside it, without following them.
fn survey(root: &Path) -> Survey {
    let mut out = Survey::default();
    survey_into(root, root, &mut out);
    out
}

fn survey_into(dir: &Path, logical: &Path, out: &mut Survey) {
    let Ok(rd) = fs::read_dir(dir) else {
        return;
    };
    for entry in rd.flatten() {
        let p = entry.path();
        let Ok(meta) = fs::symlink_metadata(&p) else {
            continue;
        };
        if is_link(&meta) {
            out.links = out.links.saturating_add(1);
            if copy_target(&p, &logical.join(entry.file_name())).is_err() {
                out.unreadable = out.unreadable.saturating_add(1);
            }
            #[cfg(windows)]
            {
                use std::os::windows::fs::FileTypeExt;
                if meta.file_type().is_symlink_file() {
                    out.file_links = out.file_links.saturating_add(1);
                }
            }
        } else if meta.is_dir() {
            survey_into(&p, &logical.join(entry.file_name()), out);
        } else {
            out.bytes = out.bytes.saturating_add(meta.len());
        }
    }
}

fn dir_size(path: &Path) -> u64 {
    let Ok(meta) = fs::symlink_metadata(path) else {
        return 0;
    };
    if is_link(&meta) {
        if let Some(target) = link_target(path) {
            return dir_size_contents(&target);
        }
        return 0;
    }
    if meta.is_file() {
        return meta.len();
    }
    dir_size_contents(path)
}

fn dir_size_contents(path: &Path) -> u64 {
    let Ok(rd) = fs::read_dir(path) else {
        return 0;
    };
    let mut n = 0u64;
    for entry in rd.flatten() {
        let p = entry.path();
        let Ok(meta) = fs::symlink_metadata(&p) else {
            continue;
        };
        if is_link(&meta) {
            continue;
        }
        if meta.is_dir() {
            n = n.saturating_add(dir_size_contents(&p));
        } else {
            n = n.saturating_add(meta.len());
        }
    }
    n
}

fn file_len(path: &Path) -> u64 {
    fs::metadata(path).map_or(0, |m| m.len())
}

/// Exists, or is a link (even a dangling one).
fn path_present(path: &Path) -> bool {
    fs::symlink_metadata(path).is_ok()
}

fn is_dir_link(path: &Path) -> bool {
    fs::symlink_metadata(path).is_ok_and(|m| is_link(&m))
}

/// A symlink or junction. Other reparse points (cloud placeholders, dedup)
/// are ordinary files and folders as far as copying goes.
fn is_link(meta: &fs::Metadata) -> bool {
    meta.file_type().is_symlink()
}

fn link_target(path: &Path) -> Option<PathBuf> {
    #[cfg(windows)]
    {
        if let Ok(t) = junction::get_target(path) {
            return Some(t);
        }
    }
    fs::read_link(path).ok().map(|t| resolve_link(path, &t))
}

fn resolve_link(link: &Path, target: &Path) -> PathBuf {
    if target.is_absolute() {
        target.to_path_buf()
    } else {
        link.parent().unwrap_or_else(|| Path::new(".")).join(target)
    }
}

fn links_to(link: &Path, dest: &Path) -> bool {
    let Some(target) = link_target(link) else {
        return false;
    };
    paths_equal(&target, dest)
}

fn paths_equal(a: &Path, b: &Path) -> bool {
    match (fs::canonicalize(a), fs::canonicalize(b)) {
        (Ok(ca), Ok(cb)) => ca == cb,
        _ => same_path(a, b),
    }
}

/// Same path by spelling (no filesystem access); case-insensitive on Windows.
fn same_path(a: &Path, b: &Path) -> bool {
    let a = normalize_lexical(&strip_verbatim(a));
    let b = normalize_lexical(&strip_verbatim(b));
    is_under(&a, &b) && is_under(&b, &a)
}

fn strip_verbatim(path: &Path) -> PathBuf {
    let s = path.to_string_lossy();
    if let Some(rest) = s.strip_prefix(r"\\?\UNC\") {
        return PathBuf::from(format!(r"\\{rest}"));
    }
    if let Some(rest) = s.strip_prefix(r"\\?\") {
        return PathBuf::from(rest);
    }
    path.to_path_buf()
}

fn normalize_lexical(path: &Path) -> PathBuf {
    let mut out = PathBuf::new();
    for c in path.components() {
        match c {
            Component::CurDir => {}
            Component::ParentDir => {
                if !out.pop() {
                    out.push(c);
                }
            }
            other => out.push(other),
        }
    }
    out
}

fn is_under(path: &Path, ancestor: &Path) -> bool {
    let path = abs_lexical(path);
    let ancestor = abs_lexical(ancestor);
    let pc: Vec<_> = path.components().collect();
    let ac: Vec<_> = ancestor.components().collect();
    if ac.is_empty() || pc.len() < ac.len() {
        return false;
    }
    pc.iter().zip(ac.iter()).all(|(a, b)| component_eq(a, b))
}

fn component_eq(a: &Component<'_>, b: &Component<'_>) -> bool {
    #[cfg(windows)]
    {
        a.as_os_str().eq_ignore_ascii_case(b.as_os_str())
    }
    #[cfg(not(windows))]
    {
        a == b
    }
}

fn abs_lexical(path: &Path) -> PathBuf {
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()
            .unwrap_or_else(|_| PathBuf::from("."))
            .join(path)
    }
}

// --- volumes ---------------------------------------------------------------------

fn same_volume(a: &Path, b: &Path) -> bool {
    match (volume_key(a), volume_key(b)) {
        (Some(x), Some(y)) => x == y,
        _ => false,
    }
}

fn volume_key(path: &Path) -> Option<String> {
    #[cfg(windows)]
    {
        for c in abs_lexical(path).components() {
            if let Component::Prefix(p) = c {
                return Some(p.as_os_str().to_string_lossy().to_ascii_uppercase());
            }
        }
        None
    }
    #[cfg(not(windows))]
    {
        let _ = path;
        Some(String::from("/"))
    }
}

/// `path` itself or its closest existing ancestor.
fn nearest_existing(path: &Path) -> Option<PathBuf> {
    path.ancestors()
        .find(|a| !a.as_os_str().is_empty() && a.exists())
        .map(Path::to_path_buf)
}

fn disk_free(path: &Path) -> Option<u64> {
    nearest_existing(path).and_then(|p| disk_free_existing(&p))
}

#[cfg(windows)]
fn wide(path: &Path) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    path.as_os_str().encode_wide().chain(Some(0)).collect()
}

#[cfg(windows)]
fn disk_free_existing(path: &Path) -> Option<u64> {
    #[link(name = "kernel32")]
    extern "system" {
        fn GetDiskFreeSpaceExW(
            directory: *const u16,
            available: *mut u64,
            total: *mut u64,
            free: *mut u64,
        ) -> i32;
    }
    let wide = wide(path);
    let mut available = 0u64;
    let mut total = 0u64;
    let mut free = 0u64;
    let ok = unsafe { GetDiskFreeSpaceExW(wide.as_ptr(), &mut available, &mut total, &mut free) };
    if ok == 0 {
        None
    } else {
        Some(available)
    }
}

#[cfg(not(windows))]
fn disk_free_existing(_path: &Path) -> Option<u64> {
    None
}

struct VolumeInfo {
    /// A fixed local disk: not removable, not a network share.
    fixed: bool,
    /// NTFS or `ReFS`, so the destination can be locked down to this user.
    acl_fs: bool,
}

#[cfg(windows)]
fn volume_info(path: &Path) -> Option<VolumeInfo> {
    const DRIVE_FIXED: u32 = 3;
    #[link(name = "kernel32")]
    extern "system" {
        fn GetVolumePathNameW(file: *const u16, root: *mut u16, len: u32) -> i32;
        fn GetDriveTypeW(root: *const u16) -> u32;
        fn GetVolumeInformationW(
            root: *const u16,
            name: *mut u16,
            name_len: u32,
            serial: *mut u32,
            max_component: *mut u32,
            flags: *mut u32,
            fs_name: *mut u16,
            fs_name_len: u32,
        ) -> i32;
    }
    let path = wide(path);
    let mut root = [0u16; 1024];
    let mut fs_name = [0u16; 64];
    unsafe {
        if GetVolumePathNameW(path.as_ptr(), root.as_mut_ptr(), 1024) == 0 {
            return None;
        }
        let fixed = GetDriveTypeW(root.as_ptr()) == DRIVE_FIXED;
        let ok = GetVolumeInformationW(
            root.as_ptr(),
            std::ptr::null_mut(),
            0,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            fs_name.as_mut_ptr(),
            64,
        );
        let end = fs_name
            .iter()
            .position(|&c| c == 0)
            .unwrap_or(fs_name.len());
        let fs = String::from_utf16_lossy(&fs_name[..end]);
        Some(VolumeInfo {
            fixed,
            acl_fs: ok != 0 && (fs.eq_ignore_ascii_case("NTFS") || fs.eq_ignore_ascii_case("ReFS")),
        })
    }
}

#[cfg(not(windows))]
fn volume_info(_path: &Path) -> Option<VolumeInfo> {
    None
}

#[cfg(windows)]
fn can_create_file_symlinks() -> bool {
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |d| d.subsec_nanos());
    let probe =
        std::env::temp_dir().join(format!("cswitch-link-probe-{}-{nanos}", std::process::id()));
    let ok = std::os::windows::fs::symlink_file("cswitch-link-probe-target", &probe).is_ok();
    let _ = fs::remove_file(&probe);
    ok
}

#[cfg(not(windows))]
fn can_create_file_symlinks() -> bool {
    true
}

// --- access control --------------------------------------------------------------

/// Give a destination tree the same access a user profile folder has: this
/// user, SYSTEM and Administrators. Other accounts on the machine could
/// otherwise read the credentials that live in these trees.
#[cfg(windows)]
fn restrict_acl(dir: &Path) -> io::Result<()> {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    let sid =
        current_user_sid().ok_or_else(|| io::Error::other("cannot read the current user's SID"))?;
    let status = std::process::Command::new("icacls")
        .arg(dir)
        .arg("/inheritance:r")
        .arg("/grant:r")
        .arg(format!("*{sid}:(OI)(CI)F"))
        .arg("*S-1-5-18:(OI)(CI)F")
        .arg("*S-1-5-32-544:(OI)(CI)F")
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .creation_flags(CREATE_NO_WINDOW)
        .status()?;
    if status.success() {
        Ok(())
    } else {
        Err(io::Error::other(format!("icacls failed ({status})")))
    }
}

#[cfg(not(windows))]
fn restrict_acl(dir: &Path) -> io::Result<()> {
    use std::os::unix::fs::PermissionsExt;
    fs::set_permissions(dir, fs::Permissions::from_mode(0o700))
}

/// `S-1-5-21-…` of the user running this process.
#[cfg(windows)]
fn current_user_sid() -> Option<String> {
    const TOKEN_QUERY: u32 = 0x0008;
    const TOKEN_USER: u32 = 1;
    #[link(name = "kernel32")]
    extern "system" {
        fn GetCurrentProcess() -> isize;
        fn CloseHandle(handle: isize) -> i32;
        fn LocalFree(mem: *mut u16) -> *mut u16;
    }
    #[link(name = "advapi32")]
    extern "system" {
        fn OpenProcessToken(process: isize, access: u32, token: *mut isize) -> i32;
        fn GetTokenInformation(
            token: isize,
            class: u32,
            info: *mut u64,
            len: u32,
            returned: *mut u32,
        ) -> i32;
        fn ConvertSidToStringSidW(sid: *const u8, out: *mut *mut u16) -> i32;
    }
    // TOKEN_USER starts with the SID pointer; u64 keeps it aligned.
    let mut buf = [0u64; 64];
    unsafe {
        let mut token = 0isize;
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return None;
        }
        let mut returned = 0u32;
        let ok = GetTokenInformation(token, TOKEN_USER, buf.as_mut_ptr(), 512, &mut returned);
        CloseHandle(token);
        if ok == 0 {
            return None;
        }
        let sid = buf.as_ptr().cast::<*const u8>().read();
        let mut text: *mut u16 = std::ptr::null_mut();
        if ConvertSidToStringSidW(sid, &mut text) == 0 || text.is_null() {
            return None;
        }
        let mut len = 0usize;
        while *text.add(len) != 0 {
            len += 1;
        }
        let out = String::from_utf16_lossy(std::slice::from_raw_parts(text, len));
        LocalFree(text);
        Some(out)
    }
}

// --- links -----------------------------------------------------------------------

fn create_dir_link(link: &Path, target: &Path) -> io::Result<()> {
    #[cfg(windows)]
    {
        junction::create(target, link)
    }
    #[cfg(unix)]
    {
        std::os::unix::fs::symlink(target, link)
    }
}

fn remove_dir_link(path: &Path) -> io::Result<()> {
    #[cfg(windows)]
    {
        fs::remove_dir(path)
    }
    #[cfg(unix)]
    {
        fs::remove_file(path)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn env_at(root: &Path) -> PathEnv {
        PathEnv {
            home: root.to_path_buf(),
            claude_config_dir: None,
            xdg_data_home: None,
            platform: crate::models::Platform::Windows,
        }
    }

    fn write_tree(dir: &Path, rel: &str, body: &[u8]) {
        let path = dir.join(rel);
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).unwrap();
        }
        fs::write(path, body).unwrap();
    }

    fn unlink_test_links(env: &PathEnv) {
        for spec in specs(env) {
            if is_dir_link(&spec.source) {
                let _ = remove_dir_link(&spec.source);
            }
        }
    }

    fn claim_live_session(config_home: &Path) {
        let sessions = config_home.join("sessions");
        fs::create_dir_all(&sessions).unwrap();
        let pid = std::process::id();
        fs::write(
            sessions.join(format!("{pid}.json")),
            json!({ "pid": pid }).to_string(),
        )
        .unwrap();
    }

    #[test]
    fn scan_reports_sizes_and_the_sidecar_json() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        write_tree(
            &env.home.join(".claude"),
            "projects/a.jsonl",
            b"hello world",
        );
        write_tree(
            &env.backup_root(),
            "sessions/1-a/projects/b.jsonl",
            b"session-bytes",
        );
        fs::write(env.home.join(".claude.json"), b"{\"mcpServers\":{}}").unwrap();

        let s = scan(&env);
        assert_eq!(s.trees.len(), 2);
        let claude = s.trees.iter().find(|t| t.id == "claude").unwrap();
        let swap = s.trees.iter().find(|t| t.id == "swap").unwrap();
        assert!(claude.bytes >= 11);
        assert!(swap.bytes >= 13);
        assert!(!claude.linked);
        assert!(!s.relocated);
        assert!(s.claude_json_bytes > 0);
        assert_eq!(s.live_sessions, 0);
    }

    #[test]
    fn plan_rejects_a_relative_destination() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        fs::create_dir_all(env.home.join(".claude")).unwrap();
        let err = plan(&env, Path::new("ClaudeData")).unwrap_err();
        assert!(err.to_string().contains("dest-not-absolute"), "{err}");
    }

    #[test]
    fn plan_rejects_a_destination_inside_the_source() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let claude = env.home.join(".claude");
        fs::create_dir_all(&claude).unwrap();
        write_tree(&claude, "x.txt", b"x");
        let nested = claude.join("offload");
        let planned = plan(&env, &nested).unwrap();
        assert!(
            planned.problems.iter().any(|p| p == "dest-inside-source"),
            "{planned:?}"
        );
    }

    #[test]
    fn apply_links_both_trees_and_restore_puts_them_back() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        write_tree(&env.home.join(".claude"), "settings.json", b"{\"a\":1}");
        write_tree(&env.backup_root(), "sequence.json", b"{\"n\":1}");
        let dest = tmp.path().join("offload");

        let done = apply(&env, &dest).unwrap();
        assert_eq!(done.copied, vec!["claude", "swap"]);
        assert_eq!(done.linked, vec!["claude", "swap"]);

        let claude = env.home.join(".claude");
        let swap = env.backup_root();
        assert!(is_dir_link(&claude), "default login path should be a link");
        assert!(is_dir_link(&swap), "backup root should be a link");
        assert_eq!(
            fs::read_to_string(claude.join("settings.json")).unwrap(),
            "{\"a\":1}"
        );
        assert_eq!(
            fs::read_to_string(swap.join("sequence.json")).unwrap(),
            "{\"n\":1}"
        );
        assert!(backup_of(&claude).exists());
        assert!(scan(&env).relocated);

        let back = restore(&env).unwrap();
        assert_eq!(back.restored.len(), 2);
        assert!(back.stale.is_empty());
        assert_eq!(back.leftover.len(), 2);
        assert!(!is_dir_link(&claude));
        assert!(!backup_of(&claude).exists());
        assert_eq!(
            fs::read_to_string(claude.join("settings.json")).unwrap(),
            "{\"a\":1}"
        );
        unlink_test_links(&env);
    }

    #[test]
    fn delete_backups_refuses_until_the_live_path_is_a_link() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        write_tree(&env.home.join(".claude"), "a.txt", b"aa");
        let dest = tmp.path().join("offload");
        apply(&env, &dest).unwrap();
        let gone = delete_backups(&env).unwrap();
        assert!(gone.deleted.contains(&"claude".to_string()));
        assert!(!backup_of(&env.home.join(".claude")).exists());
        unlink_test_links(&env);
    }

    #[test]
    fn delete_backups_checks_every_tree_before_deleting_any() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let claude = env.home.join(".claude");
        write_tree(&claude, "a.txt", b"aa");
        apply(&env, &tmp.path().join("offload")).unwrap();
        // A stray swap backup next to a swap tree that is not a link.
        write_tree(&env.backup_root(), "sequence.json", b"{}");
        write_tree(&backup_of(&env.backup_root()), "old.json", b"{}");

        let err = delete_backups(&env).unwrap_err();
        assert!(err.to_string().contains("not-linked"), "{err}");
        assert!(backup_of(&claude).exists(), "nothing deleted on refusal");
        unlink_test_links(&env);
    }

    #[test]
    fn apply_stops_when_a_claude_session_is_live() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        claim_live_session(&env.home.join(".claude"));
        let dest = tmp.path().join("offload");
        let err = apply(&env, &dest).unwrap_err();
        match err {
            Error::SessionInUse(_) => {}
            other => panic!("expected session-in-use, got {other}"),
        }
    }

    #[test]
    fn apply_is_idempotent_once_linked() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        write_tree(&env.home.join(".claude"), "a.txt", b"aa");
        let dest = tmp.path().join("offload");
        apply(&env, &dest).unwrap();
        let again = apply(&env, &dest).unwrap();
        assert!(again.copied.is_empty());
        assert!(again.skipped.contains(&"claude".to_string()));
        unlink_test_links(&env);
    }

    #[test]
    fn links_inside_a_tree_survive_the_move() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let claude = env.home.join(".claude");
        write_tree(&claude, "settings.json", b"{}");
        let dotfiles = tmp.path().join("dotfiles");
        write_tree(&dotfiles, "commands/review.md", b"# review");
        write_tree(&dotfiles, "CLAUDE.md", b"# memory");
        create_dir_link(&claude.join("commands"), &dotfiles.join("commands")).unwrap();
        let file_link = can_create_file_symlinks();
        if file_link {
            #[cfg(windows)]
            std::os::windows::fs::symlink_file(
                dotfiles.join("CLAUDE.md"),
                claude.join("CLAUDE.md"),
            )
            .unwrap();
            #[cfg(unix)]
            std::os::unix::fs::symlink(dotfiles.join("CLAUDE.md"), claude.join("CLAUDE.md"))
                .unwrap();
        }

        let planned = plan(&env, &tmp.path().join("offload")).unwrap();
        assert!(planned.links >= 1, "{planned:?}");
        assert!(planned.warnings.iter().any(|w| w == "links"));

        apply(&env, &tmp.path().join("offload")).unwrap();
        let dest_commands = tmp.path().join("offload").join("claude").join("commands");
        assert!(
            is_dir_link(&dest_commands),
            "recreated as a link, not copied"
        );
        assert_eq!(
            fs::read_to_string(claude.join("commands").join("review.md")).unwrap(),
            "# review"
        );
        if file_link {
            assert_eq!(
                fs::read_to_string(claude.join("CLAUDE.md")).unwrap(),
                "# memory"
            );
        }
        // Deleting the move-time backup must leave the linked folder alone.
        delete_backups(&env).unwrap();
        assert!(dotfiles.join("commands").join("review.md").exists());
        unlink_test_links(&env);
        let _ = remove_tree(&dest_commands);
    }

    #[test]
    fn a_file_held_open_leaves_nothing_half_moved() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let claude = env.home.join(".claude");
        write_tree(&claude, "settings.json", b"{}");
        write_tree(&env.backup_root(), "sequence.json", b"{}");
        let dest = tmp.path().join("offload");

        // Windows will not rename a folder with a file open inside it.
        let held = fs::File::open(env.backup_root().join("sequence.json")).unwrap();
        let err = apply(&env, &dest).unwrap_err();
        drop(held);
        if cfg!(windows) {
            assert!(err.to_string().contains("in-use"), "{err}");
            assert!(!is_dir_link(&claude), "the first tree was put back");
            assert!(!is_dir_link(&env.backup_root()));
            assert!(!backup_of(&claude).exists());
            assert!(claude.join("settings.json").exists());

            // A retry reuses the copy already on the destination.
            let planned = plan(&env, &dest).unwrap();
            assert!(planned.problems.is_empty(), "{planned:?}");
            apply(&env, &dest).unwrap();
            assert!(scan(&env).relocated);
        }
        unlink_test_links(&env);
    }

    #[test]
    fn restore_keeps_changes_made_after_the_move() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let claude = env.home.join(".claude");
        write_tree(&claude, ".credentials.json", b"token-v1");
        write_tree(&claude, "projects/old.jsonl", b"old");
        apply(&env, &tmp.path().join("offload")).unwrap();

        // Claude Code keeps running on the new drive for a while.
        fs::write(claude.join(".credentials.json"), b"token-v2").unwrap();
        write_tree(&claude, "projects/new.jsonl", b"new");
        fs::remove_file(claude.join("projects").join("old.jsonl")).unwrap();

        restore(&env).unwrap();
        assert!(!is_dir_link(&claude));
        assert_eq!(
            fs::read_to_string(claude.join(".credentials.json")).unwrap(),
            "token-v2"
        );
        assert!(claude.join("projects").join("new.jsonl").exists());
        assert!(!claude.join("projects").join("old.jsonl").exists());
    }

    #[test]
    fn restore_works_after_the_backups_are_deleted() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let claude = env.home.join(".claude");
        write_tree(&claude, "settings.json", b"{\"v\":2}");
        apply(&env, &tmp.path().join("offload")).unwrap();
        delete_backups(&env).unwrap();

        restore(&env).unwrap();
        assert!(!is_dir_link(&claude));
        assert_eq!(
            fs::read_to_string(claude.join("settings.json")).unwrap(),
            "{\"v\":2}"
        );
    }

    #[test]
    fn restore_falls_back_to_the_backup_when_the_new_drive_is_gone() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let claude = env.home.join(".claude");
        write_tree(&claude, "settings.json", b"{\"v\":1}");
        let dest = tmp.path().join("offload");
        apply(&env, &dest).unwrap();
        remove_tree(&dest.join("claude")).unwrap();

        let back = restore(&env).unwrap();
        assert_eq!(back.stale, vec!["claude"]);
        assert_eq!(
            fs::read_to_string(claude.join("settings.json")).unwrap(),
            "{\"v\":1}"
        );
    }

    #[test]
    fn restore_refuses_while_a_session_is_live() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let claude = env.home.join(".claude");
        write_tree(&claude, "a.txt", b"a");
        apply(&env, &tmp.path().join("offload")).unwrap();
        claim_live_session(&claude);

        let err = restore(&env).unwrap_err();
        assert!(matches!(err, Error::SessionInUse(_)), "{err}");
        assert!(is_dir_link(&claude), "still linked");
        unlink_test_links(&env);
    }

    #[test]
    fn plan_refuses_someone_elses_folder_but_reuses_its_own() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        write_tree(&env.home.join(".claude"), "a.txt", b"a");
        let dest = tmp.path().join("offload");
        write_tree(&dest, "claude/unrelated.txt", b"not ours");

        let planned = plan(&env, &dest).unwrap();
        assert!(
            planned.problems.iter().any(|p| p == "dest-not-empty"),
            "{planned:?}"
        );

        let mut marker = Marker::default();
        marker.trees.insert(
            "claude".into(),
            env.home.join(".claude").to_string_lossy().into_owned(),
        );
        marker.save(&dest).unwrap();
        let planned = plan(&env, &dest).unwrap();
        assert!(planned.problems.is_empty(), "{planned:?}");

        apply(&env, &dest).unwrap();
        assert!(
            !dest.join("claude").join("unrelated.txt").exists(),
            "our own earlier copy is brought in line with the source"
        );
        unlink_test_links(&env);
    }

    #[test]
    fn plan_flags_a_leftover_backup_and_a_link_elsewhere() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let claude = env.home.join(".claude");
        write_tree(&claude, "a.txt", b"a");
        write_tree(&backup_of(&claude), "old.txt", b"old");
        let planned = plan(&env, &tmp.path().join("offload")).unwrap();
        assert!(planned.problems.iter().any(|p| p == "backup-exists"));

        remove_tree(&backup_of(&claude)).unwrap();
        apply(&env, &tmp.path().join("first")).unwrap();
        let planned = plan(&env, &tmp.path().join("second")).unwrap();
        assert!(
            planned.problems.iter().any(|p| p == "linked-elsewhere"),
            "{planned:?}"
        );
        unlink_test_links(&env);
    }

    #[test]
    fn mirror_updates_changed_files_and_drops_removed_ones() {
        let tmp = tempfile::tempdir().unwrap();
        let src = tmp.path().join("src");
        let dst = tmp.path().join("dst");
        write_tree(&src, "keep.txt", b"same");
        write_tree(&src, "edit.txt", b"v1");
        write_tree(&src, "gone/x.txt", b"x");
        write_tree(&src, "ro.txt", b"ro");
        let mut perms = fs::metadata(src.join("ro.txt")).unwrap().permissions();
        perms.set_readonly(true);
        fs::set_permissions(src.join("ro.txt"), perms).unwrap();
        mirror(&src, &src, &dst).unwrap();
        assert_eq!(
            fs::metadata(dst.join("keep.txt"))
                .unwrap()
                .modified()
                .unwrap(),
            fs::metadata(src.join("keep.txt"))
                .unwrap()
                .modified()
                .unwrap(),
        );

        fs::write(src.join("edit.txt"), b"version-2").unwrap();
        remove_tree(&src.join("gone")).unwrap();
        remove_tree(&src.join("ro.txt")).unwrap();
        mirror(&src, &src, &dst).unwrap();
        assert_eq!(
            fs::read_to_string(dst.join("edit.txt")).unwrap(),
            "version-2"
        );
        assert!(!dst.join("gone").exists());
        assert!(
            !dst.join("ro.txt").exists(),
            "read-only files are removable"
        );
        assert!(dst.join("keep.txt").exists());
    }
}
