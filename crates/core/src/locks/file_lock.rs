//! Exclusive file lock via `fs4` (`LockFileEx` / flock).

use std::fs::{File, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::thread;
use std::time::{Duration, Instant};

use fs4::fs_std::FileExt;

use crate::errors::{Error, Result};

/// Cross-process exclusive lock on a regular file (ref: `locking.py` `FileLock`).
pub struct FileLock {
    path: PathBuf,
    timeout: Duration,
    file: Option<File>,
}

impl FileLock {
    #[must_use]
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self {
            path: path.into(),
            timeout: Duration::from_secs(10),
            file: None,
        }
    }

    #[must_use]
    pub fn with_timeout(mut self, timeout: Duration) -> Self {
        self.timeout = timeout;
        self
    }

    /// Acquire exclusive lock; returns `Err` on timeout.
    pub fn acquire(&mut self) -> Result<()> {
        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent).map_err(Error::Io)?;
        }
        let file = OpenOptions::new()
            .create(true)
            .write(true)
            .truncate(false)
            .open(&self.path)
            .map_err(Error::Io)?;

        let start = Instant::now();
        loop {
            match file.try_lock_exclusive() {
                Ok(()) => {
                    let _ = (&file).write_all(b"locked\n");
                    self.file = Some(file);
                    return Ok(());
                }
                Err(e) if is_would_block(&e) => {
                    if start.elapsed() >= self.timeout {
                        return Err(Error::LockTimeout(format!(
                            "Failed to acquire lock {} — another instance may be running",
                            self.path.display()
                        )));
                    }
                    thread::sleep(Duration::from_millis(100));
                }
                Err(e) => return Err(Error::Io(e)),
            }
        }
    }

    pub fn release(&mut self) {
        if let Some(file) = self.file.take() {
            // fs4's trait method, not `File::unlock` — the latter is newer than
            // this crate's MSRV and would silently raise the toolchain floor.
            let _ = fs4::fs_std::FileExt::unlock(&file);
            drop(file);
        }
    }
}

impl Drop for FileLock {
    fn drop(&mut self) {
        self.release();
    }
}

fn is_would_block(e: &io::Error) -> bool {
    matches!(
        e.kind(),
        io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut | io::ErrorKind::PermissionDenied
    ) || matches!(e.raw_os_error(), Some(11 | 32 | 33 | 35 | 36))
}

/// Path helper for tests / docs.
#[must_use]
#[allow(dead_code)]
pub fn lock_path_for(backup_root: &Path) -> PathBuf {
    backup_root.join(".lock")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn acquire_release_roundtrip() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(".lock");
        let mut a = FileLock::new(&path).with_timeout(Duration::from_secs(2));
        a.acquire().unwrap();
        a.release();
        let mut b = FileLock::new(&path).with_timeout(Duration::from_secs(2));
        b.acquire().unwrap();
    }

    #[test]
    fn second_holder_times_out() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(".lock");
        let mut a = FileLock::new(&path).with_timeout(Duration::from_secs(5));
        a.acquire().unwrap();
        let mut b = FileLock::new(&path).with_timeout(Duration::from_millis(300));
        let err = b.acquire().unwrap_err();
        assert!(matches!(err, Error::LockTimeout(_)));
        a.release();
    }
}
