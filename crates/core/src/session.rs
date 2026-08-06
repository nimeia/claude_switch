//! Session mode: run one stored account in its own terminal, in parallel.
//!
//! Claude Code resolves *both* its config and its credentials from
//! `CLAUDE_CONFIG_DIR`. Point that at a per-account directory and the launched
//! instance is logged in as that account while `~/.claude/` — the default
//! login, every other terminal, and the VS Code extension — stays untouched.
//! That one fact is the whole feature; everything else in this module handles
//! its consequences.
//!
//! Profiles live at `<backup>/sessions/<num>-<email-slug>/` and are seeded with
//! a plaintext `.credentials.json` plus a `.claude.json` carrying the account's
//! `oauthAccount`. On Windows plaintext *is* Claude Code's credential store, so
//! unlike the upstream Python CLI there is no keychain to shadow the seed and no
//! hashed service name to derive.
//!
//! **Validation is local and does not spawn `claude`.** Upstream runs
//! `claude auth status --json` to let Claude Code answer "who is this profile
//! logged in as" in its own words. That check makes no API call either, so its
//! only advantage is modelling storage we would otherwise have to model
//! ourselves — and on Windows there is nothing to model: the plaintext file is
//! the store. Reading it directly costs microseconds instead of a ~1s process
//! spawn on every launch.
//!
//! **The backup credential is seeded as-is, without a pre-refresh.** Upstream
//! refreshes once during bootstrap so a profile starts with a fresh access
//! token, and writes any rotated refresh token back to the backup. Here that
//! write would immediately trip the invalidation chokepoint
//! (`Switcher::post_backup_write`) and mark the profile just created as stale —
//! a loop. It also buys little: Claude Code refreshes lazily on its first API
//! call, so an expired access token with a live refresh token launches fine.
//! For the same reason [`profile_is_valid`] checks that a token *exists*, not
//! that it is unexpired — requiring freshness would re-seed on every launch
//! after an idle day, replacing the profile's newer token with an older one.
//!
//! **Sharing is by copy, re-synced on every launch.** Windows symlinks need
//! Developer Mode or elevation, so the upstream POSIX symlink mode (which lets
//! in-session `/config` edits write through to `~/.claude`) is not available.
//! A manifest records what we created so removing a share never touches user
//! data. Conversation history is deliberately *not* copied — a copy would fork
//! it rather than share it. Instead the transcript scanners read every profile
//! as an additional root (see [`crate::projects::scan_envs`]), which keeps one
//! history visible without duplicating a byte of it.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};

use crate::errors::{Error, Result};
use crate::fsutil::atomic_write;
use crate::paths::PathEnv;

/// Directory under the backup root holding every session profile.
pub const SESSIONS_DIRNAME: &str = "sessions";

/// Set when backup credentials change while a profile is live: the profile must
/// be re-seeded on the next launch that finds it quiescent, even though its own
/// credential still looks locally valid.
pub const STALE_MARKER: &str = ".cswitch-stale-credentials";

/// Records which entries in a profile we created, so turning sharing off only
/// ever removes our own copies and never anything the user put there.
pub const SHARE_MANIFEST: &str = ".cswitch-shared.json";

/// Marks a profile whose `mcpServers` key is ours to manage. Gates removal so
/// a profile that predates mirroring never has its own definitions destroyed.
pub const MCP_MIRROR_MARKER: &str = ".cswitch-mcp-mirror-v1";

/// The one user-scoped key mirrored out of the default profile's `.claude.json`.
pub const MCP_KEY: &str = "mcpServers";

/// Copied from `~/.claude` into a profile when sharing is on.
///
/// Deliberately excludes everything account- or instance-scoped: `plugins/`,
/// `ide/`, `statsig/`, `.claude.json`, `.credentials.json`, and `projects/` —
/// the last of these because a copy forks history instead of sharing it.
pub const SHARED_ITEMS: &[&str] = &[
    "settings.json",
    "keybindings.json",
    "CLAUDE.md",
    "skills",
    "commands",
    "agents",
];

/// Environment variables that make Claude Code bypass account OAuth entirely.
///
/// Launching a session is an explicit request for *this* account, so an
/// exported API key inherited from the tray process must not silently hijack
/// it. Callers scrub these from the child environment.
pub const AUTH_OVERRIDE_ENV_VARS: &[&str] = &[
    "ANTHROPIC_API_KEY",
    "ANTHROPIC_AUTH_TOKEN",
    "CLAUDE_CODE_OAUTH_TOKEN",
    "CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR",
    "CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR",
];

// --- layout ----------------------------------------------------------------

/// Filesystem-safe slug for an email address.
///
/// Uniqueness comes from the `<num>-` slot prefix, so this only has to be safe
/// (including against Windows-forbidden characters), not injective.
#[must_use]
pub fn slugify_email(email: &str) -> String {
    email
        .chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() || c == '.' || c == '_' || c == '-' {
                c
            } else {
                '_'
            }
        })
        .collect()
}

