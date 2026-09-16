//! The status line Claude Code prints at the bottom of a terminal.
//!
//! Three presets, no widget editor: `lean` / `standard` / `full` (see
//! `docs/statusline.md`). This module holds everything that decides what the
//! line says — the renderer is a pure function, so the GUI preview and the
//! `cs-statusline` binary produce the same text from the same inputs.
//!
//! Three sources feed it, in falling order of authority:
//!
//! 1. **Claude Code's own stdin JSON** ([`Live`]). It already carries
//!    `rate_limits` for the account the terminal is logged into, fresher than
//!    anything we could poll, so the current account's percentages are not ours
//!    to fetch.
//! 2. **`<backup_root>/statusline.json`** ([`State`]), written by the engine on
//!    every usage poll. It answers what stdin cannot: slot numbers, aliases,
//!    where an automatic switch would land, and — for Claude Code versions or
//!    accounts that send no `rate_limits` — a fallback percentage.
//! 3. **The terminal's own config home**, which says *which account this
//!    terminal is*. Session terminals run under `CLAUDE_CONFIG_DIR`, so each one
//!    answers for itself rather than for the default login.
//!
//! The nerve of the module is [`render`]: it never fails, never blocks, and
//! drops any segment it cannot fill rather than printing a placeholder. A status
//! line that spills an error into someone's terminal on every repaint is worse
//! than no status line.

use std::path::{Path, PathBuf};

use chrono::{DateTime, Duration, Local, Utc};
use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::errors::{Error, Result};
use crate::fsutil::atomic_write;
use crate::usage::UsageStatus;

pub const STATUSLINE_SCHEMA_VERSION: u32 = 1;

/// Published state file, under `backup_root` beside `autoswitch_state.json`.
pub const STATE_FILENAME: &str = "statusline.json";

pub mod install;

/// Past this age the published state is quoted, not trusted: the app that
/// writes it is closed, so its percentages describe some earlier hour.
const STALE_AFTER_MINUTES: i64 = 10;

/// Cells in the `standard` / `full` usage bar, and what one cell is worth.
/// The two are tied (`BAR_CELL_PCT == 100 / BAR_CELLS`); the bar tests pin it.
const BAR_CELLS: usize = 10;
const BAR_CELL_PCT: f64 = 10.0;

const SEP: &str = " · ";

// --- presets ----------------------------------------------------------------

/// How much the line says. Wire values follow the kebab alphabet (design §8.0).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Preset {
    /// Account, 5h, model. ASCII only, for narrow terminals.
    Lean,
    /// Adds the 5h bar, the 7d window, context and the directory.
    #[default]
    Standard,
    /// A second line: reset times, the spare account, auto-switch state, cost.
    Full,
}

impl Preset {
    /// Parse a wire value; unknown text is not a preset.
    #[must_use]
    pub fn parse(s: &str) -> Option<Self> {
        match s.trim() {
            "lean" => Some(Self::Lean),
            "standard" => Some(Self::Standard),
            "full" => Some(Self::Full),
            _ => None,
        }
    }

    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Lean => "lean",
            Self::Standard => "standard",
            Self::Full => "full",
        }
    }

    /// Whether this preset shows the bar, the 7d window and the directory.
    const fn is_wide(self) -> bool {
        matches!(self, Self::Standard | Self::Full)
    }
}

// --- published state --------------------------------------------------------

/// One usage window as published (RFC3339 reset, like [`crate::usage`]).
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WindowBrief {
    pub pct: f64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub resets_at: Option<String>,
}

/// One slot, as much of it as a status line can use.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountBrief {
    pub slot: u32,
    /// Join key against the terminal's `.claude.json` identity.
    pub email: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub alias: Option<String>,
    /// Why a window is missing, when one is (see [`UsageStatus`]).
    #[serde(default)]
    pub state: UsageStatus,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub five_hour: Option<WindowBrief>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub seven_day: Option<WindowBrief>,
}

impl AccountBrief {
    /// Highest known window — the figure that binds this account.
    #[must_use]
    pub fn binding_pct(&self) -> Option<f64> {
        match (&self.five_hour, &self.seven_day) {
            (Some(a), Some(b)) => Some(a.pct.max(b.pct)),
            (Some(w), None) | (None, Some(w)) => Some(w.pct),
            (None, None) => None,
        }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutoSwitchBrief {
    pub enabled: bool,
    pub threshold: f64,
}

/// `<backup_root>/statusline.json`: what the engine knows and a status-line
/// process cannot work out for itself.
///
/// Read-only for every consumer, and it holds no token — the same sensitivity
/// as `sequence.json` next to it.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct State {
    #[serde(default)]
    pub schema_version: u32,
    /// RFC3339 UTC; how [`State::is_stale`] knows the writer went away.
    pub updated_at: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub active_slot: Option<u32>,
    /// Where an automatic switch would land once the active account tops out.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub spare_slot: Option<u32>,
    #[serde(default)]
    pub auto_switch: AutoSwitchBrief,
    /// Mirrors the app's "hide emails" preference (see [`crate::settings::UiSettings`]).
    #[serde(default)]
    pub hide_email: bool,
    #[serde(default)]
    pub accounts: Vec<AccountBrief>,
}

impl Default for State {
    fn default() -> Self {
        Self {
            schema_version: STATUSLINE_SCHEMA_VERSION,
            updated_at: Utc::now().to_rfc3339(),
            active_slot: None,
            spare_slot: None,
            auto_switch: AutoSwitchBrief::default(),
            hide_email: false,
            accounts: Vec::new(),
        }
    }
}

impl State {
    /// Read the file, or [`None`] for every reason it might not be readable.
    ///
    /// Deliberately not a `Result`: the only caller renders a terminal line, and
    /// a missing or half-written state file means "show less", never "fail".
    #[must_use]
    pub fn load(path: &Path) -> Option<Self> {
        let text = std::fs::read_to_string(path).ok()?;
        serde_json::from_str(&text).ok()
    }

