//! Account add / switch under three locks (ref: switcher.py).

use std::path::Path;
use std::time::Duration;

use crate::credentials::{
    email_from_global_config, email_from_oauth_cred, identity_from_config,
    merge_shared_credential_fields, oauth_account_from_config, shared_credential_fields,
    splice_oauth_account, CredentialStore,
};
use crate::errors::{Error, Result};
use crate::fsutil::atomic_write;
use crate::locks::{ClaudeCodeLocks, FileLock};
use crate::paths::{PathEnv, Paths};
use crate::sequence::{AccountRecord, SequenceData};

/// Identity columns for a slot record, read from the config snapshot being stored.
///
/// cswap fills `uuid` / `organizationUuid` / `organizationName`; leaving them
/// blank (as this did) makes a stored slot unidentifiable, so nothing can tell
/// which slot the live login belongs to. `email`/`added`/`alias` stay for the
/// caller to fill.
fn identity_record(config_json: &str) -> AccountRecord {
    let id = identity_from_config(config_json).unwrap_or_default();
    let org_name = serde_json::from_str::<serde_json::Value>(config_json)
        .ok()
        .and_then(|v| {
            v.pointer("/oauthAccount/organizationName")
                .and_then(|x| x.as_str())
                .map(str::to_string)
        })
        .unwrap_or_default();
    AccountRecord {
        email: String::new(),
        uuid: id.uuid,
        org_uuid: id.org_uuid,
        org_name,
        added: String::new(),
        alias: None,
        disabled: false,
    }
}

/// Core account switcher (Windows file-backed path).
pub struct Switcher {
    pub paths: Paths,
    pub store: CredentialStore,
    lock_timeout: Duration,
}

impl Switcher {
    #[must_use]
    pub fn new(paths: Paths) -> Self {
        let store = CredentialStore::new(paths.env.clone(), paths.credentials_dir.clone());
        Self {
            paths,
            store,
            lock_timeout: Duration::from_secs_f64(crate::locks::DEFAULT_LOCK_TIMEOUT_S),
        }
    }

    pub fn load_sequence(&self) -> Result<SequenceData> {
        SequenceData::load(&self.paths.sequence_file)
    }

    fn save_sequence(&self, data: &SequenceData) -> Result<()> {
        data.save(&self.paths.sequence_file)
    }

    /// Capture current Claude login into a managed slot.
    pub fn add_current_account(&self, slot: Option<u32>, alias: Option<String>) -> Result<u32> {
        let creds = self.store.read_active()?.ok_or_else(|| {
            Error::Config("No active Claude account found. Please log in first.".into())
        })?;

        let mut email = email_from_oauth_cred(&creds);
        let global_path = self.paths.global_config.clone();
        let global_text = if global_path.exists() {
            std::fs::read_to_string(&global_path).map_err(Error::Io)?
        } else {
            String::new()
        };
        if email.is_none() {
            email = email_from_global_config(&global_text);
        }
        let email = email.unwrap_or_else(|| "unknown@local".into());

        // Prefer full global config as slot config if it has oauthAccount; else minimal.
        let config_json = if oauth_account_from_config(&global_text).is_some() {
            global_text
        } else {
            serde_json::json!({
                "oauthAccount": {
                    "emailAddress": email,
                    "accountUuid": "",
                    "organizationUuid": "",
                    "organizationName": "",
                    "displayName": email,
                }
            })
            .to_string()
        };

        let mut seq = self.load_sequence()?;
        let num = slot.unwrap_or_else(|| seq.next_slot());
        if let Some(existing) = seq.account(num) {
            if existing.email != email {
                // Overwrite allowed (caller confirmed in GUI).
            }
        }

        self.store.write_slot(num, &email, &creds)?;
        self.store
            .write_slot_config(&self.paths.configs_dir, num, &email, &config_json)?;
        self.post_backup_write(num, &email);

        let rec = AccountRecord {
            email: email.clone(),
            added: chrono::Utc::now().to_rfc3339(),
            alias,
            disabled: false,
            ..identity_record(&config_json)
        };
        seq.upsert_account(num, rec);
        // This slot *is* the live login — that is what "add current" captured.
        // Only setting it when nothing was active before left the previously
        // added slot marked as current forever.
        seq.active_account_number = Some(num);
        self.save_sequence(&seq)?;
        Ok(num)
    }