/// Root holding every session profile.
#[must_use]
pub fn sessions_root(backup_root: &Path) -> PathBuf {
    backup_root.join(SESSIONS_DIRNAME)
}

/// Profile directory for one slot.
///
/// The profile contains Claude Code's own `sessions/<pid>.json` files, so full
/// paths read `<backup>/sessions/2-a_x.com/sessions/1234.json` — intentional.
#[must_use]
pub fn session_dir_for(backup_root: &Path, num: u32, email: &str) -> PathBuf {
    sessions_root(backup_root).join(format!("{num}-{}", slugify_email(email)))
}

/// A profile directory found on disk, with the slot it claims.
#[derive(Clone, Debug)]
pub struct DiscoveredProfile {
    pub dir: PathBuf,
    pub number: u32,
}

/// Every profile directory under the backup root, whether or not its slot still
/// exists.
///
/// Used by the transcript scanners, which must see a profile's history even
/// after the account behind it was removed.
#[must_use]
pub fn list_profiles(backup_root: &Path) -> Vec<DiscoveredProfile> {
    let mut out = Vec::new();
    let Ok(entries) = std::fs::read_dir(sessions_root(backup_root)) else {
        return out;
    };
    for entry in entries.flatten() {
        if !entry.path().is_dir() {
            continue;
        }
        let name = entry.file_name();
        let Some(name) = name.to_str() else { continue };
        let Some((num, _)) = name.split_once('-') else {
            continue;
        };
        let Ok(number) = num.parse::<u32>() else {
            continue;
        };
        out.push(DiscoveredProfile {
            dir: entry.path(),
            number,
        });
    }
    out.sort_by(|a, b| a.number.cmp(&b.number).then_with(|| a.dir.cmp(&b.dir)));
    out
}

// --- live instances ---------------------------------------------------------

/// A running Claude Code instance, read from the profile's own PID files.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LiveSession {
    pub pid: u32,
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub session_id: String,
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub cwd: String,
    #[serde(default)]
    pub started_at_ms: i64,
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub entrypoint: String,
}

/// Whether a process with this id is running.
#[must_use]
pub fn is_pid_alive(pid: u32) -> bool {
    if pid <= 1 {
        return false;
    }
    #[cfg(windows)]
    {
        // Declared inline rather than pulling in `windows-sys` for two symbols.
        // QUERY_LIMITED_INFORMATION is the least privilege that answers
        // "does this exist", and works across integrity levels.
        const PROCESS_QUERY_LIMITED_INFORMATION: u32 = 0x1000;
        extern "system" {
            fn OpenProcess(access: u32, inherit: i32, pid: u32) -> isize;
            fn CloseHandle(handle: isize) -> i32;
        }
        unsafe {
            let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
            if handle == 0 {
                return false;
            }
            CloseHandle(handle);
            true
        }
    }
    #[cfg(not(windows))]
    {
        // `kill -0`: Ok means alive, EPERM means alive but not ours.
        extern "C" {
            fn kill(pid: i32, sig: i32) -> i32;
        }
        let Ok(pid) = i32::try_from(pid) else {
            return false;
        };
        unsafe { kill(pid, 0) == 0 || std::io::Error::last_os_error().raw_os_error() == Some(1) }
    }
}

/// Claude Code instances currently running against a profile.
///
/// Reads the same `sessions/<pid>.json` files Claude Code writes for itself,
/// dropping any whose process has exited (they are not cleaned up on crash).
#[must_use]
pub fn live_sessions_for(session_dir: &Path) -> Vec<LiveSession> {
    let mut out = Vec::new();
    let Ok(entries) = std::fs::read_dir(session_dir.join("sessions")) else {
        return out;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if !path.extension().is_some_and(|e| e == "json") {
            continue;
        }
        let Ok(text) = std::fs::read_to_string(&path) else {
            continue;
        };
        let Ok(v) = serde_json::from_str::<Value>(&text) else {
            continue;
        };
        let Some(pid) = v
            .get("pid")
            .and_then(Value::as_u64)
            .and_then(|p| u32::try_from(p).ok())
        else {
            continue;
        };
        if !is_pid_alive(pid) {
            continue;
        }
        out.push(LiveSession {
            pid,
            session_id: str_field(&v, "sessionId"),
            cwd: str_field(&v, "cwd"),
            started_at_ms: v.get("startedAt").and_then(Value::as_i64).unwrap_or(0),
            entrypoint: str_field(&v, "entrypoint"),
        });
    }
    out.sort_by_key(|s| s.pid);
    out
}

fn str_field(v: &Value, key: &str) -> String {
    v.get(key)
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_string()
}

// --- identity ---------------------------------------------------------------

