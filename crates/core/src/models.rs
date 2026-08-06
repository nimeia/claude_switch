//! Shared data types for the core library.
//!
//! PR #2 only lands [`Platform`]. Account/usage structs arrive with sequence
//! and oauth modules in later PRs.

use serde::{Deserialize, Serialize};

/// Host OS as seen by path and credential routing (ref: `models.py` `Platform`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Platform {
    MacOs,
    Linux,
    Wsl,
    Windows,
    Unknown,
}

impl Platform {
    /// Detect the current platform.
    ///
    /// Uses `std::env::consts::OS` (same idea as Python `sys.platform`) rather
    /// than heavier OS APIs — on Windows, some `platform` queries can hang on
    /// slow WMI (ref: `models.py` `Platform.detect`).
    #[must_use]
    pub fn detect() -> Self {
        Self::detect_with(|key| std::env::var(key).ok())
    }

    /// Test-friendly detect with an injectable env lookup (only `WSL_DISTRO_NAME`).
    #[must_use]
    pub fn detect_with<F>(mut getenv: F) -> Self
    where
        F: FnMut(&str) -> Option<String>,
    {
        match std::env::consts::OS {
            "macos" => Self::MacOs,
            "windows" => Self::Windows,
            "linux" => {
                if getenv("WSL_DISTRO_NAME").is_some_and(|v| !v.is_empty()) {
                    Self::Wsl
                } else {
                    Self::Linux
                }
            }
            _ => Self::Unknown,
        }
    }

    /// Whether `backup_root` uses the XDG layout (`$XDG_DATA_HOME/claude-swap`).
    #[must_use]
    pub const fn uses_xdg_backup(self) -> bool {
        matches!(self, Self::Linux | Self::Wsl)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detect_matches_host_family() {
        let p = Platform::detect();
        match std::env::consts::OS {
            "windows" => assert_eq!(p, Platform::Windows),
            "macos" => assert_eq!(p, Platform::MacOs),
            "linux" => {
                assert!(matches!(p, Platform::Linux | Platform::Wsl));
            }
            _ => assert_eq!(p, Platform::Unknown),
        }
    }

    #[test]
    fn wsl_when_distro_set() {
        // Only meaningful when compiling for linux; on other OS detect_with still
        // keys off consts::OS, so we only assert the helper path on linux.
        if std::env::consts::OS == "linux" {
            let p = Platform::detect_with(|k| {
                if k == "WSL_DISTRO_NAME" {
                    Some("Ubuntu".into())
                } else {
                    None
                }
            });
            assert_eq!(p, Platform::Wsl);
        }
    }
}
