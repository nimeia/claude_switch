//! Secret storage backends. Windows/Linux use file `.enc`; macOS Keychain later.

pub mod file;

pub use file::FileSecretStore;

use crate::errors::Result;

/// Abstract secret store (Keychain or file).
pub trait SecretStore: Send + Sync {
    fn get(&self, service: &str, account: &str) -> Result<Option<String>>;
    fn set(&self, service: &str, account: &str, secret: &str) -> Result<()>;
    fn delete(&self, service: &str, account: &str) -> Result<()>;
}

pub const SECURITY_SERVICE: &str = "claude-swap";
pub const CLAUDE_CODE_KEYCHAIN_SERVICE: &str = "Claude Code-credentials";
pub const CLAUDE_CODE_MANAGED_KEYCHAIN_SERVICE: &str = "Claude Code";