/// The profile's *current* credential, which outranks the slot's backup copy.
///
/// Claude Code rotates the token family in place inside the profile and nothing
/// syncs it back, so once a session has run this is the only live generation —
/// the backup's refresh token is spent and the server will 401 it forever.
/// Read-only by design: writing here is Claude Code's job.
#[must_use]
pub fn read_session_credentials(session_dir: &Path) -> Option<String> {
    let text = std::fs::read_to_string(session_dir.join(".credentials.json")).ok()?;
    if text.trim().is_empty() {
        return None;
    }
    Some(text)
}

/// `(email, organization_uuid)` the profile is currently logged in as.
///
/// Claude Code rewrites `oauthAccount` on every login, so an in-session
/// `/login` is visible here — the profile can end up pointing at a different
/// account than the slot it was created for.
#[must_use]
pub fn read_session_identity(session_dir: &Path) -> Option<(String, String)> {
    let text = std::fs::read_to_string(session_dir.join(".claude.json")).ok()?;
    let cfg: Value = serde_json::from_str(&text).ok()?;
    let account = cfg.get("oauthAccount")?;
    let email = account.get("emailAddress").and_then(Value::as_str)?;
    if email.is_empty() {
        return None;
    }
    let org = account
        .get("organizationUuid")
        .and_then(Value::as_str)
        .unwrap_or_default();
    Some((email.to_string(), org.to_string()))
}

/// Whether the profile is logged in as a *different* account than its slot.
///
/// An unreadable identity is not drift: missing metadata degrades to trusting
/// the profile (its token family is normally the slot's freshest) rather than
/// abandoning it over one broken file. The org is compared only when both sides
/// have a value, so schema drift cannot manufacture a false mismatch.
#[must_use]
pub fn session_identity_drifted(session_dir: &Path, email: &str, org_uuid: &str) -> bool {
    let Some((profile_email, profile_org)) = read_session_identity(session_dir) else {
        return false;
    };
    if profile_email != email {
        return true;
    }
    !profile_org.is_empty() && !org_uuid.is_empty() && profile_org != org_uuid
}

// --- staleness --------------------------------------------------------------

/// Flag a live profile for re-seeding once its last instance exits.
pub fn mark_session_stale(session_dir: &Path) {
    if session_dir.is_dir() {
        // Best effort: the worst case is the old reuse behaviour.
        let _ = std::fs::write(session_dir.join(STALE_MARKER), b"");
    }
}

#[must_use]
pub fn is_stale(session_dir: &Path) -> bool {
    session_dir.join(STALE_MARKER).exists()
}

pub fn clear_stale(session_dir: &Path) {
    let _ = std::fs::remove_file(session_dir.join(STALE_MARKER));
}

/// Drop a profile's credential material, keeping its history and settings.
pub fn invalidate_credentials(session_dir: &Path) {
    if !session_dir.is_dir() {
        return;
    }
    let _ = std::fs::remove_file(session_dir.join(".credentials.json"));
    clear_stale(session_dir);
}

/// Remove a profile entirely (slot removed, or the user asked).
///
/// # Errors
///
/// [`Error::Io`] when the directory exists but cannot be removed.
pub fn remove_profile(session_dir: &Path) -> Result<()> {
    if !session_dir.exists() {
        return Ok(());
    }
    std::fs::remove_dir_all(session_dir).map_err(Error::Io)
}

// --- validation & bootstrap -------------------------------------------------

/// Whether the profile can be launched as-is for this account.
///
/// See the module docs on why this is a local read rather than a
/// `claude auth status` spawn.
#[must_use]
pub fn profile_is_valid(session_dir: &Path, email: &str, org_uuid: &str) -> bool {
    if !session_dir.is_dir() || is_stale(session_dir) {
        return false;
    }
    let Some(cred) = read_session_credentials(session_dir) else {
        return false;
    };
    if crate::oauth::access_token(&cred).is_none() {
        return false;
    }
    // A profile that drifted to another account is still a perfectly good
    // profile — just not this slot's. Re-seeding puts the requested account
    // back, which is what launching slot N has to mean.
    match read_session_identity(session_dir) {
        Some((profile_email, profile_org)) => {
            profile_email == email
                && !(!profile_org.is_empty() && !org_uuid.is_empty() && profile_org != org_uuid)
        }
        // Credentials present but no readable identity: Claude Code has not
        // written its config yet, or it was corrupted. Re-seed rather than
        // launch something we cannot name.
        None => false,
    }
}

