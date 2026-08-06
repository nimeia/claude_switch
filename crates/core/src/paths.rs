//! Path resolution for Claude Code config and claude-swap-compatible backups.
//!
//! Mirrors claude-code's own resolution so we read/write the same files
//! (ref: `paths.py`). Key rules:
//!
//! - Config home: `CLAUDE_CONFIG_DIR` if set and non-empty, else `~/.claude`.
//! - Global config: `<config_home>/.config.json` if it exists (legacy),
//!   otherwise `(CLAUDE_CONFIG_DIR || $HOME)/.claude.json`.
//! - Credentials: `<config_home>/.credentials.json`.
//! - Backup root: XDG on Linux/WSL; `~/.claude-swap-backup` elsewhere.

use std::env;
use std::fs;
use std::io;
use std::path::{Component, Path, PathBuf};

use crate::errors::{Error, Result};
use crate::models::Platform;

/// Legacy (pre-XDG) backup directory name under `$HOME`.
pub const LEGACY_BACKUP_DIRNAME: &str = ".claude-swap-backup";

/// Names that prior runs may create without user data — migration treats a
/// target containing only these as empty (ref: `paths.py` `_THROWAWAY_*`).
const THROWAWAY_NAMES: &[&str] = &["cache"];
const THROWAWAY_PREFIXES: &[&str] = &["claude-swap.log"];

/// Injectable environment for path resolution (production + unit tests).
#[derive(Clone, Debug)]
pub struct PathEnv {
    pub home: PathBuf,
    /// `CLAUDE_CONFIG_DIR` when set and non-empty.
    pub claude_config_dir: Option<PathBuf>,
    /// Raw `XDG_DATA_HOME` (may be empty, relative, or contain `~`).
    pub xdg_data_home: Option<String>,
    pub platform: Platform,
}

impl PathEnv {
    /// Snapshot from the real process environment.
    #[must_use]
    pub fn from_process() -> Self {
        Self {
            home: process_home(),
            claude_config_dir: non_empty_env_path("CLAUDE_CONFIG_DIR"),
            xdg_data_home: env::var("XDG_DATA_HOME").ok(),
            platform: Platform::detect(),
        }
    }

    /// Build an isolated env for tests (home-only, host platform by default).
    #[must_use]
    pub fn isolated(home: impl Into<PathBuf>) -> Self {
        Self {
            home: home.into(),
            claude_config_dir: None,
            xdg_data_home: None,
            platform: Platform::detect(),
        }
    }

    /// Claude config home: `CLAUDE_CONFIG_DIR` or `~/.claude`.
    #[must_use]
    pub fn claude_config_home(&self) -> PathBuf {
        self.claude_config_dir
            .clone()
            .unwrap_or_else(|| self.home.join(".claude"))
    }

    /// Global Claude config file path (legacy `.config.json` if present).
    #[must_use]
    pub fn global_config_path(&self) -> PathBuf {
        let legacy = self.claude_config_home().join(".config.json");
        if legacy.exists() {
            return legacy;
        }
        let base = self
            .claude_config_dir
            .clone()
            .unwrap_or_else(|| self.home.clone());
        base.join(".claude.json")
    }

    /// Global config of the *default* profile — ignores `CLAUDE_CONFIG_DIR`
    /// (ref: `get_default_global_config_path`).
    #[must_use]
    pub fn default_global_config_path(&self) -> PathBuf {
        let legacy = self.home.join(".claude").join(".config.json");
        if legacy.exists() {
            return legacy;
        }
        self.home.join(".claude.json")
    }

    /// Active credentials file: `<config_home>/.credentials.json`.
    #[must_use]
    pub fn credentials_path(&self) -> PathBuf {
        self.claude_config_home().join(".credentials.json")
    }

    /// Legacy backup root: `~/.claude-swap-backup`.
    #[must_use]
    pub fn legacy_backup_root(&self) -> PathBuf {
        self.home.join(LEGACY_BACKUP_DIRNAME)
    }

