//! Staged browser login. Secrets stay in the core; the shell receives only an
//! opaque flow id and the child process environment. No locks cross a browser wait.

use super::{CoreEvent, Engine};
use crate::credentials::{
    identity_from_config, looks_like_api_key, slot_config_path, AccountIdentity,
};
use crate::errors::{Error, Result};
use crate::fsutil::atomic_write;
use crate::keychain::file::backup_enc_path;
use crate::locks::{ClaudeCodeLocks, FileLock};
use crate::sequence::AccountRecord;
use serde_json::{json, Value};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::Duration;

#[cfg(test)]
mod tests;

pub(super) struct LoginFlow {
    id: String,
    baseline: Option<LiveStamp>,
    _lease: FileLock,
}

#[derive(PartialEq, Eq)]
struct LiveStamp {
    credential: Option<String>,
    identity: Option<AccountIdentity>,
}

fn optional_file(path: &Path) -> Result<Option<Vec<u8>>> {
    match fs::read(path) {
        Ok(v) => Ok(Some(v)),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(Error::Io(e)),
    }
}

fn restore_file(path: &Path, data: Option<&[u8]>) -> Result<()> {
    if optional_file(path)?.as_deref() == data {
        return Ok(());
    }
    match data {
        Some(v) => atomic_write(path, v).map_err(Error::Io),
        None => match fs::remove_file(path) {
            Ok(()) => Ok(()),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(Error::Io(e)),
        },
    }
}

// Account emails become existing cswap-compatible filenames. Reject separators
// and reserved filename characters before any write, including on non-Windows.
fn validate_email(email: &str) -> Result<()> {
    if email.is_empty()
        || !email.contains('@')
        || email.len() > 254
        || email
            .chars()
            .any(|c| c.is_control() || "<>:\"/\\|?*".contains(c))
        || email.ends_with(['.', ' '])
    {
        return Err(Error::Validation("login-invalid-identity".into()));
    }
    Ok(())
}

fn same_account(a: &AccountIdentity, b: &AccountIdentity) -> bool {
    // A UUID can be shared by memberships in different organizations.
    a.org_uuid == b.org_uuid
        && if !a.uuid.is_empty() && !b.uuid.is_empty() {
            a.uuid == b.uuid
        } else {
            !a.email.is_empty() && a.email.eq_ignore_ascii_case(&b.email)
        }
}

fn validate_login(credentials: &str, config: &str) -> Result<AccountIdentity> {
    let id = identity_from_config(config)
        .ok_or_else(|| Error::Credential("login-invalid-identity".into()))?;
    validate_email(&id.email)?;
    if id.uuid.is_empty()
        || id.org_uuid.is_empty()
        || crate::oauth::access_token(credentials).is_none()
    {
        return Err(Error::Credential("login-incomplete".into()));
    }
    if crate::credentials::email_from_oauth_cred(credentials)
        .is_some_and(|email| !email.eq_ignore_ascii_case(&id.email))
    {
        return Err(Error::Credential("login-identity-mismatch".into()));
    }
    Ok(id)
}

impl Engine {
    fn login_root(&self) -> PathBuf {
        self.paths.backup_root.join("login-pending")
    }

