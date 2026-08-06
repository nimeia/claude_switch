//! Base64 `.enc` file backend for per-account credentials (ref: credentials.py).

use std::path::{Path, PathBuf};

use base64::{engine::general_purpose::STANDARD as B64, Engine as _};

use super::SecretStore;
use crate::errors::{Error, Result};
use crate::fsutil::atomic_write;

/// File-backed secrets under `credentials_dir`.
///
/// Account keys are stored as `.creds-{account}.enc` where `account` is the
/// store username (`account-{num}-{email}`).
pub struct FileSecretStore {
    dir: PathBuf,
}

impl FileSecretStore {
    #[must_use]
    pub fn new(dir: impl Into<PathBuf>) -> Self {
        Self { dir: dir.into() }
    }

    fn path_for(&self, service: &str, account: &str) -> PathBuf {
        // Namespace by service so active vs backup don't collide if misused.
        let safe_svc = service.replace(['/', '\\', ':', ' '], "_");
        let safe_acc = account.replace(['/', '\\', ':'], "_");
        self.dir.join(format!(".{safe_svc}-{safe_acc}.enc"))
    }
}

impl SecretStore for FileSecretStore {
    fn get(&self, service: &str, account: &str) -> Result<Option<String>> {
        let path = self.path_for(service, account);
        if !path.exists() {
            return Ok(None);
        }
        let encoded = std::fs::read_to_string(&path).map_err(Error::Io)?;
        let bytes = B64
            .decode(encoded.trim())
            .map_err(|e| Error::CredentialRead(format!("corrupt .enc: {e}")))?;
        let s =
            String::from_utf8(bytes).map_err(|e| Error::CredentialRead(format!("utf8: {e}")))?;
        if s.is_empty() {
            Ok(None)
        } else {
            Ok(Some(s))
        }
    }

    fn set(&self, service: &str, account: &str, secret: &str) -> Result<()> {
        std::fs::create_dir_all(&self.dir).map_err(Error::Io)?;
        let path = self.path_for(service, account);
        let encoded = B64.encode(secret.as_bytes());
        atomic_write(&path, encoded.as_bytes()).map_err(Error::Io)?;
        Ok(())
    }

    fn delete(&self, service: &str, account: &str) -> Result<()> {
        let path = self.path_for(service, account);
        match std::fs::remove_file(&path) {
            Ok(()) => Ok(()),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(Error::Io(e)),
        }
    }
}

/// Slot backup path used by `CredentialStore` (matches cswap naming).
#[must_use]
pub fn backup_enc_path(credentials_dir: &Path, account_num: &str, email: &str) -> PathBuf {
    credentials_dir.join(format!(".creds-{account_num}-{email}.enc"))
}

pub fn write_slot_enc(
    credentials_dir: &Path,
    account_num: &str,
    email: &str,
    secret: &str,
) -> Result<()> {
    std::fs::create_dir_all(credentials_dir).map_err(Error::Io)?;
    let path = backup_enc_path(credentials_dir, account_num, email);
    let encoded = B64.encode(secret.as_bytes());
    atomic_write(&path, encoded.as_bytes()).map_err(Error::Io)?;
    Ok(())
}

pub fn read_slot_enc(
    credentials_dir: &Path,
    account_num: &str,
    email: &str,
) -> Result<Option<String>> {
    let path = backup_enc_path(credentials_dir, account_num, email);
    if !path.exists() {
        return Ok(None);
    }
    let encoded = std::fs::read_to_string(&path).map_err(Error::Io)?;
    let bytes = B64
        .decode(encoded.trim())
        .map_err(|e| Error::CredentialRead(format!("corrupt .enc: {e}")))?;
    Ok(Some(String::from_utf8(bytes).map_err(|e| {
        Error::CredentialRead(format!("utf8: {e}"))
    })?))
}

pub fn delete_slot_enc(credentials_dir: &Path, account_num: &str, email: &str) -> Result<()> {
    let path = backup_enc_path(credentials_dir, account_num, email);
    match std::fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(e) => Err(Error::Io(e)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn enc_roundtrip() {
        let dir = tempfile::tempdir().unwrap();
        write_slot_enc(dir.path(), "1", "a@x.com", r#"{"claudeAiOauth":{}}"#).unwrap();
        let got = read_slot_enc(dir.path(), "1", "a@x.com").unwrap().unwrap();
        assert!(got.contains("claudeAiOauth"));
    }
}