    /// Platform backup root (XDG on Linux/WSL, legacy elsewhere).
    #[must_use]
    pub fn backup_root(&self) -> PathBuf {
        if self.platform.uses_xdg_backup() {
            if let Some(ref xdg) = self.xdg_data_home {
                if !xdg.is_empty() {
                    let expanded = expand_user_path(xdg, &self.home);
                    if expanded.is_absolute() {
                        return expanded.join("claude-swap");
                    }
                }
            }
            return self.home.join(".local").join("share").join("claude-swap");
        }
        self.legacy_backup_root()
    }
}

/// Resolved path bundle used by the rest of the core (constructed at init).
#[derive(Clone, Debug)]
pub struct Paths {
    pub env: PathEnv,
    pub claude_config_home: PathBuf,
    pub global_config: PathBuf,
    pub credentials: PathBuf,
    pub backup_root: PathBuf,
    pub credentials_dir: PathBuf,
    pub configs_dir: PathBuf,
    pub cache_dir: PathBuf,
    pub stash_dir: PathBuf,
    pub lock_file: PathBuf,
    pub sequence_file: PathBuf,
    pub settings_file: PathBuf,
    pub autoswitch_state_file: PathBuf,
}

impl Paths {
    /// Resolve paths from `env` **without** running legacy migration.
    #[must_use]
    pub fn resolve(env: PathEnv) -> Self {
        let backup_root = env.backup_root();
        Self {
            claude_config_home: env.claude_config_home(),
            global_config: env.global_config_path(),
            credentials: env.credentials_path(),
            credentials_dir: backup_root.join("credentials"),
            configs_dir: backup_root.join("configs"),
            cache_dir: backup_root.join("cache"),
            stash_dir: backup_root.join("stash"),
            lock_file: backup_root.join(".lock"),
            sequence_file: backup_root.join("sequence.json"),
            settings_file: backup_root.join("settings.json"),
            autoswitch_state_file: backup_root.join("autoswitch_state.json"),
            backup_root,
            env,
        }
    }

    /// Resolve paths and run legacy → XDG migration when needed
    /// (ref: `migrate_legacy_backup_dir` at init).
    ///
    /// # Errors
    ///
    /// [`Error::Migration`] on collision or move failure.
    pub fn resolve_and_migrate(env: PathEnv) -> Result<Self> {
        let legacy = env.legacy_backup_root();
        let target = env.backup_root();
        migrate_legacy_backup_dir(&legacy, &target)?;
        Ok(Self::resolve(env))
    }
}

// --- Process-level convenience wrappers (real env) -------------------------

#[must_use]
pub fn get_claude_config_home() -> PathBuf {
    PathEnv::from_process().claude_config_home()
}

#[must_use]
pub fn get_global_config_path() -> PathBuf {
    PathEnv::from_process().global_config_path()
}

#[must_use]
pub fn get_default_global_config_path() -> PathBuf {
    PathEnv::from_process().default_global_config_path()
}

#[must_use]
pub fn get_credentials_path() -> PathBuf {
    PathEnv::from_process().credentials_path()
}

#[must_use]
pub fn get_legacy_backup_root() -> PathBuf {
    PathEnv::from_process().legacy_backup_root()
}

#[must_use]
pub fn get_backup_root() -> PathBuf {
    PathEnv::from_process().backup_root()
}

/// Move the legacy backup directory to `target` if needed.
///
/// Crash-safe via `<target.parent>/.{target.name}.migrating` flag
/// (ref: `paths.py` `migrate_legacy_backup_dir`).
///
/// Returns `true` if a move ran in this call, `false` if it was a no-op.
///
/// # Errors
///
/// [`Error::Migration`] on genuine collision or filesystem failure.
pub fn migrate_legacy_backup_dir(legacy: &Path, target: &Path) -> Result<bool> {
    let same_path = paths_equal(legacy, target);
    if same_path {
        return Ok(false);
    }

    let flag = migration_flag_path(target);

    if !legacy.exists() {
        // Successful prior run that died before unlinking the flag.
        let _ = fs::remove_file(&flag);
        return Ok(false);
    }

    match migrate_inner(legacy, target, &flag) {
        Ok(moved) => Ok(moved),
        Err(e) => Err(Error::Migration(format!(
            "Migration of {} → {} failed: {e}",
            legacy.display(),
            target.display()
        ))),
    }
}

