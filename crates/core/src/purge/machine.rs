//! A narrow, stateless retry for the elevated helper. No account stores or
//! caller-supplied paths are opened: only named children of Windows `ProgramData`.

use super::{action, failure_reason, fs, io, is_reparse, item_bytes, remove_candidate};
use super::{Candidate, Error, Operation, Path, PathBuf, PurgeOutcome, Result, GROUP_DESKTOP_DATA};
use serde::Deserialize;

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum Target {
    DesktopData,
    DesktopLogs,
    Managed,
    ManagedSpaced,
}

impl Target {
    fn key(self) -> &'static str {
        match self {
            Self::DesktopData => "desktop-data",
            Self::DesktopLogs => "desktop-logs",
            Self::Managed => "managed",
            Self::ManagedSpaced => "managed-spaced",
        }
    }

    fn relative(self) -> &'static str {
        match self {
            Self::DesktopData => "Claude",
            Self::DesktopLogs => "Claude/Logs",
            Self::Managed => "ClaudeCode",
            Self::ManagedSpaced => "Claude Code",
        }
    }
}

/// Retry only explicitly named machine-wide data. The helper must obtain UAC
/// consent before calling this; this function does not elevate or change ACLs.
pub fn apply(targets: &[Target]) -> Result<PurgeOutcome> {
    if targets.is_empty() || targets.len() > 4 {
        return Err(Error::Validation(
            "invalid machine cleanup selection".into(),
        ));
    }
    let root = program_data().map_err(|e| Error::Validation(e.to_string()))?;
    let names = super::residuals::running_process_names()
        .map_err(|e| Error::Validation(format!("process-check-failed: {e}")))?;
    if names.iter().any(|n| n == "claude.exe") {
        return Err(Error::SessionInUse("claude-running".into()));
    }
    apply_at(&root, targets).map_err(|e| Error::Validation(e.to_string()))
}

fn apply_at(root: &Path, targets: &[Target]) -> io::Result<PurgeOutcome> {
    if !root.is_absolute() || root.parent().is_none() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid ProgramData root",
        ));
    }
    // Pin the parent chain without write/delete sharing. A directory cannot be
    // replaced or changed into a junction while the elevated delete uses it.
    let _parents = pin_parents(root)?;
    let mut out = PurgeOutcome {
        deleted: vec![],
        skipped: vec![],
        failed: vec![],
        bytes: 0,
    };
    let mut seen = Vec::new();
    for &target in targets {
        if seen.contains(&target)
            || (target == Target::DesktopLogs && targets.contains(&Target::DesktopData))
        {
            continue;
        }
        seen.push(target);
        let candidate = Candidate {
            id: target.key().into(),
            group: GROUP_DESKTOP_DATA,
            option: None,
            path: root.join(target.relative()),
            follow_link: false,
            operation: Operation::Remove,
        };
        let result: io::Result<u64> = (|| {
            // Claude is an extra parent of Claude/Logs; never follow a link here.
            let _nested = if target == Target::DesktopLogs {
                Some(pin_directory(&root.join("Claude"))?)
            } else {
                None
            };
            let bytes = item_bytes(&candidate);
            remove_candidate(&candidate)?;
            Ok(bytes)
        })();
        match result {
            Ok(bytes) => {
                out.bytes = out.bytes.saturating_add(bytes);
                out.deleted.push(action(&candidate, None));
            }
            Err(e) if e.kind() == io::ErrorKind::NotFound => {
                out.skipped.push(action(&candidate, Some("missing")));
            }
            Err(e) => out
                .failed
                .push(action(&candidate, Some(&failure_reason(&e)))),
        }
    }
    Ok(out)
}

fn pin_parents(path: &Path) -> io::Result<Vec<fs::File>> {
    let mut parents: Vec<_> = path.ancestors().collect();
    parents.reverse();
    parents.into_iter().map(pin_directory).collect()
}

fn pin_directory(path: &Path) -> io::Result<fs::File> {
    let mut options = fs::OpenOptions::new();
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        options
            .access_mode(0x8000_0000) // GENERIC_READ; participates in sharing checks
            .share_mode(0x1) // FILE_SHARE_READ (no WRITE or DELETE)
            .custom_flags(0x0220_0000); // BACKUP_SEMANTICS | OPEN_REPARSE_POINT
    }
    #[cfg(not(windows))]
    options.read(true);
    let handle = options.open(path)?;
    let metadata = handle.metadata()?;
    if !metadata.is_dir()
        || is_reparse(&metadata)
        || fs::symlink_metadata(path)?.file_type().is_symlink()
    {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "linked-parent"));
    }
    Ok(handle)
}