    /// Register a fixture/slot without reading live Claude (tests / import).
    pub fn add_account_raw(
        &self,
        num: u32,
        email: &str,
        credentials: &str,
        config_json: &str,
        alias: Option<String>,
    ) -> Result<()> {
        self.store.write_slot(num, email, credentials)?;
        self.store
            .write_slot_config(&self.paths.configs_dir, num, email, config_json)?;
        self.post_backup_write(num, email);
        let mut seq = self.load_sequence()?;
        seq.upsert_account(
            num,
            AccountRecord {
                email: email.into(),
                added: chrono::Utc::now().to_rfc3339(),
                alias,
                disabled: false,
                ..identity_record(config_json)
            },
        );
        self.save_sequence(&seq)?;
        Ok(())
    }

    /// Switch active Claude login to managed account `num`.
    ///
    /// Long, and deliberately kept whole: it is one transaction — acquire three
    /// locks, snapshot for rollback, back up the outgoing account, write the
    /// incoming one, roll back on failure. Splitting it would separate each
    /// write from the rollback that undoes it, which is exactly the pairing a
    /// reader has to check.
    #[allow(clippy::too_many_lines)]
    pub fn switch_to(&self, num: u32) -> Result<SwitchResult> {
        let seq = self.load_sequence()?;
        let rec = seq
            .account(num)
            .ok_or_else(|| Error::AccountNotFound(num.to_string()))?
            .clone();
        if rec.disabled {
            // Explicit switch still allowed per design; proceed.
        }
        let target_creds = self.store.read_slot(num, &rec.email)?.ok_or_else(|| {
            Error::Credential(format!(
                "Account-{num} has no stored credentials. Re-add the account."
            ))
        })?;
        // Refuse to activate a husk. Writing a credential with no access token
        // into `.credentials.json` does not "switch" anywhere — it logs Claude
        // Code out, and the user finds out only when their next prompt fails.
        // Failing loudly here leaves the working session untouched.
        if !crate::credentials::looks_like_api_key(&target_creds)
            && crate::oauth::access_token(&target_creds).is_none()
        {
            return Err(Error::Credential(format!(
                "Account-{num} ({}) has no usable access token — its login was cleared. \
                 Log in to this account in Claude Code, then re-add it to slot {num}.",
                rec.email
            )));
        }

        let target_config = self
            .store
            .read_slot_config(&self.paths.configs_dir, num, &rec.email)?
            .ok_or_else(|| Error::Config(format!("Account-{num} has no stored config backup.")))?;
        let target_oauth = oauth_account_from_config(&target_config)
            .ok_or_else(|| Error::Config("Invalid oauthAccount in backup".into()))?;

        // Snapshot live for rollback + shared fields.
        let live_creds = self.store.read_active()?;
        let global_path = self.paths.global_config.clone();
        let live_config = if global_path.exists() {
            Some(std::fs::read_to_string(&global_path).map_err(Error::Io)?)
        } else {
            None
        };

        let from_num = seq.active_account_number;
        let from_email = from_num.and_then(|n| seq.account(n).map(|a| a.email.clone()));

        // Three locks: our FileLock + CC credentials + CC config.
        let mut file_lock = FileLock::new(&self.paths.lock_file).with_timeout(self.lock_timeout);
        file_lock.acquire()?;
        let _cred_locks = ClaudeCodeLocks::acquire_credentials(&self.paths.env, self.lock_timeout)?;
        let _cfg_lock = ClaudeCodeLocks::acquire_config(&self.paths.env, self.lock_timeout)?;

        // Re-read under lock.
        let live_creds = self.store.read_active().unwrap_or(live_creds);
        let live_config = if global_path.exists() {
            std::fs::read_to_string(&global_path).ok().or(live_config)
        } else {
            live_config
        };

        // Back up the outgoing account's live credential — but never let a
        // logged-out husk overwrite a good backup. Claude Code blanks
        // `accessToken`/`refreshToken` in place on logout, and copying that over
        // the slot destroys the only copy: the account then shows no usage and
        // can only be recovered by logging in again.
        if let (Some(cur), Some(live)) = (from_num, live_creds.as_ref()) {
            if let Some(cur_rec) = seq.account(cur) {
                if crate::oauth::is_wiped(live) && !crate::credentials::looks_like_api_key(live) {
                    // Keep the stored credential; still refresh the config copy.
                } else {
                    let _ = self.store.write_slot(cur, &cur_rec.email, live);
                    self.post_backup_write(cur, &cur_rec.email);
                }
                if let Some(ref cfg) = live_config {
                    let _ = self.store.write_slot_config(
                        &self.paths.configs_dir,
                        cur,
                        &cur_rec.email,
                        cfg,
                    );
                }
            }
        }

        let shared = live_creds
            .as_deref()
            .and_then(shared_credential_fields)
            .unwrap_or_default();
        let composed = merge_shared_credential_fields(&target_creds, &shared);

        // Activate credentials.
        self.store.write_active(&composed)?;

        // Splice oauthAccount into global config.
        let new_config = splice_oauth_account(live_config.as_deref().unwrap_or(""), &target_oauth)?;
        if let Some(parent) = global_path.parent() {
            std::fs::create_dir_all(parent).map_err(Error::Io)?;
        }
        if let Err(e) = atomic_write(&global_path, new_config.as_bytes()) {
            // Best-effort rollback credentials.
            if let Some(prev) = live_creds.as_ref() {
                let _ = self.store.write_active(prev);
            }
            return Err(Error::Config(format!("failed to write global config: {e}")));
        }

        // Commit sequence.
        let mut seq = self.load_sequence()?;
        seq.active_account_number = Some(num);
        if let Err(e) = self.save_sequence(&seq) {
            // Attempt rollback config + creds.
            if let Some(prev) = live_config.as_ref() {
                let _ = atomic_write(&global_path, prev.as_bytes());
            }
            if let Some(prev) = live_creds.as_ref() {
                let _ = self.store.write_active(prev);
            }
            return Err(e);
        }

        drop(file_lock);

        Ok(SwitchResult {
            from: from_num.map(|n| AccountRef {
                number: n,
                email: from_email.unwrap_or_default(),
                alias: None,
            }),
            to: AccountRef {
                number: num,
                email: rec.email,
                alias: rec.alias,
            },
            switched: true,
        })
    }

