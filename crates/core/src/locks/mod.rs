//! Cross-process locks: our `FileLock` and Claude Code proper-lockfile dirs.
//!
//! Lock order for switch transactions (never invert):
//! 1. `FileLock(<backup_root>/.lock)`
//! 2. credentials: `oauth_refresh` then legacy (`claude_locks`)
//! 3. config proper-lockfile

mod claude_locks;
mod file_lock;

pub use claude_locks::{
    config_lock_dir, credentials_lock_dir, oauth_refresh_lock_dir, ClaudeCodeLocks, ProperLock,
    CONFIG_STALENESS_S, CREDENTIALS_STALENESS_S, DEFAULT_LOCK_TIMEOUT_S,
};
pub use file_lock::FileLock;