    fn login_dir(&self, id: &str) -> Result<PathBuf> {
        if !id.starts_with("flow-")
            || id.len() > 80
            || !id.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-')
        {
            return Err(Error::Validation("login-invalid-flow".into()));
        }
        let root = fs::canonicalize(self.login_root()).map_err(Error::Io)?;
        let dir = root.join(id);
        let meta = fs::symlink_metadata(&dir).map_err(Error::Io)?;
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            if meta.file_attributes() & 0x400 != 0 {
                return Err(Error::Validation("login-invalid-flow".into()));
            }
        }
        if !meta.is_dir()
            || meta.file_type().is_symlink()
            || fs::canonicalize(&dir).map_err(Error::Io)?.parent() != Some(root.as_path())
            || fs::read_to_string(dir.join(".login-owner")).map_err(Error::Io)?
                != "claude-switch-login-v1"
        {
            return Err(Error::Validation("login-invalid-flow".into()));
        }
        Ok(dir)
    }

    fn live_stamp(&self) -> Result<LiveStamp> {
        // Do not turn inaccessible files into a signed-out state via Path::exists.
        optional_file(&self.paths.env.credentials_path())?;
        let config = optional_file(&self.paths.global_config)?;
        let credential = self.switcher.store.read_active()?;
        if let Some(c) = credential.as_deref() {
            if !looks_like_api_key(c) {
                serde_json::from_str::<Value>(c)
                    .map_err(|_| Error::Credential("login-current-unreadable".into()))?;
            }
        }
        Ok(LiveStamp {
            credential,
            identity: config
                .as_deref()
                .and_then(|c| std::str::from_utf8(c).ok())
                .and_then(identity_from_config),
        })
    }

    pub(super) fn login_pending(&self) -> Result<Value> {
        let mut pending = Vec::new();
        match fs::read_dir(self.login_root()) {
            Ok(entries) => {
                for entry in entries.flatten() {
                    let id = entry.file_name().to_string_lossy().into_owned();
                    if let Ok(dir) = self.login_dir(&id) {
                        pending.push(json!({ "id": id, "configDir": dir,
                            "committed": dir.join(".login-committed").exists() }));
                    }
                }
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => return Err(Error::Io(e)),
        }
        pending.sort_by_key(|v| v["id"].as_str().unwrap_or_default().to_string());
        Ok(json!({ "pending": pending }))
    }

    pub(super) fn login_begin(&self, recover: Option<&str>) -> Result<Value> {
        let mut flow = self.login.lock();
        if flow.is_some() {
            return Err(Error::Validation("login-in-progress".into()));
        }
        let mut lease =
            FileLock::new(self.paths.backup_root.join(".login.lock")).with_timeout(Duration::ZERO);
        lease.acquire()?;
        let mut lock = FileLock::new(&self.paths.lock_file).with_timeout(Duration::from_secs(2));
        lock.acquire()?;
        let _creds = ClaudeCodeLocks::acquire_credentials(&self.paths.env, Duration::from_secs(2))?;
        let _config = ClaudeCodeLocks::acquire_config(&self.paths.env, Duration::from_secs(2))?;
        let baseline = self.live_stamp()?;
        let mut backed_up = false;
        if let Some(c) = baseline.credential.as_deref() {
            if looks_like_api_key(c) || !crate::oauth::is_wiped(c) {
                if !looks_like_api_key(c) && baseline.identity.is_none() {
                    return Err(Error::Credential("login-current-unreadable".into()));
                }
                if let Some(id) = &baseline.identity {
                    validate_email(&id.email)?;
                }
                if looks_like_api_key(c) {
                    *self.live.inner.lock() = None;
                    self.add_current(None, None)?;
                } else {
                    let config =
                        fs::read_to_string(&self.paths.global_config).map_err(Error::Io)?;
                    let id = validate_login(c, &config)?;
                    let (number, _) = self.import_login(c, &config, &id)?;
                    let mut seq = self.switcher.load_sequence()?;
                    seq.active_account_number = Some(number);
                    seq.save(&self.paths.sequence_file)?;
                    self.emit(CoreEvent::SnapshotUpdated);
                }
                backed_up = true;
            }
        }
        let (id, dir) = if let Some(id) = recover {
            (id.to_string(), self.login_dir(id)?)
        } else {
            fs::create_dir_all(self.login_root()).map_err(Error::Io)?;
            let temp = tempfile::Builder::new()
                .prefix("flow-")
                .tempdir_in(self.login_root())
                .map_err(Error::Io)?;
            crate::fsutil::set_owner_private_dir_mode(temp.path()).map_err(Error::Io)?;
            atomic_write(&temp.path().join(".login-owner"), b"claude-switch-login-v1")
                .map_err(Error::Io)?;
            let id = temp
                .path()
                .file_name()
                .unwrap()
                .to_string_lossy()
                .into_owned();
            (id, temp.keep())
        };
        let (extra, mut scrub, _) = crate::proxy::launch_env_in(
            None,
            crate::proxy::ANTHROPIC_API_HOST,
            self.app_proxy_stored().as_deref(),
        );
        scrub.extend(
            crate::session::AUTH_OVERRIDE_ENV_VARS
                .iter()
                .map(|s| (*s).into()),
        );
        // Prevent a shell's provider selection from replacing subscription OAuth.
        scrub.extend(
            [
                "ANTHROPIC_BASE_URL",
                "ANTHROPIC_PROFILE",
                "ANTHROPIC_FEDERATION_RULE_ID",
                "ANTHROPIC_ORGANIZATION_ID",
                "CLAUDE_CODE_USE_BEDROCK",
                "CLAUDE_CODE_USE_VERTEX",
                "CLAUDE_CODE_USE_FOUNDRY",
            ]
            .map(str::to_string),
        );
        let result = json!({"id": id, "configDir": dir, "extraEnv": extra,
            "scrubEnv": scrub, "backedUp": backed_up, "recovered": recover.is_some()});
        *flow = Some(LoginFlow {
            id,
            baseline: recover.is_none().then_some(baseline),
            _lease: lease,
        });
        Ok(result)
    }

    pub(super) fn login_discard(&self, id: &str) -> Result<Value> {
        let mut lease =
            FileLock::new(self.paths.backup_root.join(".login.lock")).with_timeout(Duration::ZERO);
        lease.acquire()?;
        let dir = self.login_dir(id)?;
        fs::remove_dir_all(dir).map_err(Error::Io)?;
        Ok(json!({"ok": true}))
    }

    pub(super) fn login_cancel(&self, id: &str, discard: bool) -> Result<Value> {
        let mut holder = self.login.lock();
        let flow = holder
            .as_ref()
            .ok_or_else(|| Error::Validation("login-no-flow".into()))?;
        if flow.id != id {
            return Err(Error::Validation("login-invalid-flow".into()));
        }
        // The shell has already stopped and awaited its owned process tree.
        let cleanup = if discard {
            self.login_dir(id)
                .and_then(|dir| fs::remove_dir_all(dir).map_err(Error::Io))
        } else {
            Ok(())
        };
        *holder = None;
        cleanup?;
        Ok(json!({"ok": true}))
    }

    /// Import under the sequence lock; failures restore both previous backup
    /// files. Config holds only identity, never staging paths or project state.
    fn import_login(
        &self,
        credentials: &str,
        config: &str,
        id: &AccountIdentity,
    ) -> Result<(u32, bool)> {
        let mut seq = self.switcher.load_sequence()?;
        let existing = seq.sequence.iter().copied().find(|num| {
            seq.account(*num).is_some_and(|r| {
                same_account(
                    id,
                    &AccountIdentity {
                        uuid: r.uuid.clone(),
                        org_uuid: r.org_uuid.clone(),
                        email: r.email.clone(),
                    },
                )
            })
        });
        let number = existing.unwrap_or_else(|| seq.next_slot());
        let mut rec = existing
            .and_then(|n| seq.account(n).cloned())
            .unwrap_or_else(|| AccountRecord {
                email: id.email.clone(),
                added: chrono::Utc::now().to_rfc3339(),
                uuid: String::new(),
                org_uuid: String::new(),
                org_name: String::new(),
                alias: None,
                disabled: false,
                proxy: None,
                timezone: None,
                language: None,
            });
        // Keep the stored filename for case-only or renamed email changes.
        let email = rec.email.clone();
        validate_email(&email)?;
        rec.uuid.clone_from(&id.uuid);
        rec.org_uuid.clone_from(&id.org_uuid);
        let account = crate::credentials::oauth_account_from_config(config).unwrap();
        rec.org_name = account
            .get("organizationName")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .into();
        let config = json!({"oauthAccount": account}).to_string();
        let cred_path = backup_enc_path(&self.paths.credentials_dir, &number.to_string(), &email);
        let cfg_path = slot_config_path(&self.paths.configs_dir, number, &email);
        let old_cred = optional_file(&cred_path)?;
        let old_cfg = optional_file(&cfg_path)?;
        let result = (|| {
            self.switcher
                .store
                .write_slot(number, &email, credentials)?;
            self.switcher.store.write_slot_config(
                &self.paths.configs_dir,
                number,
                &email,
                &config,
            )?;
            seq.upsert_account(number, rec);
            seq.save(&self.paths.sequence_file)
        })();
        if let Err(err) = result {
            let a = restore_file(&cred_path, old_cred.as_deref());
            let b = restore_file(&cfg_path, old_cfg.as_deref());
            if a.is_err() || b.is_err() {
                return Err(Error::CredentialWrite(
                    "login-import-rollback-failed".into(),
                ));
            }
            return Err(err);
        }
        self.switcher.post_backup_write(number, &email);
        Ok((number, existing.is_some()))
    }

    pub(super) fn login_commit(&self, flow_id: &str, params: &Value) -> Result<Value> {
        let mut holder = self.login.lock();
        let flow = holder
            .as_ref()
            .ok_or_else(|| Error::Validation("login-no-flow".into()))?;
        if flow.id != flow_id {
            return Err(Error::Validation("login-invalid-flow".into()));
        }
        let dir = self.login_dir(flow_id)?;
        if dir.join(".login-committed").exists() {
            return Err(Error::Validation("login-already-imported".into()));
        }
        let credentials = fs::read_to_string(dir.join(".credentials.json")).map_err(Error::Io)?;
        let config = fs::read_to_string(dir.join(".claude.json")).map_err(Error::Io)?;
        let id = validate_login(&credentials, &config)?;
        let status = &params["status"];
        // Require status from the same isolated CLI invocation, and cross-check
        // every identity field it provides. Never accept API-key auth as success.
        if status["loggedIn"] != true
            || status["authMethod"] != "claude.ai"
            || status["email"]
                .as_str()
                .map_or(true, |e| !e.eq_ignore_ascii_case(&id.email))
            || status["orgId"]
                .as_str()
                .is_some_and(|org| org != id.org_uuid)
            || status["configDirectory"]
                .as_str()
                .is_some_and(|p| fs::canonicalize(p).ok().as_ref() != Some(&dir))
        {
            return Err(Error::Credential("login-identity-mismatch".into()));
        }
        let mut lock = FileLock::new(&self.paths.lock_file).with_timeout(Duration::from_secs(2));
        lock.acquire()?;
        let _creds = ClaudeCodeLocks::acquire_credentials(&self.paths.env, Duration::from_secs(2))?;
        let _config = ClaudeCodeLocks::acquire_config(&self.paths.env, Duration::from_secs(2))?;
        let current = self.live_stamp()?;
        let changed = flow.baseline.as_ref() != Some(&current);
        let (number, existing) = self.import_login(&credentials, &config, &id)?;
        // A crash or failed cleanup after this point must never replay an older
        // token over credentials subsequently refreshed by the active account.
        let _ = atomic_write(&dir.join(".login-committed"), b"saved");
        let requested = params["activate"].as_bool().unwrap_or(true);
        let mut switched = false;
        let mut switch_error = None;
        if requested && !changed {
            *self.live.inner.lock() = None;
            // The baseline guarantees this is still the account captured at
            // begin. Keep that recorded slot; broad legacy identity matching
            // could confuse two memberships with the same account UUID.
            match self.switcher.switch_to_locked(number, true) {
                Ok(r) => {
                    switched = true;
                    self.autoswitch.mark_switched();
                    self.emit(CoreEvent::Switch {
                        from: r.from,
                        to: r.to,
                        dry_run: false,
                    });
                    if let Some(rec) = self
                        .switcher
                        .load_sequence()
                        .ok()
                        .and_then(|s| s.account(number).cloned())
                    {
                        self.write_launch_settings_file(&self.paths.claude_settings, &rec);
                    }
                }
                Err(e) => switch_error = Some(e.to_string()),
            }
        }
        // Saving succeeded even when activation/cleanup did not. Return that
        // distinction so a retry cannot accidentally create a second account.
        // login_dir has checked the exact owned canonical directory.
        let cleanup_pending = fs::remove_dir_all(&dir).is_err();
        *holder = None;
        self.plans.inner.lock().clear();
        *self.live.inner.lock() = None;
        self.emit(CoreEvent::SnapshotUpdated);
        Ok(
            json!({"number": number, "existing": existing, "email": id.email,
            "switched": switched, "currentChanged": requested && changed,
            "switchError": switch_error, "cleanupPending": cleanup_pending}),
        )
    }
}