fn migrate_inner(legacy: &Path, target: &Path, flag: &Path) -> io::Result<bool> {
    if flag.exists() {
        // Prior run interrupted: discard any partial target and retry.
        if target.exists() {
            remove_path_all(target)?;
        }
    } else if target.exists() {
        if target_has_meaningful_data(target)? {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                format!(
                    "Both legacy ({}) and new ({}) backup paths exist. \
                     Refusing to merge or overwrite — inspect both and remove the \
                     stale one manually before re-running.",
                    legacy.display(),
                    target.display()
                ),
            ));
        }
        wipe_throwaway_artifacts(target)?;
    }

    if let Some(parent) = target.parent() {
        fs::create_dir_all(parent)?;
    }
    // Touch flag *before* the move.
    fs::write(flag, b"")?;
    move_path(legacy, target)?;
    let _ = fs::remove_file(flag);
    Ok(true)
}

fn migration_flag_path(target: &Path) -> PathBuf {
    let name = target
        .file_name()
        .map_or_else(|| "target".into(), |n| n.to_string_lossy().into_owned());
    let parent = target.parent().unwrap_or_else(|| Path::new("."));
    parent.join(format!(".{name}.migrating"))
}

fn target_has_meaningful_data(target: &Path) -> io::Result<bool> {
    let entries = match fs::read_dir(target) {
        Ok(rd) => rd,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(false),
        Err(e) => return Err(e),
    };
    for entry in entries {
        let entry = entry?;
        let name = entry.file_name();
        let name = name.to_string_lossy();
        if THROWAWAY_NAMES.contains(&name.as_ref()) {
            continue;
        }
        if THROWAWAY_PREFIXES.iter().any(|p| name.starts_with(p)) {
            continue;
        }
        return Ok(true);
    }
    Ok(false)
}

fn wipe_throwaway_artifacts(target: &Path) -> io::Result<()> {
    let entries = match fs::read_dir(target) {
        Ok(rd) => rd,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(e),
    };
    for entry in entries {
        let entry = entry?;
        let path = entry.path();
        remove_path_all(&path)?;
    }
    // Remove the now-empty directory so move can land on `target`.
    fs::remove_dir(target)?;
    Ok(())
}

fn remove_path_all(path: &Path) -> io::Result<()> {
    let meta = match fs::symlink_metadata(path) {
        Ok(m) => m,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(e),
    };
    if meta.is_dir() && !meta.file_type().is_symlink() {
        fs::remove_dir_all(path)
    } else {
        fs::remove_file(path)
    }
}

/// Move a file or directory to `dst` (same-FS rename, else copy + delete).
fn move_path(src: &Path, dst: &Path) -> io::Result<()> {
    match fs::rename(src, dst) {
        Ok(()) => Ok(()),
        Err(e) if is_cross_device(&e) || e.kind() == io::ErrorKind::AlreadyExists => {
            // Fall back to recursive copy + remove (shutil.move semantics).
            copy_recursive(src, dst)?;
            remove_path_all(src)?;
            Ok(())
        }
        Err(e) => {
            // Some platforms return Other for EXDEV — try copy fallback when
            // rename failed and src still exists.
            if src.exists() && !dst.exists() {
                copy_recursive(src, dst)?;
                remove_path_all(src)?;
                Ok(())
            } else {
                Err(e)
            }
        }
    }
}

fn is_cross_device(e: &io::Error) -> bool {
    // Windows ERROR_NOT_SAME_DEVICE = 17; Unix EXDEV = 18.
    // Avoid `ErrorKind::CrossesDevices` (stable since 1.85; MSRV is 1.80).
    matches!(e.raw_os_error(), Some(17 | 18))
}

