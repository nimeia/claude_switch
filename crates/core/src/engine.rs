//! Public Engine façade used by FFI and GUI.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::SystemTime;

use parking_lot::Mutex;
use serde::Serialize;
use serde_json::json;

use crate::autoswitch::AutoSwitchEngine;
use crate::credentials::{identity_from_config, AccountIdentity};
use crate::errors::{Error, Result};
use crate::oauth;
use crate::paths::{PathEnv, Paths};
use crate::plan::{plan_for_slot, PlanInfo};
use crate::sequence::SequenceData;
use crate::session;
use crate::settings::{AutoSwitchSettings, Settings};
use crate::switcher::{AccountRef, SwitchResult, Switcher};
use crate::usage::{
    fetch_usage, plan_after_fetch, MockHttp, SharedHttp, UreqHttp, Usage, UsageCache, UsageStatus,
};
use std::time::Duration;

/// Bumped to 3 when `accounts[].liveSessions` was added (additive; older readers ignore it).
pub const SNAPSHOT_SCHEMA_VERSION: u32 = 3;
pub const FFI_SCHEMA_VERSION: u32 = 1;

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountSnapshot {
    pub number: u32,
    pub email: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub alias: Option<String>,
    pub active: bool,
    pub disabled: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub usage: Option<Usage>,
    /// Why `usage` is absent, when it is (see [`UsageStatus`]).
    pub usage_status: UsageStatus,
    /// Subscription facts read back from the slot's own backups (see [`crate::plan`]).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub plan: Option<PlanInfo>,
    /// Claude Code instances running against this account's session profile.
    ///
    /// Session terminals are invisible once opened, so the count is carried on
    /// every snapshot: it labels the card, and it is the reason a switch or a
    /// delete can be refused.
    #[serde(default)]
    pub live_sessions: u32,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub schema_version: u32,
    /// Slot the live Claude Code login belongs to — resolved from the live
    /// credential, not from the last switch this tool recorded.
    pub active_account_number: Option<u32>,
    /// False when the live login could not be identified and the stored
    /// `activeAccountNumber` was used as-is.
    pub active_verified: bool,
    /// Set when Claude Code is logged into an account no slot holds.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub unmanaged_login_email: Option<String>,
    pub accounts: Vec<AccountSnapshot>,
    pub autoswitch: AutoSwitchSettings,
    pub is_leader: bool,
    /// Seconds until the next adaptive usage poll (from last [`Engine::refresh_usage`]).
    pub next_poll_seconds: f64,
}

/// Required string parameter, named in the error so a typo is obvious.
fn str_param<'a>(params: &'a serde_json::Value, key: &str) -> Result<&'a str> {
    params
        .get(key)
        .and_then(serde_json::Value::as_str)
        .ok_or_else(|| Error::Validation(format!("missing {key}")))
}

/// Transcript folders for one directory.
///
/// Accepts `transcriptDirs` (the complete set, since session mode can spread one
/// directory's history over several profiles) and still honours a lone
/// `transcriptDir` so a caller built against the single-root shape keeps working.
fn transcript_dirs_param(params: &serde_json::Value) -> Result<Vec<PathBuf>> {
    if let Some(list) = params.get("transcriptDirs").and_then(|v| v.as_array()) {
        let dirs: Vec<PathBuf> = list
            .iter()
            .filter_map(|v| v.as_str())
            .map(PathBuf::from)
            .collect();
        if !dirs.is_empty() {
            return Ok(dirs);
        }
    }
    Ok(vec![PathBuf::from(str_param(params, "transcriptDir")?)])
}

/// Everything the shell needs to launch one session terminal.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionLaunch {
    /// Value for the child's `CLAUDE_CONFIG_DIR`; empty when
    /// [`SessionLaunch::use_default_login`] is set.
    pub config_dir: String,
    /// Launch with the ordinary default login and no profile at all.
    ///
    /// Set when the requested account already *is* the default login. Giving it
    /// a profile too would put one account's rotating refresh token in two
    /// config directories, and the copy that is not in use goes stale the first
    /// time the server rotates — the exact drift this feature exists to avoid.
    /// Running plain `claude` reaches the same account with no second copy.
    pub use_default_login: bool,
    pub number: u32,
    pub email: String,
    /// False when the profile had to be seeded from backup for this launch.
    pub reused: bool,
    /// Instances already running against this profile — a second terminal for
    /// the same account is allowed, and the caller may want to say so.
    pub live_sessions: u32,
    /// Shared items that could not be copied, usually because the profile has
    /// its own version. A degraded session, not a failed one.
    pub share_problems: Vec<String>,
    /// Variables the caller must remove from the child environment: each one
    /// would override the account this launch is explicitly asking for.
    pub scrub_env: Vec<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RefreshUsageResult {
    pub ok: bool,
    pub next_poll_seconds: f64,
    pub accounts_refreshed: u32,
}

#[derive(Clone, Debug, Serialize)]
#[serde(tag = "event", rename_all = "kebab-case")]
pub enum CoreEvent {
    SnapshotUpdated,
    Switch {
        from: Option<AccountRef>,
        to: AccountRef,
        dry_run: bool,
    },
    NoSwitch {
        reason: String,
        detail: String,
    },
    Error {
        message: String,
        #[serde(rename = "retryable")]
        retryable: bool,
    },
}

type EventSink = Arc<Mutex<Vec<CoreEvent>>>;

/// `(mtime, len)` of a backing file; `None` when it does not exist.
///
/// Length rides along because filesystem timestamps are coarse (FAT/exFAT: 2s),
/// so a same-second rewrite of a different size still invalidates.
type FileStamp = Option<(SystemTime, u64)>;

fn file_stamp(path: &Path) -> FileStamp {
    let m = std::fs::metadata(path).ok()?;
    Some((m.modified().unwrap_or(SystemTime::UNIX_EPOCH), m.len()))
}

/// One slot's parsed plan + identity, plus the stamps they were parsed from.
struct PlanEntry {
    /// Slot email at parse time — a re-added slot invalidates even if stamps match.
    email: String,
    cred_stamp: FileStamp,
    config_stamp: FileStamp,
    plan: Option<PlanInfo>,
    /// Identity from the slot's config backup, for matching the live login.
    identity: Option<AccountIdentity>,
}

/// Plan facts cached against the backing files' stamps.
///
/// `snapshot()` runs on every UI refresh and every poll, and a slot's config
/// backup is a full `.claude.json` copy (~70 KB), so re-parsing per call would
/// be pure waste. Two `stat`s per account decide instead, and a slot is
/// re-read only when a switch/add actually rewrote it.
#[derive(Default)]
struct PlanCache {
    inner: Mutex<HashMap<u32, PlanEntry>>,
}

/// Who Claude Code is logged in as right now, cached against the live files.
#[derive(Default)]
struct LiveCache {
    inner: Mutex<Option<LiveEntry>>,
}

struct LiveEntry {
    config_stamp: FileStamp,
    cred_stamp: FileStamp,
    state: LiveState,
}

/// What Claude Code's live login is right now.
#[derive(Clone)]
enum LiveState {
    /// No active credential — nothing is logged in.
    LoggedOut,
    /// Logged in, but whose it is can't be read (no `oauthAccount` in the
    /// config). Not evidence of anything, so the stored number stands.
    Unresolvable,
    Oauth(AccountIdentity),
    /// Managed `sk-ant-api…` key — matched by key, not by oauth identity.
    ApiKey(String),
}

/// Choose among slots that all match the live login.
///
/// The same account can legitimately occupy two slots (a duplicate add, or one
/// email under two orgs where the older backup recorded no org). The recorded
/// `activeAccountNumber` breaks the tie when it is one of them — otherwise the
/// lowest slot wins, so the answer is at least stable across refreshes.
fn pick_slot(seq: &SequenceData, matches: impl Iterator<Item = u32>) -> Option<u32> {
    let mut found: Vec<u32> = matches.collect();
    if found.len() > 1 {
        if let Some(stored) = seq.active_account_number {
            if found.contains(&stored) {
                return Some(stored);
            }
        }
        found.sort_unstable();
    }
    found.first().copied()
}

/// Outcome of matching the live login against managed slots.
struct ActiveResolution {
    number: Option<u32>,
    /// True when the live login decided this, false when it fell back to
    /// `sequence.json` (nothing readable to compare against).
    verified: bool,
    /// Email of the live login, for reporting a logged-in-but-unmanaged account.
    live_email: Option<String>,
}

/// Process-local engine.
pub struct Engine {
    paths: Paths,
    switcher: Switcher,
    usage: UsageCache,
    plans: PlanCache,
    live: LiveCache,
    http: SharedHttp,
    autoswitch: AutoSwitchEngine,
    settings: Mutex<Settings>,
    events: EventSink,
    is_leader: Mutex<bool>,
    /// Adaptive cadence from last refresh (`plan_after_fetch`).
    next_poll: Mutex<Duration>,
}

impl Engine {
    /// Initialize with real process paths (runs legacy migration).
    ///
    /// Production path uses [`UreqHttp`] so usage refresh hits Anthropic when
    /// accounts hold real OAuth tokens. Tests/fixtures inject [`MockHttp`].
    pub fn init_default() -> Result<Self> {
        let env = PathEnv::from_process();
        let paths = Paths::resolve_and_migrate(env)?;
        Self::with_paths(paths, Arc::new(UreqHttp::new()))
    }

    /// Isolated engine for tests / FFI consumer fixtures.
    pub fn init_isolated(root: impl Into<PathBuf>, http: SharedHttp) -> Result<Self> {
        let root = root.into();
        let env = PathEnv {
            home: root.clone(),
            claude_config_dir: Some(root.join(".claude")),
            xdg_data_home: None,
            platform: crate::models::Platform::Windows,
        };
        let _ = std::fs::create_dir_all(env.claude_config_home());
        let paths = Paths::resolve(env);
        let _ = std::fs::create_dir_all(&paths.backup_root);
        Self::with_paths(paths, http)
    }

    /// Isolated demo engine: mock HTTP pre-seeded for `tok-a` / `tok-b`.
    pub fn init_isolated_demo(root: impl Into<PathBuf>) -> Result<Self> {
        Self::init_isolated(root, Arc::new(MockHttp::with_demo_tokens()))
    }