/// Seed a profile from backup material.
///
/// `config_json` is the slot's `.claude.json` backup; its `oauthAccount` is the
/// identity Claude Code reads back. Any existing `.claude.json` in the profile
/// is merged into rather than replaced, so re-seeding preserves the profile's
/// own project registry.
///
/// # Errors
///
/// [`Error::Validation`] when the backup config carries no `oauthAccount`
/// (nothing identifies the account, so the profile would launch logged out),
/// or [`Error::Io`] on write failure.
pub fn bootstrap(session_dir: &Path, credentials: &str, config_json: &str) -> Result<()> {
    let backup: Value = serde_json::from_str(config_json).unwrap_or_else(|_| json!({}));
    let Some(oauth_account) = backup.get("oauthAccount").filter(|v| !v.is_null()).cloned() else {
        return Err(Error::Validation(
            "the account has no stored config backup — re-add it to enable session mode".into(),
        ));
    };

    std::fs::create_dir_all(session_dir).map_err(Error::Io)?;

    atomic_write(
        &session_dir.join(".credentials.json"),
        credentials.as_bytes(),
    )
    .map_err(|e| Error::CredentialWrite(format!("seed session credentials: {e}")))?;

    let config_path = session_dir.join(".claude.json");
    let mut existing: Map<String, Value> = std::fs::read_to_string(&config_path)
        .ok()
        .and_then(|t| serde_json::from_str::<Value>(&t).ok())
        .and_then(|v| v.as_object().cloned())
        .unwrap_or_default();
    existing.insert("oauthAccount".into(), oauth_account);
    existing.insert("hasCompletedOnboarding".into(), json!(true));
    // Load-bearing: Claude Code shows onboarding when `!theme`, which would
    // greet the user with a setup wizard in what should be a ready terminal.
    if !existing.contains_key("theme") {
        let theme = backup
            .get("theme")
            .and_then(Value::as_str)
            .unwrap_or("dark");
        existing.insert("theme".into(), json!(theme));
    }
    let text = serde_json::to_string_pretty(&Value::Object(existing))
        .map_err(|e| Error::Internal(format!("serialise session config: {e}")))?;
    atomic_write(&config_path, text.as_bytes()).map_err(Error::Io)?;

    clear_stale(session_dir);
    Ok(())
}

// --- sharing ----------------------------------------------------------------

#[derive(Default, Serialize, Deserialize)]
struct ShareManifest {
    #[serde(default)]
    items: Vec<String>,
}

fn read_manifest(session_dir: &Path) -> Vec<String> {
    std::fs::read_to_string(session_dir.join(SHARE_MANIFEST))
        .ok()
        .and_then(|t| serde_json::from_str::<ShareManifest>(&t).ok())
        .map(|m| m.items)
        .unwrap_or_default()
        .into_iter()
        // Only ever act on names we could have created ourselves.
        .filter(|i| SHARED_ITEMS.contains(&i.as_str()))
        .collect()
}

fn write_manifest(session_dir: &Path, items: &[String]) {
    let payload = serde_json::to_string_pretty(&ShareManifest {
        items: items.to_vec(),
    })
    .unwrap_or_else(|_| "{\"items\":[]}".into());
    let _ = atomic_write(&session_dir.join(SHARE_MANIFEST), payload.as_bytes());
}

fn remove_managed(dest: &Path) {
    if dest.is_dir() {
        let _ = std::fs::remove_dir_all(dest);
    } else {
        let _ = std::fs::remove_file(dest);
    }
}

fn copy_tree(src: &Path, dst: &Path) -> std::io::Result<()> {
    if src.is_dir() {
        std::fs::create_dir_all(dst)?;
        for entry in std::fs::read_dir(src)? {
            let entry = entry?;
            copy_tree(&entry.path(), &dst.join(entry.file_name()))?;
        }
        Ok(())
    } else {
        if let Some(parent) = dst.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::copy(src, dst).map(|_| ())
    }
}

/// Mirror the user's customisations from `~/.claude` into a profile, or undo it.
///
/// Idempotent and run on every launch, because a copy goes stale the moment the
/// user edits the original. Returns names that could not be synced, for the
/// caller to surface — a failed share is a degraded session, not a failed one.
#[must_use]
pub fn sync_sharing(session_dir: &Path, default_home: &Path, share: bool) -> Vec<String> {
    let mut problems = Vec::new();
    if !session_dir.is_dir() {
        return problems;
    }
    let managed = read_manifest(session_dir);

    // Sharing turned off since the last launch: remove our copies only.
    if !share {
        for name in &managed {
            remove_managed(&session_dir.join(name));
        }
        let _ = std::fs::remove_file(session_dir.join(SHARE_MANIFEST));
        return problems;
    }

    let mut now_managed: Vec<String> = Vec::new();
    for name in SHARED_ITEMS {
        let src = default_home.join(name);
        let dest = session_dir.join(name);
        let is_ours = managed.iter().any(|m| m == name);

        if !src.exists() {
            // Source gone: prune our copy, leave anything else alone.
            if is_ours {
                remove_managed(&dest);
            }
            continue;
        }
        if dest.exists() && !is_ours {
            // The profile grew its own copy of this. Never overwrite it.
            problems.push((*name).to_string());
            continue;
        }
        if dest.exists() {
            remove_managed(&dest);
        }
        if copy_tree(&src, &dest).is_err() {
            problems.push((*name).to_string());
            continue;
        }
        now_managed.push((*name).to_string());
    }
    write_manifest(session_dir, &now_managed);
    problems
}

