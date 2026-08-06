//! Filesystem primitives with no higher-level dependencies.
//!
//! Leaf module: atomic writes are used by settings, credentials, and paths
//! migration (ref: `fsutil.py`).

use std::fs::{self, File, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::thread;
use std::time::Duration;

/// Windows error codes that usually mean "someone else has the file open":
/// `ERROR_ACCESS_DENIED` (5), `ERROR_SHARING_VIOLATION` (32),
/// `ERROR_LOCK_VIOLATION` (33). AV/indexer contention clears within ms;
/// a persistent ACL denial still surfaces after the attempt budget.
const TRANSIENT_WIN_ERRORS: &[i32] = &[5, 32, 33];

/// `rename`/`replace` with retries past transient Windows sharing failures.
///
/// POSIX rename is atomic and never fails this way; on Windows Defender and the
/// search indexer open freshly-created files opportunistically (ref: `fsutil.py`
/// `replace_with_retry`, measured ~44% flaky replaces under scan).
///
/// # Errors
///
/// Returns the last I/O error if all attempts fail, or immediately for
/// non-transient errors. `attempts < 1` → `InvalidInput`.
pub fn replace_with_retry(src: &Path, dst: &Path, attempts: u32) -> io::Result<()> {
    if attempts < 1 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "attempts must be >= 1",
        ));
    }

    let mut delay = Duration::from_millis(2);
    let max_delay = Duration::from_millis(250);

    for attempt in 0..attempts {
        match fs::rename(src, dst) {
            Ok(()) => return Ok(()),
            Err(e) => {
                let transient = is_transient_replace_error(&e);
                if !transient || attempt + 1 == attempts {
                    return Err(e);
                }
                thread::sleep(delay);
                delay = (delay * 2).min(max_delay);
            }
        }
    }
    unreachable!("loop always returns")
}

fn is_transient_replace_error(e: &io::Error) -> bool {
    if !cfg!(windows) {
        return false;
    }
    e.raw_os_error()
        .is_some_and(|code| TRANSIENT_WIN_ERRORS.contains(&code))
}

/// Atomically write `data` to `path` (temp file in the same directory + replace).
///
/// On Unix, sets mode `0o600` after a successful replace (ref: credentials /
/// settings writers). On Windows, mode is a no-op.
///
/// # Errors
///
/// Propagates create/write/replace failures.
pub fn atomic_write(path: &Path, data: &[u8]) -> io::Result<()> {
    let parent = path
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .map_or_else(|| PathBuf::from("."), Path::to_path_buf);
    fs::create_dir_all(&parent)?;

    let mut tmp = tempfile::Builder::new()
        .prefix(".cswap-")
        .suffix(".tmp")
        .tempfile_in(&parent)?;
    tmp.write_all(data)?;
    tmp.flush()?;
    // Ensure contents hit disk before rename (best-effort on all platforms).
    tmp.as_file().sync_all()?;

    // Disable auto-delete so we can rename with replace_with_retry (Windows AV path).
    let (_file, tmp_owned) = tmp.keep().map_err(|e| e.error)?;

    match replace_with_retry(&tmp_owned, path, 10) {
        Ok(()) => {
            set_owner_secret_mode(path)?;
            Ok(())
        }
        Err(e) => {
            let _ = fs::remove_file(&tmp_owned);
            Err(e)
        }
    }
}

/// Set `0o600` on Unix; no-op on Windows (ref: `os.chmod` gated on non-win32).
pub fn set_owner_secret_mode(path: &Path) -> io::Result<()> {
    set_mode(path, 0o600)
}

/// Set `0o700` on a directory (Unix); no-op on Windows.
pub fn set_owner_private_dir_mode(path: &Path) -> io::Result<()> {
    set_mode(path, 0o700)
}

#[cfg(unix)]
fn set_mode(path: &Path, mode: u32) -> io::Result<()> {
    use std::os::unix::fs::PermissionsExt;
    let perms = fs::Permissions::from_mode(mode);
    fs::set_permissions(path, perms)
}

#[cfg(not(unix))]
#[allow(clippy::unnecessary_wraps)] // same signature as the unix implementation
fn set_mode(_path: &Path, _mode: u32) -> io::Result<()> {
    Ok(())
}

/// Create a directory (and parents) with private mode when possible.
pub fn ensure_dir(path: &Path) -> io::Result<()> {
    fs::create_dir_all(path)?;
    set_owner_private_dir_mode(path)
}

/// Open a file for exclusive create, or truncate if `truncate` is true.
#[allow(dead_code)] // used by later PRs
pub fn open_private_file(path: &Path, truncate: bool) -> io::Result<File> {
    let mut opts = OpenOptions::new();
    opts.write(true).create(true);
    if truncate {
        opts.truncate(true);
    }
    let file = opts.open(path)?;
    set_owner_secret_mode(path)?;
    Ok(file)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn replace_rejects_zero_attempts() {
        let dir = tempfile::tempdir().unwrap();
        let src = dir.path().join("a");
        let dst = dir.path().join("b");
        fs::write(&src, b"x").unwrap();
        let err = replace_with_retry(&src, &dst, 0).unwrap_err();
        assert_eq!(err.kind(), io::ErrorKind::InvalidInput);
        assert!(src.exists());
    }

    #[test]
    fn replace_moves_file() {
        let dir = tempfile::tempdir().unwrap();
        let src = dir.path().join("tmp.tmp");
        let dst = dir.path().join("target.json");
        fs::write(&src, b"payload").unwrap();
        replace_with_retry(&src, &dst, 10).unwrap();
        assert_eq!(fs::read_to_string(&dst).unwrap(), "payload");
        assert!(!src.exists());
    }

    #[test]
    fn atomic_write_roundtrip() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("nested").join("file.txt");
        atomic_write(&path, b"hello").unwrap();
        assert_eq!(fs::read_to_string(&path).unwrap(), "hello");
        atomic_write(&path, b"world").unwrap();
        assert_eq!(fs::read_to_string(&path).unwrap(), "world");
    }

    #[cfg(unix)]
    #[test]
    fn atomic_write_sets_0600() {
        use std::os::unix::fs::PermissionsExt;
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("secret");
        atomic_write(&path, b"s3cret").unwrap();
        let mode = fs::metadata(&path).unwrap().permissions().mode() & 0o777;
        assert_eq!(mode, 0o600);
    }

    /// Simulate flaky Windows replace without requiring a real winerror inject
    /// into `std::fs::rename` — unit-test the retry classifier instead.
    #[test]
    fn transient_classifier_windows_codes() {
        if cfg!(windows) {
            let e = io::Error::from_raw_os_error(5);
            assert!(is_transient_replace_error(&e));
            let e = io::Error::from_raw_os_error(32);
            assert!(is_transient_replace_error(&e));
            let e = io::Error::from_raw_os_error(2); // not found
            assert!(!is_transient_replace_error(&e));
        } else {
            let e = io::Error::from_raw_os_error(5);
            assert!(!is_transient_replace_error(&e));
        }
    }

    #[test]
    fn missing_source_fails_once_conceptually() {
        // Non-existent source: rename fails; we don't spin forever.
        let dir = tempfile::tempdir().unwrap();
        let src = dir.path().join("missing");
        let dst = dir.path().join("dst");
        let start = std::time::Instant::now();
        let err = replace_with_retry(&src, &dst, 3).unwrap_err();
        assert!(start.elapsed() < Duration::from_secs(2));
        assert!(err.kind() == io::ErrorKind::NotFound || err.raw_os_error().is_some());
    }
}
