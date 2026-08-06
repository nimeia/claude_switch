//! Credential storage: active Claude login + per-account backups (Windows file path).

use std::path::Path;

use serde_json::{json, Map, Value};

use crate::errors::{Error, Result};
use crate::fsutil::atomic_write;
use crate::keychain::file::{delete_slot_enc, read_slot_enc, write_slot_enc};
use crate::paths::PathEnv;

/// Machine-shared credential siblings (live-owned on activation).
pub const SHARED_CREDENTIAL_KEYS: &[&str] = &[
    "mcpOAuth",
    "mcpOAuthClientConfig",
    "mcpXaaIdp",
    "mcpXaaIdpConfig",
    "pluginSecrets",
];

#[must_use]
pub fn looks_like_api_key(credentials: &str) -> bool {
    let t = credentials.trim();
    t.starts_with("sk-ant-api") && !t.starts_with('{')
}

#[must_use]
pub fn approved_form(api_key: &str) -> &str {
    let t = api_key.trim();
    let len = t.len();
    if len <= 20 {
        t
    } else {
        &t[len - 20..]
    }
}

fn credential_object(credentials: &str) -> Option<Map<String, Value>> {
    if looks_like_api_key(credentials) {
        return None;
    }
    let v: Value = serde_json::from_str(credentials).ok()?;
    v.as_object().cloned()
}

#[must_use]
pub fn shared_credential_fields(credentials: &str) -> Option<Map<String, Value>> {
    let data = credential_object(credentials)?;
    let mut out = Map::new();
    for key in SHARED_CREDENTIAL_KEYS {
        if let Some(v) = data.get(*key) {
            out.insert((*key).to_string(), v.clone());
        }
    }
    Some(out)
}

#[must_use]
pub fn merge_shared_credential_fields(
    target_credentials: &str,
    shared: &Map<String, Value>,
) -> String {
    let Some(mut target) = credential_object(target_credentials) else {
        return target_credentials.to_string();
    };
    if !target.contains_key("claudeAiOauth") {
        return target_credentials.to_string();
    }
    for key in SHARED_CREDENTIAL_KEYS {
        target.remove(*key);
    }
    for (k, v) in shared {
        target.insert(k.clone(), v.clone());
    }
    Value::Object(target).to_string()
}

/// Read/write Claude Code active credentials + slot backups (file-backed).
pub struct CredentialStore {
    env: PathEnv,
    credentials_dir: std::path::PathBuf,
}

impl CredentialStore {
    #[must_use]
    pub fn new(env: PathEnv, credentials_dir: impl Into<std::path::PathBuf>) -> Self {
        Self {
            env,
            credentials_dir: credentials_dir.into(),
        }
    }

    /// Active credential string; empty string means none; Err on I/O failure.
    pub fn read_active(&self) -> Result<Option<String>> {
        let path = self.env.credentials_path();
        if !path.exists() {
            // Managed key in global config
            if let Some(key) = self.read_primary_api_key()? {
                return Ok(Some(key));
            }
            return Ok(None);
        }
        let text = std::fs::read_to_string(&path)
            .map_err(|e| Error::CredentialRead(format!("failed to read credentials file: {e}")))?;
        if text.trim().is_empty() {
            Ok(None)
        } else {
            Ok(Some(text))
        }
    }

    pub fn write_active(&self, credentials: &str) -> Result<()> {
        if looks_like_api_key(credentials) {
            self.write_managed_key(credentials.trim())?;
            self.clear_oauth_file()?;
            return Ok(());
        }
        self.write_oauth_file(credentials)?;
        self.clear_managed_key()?;
        Ok(())
    }

