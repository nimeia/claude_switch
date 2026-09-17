//! Move Claude Code user data and this app's backup tree onto another drive.
//!
//! The original paths stay (`~/.claude`, `~/.claude-swap-backup`). After a
//! successful apply they become directory links (a junction on Windows, a
//! symlink elsewhere) pointing at `<dest>/claude` and `<dest>/swap`. That is
//! why this does **not** set `CLAUDE_CONFIG_DIR`: session isolation in this
//! app already uses that variable, and the VS Code lock files still write
//! under `~/.claude\ide`.
//!
//! Copy first, then rename the source aside and create the link. The renamed
//! copy is the rollback; deleting it is a separate step.

use std::fs;
use std::io;
use std::path::{Component, Path, PathBuf};

use serde::Serialize;

use crate::cleanup;
use crate::errors::{Error, Result};
use crate::paths::PathEnv;
use crate::session;

/// Sibling suffix for the pre-link original (`~/.claude.reloc-backup`).
pub const BACKUP_SUFFIX: &str = ".reloc-backup";

const TREE_CLAUDE: &str = "claude";
const TREE_SWAP: &str = "swap";
const DEST_CLAUDE: &str = "claude";
const DEST_SWAP: &str = "swap";
const LOCK_NAME: &str = ".lock";
const SPACE_MARGIN: u64 = 64 * 1024 * 1024;

#[cfg(windows)]
const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;

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
    pub bytes: u64,
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
    let scan = scan(env);
    let mut problems = Vec::new();
    let mut warnings = Vec::new();

    if dest_root.file_name().is_none() {
        problems.push("drive-root".into());
    }

    let mut trees = Vec::new();
    let mut copy_bytes = 0u64;
    for spec in specs(env) {
        let scanned = scan_tree(&spec);
        if scanned.exists && is_under(&dest_root, &spec.source) {
            problems.push("dest-inside-source".into());
        }
        let dest = dest_root.join(spec.dest_name);
        let skip = scanned.exists && scanned.linked && links_to(&spec.source, &dest);
        if !skip && scanned.exists {
            copy_bytes = copy_bytes.saturating_add(scanned.bytes);
        }
        trees.push(RelocatePlanTree {
            id: spec.id.to_string(),
            source: scanned.source.clone(),
            dest: dest.to_string_lossy().into_owned(),
            backup: scanned.backup_path.clone(),
            bytes: scanned.bytes,
            skip,
        });
    }

    let needs_work = trees
        .iter()
        .any(|t| !t.skip && Path::new(&t.source).exists());
    if !needs_work && trees.iter().all(|t| !t.skip) {
        problems.push("nothing".into());
    }

    let same_volume = trees
        .iter()
        .any(|t| Path::new(&t.source).exists() && same_volume(Path::new(&t.source), &dest_root));
    if same_volume {
        warnings.push("same-volume".into());
    }

    let dest_free_bytes = disk_free(&dest_root);
    if let Some(free) = dest_free_bytes {
        let needed = copy_bytes.saturating_add(SPACE_MARGIN);
        if copy_bytes > 0 && free < needed {
            problems.push("not-enough-space".into());
        }
    }

    if scan.live_sessions > 0 {
        problems.push("live-sessions".into());
    }

    problems.sort();
    problems.dedup();

    Ok(RelocatePlan {
        dest_root: dest_root.to_string_lossy().into_owned(),
        dest_free_bytes,
        same_volume,
        live_sessions: scan.live_sessions,
        claude_config_dir: scan.claude_config_dir,
        trees,
        copy_bytes,
        warnings,
        problems,
    })
}