    /// Publish atomically (the reader may be running right now).
    ///
    /// # Errors
    ///
    /// [`Error::Io`] when the file cannot be written.
    pub fn save(&self, path: &Path) -> Result<()> {
        let text = serde_json::to_string_pretty(self)
            .map_err(|e| Error::Internal(format!("statusline state serialize: {e}")))?;
        atomic_write(path, text.as_bytes()).map_err(Error::Io)
    }

    /// Whether the writer has been gone long enough that the numbers are quoted
    /// rather than shown as current.
    #[must_use]
    pub fn is_stale(&self, now: DateTime<Local>) -> bool {
        let Some(at) = parse_rfc3339(&self.updated_at) else {
            return true;
        };
        now.with_timezone(&Utc) - at > Duration::minutes(STALE_AFTER_MINUTES)
    }

    #[must_use]
    pub fn account(&self, slot: u32) -> Option<&AccountBrief> {
        self.accounts.iter().find(|a| a.slot == slot)
    }

    #[must_use]
    pub fn account_for_email(&self, email: &str) -> Option<&AccountBrief> {
        self.accounts
            .iter()
            .find(|a| a.email.eq_ignore_ascii_case(email))
    }
}

// --- what Claude Code hands us ---------------------------------------------

/// A usage window ready to render, wherever it came from.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Window {
    pub pct: f64,
    pub resets_at: Option<DateTime<Utc>>,
    /// The figure came from a published state file old enough to doubt.
    pub stale: bool,
}

/// The fields we take from Claude Code's status-line stdin JSON.
///
/// Every one is optional and every number tolerates arriving as a string:
/// this is someone else's payload, and a schema change upstream must cost us a
/// segment, not the line.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Live {
    pub model: Option<String>,
    /// Thinking effort for this session (`low` … `max`), when Claude Code says.
    pub effort: Option<String>,
    pub current_dir: Option<PathBuf>,
    pub context_pct: Option<f64>,
    pub cost_usd: Option<f64>,
    pub duration_ms: Option<i64>,
    pub five_hour: Option<Window>,
    pub seven_day: Option<Window>,
}

impl Live {
    /// Parse the payload; anything unreadable yields an empty [`Live`].
    #[must_use]
    pub fn parse(stdin: &str) -> Self {
        let v: Value = serde_json::from_str(stdin).unwrap_or(Value::Null);
        Self {
            model: model_name(&v),
            effort: effort_level(&v),
            current_dir: v
                .pointer("/workspace/current_dir")
                .and_then(Value::as_str)
                .or_else(|| v.get("cwd").and_then(Value::as_str))
                .map(PathBuf::from),
            context_pct: num(v.pointer("/context_window/used_percentage")).or_else(|| {
                num(v.pointer("/context_window/remaining_percentage")).map(|r| 100.0 - r)
            }),
            cost_usd: num(v.pointer("/cost/total_cost_usd")),
            #[allow(clippy::cast_possible_truncation)]
            duration_ms: num(v.pointer("/cost/total_duration_ms")).map(|ms| ms as i64),
            five_hour: live_window(v.pointer("/rate_limits/five_hour")),
            seven_day: live_window(v.pointer("/rate_limits/seven_day")),
        }
    }
}

/// `effort.level` — `low` / `medium` / `high` / `xhigh` / `max` today.
///
/// Taken from the payload only. ccstatusline falls back to scanning the
/// transcript and then to `settings.json`, but the first is the per-repaint cost
/// this renderer exists to avoid, and the second is the configured default
/// rather than what this session is actually running — a plausible wrong answer
/// is worse than no segment.
///
/// Whatever string arrives is shown, so a level added upstream appears without a
/// release here; only the two that mean "nothing to say" are dropped.
fn effort_level(v: &Value) -> Option<String> {
    let level = v
        .pointer("/effort/level")
        .and_then(Value::as_str)?
        .trim();
    if level.is_empty() || level.eq_ignore_ascii_case("none") || level.eq_ignore_ascii_case("default")
    {
        return None;
    }
    // A payload is someone else's; cap what it can do to the line's width.
    Some(level.chars().take(12).collect())
}