fn copy_recursive(src: &Path, dst: &Path) -> io::Result<()> {
    let meta = fs::symlink_metadata(src)?;
    if meta.is_dir() && !meta.file_type().is_symlink() {
        fs::create_dir_all(dst)?;
        for entry in fs::read_dir(src)? {
            let entry = entry?;
            copy_recursive(&entry.path(), &dst.join(entry.file_name()))?;
        }
        Ok(())
    } else if meta.file_type().is_symlink() {
        // Best-effort: copy target contents rather than recreating the link.
        if let Ok(target) = fs::read_link(src) {
            if target.is_absolute() && target.exists() {
                return copy_recursive(&target, dst);
            }
        }
        fs::copy(src, dst).map(|_| ())
    } else {
        if let Some(parent) = dst.parent() {
            fs::create_dir_all(parent)?;
        }
        fs::copy(src, dst).map(|_| ())
    }
}

fn paths_equal(a: &Path, b: &Path) -> bool {
    match (fs::canonicalize(a), fs::canonicalize(b)) {
        (Ok(ca), Ok(cb)) => ca == cb,
        _ => a == b,
    }
}

/// Expand a leading `~` or `~/…` using `home` (ref: `os.path.expanduser`).
fn expand_user_path(raw: &str, home: &Path) -> PathBuf {
    if raw == "~" {
        return home.to_path_buf();
    }
    if let Some(rest) = raw.strip_prefix("~/") {
        return home.join(rest);
    }
    if let Some(rest) = raw.strip_prefix("~\\") {
        return home.join(rest);
    }
    PathBuf::from(raw)
}

fn non_empty_env_path(key: &str) -> Option<PathBuf> {
    env::var_os(key).and_then(|v| {
        if v.is_empty() {
            None
        } else {
            Some(PathBuf::from(v))
        }
    })
}

fn process_home() -> PathBuf {
    // Prefer USERPROFILE on Windows (matches Python Path.home()); then HOME.
    if cfg!(windows) {
        if let Ok(p) = env::var("USERPROFILE") {
            if !p.is_empty() {
                return PathBuf::from(p);
            }
        }
    }
    if let Ok(p) = env::var("HOME") {
        if !p.is_empty() {
            return PathBuf::from(p);
        }
    }
    env::var_os("USERPROFILE")
        .map(PathBuf::from)
        .or_else(|| env::var_os("HOME").map(PathBuf::from))
        .unwrap_or_else(|| PathBuf::from("."))
}

/// Normalize a user-supplied path: expand `~`, make absolute against `cwd`/`home`.
#[must_use]
#[allow(dead_code)] // used by mappings in a later PR
pub fn normalize_user_path(raw: &Path, home: &Path, cwd: &Path) -> PathBuf {
    let s = raw.to_string_lossy();
    let expanded = expand_user_path(&s, home);
    if expanded.is_absolute() {
        expanded
    } else {
        cwd.join(expanded)
    }
}