/// Copy each tree, then replace the original path with a link.
pub fn apply(env: &PathEnv, dest_root: &Path) -> Result<RelocateApply> {
    let planned = plan(env, dest_root)?;
    if let Some(code) = planned.problems.first() {
        return Err(apply_problem(code, &planned));
    }
    let dest_root = PathBuf::from(&planned.dest_root);
    fs::create_dir_all(&dest_root).map_err(Error::Io)?;

    let mut copied = Vec::new();
    let mut skipped = Vec::new();
    for tree in &planned.trees {
        if tree.skip {
            skipped.push(tree.id.clone());
            continue;
        }
        let source = PathBuf::from(&tree.source);
        if !source.exists() {
            skipped.push(tree.id.clone());
            continue;
        }
        if is_dir_link(&source) {
            return Err(Error::Migration(format!(
                "{} is already a link, but not to {}",
                tree.id, tree.dest
            )));
        }
        let dest = PathBuf::from(&tree.dest);
        copy_tree(&source, &dest)
            .map_err(|e| Error::Migration(format!("copy {}: {e}", tree.id)))?;
        copied.push(tree.id.clone());
    }
    tighten_acl(&dest_root);

    let mut linked = Vec::new();
    for tree in &planned.trees {
        if tree.skip {
            continue;
        }
        let source = PathBuf::from(&tree.source);
        if !source.exists() && !Path::new(&tree.dest).exists() {
            continue;
        }
        if !source.exists() && Path::new(&tree.dest).exists() {
            // Copy landed; original vanished (unusual). Still create the link.
        }
        cutover_tree(&source, Path::new(&tree.dest), Path::new(&tree.backup))
            .map_err(|e| Error::Migration(format!("link {}: {e}", tree.id)))?;
        linked.push(tree.id.clone());
    }

    Ok(RelocateApply {
        copied,
        linked,
        skipped,
    })
}

/// Point the original paths back at the C: copies, if those backups exist.
pub fn restore(env: &PathEnv) -> Result<RelocateRestore> {
    let mut restored = Vec::new();
    for spec in specs(env) {
        if !is_dir_link(&spec.source) {
            continue;
        }
        if !spec.backup.exists() {
            return Err(Error::Migration(format!(
                "cannot restore {}: backup {} is missing",
                spec.id,
                spec.backup.display()
            )));
        }
        remove_dir_link(&spec.source).map_err(Error::Io)?;
        fs::rename(&spec.backup, &spec.source).map_err(Error::Io)?;
        restored.push(spec.id.to_string());
    }
    if restored.is_empty() {
        return Err(Error::Validation("relocate: not-linked".into()));
    }
    Ok(RelocateRestore { restored })
}

/// Delete the pre-link originals. Refused unless the live path is still a link.
pub fn delete_backups(env: &PathEnv) -> Result<RelocateDeleteBackups> {
    let mut deleted = Vec::new();
    let mut bytes = 0u64;
    for spec in specs(env) {
        if !spec.backup.exists() {
            continue;
        }
        if !is_dir_link(&spec.source) {
            return Err(Error::Validation("relocate: not-linked".into()));
        }
        let size = dir_size(&spec.backup);
        remove_tree(&spec.backup).map_err(Error::Io)?;
        bytes = bytes.saturating_add(size);
        deleted.push(spec.id.to_string());
    }
    if deleted.is_empty() {
        return Err(Error::Validation("relocate: no-backup".into()));
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
    let exists = spec.source.exists() || is_dir_link(&spec.source);
    let linked = is_dir_link(&spec.source);
    let link_target = linked
        .then(|| link_target(&spec.source))
        .flatten()
        .map(|p| p.to_string_lossy().into_owned());
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

fn cutover_tree(source: &Path, dest: &Path, backup: &Path) -> io::Result<()> {
    if is_dir_link(source) {
        if links_to(source, dest) {
            return Ok(());
        }
        return Err(io::Error::new(
            io::ErrorKind::AlreadyExists,
            "source is a link to a different location",
        ));
    }
    if !dest.exists() {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            "destination tree is missing",
        ));
    }
    let dest_abs = fs::canonicalize(dest)?;
    if source.exists() {
        if backup.exists() {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                "backup from a previous move is still there",
            ));
        }
        fs::rename(source, backup)?;
    }
    match create_dir_link(source, &dest_abs) {
        Ok(()) => Ok(()),
        Err(e) => {
            if backup.exists() && !source.exists() {
                let _ = fs::rename(backup, source);
            }
            Err(e)
        }
    }
}

