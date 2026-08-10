//! 5-hour usage-window warmup ("window guardian").
//!
//! Claude's 5h quota window is anchored by the first *inference* request and
//! cannot be moved once open. Polling `/api/oauth/usage` does not open it.
//! This module opens a full window at a chosen local time (typically before
//! work hours) so the work day can cover more complete buckets.
//!
//! **Multi-account stagger:** N is the count of *subscribed + healthy* OAuth
//! accounts only (`UsageStatus::Ok`). Phase for index `i` is
//! `Aᵢ = A₀ + i·(5/N)` hours. Broken / no-sub / API-key slots do not enter N.
//!
//! Pure decision logic is unit-tested without spawning; the runner is only
//! used when the engine actually fires a warmup.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use chrono::{DateTime, Duration as ChronoDuration, Local, NaiveDate, NaiveTime, Timelike, Utc};
use serde::{Deserialize, Serialize};

use crate::session;
use crate::settings::WarmupSettings;

/// Default Haiku model — any model anchors the account-level window, and this
/// one burns the least quota on the opening request.
pub const DEFAULT_WARMUP_MODEL: &str = "claude-haiku-4-5-20251001";

/// Minimum gap between fire attempts for one account (in-process).
pub const ATTEMPT_COOLDOWN: Duration = Duration::from_secs(15 * 60);

/// Window length in hours (Anthropic 5h rolling bucket).
const WINDOW_HOURS: f64 = 5.0;

/// Why a tick did not fire (stable strings for diagnostics / UI).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SkipReason {
    Disabled,
    BeforeAnchor,
    AfterWorkEnd,
    WindowActive,
    Cooldown,
    NoEligibleAccount,
    BadSchedule,
}

impl SkipReason {
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Disabled => "disabled",
            Self::BeforeAnchor => "before-anchor",
            Self::AfterWorkEnd => "after-work-end",
            Self::WindowActive => "window-active",
            Self::Cooldown => "cooldown",
            Self::NoEligibleAccount => "no-eligible-account",
            Self::BadSchedule => "bad-schedule",
        }
    }
}

/// One account's decision for this tick.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum WarmupDecision {
    Fire,
    Skip(SkipReason),
}

/// Result of a fire attempt (spawn only — confirmation is next poll's `resets_at`).
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WarmupFireResult {
    pub number: u32,
    pub email: String,
    pub ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub detail: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub config_dir: Option<String>,
}

/// Aggregate tick outcome returned to the engine / FFI.
#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WarmupTickResult {
    pub enabled: bool,
    pub fired: Vec<WarmupFireResult>,
    pub skipped: Vec<WarmupSkipEntry>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WarmupSkipEntry {
    pub number: u32,
    pub reason: String,
}

/// In-process attempt ledger so a failed fire is not retried every poll tick.
#[derive(Default)]
pub struct WarmupState {
    last_attempt: HashMap<u32, Instant>,
}

impl WarmupState {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    pub fn mark_attempted(&mut self, number: u32, at: Instant) {
        self.last_attempt.insert(number, at);
    }

    #[must_use]
    pub fn last_attempt(&self, number: u32) -> Option<Instant> {
        self.last_attempt.get(&number).copied()
    }
}

/// Parse `"HH:MM"` or `"H:MM"` into a local wall-clock time.
#[must_use]
pub fn parse_hhmm(s: &str) -> Option<NaiveTime> {
    let s = s.trim();
    let (h, m) = s.split_once(':')?;
    let hour: u32 = h.parse().ok()?;
    let min: u32 = m.parse().ok()?;
    NaiveTime::from_hms_opt(hour, min, 0)
}

/// Work-window length in hours (`end` after `start`; overnight not supported).
#[must_use]
pub fn work_hours(start: NaiveTime, end: NaiveTime) -> Option<f64> {
    let secs = (end - start).num_seconds();
    if secs <= 0 {
        return None;
    }
    Some(secs as f64 / 3600.0)
}

/// Optimal first-request anchor for a work window.
///
/// Puts the first 5h boundary at `S + (W − 5) / 2` so the leading and trailing
/// partial buckets are equal; the anchor is five hours before that boundary:
///
/// ```text
/// anchor = S + (W − 5) / 2 − 5
/// ```
///
/// Example: 09:00–18:00 (W=9) → anchor 06:00, boundaries 11:00 and 16:00.
#[must_use]
pub fn compute_base_anchor(work_start: NaiveTime, work_end: NaiveTime) -> Option<NaiveTime> {
    let w = work_hours(work_start, work_end)?;
    let offset_hours = (w - WINDOW_HOURS) / 2.0 - WINDOW_HOURS;
    shift_time(work_start, offset_hours)
}