/// Mirror the default profile's user-scope `mcpServers` into a profile.
///
/// A pure mirror in one direction: the default profile is the source of truth,
/// so adds, edits and removals all propagate and in-session MCP changes are
/// overwritten on the next launch. `share = false` removes the key, but only
/// from profiles that adopted mirroring — a profile that predates the feature
/// keeps whatever it defined itself.
///
/// Fails open: an unreadable file on either side leaves the profile untouched
/// rather than blocking a launch over MCP config.
pub fn sync_mcp_servers(session_dir: &Path, default_global_config: &Path, share: bool) {
    let marker = session_dir.join(MCP_MIRROR_MARKER);
    let adopted = marker.exists();

    let source: Map<String, Value> = if share {
        let Some(cfg) = load_object(default_global_config) else {
            return;
        };
        match cfg.get(MCP_KEY) {
            Some(Value::Object(m)) => m.clone(),
            // A readable config without the key genuinely has no user servers,
            // which propagates as a removal.
            None => Map::new(),
            // Present but not an object: leave the profile alone.
            Some(_) => return,
        }
    } else if adopted {
        Map::new()
    } else {
        return;
    };

    let config_path = session_dir.join(".claude.json");
    let Some(mut existing) = load_object(&config_path) else {
        return; // bootstrap owns a missing or broken config
    };
    let current = match existing.get(MCP_KEY) {
        Some(Value::Object(m)) => m.clone(),
        None => Map::new(),
        Some(_) => return,
    };
    if current == source && (!share || adopted) {
        return;
    }

    if source.is_empty() {
        // Claude Code strips default-valued keys; match it rather than leaving
        // an empty object it would not have written.
        existing.remove(MCP_KEY);
    } else {
        existing.insert(MCP_KEY.into(), Value::Object(source));
    }
    let Ok(text) = serde_json::to_string_pretty(&Value::Object(existing)) else {
        return;
    };
    if atomic_write(&config_path, text.as_bytes()).is_err() {
        return;
    }
    if share && !adopted {
        let _ = std::fs::write(&marker, b"");
    }
}

fn load_object(path: &Path) -> Option<Map<String, Value>> {
    let text = std::fs::read_to_string(path).ok()?;
    serde_json::from_str::<Value>(&text)
        .ok()?
        .as_object()
        .cloned()
}

// --- transcript roots --------------------------------------------------------

/// A [`PathEnv`] whose Claude config home is this profile.
///
/// Everything path-shaped then follows automatically: `.claude.json` (with the
/// profile's own project registry) and `projects/` both resolve inside the
/// profile, so the existing scanners work against it unchanged.
#[must_use]
pub fn profile_env(base: &PathEnv, session_dir: &Path) -> PathEnv {
    let mut env = base.clone();
    env.claude_config_dir = Some(session_dir.to_path_buf());
    env
}

// --- directory → account mappings --------------------------------------------

/// Schema of `<backup>/mappings.json`.
pub const MAPPINGS_SCHEMA_VERSION: u32 = 1;

/// One bound directory.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MappingEntry {
    /// The directory as the user typed it, for display.
    pub path: String,
    pub email: String,
    #[serde(default, rename = "organizationUuid")]
    pub org_uuid: String,
}

#[derive(Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct MappingFile {
    #[serde(default)]
    schema_version: u32,
    #[serde(default)]
    mappings: BTreeMap<String, MappingEntry>,
}

/// Comparable key for a directory path.
///
/// Case-folded and separator-normalised because Windows writes the same
/// directory both ways and compares it case-insensitively.
#[must_use]
pub fn mapping_key(path: &str) -> String {
    path.replace('\\', "/").trim_end_matches('/').to_lowercase()
}

/// Reads and writes the directory → account bindings.
pub struct MappingStore {
    path: PathBuf,
}

impl MappingStore {
    #[must_use]
    pub fn new(backup_root: &Path) -> Self {
        Self {
            path: backup_root.join("mappings.json"),
        }
    }

    fn load_file(&self) -> MappingFile {
        std::fs::read_to_string(&self.path)
            .ok()
            .and_then(|t| serde_json::from_str::<MappingFile>(&t).ok())
            .unwrap_or_default()
    }

    /// Every binding, keyed by [`mapping_key`].
    #[must_use]
    pub fn all(&self) -> BTreeMap<String, MappingEntry> {
        self.load_file().mappings
    }