fn copy_tree(src: &Path, dst: &Path) -> io::Result<()> {
    let meta = fs::symlink_metadata(src)?;
    if is_reparse(&meta) {
        return Err(io::Error::other("refusing to copy a link as a source tree"));
    }
    copy_nofollow(src, dst)
}

fn copy_nofollow(src: &Path, dst: &Path) -> io::Result<()> {
    let meta = fs::symlink_metadata(src)?;
    if is_reparse(&meta) {
        return Ok(());
    }
    if meta.is_dir() {
        fs::create_dir_all(dst)?;
        for entry in fs::read_dir(src)? {
            let entry = entry?;
            let name = entry.file_name();
            if name == LOCK_NAME {
                continue;
            }
            copy_nofollow(&entry.path(), &dst.join(name))?;
        }
        Ok(())
    } else {
        if let Some(parent) = dst.parent() {
            fs::create_dir_all(parent)?;
        }
        fs::copy(src, dst).map(|_| ())
    }
}

fn remove_tree(path: &Path) -> io::Result<()> {
    let meta = match fs::symlink_metadata(path) {
        Ok(m) => m,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(e),
    };
    if is_reparse(&meta) {
        return remove_dir_link(path);
    }
    if meta.is_dir() {
        fs::remove_dir_all(path)
    } else {
        fs::remove_file(path)
    }
}

fn tighten_acl(dest_root: &Path) {
    #[cfg(windows)]
    {
        let Ok(user) = std::env::var("USERNAME") else {
            return;
        };
        if user.is_empty() {
            return;
        }
        let grant = format!("{user}:(OI)(CI)F");
        let _ = std::process::Command::new("icacls")
            .arg(dest_root)
            .arg("/inheritance:r")
            .arg("/grant:r")
            .arg(&grant)
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status();
    }
    #[cfg(not(windows))]
    {
        let _ = dest_root;
    }
}

fn dir_size(path: &Path) -> u64 {
    let Ok(meta) = fs::symlink_metadata(path) else {
        return 0;
    };
    if is_reparse(&meta) {
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
        if is_reparse(&meta) {
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

fn is_dir_link(path: &Path) -> bool {
    fs::symlink_metadata(path).is_ok_and(|m| is_reparse(&m))
}

fn is_reparse(meta: &fs::Metadata) -> bool {
    if meta.file_type().is_symlink() {
        return true;
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        meta.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
    }
    #[cfg(not(windows))]
    {
        false
    }
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
        _ => {
            #[cfg(windows)]
            {
                a.to_string_lossy()
                    .eq_ignore_ascii_case(&b.to_string_lossy())
            }
            #[cfg(not(windows))]
            {
                a == b
            }
        }
    }
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
        Some(String::from("/"))
    }
}

fn disk_free(path: &Path) -> Option<u64> {
    let probe = if path.exists() {
        path.to_path_buf()
    } else {
        path.ancestors()
            .find(|a| a.exists() && a.parent().is_some())
            .or_else(|| path.ancestors().find(|a| a.exists()))
            .map(Path::to_path_buf)?
    };
    disk_free_existing(&probe)
}

#[cfg(windows)]
fn disk_free_existing(path: &Path) -> Option<u64> {
    use std::os::windows::ffi::OsStrExt;
    #[link(name = "kernel32")]
    extern "system" {
        fn GetDiskFreeSpaceExW(
            directory: *const u16,
            available: *mut u64,
            total: *mut u64,
            free: *mut u64,
        ) -> i32;
    }
    let wide: Vec<u16> = path.as_os_str().encode_wide().chain(Some(0)).collect();
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
        assert!(!is_dir_link(&claude));
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
    fn apply_stops_when_a_claude_session_is_live() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let sessions = env.home.join(".claude").join("sessions");
        fs::create_dir_all(&sessions).unwrap();
        let pid = std::process::id();
        fs::write(
            sessions.join(format!("{pid}.json")),
            json!({ "pid": pid }).to_string(),
        )
        .unwrap();
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
}
