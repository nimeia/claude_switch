//! Auto-switch decision engine (threshold / cooldown / best strategy).

use std::time::{Duration, Instant};

use parking_lot::Mutex;
use serde::{Deserialize, Serialize};

use crate::sequence::SequenceData;
use crate::settings::AutoSwitchSettings;
use crate::usage::{UsageCache, UsageStatus};

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum NoSwitchReason {
    BelowThreshold,
    Cooldown,
    NoCandidate,
    NoActive,
    ObserveOnly,
    EnginePaused,
    /// Active usage is temporarily unreadable; counting toward failover.
    ActiveUnhealthy,
    /// Active account is a managed API key — it has no quota to watch, and it
    /// works fine, so there is nothing to switch away from.
    ActiveApiKey,
    Other,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AutoswitchDecision {
    pub should_switch: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<NoSwitchReason>,
    pub detail: String,
}

#[derive(Default)]
struct RuntimeState {
    last_switch: Option<Instant>,
    observe_only: bool,
    /// Consecutive ticks the active account's usage was unreadable.
    unhealthy_ticks: u32,
}

/// Whether the active account gives a figure to compare, or settles the tick.
enum ActiveState {
    /// A real utilization to compare against the threshold.
    Measured(f64),
    /// Unusable; move if there is anywhere to move. Carries the reason so the
    /// "nowhere to go" message can name it.
    ///
    /// A distinct variant rather than a sentinel percentage: an account measured
    /// at exactly 100% is a real reading, and comparing floats against the
    /// failover stand-in would report it as broken.
    Failover(UsageStatus),
    /// Nothing to do this tick; report this.
    Settled(AutoswitchDecision),
}

/// Outcome of scanning the switchable accounts.
struct Ranked {
    /// Best measured candidate and its headroom.
    best: Option<(u32, f64)>,
    /// First opted-in API-key slot, usable only when nothing measured qualifies.
    api_key_fallback: Option<u32>,
    /// Candidates skipped for having no usable usage — reported, not hidden.
    skipped_unmeasured: u32,
    /// Candidates skipped for running a session terminal — also reported, since
    /// "nothing to switch to" reads very differently when the reason is that
    /// every spare account is already in use.
    skipped_session_busy: u32,
}

/// Stand-in utilization for an active account whose real figure is unusable.
///
/// Treating it as fully consumed is what makes failover work with the ordinary
/// ranking code: every measured, non-exhausted candidate then clears the
/// hysteresis margin, so the best one wins instead of nothing happening.
const FAILOVER_PCT: f64 = 100.0;

/// Pure-ish decision helper + cooldown clock.
pub struct AutoSwitchEngine {
    state: Mutex<RuntimeState>,
}

impl AutoSwitchEngine {
    #[must_use]
    pub fn new() -> Self {
        Self {
            state: Mutex::new(RuntimeState::default()),
        }
    }

    pub fn set_observe_only(&self, v: bool) {
        self.state.lock().observe_only = v;
    }

    pub fn mark_switched(&self) {
        self.state.lock().last_switch = Some(Instant::now());
    }

    /// Same answer as [`AutoSwitchEngine::decide`] without advancing the
    /// failover counter — a preview must not bring a real failover closer.
    pub fn preview(
        &self,
        seq: &SequenceData,
        usage: &UsageCache,
        settings: &AutoSwitchSettings,
    ) -> AutoswitchDecision {
        let saved = self.state.lock().unhealthy_ticks;
        let decision = self.decide(seq, usage, settings);
        self.state.lock().unhealthy_ticks = saved;
        decision
    }

    /// What the active account's own state says about looking for a target.
    ///
    /// Measured is the ordinary path; the rest are the ways an account can be
    /// unusable, and they do not deserve the same patience — a cleared login is
    /// broken *now*, while a network blip may fix itself.
    fn active_utilization(
        &self,
        usage: &UsageCache,
        settings: &AutoSwitchSettings,
        active: u32,
    ) -> ActiveState {
        let active_status = usage.status(active);
        let active_measured = usage.get(active).and_then(|u| u.binding_pct());

        match (active_measured, active_status) {
            // Ordinary: compare against the threshold.
            (Some(pct), _) => {
                self.state.lock().unhealthy_ticks = 0;
                if pct < settings.threshold {
                    return ActiveState::Settled(AutoswitchDecision {
                        should_switch: false,
                        target: None,
                        reason: Some(NoSwitchReason::BelowThreshold),
                        detail: format!("active {pct:.1}% < threshold {}", settings.threshold),
                    });
                }
                ActiveState::Measured(pct)
            }
            // A managed API key works; it simply has no subscription window to
            // watch. Nothing is wrong, so nothing should move (ref: `autoswitch.py`
            // `active-api-key`).
            (None, UsageStatus::ApiKey) => {
                self.state.lock().unhealthy_ticks = 0;
                ActiveState::Settled(AutoswitchDecision {
                    should_switch: false,
                    target: None,
                    reason: Some(NoSwitchReason::ActiveApiKey),
                    detail: "active account is an API key — no quota to watch".into(),
                })
            }
            // Broken in a way only the user can repair: the login was cleared,
            // the refresh lineage is dead, the slot is empty, or the subscription
            // is gone. The session is already unusable, so waiting out
            // `unhealthy_ticks` buys nothing — move now if anywhere to move.
            (None, s) if s.is_permanent() => {
                self.state.lock().unhealthy_ticks = 0;
                ActiveState::Failover(s)
            }
            // Transient (network, rate limit, not fetched yet): the numbers may
            // come back, so spend `unhealthy_ticks` before disturbing a session
            // that is probably fine.
            (None, _) => {
                let ticks = {
                    let mut st = self.state.lock();
                    st.unhealthy_ticks = st.unhealthy_ticks.saturating_add(1);
                    st.unhealthy_ticks
                };
                if ticks < settings.unhealthy_ticks.max(1) {
                    return ActiveState::Settled(AutoswitchDecision {
                        should_switch: false,
                        target: None,
                        reason: Some(NoSwitchReason::ActiveUnhealthy),
                        detail: format!(
                            "active usage unreadable {ticks}/{} before failover",
                            settings.unhealthy_ticks.max(1)
                        ),
                    });
                }
                ActiveState::Failover(active_status)
            }
        }
    }

    /// Scan the switchable accounts for a target.
    ///
    /// Unknown usage is not free usage: an account whose credential was wiped by
    /// a logout reports no numbers, and reading that as "0% used, 100% free"
    /// makes the one account that cannot serve a request look like the best
    /// target in the list — switching to it logs Claude Code out, precisely when
    /// the active account is at its limit and the user needs the switch to work
    /// (ref: `autoswitch.py` `_rank_candidates`, which skips `headroom is None`).
    ///
    /// `session_busy` lists slots with a live session terminal. They are skipped:
    /// a `cswitch` session already owns that account's token in its own profile,
    /// and making it the default login too would put one rotating refresh token
    /// in two config directories — the stale-copy failure, with nobody watching.
    /// Its quota is being consumed by that terminal anyway, so it is the last
    /// account an automatic switch should land on. A manual switch still may.
    fn rank(
        seq: &SequenceData,
        usage: &UsageCache,
        settings: &AutoSwitchSettings,
        active: u32,
        active_pct: f64,
        session_busy: &[u32],
    ) -> Ranked {
        let active_headroom = 100.0 - active_pct;
        let mut out = Ranked {
            best: None,
            api_key_fallback: None,
            skipped_unmeasured: 0,
            skipped_session_busy: 0,
        };

        for &num in &seq.sequence {
            if num == active || seq.account(num).is_some_and(|a| a.disabled) {
                continue;
            }
            if session_busy.contains(&num) {
                out.skipped_session_busy += 1;
                continue;
            }

            // API-key slots have no subscription window to compare, so they can
            // never win on headroom; they are a last resort and only when the
            // user opted in.
            if usage.status(num) == UsageStatus::ApiKey {
                if settings.include_api_key_accounts && out.api_key_fallback.is_none() {
                    out.api_key_fallback = Some(num);
                }
                continue;
            }

            let Some(pct) = usage.get(num).and_then(|u| u.binding_pct()) else {
                out.skipped_unmeasured += 1;
                continue;
            };
            if pct >= settings.threshold {
                continue; // candidate must be under threshold
            }
            let headroom = 100.0 - pct;
            if headroom < active_headroom + settings.hysteresis_pct {
                continue;
            }
            // `is_none_or` reads better but postdates this crate's MSRV.
            if !out.best.is_some_and(|(_, h)| headroom >= h) {
                out.best = Some((num, headroom));
            }
        }
        out
    }

    /// Decide whether to switch given sequence + usage cache + settings.
    pub fn decide(
        &self,
        seq: &SequenceData,
        usage: &UsageCache,
        settings: &AutoSwitchSettings,
    ) -> AutoswitchDecision {
        self.decide_with_sessions(seq, usage, settings, &[])
    }

    /// [`Self::decide`], told which slots already have a session terminal.
    pub fn decide_with_sessions(
        &self,
        seq: &SequenceData,
        usage: &UsageCache,
        settings: &AutoSwitchSettings,
        session_busy: &[u32],
    ) -> AutoswitchDecision {
        if self.state.lock().observe_only {
            return AutoswitchDecision {
                should_switch: false,
                target: None,
                reason: Some(NoSwitchReason::ObserveOnly),
                detail: "not autoswitch leader".into(),
            };
        }
        if !settings.enabled {
            return AutoswitchDecision {
                should_switch: false,
                target: None,
                reason: Some(NoSwitchReason::EnginePaused),
                detail: "autoswitch disabled".into(),
            };
        }

        let Some(active) = seq.active_account_number else {
            return AutoswitchDecision {
                should_switch: false,
                target: None,
                reason: Some(NoSwitchReason::NoActive),
                detail: "no active account".into(),
            };
        };

        if let Some(last) = self.state.lock().last_switch {
            let cool = Duration::from_secs_f64(settings.cooldown_seconds);
            if last.elapsed() < cool {
                return AutoswitchDecision {
                    should_switch: false,
                    target: None,
                    reason: Some(NoSwitchReason::Cooldown),
                    detail: format!("cooldown {}s", settings.cooldown_seconds),
                };
            }
        }

        let (active_pct, failover_status) = match self.active_utilization(usage, settings, active) {
            ActiveState::Settled(decision) => return decision,
            ActiveState::Measured(pct) => (pct, None),
            ActiveState::Failover(status) => (FAILOVER_PCT, Some(status)),
        };

        // Only *measured* accounts can be targets — see [`Self::rank`].
        let Ranked {
            best,
            api_key_fallback,
            skipped_unmeasured,
            skipped_session_busy,
        } = Self::rank(seq, usage, settings, active, active_pct, session_busy);

        if let Some((num, h)) = best {
            return AutoswitchDecision {
                should_switch: true,
                target: Some(num),
                reason: None,
                detail: format!("best headroom {h:.1}% on account {num}"),
            };
        }
        if let Some(num) = api_key_fallback {
            return AutoswitchDecision {
                should_switch: true,
                target: Some(num),
                reason: None,
                detail: format!("no measured oauth candidate; API key account {num}"),
            };
        }

        // Nothing to move to. Say whether that is because the active account is
        // broken with no healthy peer (the user must fix an account) or simply
        // because no peer has more headroom (normal, nothing to do).
        let mut notes: Vec<String> = Vec::new();
        if skipped_unmeasured > 0 {
            notes.push(format!(
                "{skipped_unmeasured} skipped: no usable usage data"
            ));
        }
        if skipped_session_busy > 0 {
            notes.push(format!(
                "{skipped_session_busy} skipped: running a session terminal"
            ));
        }
        let skipped_note = if notes.is_empty() {
            String::new()
        } else {
            format!(" ({})", notes.join("; "))
        };
        AutoswitchDecision {
            should_switch: false,
            target: None,
            reason: Some(NoSwitchReason::NoCandidate),
            detail: failover_status.map_or_else(
                || format!("no better account under threshold{skipped_note}"),
                |status| {
                    format!(
                        "active account unusable ({status:?}) and no healthy account                          to fail over to{skipped_note}"
                    )
                },
            ),
        }
    }
}

impl Default for AutoSwitchEngine {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sequence::AccountRecord;
    use crate::usage::{Usage, UsageStatus, UsageWindow};

    fn seq_two() -> SequenceData {
        let mut s = SequenceData::default();
        s.upsert_account(
            1,
            AccountRecord {
                email: "a@x.com".into(),
                uuid: String::new(),
                org_uuid: String::new(),
                org_name: String::new(),
                added: String::new(),
                alias: None,
                disabled: false,
            },
        );
        s.upsert_account(
            2,
            AccountRecord {
                email: "b@x.com".into(),
                uuid: String::new(),
                org_uuid: String::new(),
                org_name: String::new(),
                added: String::new(),
                alias: None,
                disabled: false,
            },
        );
        s.active_account_number = Some(1);
        s
    }

    fn usage_at(five: f64, seven: f64) -> Usage {
        Usage {
            five_hour: Some(UsageWindow {
                pct: five,
                resets_at: None,
            }),
            seven_day: Some(UsageWindow {
                pct: seven,
                resets_at: None,
            }),
        }
    }

    fn settings_at(threshold: f64) -> AutoSwitchSettings {
        AutoSwitchSettings {
            enabled: true,
            threshold,
            cooldown_seconds: 0.0,
            hysteresis_pct: 10.0,
            ..Default::default()
        }
    }

    /// The account is at its limit — a switch is wanted and must go somewhere real.
    fn exhausted_active() -> (SequenceData, UsageCache) {
        let cache = UsageCache::new();
        cache.put(1, usage_at(95.0, 50.0), UsageStatus::Ok);
        (seq_two(), cache)
    }

    #[test]
    fn never_targets_an_account_whose_credential_is_dead() {
        // The reported case: slot 2's login was wiped, so it reports no usage.
        // Treating "no data" as "0% used" would make it the *best* target and
        // switching to it logs Claude Code out.
        let (seq, cache) = exhausted_active();
        cache.put(2, Usage::default(), UsageStatus::NeedsLogin);

        let d = AutoSwitchEngine::new().decide(&seq, &cache, &settings_at(90.0));
        assert!(!d.should_switch, "{}", d.detail);
        assert_eq!(d.target, None);
        assert!(matches!(d.reason, Some(NoSwitchReason::NoCandidate)));
        assert!(d.detail.contains("no usable usage data"), "{}", d.detail);
    }

    #[test]
    fn never_targets_an_account_that_was_never_measured() {
        // Same rule for a slot that simply has not been fetched yet.
        let (seq, cache) = exhausted_active();
        let d = AutoSwitchEngine::new().decide(&seq, &cache, &settings_at(90.0));
        assert!(!d.should_switch, "{}", d.detail);
    }

    #[test]
    fn never_targets_an_unavailable_account() {
        let (seq, cache) = exhausted_active();
        cache.put(2, Usage::default(), UsageStatus::Unavailable);
        let d = AutoSwitchEngine::new().decide(&seq, &cache, &settings_at(90.0));
        assert!(!d.should_switch, "{}", d.detail);
    }

    #[test]
    fn api_key_accounts_are_excluded_unless_opted_in() {
        let (seq, cache) = exhausted_active();
        cache.put(2, Usage::default(), UsageStatus::ApiKey);

        let mut settings = settings_at(90.0);
        settings.include_api_key_accounts = false;
        let d = AutoSwitchEngine::new().decide(&seq, &cache, &settings);
        assert!(!d.should_switch, "{}", d.detail);

        // Opted in, they are a last resort — they have no window to compare.
        settings.include_api_key_accounts = true;
        let d = AutoSwitchEngine::new().decide(&seq, &cache, &settings);
        assert!(d.should_switch, "{}", d.detail);
        assert_eq!(d.target, Some(2));
    }

    #[test]
    fn a_measured_account_still_wins_over_an_api_key_one() {
        let mut seq = seq_two();
        seq.upsert_account(
            3,
            AccountRecord {
                email: "c@x.com".into(),
                uuid: String::new(),
                org_uuid: String::new(),
                org_name: String::new(),
                added: String::new(),
                alias: None,
                disabled: false,
            },
        );
        let cache = UsageCache::new();
        cache.put(1, usage_at(95.0, 50.0), UsageStatus::Ok);
        cache.put(2, Usage::default(), UsageStatus::ApiKey);
        cache.put(3, usage_at(20.0, 10.0), UsageStatus::Ok);

        let mut settings = settings_at(90.0);
        settings.include_api_key_accounts = true;
        let d = AutoSwitchEngine::new().decide(&seq, &cache, &settings);
        assert_eq!(d.target, Some(3), "{}", d.detail);
    }

    /// Healthy peer to fail over to, and a settings block with a high threshold
    /// so nothing but failover could ever produce a switch.
    fn healthy_peer() -> (SequenceData, UsageCache, AutoSwitchSettings) {
        let cache = UsageCache::new();
        cache.put(2, usage_at(20.0, 5.0), UsageStatus::Ok);
        (seq_two(), cache, settings_at(90.0))
    }

    #[test]
    fn fails_over_immediately_when_the_active_login_is_dead() {
        // Nothing to wait for: the session cannot serve a request right now.
        let (seq, cache, settings) = healthy_peer();
        cache.put(1, Usage::default(), UsageStatus::NeedsLogin);

        let d = AutoSwitchEngine::new().decide(&seq, &cache, &settings);
        assert!(d.should_switch, "{}", d.detail);
        assert_eq!(d.target, Some(2));
    }

    #[test]
    fn fails_over_immediately_when_the_active_subscription_is_gone() {
        let (seq, cache, settings) = healthy_peer();
        cache.put(1, Usage::default(), UsageStatus::NoSubscription);

        let d = AutoSwitchEngine::new().decide(&seq, &cache, &settings);
        assert!(d.should_switch, "{}", d.detail);
        assert_eq!(d.target, Some(2));
    }

    #[test]
    fn fails_over_immediately_when_the_active_slot_has_no_credential() {
        let (seq, cache, settings) = healthy_peer();
        cache.put(1, Usage::default(), UsageStatus::NoCredential);

        let d = AutoSwitchEngine::new().decide(&seq, &cache, &settings);
        assert!(d.should_switch, "{}", d.detail);
        assert_eq!(d.target, Some(2));
    }

    #[test]
    fn waits_unhealthy_ticks_before_failing_over_on_a_transient_fault() {
        // A network blip must not yank a working session away on the first tick.
        let (seq, cache, mut settings) = healthy_peer();
        settings.unhealthy_ticks = 3;
        cache.put(1, Usage::default(), UsageStatus::Unavailable);
        let eng = AutoSwitchEngine::new();

        for expected in 1..3 {
            let d = eng.decide(&seq, &cache, &settings);
            assert!(!d.should_switch, "tick {expected}: {}", d.detail);
            assert_eq!(d.reason, Some(NoSwitchReason::ActiveUnhealthy));
            assert!(d.detail.contains(&format!("{expected}/3")), "{}", d.detail);
        }
        let d = eng.decide(&seq, &cache, &settings);
        assert!(d.should_switch, "{}", d.detail);
        assert_eq!(d.target, Some(2));
    }

    #[test]
    fn recovered_usage_resets_the_failover_countdown() {
        let (seq, cache, mut settings) = healthy_peer();
        settings.unhealthy_ticks = 3;
        let eng = AutoSwitchEngine::new();

        cache.put(1, Usage::default(), UsageStatus::Unavailable);
        eng.decide(&seq, &cache, &settings);
        eng.decide(&seq, &cache, &settings);

        // Numbers come back below threshold — the count must start over, or a
        // few scattered blips would eventually add up to a spurious failover.
        cache.put(1, usage_at(10.0, 5.0), UsageStatus::Ok);
        let d = eng.decide(&seq, &cache, &settings);
        assert_eq!(d.reason, Some(NoSwitchReason::BelowThreshold));

        cache.put(1, Usage::default(), UsageStatus::Unavailable);
        let d = eng.decide(&seq, &cache, &settings);
        assert!(d.detail.contains("1/3"), "{}", d.detail);
    }

    #[test]
    fn preview_does_not_advance_the_failover_countdown() {
        let (seq, cache, mut settings) = healthy_peer();
        settings.unhealthy_ticks = 2;
        cache.put(1, Usage::default(), UsageStatus::Unavailable);
        let eng = AutoSwitchEngine::new();

        // Asking "what would happen" must not make it happen sooner.
        for _ in 0..5 {
            assert!(!eng.preview(&seq, &cache, &settings).should_switch);
        }
        let d = eng.decide(&seq, &cache, &settings);
        assert!(!d.should_switch, "{}", d.detail);
        assert!(d.detail.contains("1/2"), "{}", d.detail);
    }

    #[test]
    fn an_active_account_at_exactly_100_percent_is_not_a_failover() {
        // A real reading of 100% is measured, not broken: the message must say
        // "no better account", never "active account unusable".
        let (seq, cache, settings) = healthy_peer();
        cache.put(1, usage_at(100.0, 100.0), UsageStatus::Ok);
        cache.put(2, usage_at(95.0, 95.0), UsageStatus::Ok);

        let d = AutoSwitchEngine::new().decide(&seq, &cache, &settings);
        assert!(!d.should_switch, "{}", d.detail);
        assert!(!d.detail.contains("unusable"), "{}", d.detail);
    }

    #[test]
    fn an_active_api_key_is_left_alone() {
        // It works; it just has no window to watch. Moving would be wrong.
        let (seq, cache, settings) = healthy_peer();
        cache.put(1, Usage::default(), UsageStatus::ApiKey);

        let d = AutoSwitchEngine::new().decide(&seq, &cache, &settings);
        assert!(!d.should_switch, "{}", d.detail);
        assert_eq!(d.reason, Some(NoSwitchReason::ActiveApiKey));
    }

    #[test]
    fn a_broken_active_with_no_healthy_peer_says_so() {
        let seq = seq_two();
        let cache = UsageCache::new();
        cache.put(1, Usage::default(), UsageStatus::NeedsLogin);
        cache.put(2, Usage::default(), UsageStatus::NeedsLogin);

        let d = AutoSwitchEngine::new().decide(&seq, &cache, &settings_at(90.0));
        assert!(!d.should_switch);
        assert_eq!(d.reason, Some(NoSwitchReason::NoCandidate));
        assert!(d.detail.contains("no healthy account"), "{}", d.detail);
    }

    #[test]
    fn failover_still_refuses_an_exhausted_peer() {
        // Failing over onto an account that is itself at its limit helps nobody.
        let (seq, cache, settings) = healthy_peer();
        cache.put(1, Usage::default(), UsageStatus::NeedsLogin);
        cache.put(2, usage_at(99.0, 99.0), UsageStatus::Ok);

        let d = AutoSwitchEngine::new().decide(&seq, &cache, &settings);
        assert!(!d.should_switch, "{}", d.detail);
    }

    #[test]
    fn switches_when_over_threshold_and_better_exists() {
        let eng = AutoSwitchEngine::new();
        let seq = seq_two();
        let cache = UsageCache::new();
        cache.put(
            1,
            Usage {
                five_hour: Some(UsageWindow {
                    pct: 95.0,
                    resets_at: None,
                }),
                seven_day: Some(UsageWindow {
                    pct: 50.0,
                    resets_at: None,
                }),
            },
            UsageStatus::Ok,
        );
        cache.put(
            2,
            Usage {
                five_hour: Some(UsageWindow {
                    pct: 10.0,
                    resets_at: None,
                }),
                seven_day: Some(UsageWindow {
                    pct: 5.0,
                    resets_at: None,
                }),
            },
            UsageStatus::Ok,
        );
        let settings = AutoSwitchSettings {
            enabled: true,
            threshold: 90.0,
            cooldown_seconds: 0.0,
            hysteresis_pct: 10.0,
            ..Default::default()
        };
        let d = eng.decide(&seq, &cache, &settings);
        assert!(d.should_switch);
        assert_eq!(d.target, Some(2));
    }
}
