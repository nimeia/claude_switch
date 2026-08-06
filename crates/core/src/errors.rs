//! Structured error tree for the core library.
//!
//! FFI maps each variant to a stable kebab-case `code` and a numeric
//! `CsErrorCode` (see design doc §8.5). Codes only grow; never renumber.

use std::io;

/// Stable numeric codes shared with the C ABI (`cs_error_code_string`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum ErrorCode {
    AccountNotFound = 1,
    ValidationFailed = 2,
    ConfigError = 3,
    CredentialError = 4,
    CredentialWriteFailed = 5,
    CredentialReadFailed = 6,
    LockTimeout = 7,
    ClaudeCodeLockTimeout = 8,
    SessionInUse = 9,
    KeychainUnavailable = 10,
    Network = 11,
    TransferError = 12,
    MigrationError = 13,
    SchemaUnsupported = 14,
    AlreadyRunning = 15,
    Internal = 16,
    NullPointer = 17,
    InvalidMethod = 18,
    JsonParse = 19,
}

impl ErrorCode {
    /// Kebab-case wire string (never change existing values).
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::AccountNotFound => "account-not-found",
            Self::ValidationFailed => "validation-failed",
            Self::ConfigError => "config-error",
            Self::CredentialError => "credential-error",
            Self::CredentialWriteFailed => "credential-write-failed",
            Self::CredentialReadFailed => "credential-read-failed",
            Self::LockTimeout => "lock-timeout",
            Self::ClaudeCodeLockTimeout => "claude-code-lock-timeout",
            Self::SessionInUse => "session-in-use",
            Self::KeychainUnavailable => "keychain-unavailable",
            Self::Network => "network",
            Self::TransferError => "transfer-error",
            Self::MigrationError => "migration-error",
            Self::SchemaUnsupported => "schema-unsupported",
            Self::AlreadyRunning => "already-running",
            Self::Internal => "internal",
            Self::NullPointer => "null-pointer",
            Self::InvalidMethod => "invalid-method",
            Self::JsonParse => "json-parse",
        }
    }

    /// Parse a kebab-case code string; unknown → `None`.
    #[must_use]
    pub fn from_str_code(s: &str) -> Option<Self> {
        Some(match s {
            "account-not-found" => Self::AccountNotFound,
            "validation-failed" => Self::ValidationFailed,
            "config-error" => Self::ConfigError,
            "credential-error" => Self::CredentialError,
            "credential-write-failed" => Self::CredentialWriteFailed,
            "credential-read-failed" => Self::CredentialReadFailed,
            "lock-timeout" => Self::LockTimeout,
            "claude-code-lock-timeout" => Self::ClaudeCodeLockTimeout,
            "session-in-use" => Self::SessionInUse,
            "keychain-unavailable" => Self::KeychainUnavailable,
            "network" => Self::Network,
            "transfer-error" => Self::TransferError,
            "migration-error" => Self::MigrationError,
            "schema-unsupported" => Self::SchemaUnsupported,
            "already-running" => Self::AlreadyRunning,
            "internal" => Self::Internal,
            "null-pointer" => Self::NullPointer,
            "invalid-method" => Self::InvalidMethod,
            "json-parse" => Self::JsonParse,
            _ => return None,
        })
    }
}

/// Core library error (ref: `exceptions.py`, extended for FFI).
#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("account not found: {0}")]
    AccountNotFound(String),

    #[error("validation failed: {0}")]
    Validation(String),

    #[error("config error: {0}")]
    Config(String),

    #[error("credential error: {0}")]
    Credential(String),

    #[error("failed to write credentials: {0}")]
    CredentialWrite(String),

    #[error("failed to read credentials: {0}")]
    CredentialRead(String),

    #[error("lock timeout: {0}")]
    LockTimeout(String),

    #[error("Claude Code lock timeout: {0}")]
    ClaudeCodeLockTimeout(String),

    #[error("session in use: {0}")]
    SessionInUse(String),

    #[error("keychain unavailable: {0}")]
    KeychainUnavailable(String),

    #[error("network error: {0}")]
    Network(String),

    #[error("transfer error: {0}")]
    Transfer(String),

    #[error("migration error: {0}")]
    Migration(String),

    #[error("schema unsupported: {0}")]
    SchemaUnsupported(String),

    #[error("already running: {0}")]
    AlreadyRunning(String),

    #[error("internal error: {0}")]
    Internal(String),

    #[error("I/O error: {0}")]
    Io(#[from] io::Error),
}

impl Error {
    /// Stable kebab-case code for FFI / JSON envelopes.
    #[must_use]
    pub fn code(&self) -> ErrorCode {
        match self {
            Self::AccountNotFound(_) => ErrorCode::AccountNotFound,
            Self::Validation(_) => ErrorCode::ValidationFailed,
            Self::Config(_) => ErrorCode::ConfigError,
            Self::Credential(_) => ErrorCode::CredentialError,
            Self::CredentialWrite(_) => ErrorCode::CredentialWriteFailed,
            Self::CredentialRead(_) => ErrorCode::CredentialReadFailed,
            Self::LockTimeout(_) => ErrorCode::LockTimeout,
            Self::ClaudeCodeLockTimeout(_) => ErrorCode::ClaudeCodeLockTimeout,
            Self::SessionInUse(_) => ErrorCode::SessionInUse,
            Self::KeychainUnavailable(_) => ErrorCode::KeychainUnavailable,
            Self::Network(_) => ErrorCode::Network,
            Self::Transfer(_) => ErrorCode::TransferError,
            Self::Migration(_) => ErrorCode::MigrationError,
            Self::SchemaUnsupported(_) => ErrorCode::SchemaUnsupported,
            Self::AlreadyRunning(_) => ErrorCode::AlreadyRunning,
            Self::Internal(_) | Self::Io(_) => ErrorCode::Internal,
        }
    }

    /// Whether the caller may reasonably retry (locks, transient I/O, network).
    #[must_use]
    pub fn retryable(&self) -> bool {
        matches!(
            self,
            Self::LockTimeout(_)
                | Self::ClaudeCodeLockTimeout(_)
                | Self::Network(_)
                | Self::KeychainUnavailable(_)
                | Self::Io(_)
        )
    }

    /// Human-readable message (no secrets; callers must not put tokens in `Error` strings).
    #[must_use]
    pub fn message(&self) -> String {
        self.to_string()
    }
}

/// Convenience alias.
pub type Result<T> = std::result::Result<T, Error>;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn error_codes_roundtrip_kebab() {
        for code in [
            ErrorCode::AccountNotFound,
            ErrorCode::MigrationError,
            ErrorCode::JsonParse,
            ErrorCode::ClaudeCodeLockTimeout,
        ] {
            let s = code.as_str();
            assert_eq!(ErrorCode::from_str_code(s), Some(code));
            assert!(!s.contains('_'));
        }
    }

    #[test]
    fn migration_maps_to_code_13() {
        let err = Error::Migration("boom".into());
        assert_eq!(err.code(), ErrorCode::MigrationError);
        assert_eq!(err.code() as u32, 13);
        assert!(!err.retryable());
    }
}