fn model_name(v: &Value) -> Option<String> {
    match v.get("model")? {
        Value::String(s) => Some(s.clone()),
        m => m
            .get("display_name")
            .or_else(|| m.get("id"))
            .and_then(Value::as_str)
            .map(ToOwned::to_owned),
    }
    .filter(|s| !s.trim().is_empty())
}

/// `rate_limits.*` — percentage plus a Unix-epoch reset.
fn live_window(v: Option<&Value>) -> Option<Window> {
    let v = v?;
    let pct = num(v.get("used_percentage"))?;
    Some(Window {
        pct,
        resets_at: num(v.get("resets_at")).and_then(|s| {
            #[allow(clippy::cast_possible_truncation)]
            DateTime::from_timestamp(s as i64, 0)
        }),
        stale: false,
    })
}

/// A number, or a number that arrived as a string (Claude Code has sent both).
fn num(v: Option<&Value>) -> Option<f64> {
    match v? {
        Value::Number(n) => n.as_f64(),
        Value::String(s) => s.trim().parse().ok(),
        _ => None,
    }
}

fn parse_rfc3339(s: &str) -> Option<DateTime<Utc>> {
    DateTime::parse_from_rfc3339(s.trim())
        .ok()
        .map(|d| d.with_timezone(&Utc))
}

// --- which account is this terminal ----------------------------------------

/// The account behind the terminal being rendered.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Identity {
    /// Slot number, when the login is one this tool manages.
    pub slot: Option<u32>,
    /// What the line shows: the alias if there is one, else the email's local
    /// part, masked when the app is hiding emails.
    pub label: String,
    /// Why this account has no numbers, when that is known.
    pub state: UsageStatus,
}

/// Resolve the account from the config home Claude Code is actually using.
///
/// `global_config` is `PathEnv::global_config_path()`: `$CLAUDE_CONFIG_DIR/.claude.json`
/// for a session terminal, `~/.claude.json` for the default login. That is the
/// same file [`crate::session::bootstrap`] seeds, so a terminal opened for one
/// account answers for that account and not for whoever is logged in by default.
#[must_use]
pub fn identity(global_config: &Path, state: Option<&State>) -> Option<Identity> {
    let text = std::fs::read_to_string(global_config).ok()?;
    let v: Value = serde_json::from_str(&text).ok()?;
    let email = v
        .pointer("/oauthAccount/emailAddress")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|s| !s.is_empty())?;

    let hide = state.is_some_and(|s| s.hide_email);
    let found = state.and_then(|s| s.account_for_email(email));
    Some(Identity {
        slot: found.map(|a| a.slot),
        label: label_for(found.and_then(|a| a.alias.as_deref()), email, hide),
        // A login no slot holds is not broken, we simply have nothing to say
        // about its quota — that is `Unknown`, not a fault.
        state: found.map_or(UsageStatus::Unknown, |a| a.state),
    })
}

/// Alias if the user gave one, else the email's local part; masked on request.
///
/// Aliases are never masked: the user typed that name themselves, and hiding it
/// would leave the line with nothing to identify the account by.
fn label_for(alias: Option<&str>, email: &str, hide: bool) -> String {
    if let Some(a) = alias.map(str::trim).filter(|a| !a.is_empty()) {
        return a.to_string();
    }
    let local = email.split('@').next().unwrap_or(email);
    if hide {
        let first: String = local.chars().take(1).collect();
        format!("{first}***")
    } else {
        local.to_string()
    }
}

// --- git --------------------------------------------------------------------

/// Current branch, read straight out of `.git/HEAD`.
///
/// No `git` subprocess: this runs on every repaint, and the things a subprocess
/// would buy (dirty count, ahead/behind) cost tens to hundreds of milliseconds
/// each time. A detached HEAD has no branch name, so it has no segment.
#[must_use]
pub fn git_branch(dir: &Path) -> Option<String> {
    let mut cur = Some(dir);
    while let Some(d) = cur {
        let dot = d.join(".git");
        if let Some(head) = read_head(&dot) {
            return Some(head);
        }
        cur = d.parent();
    }
    None
}

fn read_head(dot_git: &Path) -> Option<String> {
    // A worktree's `.git` is a file pointing at the real directory.
    let git_dir = if dot_git.is_file() {
        let text = std::fs::read_to_string(dot_git).ok()?;
        let target = text.trim().strip_prefix("gitdir:")?.trim();
        PathBuf::from(target)
    } else if dot_git.is_dir() {
        dot_git.to_path_buf()
    } else {
        return None;
    };
    let head = std::fs::read_to_string(git_dir.join("HEAD")).ok()?;
    let name = head.trim().strip_prefix("ref: refs/heads/")?.trim();
    (!name.is_empty()).then(|| name.to_string())
}

// --- rendering --------------------------------------------------------------

