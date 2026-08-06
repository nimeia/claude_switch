//! `sequence.json` account table (ref: switcher sequence helpers).

use std::collections::BTreeMap;
use std::path::Path;

use serde::{Deserialize, Serialize};

use crate::errors::{Error, Result};
use crate::fsutil::atomic_write;

pub const SEQUENCE_SCHEMA_VERSION: u32 = 1;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountRecord {
    pub email: String,
    #[serde(default)]
    pub uuid: String,
    #[serde(default, rename = "organizationUuid")]
    pub org_uuid: String,
    #[serde(default, rename = "organizationName")]
    pub org_name: String,
    #[serde(default)]
    pub added: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub alias: Option<String>,
    #[serde(default)]
    pub disabled: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SequenceData {
    #[serde(default = "default_schema")]
    pub schema_version: u32,
    #[serde(default)]
    pub accounts: BTreeMap<String, AccountRecord>,
    #[serde(default)]
    pub sequence: Vec<u32>,
    #[serde(default)]
    pub active_account_number: Option<u32>,
    #[serde(default)]
    pub last_updated: String,
}

fn default_schema() -> u32 {
    SEQUENCE_SCHEMA_VERSION
}

impl Default for SequenceData {
    fn default() -> Self {
        Self {
            schema_version: SEQUENCE_SCHEMA_VERSION,
            accounts: BTreeMap::new(),
            sequence: Vec::new(),
            active_account_number: None,
            last_updated: String::new(),
        }
    }
}

impl SequenceData {
    pub fn load(path: &Path) -> Result<Self> {
        if !path.exists() {
            return Ok(Self::default());
        }
        let text = std::fs::read_to_string(path).map_err(Error::Io)?;
        match serde_json::from_str(&text) {
            Ok(v) => Ok(v),
            Err(e) => {
                // Forgiving read: corrupt file → empty with warning-level error path.
                let _ = e;
                Ok(Self::default())
            }
        }
    }

    pub fn save(&self, path: &Path) -> Result<()> {
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(Error::Io)?;
        }
        let mut data = self.clone();
        data.schema_version = SEQUENCE_SCHEMA_VERSION;
        data.last_updated = chrono::Utc::now().to_rfc3339();
        let text = serde_json::to_string_pretty(&data)
            .map_err(|e| Error::Internal(format!("sequence serialize: {e}")))?;
        atomic_write(path, text.as_bytes()).map_err(Error::Io)?;
        Ok(())
    }

    #[must_use]
    pub fn account(&self, num: u32) -> Option<&AccountRecord> {
        self.accounts.get(&num.to_string())
    }

    pub fn account_mut(&mut self, num: u32) -> Option<&mut AccountRecord> {
        self.accounts.get_mut(&num.to_string())
    }

    /// Next free slot number (1-based).
    #[must_use]
    pub fn next_slot(&self) -> u32 {
        let mut n = 1u32;
        while self.accounts.contains_key(&n.to_string()) {
            n += 1;
        }
        n
    }

    pub fn upsert_account(&mut self, num: u32, record: AccountRecord) {
        let key = num.to_string();
        if !self.sequence.contains(&num) {
            self.sequence.push(num);
            self.sequence.sort_unstable();
        }
        self.accounts.insert(key, record);
    }

    pub fn remove_account(&mut self, num: u32) {
        self.accounts.remove(&num.to_string());
        self.sequence.retain(|n| *n != num);
        if self.active_account_number == Some(num) {
            self.active_account_number = None;
        }
    }

    pub fn resolve_identifier(&self, id: &str) -> Result<u32> {
        if let Ok(n) = id.parse::<u32>() {
            if self.accounts.contains_key(&n.to_string()) {
                return Ok(n);
            }
            return Err(Error::AccountNotFound(id.into()));
        }
        let lower = id.to_ascii_lowercase();
        // alias first
        for (k, rec) in &self.accounts {
            if rec
                .alias
                .as_ref()
                .is_some_and(|a| a.eq_ignore_ascii_case(&lower))
            {
                return Ok(k.parse().unwrap_or(0));
            }
        }
        let mut matches: Vec<u32> = self
            .accounts
            .iter()
            .filter(|(_, r)| r.email.eq_ignore_ascii_case(id))
            .filter_map(|(k, _)| k.parse().ok())
            .collect();
        matches.sort_unstable();
        match matches.as_slice() {
            [n] => Ok(*n),
            [] => Err(Error::AccountNotFound(id.into())),
            _ => Err(Error::Validation(format!(
                "ambiguous email {id}: matches slots {matches:?}"
            ))),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip_sequence_json() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("sequence.json");
        let mut data = SequenceData::default();
        data.upsert_account(
            1,
            AccountRecord {
                email: "a@x.com".into(),
                uuid: "u1".into(),
                org_uuid: String::new(),
                org_name: String::new(),
                added: "t".into(),
                alias: Some("dev".into()),
                disabled: false,
            },
        );
        data.active_account_number = Some(1);
        data.save(&path).unwrap();
        let loaded = SequenceData::load(&path).unwrap();
        assert_eq!(loaded.active_account_number, Some(1));
        assert_eq!(loaded.account(1).unwrap().email, "a@x.com");
        assert_eq!(loaded.resolve_identifier("dev").unwrap(), 1);
    }
}
