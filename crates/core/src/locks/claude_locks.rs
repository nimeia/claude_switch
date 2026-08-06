//! proper-lockfile-compatible directory locks used by Claude Code
//! (ref: `claude_locks.py`).

use std::fs;
use std::path::{Path, PathBuf};
use std::thread;
use std::time::{Duration, Instant, SystemTime};

use crate::errors::{Error, Result};
use crate::paths::PathEnv;

/// Credential locks (oauth + legacy) use 60s staleness.
pub const CREDENTIALS_STALENESS_S: f64 = 60.0;
/// Config lock uses 10s staleness.
pub const CONFIG_STALENESS_S: f64 = 10.0;
/// Touch interval while held (CC uses 5s; we use 3s for margin).
pub const TOUCH_INTERVAL_S: f64 = 3.0;
/// Per-lock acquire budget.
pub const DEFAULT_LOCK_TIMEOUT_S: f64 = 9.0;

/// Legacy credential lock: `<config_home.parent>/<config_home.name>.lock`
/// (default `~/.claude.lock`).
#[must_use]
pub fn credentials_lock_dir(env: &PathEnv) -> PathBuf {
    let home = env.claude_config_home();
    let name = home
        .file_name()
        .map_or_else(|| "claude".into(), |n| n.to_string_lossy().into_owned());
    home.parent()
        .unwrap_or_else(|| Path::new("."))
        .join(format!("{name}.lock"))
}

/// Primary OAuth refresh lock: `<config_home>/.oauth_refresh.lock`.
#[must_use]
pub fn oauth_refresh_lock_dir(env: &PathEnv) -> PathBuf {
    env.claude_config_home().join(".oauth_refresh.lock")
}

/// Config lock: `<global_config.parent>/<global_config.name>.lock`.
#[must_use]
pub fn config_lock_dir(env: &PathEnv) -> PathBuf {
    let path = env.global_config_path();
    let name = path
        .file_name()
        .map_or_else(|| "config".into(), |n| n.to_string_lossy().into_owned());
    path.parent()
        .unwrap_or_else(|| Path::new("."))
        .join(format!("{name}.lock"))
}

/// Held proper-lockfile directory lock (releases on drop).
pub struct ProperLock {
    dir: PathBuf,
    touch_stop: Option<std::sync::mpsc::Sender<()>>,
    touch_join: Option<thread::JoinHandle<()>>,
}

impl ProperLock {
    /// Acquire a directory lock with staleness theft and background touch.
    pub fn acquire(lock_dir: PathBuf, timeout: Duration, staleness: Duration) -> Result<Self> {
        if let Some(parent) = lock_dir.parent() {
            fs::create_dir_all(parent).map_err(Error::Io)?;
        }
        let start = Instant::now();
        loop {
            match fs::create_dir(&lock_dir) {
                Ok(()) => break,
                Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {
                    if start.elapsed() > timeout {
                        return Err(Error::ClaudeCodeLockTimeout(format!(
                            "Could not acquire {} — Claude Code appears to be refreshing credentials. Retry in a few seconds.",
                            lock_dir
                                .file_name()
                                .map(|n| n.to_string_lossy())
                                .unwrap_or_default()
                        )));
                    }
                    // Steal if stale.
                    if let Ok(meta) = fs::metadata(&lock_dir) {
                        if let Ok(modified) = meta.modified() {
                            if SystemTime::now()
                                .duration_since(modified)
                                .unwrap_or(Duration::ZERO)
                                >= staleness
                            {
                                let _ = fs::remove_dir(&lock_dir);
                                continue;
                            }
                        }
                    }
                    thread::sleep(Duration::from_millis(50 + fastrand_u64() % 50));
                }
                Err(e) => return Err(Error::Io(e)),
            }
        }

        let (tx, rx) = std::sync::mpsc::channel::<()>();
        let touch_dir = lock_dir.clone();
        let join = thread::spawn(move || {
            let interval = Duration::from_secs_f64(TOUCH_INTERVAL_S);
            loop {
                match rx.recv_timeout(interval) {
                    Ok(()) | Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => break,
                    Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {
                        let _ = touch_mtime(&touch_dir);
                    }
                }
            }
        });

        Ok(Self {
            dir: lock_dir,
            touch_stop: Some(tx),
            touch_join: Some(join),
        })
    }