    /// A slot's session profile directory (whether or not it exists).
    fn session_dir(&self, num: u32, email: &str) -> std::path::PathBuf {
        crate::session::session_dir_for(&self.paths.backup_root, num, email)
    }

    /// Invalidate a slot's session profile after its backup credentials change.
    ///
    /// The single chokepoint for every path that rewrites a slot's backup
    /// (re-add, import, switching out, a usage refresh that rotated the token).
    /// A profile seeded from the previous generation now holds a credential that
    /// still looks locally valid but may already be spent, so drop its credential
    /// material and let the next launch re-seed from the fresh backup — history
    /// and shared settings survive, only the token goes.
    ///
    /// A *live* profile keeps its copy: Claude Code is managing that token right
    /// now, and pulling it out from under a running process is worse than the
    /// drift. It gets a marker instead, honoured on the first launch that finds
    /// the profile quiescent.
    fn post_backup_write(&self, num: u32, email: &str) {
        let dir = self.session_dir(num, email);
        if !dir.is_dir() {
            return;
        }
        if crate::session::live_sessions_for(&dir).is_empty() {
            crate::session::invalidate_credentials(&dir);
        } else {
            crate::session::mark_session_stale(&dir);
        }
    }

    /// Remove an account: backups, session profile, and directory bindings.
    ///
    /// # Errors
    ///
    /// [`Error::SessionInUse`] while a session-mode Claude Code is running
    /// against the slot — deleting its profile would break a session in
    /// progress, and the credential backing it is about to be erased.
    pub fn remove_account(&self, num: u32) -> Result<()> {
        let mut seq = self.load_sequence()?;
        let rec = seq
            .account(num)
            .ok_or_else(|| Error::AccountNotFound(num.to_string()))?
            .clone();

        let session_dir = self.session_dir(num, &rec.email);
        let live = crate::session::live_sessions_for(&session_dir);
        if !live.is_empty() {
            return Err(Error::SessionInUse(format!(
                "Account-{num} ({}) has {} running session(s); close them first",
                rec.email,
                live.len()
            )));
        }

        self.store.delete_slot(num, &rec.email)?;
        let cfg = crate::credentials::slot_config_path(&self.paths.configs_dir, num, &rec.email);
        let _ = std::fs::remove_file(cfg);
        // A profile that outlived its account would keep answering as an
        // account no slot holds, and its transcripts would stay in the scans.
        let _ = crate::session::remove_profile(&session_dir);
        let _ = crate::session::MappingStore::new(&self.paths.backup_root)
            .prune_account(&rec.email, &rec.org_uuid);

        seq.remove_account(num);
        self.save_sequence(&seq)?;
        Ok(())
    }