/// Stagger account `index` of `count` so peaks do not align.
///
/// Offset = `index * (5 / count)` hours from the base anchor.
#[must_use]
pub fn stagger_anchor(base: NaiveTime, index: usize, count: usize) -> NaiveTime {
    if count <= 1 || index == 0 {
        return base;
    }
    let hours = (index as f64) * (WINDOW_HOURS / count as f64);
    shift_time(base, hours).unwrap_or(base)
}

fn shift_time(t: NaiveTime, hours: f64) -> Option<NaiveTime> {
    let base_secs = i64::from(t.num_seconds_from_midnight());
    let delta = (hours * 3600.0).round() as i64;
    let mut secs = base_secs + delta;
    // Allow anchors before midnight of the work day (e.g. 06:00 for a 09:00 start).
    // Clamp to a single local day range via chrono on today's date in decide().
    // Here we only need a NaiveTime on a synthetic day.
    // Normalize into 0..86400 by wrapping? No — negative means previous calendar
    // day; we keep that as a signed offset handled at decide-time.
    // NaiveTime cannot represent "yesterday 22:00", so for extreme W we clamp.
    if secs < 0 {
        secs = 0;
    }
    if secs >= 86_400 {
        secs = 86_399;
    }
    let h = (secs / 3600) as u32;
    let m = ((secs % 3600) / 60) as u32;
    let s = (secs % 60) as u32;
    NaiveTime::from_hms_opt(h, m, s)
}

/// Whether the 5h window is already open (cannot be moved or cancelled).
///
/// `resets_at` in the future is the only reliable signal — usage poll does not
/// open a window, and a 0% open window still carries a reset timestamp.
#[must_use]
pub fn window_is_active(resets_at: Option<&str>, now: DateTime<Utc>) -> bool {
    let Some(raw) = resets_at.map(str::trim).filter(|s| !s.is_empty()) else {
        return false;
    };
    let parsed = DateTime::parse_from_rfc3339(raw)
        .map(|d| d.with_timezone(&Utc))
        .or_else(|_| {
            // Some payloads omit the offset; treat as UTC.
            DateTime::parse_from_str(raw, "%Y-%m-%dT%H:%M:%S%.fZ")
                .or_else(|_| DateTime::parse_from_str(raw, "%Y-%m-%dT%H:%M:%SZ"))
                .map(|d| d.with_timezone(&Utc))
        });
    match parsed {
        Ok(reset) => reset > now + chrono::Duration::seconds(60),
        Err(_) => false,
    }
}

/// How a fire decision is constrained.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum WarmupMode {
    /// Guardian path: enabled + schedule + cooldown + not already open.
    Scheduled,
    /// Manual "warm now": ignore schedule and enabled; still skip open windows
    /// and honour a short cooldown so a double-click cannot burn two buckets.
    Force,
}

/// Decide whether to fire a warmup for one account at `now_local`.
#[must_use]
pub fn decide(
    settings: &WarmupSettings,
    now_local: DateTime<Local>,
    account_index: usize,
    account_count: usize,
    five_hour_resets_at: Option<&str>,
    last_attempt: Option<Instant>,
    now_instant: Instant,
    cooldown: Duration,
    mode: WarmupMode,
) -> WarmupDecision {
    if mode == WarmupMode::Scheduled && !settings.enabled {
        return WarmupDecision::Skip(SkipReason::Disabled);
    }
    let Some(start) = parse_hhmm(&settings.work_start) else {
        return WarmupDecision::Skip(SkipReason::BadSchedule);
    };
    let Some(end) = parse_hhmm(&settings.work_end) else {
        return WarmupDecision::Skip(SkipReason::BadSchedule);
    };
    if work_hours(start, end).is_none() {
        return WarmupDecision::Skip(SkipReason::BadSchedule);
    }
    let Some(base) = compute_base_anchor(start, end) else {
        return WarmupDecision::Skip(SkipReason::BadSchedule);
    };
    let anchor = stagger_anchor(base, account_index, account_count);

    if window_is_active(five_hour_resets_at, now_local.with_timezone(&Utc)) {
        return WarmupDecision::Skip(SkipReason::WindowActive);
    }

    if mode == WarmupMode::Scheduled {
        let t = now_local.time();
        // Fire window: [anchor, work_end]. Before anchor we wait; after work end
        // there is no benefit to burning a bucket that will mostly sit unused.
        if t < anchor {
            return WarmupDecision::Skip(SkipReason::BeforeAnchor);
        }
        if t > end {
            return WarmupDecision::Skip(SkipReason::AfterWorkEnd);
        }
    }

    if let Some(prev) = last_attempt {
        if now_instant.saturating_duration_since(prev) < cooldown {
            return WarmupDecision::Skip(SkipReason::Cooldown);
        }
    }

    WarmupDecision::Fire
}