    fn release_inner(&mut self) {
        if let Some(tx) = self.touch_stop.take() {
            let _ = tx.send(());
        }
        if let Some(j) = self.touch_join.take() {
            let _ = j.join();
        }
        let _ = fs::remove_dir(&self.dir);
    }
}

impl Drop for ProperLock {
    fn drop(&mut self) {
        self.release_inner();
    }
}

fn touch_mtime(path: &Path) -> std::io::Result<()> {
    // Opening for write updates mtime on most platforms; fall back to write a marker.
    let marker = path.join(".touch");
    fs::write(&marker, b"t")?;
    let _ = fs::remove_file(&marker);
    Ok(())
}

fn fastrand_u64() -> u64 {
    use std::collections::hash_map::DefaultHasher;
    use std::hash::{Hash, Hasher};
    let mut h = DefaultHasher::new();
    Instant::now().hash(&mut h);
    h.finish()
}

/// Acquires `oauth_refresh` then legacy credential locks (CC order).
pub struct ClaudeCodeLocks {
    _oauth: ProperLock,
    _legacy: ProperLock,
}

impl ClaudeCodeLocks {
    pub fn acquire_credentials(env: &PathEnv, timeout: Duration) -> Result<Self> {
        let stale = Duration::from_secs_f64(CREDENTIALS_STALENESS_S);
        let oauth = ProperLock::acquire(oauth_refresh_lock_dir(env), timeout, stale)?;
        let legacy = ProperLock::acquire(credentials_lock_dir(env), timeout, stale)?;
        Ok(Self {
            _oauth: oauth,
            _legacy: legacy,
        })
    }

    pub fn acquire_config(env: &PathEnv, timeout: Duration) -> Result<ProperLock> {
        let stale = Duration::from_secs_f64(CONFIG_STALENESS_S);
        ProperLock::acquire(config_lock_dir(env), timeout, stale)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::Platform;
    use crate::paths::PathEnv;

    fn env_at(home: &Path) -> PathEnv {
        PathEnv {
            home: home.to_path_buf(),
            claude_config_dir: None,
            xdg_data_home: None,
            platform: Platform::Windows,
        }
    }

    #[test]
    fn lock_paths_default_home() {
        // Build expected paths with join() so separators match the host OS —
        // hard-coded `\` strings fail on Linux/macOS CI runners.
        let home = PathBuf::from("Users").join("test");
        let env = env_at(&home);
        assert_eq!(credentials_lock_dir(&env), home.join(".claude.lock"));
        assert_eq!(
            oauth_refresh_lock_dir(&env),
            home.join(".claude").join(".oauth_refresh.lock")
        );
        assert_eq!(config_lock_dir(&env), home.join(".claude.json.lock"));
    }

    #[test]
    fn lock_paths_claude_config_dir() {
        let home = PathBuf::from("Users").join("test");
        let mut env = env_at(&home);
        let ccd = PathBuf::from("c").join("claude");
        env.claude_config_dir = Some(ccd.clone());
        assert_eq!(
            credentials_lock_dir(&env),
            PathBuf::from("c").join("claude.lock")
        );
        assert_eq!(
            oauth_refresh_lock_dir(&env),
            ccd.join(".oauth_refresh.lock")
        );
        assert_eq!(config_lock_dir(&env), ccd.join(".claude.json.lock"));
    }

    #[test]
    fn lock_paths_legacy_config_json() {
        let dir = tempfile::tempdir().unwrap();
        let home = dir.path();
        let ccd = home.join("claude");
        fs::create_dir_all(&ccd).unwrap();
        fs::write(ccd.join(".config.json"), b"{}").unwrap();
        let mut env = env_at(home);
        env.claude_config_dir = Some(ccd.clone());
        assert_eq!(config_lock_dir(&env), ccd.join(".config.json.lock"));
    }

    #[test]
    fn proper_lock_exclusive() {
        let dir = tempfile::tempdir().unwrap();
        let lock = dir.path().join("mylock");
        let a = ProperLock::acquire(
            lock.clone(),
            Duration::from_secs(2),
            Duration::from_secs(60),
        )
        .unwrap();
        let err = ProperLock::acquire(lock, Duration::from_millis(200), Duration::from_secs(60));
        assert!(err.is_err());
        drop(a);
    }
}