/// Return true if `path` has no `..` components after normalization.
#[must_use]
#[allow(dead_code)]
pub fn is_lexically_safe(path: &Path) -> bool {
    !path.components().any(|c| matches!(c, Component::ParentDir))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::Platform;

    fn linux_env(home: &Path) -> PathEnv {
        PathEnv {
            home: home.to_path_buf(),
            claude_config_dir: None,
            xdg_data_home: None,
            platform: Platform::Linux,
        }
    }

    #[test]
    fn config_home_default() {
        let home = tempfile::tempdir().unwrap();
        let env = PathEnv::isolated(home.path());
        assert_eq!(env.claude_config_home(), home.path().join(".claude"));
    }

    #[test]
    fn config_home_respects_ccd() {
        let home = tempfile::tempdir().unwrap();
        let custom = home.path().join("custom-claude");
        let mut env = PathEnv::isolated(home.path());
        env.claude_config_dir = Some(custom.clone());
        assert_eq!(env.claude_config_home(), custom);
    }

    #[test]
    fn global_config_default_is_homedir_claude_json() {
        let home = tempfile::tempdir().unwrap();
        let env = PathEnv::isolated(home.path());
        assert_eq!(env.global_config_path(), home.path().join(".claude.json"));
    }

    #[test]
    fn global_config_ccd() {
        let home = tempfile::tempdir().unwrap();
        let custom = home.path().join("ccd");
        fs::create_dir_all(&custom).unwrap();
        let mut env = PathEnv::isolated(home.path());
        env.claude_config_dir = Some(custom.clone());
        assert_eq!(env.global_config_path(), custom.join(".claude.json"));
    }

    #[test]
    fn legacy_config_json_takes_precedence() {
        let home = tempfile::tempdir().unwrap();
        let config_home = home.path().join(".claude");
        fs::create_dir_all(&config_home).unwrap();
        let legacy = config_home.join(".config.json");
        fs::write(&legacy, b"{}").unwrap();
        let env = PathEnv::isolated(home.path());
        assert_eq!(env.global_config_path(), legacy);
    }

    #[test]
    fn legacy_config_json_in_ccd() {
        let home = tempfile::tempdir().unwrap();
        let custom = home.path().join("ccd");
        fs::create_dir_all(&custom).unwrap();
        let legacy = custom.join(".config.json");
        fs::write(&legacy, b"{}").unwrap();
        let mut env = PathEnv::isolated(home.path());
        env.claude_config_dir = Some(custom);
        assert_eq!(env.global_config_path(), legacy);
    }

    #[test]
    fn credentials_default_and_ccd() {
        let home = tempfile::tempdir().unwrap();
        let env = PathEnv::isolated(home.path());
        assert_eq!(
            env.credentials_path(),
            home.path().join(".claude").join(".credentials.json")
        );
        let custom = home.path().join("ccd");
        let mut env = PathEnv::isolated(home.path());
        env.claude_config_dir = Some(custom.clone());
        assert_eq!(env.credentials_path(), custom.join(".credentials.json"));
    }

    #[test]
    fn backup_root_linux_xdg_default() {
        let home = tempfile::tempdir().unwrap();
        let env = linux_env(home.path());
        assert_eq!(
            env.backup_root(),
            home.path().join(".local").join("share").join("claude-swap")
        );
    }

    #[test]
    fn backup_root_linux_respects_xdg() {
        let home = tempfile::tempdir().unwrap();
        let custom = home.path().join("xdg");
        let mut env = linux_env(home.path());
        env.xdg_data_home = Some(custom.to_string_lossy().into_owned());
        assert_eq!(env.backup_root(), custom.join("claude-swap"));
    }

    #[test]
    fn backup_root_linux_ignores_empty_and_relative_xdg() {
        let home = tempfile::tempdir().unwrap();
        let expected = home.path().join(".local").join("share").join("claude-swap");

        let mut env = linux_env(home.path());
        env.xdg_data_home = Some(String::new());
        assert_eq!(env.backup_root(), expected);

        env.xdg_data_home = Some("relative/path".into());
        assert_eq!(env.backup_root(), expected);
    }

    #[test]
    fn backup_root_linux_expands_tilde_xdg() {
        let home = tempfile::tempdir().unwrap();
        let mut env = linux_env(home.path());
        env.xdg_data_home = Some("~/custom-data".into());
        assert_eq!(
            env.backup_root(),
            home.path().join("custom-data").join("claude-swap")
        );
    }

    #[test]
    fn backup_root_wsl_uses_xdg() {
        let home = tempfile::tempdir().unwrap();
        let mut env = linux_env(home.path());
        env.platform = Platform::Wsl;
        assert_eq!(
            env.backup_root(),
            home.path().join(".local").join("share").join("claude-swap")
        );
    }

    #[test]
    fn backup_root_macos_windows_legacy() {
        let home = tempfile::tempdir().unwrap();
        for platform in [Platform::MacOs, Platform::Windows, Platform::Unknown] {
            let mut env = PathEnv::isolated(home.path());
            env.platform = platform;
            assert_eq!(
                env.backup_root(),
                home.path().join(LEGACY_BACKUP_DIRNAME),
                "platform={platform:?}"
            );
        }
    }

    #[test]
    fn migrate_no_legacy_is_noop() {
        let home = tempfile::tempdir().unwrap();
        let target = home.path().join(".local").join("share").join("claude-swap");
        assert!(
            !migrate_legacy_backup_dir(&home.path().join(LEGACY_BACKUP_DIRNAME), &target).unwrap()
        );
        assert!(!target.exists());
    }

    #[test]
    fn migrate_target_equals_legacy_is_noop() {
        let home = tempfile::tempdir().unwrap();
        let legacy = home.path().join(LEGACY_BACKUP_DIRNAME);
        fs::create_dir_all(&legacy).unwrap();
        fs::write(legacy.join("marker"), b"keep me").unwrap();
        assert!(!migrate_legacy_backup_dir(&legacy, &legacy).unwrap());
        assert_eq!(
            fs::read_to_string(legacy.join("marker")).unwrap(),
            "keep me"
        );
    }

    #[test]
    fn migrate_moves_legacy_to_target() {
        let home = tempfile::tempdir().unwrap();
        let legacy = home.path().join(LEGACY_BACKUP_DIRNAME);
        fs::create_dir_all(legacy.join("configs")).unwrap();
        fs::write(legacy.join("sequence.json"), b"{\"k\": 1}").unwrap();
        fs::write(legacy.join("configs").join("x.json"), b"{}").unwrap();

        let target = home.path().join(".local").join("share").join("claude-swap");
        assert!(migrate_legacy_backup_dir(&legacy, &target).unwrap());
        assert!(!legacy.exists());
        assert_eq!(
            fs::read_to_string(target.join("sequence.json")).unwrap(),
            "{\"k\": 1}"
        );
        assert_eq!(
            fs::read_to_string(target.join("configs").join("x.json")).unwrap(),
            "{}"
        );
    }

    #[test]
    fn migrate_collision_raises() {
        let home = tempfile::tempdir().unwrap();
        let legacy = home.path().join(LEGACY_BACKUP_DIRNAME);
        fs::create_dir_all(&legacy).unwrap();
        fs::write(legacy.join("sequence.json"), b"{\"src\": \"legacy\"}").unwrap();

        let target = home.path().join(".local").join("share").join("claude-swap");
        fs::create_dir_all(&target).unwrap();
        fs::write(target.join("sequence.json"), b"{\"src\": \"target\"}").unwrap();

        let err = migrate_legacy_backup_dir(&legacy, &target).unwrap_err();
        assert!(matches!(err, Error::Migration(_)));
        assert!(err.message().contains("Refusing to merge"));
        assert_eq!(
            fs::read_to_string(legacy.join("sequence.json")).unwrap(),
            "{\"src\": \"legacy\"}"
        );
        assert_eq!(
            fs::read_to_string(target.join("sequence.json")).unwrap(),
            "{\"src\": \"target\"}"
        );
    }

    #[test]
    fn migrate_wipes_throwaway_only_target() {
        let home = tempfile::tempdir().unwrap();
        let legacy = home.path().join(LEGACY_BACKUP_DIRNAME);
        fs::create_dir_all(&legacy).unwrap();
        fs::write(legacy.join("sequence.json"), b"{\"src\": \"legacy\"}").unwrap();

        let target = home.path().join(".local").join("share").join("claude-swap");
        fs::create_dir_all(target.join("cache")).unwrap();
        fs::write(target.join("cache").join("update_check.json"), b"{}").unwrap();
        fs::write(target.join("claude-swap.log"), b"noise").unwrap();
        fs::write(target.join("claude-swap.log.1"), b"rotated").unwrap();

        assert!(migrate_legacy_backup_dir(&legacy, &target).unwrap());
        assert!(!legacy.exists());
        assert_eq!(
            fs::read_to_string(target.join("sequence.json")).unwrap(),
            "{\"src\": \"legacy\"}"
        );
        assert!(!target.join("cache").exists());
        assert!(!target.join("claude-swap.log").exists());
    }

    #[test]
    fn migrate_real_data_with_artifacts_still_collides() {
        let home = tempfile::tempdir().unwrap();
        let legacy = home.path().join(LEGACY_BACKUP_DIRNAME);
        fs::create_dir_all(&legacy).unwrap();
        fs::write(legacy.join("sequence.json"), b"{\"src\": \"legacy\"}").unwrap();

        let target = home.path().join(".local").join("share").join("claude-swap");
        fs::create_dir_all(target.join("cache")).unwrap();
        fs::write(target.join("claude-swap.log"), b"noise").unwrap();
        fs::write(target.join("sequence.json"), b"{\"src\": \"target\"}").unwrap();

        let err = migrate_legacy_backup_dir(&legacy, &target).unwrap_err();
        assert!(matches!(err, Error::Migration(_)));
    }

    #[test]
    fn migrate_resumes_after_interrupted_move() {
        let home = tempfile::tempdir().unwrap();
        let legacy = home.path().join(LEGACY_BACKUP_DIRNAME);
        fs::create_dir_all(&legacy).unwrap();
        fs::write(legacy.join("sequence.json"), b"{\"src\": \"legacy\"}").unwrap();

        let target = home.path().join(".local").join("share").join("claude-swap");
        fs::create_dir_all(&target).unwrap();
        fs::write(target.join("stale-partial.json"), b"garbage").unwrap();
        let flag = target.parent().unwrap().join(".claude-swap.migrating");
        fs::write(&flag, b"").unwrap();

        assert!(migrate_legacy_backup_dir(&legacy, &target).unwrap());
        assert!(!legacy.exists());
        assert!(!flag.exists());
        assert!(!target.join("stale-partial.json").exists());
        assert_eq!(
            fs::read_to_string(target.join("sequence.json")).unwrap(),
            "{\"src\": \"legacy\"}"
        );
    }

    #[test]
    fn migrate_cleans_stale_flag_after_completed_move() {
        let home = tempfile::tempdir().unwrap();
        let target = home.path().join(".local").join("share").join("claude-swap");
        fs::create_dir_all(&target).unwrap();
        fs::write(target.join("sequence.json"), b"{\"complete\": true}").unwrap();
        let flag = target.parent().unwrap().join(".claude-swap.migrating");
        fs::write(&flag, b"").unwrap();

        let legacy = home.path().join(LEGACY_BACKUP_DIRNAME);
        assert!(!migrate_legacy_backup_dir(&legacy, &target).unwrap());
        assert!(!flag.exists());
        assert_eq!(
            fs::read_to_string(target.join("sequence.json")).unwrap(),
            "{\"complete\": true}"
        );
    }

    #[test]
    fn paths_resolve_and_migrate_wires_layout() {
        let home = tempfile::tempdir().unwrap();
        let env = linux_env(home.path());
        let legacy = env.legacy_backup_root();
        fs::create_dir_all(&legacy).unwrap();
        fs::write(legacy.join("sequence.json"), b"{}").unwrap();

        let paths = Paths::resolve_and_migrate(env.clone()).unwrap();
        assert_eq!(paths.backup_root, env.backup_root());
        assert!(paths.sequence_file.ends_with("sequence.json"));
        assert!(paths.credentials_dir.ends_with("credentials"));
        assert!(!legacy.exists());
        assert!(paths.sequence_file.exists() || paths.backup_root.join("sequence.json").exists());
    }

    #[test]
    fn expand_user_path_variants() {
        let home = PathBuf::from("/home/u");
        assert_eq!(expand_user_path("~", &home), home);
        assert_eq!(expand_user_path("~/data", &home), home.join("data"));
        assert_eq!(expand_user_path("/abs", &home), PathBuf::from("/abs"));
    }
}