#[cfg(windows)]
fn program_data() -> io::Result<PathBuf> {
    use std::os::windows::ffi::OsStringExt;
    #[link(name = "shell32")]
    extern "system" {
        fn SHGetFolderPathW(
            window: *mut std::ffi::c_void,
            folder: i32,
            token: *mut std::ffi::c_void,
            flags: u32,
            path: *mut u16,
        ) -> i32;
    }
    let mut buffer = [0u16; 260];
    let result = unsafe {
        SHGetFolderPathW(
            std::ptr::null_mut(),
            0x23,
            std::ptr::null_mut(),
            0,
            buffer.as_mut_ptr(),
        )
    };
    if result < 0 {
        return Err(io::Error::other(format!(
            "cannot resolve ProgramData: {result:#x}"
        )));
    }
    let len = buffer.iter().position(|&c| c == 0).unwrap_or(buffer.len());
    Ok(PathBuf::from(std::ffi::OsString::from_wide(&buffer[..len])))
}

#[cfg(not(windows))]
fn program_data() -> io::Result<PathBuf> {
    Err(io::Error::new(io::ErrorKind::Unsupported, "Windows only"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(windows)]
    #[test]
    fn system_folder_can_be_resolved_and_pinned_without_changing_files() {
        let root = program_data().unwrap();
        assert!(root.is_absolute());
        assert!(root.is_dir());
        let _parents = pin_parents(&root).unwrap();
    }

    #[test]
    #[cfg(windows)]
    fn retries_only_selected_machine_directories() {
        let temp = tempfile::tempdir().unwrap();
        for path in [
            "Claude/Logs/service.log",
            "Claude/config.json",
            "ClaudeCode/managed.json",
            "Other/keep",
        ] {
            let file = temp.path().join(path);
            fs::create_dir_all(file.parent().unwrap()).unwrap();
            fs::write(file, "fixture").unwrap();
        }
        let result = apply_at(temp.path(), &[Target::DesktopLogs]).unwrap();
        assert!(result.failed.is_empty(), "{:?}", result.failed);
        assert!(!temp.path().join("Claude/Logs").exists());
        assert!(temp.path().join("Claude/config.json").exists());
        assert!(temp.path().join("ClaudeCode/managed.json").exists());
        assert!(temp.path().join("Other/keep").exists());
        let result = apply_at(
            temp.path(),
            &[
                Target::DesktopData,
                Target::DesktopLogs,
                Target::DesktopData,
            ],
        )
        .unwrap();
        assert_eq!(result.deleted.len(), 1);
        assert!(!temp.path().join("Claude").exists());
        assert!(temp.path().join("Other/keep").exists());
    }

    #[cfg(windows)]
    #[test]
    fn junctions_cannot_redirect_an_elevated_retry() {
        let temp = tempfile::tempdir().unwrap();
        let outside = tempfile::tempdir().unwrap();
        fs::create_dir(outside.path().join("Logs")).unwrap();
        fs::write(outside.path().join("Logs/keep"), "keep").unwrap();
        junction::create(outside.path(), temp.path().join("Claude")).unwrap();
        let result = apply_at(temp.path(), &[Target::DesktopLogs]).unwrap();
        assert_eq!(result.failed.len(), 1);
        assert!(outside.path().join("Logs/keep").exists());
        let result = apply_at(temp.path(), &[Target::DesktopData]).unwrap();
        assert!(result.failed.is_empty());
        assert!(!temp.path().join("Claude").exists());
        assert!(outside.path().join("Logs/keep").exists());
    }

    #[cfg(windows)]
    #[test]
    fn parent_handles_prevent_junction_substitution() {
        use std::os::windows::fs::OpenOptionsExt;
        let temp = tempfile::tempdir().unwrap();
        let parent = temp.path().join("parent");
        fs::create_dir(&parent).unwrap();
        let pinned = pin_directory(&parent).unwrap();
        assert!(fs::rename(&parent, temp.path().join("renamed")).is_err());
        let open_write = || {
            fs::OpenOptions::new()
                .access_mode(0x4000_0000)
                .share_mode(0x7)
                .custom_flags(0x0220_0000)
                .open(&parent)
        };
        assert!(open_write().is_err());
        drop(pinned);
        assert!(open_write().is_ok());
        fs::rename(&parent, temp.path().join("renamed")).unwrap();
    }

    #[test]
    fn request_cannot_name_arbitrary_paths() {
        for json in [r#"["C:\\Users"]"#, r#"["../other"]"#, r#"["accounts"]"#] {
            assert!(serde_json::from_str::<Vec<Target>>(json).is_err());
        }
        assert!(apply(&[]).is_err());
    }
}