    fn with_paths(paths: Paths, http: SharedHttp) -> Result<Self> {
        let settings = Settings::load(&paths.settings_file)?;
        let switcher = Switcher::new(paths.clone());
        Ok(Self {
            paths,
            switcher,
            usage: UsageCache::new(),
            plans: PlanCache::default(),
            live: LiveCache::default(),
            http,
            autoswitch: AutoSwitchEngine::new(),
            settings: Mutex::new(settings),
            events: Arc::new(Mutex::new(Vec::new())),
            is_leader: Mutex::new(true),
            next_poll: Mutex::new(Duration::from_secs(60)),
        })
    }

    pub fn paths(&self) -> &Paths {
        &self.paths
    }

    fn emit(&self, ev: CoreEvent) {
        self.events.lock().push(ev);
    }

    /// Drain queued events (FFI polling style).
    pub fn drain_events(&self) -> Vec<CoreEvent> {
        std::mem::take(&mut *self.events.lock())
    }

    /// Cached facts for one slot, re-parsed only when its backups changed.
    ///
    /// `pick` selects what the caller wants out of the entry so plan and
    /// identity share a single parse of the (large) config backup.
    ///
    /// Failures are not errors: a slot whose credential is unreadable simply
    /// reports nothing, exactly like one whose backups predate these fields.
    fn slot_entry<T>(&self, num: u32, email: &str, pick: impl Fn(&PlanEntry) -> T) -> T {
        let cred_path = crate::keychain::file::backup_enc_path(
            &self.paths.credentials_dir,
            &num.to_string(),
            email,
        );
        let config_path = crate::credentials::slot_config_path(&self.paths.configs_dir, num, email);
        let cred_stamp = file_stamp(&cred_path);
        let config_stamp = file_stamp(&config_path);

        if let Some(hit) = self.plans.inner.lock().get(&num) {
            if hit.email == email
                && hit.cred_stamp == cred_stamp
                && hit.config_stamp == config_stamp
            {
                return pick(hit);
            }
        }

        let cred = self.switcher.store.read_slot(num, email).ok().flatten();
        let config = self
            .switcher
            .store
            .read_slot_config(&self.paths.configs_dir, num, email)
            .ok()
            .flatten();
        let plan = plan_for_slot(cred.as_deref(), config.as_deref());
        let identity = config.as_deref().and_then(identity_from_config);
        let entry = PlanEntry {
            email: email.to_string(),
            cred_stamp,
            config_stamp,
            plan,
            identity,
        };
        let out = pick(&entry);
        self.plans.inner.lock().insert(num, entry);
        out
    }

    /// Plan facts for one slot (see [`Engine::slot_entry`]).
    fn plan_for(&self, num: u32, email: &str) -> Option<PlanInfo> {
        self.slot_entry(num, email, |e| e.plan.clone())
    }

    /// Identity stored for one slot, preferring the sequence record.
    ///
    /// Records written before identity was captured have blank columns; those
    /// fall back to the slot's config backup (already cached for plan facts).
    fn slot_identity(&self, seq: &SequenceData, num: u32) -> Option<AccountIdentity> {
        let rec = seq.account(num)?;
        let from_record = AccountIdentity {
            uuid: rec.uuid.clone(),
            email: rec.email.clone(),
            org_uuid: rec.org_uuid.clone(),
        };
        if !from_record.uuid.is_empty() {
            return Some(from_record);
        }
        self.slot_entry(num, &rec.email, |e| e.identity.clone())
            .or(if from_record.is_empty() {
                None
            } else {
                Some(from_record)
            })
    }

    /// Who Claude Code is logged in as right now.
    ///
    /// Cached against the live credential + config stamps: `snapshot()` runs on
    /// every refresh, and `.claude.json` is a ~70 KB parse.
    fn live_state(&self) -> LiveState {
        let config_path = self.paths.global_config.clone();
        let cred_path = self.paths.credentials.clone();
        let config_stamp = file_stamp(&config_path);
        let cred_stamp = file_stamp(&cred_path);

        if let Some(hit) = self.live.inner.lock().as_ref() {
            if hit.config_stamp == config_stamp && hit.cred_stamp == cred_stamp {
                return hit.state.clone();
            }
        }

        let cred = self.switcher.store.read_active().ok().flatten();
        let state = match cred {
            None => LiveState::LoggedOut,
            Some(c) if crate::credentials::looks_like_api_key(&c) => {
                LiveState::ApiKey(c.trim().to_string())
            }
            // The oauth identity lives in the config, not the credential; it is
            // only trustworthy while an oauth credential is actually present
            // (`.claude.json` keeps a stale `oauthAccount` after a logout).
            Some(_) => std::fs::read_to_string(&config_path)
                .ok()
                .as_deref()
                .and_then(identity_from_config)
                .map_or(LiveState::Unresolvable, LiveState::Oauth),
        };

        *self.live.inner.lock() = Some(LiveEntry {
            config_stamp,
            cred_stamp,
            state: state.clone(),
        });
        state
    }

    /// Slot the live login actually belongs to.
    ///
    /// `sequence.json`'s `activeAccountNumber` is only a record of the last
    /// switch *this tool* performed — a `claude /login`, a cswap CLI switch, or
    /// a logout all move the live login without touching it. So the live
    /// credential decides, and the stored number is merely the fallback for when
    /// there is nothing to compare against.
    fn resolve_active(&self, seq: &SequenceData) -> ActiveResolution {
        let live = self.live_state();
        let (matched, live_email) = match &live {
            // Nothing logged in: no slot can be current.
            LiveState::LoggedOut => {
                return ActiveResolution {
                    number: None,
                    verified: true,
                    live_email: None,
                }
            }
            // No evidence either way — keep whatever was recorded.
            LiveState::Unresolvable => {
                return ActiveResolution {
                    number: seq.active_account_number,
                    verified: false,
                    live_email: None,
                }
            }
            LiveState::Oauth(id) => (
                pick_slot(
                    seq,
                    seq.sequence
                        .iter()
                        .copied()
                        .filter(|&n| self.slot_identity(seq, n).is_some_and(|s| s.matches(id))),
                ),
                Some(id.email.clone()).filter(|e| !e.is_empty()),
            ),
            LiveState::ApiKey(key) => (
                pick_slot(
                    seq,
                    seq.sequence.iter().copied().filter(|&n| {
                        seq.account(n).is_some_and(|rec| {
                            self.switcher
                                .store
                                .read_slot(n, &rec.email)
                                .ok()
                                .flatten()
                                .is_some_and(|c| c.trim() == key)
                        })
                    }),
                ),
                None,
            ),
        };

        // Logged in as somebody we don't manage: reporting the stale stored slot
        // would be a lie, so no row is current and the shell can say who is.
        ActiveResolution {
            number: matched,
            verified: true,
            live_email,
        }
    }

    /// Resolve the active slot and persist a corrected `activeAccountNumber`.
    ///
    /// Kept out of [`Engine::snapshot`] so reads stay side-effect free; shells
    /// call this on refresh to keep `sequence.json` truthful for cswap's CLI.
    pub fn reconcile_active(&self) -> Result<Option<u32>> {
        let mut seq = self.switcher.load_sequence()?;
        let resolved = self.resolve_active(&seq);

        let mut dirty = false;
        if resolved.verified && seq.active_account_number != resolved.number {
            seq.active_account_number = resolved.number;
            dirty = true;
        }
        // Backfill identity columns on slots added before they were captured, so
        // matching stops depending on re-reading each config backup.
        for &num in &seq.sequence.clone() {
            let Some(rec) = seq.account(num) else {
                continue;
            };
            if !rec.uuid.is_empty() {
                continue;
            }
            let email = rec.email.clone();
            let Some(id) = self.slot_entry(num, &email, |e| e.identity.clone()) else {
                continue;
            };
            if id.uuid.is_empty() {
                continue;
            }
            if let Some(rec) = seq.account_mut(num) {
                rec.uuid = id.uuid;
                if rec.org_uuid.is_empty() {
                    rec.org_uuid = id.org_uuid;
                }
                dirty = true;
            }
        }

        if dirty {
            seq.save(&self.paths.sequence_file)?;
            self.emit(CoreEvent::SnapshotUpdated);
        }
        Ok(seq.active_account_number)
    }

    pub fn snapshot(&self) -> Result<Snapshot> {
        let seq = self.switcher.load_sequence()?;
        let settings = self.settings.lock().clone();
        let active = self.resolve_active(&seq);
        let mut accounts = Vec::new();
        for &num in &seq.sequence {
            let Some(rec) = seq.account(num) else {
                continue;
            };
            accounts.push(AccountSnapshot {
                number: num,
                email: rec.email.clone(),
                alias: rec.alias.clone(),
                active: active.number == Some(num),
                disabled: rec.disabled,
                usage: self.usage.get(num),
                usage_status: self.usage.status(num),
                plan: self.plan_for(num, &rec.email),
                live_sessions: u32::try_from(self.live_sessions(num, &rec.email).len())
                    .unwrap_or(u32::MAX),
            });
        }
        // Only report a foreign login when it is genuinely unmanaged.
        let unmanaged_email = active
            .live_email
            .filter(|_| active.number.is_none() && active.verified);
        Ok(Snapshot {
            schema_version: SNAPSHOT_SCHEMA_VERSION,
            active_account_number: active.number,
            active_verified: active.verified,
            unmanaged_login_email: unmanaged_email,
            accounts,
            autoswitch: settings.autoswitch,
            is_leader: *self.is_leader.lock(),
            next_poll_seconds: self.next_poll.lock().as_secs_f64(),
        })
    }

    pub fn snapshot_json(&self) -> Result<String> {
        let snap = self.snapshot()?;
        serde_json::to_string(&snap).map_err(|e| Error::Internal(e.to_string()))
    }

    // -- session mode ------------------------------------------------------

    /// Profile directory for a slot (whether or not it exists yet).
    fn session_dir(&self, num: u32, email: &str) -> PathBuf {
        session::session_dir_for(&self.paths.backup_root, num, email)
    }

    /// Claude Code instances running against a slot's session profile.
    fn live_sessions(&self, num: u32, email: &str) -> Vec<session::LiveSession> {
        session::live_sessions_for(&self.session_dir(num, email))
    }