    fn write_oauth_file(&self, credentials: &str) -> Result<()> {
        let path = self.env.credentials_path();
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(Error::Io)?;
        }
        atomic_write(&path, credentials.as_bytes())
            .map_err(|e| Error::CredentialWrite(format!("{e}")))?;
        Ok(())
    }

    fn clear_oauth_file(&self) -> Result<()> {
        let path = self.env.credentials_path();
        match std::fs::remove_file(path) {
            Ok(()) => Ok(()),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(Error::Io(e)),
        }
    }

    fn read_primary_api_key(&self) -> Result<Option<String>> {
        let path = self.env.global_config_path();
        if !path.exists() {
            return Ok(None);
        }
        let text = std::fs::read_to_string(&path).map_err(Error::Io)?;
        let v: Value = serde_json::from_str(&text).unwrap_or(json!({}));
        Ok(v.get("primaryApiKey")
            .and_then(|x| x.as_str())
            .map(str::to_string))
    }

    fn write_managed_key(&self, api_key: &str) -> Result<()> {
        let path = self.env.global_config_path();
        let mut cfg: Value = if path.exists() {
            let text = std::fs::read_to_string(&path).map_err(Error::Io)?;
            serde_json::from_str(&text).unwrap_or(json!({}))
        } else {
            json!({})
        };
        let obj = cfg
            .as_object_mut()
            .ok_or_else(|| Error::Config("global config not object".into()))?;
        let approved = approved_form(api_key);
        let responses = obj
            .entry("customApiKeyResponses")
            .or_insert_with(|| json!({"approved": [], "rejected": []}));
        if let Some(map) = responses.as_object_mut() {
            let list = map.entry("approved").or_insert_with(|| json!([]));
            if let Some(arr) = list.as_array_mut() {
                if !arr.iter().any(|v| v.as_str() == Some(approved)) {
                    arr.push(json!(approved));
                }
            }
            map.entry("rejected").or_insert_with(|| json!([]));
        }
        obj.insert("primaryApiKey".into(), json!(api_key));
        let text = serde_json::to_string_pretty(&cfg).map_err(|e| Error::Config(format!("{e}")))?;
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(Error::Io)?;
        }
        atomic_write(&path, text.as_bytes()).map_err(Error::Io)?;
        Ok(())
    }

    fn clear_managed_key(&self) -> Result<()> {
        let path = self.env.global_config_path();
        if !path.exists() {
            return Ok(());
        }
        let text = std::fs::read_to_string(&path).map_err(Error::Io)?;
        let mut cfg: Value = serde_json::from_str(&text).unwrap_or(json!({}));
        if let Some(obj) = cfg.as_object_mut() {
            obj.remove("primaryApiKey");
        }
        let text = serde_json::to_string_pretty(&cfg).map_err(|e| Error::Config(format!("{e}")))?;
        atomic_write(&path, text.as_bytes()).map_err(Error::Io)?;
        Ok(())
    }

    pub fn read_slot(&self, num: u32, email: &str) -> Result<Option<String>> {
        read_slot_enc(&self.credentials_dir, &num.to_string(), email)
    }

    pub fn write_slot(&self, num: u32, email: &str, credentials: &str) -> Result<()> {
        write_slot_enc(&self.credentials_dir, &num.to_string(), email, credentials)
    }

    pub fn delete_slot(&self, num: u32, email: &str) -> Result<()> {
        delete_slot_enc(&self.credentials_dir, &num.to_string(), email)
    }

    pub fn write_slot_config(
        &self,
        configs_dir: &Path,
        num: u32,
        email: &str,
        config_json: &str,
    ) -> Result<()> {
        std::fs::create_dir_all(configs_dir).map_err(Error::Io)?;
        let path = slot_config_path(configs_dir, num, email);
        atomic_write(&path, config_json.as_bytes()).map_err(Error::Io)?;
        Ok(())
    }

    pub fn read_slot_config(
        &self,
        configs_dir: &Path,
        num: u32,
        email: &str,
    ) -> Result<Option<String>> {
        let path = slot_config_path(configs_dir, num, email);
        if !path.exists() {
            return Ok(None);
        }
        Ok(Some(std::fs::read_to_string(path).map_err(Error::Io)?))
    }
}

/// Per-slot `.claude.json` backup path (cswap-compatible naming).
#[must_use]
pub fn slot_config_path(configs_dir: &Path, num: u32, email: &str) -> std::path::PathBuf {
    configs_dir.join(format!(".claude-config-{num}-{email}.json"))
}

/// Who a credential/config belongs to (ref: `session.py` identity checks).
///
/// `uuid` is the strong key. Email alone is *not* an identity: one email can own
/// two organizations, which are two separate subscriptions — so the weak form
/// compares email **and** org together.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct AccountIdentity {
    pub uuid: String,
    pub email: String,
    pub org_uuid: String,
}

