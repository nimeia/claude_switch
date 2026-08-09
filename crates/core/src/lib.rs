//! `claude-switch-core` — business logic for Claude Code multi-account switching.

pub mod agentruns;
pub mod autocontinue;
pub mod autoswitch;
pub mod credentials;
pub mod engine;
pub mod errors;
pub mod fsutil;
pub mod keychain;
pub mod locks;
pub mod models;
pub mod oauth;
pub mod paths;
pub mod plan;
pub mod projects;
pub mod proxy;
pub mod sequence;
pub mod session;
pub mod settings;
pub mod stalled;
pub mod switcher;
pub mod usage;
pub mod warmup;

/// Package version from Cargo (kept in sync with root `VERSION` via CI / workspace).
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

pub use engine::{Engine, Snapshot, SNAPSHOT_SCHEMA_VERSION};
pub use errors::{Error, ErrorCode, Result};
pub use models::Platform;
pub use paths::{PathEnv, Paths, LEGACY_BACKUP_DIRNAME};

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn version_is_semverish() {
        let major = VERSION
            .split('.')
            .next()
            .expect("VERSION must contain a major component");
        assert!(
            major.chars().all(|c| c.is_ascii_digit()) && !major.is_empty(),
            "VERSION major must be numeric, got {VERSION:?}"
        );
    }
}