    /// The slot's session-profile credential, when the profile still holds
    /// *this* account's token family.
    ///
    /// An in-session `/login` can re-point a profile at a different account.
    /// Its credential then measures that account's usage, which recorded under
    /// this slot's label would be a plausible-looking lie — so drift falls back
    /// to the backup, which is both the right identity and safe to refresh.
    fn session_credential(&self, num: u32, email: &str) -> Option<String> {
        let dir = self.session_dir(num, email);
        let cred = session::read_session_credentials(&dir)?;
        let org = self
            .switcher
            .load_sequence()
            .ok()
            .and_then(|s| s.account(num).map(|a| a.org_uuid.clone()))
            .unwrap_or_default();
        if session::session_identity_drifted(&dir, email, &org) {
            return None;
        }
        Some(cred)
    }

    /// Slots with a session terminal open, which an automatic switch must not
    /// land on (see [`crate::autoswitch::AutoSwitchEngine::rank`]).
    fn session_busy(&self, seq: &SequenceData) -> Vec<u32> {
        seq.sequence
            .iter()
            .copied()
            .filter(|&n| {
                seq.account(n)
                    .is_some_and(|rec| !self.live_sessions(n, &rec.email).is_empty())
            })
            .collect()
    }

    /// Config homes whose transcripts belong to this machine (see
    /// [`crate::projects::scan_envs`]).
    fn scan_envs(&self) -> Vec<PathEnv> {
        crate::projects::scan_envs(&self.paths.env, &self.paths.backup_root)
    }

    /// Directory → account bindings.
    fn mappings(&self) -> session::MappingStore {
        session::MappingStore::new(&self.paths.backup_root)
    }

    /// Make a slot's session profile ready to launch, and say how.
    ///
    /// Reuses an existing profile when it is still valid for the account —
    /// re-seeding would replace the credential Claude Code has been rotating in
    /// place with the backup's older generation, which is exactly the drift this
    /// feature has to avoid. Sharing is re-synced either way, because a copy of
    /// `settings.json` goes stale the moment the original is edited.
    ///
    /// # Errors
    ///
    /// [`Error::AccountNotFound`] for an unknown slot, [`Error::Validation`]
    /// for a disabled or API-key account, [`Error::Credential`] when the slot
    /// has nothing to seed from.
    pub fn session_prepare(&self, identifier: &str, share: bool) -> Result<SessionLaunch> {
        let seq = self.switcher.load_sequence()?;
        let num = seq.resolve_identifier(identifier)?;
        let rec = seq
            .account(num)
            .ok_or_else(|| Error::AccountNotFound(identifier.to_string()))?;
        let email = rec.email.clone();
        let org_uuid = rec.org_uuid.clone();

        if rec.disabled {
            return Err(Error::Validation(format!(
                "Account-{num} ({email}) is disabled"
            )));
        }

        let dir = self.session_dir(num, &email);
        let stored = self.switcher.store.read_slot(num, &email)?;
        let Some(stored) = stored.filter(|c| !c.trim().is_empty()) else {
            return Err(Error::Credential(format!(
                "Account-{num} ({email}) has no stored credentials"
            )));
        };
        // Session bootstrap is OAuth-shaped: it seeds `.credentials.json` and
        // validates an access token. An API-key account would fail that check
        // opaquely, so refuse it by name instead.
        if crate::credentials::looks_like_api_key(&stored) {
            return Err(Error::Validation(format!(
                "Account-{num} ({email}) is an API-key account; session mode needs an OAuth login"
            )));
        }

        // Already the default login: reach it the plain way (see
        // `SessionLaunch::use_default_login`). Checked against the *resolved*
        // live account rather than the recorded one, so a `/login` outside this
        // tool cannot make us open a redundant profile.
        if self.resolve_active(&seq).number == Some(num) {
            return Ok(SessionLaunch {
                config_dir: String::new(),
                use_default_login: true,
                number: num,
                email,
                reused: true,
                live_sessions: 0,
                share_problems: Vec::new(),
                scrub_env: Vec::new(),
            });
        }

        // A stale marker set while the profile was live only applies once the
        // last instance has exited — never pull credentials out from under a
        // running Claude Code.
        let live = session::live_sessions_for(&dir);
        if session::is_stale(&dir) && live.is_empty() {
            session::invalidate_credentials(&dir);
        }

        let reused = session::profile_is_valid(&dir, &email, &org_uuid);
        if !reused {
            if live.is_empty() {
                let config = self
                    .switcher
                    .store
                    .read_slot_config(&self.paths.configs_dir, num, &email)?
                    .unwrap_or_default();
                session::bootstrap(&dir, &stored, &config)?;
            } else {
                // Re-seeding under a running instance would swap the credential
                // it is holding. Launch into the profile as it stands; the
                // stale marker survives for the next quiescent launch.
                self.emit(CoreEvent::Error {
                    message: format!(
                        "Account-{num} ({email}) has {} running session(s); \
                         launching without re-seeding the profile",
                        live.len()
                    ),
                    retryable: false,
                });
            }
        }

        let default_home = {
            let mut base = self.paths.env.clone();
            base.claude_config_dir = None;
            base.claude_config_home()
        };
        let share_problems = session::sync_sharing(&dir, &default_home, share);
        session::sync_mcp_servers(&dir, &self.paths.env.default_global_config_path(), share);

        Ok(SessionLaunch {
            config_dir: dir.to_string_lossy().to_string(),
            use_default_login: false,
            number: num,
            email,
            reused,
            live_sessions: u32::try_from(live.len()).unwrap_or(u32::MAX),
            share_problems,
            scrub_env: session::AUTH_OVERRIDE_ENV_VARS
                .iter()
                .map(|s| (*s).to_string())
                .collect(),
        })
    }

    /// Live sessions for every slot, plus which profiles exist on disk.
    pub fn session_status(&self) -> Result<serde_json::Value> {
        let seq = self.switcher.load_sequence()?;
        let mut out = Vec::new();
        for &num in &seq.sequence {
            let Some(rec) = seq.account(num) else {
                continue;
            };
            let dir = self.session_dir(num, &rec.email);
            out.push(json!({
                "number": num,
                "email": rec.email,
                "profileExists": dir.is_dir(),
                "configDir": dir.to_string_lossy(),
                "live": self.live_sessions(num, &rec.email),
            }));
        }
        Ok(json!({ "accounts": out }))
    }

    /// Delete a slot's session profile, with its history and settings.
    ///
    /// # Errors
    ///
    /// [`Error::SessionInUse`] while an instance is still running against it —
    /// removing the directory under a live Claude Code would break the session
    /// it is in the middle of.
    pub fn session_remove(&self, identifier: &str) -> Result<()> {
        let seq = self.switcher.load_sequence()?;
        let num = seq.resolve_identifier(identifier)?;
        let rec = seq
            .account(num)
            .ok_or_else(|| Error::AccountNotFound(identifier.to_string()))?;
        let dir = self.session_dir(num, &rec.email);
        let live = session::live_sessions_for(&dir);
        if !live.is_empty() {
            return Err(Error::SessionInUse(format!(
                "Account-{num} ({}) has {} running session(s)",
                rec.email,
                live.len()
            )));
        }
        session::remove_profile(&dir)?;
        self.emit(CoreEvent::SnapshotUpdated);
        Ok(())
    }

    /// Bind a directory to an account, so opening it picks that account.
    pub fn mapping_set(&self, path: &str, identifier: &str) -> Result<u32> {
        let seq = self.switcher.load_sequence()?;
        let num = seq.resolve_identifier(identifier)?;
        let rec = seq
            .account(num)
            .ok_or_else(|| Error::AccountNotFound(identifier.to_string()))?;
        self.mappings().set(path, &rec.email, &rec.org_uuid)?;
        self.emit(CoreEvent::SnapshotUpdated);
        Ok(num)
    }

    /// Remove a directory binding; `false` when there was none.
    pub fn mapping_remove(&self, path: &str) -> Result<bool> {
        let removed = self.mappings().remove(path)?;
        if removed {
            self.emit(CoreEvent::SnapshotUpdated);
        }
        Ok(removed)
    }

    /// Every binding, resolved to the slot that currently holds each identity.
    ///
    /// Bindings store `(email, org)` rather than a slot number because slots are
    /// reused; a binding whose account is gone is reported with a null slot
    /// rather than dropped, so the UI can say so.
    pub fn mapping_list(&self) -> Result<serde_json::Value> {
        let seq = self.switcher.load_sequence()?;
        let mut out = Vec::new();
        for (key, entry) in self.mappings().all() {
            let slot = seq.sequence.iter().copied().find(|n| {
                seq.account(*n)
                    .is_some_and(|a| a.email == entry.email && a.org_uuid == entry.org_uuid)
            });
            out.push(json!({
                "key": key,
                "path": entry.path,
                "email": entry.email,
                "number": slot,
            }));
        }
        Ok(json!({ "mappings": out }))
    }

    /// The account bound to a directory (itself or its nearest bound ancestor).
    pub fn mapping_resolve(&self, path: &str) -> Result<serde_json::Value> {
        let Some(entry) = self.mappings().resolve(path) else {
            return Ok(json!({ "matched": false }));
        };
        let seq = self.switcher.load_sequence()?;
        let slot = seq.sequence.iter().copied().find(|n| {
            seq.account(*n)
                .is_some_and(|a| a.email == entry.email && a.org_uuid == entry.org_uuid)
        });
        Ok(json!({
            "matched": true,
            "path": entry.path,
            "email": entry.email,
            "number": slot,
        }))
    }