/// Everything the renderer is allowed to look at.
///
/// Assembled by the caller (which does the file reads) so [`render`] stays pure
/// and its tests can fix both ends, including the clock.
#[derive(Clone, Debug)]
pub struct Frame {
    pub preset: Preset,
    pub live: Live,
    pub state: Option<State>,
    pub identity: Option<Identity>,
    pub branch: Option<String>,
    pub now: DateTime<Local>,
    /// ANSI colour. Off for the GUI preview and when `NO_COLOR` is set.
    pub color: bool,
}

impl Frame {
    /// A frame with nothing in it but the preset and the clock.
    #[must_use]
    pub fn new(preset: Preset, now: DateTime<Local>) -> Self {
        Self {
            preset,
            live: Live::default(),
            state: None,
            identity: None,
            branch: None,
            now,
            color: false,
        }
    }
}

/// The line (or, for [`Preset::Full`], the two lines) to print.
///
/// Segments whose data is missing are dropped, not filled with `?`: a status
/// line is read at a glance, and a row of placeholders costs the reader more
/// than the absent fact was worth.
#[must_use]
pub fn render(f: &Frame) -> String {
    let mut out = join(&line_one(f), f.color);
    if f.preset == Preset::Full {
        let second = join(&line_two(f), f.color);
        if !second.is_empty() {
            out.push('\n');
            out.push_str(&second);
        }
    }
    out
}

fn join(segments: &[String], color: bool) -> String {
    segments.join(&dim(SEP, color))
}

fn line_one(f: &Frame) -> Vec<String> {
    let mut seg = Vec::new();
    let wide = f.preset.is_wide();

    if let Some(id) = &f.identity {
        let text = match id.slot {
            Some(n) => format!("#{n} {}", id.label),
            None => id.label.clone(),
        };
        seg.push(bold(&text, f.color));
    }

    let (five, seven) = windows(f);
    match (five, seven) {
        (None, None) => {
            // No numbers anywhere. Say why rather than leaving the reader to
            // wonder whether the account is fine and the tool is broken.
            if let Some(reason) = f.identity.as_ref().and_then(|id| missing_reason(id.state)) {
                seg.push(dim(reason, f.color));
            }
        }
        (five, seven) => {
            if let Some(w) = five {
                seg.push(window_segment("5h", &w, wide, f.color));
            }
            if let (true, Some(w)) = (wide, seven) {
                seg.push(window_segment("7d", &w, false, f.color));
            }
        }
    }

    if wide {
        if let Some(pct) = f.live.context_pct {
            seg.push(format!(
                "{} {}",
                dim("ctx", f.color),
                paint(&format!("{}%", pct_int(pct)), hue(pct), f.color)
            ));
        }
    }

    // Effort rides the model rather than taking a separator of its own: it
    // qualifies the model the way a branch qualifies a directory, and on the
    // narrow preset a whole extra segment would not be worth its width.
    if let Some(model) = &f.live.model {
        seg.push(match &f.live.effort {
            Some(effort) => format!("{model} {}", dim(effort, f.color)),
            None => model.clone(),
        });
    }

    if wide {
        if let Some(place) = place(f) {
            seg.push(dim(&place, f.color));
        }
    }

    seg
}

fn line_two(f: &Frame) -> Vec<String> {
    let mut seg = Vec::new();
    let (five, seven) = windows(f);

    for (label, w) in [("5h", five), ("7d", seven)] {
        if let Some(at) = w.and_then(|w| w.resets_at) {
            seg.push(dim(
                &format!("{label} resets {}", resets_text(at, f.now)),
                f.color,
            ));
        }
    }

    // The spare is the one fact here that only this tool holds: where the next
    // switch lands. A stale state file cannot answer it — an account that had
    // room an hour ago may be the one that ran out.
    if let Some(state) = f.state.as_ref().filter(|s| !s.is_stale(f.now)) {
        if let Some(spare) = state.spare_slot.and_then(|n| state.account(n)) {
            let label = label_for(spare.alias.as_deref(), &spare.email, state.hide_email);
            let text = match spare.binding_pct() {
                Some(pct) => format!("spare #{} {label} {}%", spare.slot, pct_int(pct)),
                None => format!("spare #{} {label}", spare.slot),
            };
            seg.push(dim(&text, f.color));
        }
        seg.push(dim(
            &if state.auto_switch.enabled {
                format!("auto {}%", pct_int(state.auto_switch.threshold))
            } else {
                "auto off".to_string()
            },
            f.color,
        ));
    }

    // What this session has cost so far, in money and in wall clock. Either
    // half may be missing; both missing means no segment at all.
    let session: Vec<String> = [
        f.live
            .cost_usd
            .filter(|c| *c > 0.0)
            .map(|usd| format!("${usd:.2}")),
        f.live.duration_ms.filter(|ms| *ms > 0).map(duration_text),
    ]
    .into_iter()
    .flatten()
    .collect();
    if !session.is_empty() {
        seg.push(dim(&session.join(" "), f.color));
    }

    seg
}