/// Next time the guardian would try to open a window for this account.
///
/// - Window already open → `None` (use `resets_at` instead).
/// - Still before today's anchor → today's staggered anchor.
/// - Inside the fire window with no open bucket → "now" (`now_local`).
/// - After work end → tomorrow's staggered anchor.
#[must_use]
pub fn next_planned_fire(
    settings: &WarmupSettings,
    now_local: DateTime<Local>,
    account_index: usize,
    account_count: usize,
    five_hour_resets_at: Option<&str>,
) -> Option<DateTime<Local>> {
    if !settings.enabled {
        return None;
    }
    if window_is_active(five_hour_resets_at, now_local.with_timezone(&Utc)) {
        return None;
    }
    let start = parse_hhmm(&settings.work_start)?;
    let end = parse_hhmm(&settings.work_end)?;
    let base = compute_base_anchor(start, end)?;
    let anchor = stagger_anchor(base, account_index, account_count);
    let today = now_local.date_naive();
    let t = now_local.time();
    if t < anchor {
        return local_on(today, anchor);
    }
    if t <= end {
        // Eligible to fire on this tick — surface "now" so the UI can say so.
        return Some(now_local);
    }
    let tomorrow = today + ChronoDuration::days(1);
    local_on(tomorrow, anchor)
}

/// Planned next-window open time as RFC3339 local (display helper).
#[must_use]
pub fn next_anchor_rfc3339(
    settings: &WarmupSettings,
    now_local: DateTime<Local>,
    account_index: usize,
    account_count: usize,
    five_hour_resets_at: Option<&str>,
) -> Option<String> {
    next_planned_fire(
        settings,
        now_local,
        account_index,
        account_count,
        five_hour_resets_at,
    )
    .map(|dt| dt.to_rfc3339())
}

fn local_on(date: NaiveDate, time: NaiveTime) -> Option<DateTime<Local>> {
    date.and_time(time).and_local_timezone(Local).single()
}

/// Resolve the `claude` executable the same way the GUI does.
#[must_use]
pub fn find_claude_executable() -> Option<PathBuf> {
    if let Some(p) = find_on_path("claude") {
        return Some(p);
    }
    // Claude Code installer default; PATH may lag after install.
    let home = std::env::var_os("USERPROFILE")
        .or_else(|| std::env::var_os("HOME"))
        .map(PathBuf::from)?;
    let candidate = home.join(".local").join("bin").join(claude_bin_name());
    candidate.is_file().then_some(candidate)
}

fn claude_bin_name() -> &'static str {
    if cfg!(windows) {
        "claude.exe"
    } else {
        "claude"
    }
}

fn find_on_path(command: &str) -> Option<PathBuf> {
    let path = std::env::var_os("PATH")?;
    let exts: Vec<String> = if cfg!(windows) {
        std::env::var("PATHEXT")
            .unwrap_or_else(|_| ".COM;.EXE;.BAT;.CMD".into())
            .split(';')
            .filter(|s| !s.is_empty())
            .map(|s| s.to_string())
            .collect()
    } else {
        vec![String::new()]
    };
    for dir in std::env::split_paths(&path) {
        for ext in &exts {
            let name = if ext.is_empty() {
                command.to_string()
            } else if command
                .to_ascii_lowercase()
                .ends_with(&ext.to_ascii_lowercase())
            {
                command.to_string()
            } else {
                format!("{command}{ext}")
            };
            let candidate = dir.join(&name);
            if candidate.is_file() {
                return Some(candidate);
            }
        }
    }
    None
}