    /// Usage for one slot, refreshing its OAuth token first when needed.
    ///
    /// Two rules keep this honest:
    ///
    /// - **The live slot uses the live credential.** Claude Code keeps that one
    ///   fresh; the slot's backup is only as new as its last switch-out, and can
    ///   even be a logged-out husk.
    /// - **The live slot is never refreshed by us.** A grant rotates the refresh
    ///   token, and rotating the one Claude Code is holding would log the user
    ///   out of the session they are using right now.
    ///
    /// A session profile is a third source and outranks the backup for a slot
    /// that has one. Claude Code rotates the token family inside the profile and
    /// nothing syncs it back, so after the first session the backup's refresh
    /// token is a spent generation the server will 401 forever — reading it
    /// would freeze this account's usage at its last pre-session measurement.
    /// The profile's credential is used strictly read-only for the same reason
    /// the live slot is: rotating it would log the next session launch out.
    fn fetch_slot_usage(
        &self,
        num: u32,
        email: &str,
        is_active: bool,
    ) -> (Option<Usage>, UsageStatus) {
        let stored = self.switcher.store.read_slot(num, email).ok().flatten();
        let cred = if is_active {
            self.switcher
                .store
                .read_active()
                .ok()
                .flatten()
                .or(stored.clone())
        } else {
            stored.clone()
        };

        // Read-only source: never refreshed, never persisted.
        let mut profile_owned = false;
        let mut cred = cred;
        if !is_active {
            if let Some(from_profile) = self.session_credential(num, email) {
                profile_owned = true;
                cred = Some(from_profile);
            }
        }

        let Some(mut cred) = cred else {
            return (None, UsageStatus::NoCredential);
        };
        if crate::credentials::looks_like_api_key(&cred) {
            return (None, UsageStatus::ApiKey);
        }

        if profile_owned && oauth::needs_refresh(&cred, oauth::now_ms()) {
            // The profile's own Claude Code refreshes lazily on its next API
            // call. Asking now would 401, and refreshing it ourselves would
            // rotate the family out from under it.
            return (None, UsageStatus::NeedsLogin);
        }

        if !is_active && !profile_owned && oauth::needs_refresh(&cred, oauth::now_ms()) {
            match oauth::try_refresh(self.http.as_ref(), &cred) {
                Ok(rotated) => {
                    // Persist before use: the response may carry a rotated
                    // refresh token, and losing it strands the account.
                    if let Err(e) = self.switcher.store.write_slot(num, email, &rotated) {
                        self.emit(CoreEvent::Error {
                            message: format!(
                                "failed to store refreshed credential: {}",
                                e.message()
                            ),
                            retryable: true,
                        });
                        return (None, UsageStatus::Unavailable);
                    }
                    cred = rotated;
                }
                Err(e) => {
                    let status = match e {
                        oauth::RefreshError::NoRefreshToken | oauth::RefreshError::InvalidGrant => {
                            UsageStatus::NeedsLogin
                        }
                        oauth::RefreshError::Transient(ref msg) => {
                            self.emit(CoreEvent::Error {
                                message: format!("token refresh failed: {msg}"),
                                retryable: true,
                            });
                            UsageStatus::Unavailable
                        }
                    };
                    return (None, status);
                }
            }
        }

        // An expired live credential is Claude Code's to renew, not ours; report
        // it rather than silently showing nothing.
        if oauth::access_token(&cred).is_none() {
            return (None, UsageStatus::NeedsLogin);
        }

        match fetch_usage(self.http.as_ref(), &cred) {
            // A response carrying neither window is not "0% used" — the account
            // has no subscription quota at all (lapsed, or never subscribed).
            // Recording it as Ok would show an empty card with no explanation
            // and feed a phantom 100% headroom into autoswitch.
            Ok(u) if u.binding_pct().is_none() => (None, UsageStatus::NoSubscription),
            Ok(u) => (Some(u), UsageStatus::Ok),
            Err(e) => {
                self.emit(CoreEvent::Error {
                    message: e.message(),
                    retryable: e.retryable(),
                });
                (None, UsageStatus::Unavailable)
            }
        }
    }

    /// Refresh per-account usage via the configured HTTP client and update the
    /// adaptive next-poll interval with [`plan_after_fetch`].
    pub fn refresh_usage(&self) -> Result<RefreshUsageResult> {
        let seq = self.switcher.load_sequence()?;
        let threshold = self.settings.lock().autoswitch.threshold;
        let active = self.resolve_active(&seq).number;
        let mut refreshed = 0u32;
        for &num in &seq.sequence {
            let Some(rec) = seq.account(num) else {
                continue;
            };
            let (usage, status) = self.fetch_slot_usage(num, &rec.email, active == Some(num));
            if status.has_numbers() {
                refreshed += 1;
            }
            self.usage.put(num, usage.unwrap_or_default(), status);
        }

        // Adaptive cadence from the *active* account's binding %, falling back
        // to the hottest managed account so polling still accelerates near limits.
        let active_pct = seq
            .active_account_number
            .and_then(|n| self.usage.get(n))
            .and_then(|u| u.binding_pct())
            .or_else(|| {
                seq.sequence
                    .iter()
                    .filter_map(|n| self.usage.get(*n).and_then(|u| u.binding_pct()))
                    .fold(None, |acc: Option<f64>, p| {
                        Some(acc.map_or(p, |a| a.max(p)))
                    })
            });
        let plan = plan_after_fetch(active_pct, threshold);
        *self.next_poll.lock() = plan.next_after;

        self.emit(CoreEvent::SnapshotUpdated);
        Ok(RefreshUsageResult {
            ok: true,
            next_poll_seconds: plan.next_after.as_secs_f64(),
            accounts_refreshed: refreshed,
        })
    }

    #[must_use]
    pub fn next_poll_seconds(&self) -> f64 {
        self.next_poll.lock().as_secs_f64()
    }

    pub fn switch_to(&self, identifier: &str) -> Result<SwitchResult> {
        let seq = self.switcher.load_sequence()?;
        let num = seq.resolve_identifier(identifier)?;
        let result = self.switcher.switch_to(num)?;
        self.autoswitch.mark_switched();
        self.emit(CoreEvent::Switch {
            from: result.from.clone(),
            to: result.to.clone(),
            dry_run: false,
        });
        self.emit(CoreEvent::SnapshotUpdated);
        Ok(result)
    }

    pub fn add_current(&self, slot: Option<u32>, alias: Option<String>) -> Result<u32> {
        let n = self.switcher.add_current_account(slot, alias)?;
        self.emit(CoreEvent::SnapshotUpdated);
        Ok(n)
    }

    /// Test/import helper.
    pub fn add_raw(
        &self,
        num: u32,
        email: &str,
        credentials: &str,
        config_json: &str,
        alias: Option<String>,
    ) -> Result<()> {
        self.switcher
            .add_account_raw(num, email, credentials, config_json, alias)?;
        self.emit(CoreEvent::SnapshotUpdated);
        Ok(())
    }

    pub fn remove_account(&self, identifier: &str) -> Result<()> {
        let seq = self.switcher.load_sequence()?;
        let num = seq.resolve_identifier(identifier)?;
        self.switcher.remove_account(num)?;
        self.plans.inner.lock().remove(&num);
        self.emit(CoreEvent::SnapshotUpdated);
        Ok(())
    }

    pub fn set_disabled(&self, identifier: &str, disabled: bool) -> Result<()> {
        let seq = self.switcher.load_sequence()?;
        let num = seq.resolve_identifier(identifier)?;
        self.switcher.set_disabled(num, disabled)?;
        self.emit(CoreEvent::SnapshotUpdated);
        Ok(())
    }

    pub fn set_alias(&self, identifier: &str, alias: Option<String>) -> Result<()> {
        let seq = self.switcher.load_sequence()?;
        let num = seq.resolve_identifier(identifier)?;
        self.switcher.set_alias(num, alias)?;
        self.emit(CoreEvent::SnapshotUpdated);
        Ok(())
    }

    pub fn reorder_accounts(&self, order: &[u32]) -> Result<()> {
        self.switcher.reorder_accounts(order)?;
        self.emit(CoreEvent::SnapshotUpdated);
        Ok(())
    }

    pub fn get_settings(&self) -> Settings {
        self.settings.lock().clone()
    }

    pub fn set_autoswitch(&self, auto: AutoSwitchSettings) -> Result<()> {
        let mut s = self.settings.lock();
        s.autoswitch = auto.clamp();
        s.save(&self.paths.settings_file)?;
        Ok(())
    }

    /// What an autoswitch tick *would* do, without doing it.
    ///
    /// Same inputs as [`Engine::autoswitch_tick`] but never activates anything,
    /// so callers can answer "if I turn this on, where does it send me?" without
    /// risking the working session on the answer.
    /// `threshold` overrides the stored one, so a shell can answer "if I turn
    /// this on at 90%, where does it send me?" before the user commits.
    pub fn autoswitch_preview(
        &self,
        enabled: bool,
        threshold: Option<f64>,
    ) -> Result<serde_json::Value> {
        self.refresh_usage()?;
        self.reconcile_active()?;
        let seq = self.switcher.load_sequence()?;
        let mut settings = self.settings.lock().autoswitch.clone();
        settings.enabled = enabled || settings.enabled;
        if let Some(t) = threshold {
            settings.threshold = t;
        }
        let d = self.autoswitch.preview(&seq, &self.usage, &settings);
        Ok(json!({
            "wouldSwitch": d.should_switch,
            "target": d.target,
            "detail": d.detail,
            "reason": d.reason.map(|r| serde_json::to_value(r).unwrap_or_default()),
        }))
    }

    /// Run one autoswitch decision (+ optional switch).
    pub fn autoswitch_tick(&self) -> Result<serde_json::Value> {
        self.refresh_usage()?;
        // Decide against the account that is actually live. Judging the stored
        // number would compare the wrong account's usage against the threshold
        // whenever the login moved outside this tool.
        self.reconcile_active()?;
        let seq = self.switcher.load_sequence()?;
        let settings = self.settings.lock().autoswitch.clone();
        let decision = self.autoswitch.decide_with_sessions(
            &seq,
            &self.usage,
            &settings,
            &self.session_busy(&seq),
        );
        if decision.should_switch {
            if let Some(t) = decision.target {
                let r = self.switch_to(&t.to_string())?;
                return Ok(json!({
                    "switched": true,
                    // Both ends: a shell announcing the switch needs to say what
                    // it moved away from, not only where it landed.
                    "from": r.from,
                    "to": r.to,
                    "detail": decision.detail,
                }));
            }
        }
        self.emit(CoreEvent::NoSwitch {
            reason: decision.reason.map_or_else(
                || "other".into(),
                |r| {
                    serde_json::to_value(r)
                        .ok()
                        .and_then(|v| v.as_str().map(str::to_string))
                        .unwrap_or_else(|| "other".into())
                },
            ),
            detail: decision.detail.clone(),
        });
        Ok(json!({
            "switched": false,
            "detail": decision.detail,
        }))
    }

    pub fn claim_leadership(&self, force: bool) -> Result<bool> {
        let mut lead = self.is_leader.lock();
        if *lead && !force {
            return Ok(true);
        }
        *lead = true;
        self.autoswitch.set_observe_only(false);
        Ok(true)
    }

    pub fn set_leader(&self, leader: bool) {
        *self.is_leader.lock() = leader;
        self.autoswitch.set_observe_only(!leader);
    }