    /// Bind a directory to an account identity.
    ///
    /// Identity is stored rather than the slot number, which is reused when an
    /// account is removed and another added in its place.
    ///
    /// # Errors
    ///
    /// [`Error::Io`] when the file cannot be written.
    pub fn set(&self, path: &str, email: &str, org_uuid: &str) -> Result<()> {
        let mut file = self.load_file();
        file.schema_version = MAPPINGS_SCHEMA_VERSION;
        file.mappings.insert(
            mapping_key(path),
            MappingEntry {
                path: path.to_string(),
                email: email.to_string(),
                org_uuid: org_uuid.to_string(),
            },
        );
        self.save(&file)
    }

    /// Remove a binding; `false` when there was none.
    ///
    /// # Errors
    ///
    /// [`Error::Io`] when the file cannot be written.
    pub fn remove(&self, path: &str) -> Result<bool> {
        let mut file = self.load_file();
        if file.mappings.remove(&mapping_key(path)).is_none() {
            return Ok(false);
        }
        self.save(&file)?;
        Ok(true)
    }

    /// Drop every binding pointing at an account. Returns how many went.
    ///
    /// # Errors
    ///
    /// [`Error::Io`] when the file cannot be written.
    pub fn prune_account(&self, email: &str, org_uuid: &str) -> Result<usize> {
        let mut file = self.load_file();
        let before = file.mappings.len();
        file.mappings
            .retain(|_, e| !(e.email == email && e.org_uuid == org_uuid));
        let removed = before - file.mappings.len();
        if removed > 0 {
            self.save(&file)?;
        }
        Ok(removed)
    }

    /// The binding governing `dir`: itself, or its nearest bound ancestor.
    ///
    /// The most specific match wins so a bound repo root covers its subfolders
    /// while a nested override still takes precedence. Every candidate lies on
    /// the single root→dir chain, so the longest matching key is the deepest.
    #[must_use]
    pub fn resolve(&self, dir: &str) -> Option<MappingEntry> {
        let target = mapping_key(dir);
        let mut best: Option<(usize, MappingEntry)> = None;
        for (key, entry) in self.load_file().mappings {
            let is_ancestor = target == key
                || (target.starts_with(&key) && target.as_bytes().get(key.len()) == Some(&b'/'));
            if !is_ancestor {
                continue;
            }
            // `is_none_or` reads better but postdates this crate's MSRV.
            let deeper = match &best {
                Some((len, _)) => key.len() > *len,
                None => true,
            };
            if deeper {
                best = Some((key.len(), entry));
            }
        }
        best.map(|(_, e)| e)
    }