/// Live windows, falling back to the published state for this account.
///
/// Claude Code's own `rate_limits` win whenever they are there: they describe
/// the very session being rendered and are newer than any poll of ours.
fn windows(f: &Frame) -> (Option<Window>, Option<Window>) {
    let published = |pick: fn(&AccountBrief) -> Option<&WindowBrief>| -> Option<Window> {
        let state = f.state.as_ref()?;
        let slot = f.identity.as_ref()?.slot?;
        let w = pick(state.account(slot)?)?;
        Some(Window {
            pct: w.pct,
            resets_at: w.resets_at.as_deref().and_then(parse_rfc3339),
            stale: state.is_stale(f.now),
        })
    };
    (
        f.live
            .five_hour
            .or_else(|| published(|a| a.five_hour.as_ref())),
        f.live
            .seven_day
            .or_else(|| published(|a| a.seven_day.as_ref())),
    )
}

/// `5h ████░░░░░░ 38%`, or `5h 38%` when the preset has no room for a bar.
fn window_segment(label: &str, w: &Window, with_bar: bool, color: bool) -> String {
    let tone = hue(w.pct);
    let mut out = dim(label, color);
    out.push(' ');
    if with_bar {
        out.push_str(&paint(&bar(w.pct), tone, color));
        out.push(' ');
    }
    let tilde = if w.stale { "~" } else { "" };
    out.push_str(&paint(&format!("{tilde}{}%", pct_int(w.pct)), tone, color));
    out
}

/// Directory leaf, with the branch when there is one: `claude_switch (master)`.
fn place(f: &Frame) -> Option<String> {
    let dir = f.live.current_dir.as_ref()?;
    let leaf = dir.file_name().map_or_else(
        || dir.to_string_lossy().to_string(),
        |n| n.to_string_lossy().to_string(),
    );
    if leaf.is_empty() {
        return None;
    }
    Some(match &f.branch {
        Some(b) => format!("{leaf} ({b})"),
        None => leaf,
    })
}

/// Short reason an account has no percentages, for the states where the user
/// has to do something about it.
const fn missing_reason(state: UsageStatus) -> Option<&'static str> {
    match state {
        UsageStatus::NeedsLogin => Some("no login"),
        UsageStatus::NoCredential => Some("no creds"),
        UsageStatus::NoSubscription => Some("no sub"),
        UsageStatus::ApiKey => Some("api key"),
        UsageStatus::Ok | UsageStatus::Unknown | UsageStatus::Unavailable => None,
    }
}

/// Absolute, never a countdown: a repaint may be seconds or minutes away, and a
/// countdown that only moves when the terminal happens to redraw reads as wrong.
/// Wording tightens with distance, matching the account card.
fn resets_text(at: DateTime<Utc>, now: DateTime<Local>) -> String {
    let local = at.with_timezone(&Local);
    if local <= now {
        return "now".to_string();
    }
    if local.date_naive() == now.date_naive() {
        return local.format("%H:%M").to_string();
    }
    if local - now < Duration::days(7) {
        return local.format("%a %H:%M").to_string();
    }
    local.format("%m/%d %H:%M").to_string()
}

fn duration_text(ms: i64) -> String {
    let secs = ms / 1000;
    if secs < 60 {
        return format!("{secs}s");
    }
    let mins = secs / 60;
    if mins < 60 {
        return format!("{mins}m");
    }
    format!("{}h{:02}m", mins / 60, mins % 60)
}

fn bar(pct: f64) -> String {
    #[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)]
    let filled = ((pct / BAR_CELL_PCT).round().max(0.0) as usize).min(BAR_CELLS);
    let mut s = String::with_capacity(BAR_CELLS * 3);
    for i in 0..BAR_CELLS {
        s.push(if i < filled { '█' } else { '░' });
    }
    s
}

/// Percentages are read, not computed with: round to whole points and never
/// print more than full, so a 100.4% reading does not become `100.4%`.
#[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)]
fn pct_int(pct: f64) -> u32 {
    pct.clamp(0.0, 100.0).round() as u32
}

// --- colour -----------------------------------------------------------------

/// Green / amber / red by how much of the window is gone.
const fn hue(pct: f64) -> &'static str {
    if pct < 50.0 {
        "32"
    } else if pct < 80.0 {
        "33"
    } else {
        "31"
    }
}

fn paint(text: &str, code: &str, on: bool) -> String {
    if on {
        format!("\x1b[{code}m{text}\x1b[0m")
    } else {
        text.to_string()
    }
}

fn dim(text: &str, on: bool) -> String {
    paint(text, "2", on)
}