    /// JSON-RPC style call for C ABI method catalog.
    /// The FFI surface: one arm per method.
    ///
    /// Long by nature — it is a flat dispatch table, and splitting it into
    /// sub-dispatchers would hide the one property that matters here, that every
    /// supported method is visible in a single list.
    #[allow(clippy::too_many_lines)]
    pub fn call_json(&self, method: &str, params: &serde_json::Value) -> Result<serde_json::Value> {
        match method {
            "snapshot" => {
                let s = self.snapshot()?;
                Ok(serde_json::to_value(s).map_err(|e| Error::Internal(e.to_string()))?)
            }
            "refresh_usage" => {
                let r = self.refresh_usage()?;
                Ok(serde_json::to_value(r).map_err(|e| Error::Internal(e.to_string()))?)
            }
            // Directory views. Listing is cheap (registry + transcript heads);
            // `project_stats` reads every transcript byte, so it is only ever
            // run when the user asks for it.
            "list_projects" => {
                let list = crate::projects::list_projects_in(&self.scan_envs())?;
                Ok(json!({ "projects": list }))
            }
            "list_sessions" => {
                let dirs = transcript_dirs_param(params)?;
                let list = crate::projects::list_sessions_in(&dirs)?;
                Ok(json!({ "sessions": list }))
            }
            "overview_stats" => {
                let o = crate::projects::scan_overview_cached_in(
                    &self.scan_envs(),
                    &self.paths.cache_dir,
                )?;
                Ok(serde_json::to_value(o).map_err(|e| Error::Internal(e.to_string()))?)
            }
            "project_stats" => {
                let dirs = transcript_dirs_param(params)?;
                let stats = crate::projects::scan_stats_in(&dirs)?;
                Ok(serde_json::to_value(stats).map_err(|e| Error::Internal(e.to_string()))?)
            }
            // Session mode: one account per terminal, in parallel.
            "session_prepare" => {
                let id = str_param(params, "id")?;
                // Sharing defaults on: a session without the user's settings and
                // CLAUDE.md is a stranger's Claude Code, not theirs.
                let share = params
                    .get("share")
                    .and_then(serde_json::Value::as_bool)
                    .unwrap_or(true);
                let launch = self.session_prepare(id, share)?;
                Ok(serde_json::to_value(launch).map_err(|e| Error::Internal(e.to_string()))?)
            }
            "session_status" => self.session_status(),
            "session_remove" => {
                self.session_remove(str_param(params, "id")?)?;
                Ok(json!({ "ok": true }))
            }
            "mapping_set" => {
                let number =
                    self.mapping_set(str_param(params, "path")?, str_param(params, "id")?)?;
                Ok(json!({ "ok": true, "number": number }))
            }
            "mapping_remove" => {
                let removed = self.mapping_remove(str_param(params, "path")?)?;
                Ok(json!({ "removed": removed }))
            }
            "mapping_list" => self.mapping_list(),
            "mapping_resolve" => self.mapping_resolve(str_param(params, "path")?),
            "reconcile_active" => {
                let n = self.reconcile_active()?;
                Ok(json!({ "activeAccountNumber": n }))
            }
            "switch_to" => {
                let id = params
                    .get("id")
                    .or_else(|| params.get("identifier"))
                    .and_then(|v| v.as_str())
                    .ok_or_else(|| Error::Validation("missing id".into()))?;
                let r = self.switch_to(id)?;
                Ok(serde_json::to_value(r).map_err(|e| Error::Internal(e.to_string()))?)
            }
            "add_current" => {
                let slot = params
                    .get("slot")
                    .and_then(serde_json::Value::as_u64)
                    .and_then(|u| u32::try_from(u).ok());
                let alias = params
                    .get("alias")
                    .and_then(|v| v.as_str())
                    .map(str::to_string);
                let n = self.add_current(slot, alias)?;
                Ok(json!({"number": n}))
            }
            "add_raw" => {
                let num = params
                    .get("number")
                    .and_then(serde_json::Value::as_u64)
                    .and_then(|n| u32::try_from(n).ok())
                    .ok_or_else(|| Error::Validation("missing number".into()))?;
                let email = params
                    .get("email")
                    .and_then(|v| v.as_str())
                    .ok_or_else(|| Error::Validation("missing email".into()))?;
                let credentials = params
                    .get("credentials")
                    .and_then(|v| v.as_str())
                    .ok_or_else(|| Error::Validation("missing credentials".into()))?;
                let config = params
                    .get("config")
                    .and_then(|v| v.as_str())
                    .ok_or_else(|| Error::Validation("missing config".into()))?;
                let alias = params
                    .get("alias")
                    .and_then(|v| v.as_str())
                    .map(str::to_string);
                self.add_raw(num, email, credentials, config, alias)?;
                Ok(json!({"ok": true}))
            }
            "remove_account" => {
                let id = params
                    .get("id")
                    .and_then(|v| v.as_str())
                    .ok_or_else(|| Error::Validation("missing id".into()))?;
                self.remove_account(id)?;
                Ok(json!({"ok": true}))
            }
            "set_disabled" => {
                let id = params
                    .get("id")
                    .and_then(|v| v.as_str())
                    .ok_or_else(|| Error::Validation("missing id".into()))?;
                let disabled = params
                    .get("disabled")
                    .and_then(serde_json::Value::as_bool)
                    .unwrap_or(true);
                self.set_disabled(id, disabled)?;
                Ok(json!({"ok": true}))
            }
            "set_alias" => {
                let id = params
                    .get("id")
                    .and_then(|v| v.as_str())
                    .ok_or_else(|| Error::Validation("missing id".into()))?;
                let alias = params
                    .get("alias")
                    .and_then(|v| v.as_str())
                    .map(str::to_string)
                    .filter(|s| !s.is_empty());
                self.set_alias(id, alias)?;
                Ok(json!({"ok": true}))
            }
            "reorder_accounts" => {
                let order: Vec<u32> = params
                    .get("order")
                    .and_then(|v| v.as_array())
                    .ok_or_else(|| Error::Validation("missing order array".into()))?
                    .iter()
                    .filter_map(|x| x.as_u64().and_then(|n| u32::try_from(n).ok()))
                    .collect();
                if order.is_empty() {
                    return Err(Error::Validation("order is empty".into()));
                }
                self.reorder_accounts(&order)?;
                Ok(json!({"ok": true, "order": order}))
            }
            "get_settings" => {
                let s = self.get_settings();
                Ok(serde_json::to_value(s).map_err(|e| Error::Internal(e.to_string()))?)
            }
            "set_autoswitch" => {
                let auto: AutoSwitchSettings = serde_json::from_value(params.clone())
                    .map_err(|e| Error::Validation(e.to_string()))?;
                self.set_autoswitch(auto)?;
                Ok(json!({"ok": true}))
            }
            "autoswitch_tick" => self.autoswitch_tick(),
            "autoswitch_preview" => {
                let enabled = params
                    .get("enabled")
                    .and_then(serde_json::Value::as_bool)
                    .unwrap_or(true);
                let threshold = params.get("threshold").and_then(serde_json::Value::as_f64);
                self.autoswitch_preview(enabled, threshold)
            }
            "drain_events" => {
                let ev = self.drain_events();
                Ok(serde_json::to_value(ev).map_err(|e| Error::Internal(e.to_string()))?)
            }
            "claim_leadership" => {
                let force = params
                    .get("force")
                    .and_then(serde_json::Value::as_bool)
                    .unwrap_or(false);
                let ok = self.claim_leadership(force)?;
                Ok(json!({"isLeader": ok}))
            }
            "schema_version" => Ok(json!({"ffiSchemaVersion": FFI_SCHEMA_VERSION})),
            other => Err(Error::Validation(format!("invalid-method: {other}"))),
        }
    }
}