    fn save(&self, file: &MappingFile) -> Result<()> {
        let text = serde_json::to_string_pretty(file)
            .map_err(|e| Error::Internal(format!("serialise mappings: {e}")))?;
        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent).map_err(Error::Io)?;
        }
        atomic_write(&self.path, text.as_bytes()).map_err(Error::Io)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::tempdir;

    fn seed_profile(dir: &Path, email: &str, org: &str, token: &str) {
        std::fs::create_dir_all(dir).unwrap();
        std::fs::write(
            dir.join(".credentials.json"),
            json!({"claudeAiOauth":{"accessToken":token,"refreshToken":"r"}}).to_string(),
        )
        .unwrap();
        std::fs::write(
            dir.join(".claude.json"),
            json!({"oauthAccount":{"emailAddress":email,"organizationUuid":org}}).to_string(),
        )
        .unwrap();
    }

    #[test]
    fn slug_is_filesystem_safe() {
        assert_eq!(slugify_email("a.b+tag@x.com"), "a.b_tag_x.com");
        assert!(!slugify_email("a/b\\c:d@x").contains(['/', '\\', ':']));
    }

    #[test]
    fn session_dir_carries_slot_and_email() {
        let dir = session_dir_for(Path::new("/backup"), 2, "a@x.com");
        assert!(dir.ends_with("2-a_x.com"));
        assert!(dir.parent().unwrap().ends_with("sessions"));
    }

    #[test]
    fn bootstrap_seeds_credentials_and_identity() {
        let tmp = tempdir().unwrap();
        let dir = tmp.path().join("2-a");
        let cfg = json!({"oauthAccount":{"emailAddress":"a@x.com","organizationUuid":"o"}});
        bootstrap(
            &dir,
            r#"{"claudeAiOauth":{"accessToken":"t"}}"#,
            &cfg.to_string(),
        )
        .unwrap();

        assert_eq!(
            read_session_identity(&dir),
            Some(("a@x.com".into(), "o".into()))
        );
        // Onboarding must not greet the user in what should be a ready terminal.
        let written: Value =
            serde_json::from_str(&std::fs::read_to_string(dir.join(".claude.json")).unwrap())
                .unwrap();
        assert_eq!(written["hasCompletedOnboarding"], json!(true));
        assert!(written.get("theme").is_some());
    }

    #[test]
    fn bootstrap_without_oauth_account_is_refused() {
        let tmp = tempdir().unwrap();
        let err = bootstrap(&tmp.path().join("p"), "{}", "{}").unwrap_err();
        assert!(matches!(err, Error::Validation(_)));
    }

    #[test]
    fn bootstrap_preserves_the_profiles_own_registry() {
        let tmp = tempdir().unwrap();
        let dir = tmp.path().join("2-a");
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(
            dir.join(".claude.json"),
            json!({"projects":{"D:/work":{"lastSessionId":"s1"}}}).to_string(),
        )
        .unwrap();

        let cfg = json!({"oauthAccount":{"emailAddress":"a@x.com"}});
        bootstrap(
            &dir,
            r#"{"claudeAiOauth":{"accessToken":"t"}}"#,
            &cfg.to_string(),
        )
        .unwrap();

        let written: Value =
            serde_json::from_str(&std::fs::read_to_string(dir.join(".claude.json")).unwrap())
                .unwrap();
        assert_eq!(written["projects"]["D:/work"]["lastSessionId"], json!("s1"));
    }

    #[test]
    fn validity_requires_matching_identity_and_a_token() {
        let tmp = tempdir().unwrap();
        let dir = tmp.path().join("2-a");
        seed_profile(&dir, "a@x.com", "o", "tok");
        assert!(profile_is_valid(&dir, "a@x.com", "o"));
        assert!(!profile_is_valid(&dir, "b@x.com", "o"));
        // Another org under the same email is a different account.
        assert!(!profile_is_valid(&dir, "a@x.com", "other"));

        invalidate_credentials(&dir);
        assert!(!profile_is_valid(&dir, "a@x.com", "o"));
    }

    #[test]
    fn a_stale_marker_beats_a_locally_valid_profile() {
        let tmp = tempdir().unwrap();
        let dir = tmp.path().join("2-a");
        seed_profile(&dir, "a@x.com", "o", "tok");
        assert!(profile_is_valid(&dir, "a@x.com", "o"));

        mark_session_stale(&dir);
        assert!(!profile_is_valid(&dir, "a@x.com", "o"));
        // Re-seeding clears it; nothing else should.
        bootstrap(
            &dir,
            r#"{"claudeAiOauth":{"accessToken":"t2"}}"#,
            &json!({"oauthAccount":{"emailAddress":"a@x.com","organizationUuid":"o"}}).to_string(),
        )
        .unwrap();
        assert!(profile_is_valid(&dir, "a@x.com", "o"));
    }

    #[test]
    fn drift_is_detected_but_missing_metadata_is_not_drift() {
        let tmp = tempdir().unwrap();
        let dir = tmp.path().join("2-a");
        seed_profile(&dir, "other@x.com", "o", "tok");
        assert!(session_identity_drifted(&dir, "a@x.com", "o"));

        std::fs::remove_file(dir.join(".claude.json")).unwrap();
        assert!(!session_identity_drifted(&dir, "a@x.com", "o"));
    }

    #[test]
    fn sharing_copies_then_removes_only_its_own_copies() {
        let tmp = tempdir().unwrap();
        let home = tmp.path().join("home");
        let profile = tmp.path().join("profile");
        std::fs::create_dir_all(home.join("skills")).unwrap();
        std::fs::write(home.join("settings.json"), b"{\"a\":1}").unwrap();
        std::fs::write(home.join("skills").join("s.md"), b"hi").unwrap();
        std::fs::create_dir_all(&profile).unwrap();

        assert!(sync_sharing(&profile, &home, true).is_empty());
        assert!(profile.join("settings.json").exists());
        assert!(profile.join("skills").join("s.md").exists());

        // Something the user put in the profile themselves survives everything.
        std::fs::write(profile.join("CLAUDE.md"), b"mine").unwrap();
        let _ = sync_sharing(&profile, &home, false);
        assert!(!profile.join("settings.json").exists());
        assert!(!profile.join("skills").exists());
        assert_eq!(
            std::fs::read_to_string(profile.join("CLAUDE.md")).unwrap(),
            "mine"
        );
    }

    #[test]
    fn sharing_never_overwrites_a_profiles_own_file() {
        let tmp = tempdir().unwrap();
        let home = tmp.path().join("home");
        let profile = tmp.path().join("profile");
        std::fs::create_dir_all(&home).unwrap();
        std::fs::create_dir_all(&profile).unwrap();
        std::fs::write(home.join("settings.json"), b"shared").unwrap();
        std::fs::write(profile.join("settings.json"), b"local").unwrap();

        let problems = sync_sharing(&profile, &home, true);
        assert_eq!(problems, vec!["settings.json".to_string()]);
        assert_eq!(
            std::fs::read_to_string(profile.join("settings.json")).unwrap(),
            "local"
        );
    }

    #[test]
    fn sharing_re_syncs_an_edited_source() {
        let tmp = tempdir().unwrap();
        let home = tmp.path().join("home");
        let profile = tmp.path().join("profile");
        std::fs::create_dir_all(&home).unwrap();
        std::fs::create_dir_all(&profile).unwrap();
        std::fs::write(home.join("settings.json"), b"v1").unwrap();
        let _ = sync_sharing(&profile, &home, true);
        std::fs::write(home.join("settings.json"), b"v2").unwrap();
        let _ = sync_sharing(&profile, &home, true);
        assert_eq!(
            std::fs::read_to_string(profile.join("settings.json")).unwrap(),
            "v2"
        );
    }

    #[test]
    fn mcp_mirrors_then_removes_only_after_adoption() {
        let tmp = tempdir().unwrap();
        let profile = tmp.path().join("profile");
        let global = tmp.path().join(".claude.json");
        std::fs::create_dir_all(&profile).unwrap();
        std::fs::write(
            profile.join(".claude.json"),
            json!({"mcpServers":{"local":{"command":"x"}}}).to_string(),
        )
        .unwrap();

        // Not adopted yet: --no-share must not destroy the profile's own entry.
        sync_mcp_servers(&profile, &global, false);
        let cfg = load_object(&profile.join(".claude.json")).unwrap();
        assert!(cfg.contains_key(MCP_KEY));

        std::fs::write(
            &global,
            json!({"mcpServers":{"shared":{"command":"y"}}}).to_string(),
        )
        .unwrap();
        sync_mcp_servers(&profile, &global, true);
        let cfg = load_object(&profile.join(".claude.json")).unwrap();
        assert!(cfg[MCP_KEY].get("shared").is_some());
        assert!(cfg[MCP_KEY].get("local").is_none());

        // Adopted now, so turning sharing off removes the mirrored key.
        sync_mcp_servers(&profile, &global, false);
        let cfg = load_object(&profile.join(".claude.json")).unwrap();
        assert!(!cfg.contains_key(MCP_KEY));
    }

    #[test]
    fn dead_pids_are_not_live_sessions() {
        let tmp = tempdir().unwrap();
        let profile = tmp.path().join("profile");
        std::fs::create_dir_all(profile.join("sessions")).unwrap();
        // Claude Code leaves PID files behind on crash; they must not count.
        std::fs::write(
            profile.join("sessions").join("999999.json"),
            json!({"pid":999_999,"sessionId":"s","cwd":"D:/x"}).to_string(),
        )
        .unwrap();
        assert!(live_sessions_for(&profile).is_empty());

        let me = std::process::id();
        std::fs::write(
            profile.join("sessions").join(format!("{me}.json")),
            json!({"pid":me,"sessionId":"mine","cwd":"D:/x","startedAt":7}).to_string(),
        )
        .unwrap();
        let live = live_sessions_for(&profile);
        assert_eq!(live.len(), 1);
        assert_eq!(live[0].session_id, "mine");
    }

    #[test]
    fn profiles_are_discovered_by_slot_prefix() {
        let tmp = tempdir().unwrap();
        std::fs::create_dir_all(sessions_root(tmp.path()).join("2-a_x.com")).unwrap();
        std::fs::create_dir_all(sessions_root(tmp.path()).join("10-b_x.com")).unwrap();
        std::fs::create_dir_all(sessions_root(tmp.path()).join("not-a-slot")).unwrap();

        let found = list_profiles(tmp.path());
        assert_eq!(found.len(), 2);
        assert_eq!(found[0].number, 2);
        assert_eq!(found[1].number, 10);
    }

    #[test]
    fn mappings_resolve_to_the_nearest_bound_ancestor() {
        let tmp = tempdir().unwrap();
        let store = MappingStore::new(tmp.path());
        store.set("D:\\work", "work@x.com", "o1").unwrap();
        store.set("D:\\work\\client", "client@x.com", "o2").unwrap();

        assert_eq!(store.resolve("D:/work/src").unwrap().email, "work@x.com");
        assert_eq!(
            store.resolve("D:\\work\\client\\deep").unwrap().email,
            "client@x.com"
        );
        assert!(store.resolve("D:/elsewhere").is_none());
        // A sibling that merely shares a prefix must not match.
        assert!(store.resolve("D:/workshop").is_none());
    }

    #[test]
    fn mappings_prune_with_their_account() {
        let tmp = tempdir().unwrap();
        let store = MappingStore::new(tmp.path());
        store.set("D:\\a", "gone@x.com", "o").unwrap();
        store.set("D:\\b", "gone@x.com", "o").unwrap();
        store.set("D:\\c", "stays@x.com", "o").unwrap();

        assert_eq!(store.prune_account("gone@x.com", "o").unwrap(), 2);
        assert_eq!(store.all().len(), 1);
    }
}