impl AccountIdentity {
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.uuid.is_empty() && self.email.is_empty()
    }

    /// Whether two identities name the same account.
    ///
    /// A shared non-empty `uuid` decides on its own. Otherwise both must agree on
    /// email *and* org uuid — with one concession: when either side has no org
    /// recorded (older backups never captured it), email alone has to do.
    #[must_use]
    pub fn matches(&self, other: &Self) -> bool {
        if self.is_empty() || other.is_empty() {
            return false;
        }
        if !self.uuid.is_empty() && !other.uuid.is_empty() {
            return self.uuid == other.uuid;
        }
        if self.email.is_empty() || !self.email.eq_ignore_ascii_case(&other.email) {
            return false;
        }
        self.org_uuid.is_empty() || other.org_uuid.is_empty() || self.org_uuid == other.org_uuid
    }
}

/// Identity recorded in a `.claude.json` (live or slot backup) `oauthAccount`.
#[must_use]
pub fn identity_from_config(text: &str) -> Option<AccountIdentity> {
    let v: Value = serde_json::from_str(text).ok()?;
    let acct = v.get("oauthAccount")?;
    let get = |k: &str| {
        acct.get(k)
            .and_then(Value::as_str)
            .map(str::trim)
            .unwrap_or_default()
            .to_string()
    };
    let id = AccountIdentity {
        uuid: get("accountUuid"),
        email: get("emailAddress"),
        org_uuid: get("organizationUuid"),
    };
    if id.is_empty() {
        None
    } else {
        Some(id)
    }
}

/// Extract email from oauth credential JSON or global config oauthAccount.
pub fn email_from_oauth_cred(cred: &str) -> Option<String> {
    let v: Value = serde_json::from_str(cred).ok()?;
    v.pointer("/claudeAiOauth/emailAddress")
        .or_else(|| v.pointer("/claudeAiOauth/account/emailAddress"))
        .and_then(|x| x.as_str())
        .map(str::to_string)
}

pub fn email_from_global_config(text: &str) -> Option<String> {
    let v: Value = serde_json::from_str(text).ok()?;
    v.pointer("/oauthAccount/emailAddress")
        .and_then(|x| x.as_str())
        .map(str::to_string)
}

#[must_use]
pub fn oauth_account_from_config(text: &str) -> Option<Value> {
    let v: Value = serde_json::from_str(text).ok()?;
    v.get("oauthAccount").cloned()
}

pub fn splice_oauth_account(live_config: &str, oauth_account: &Value) -> Result<String> {
    let mut cfg: Value = if live_config.trim().is_empty() {
        json!({})
    } else {
        serde_json::from_str(live_config)
            .map_err(|e| Error::Config(format!("parse config: {e}")))?
    };
    let obj = cfg
        .as_object_mut()
        .ok_or_else(|| Error::Config("config is not an object".into()))?;
    obj.insert("oauthAccount".into(), oauth_account.clone());
    serde_json::to_string_pretty(&cfg).map_err(|e| Error::Config(format!("{e}")))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::Platform;
    use crate::paths::PathEnv;

    fn fixture_env(root: &Path) -> PathEnv {
        PathEnv {
            home: root.to_path_buf(),
            claude_config_dir: Some(root.join(".claude")),
            xdg_data_home: None,
            platform: Platform::Windows,
        }
    }

    #[test]
    fn active_oauth_roundtrip() {
        let dir = tempfile::tempdir().unwrap();
        let env = fixture_env(dir.path());
        std::fs::create_dir_all(env.claude_config_home()).unwrap();
        let store = CredentialStore::new(env, dir.path().join("credentials"));
        let body = r#"{"claudeAiOauth":{"accessToken":"at","refreshToken":"rt"}}"#;
        store.write_active(body).unwrap();
        let got = store.read_active().unwrap().unwrap();
        assert!(got.contains("accessToken"));
    }

    #[test]
    fn merge_shared_keeps_live_mcp() {
        let target = r#"{"claudeAiOauth":{"accessToken":"new"},"mcpOAuth":{"old":true}}"#;
        let live = r#"{"claudeAiOauth":{"accessToken":"live"},"mcpOAuth":{"live":true}}"#;
        let shared = shared_credential_fields(live).unwrap();
        let merged = merge_shared_credential_fields(target, &shared);
        assert!(merged.contains("\"live\":true") || merged.contains("\"live\": true"));
        assert!(!merged.contains("\"old\""));
    }
}