/// Spawn a minimal print-mode Claude Code request that anchors the 5h window.
///
/// - `CLAUDE_CONFIG_DIR` points at the account session profile (never touches
///   the default login when a profile is used).
/// - Working directory is an empty temp dir so project `CLAUDE.md` is not loaded.
/// - Auth-override env vars are scrubbed so an inherited API key cannot hijack.
/// - `--bare` is intentionally *not* used (it ignores OAuth / keychain).
pub fn spawn_warmup(
    claude: &Path,
    config_dir: Option<&Path>,
    model: &str,
    work_dir: &Path,
) -> Result<(), String> {
    std::fs::create_dir_all(work_dir).map_err(|e| format!("warmup work dir: {e}"))?;

    let mut cmd = Command::new(claude);
    cmd.arg("-p")
        .arg("hi")
        .arg("--model")
        .arg(model)
        .arg("--strict-mcp-config")
        .arg("--no-session-persistence")
        .current_dir(work_dir)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());

    if let Some(dir) = config_dir {
        cmd.env("CLAUDE_CONFIG_DIR", dir);
    }

    for key in session::AUTH_OVERRIDE_ENV_VARS {
        cmd.env_remove(key);
    }

    // Detached enough that a slow Haiku reply does not block the poll thread.
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        cmd.creation_flags(CREATE_NO_WINDOW);
    }

    cmd.spawn()
        .map(|_| ())
        .map_err(|e| format!("spawn claude warmup: {e}"))
}