    pub fn set_disabled(&self, num: u32, disabled: bool) -> Result<()> {
        let mut seq = self.load_sequence()?;
        let rec = seq
            .account_mut(num)
            .ok_or_else(|| Error::AccountNotFound(num.to_string()))?;
        rec.disabled = disabled;
        self.save_sequence(&seq)?;
        Ok(())
    }

    pub fn set_alias(&self, num: u32, alias: Option<String>) -> Result<()> {
        let mut seq = self.load_sequence()?;
        let rec = seq
            .account_mut(num)
            .ok_or_else(|| Error::AccountNotFound(num.to_string()))?;
        rec.alias = alias;
        self.save_sequence(&seq)?;
        Ok(())
    }

    /// Replace display/rotation order. `order` must be a permutation of current slots.
    pub fn reorder_accounts(&self, order: &[u32]) -> Result<()> {
        let mut seq = self.load_sequence()?;
        let mut current = seq.sequence.clone();
        current.sort_unstable();
        let mut incoming: Vec<u32> = order.to_vec();
        let mut check = incoming.clone();
        check.sort_unstable();
        if check != current {
            return Err(Error::Validation(
                "reorder order must contain exactly the managed account slots".into(),
            ));
        }
        // Preserve uniqueness
        incoming.dedup();
        if incoming.len() != current.len() {
            return Err(Error::Validation("reorder order has duplicates".into()));
        }
        seq.sequence = order.to_vec();
        self.save_sequence(&seq)?;
        Ok(())
    }

    /// Activate credentials for a slot without requiring a prior active (fresh machine).
    pub fn activate_slot_as_live(&self, num: u32) -> Result<()> {
        self.switch_to(num)?;
        Ok(())
    }
}

#[derive(Clone, Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountRef {
    pub number: u32,
    pub email: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub alias: Option<String>,
}

#[derive(Clone, Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SwitchResult {
    pub from: Option<AccountRef>,
    pub to: AccountRef,
    pub switched: bool,
}

/// Build isolated Paths for tests (does not migrate real home).
#[must_use]
pub fn test_paths(root: &Path) -> Paths {
    let env = PathEnv {
        home: root.to_path_buf(),
        claude_config_dir: Some(root.join(".claude")),
        xdg_data_home: None,
        platform: crate::models::Platform::Windows,
    };
    let _ = std::fs::create_dir_all(env.claude_config_home());
    Paths::resolve(env)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn oauth_cred(email: &str, token: &str) -> String {
        serde_json::json!({
            "claudeAiOauth": {
                "accessToken": token,
                "refreshToken": format!("r-{token}"),
                "emailAddress": email,
            },
            "mcpOAuth": { "shared": true }
        })
        .to_string()
    }

    fn oauth_cfg(email: &str) -> String {
        serde_json::json!({
            "oauthAccount": {
                "emailAddress": email,
                "accountUuid": "u",
                "organizationUuid": "o",
                "organizationName": "Org",
                "displayName": email
            },
            "projects": { "keep": true }
        })
        .to_string()
    }

    #[test]
    fn switch_transaction_swaps_files() {
        let dir = tempfile::tempdir().unwrap();
        let paths = test_paths(dir.path());
        let sw = Switcher::new(paths.clone());

        // Seed two accounts.
        sw.add_account_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();
        sw.add_account_raw(
            2,
            "b@x.com",
            &oauth_cred("b@x.com", "tok-b"),
            &oauth_cfg("b@x.com"),
            None,
        )
        .unwrap();

        // Make account 1 live.
        sw.store
            .write_active(&oauth_cred("a@x.com", "tok-a"))
            .unwrap();
        atomic_write(&paths.global_config, oauth_cfg("a@x.com").as_bytes()).unwrap();
        let mut seq = sw.load_sequence().unwrap();
        seq.active_account_number = Some(1);
        sw.save_sequence(&seq).unwrap();

        let result = sw.switch_to(2).unwrap();
        assert!(result.switched);
        assert_eq!(result.to.number, 2);

        let active = sw.store.read_active().unwrap().unwrap();
        assert!(active.contains("tok-b"));
        // Shared mcp field from live should merge onto target.
        assert!(active.contains("shared"));

        let cfg = std::fs::read_to_string(&paths.global_config).unwrap();
        assert!(cfg.contains("b@x.com"));
        assert!(cfg.contains("keep")); // projects preserved

        let seq = sw.load_sequence().unwrap();
        assert_eq!(seq.active_account_number, Some(2));
    }
}