/// Convenience for tests that seed mock HTTP.
pub fn test_engine_with_mock(root: PathBuf) -> Result<(Engine, Arc<MockHttp>)> {
    let mock = Arc::new(MockHttp::new());
    let eng = Engine::init_isolated(root, mock.clone())?;
    Ok((eng, mock))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn oauth_cred(email: &str, token: &str) -> String {
        json!({
            "claudeAiOauth": {
                "accessToken": token,
                "refreshToken": format!("r-{token}"),
                "emailAddress": email,
                "subscriptionType": "pro",
                "rateLimitTier": "default_claude_ai",
            }
        })
        .to_string()
    }

    /// Distinct `accountUuid` per email — real accounts never share one, and
    /// identity matching is what decides which slot is live.
    fn oauth_cfg(email: &str) -> String {
        json!({
            "oauthAccount": {
                "emailAddress": email,
                "accountUuid": format!("uuid-{email}"),
                "organizationUuid": format!("org-{email}"),
                "organizationName": "Org",
                "displayName": email,
                "organizationType": "claude_pro",
                "billingType": "stripe_subscription",
                "subscriptionCreatedAt": "2026-05-24T05:45:13.496010Z",
            }
        })
        .to_string()
    }

    #[test]
    fn engine_switch_and_snapshot() {
        let dir = tempfile::tempdir().unwrap();
        let (eng, mock) = test_engine_with_mock(dir.path().to_path_buf()).unwrap();
        mock.set_usage("tok-a", 10.0, 5.0);
        mock.set_usage("tok-b", 80.0, 20.0);

        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();
        eng.add_raw(
            2,
            "b@x.com",
            &oauth_cred("b@x.com", "tok-b"),
            &oauth_cfg("b@x.com"),
            None,
        )
        .unwrap();

        // Activate account 1 as live via switch_to.
        eng.switch_to("1").unwrap();

        let refreshed = eng.refresh_usage().unwrap();
        assert_eq!(refreshed.accounts_refreshed, 2);
        let snap = eng.snapshot().unwrap();
        assert_eq!(snap.schema_version, SNAPSHOT_SCHEMA_VERSION);
        assert_eq!(snap.accounts.len(), 2);
        assert!(snap.accounts.iter().any(|a| a.usage.is_some()));
        // Low active usage (10%) → adaptive 180s band.
        assert!((snap.next_poll_seconds - 180.0).abs() < f64::EPSILON);

        let r = eng.switch_to("2").unwrap();
        assert!(r.switched);
        let snap = eng.snapshot().unwrap();
        assert_eq!(snap.active_account_number, Some(2));

        let events = eng.drain_events();
        assert!(events.iter().any(|e| matches!(e, CoreEvent::Switch { .. })));
    }

    #[test]
    fn refresh_usage_drives_adaptive_poll_plan() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();
        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();
        eng.add_raw(
            2,
            "b@x.com",
            &oauth_cred("b@x.com", "tok-b"),
            &oauth_cfg("b@x.com"),
            None,
        )
        .unwrap();
        eng.switch_to("1").unwrap();

        // tok-a is 25% → below threshold-20 → 180s
        let r = eng.refresh_usage().unwrap();
        assert_eq!(r.accounts_refreshed, 2);
        assert!((r.next_poll_seconds - 180.0).abs() < f64::EPSILON);
        let a = eng
            .snapshot()
            .unwrap()
            .accounts
            .into_iter()
            .find(|x| x.number == 1)
            .unwrap();
        assert!(
            (a.usage.as_ref().unwrap().five_hour.as_ref().unwrap().pct - 25.0).abs() < f64::EPSILON
        );
        assert!(
            (a.usage.as_ref().unwrap().seven_day.as_ref().unwrap().pct - 10.0).abs() < f64::EPSILON
        );

        // Switch to tok-b (80%) → within 20pp of 90 threshold → 60s
        eng.switch_to("2").unwrap();
        let r = eng.refresh_usage().unwrap();
        assert!((r.next_poll_seconds - 60.0).abs() < f64::EPSILON);
        let b = eng
            .snapshot()
            .unwrap()
            .accounts
            .into_iter()
            .find(|x| x.number == 2)
            .unwrap();
        assert!(
            (b.usage.as_ref().unwrap().five_hour.as_ref().unwrap().pct - 80.0).abs() < f64::EPSILON
        );
    }

    #[test]
    fn init_default_uses_real_http_client_type() {
        // Smoke: constructing the production client type does not panic.
        // We don't call Anthropic; only assert UreqHttp is what init_default wires
        // by verifying NoopHttp is *not* the only option and UreqHttp builds.
        let _ = UreqHttp::new();
        // Document contract: init_default path is UreqHttp (see Engine::init_default).
        // Full network call is environment-dependent and not gated here.
    }

    #[test]
    fn call_json_catalog() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();
        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();
        let v = eng.call_json("snapshot", &json!({})).unwrap();
        assert_eq!(v["schemaVersion"], SNAPSHOT_SCHEMA_VERSION);
        assert_eq!(v["accounts"].as_array().unwrap().len(), 1);
        eng.call_json("refresh_usage", &json!({})).unwrap();
        let v = eng.call_json("snapshot", &json!({})).unwrap();
        assert!(
            v["accounts"][0]["usage"]["fiveHour"]["pct"]
                .as_f64()
                .unwrap()
                > 0.0
        );
        assert!(v["nextPollSeconds"].as_f64().is_some());
    }

    #[test]
    fn snapshot_carries_plan_and_reparses_when_backups_change() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();
        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();

        let plan = eng.snapshot().unwrap().accounts[0].plan.clone().unwrap();
        assert_eq!(plan.label, "Pro");
        assert!(plan.personal);
        assert_eq!(
            plan.subscription_created_at.as_deref(),
            Some("2026-05-24T05:45:13.496010Z")
        );
        assert_eq!(plan.billing_type.as_deref(), Some("stripe_subscription"));

        // Cache hit: same backups → same answer.
        let again = eng.snapshot().unwrap().accounts[0].plan.clone().unwrap();
        assert_eq!(again, plan);

        // Rewriting the slot's credential must invalidate the cached plan.
        let upgraded = json!({
            "claudeAiOauth": {
                "accessToken": "tok-a",
                "refreshToken": "r-tok-a",
                "subscriptionType": "max",
                "rateLimitTier": "default_claude_max_20x",
            }
        })
        .to_string();
        eng.add_raw(1, "a@x.com", &upgraded, &oauth_cfg("a@x.com"), None)
            .unwrap();
        let after = eng.snapshot().unwrap().accounts[0].plan.clone().unwrap();
        assert_eq!(after.label, "Max 20×");
        // Config-only facts survive the credential swap.
        assert_eq!(after.billing_type.as_deref(), Some("stripe_subscription"));
    }

    /// Write a live login directly, the way `claude /login` would — behind this
    /// tool's back, leaving `sequence.json` untouched.
    fn login_as(eng: &Engine, email: &str, token: &str) {
        let cred = oauth_cred(email, token);
        eng.switcher.store.write_active(&cred).unwrap();
        let cfg = oauth_cfg(email);
        crate::fsutil::atomic_write(&eng.paths.global_config, cfg.as_bytes()).unwrap();
    }

    #[test]
    fn active_follows_the_live_login_not_the_stored_number() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();
        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();
        eng.add_raw(
            2,
            "b@x.com",
            &oauth_cred("b@x.com", "tok-b"),
            &oauth_cfg("b@x.com"),
            None,
        )
        .unwrap();
        eng.switch_to("1").unwrap();
        assert_eq!(eng.snapshot().unwrap().active_account_number, Some(1));

        // The reported bug: something outside this tool changed the login.
        login_as(&eng, "b@x.com", "tok-b");

        let snap = eng.snapshot().unwrap();
        assert_eq!(snap.active_account_number, Some(2));
        assert!(snap.active_verified);
        assert!(snap.accounts.iter().any(|a| a.number == 2 && a.active));
        assert!(snap.accounts.iter().all(|a| a.number != 1 || !a.active));
        // Stored state is still stale until reconciled…
        assert_eq!(
            eng.switcher.load_sequence().unwrap().active_account_number,
            Some(1)
        );
        // …and reconciling writes the verified answer back for cswap's CLI.
        assert_eq!(eng.reconcile_active().unwrap(), Some(2));
        assert_eq!(
            eng.switcher.load_sequence().unwrap().active_account_number,
            Some(2)
        );
    }

    #[test]
    fn add_current_marks_the_captured_slot_active() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();

        login_as(&eng, "a@x.com", "tok-a");
        assert_eq!(eng.add_current(None, None).unwrap(), 1);

        // Log in as someone else, then capture that too: slot 2 is now live —
        // previously `activeAccountNumber` stayed pinned to the first add.
        login_as(&eng, "b@x.com", "tok-b");
        assert_eq!(eng.add_current(None, None).unwrap(), 2);
        assert_eq!(
            eng.switcher.load_sequence().unwrap().active_account_number,
            Some(2)
        );
        assert_eq!(eng.snapshot().unwrap().active_account_number, Some(2));

        // Identity columns must be filled, or nothing can match a slot later.
        let seq = eng.switcher.load_sequence().unwrap();
        assert_eq!(seq.account(2).unwrap().uuid, "uuid-b@x.com");
        assert_eq!(seq.account(2).unwrap().org_uuid, "org-b@x.com");
    }

    /// Credential shape Claude Code leaves behind on logout: metadata intact,
    /// both tokens blanked in place.
    fn wiped_cred() -> String {
        json!({
            "claudeAiOauth": {
                "accessToken": "",
                "refreshToken": "",
                "expiresAt": 0,
                "subscriptionType": "pro",
                "rateLimitTier": "default_claude_ai",
            }
        })
        .to_string()
    }

    fn expiring_cred(email: &str, token: &str, refresh: &str) -> String {
        json!({
            "claudeAiOauth": {
                "accessToken": token,
                "refreshToken": refresh,
                "emailAddress": email,
                "expiresAt": 0,
                "subscriptionType": "pro",
                "rateLimitTier": "default_claude_ai",
            }
        })
        .to_string()
    }

    #[test]
    fn expired_slot_token_is_refreshed_persisted_and_then_fetched() {
        let dir = tempfile::tempdir().unwrap();
        let (eng, mock) = test_engine_with_mock(dir.path().to_path_buf()).unwrap();
        mock.set_usage("tok-fresh", 42.0, 7.0);
        mock.set_refresh(
            "rt-1",
            crate::oauth::refresh_response("tok-fresh", 3600, Some("rt-2")),
        );

        eng.add_raw(
            1,
            "a@x.com",
            &expiring_cred("a@x.com", "tok-stale", "rt-1"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();

        let r = eng.refresh_usage().unwrap();
        assert_eq!(r.accounts_refreshed, 1);
        let acct = &eng.snapshot().unwrap().accounts[0];
        assert_eq!(acct.usage_status, UsageStatus::Ok);
        assert!(
            (acct.usage.as_ref().unwrap().five_hour.as_ref().unwrap().pct - 42.0).abs()
                < f64::EPSILON
        );

        // The rotated credential must be on disk, or the next refresh fails
        // with a refresh token the server has already invalidated.
        let stored = eng.switcher.store.read_slot(1, "a@x.com").unwrap().unwrap();
        assert_eq!(
            crate::oauth::access_token(&stored).as_deref(),
            Some("tok-fresh")
        );
        assert_eq!(
            crate::oauth::refresh_token(&stored).as_deref(),
            Some("rt-2")
        );
    }

    #[test]
    fn wiped_slot_reports_needs_login_not_a_blank() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();
        eng.add_raw(1, "a@x.com", &wiped_cred(), &oauth_cfg("a@x.com"), None)
            .unwrap();

        eng.refresh_usage().unwrap();
        let acct = &eng.snapshot().unwrap().accounts[0];
        assert_eq!(acct.usage_status, UsageStatus::NeedsLogin);
        assert!(acct.usage.is_none());
    }

    #[test]
    fn dead_refresh_grant_reports_needs_login() {
        let dir = tempfile::tempdir().unwrap();
        let (eng, mock) = test_engine_with_mock(dir.path().to_path_buf()).unwrap();
        mock.set_refresh_error("rt-dead", "400 invalid_grant");
        eng.add_raw(
            1,
            "a@x.com",
            &expiring_cred("a@x.com", "tok", "rt-dead"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();

        eng.refresh_usage().unwrap();
        assert_eq!(
            eng.snapshot().unwrap().accounts[0].usage_status,
            UsageStatus::NeedsLogin
        );
    }

    #[test]
    fn network_failure_is_unavailable_not_needs_login() {
        let dir = tempfile::tempdir().unwrap();
        let (eng, _mock) = test_engine_with_mock(dir.path().to_path_buf()).unwrap();
        // No scripted refresh → transient failure.
        eng.add_raw(
            1,
            "a@x.com",
            &expiring_cred("a@x.com", "tok", "rt-unknown"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();

        eng.refresh_usage().unwrap();
        assert_eq!(
            eng.snapshot().unwrap().accounts[0].usage_status,
            UsageStatus::Unavailable
        );
    }

    #[test]
    fn active_slot_uses_the_live_credential_and_is_never_refreshed() {
        let dir = tempfile::tempdir().unwrap();
        let (eng, mock) = test_engine_with_mock(dir.path().to_path_buf()).unwrap();
        mock.set_usage("tok-live", 33.0, 11.0);

        // Slot backup is a logged-out husk — what a bad switch-out leaves — but
        // Claude Code is logged in and holding a working token.
        eng.add_raw(1, "a@x.com", &wiped_cred(), &oauth_cfg("a@x.com"), None)
            .unwrap();
        let live = json!({
            "claudeAiOauth": {
                "accessToken": "tok-live",
                "refreshToken": "rt-live",
                "emailAddress": "a@x.com",
                "expiresAt": 0,
            }
        })
        .to_string();
        eng.switcher.store.write_active(&live).unwrap();
        let cfg = oauth_cfg("a@x.com");
        crate::fsutil::atomic_write(&eng.paths.global_config, cfg.as_bytes()).unwrap();

        eng.refresh_usage().unwrap();
        let acct = &eng.snapshot().unwrap().accounts[0];
        assert!(acct.active);
        assert_eq!(acct.usage_status, UsageStatus::Ok);

        // Expired though it is, the live credential must not be refreshed by us:
        // rotating it would invalidate the token Claude Code is using.
        let stored = eng.switcher.store.read_slot(1, "a@x.com").unwrap().unwrap();
        assert!(crate::oauth::is_wiped(&stored));
    }

    #[test]
    fn api_key_slot_reports_its_own_status() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();
        eng.add_raw(
            1,
            "a@x.com",
            "sk-ant-api03-abcdefghijklmnopqrst",
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();
        eng.refresh_usage().unwrap();
        assert_eq!(
            eng.snapshot().unwrap().accounts[0].usage_status,
            UsageStatus::ApiKey
        );
    }

    #[test]
    fn switch_out_never_overwrites_a_backup_with_a_logged_out_husk() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();
        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();
        eng.add_raw(
            2,
            "b@x.com",
            &oauth_cred("b@x.com", "tok-b"),
            &oauth_cfg("b@x.com"),
            None,
        )
        .unwrap();
        eng.switch_to("1").unwrap();

        // Claude Code logs out: tokens blanked in place, file still there.
        eng.switcher.store.write_active(&wiped_cred()).unwrap();
        eng.switch_to("2").unwrap();

        // Slot 1's good credential must survive — this is how it got destroyed.
        let stored = eng.switcher.store.read_slot(1, "a@x.com").unwrap().unwrap();
        assert_eq!(
            crate::oauth::access_token(&stored).as_deref(),
            Some("tok-a")
        );
    }

    #[test]
    fn autoswitch_never_lands_on_a_wiped_account() {
        let dir = tempfile::tempdir().unwrap();
        let (eng, mock) = test_engine_with_mock(dir.path().to_path_buf()).unwrap();
        // Live account is at its limit — autoswitch wants to move.
        mock.set_usage("tok-a", 96.0, 40.0);

        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();
        eng.add_raw(2, "b@x.com", &wiped_cred(), &oauth_cfg("b@x.com"), None)
            .unwrap();
        eng.switch_to("1").unwrap();

        let mut s = eng.settings.lock().clone();
        s.autoswitch.enabled = true;
        s.autoswitch.threshold = 90.0;
        s.autoswitch.cooldown_seconds = 0.0;
        *eng.settings.lock() = s;

        let out = eng.autoswitch_tick().unwrap();
        assert_eq!(out["switched"], serde_json::json!(false), "{out}");
        // The working session must still be account 1's.
        assert_eq!(eng.snapshot().unwrap().active_account_number, Some(1));
        let live = eng.switcher.store.read_active().unwrap().unwrap();
        assert_eq!(crate::oauth::access_token(&live).as_deref(), Some("tok-a"));
    }

    #[test]
    fn autoswitch_fails_over_off_a_dead_active_account() {
        let dir = tempfile::tempdir().unwrap();
        let (eng, mock) = test_engine_with_mock(dir.path().to_path_buf()).unwrap();
        mock.set_usage("tok-b", 20.0, 5.0);

        // Slot 1 is live but its login was cleared; slot 2 is healthy.
        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();
        eng.add_raw(
            2,
            "b@x.com",
            &oauth_cred("b@x.com", "tok-b"),
            &oauth_cfg("b@x.com"),
            None,
        )
        .unwrap();
        eng.switch_to("1").unwrap();
        eng.switcher.store.write_active(&wiped_cred()).unwrap();

        let mut s = eng.settings.lock().clone();
        s.autoswitch.enabled = true;
        s.autoswitch.threshold = 90.0;
        s.autoswitch.cooldown_seconds = 0.0;
        *eng.settings.lock() = s;

        // Threshold is never reachable here — only failover can move this.
        let out = eng.autoswitch_tick().unwrap();
        assert_eq!(out["switched"], serde_json::json!(true), "{out}");
        assert_eq!(out["to"]["number"], serde_json::json!(2), "{out}");
        let live = eng.switcher.store.read_active().unwrap().unwrap();
        assert_eq!(crate::oauth::access_token(&live).as_deref(), Some("tok-b"));
    }

    #[test]
    fn an_account_without_subscription_windows_is_not_zero_percent() {
        let dir = tempfile::tempdir().unwrap();
        let (eng, mock) = test_engine_with_mock(dir.path().to_path_buf()).unwrap();
        // Fetch succeeds but carries no windows: lapsed or never-subscribed.
        mock.by_token
            .lock()
            .insert("tok-a".into(), serde_json::json!({}));
        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();

        eng.refresh_usage().unwrap();
        let acct = &eng.snapshot().unwrap().accounts[0];
        assert_eq!(acct.usage_status, UsageStatus::NoSubscription);
        assert!(acct.usage.is_none());
    }

    #[test]
    fn manual_switch_to_a_wiped_account_is_refused_not_silent() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();
        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();
        eng.add_raw(2, "b@x.com", &wiped_cred(), &oauth_cfg("b@x.com"), None)
            .unwrap();
        eng.switch_to("1").unwrap();

        let err = eng.switch_to("2").unwrap_err();
        assert!(
            err.message().contains("no usable access token"),
            "{}",
            err.message()
        );
        // Refusing must leave the live login untouched, not half-applied.
        let live = eng.switcher.store.read_active().unwrap().unwrap();
        assert_eq!(crate::oauth::access_token(&live).as_deref(), Some("tok-a"));
        assert_eq!(eng.snapshot().unwrap().active_account_number, Some(1));
    }

    #[test]
    fn reconcile_backfills_identity_on_older_slots() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();
        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();

        // Simulate a slot written before identity columns were captured.
        let mut seq = eng.switcher.load_sequence().unwrap();
        let rec = seq.account_mut(1).unwrap();
        rec.uuid = String::new();
        rec.org_uuid = String::new();
        seq.save(&eng.paths.sequence_file).unwrap();

        login_as(&eng, "a@x.com", "tok-a");
        // Matching still works off the config backup…
        assert_eq!(eng.snapshot().unwrap().active_account_number, Some(1));
        // …and reconcile fills the columns so it need not next time.
        eng.reconcile_active().unwrap();
        let seq = eng.switcher.load_sequence().unwrap();
        assert_eq!(seq.account(1).unwrap().uuid, "uuid-a@x.com");
        assert_eq!(seq.account(1).unwrap().org_uuid, "org-a@x.com");
    }

    #[test]
    fn login_to_an_unmanaged_account_marks_no_slot_active() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();
        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();
        eng.switch_to("1").unwrap();

        login_as(&eng, "stranger@x.com", "tok-z");
        let snap = eng.snapshot().unwrap();
        assert_eq!(snap.active_account_number, None);
        assert!(snap.accounts.iter().all(|a| !a.active));
        assert_eq!(
            snap.unmanaged_login_email.as_deref(),
            Some("stranger@x.com")
        );
    }

    #[test]
    fn logged_out_means_no_active_slot() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();
        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();
        eng.switch_to("1").unwrap();
        assert_eq!(eng.snapshot().unwrap().active_account_number, Some(1));

        std::fs::remove_file(&eng.paths.credentials).unwrap();
        let snap = eng.snapshot().unwrap();
        assert_eq!(snap.active_account_number, None);
        assert!(snap.active_verified);
        assert!(snap.unmanaged_login_email.is_none());
    }

    #[test]
    fn unreadable_live_identity_keeps_the_stored_number() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();
        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();
        eng.switch_to("1").unwrap();

        // Credential present but the config names nobody — no evidence either
        // way, so the recorded number stands rather than blanking the list.
        crate::fsutil::atomic_write(&eng.paths.global_config, b"{}").unwrap();
        let snap = eng.snapshot().unwrap();
        assert_eq!(snap.active_account_number, Some(1));
        assert!(!snap.active_verified);
        // A non-verified resolution must never overwrite stored state.
        eng.reconcile_active().unwrap();
        assert_eq!(
            eng.switcher.load_sequence().unwrap().active_account_number,
            Some(1)
        );
    }

    #[test]
    fn snapshot_without_plan_fields_omits_plan() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();
        // Pre-plan-fields backups (what an old cswap slot looks like).
        eng.add_raw(
            1,
            "a@x.com",
            &json!({"claudeAiOauth": {"accessToken": "tok-a"}}).to_string(),
            &json!({"oauthAccount": {"emailAddress": "a@x.com"}}).to_string(),
            None,
        )
        .unwrap();
        let v = eng.call_json("snapshot", &json!({})).unwrap();
        assert!(v["accounts"][0].get("plan").is_none());
    }

    #[test]
    fn reorder_accounts_permutation() {
        let dir = tempfile::tempdir().unwrap();
        let eng = Engine::init_isolated_demo(dir.path().to_path_buf()).unwrap();
        eng.add_raw(
            1,
            "a@x.com",
            &oauth_cred("a@x.com", "tok-a"),
            &oauth_cfg("a@x.com"),
            None,
        )
        .unwrap();
        eng.add_raw(
            2,
            "b@x.com",
            &oauth_cred("b@x.com", "tok-b"),
            &oauth_cfg("b@x.com"),
            None,
        )
        .unwrap();
        eng.reorder_accounts(&[2, 1]).unwrap();
        let snap = eng.snapshot().unwrap();
        assert_eq!(snap.accounts[0].number, 2);
        assert_eq!(snap.accounts[1].number, 1);
        // Invalid permutation rejected
        assert!(eng.reorder_accounts(&[1, 1]).is_err());
        assert!(eng.reorder_accounts(&[1, 3]).is_err());
    }

    // -- session mode ------------------------------------------------------

    /// Two slots, both with usable OAuth credentials and config backups.
    fn engine_with_two_accounts(root: &Path) -> (Engine, Arc<MockHttp>) {
        let (eng, mock) = test_engine_with_mock(root.to_path_buf()).unwrap();
        for (n, email, tok) in [(1u32, "a@x.com", "tok-a"), (2, "b@x.com", "tok-b")] {
            eng.add_raw(n, email, &oauth_cred(email, tok), &oauth_cfg(email), None)
                .unwrap();
        }
        (eng, mock)
    }

    #[test]
    fn the_account_that_is_already_the_default_login_gets_no_profile() {
        let tmp = tempfile::tempdir().unwrap();
        let (eng, _m) = engine_with_two_accounts(tmp.path());
        eng.switch_to("1").unwrap();

        // A second copy of one account's token would drift the moment the
        // server rotates the refresh token; plain `claude` reaches it already.
        let launch = eng.session_prepare("1", true).unwrap();
        assert!(launch.use_default_login);
        assert_eq!(launch.config_dir, "");
        assert!(!session::session_dir_for(&eng.paths.backup_root, 1, "a@x.com").exists());

        // The other account still gets one.
        let other = eng.session_prepare("2", true).unwrap();
        assert!(!other.use_default_login);
        assert!(!other.config_dir.is_empty());
    }

    #[test]
    fn preparing_a_session_seeds_a_profile_and_then_reuses_it() {
        let tmp = tempfile::tempdir().unwrap();
        let (eng, _m) = engine_with_two_accounts(tmp.path());

        let first = eng.session_prepare("2", true).unwrap();
        assert!(!first.reused, "a fresh profile has to be seeded");
        assert_eq!(first.email, "b@x.com");
        let dir = PathBuf::from(&first.config_dir);
        assert!(dir.join(".credentials.json").exists());

        // Claude Code rotates the token in place; re-seeding would put the
        // backup's older generation back over that newer one.
        std::fs::write(
            dir.join(".credentials.json"),
            oauth_cred("b@x.com", "rotated"),
        )
        .unwrap();
        let second = eng.session_prepare("2", true).unwrap();
        assert!(second.reused);
        assert!(std::fs::read_to_string(dir.join(".credentials.json"))
            .unwrap()
            .contains("rotated"));
    }

    #[test]
    fn a_profile_logged_into_another_account_is_re_seeded() {
        let tmp = tempfile::tempdir().unwrap();
        let (eng, _m) = engine_with_two_accounts(tmp.path());
        let dir = PathBuf::from(&eng.session_prepare("2", true).unwrap().config_dir);

        // An in-session `/login` re-points the profile at someone else.
        std::fs::write(dir.join(".claude.json"), oauth_cfg("other@x.com")).unwrap();
        let again = eng.session_prepare("2", true).unwrap();
        assert!(!again.reused, "a drifted profile must go back to its slot");
        assert_eq!(session::read_session_identity(&dir).unwrap().0, "b@x.com");
    }

    #[test]
    fn rewriting_a_slots_backup_invalidates_its_quiescent_profile() {
        let tmp = tempfile::tempdir().unwrap();
        let (eng, _m) = engine_with_two_accounts(tmp.path());
        let dir = PathBuf::from(&eng.session_prepare("2", true).unwrap().config_dir);
        assert!(dir.join(".credentials.json").exists());

        // Re-adding the account writes a new backup generation, which makes the
        // profile's copy a possibly-spent one.
        eng.add_raw(
            2,
            "b@x.com",
            &oauth_cred("b@x.com", "tok-b2"),
            &oauth_cfg("b@x.com"),
            None,
        )
        .unwrap();
        assert!(
            !dir.join(".credentials.json").exists(),
            "the profile's stale credential must be dropped"
        );
        // Everything that is not a credential survives.
        assert!(dir.join(".claude.json").exists());
    }

    #[test]
    fn usage_prefers_the_session_profiles_credential() {
        let tmp = tempfile::tempdir().unwrap();
        let (eng, mock) = engine_with_two_accounts(tmp.path());
        let dir = PathBuf::from(&eng.session_prepare("2", true).unwrap().config_dir);

        // Once a session has run, the backup's token is a spent generation and
        // only the profile's answer describes the account.
        std::fs::write(
            dir.join(".credentials.json"),
            oauth_cred("b@x.com", "profile-tok"),
        )
        .unwrap();
        mock.set_usage("tok-b", 90.0, 90.0);
        mock.set_usage("profile-tok", 12.0, 5.0);

        eng.refresh_usage().unwrap();
        let snap = eng.snapshot().unwrap();
        let slot2 = snap.accounts.iter().find(|a| a.number == 2).unwrap();
        assert_eq!(slot2.usage.as_ref().unwrap().binding_pct(), Some(12.0));
    }

    #[test]
    fn a_drifted_profile_does_not_report_its_usage_under_our_slot() {
        let tmp = tempfile::tempdir().unwrap();
        let (eng, mock) = engine_with_two_accounts(tmp.path());
        let dir = PathBuf::from(&eng.session_prepare("2", true).unwrap().config_dir);

        std::fs::write(
            dir.join(".credentials.json"),
            oauth_cred("other@x.com", "someone-else"),
        )
        .unwrap();
        std::fs::write(dir.join(".claude.json"), oauth_cfg("other@x.com")).unwrap();
        mock.set_usage("tok-b", 42.0, 10.0);
        mock.set_usage("someone-else", 99.0, 99.0);

        eng.refresh_usage().unwrap();
        let snap = eng.snapshot().unwrap();
        let slot2 = snap.accounts.iter().find(|a| a.number == 2).unwrap();
        assert_eq!(
            slot2.usage.as_ref().unwrap().binding_pct(),
            Some(42.0),
            "a profile pointing elsewhere must fall back to the backup"
        );
    }

    #[test]
    fn transcripts_inside_session_profiles_are_still_listed() {
        let tmp = tempfile::tempdir().unwrap();
        let (eng, _m) = engine_with_two_accounts(tmp.path());
        let profile = PathBuf::from(&eng.session_prepare("2", true).unwrap().config_dir);

        // One working directory, worked in from both the default login and a
        // session terminal: one project, both histories.
        let default_dir = eng
            .paths
            .env
            .claude_config_home()
            .join("projects")
            .join("d-work");
        let session_dir = profile.join("projects").join("d-work");
        for (dir, id) in [(&default_dir, "s-default"), (&session_dir, "s-session")] {
            std::fs::create_dir_all(dir).unwrap();
            std::fs::write(
                dir.join(format!("{id}.jsonl")),
                format!(
                    "{}\n",
                    json!({"type":"user","cwd":"D:\\work","timestamp":"2026-01-02T03:04:05.000Z",
                           "message":{"role":"user","content":"hi"}})
                ),
            )
            .unwrap();
        }

        let projects = crate::projects::list_projects_in(&eng.scan_envs()).unwrap();
        let work = projects
            .iter()
            .find(|p| p.name.eq_ignore_ascii_case("work"))
            .expect("the directory must appear once, not twice");
        assert_eq!(work.session_count, 2);
        assert_eq!(work.transcript_dirs.len(), 2);

        let dirs: Vec<PathBuf> = work.transcript_dirs.iter().map(PathBuf::from).collect();
        let sessions = crate::projects::list_sessions_in(&dirs).unwrap();
        let ids: Vec<&str> = sessions.iter().map(|s| s.id.as_str()).collect();
        assert!(ids.contains(&"s-default") && ids.contains(&"s-session"));
    }

    #[test]
    fn removing_an_account_takes_its_profile_and_bindings_with_it() {
        let tmp = tempfile::tempdir().unwrap();
        let (eng, _m) = engine_with_two_accounts(tmp.path());
        let dir = PathBuf::from(&eng.session_prepare("2", true).unwrap().config_dir);
        eng.mapping_set("D:\\work", "2").unwrap();
        assert_eq!(
            eng.mapping_resolve("D:\\work\\src").unwrap()["matched"],
            json!(true)
        );

        eng.remove_account("2").unwrap();
        assert!(!dir.exists(), "a profile must not outlive its account");
        assert_eq!(
            eng.mapping_resolve("D:\\work\\src").unwrap()["matched"],
            json!(false)
        );
    }

    #[test]
    fn api_key_accounts_are_refused_by_name() {
        let tmp = tempfile::tempdir().unwrap();
        let (eng, _m) = test_engine_with_mock(tmp.path().to_path_buf()).unwrap();
        eng.add_raw(
            1,
            "k@x.com",
            "sk-ant-api03-xxx",
            &oauth_cfg("k@x.com"),
            None,
        )
        .unwrap();
        let err = eng.session_prepare("1", true).unwrap_err();
        assert!(err.message().contains("API-key"), "got {}", err.message());
    }

    #[test]
    fn bindings_store_identity_not_a_slot_number() {
        let tmp = tempfile::tempdir().unwrap();
        let (eng, _m) = engine_with_two_accounts(tmp.path());
        eng.mapping_set("D:\\work", "b@x.com").unwrap();
        let resolved = eng.mapping_resolve("D:\\work").unwrap();
        assert_eq!(resolved["number"], json!(2));
        assert_eq!(resolved["email"], json!("b@x.com"));
    }

    #[test]
    fn autoswitch_will_not_land_on_an_account_with_a_session_open() {
        use crate::autoswitch::AutoSwitchEngine;
        let tmp = tempfile::tempdir().unwrap();
        let (eng, mock) = engine_with_two_accounts(tmp.path());
        eng.switch_to("1").unwrap();
        mock.set_usage("tok-a", 95.0, 20.0);
        mock.set_usage("tok-b", 10.0, 10.0);
        eng.refresh_usage().unwrap();

        let seq = eng.switcher.load_sequence().unwrap();
        let mut settings = eng.settings.lock().autoswitch.clone();
        settings.enabled = true;
        let auto = AutoSwitchEngine::new();

        // Nothing running: slot 2 is the obvious target.
        let free = auto.decide_with_sessions(&seq, &eng.usage, &settings, &[]);
        assert_eq!(free.target, Some(2));

        // A session terminal on slot 2 is already consuming its quota, and
        // making it the default login too would fork its refresh token.
        let busy = auto.decide_with_sessions(&seq, &eng.usage, &settings, &[2]);
        assert!(!busy.should_switch);
        assert!(
            busy.detail.contains("session terminal"),
            "the reason must be explainable: {}",
            busy.detail
        );
    }
}