fn bold(text: &str, on: bool) -> String {
    paint(text, "1", on)
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;

    /// Local noon on a fixed date: a wall clock, so the expectations below do
    /// not depend on the machine's time zone.
    fn noon() -> DateTime<Local> {
        Local
            .with_ymd_and_hms(2026, 9, 16, 12, 0, 0)
            .single()
            .expect("2026-09-16 12:00 exists in every zone")
    }

    fn at(now: DateTime<Local>, hours: i64) -> DateTime<Utc> {
        (now + Duration::hours(hours)).with_timezone(&Utc)
    }

    fn state_fixture(now: DateTime<Local>) -> State {
        State {
            schema_version: STATUSLINE_SCHEMA_VERSION,
            updated_at: now.with_timezone(&Utc).to_rfc3339(),
            active_slot: Some(2),
            spare_slot: Some(3),
            auto_switch: AutoSwitchBrief {
                enabled: true,
                threshold: 90.0,
            },
            hide_email: false,
            accounts: vec![
                AccountBrief {
                    slot: 2,
                    email: "alice@example.com".into(),
                    alias: Some("work".into()),
                    state: UsageStatus::Ok,
                    five_hour: Some(WindowBrief {
                        pct: 38.2,
                        resets_at: Some(at(now, 2).to_rfc3339()),
                    }),
                    seven_day: Some(WindowBrief {
                        pct: 12.0,
                        resets_at: Some(at(now, 24 * 3).to_rfc3339()),
                    }),
                },
                AccountBrief {
                    slot: 3,
                    email: "ops@example.com".into(),
                    alias: Some("ops".into()),
                    state: UsageStatus::Ok,
                    five_hour: Some(WindowBrief {
                        pct: 8.0,
                        resets_at: None,
                    }),
                    seven_day: None,
                },
            ],
        }
    }

    fn frame(preset: Preset) -> Frame {
        let now = noon();
        let state = state_fixture(now);
        Frame {
            preset,
            live: Live {
                model: Some("Sonnet 5".into()),
                effort: Some("high".into()),
                current_dir: Some(PathBuf::from("/home/me/dev/claude_switch")),
                context_pct: Some(24.4),
                cost_usd: Some(1.237),
                duration_ms: Some(42 * 60 * 1000),
                five_hour: Some(Window {
                    pct: 38.2,
                    resets_at: Some(at(now, 2)),
                    stale: false,
                }),
                seven_day: Some(Window {
                    pct: 12.0,
                    resets_at: Some(at(now, 24 * 3)),
                    stale: false,
                }),
            },
            identity: Some(Identity {
                slot: Some(2),
                label: "work".into(),
                state: UsageStatus::Ok,
            }),
            branch: Some("master".into()),
            state: Some(state),
            now,
            color: false,
        }
    }

    #[test]
    fn lean_says_who_how_much_and_which_model() {
        assert_eq!(render(&frame(Preset::Lean)), "#2 work · 5h 38% · Sonnet 5 high");
    }

    #[test]
    fn standard_adds_bar_seven_day_context_and_place() {
        assert_eq!(
            render(&frame(Preset::Standard)),
            "#2 work · 5h ████░░░░░░ 38% · 7d 12% · ctx 24% · Sonnet 5 high · claude_switch (master)"
        );
    }

    #[test]
    fn full_adds_resets_spare_auto_and_session_cost() {
        let f = frame(Preset::Full);
        let rendered = render(&f);
        let lines: Vec<&str> = rendered.lines().collect();
        assert_eq!(lines.len(), 2, "full is two lines");
        assert_eq!(lines[0], render(&frame(Preset::Standard)));

        let reset5 = (f.now + Duration::hours(2)).format("%H:%M").to_string();
        let reset7 = (f.now + Duration::days(3)).format("%a %H:%M").to_string();
        assert_eq!(
            lines[1],
            format!(
                "5h resets {reset5} · 7d resets {reset7} · spare #3 ops 8% · auto 90% · $1.24 42m"
            )
        );
    }

    #[test]
    fn live_rate_limits_beat_the_published_state() {
        let mut f = frame(Preset::Lean);
        f.live.five_hour = Some(Window {
            pct: 91.0,
            resets_at: None,
            stale: false,
        });
        // The state file still says 38.2 for this slot; stdin describes the
        // session being rendered, so it wins.
        assert!(render(&f).contains("5h 91%"));
    }

    #[test]
    fn a_silent_claude_code_falls_back_to_the_published_state() {
        let mut f = frame(Preset::Standard);
        f.live.five_hour = None;
        f.live.seven_day = None;
        assert!(render(&f).contains("5h ████░░░░░░ 38%"));
        assert!(render(&f).contains("7d 12%"));
    }

    #[test]
    fn a_stale_state_is_quoted_and_hides_the_spare() {
        let mut f = frame(Preset::Full);
        f.live.five_hour = None;
        f.live.seven_day = None;
        if let Some(s) = f.state.as_mut() {
            s.updated_at = (f.now.with_timezone(&Utc) - Duration::hours(3)).to_rfc3339();
        }
        let out = render(&f);
        assert!(out.contains("5h ████░░░░░░ ~38%"), "{out}");
        assert!(!out.contains("spare"), "a stale spare is a guess: {out}");
        assert!(!out.contains("auto 90%"), "{out}");
    }

    #[test]
    fn missing_segments_disappear_rather_than_showing_placeholders() {
        let mut f = Frame::new(Preset::Standard, noon());
        f.identity = Some(Identity {
            slot: Some(1),
            label: "solo".into(),
            state: UsageStatus::Ok,
        });
        let out = render(&f);
        assert_eq!(out, "#1 solo");
        assert!(!out.contains('?'));
    }

    #[test]
    fn an_account_without_numbers_says_why() {
        let mut f = Frame::new(Preset::Lean, noon());
        f.identity = Some(Identity {
            slot: Some(4),
            label: "old".into(),
            state: UsageStatus::NeedsLogin,
        });
        f.live.model = Some("Opus 5".into());
        assert_eq!(render(&f), "#4 old · no login · Opus 5");
        // Effort qualifies the model wherever the model is shown.
        f.live.effort = Some("max".into());
        assert_eq!(render(&f), "#4 old · no login · Opus 5 max");
    }

    #[test]
    fn an_unreadable_payload_still_renders() {
        let live = Live::parse("not json at all");
        assert_eq!(live, Live::default());
        let mut f = Frame::new(Preset::Standard, noon());
        f.identity = Some(Identity {
            slot: Some(2),
            label: "work".into(),
            state: UsageStatus::Unknown,
        });
        f.live = live;
        assert_eq!(render(&f), "#2 work");
    }

    #[test]
    fn numbers_are_taken_whether_they_arrive_as_numbers_or_strings() {
        let live = Live::parse(
            r#"{
              "model": {"display_name": "Sonnet 5"},
              "workspace": {"current_dir": "/tmp/proj"},
              "context_window": {"used_percentage": "33.4"},
              "cost": {"total_cost_usd": 0.5, "total_duration_ms": "90000"},
              "rate_limits": {
                "five_hour": {"used_percentage": "44", "resets_at": 1800000000},
                "seven_day": {"used_percentage": 9}
              }
            }"#,
        );
        assert_eq!(live.model.as_deref(), Some("Sonnet 5"));
        assert_eq!(live.current_dir, Some(PathBuf::from("/tmp/proj")));
        assert_eq!(live.context_pct, Some(33.4));
        assert_eq!(live.duration_ms, Some(90_000));
        assert_eq!(live.five_hour.map(|w| w.pct), Some(44.0));
        assert_eq!(live.seven_day.map(|w| w.pct), Some(9.0));
        assert_eq!(
            live.five_hour.and_then(|w| w.resets_at),
            DateTime::from_timestamp(1_800_000_000, 0)
        );
    }

    #[test]
    fn thinking_effort_is_taken_from_the_payload_and_nowhere_else() {
        let live = |json: &str| Live::parse(json).effort;
        assert_eq!(live(r#"{"effort":{"level":"xhigh"}}"#).as_deref(), Some("xhigh"));
        assert_eq!(live(r#"{"effort":{"level":" high "}}"#).as_deref(), Some("high"));
        // A level we have never heard of still reaches the line: Claude Code is
        // free to add one without a release here.
        assert_eq!(live(r#"{"effort":{"level":"ludicrous"}}"#).as_deref(), Some("ludicrous"));
        // The ways it can say "nothing to report" cost a segment, not a word.
        assert_eq!(live(r#"{"effort":{"level":"none"}}"#), None);
        assert_eq!(live(r#"{"effort":{"level":"default"}}"#), None);
        assert_eq!(live(r#"{"effort":{"level":""}}"#), None);
        assert_eq!(live(r#"{"effort":{"level":null}}"#), None);
        assert_eq!(live(r#"{"effort":null}"#), None);
        assert_eq!(live("{}"), None);
        // And a hostile payload cannot widen the line without limit.
        let long = format!(r#"{{"effort":{{"level":"{}"}}}}"#, "x".repeat(200));
        assert_eq!(live(&long).map(|e| e.chars().count()), Some(12));
    }

    #[test]
    fn a_plain_string_model_is_accepted_too() {
        let live = Live::parse(r#"{"model": "Haiku 4.5"}"#);
        assert_eq!(live.model.as_deref(), Some("Haiku 4.5"));
    }

    #[test]
    fn colour_wraps_the_same_text_it_would_print_plain() {
        let mut f = frame(Preset::Standard);
        f.color = true;
        let painted = render(&f);
        assert!(painted.contains("\x1b["));
        f.color = false;
        assert_eq!(strip_ansi(&painted), render(&f));
    }

    fn strip_ansi(s: &str) -> String {
        let mut out = String::new();
        let mut chars = s.chars();
        while let Some(c) = chars.next() {
            if c == '\x1b' {
                for c in chars.by_ref() {
                    if c == 'm' {
                        break;
                    }
                }
            } else {
                out.push(c);
            }
        }
        out
    }

    #[test]
    fn the_bar_fills_by_tenths() {
        assert_eq!(bar(0.0), "░░░░░░░░░░");
        assert_eq!(bar(38.2), "████░░░░░░");
        assert_eq!(bar(100.0), "██████████");
        assert_eq!(bar(140.0), "██████████", "over-full is still full");
    }

    #[test]
    fn reset_wording_tightens_with_distance() {
        let now = noon();
        assert_eq!(resets_text(at(now, -1), now), "now");
        assert_eq!(
            resets_text(at(now, 2), now),
            (now + Duration::hours(2)).format("%H:%M").to_string()
        );
        assert_eq!(
            resets_text(at(now, 24 * 3), now),
            (now + Duration::days(3)).format("%a %H:%M").to_string()
        );
        assert_eq!(
            resets_text(at(now, 24 * 20), now),
            (now + Duration::days(20)).format("%m/%d %H:%M").to_string()
        );
    }

    #[test]
    fn durations_read_as_a_person_would_say_them() {
        assert_eq!(duration_text(45_000), "45s");
        assert_eq!(duration_text(42 * 60_000), "42m");
        assert_eq!(duration_text(72 * 60_000), "1h12m");
    }

    #[test]
    fn a_label_prefers_the_alias_and_masks_only_the_email() {
        assert_eq!(label_for(Some("work"), "alice@x.com", true), "work");
        assert_eq!(label_for(None, "alice@x.com", false), "alice");
        assert_eq!(label_for(None, "alice@x.com", true), "a***");
        assert_eq!(label_for(Some("  "), "bob@x.com", false), "bob");
    }

    #[test]
    fn identity_comes_from_the_config_home_the_terminal_uses() {
        let dir = tempfile::tempdir().unwrap();
        let cfg = dir.path().join(".claude.json");
        std::fs::write(
            &cfg,
            br#"{"oauthAccount":{"emailAddress":"ALICE@example.com"}}"#,
        )
        .unwrap();
        let state = state_fixture(noon());

        let id = identity(&cfg, Some(&state)).expect("identity");
        assert_eq!(id.slot, Some(2), "email matches slot 2 case-insensitively");
        assert_eq!(id.label, "work");

        // A login no slot holds still names itself; it just has no slot.
        std::fs::write(
            &cfg,
            br#"{"oauthAccount":{"emailAddress":"zoe@example.com"}}"#,
        )
        .unwrap();
        let id = identity(&cfg, Some(&state)).expect("identity");
        assert_eq!(id.slot, None);
        assert_eq!(id.label, "zoe");
        assert_eq!(id.state, UsageStatus::Unknown);

        // Logged out: nothing to say.
        std::fs::write(&cfg, b"{}").unwrap();
        assert!(identity(&cfg, Some(&state)).is_none());
        assert!(identity(&dir.path().join("nope.json"), Some(&state)).is_none());
    }

    #[test]
    fn state_roundtrips_and_a_broken_file_reads_as_absent() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(STATE_FILENAME);
        let state = state_fixture(noon());
        state.save(&path).unwrap();

        let back = State::load(&path).expect("load");
        assert_eq!(back.active_slot, Some(2));
        assert_eq!(back.spare_slot, Some(3));
        assert_eq!(back.accounts.len(), 2);
        assert_eq!(
            back.account(2).and_then(AccountBrief::binding_pct),
            Some(38.2)
        );

        std::fs::write(&path, b"{ half written").unwrap();
        assert!(State::load(&path).is_none());
        assert!(State::load(&dir.path().join("missing.json")).is_none());
    }

    #[test]
    fn staleness_is_measured_from_the_writers_stamp() {
        let now = noon();
        let mut s = state_fixture(now);
        assert!(!s.is_stale(now));
        s.updated_at = (now.with_timezone(&Utc) - Duration::minutes(30)).to_rfc3339();
        assert!(s.is_stale(now));
        s.updated_at = "not a time".into();
        assert!(s.is_stale(now), "an unreadable stamp is not a fresh one");
    }

    #[test]
    fn presets_round_trip_their_wire_names() {
        for p in [Preset::Lean, Preset::Standard, Preset::Full] {
            assert_eq!(Preset::parse(p.as_str()), Some(p));
        }
        assert_eq!(Preset::parse("powerline"), None);
        assert_eq!(Preset::default(), Preset::Standard);
    }

    #[test]
    fn a_branch_is_read_from_the_git_directory_not_a_subprocess() {
        let dir = tempfile::tempdir().unwrap();
        let work = dir.path().join("proj/src");
        std::fs::create_dir_all(&work).unwrap();
        let git = dir.path().join("proj/.git");
        std::fs::create_dir_all(&git).unwrap();

        std::fs::write(git.join("HEAD"), b"ref: refs/heads/feature/x\n").unwrap();
        assert_eq!(git_branch(&work).as_deref(), Some("feature/x"));

        // Detached HEAD has no branch to name.
        std::fs::write(
            git.join("HEAD"),
            b"9f1c0de0000000000000000000000000000000ab\n",
        )
        .unwrap();
        assert_eq!(git_branch(&work), None);

        assert_eq!(git_branch(dir.path()), None);
    }

    #[test]
    fn a_worktree_git_file_is_followed() {
        let dir = tempfile::tempdir().unwrap();
        let real = dir.path().join("real.git");
        std::fs::create_dir_all(&real).unwrap();
        std::fs::write(real.join("HEAD"), b"ref: refs/heads/wt\n").unwrap();

        let work = dir.path().join("wt");
        std::fs::create_dir_all(&work).unwrap();
        std::fs::write(
            work.join(".git"),
            format!("gitdir: {}\n", real.display()).as_bytes(),
        )
        .unwrap();
        assert_eq!(git_branch(&work).as_deref(), Some("wt"));
    }
}