/// Snapshot fields the UI can show without re-deriving schedule math.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WarmupAccountPlan {
    pub number: u32,
    pub anchor_local: String,
    pub window_active: bool,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::settings::WarmupSettings;

    fn t(h: u32, m: u32) -> NaiveTime {
        NaiveTime::from_hms_opt(h, m, 0).unwrap()
    }

    #[test]
    fn parse_hhmm_basic() {
        assert_eq!(parse_hhmm("09:00"), Some(t(9, 0)));
        assert_eq!(parse_hhmm("9:30"), Some(t(9, 30)));
        assert_eq!(parse_hhmm("18:00"), Some(t(18, 0)));
        assert!(parse_hhmm("25:00").is_none());
        assert!(parse_hhmm("nope").is_none());
    }

    #[test]
    fn anchor_nine_to_eighteen() {
        // S=09:00 W=9 → 09:00 + (9-5)/2 - 5 = 06:00
        assert_eq!(compute_base_anchor(t(9, 0), t(18, 0)), Some(t(6, 0)));
    }

    #[test]
    fn anchor_nine_to_nineteen() {
        // W=10 → 09:00 + 2.5 - 5 = 06:30
        assert_eq!(compute_base_anchor(t(9, 0), t(19, 0)), Some(t(6, 30)));
    }

    #[test]
    fn stagger_two_accounts() {
        let base = t(6, 0);
        assert_eq!(stagger_anchor(base, 0, 2), t(6, 0));
        // 5/2 = 2.5h
        assert_eq!(stagger_anchor(base, 1, 2), t(8, 30));
    }

    #[test]
    fn window_active_future_reset() {
        let now = Utc::now();
        let future = (now + chrono::Duration::hours(4)).to_rfc3339();
        assert!(window_is_active(Some(&future), now));
        let past = (now - chrono::Duration::hours(1)).to_rfc3339();
        assert!(!window_is_active(Some(&past), now));
        assert!(!window_is_active(None, now));
        assert!(!window_is_active(Some(""), now));
    }

    #[test]
    fn decide_before_anchor_skips() {
        let settings = WarmupSettings {
            enabled: true,
            work_start: "09:00".into(),
            work_end: "18:00".into(),
            ..Default::default()
        };
        // 05:00 local — before 06:00 anchor
        let now = Local::now()
            .date_naive()
            .and_time(t(5, 0))
            .and_local_timezone(Local)
            .single()
            .unwrap();
        let d = decide(
            &settings,
            now,
            0,
            1,
            None,
            None,
            Instant::now(),
            ATTEMPT_COOLDOWN,
            WarmupMode::Scheduled,
        );
        assert_eq!(d, WarmupDecision::Skip(SkipReason::BeforeAnchor));
    }

    #[test]
    fn decide_fires_in_window() {
        let settings = WarmupSettings {
            enabled: true,
            work_start: "09:00".into(),
            work_end: "18:00".into(),
            ..Default::default()
        };
        let now = Local::now()
            .date_naive()
            .and_time(t(7, 0))
            .and_local_timezone(Local)
            .single()
            .unwrap();
        let d = decide(
            &settings,
            now,
            0,
            1,
            None,
            None,
            Instant::now(),
            ATTEMPT_COOLDOWN,
            WarmupMode::Scheduled,
        );
        assert_eq!(d, WarmupDecision::Fire);
    }

    #[test]
    fn force_fires_before_anchor() {
        let settings = WarmupSettings {
            enabled: false, // force ignores the toggle
            work_start: "09:00".into(),
            work_end: "18:00".into(),
            ..Default::default()
        };
        let now = Local::now()
            .date_naive()
            .and_time(t(5, 0))
            .and_local_timezone(Local)
            .single()
            .unwrap();
        let d = decide(
            &settings,
            now,
            0,
            1,
            None,
            None,
            Instant::now(),
            ATTEMPT_COOLDOWN,
            WarmupMode::Force,
        );
        assert_eq!(d, WarmupDecision::Fire);
    }

    #[test]
    fn decide_skips_when_window_active() {
        let settings = WarmupSettings {
            enabled: true,
            work_start: "09:00".into(),
            work_end: "18:00".into(),
            ..Default::default()
        };
        let now = Local::now()
            .date_naive()
            .and_time(t(10, 0))
            .and_local_timezone(Local)
            .single()
            .unwrap();
        // Derived from `now`, not from the real clock. Taking it from
        // `Utc::now()` left the reset unrelated to the 10:00 this test
        // fabricates, so the assertion only held while the wall clock happened
        // to fall in a window of a few hours — the test passed all morning and
        // failed at night.
        let reset = (now + chrono::Duration::hours(3)).to_utc().to_rfc3339();
        let d = decide(
            &settings,
            now,
            0,
            1,
            Some(&reset),
            None,
            Instant::now(),
            ATTEMPT_COOLDOWN,
            WarmupMode::Scheduled,
        );
        assert_eq!(d, WarmupDecision::Skip(SkipReason::WindowActive));
    }

    #[test]
    fn decide_respects_cooldown() {
        let settings = WarmupSettings {
            enabled: true,
            work_start: "09:00".into(),
            work_end: "18:00".into(),
            ..Default::default()
        };
        let now = Local::now()
            .date_naive()
            .and_time(t(10, 0))
            .and_local_timezone(Local)
            .single()
            .unwrap();
        let t0 = Instant::now();
        let d = decide(
            &settings,
            now,
            0,
            1,
            None,
            Some(t0),
            t0 + Duration::from_secs(60),
            ATTEMPT_COOLDOWN,
            WarmupMode::Scheduled,
        );
        assert_eq!(d, WarmupDecision::Skip(SkipReason::Cooldown));
    }

    #[test]
    fn bad_schedule_rejected() {
        let settings = WarmupSettings {
            enabled: true,
            work_start: "18:00".into(),
            work_end: "09:00".into(),
            ..Default::default()
        };
        let now = Local::now();
        let d = decide(
            &settings,
            now,
            0,
            1,
            None,
            None,
            Instant::now(),
            ATTEMPT_COOLDOWN,
            WarmupMode::Scheduled,
        );
        assert_eq!(d, WarmupDecision::Skip(SkipReason::BadSchedule));
    }

    #[test]
    fn next_planned_before_anchor_is_today() {
        let settings = WarmupSettings {
            enabled: true,
            work_start: "09:00".into(),
            work_end: "18:00".into(),
            ..Default::default()
        };
        let now = Local::now()
            .date_naive()
            .and_time(t(5, 0))
            .and_local_timezone(Local)
            .single()
            .unwrap();
        let next = next_planned_fire(&settings, now, 0, 1, None).unwrap();
        assert_eq!(next.time(), t(6, 0));
        assert_eq!(next.date_naive(), now.date_naive());
    }

    #[test]
    fn next_planned_after_work_is_tomorrow() {
        let settings = WarmupSettings {
            enabled: true,
            work_start: "09:00".into(),
            work_end: "18:00".into(),
            ..Default::default()
        };
        let now = Local::now()
            .date_naive()
            .and_time(t(20, 0))
            .and_local_timezone(Local)
            .single()
            .unwrap();
        let next = next_planned_fire(&settings, now, 0, 1, None).unwrap();
        assert_eq!(next.time(), t(6, 0));
        assert_eq!(next.date_naive(), now.date_naive() + ChronoDuration::days(1));
    }
}
