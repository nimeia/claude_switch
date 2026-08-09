//! Auto-continue policy: deciding when an interrupted agent run may resume.
//!
//! Pure decision logic, deliberately free of any transport. The ACP client
//! ([`claude-switch-acp`]) feeds it outcomes from a live agent; the Windows GUI
//! reaches the same functions over FFI (`acp_classify` / `acp_decide`) so the
//! table below has exactly one implementation. A second copy in C# would drift,
//! and the subtle rows are the ones that would drift first.
//!
//! # The table
//!
//! "Resume when the agent stops" is wrong, and dangerously so — most stops are
//! the model handing control back to a human. What deserves a retry, and what
//! that retry should look like, differs per class:
//!
//! | outcome | meaning | action |
//! |---|---|---|
//! | `end_turn` | model finished, awaiting the user | **stop** |
//! | `max_tokens` / `max_turn_requests` | work cut off mid-flight | continue at once |
//! | `refusal` / `cancelled` | deliberate halt | stop |
//! | server error, transport death | network/backend blip | continue with backoff |
//! | usage limit reached | quota exhausted | wait for the window, then continue |
//! | `authentication_failed` | needs a human (re-login, proxy) | stop |
//!
//! The last row is not hypothetical: on a proxied network a missing
//! `HTTPS_PROXY` yields `403 "Request not allowed"` tagged
//! `authentication_failed` (see [`crate::proxy`]), and retrying it forever
//! accomplishes nothing.

use std::time::Duration;

use serde::{Deserialize, Serialize};

/// Prompt sent to resume an interrupted run.
///
/// Phrased to resume rather than restart: repeating the original instruction
/// would make the agent redo work it already finished before the interruption.
pub const DEFAULT_CONTINUE_MESSAGE: &str =
    "Continue from where you left off. Do not repeat work that is already done.";

/// Message fragments that mark a retryable transport or backend failure.
///
/// Consulted only when `errorKind` did not already settle the class — some
/// failures reach us with no kind at all.
const NETWORK_MARKERS: [&str; 11] = [
    "econnreset",
    "fetch failed",
    "connection closed",
    "connection error",
    "socket",
    "overloaded",
    "timed out",
    "529",
    "500",
    "502",
    "503",
];

/// Terminal states a turn can reach on its own (ACP `stopReason`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum StopReason {
    EndTurn,
    MaxTokens,
    MaxTurnRequests,
    Refusal,
    Cancelled,
    /// A `stopReason` string this build does not know.
    Other,
}

impl StopReason {
    #[must_use]
    pub fn parse(s: &str) -> Self {
        match s {
            "end_turn" => Self::EndTurn,
            "max_tokens" => Self::MaxTokens,
            "max_turn_requests" => Self::MaxTurnRequests,
            "refusal" => Self::Refusal,
            "cancelled" => Self::Cancelled,
            _ => Self::Other,
        }
    }

    /// Whether the turn stopped because a budget ran out mid-work rather than
    /// because the model was done.
    #[must_use]
    pub const fn is_truncation(self) -> bool {
        matches!(self, Self::MaxTokens | Self::MaxTurnRequests)
    }
}

/// Why a turn failed instead of finishing.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum InterruptKind {
    /// 5xx, overloaded, connection reset, socket closed mid-response.
    Network,
    /// Subscription quota exhausted; needs the 5h window to roll over.
    RateLimit,
    /// 403 / expired OAuth / missing proxy. A human must act.
    Auth,
    /// The agent process died or closed its stdio.
    AdapterCrash,
    /// No response within the turn timeout.
    Timeout,
    /// Well-formed failure we have no rule for.
    Unknown,
}

impl InterruptKind {
    /// Whether retrying the same request could plausibly succeed.
    ///
    /// [`Self::RateLimit`] is retryable too, but only after a long wait, so it
    /// runs on its own budget in [`decide`] rather than counting here.
    #[must_use]
    pub const fn is_transient(self) -> bool {
        matches!(
            self,
            Self::Network | Self::AdapterCrash | Self::Timeout | Self::Unknown
        )
    }

    /// Whether the agent process must be respawned before retrying.
    #[must_use]
    pub const fn needs_fresh_process(self) -> bool {
        matches!(self, Self::AdapterCrash | Self::Timeout)
    }
}

/// What one turn produced.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "state", content = "value")]
pub enum TurnOutcome {
    Completed(StopReason),
    Interrupted(InterruptKind),
}

/// How a turn ended at the transport layer, when there is no agent reply to read.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TransportFailure {
    /// Process exited or stdout closed.
    Disconnected,
    /// Turn timeout elapsed.
    Timeout,
}

/// Classify an agent-reported failure.
///
/// `error_kind` is ACP's machine-readable class (`data.errorKind`), which uses
/// the same taxonomy Claude Code writes into its transcripts. `message` is the
/// human-readable text.
///
/// **Order matters.** A quota hit is reported with `errorKind: "server_error"`,
/// so the message text is checked *first*; without that, hitting a limit would
/// be retried on a ten-second backoff instead of waiting for the window.
#[must_use]
pub fn classify_failure(error_kind: Option<&str>, message: &str) -> InterruptKind {
    let text = message.to_ascii_lowercase();

    if text.contains("usage limit reached") || text.contains("rate limit") || text.contains("429") {
        return InterruptKind::RateLimit;
    }

    match error_kind {
        Some("authentication_failed") => return InterruptKind::Auth,
        Some("server_error") => return InterruptKind::Network,
        _ => {}
    }

    if NETWORK_MARKERS.iter().any(|m| text.contains(m)) {
        return InterruptKind::Network;
    }
    if text.contains("403") || text.contains("authenticate") {
        return InterruptKind::Auth;
    }
    InterruptKind::Unknown
}

/// Classify a transport-level failure (no agent reply to inspect).
#[must_use]
pub const fn classify_transport(failure: TransportFailure) -> InterruptKind {
    match failure {
        TransportFailure::Disconnected => InterruptKind::AdapterCrash,
        TransportFailure::Timeout => InterruptKind::Timeout,
    }
}

/// What to do when the account's 5h quota is exhausted.
///
/// A quota wall is not a fault to retry through — the only two things that move
/// it are time and a different account.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum RateLimitAction {
    /// Stop and let a human decide.
    Stop,
    /// Sleep until the window resets, then continue on the same account.
    ///
    /// The default: it keeps the account, the conversation and the transcript
    /// exactly where they are, and costs only time.
    #[default]
    Wait,
    /// Hand the run to another account and continue now.
    ///
    /// Falls back to [`Self::Wait`] when no account has headroom — "switch"
    /// with nowhere to switch to should not become "give up".
    Switch,
}

/// What the engine knows about the running account's quota.
///
/// Supplied by the caller so [`decide`] stays pure. All fields default to
/// "unknown", which degrades to a fixed backoff and no switching — the right
/// behaviour for a headless caller with no usage data.
#[derive(Clone, Copy, Debug, Default)]
pub struct QuotaContext {
    /// Seconds until this account's 5h window rolls over.
    pub seconds_until_reset: Option<u64>,
    /// Another account has headroom and could take the run.
    pub alternative_available: bool,
    /// This account has no quota left.
    ///
    /// Lets a *network* failure be recognised as pointless to retry: with the
    /// quota already gone, the retry would only reach the same wall.
    pub exhausted: bool,
}

/// Woken this long after the reported reset, so a clock skew of a few seconds
/// does not put the retry back on the wrong side of the boundary.
const RESET_MARGIN_SECONDS: u64 = 30;

/// Retry budget and pacing.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ContinuePolicy {
    /// Automatic continuations allowed for transient failures.
    #[serde(default = "default_max_attempts")]
    pub max_attempts: u32,
    /// First backoff, in seconds; doubles per attempt up to `max_delay_seconds`.
    #[serde(default = "default_base_delay")]
    pub base_delay_seconds: u64,
    #[serde(default = "default_max_delay")]
    pub max_delay_seconds: u64,
    /// Continue after `max_tokens` / `max_turn_requests`.
    #[serde(default = "default_true")]
    pub continue_on_truncation: bool,
    /// How long to wait before retrying after a quota hit, in seconds.
    #[serde(default = "default_rate_limit_delay")]
    pub rate_limit_delay_seconds: u64,
    /// What a quota wall means for this run.
    #[serde(default)]
    pub on_rate_limit: RateLimitAction,
    /// Longest single quota wait, in hours. A window is five hours, so a wait
    /// longer than that means something other than the 5h bucket is in play.
    #[serde(default = "default_max_wait_hours")]
    pub max_wait_hours: u32,
    /// Quota waits allowed. Separate budget from `max_attempts`.
    #[serde(default = "default_max_rate_limit_waits")]
    pub max_rate_limit_waits: u32,
    /// Long retries allowed once the exponential backoff budget is spent.
    ///
    /// The backoff ladder is tuned for a blip — five doublings from ten seconds
    /// is about five minutes, which an outage outlives easily. Stopping there
    /// abandons a run over a network problem that would have cleared on its own,
    /// so the budget is followed by a handful of far slower attempts rather than
    /// by giving up. Zero restores the old behaviour.
    #[serde(default = "default_max_long_retries")]
    pub max_long_retries: u32,
    /// Wait before each long retry, in seconds.
    #[serde(default = "default_long_retry_delay")]
    pub long_retry_delay_seconds: u64,
    /// Text sent to resume.
    #[serde(default = "default_continue_message")]
    pub continue_message: String,
}

fn default_max_attempts() -> u32 {
    5
}
fn default_base_delay() -> u64 {
    10
}
fn default_max_delay() -> u64 {
    300
}
fn default_true() -> bool {
    true
}
fn default_rate_limit_delay() -> u64 {
    15 * 60
}
fn default_max_wait_hours() -> u32 {
    6
}
/// Enough waits to carry a run across a couple of window rollovers.
fn default_max_rate_limit_waits() -> u32 {
    3
}
/// Three long retries covers an outage of roughly half an hour.
fn default_max_long_retries() -> u32 {
    3
}
fn default_long_retry_delay() -> u64 {
    10 * 60
}
fn default_continue_message() -> String {
    DEFAULT_CONTINUE_MESSAGE.into()
}

impl Default for ContinuePolicy {
    fn default() -> Self {
        Self {
            max_attempts: default_max_attempts(),
            base_delay_seconds: default_base_delay(),
            max_delay_seconds: default_max_delay(),
            continue_on_truncation: true,
            rate_limit_delay_seconds: default_rate_limit_delay(),
            on_rate_limit: RateLimitAction::default(),
            max_wait_hours: default_max_wait_hours(),
            max_rate_limit_waits: default_max_rate_limit_waits(),
            max_long_retries: default_max_long_retries(),
            long_retry_delay_seconds: default_long_retry_delay(),
            continue_message: default_continue_message(),
        }
    }
}

impl ContinuePolicy {
    #[must_use]
    pub fn base_delay(&self) -> Duration {
        Duration::from_secs(self.base_delay_seconds)
    }
    #[must_use]
    pub fn max_delay(&self) -> Duration {
        Duration::from_secs(self.max_delay_seconds)
    }
    #[must_use]
    pub fn rate_limit_delay(&self) -> Duration {
        Duration::from_secs(self.rate_limit_delay_seconds)
    }

    /// Clamp user-supplied values into a sane range.
    #[must_use]
    pub fn clamp(mut self) -> Self {
        self.max_attempts = self.max_attempts.min(100);
        self.base_delay_seconds = self.base_delay_seconds.clamp(1, 3600);
        self.max_delay_seconds = self.max_delay_seconds.clamp(self.base_delay_seconds, 86_400);
        self.rate_limit_delay_seconds = self.rate_limit_delay_seconds.clamp(60, 86_400);
        self.max_wait_hours = self.max_wait_hours.clamp(1, 24);
        self.max_rate_limit_waits = self.max_rate_limit_waits.min(48);
        self.max_long_retries = self.max_long_retries.min(48);
        self.long_retry_delay_seconds = self.long_retry_delay_seconds.clamp(60, 86_400);
        if self.continue_message.trim().is_empty() {
            self.continue_message = default_continue_message();
        }
        self
    }
}

/// Why the runner stopped.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum StopCause {
    /// The model finished and is waiting for a human.
    Completed,
    /// Truncated, but the policy forbids continuing.
    Truncated,
    Refused,
    Cancelled,
    /// Needs a human: re-login, fix the proxy.
    NeedsAuth,
    AttemptsExhausted,
    RateLimitWaitsExhausted,
    /// Quota is gone and the policy says not to wait it out.
    RateLimited,
    /// The window reset is further away than `max_wait_hours` allows.
    WaitTooLong,
    /// Could not reach a usable agent at all.
    Failed,
}

impl StopCause {
    /// Whether this ending is the ordinary one rather than a failure.
    #[must_use]
    pub const fn is_success(self) -> bool {
        matches!(self, Self::Completed)
    }
}

// `rename_all` only renames the variants; the struct-variant *fields* need
// `rename_all_fields`, or the GUI receives `delay_seconds`.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", rename_all_fields = "camelCase", tag = "action")]
pub enum ContinueDecision {
    Continue {
        delay_seconds: u64,
        needs_fresh_process: bool,
        /// Hand the run to another account before continuing. The caller moves
        /// the session transcript into that account's profile; without the
        /// move the resumed `session/load` cannot find it.
        #[serde(default)]
        switch_account: bool,
    },
    Stop {
        cause: StopCause,
    },
}

impl ContinueDecision {
    #[must_use]
    pub const fn delay(&self) -> Duration {
        match self {
            Self::Continue { delay_seconds, .. } => Duration::from_secs(*delay_seconds),
            Self::Stop { .. } => Duration::ZERO,
        }
    }
}

/// Decide what to do after one turn. Pure — no clock, no I/O.
///
/// `attempt` counts transient retries already spent; `rate_limit_waits` counts
/// quota waits already spent. Keeping them apart means a long quota wait does
/// not eat the allowance reserved for network blips.
///
/// `quota` carries what the engine knows about the account's 5h window. With it
/// unknown (all fields default), waiting falls back to a fixed delay and
/// switching is never proposed — correct for a caller with no usage data.
#[must_use]
pub fn decide(
    outcome: TurnOutcome,
    attempt: u32,
    rate_limit_waits: u32,
    policy: &ContinuePolicy,
    quota: &QuotaContext,
) -> ContinueDecision {
    match outcome {
        TurnOutcome::Completed(StopReason::EndTurn) => ContinueDecision::Stop {
            cause: StopCause::Completed,
        },
        TurnOutcome::Completed(StopReason::Refusal) => ContinueDecision::Stop {
            cause: StopCause::Refused,
        },
        TurnOutcome::Completed(StopReason::Cancelled) => ContinueDecision::Stop {
            cause: StopCause::Cancelled,
        },
        TurnOutcome::Completed(reason) if reason.is_truncation() => {
            if !policy.continue_on_truncation {
                return ContinueDecision::Stop {
                    cause: StopCause::Truncated,
                };
            }
            if attempt >= policy.max_attempts {
                return ContinueDecision::Stop {
                    cause: StopCause::AttemptsExhausted,
                };
            }
            // Truncation is not a fault: resume at once, no backoff.
            ContinueDecision::Continue {
                delay_seconds: 0,
                needs_fresh_process: false,
                switch_account: false,
            }
        }
        // An unrecognised stopReason is treated as a normal finish rather than
        // grounds to keep prompting an agent that believes it is done.
        TurnOutcome::Completed(_) => ContinueDecision::Stop {
            cause: StopCause::Completed,
        },

        TurnOutcome::Interrupted(InterruptKind::Auth) => ContinueDecision::Stop {
            cause: StopCause::NeedsAuth,
        },
        TurnOutcome::Interrupted(InterruptKind::RateLimit) => {
            decide_rate_limit(rate_limit_waits, policy, quota)
        }
        TurnOutcome::Interrupted(kind) => {
            debug_assert!(kind.is_transient());
            // A retry needs quota to spend. With the window already empty this
            // is a rate limit wearing a network error's clothes, and backing off
            // ten seconds would only reach the same wall.
            if quota.exhausted {
                return decide_rate_limit(rate_limit_waits, policy, quota);
            }
            let Some(delay_seconds) = transient_delay(attempt, policy) else {
                return ContinueDecision::Stop {
                    cause: StopCause::AttemptsExhausted,
                };
            };
            ContinueDecision::Continue {
                delay_seconds,
                needs_fresh_process: kind.needs_fresh_process(),
                switch_account: false,
            }
        }
    }
}

/// The quota-wall branch: stop, hand over, or sleep until the window rolls.
fn decide_rate_limit(
    rate_limit_waits: u32,
    policy: &ContinuePolicy,
    quota: &QuotaContext,
) -> ContinueDecision {
    if policy.on_rate_limit == RateLimitAction::Stop {
        return ContinueDecision::Stop {
            cause: StopCause::RateLimited,
        };
    }

    // Switching costs nothing but a handover, so it is tried before spending a
    // wait. With nowhere to switch to it degrades to waiting rather than to
    // giving up — the run is still perfectly resumable, just later.
    if policy.on_rate_limit == RateLimitAction::Switch && quota.alternative_available {
        return ContinueDecision::Continue {
            delay_seconds: 0,
            // The new account is a different login: the agent must be respawned
            // against its profile.
            needs_fresh_process: true,
            switch_account: true,
        };
    }

    if rate_limit_waits >= policy.max_rate_limit_waits {
        return ContinueDecision::Stop {
            cause: StopCause::RateLimitWaitsExhausted,
        };
    }

    // The real reset time beats a fixed backoff: it neither wakes early into the
    // same wall nor sleeps past the moment quota returns.
    let delay = quota
        .seconds_until_reset
        .map_or(policy.rate_limit_delay_seconds, |s| {
            s.saturating_add(RESET_MARGIN_SECONDS)
        });

    if delay > u64::from(policy.max_wait_hours) * 3600 {
        return ContinueDecision::Stop {
            cause: StopCause::WaitTooLong,
        };
    }

    ContinueDecision::Continue {
        delay_seconds: delay,
        // The quota belongs to the account, not the process; a fresh agent would
        // hit the same wall.
        needs_fresh_process: false,
        switch_account: false,
    }
}

/// Delay before the next transient retry, or `None` once both budgets are spent.
///
/// Two tiers share the one `attempt` counter, so callers that already track it
/// need no new bookkeeping:
///
/// | `attempt` | tier | delay |
/// |---|---|---|
/// | `< max_attempts` | blip | exponential backoff |
/// | next `max_long_retries` | outage | `long_retry_delay_seconds` each |
/// | beyond | — | stop |
///
/// The ladder alone tops out around five minutes, which a real outage outlives;
/// the second tier is what keeps a run alive across one without hammering.
#[must_use]
pub fn transient_delay(attempt: u32, policy: &ContinuePolicy) -> Option<u64> {
    if attempt < policy.max_attempts {
        return Some(backoff_seconds(attempt, policy));
    }
    let long_retries_used = attempt - policy.max_attempts;
    (long_retries_used < policy.max_long_retries).then_some(policy.long_retry_delay_seconds)
}

/// Exponential backoff in seconds, saturating at `max_delay_seconds`.
#[must_use]
pub fn backoff_seconds(attempt: u32, policy: &ContinuePolicy) -> u64 {
    let factor = 1_u64.checked_shl(attempt).unwrap_or(u64::MAX);
    policy
        .base_delay_seconds
        .saturating_mul(factor)
        .min(policy.max_delay_seconds)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_stop_reasons() {
        assert_eq!(StopReason::parse("end_turn"), StopReason::EndTurn);
        assert_eq!(StopReason::parse("max_tokens"), StopReason::MaxTokens);
        assert_eq!(
            StopReason::parse("max_turn_requests"),
            StopReason::MaxTurnRequests
        );
        assert_eq!(StopReason::parse("refusal"), StopReason::Refusal);
        assert_eq!(StopReason::parse("cancelled"), StopReason::Cancelled);
        assert_eq!(StopReason::parse("something_new"), StopReason::Other);
    }

    #[test]
    fn truncation_only_for_budget_stops() {
        assert!(StopReason::MaxTokens.is_truncation());
        assert!(StopReason::MaxTurnRequests.is_truncation());
        assert!(!StopReason::EndTurn.is_truncation());
        assert!(!StopReason::Refusal.is_truncation());
    }

    #[test]
    fn classifies_the_real_403_we_hit() {
        // Verbatim from a probe run with no proxy configured.
        let k = classify_failure(
            Some("authentication_failed"),
            "Internal error: Failed to authenticate. API Error: 403 Request not allowed",
        );
        assert_eq!(k, InterruptKind::Auth);
    }

    #[test]
    fn classifies_the_real_expired_oauth() {
        let k = classify_failure(
            Some("authentication_failed"),
            "Failed to authenticate: OAuth session expired and could not be refreshed",
        );
        assert_eq!(k, InterruptKind::Auth);
    }

    #[test]
    fn classifies_server_errors_as_network() {
        assert_eq!(
            classify_failure(Some("server_error"), "API Error: 529 Overloaded"),
            InterruptKind::Network
        );
        assert_eq!(
            classify_failure(None, "API Error: Unable to connect to API (ECONNRESET)"),
            InterruptKind::Network
        );
        assert_eq!(classify_failure(None, "fetch failed"), InterruptKind::Network);
        assert_eq!(
            classify_failure(None, "API Error: Connection closed mid-response."),
            InterruptKind::Network
        );
    }

    #[test]
    fn rate_limit_beats_server_error_kind() {
        // The adapter tags quota exhaustion as server_error; text must win or we
        // would hammer a limit on a ten-second backoff.
        assert_eq!(
            classify_failure(Some("server_error"), "Claude AI usage limit reached"),
            InterruptKind::RateLimit
        );
    }

    #[test]
    fn transport_failures_map_directly() {
        assert_eq!(
            classify_transport(TransportFailure::Disconnected),
            InterruptKind::AdapterCrash
        );
        assert_eq!(
            classify_transport(TransportFailure::Timeout),
            InterruptKind::Timeout
        );
    }

    #[test]
    fn end_turn_never_continues() {
        let p = ContinuePolicy::default();
        assert_eq!(
            decide(TurnOutcome::Completed(StopReason::EndTurn), 0, 0, &p, &QuotaContext::default()),
            ContinueDecision::Stop {
                cause: StopCause::Completed
            }
        );
    }

    #[test]
    fn refusal_and_cancel_never_continue() {
        let p = ContinuePolicy::default();
        assert_eq!(
            decide(TurnOutcome::Completed(StopReason::Refusal), 0, 0, &p, &QuotaContext::default()),
            ContinueDecision::Stop {
                cause: StopCause::Refused
            }
        );
        assert_eq!(
            decide(TurnOutcome::Completed(StopReason::Cancelled), 0, 0, &p, &QuotaContext::default()),
            ContinueDecision::Stop {
                cause: StopCause::Cancelled
            }
        );
    }

    #[test]
    fn auth_failure_never_continues_even_with_budget() {
        let p = ContinuePolicy {
            max_attempts: 99,
            ..Default::default()
        };
        assert_eq!(
            decide(TurnOutcome::Interrupted(InterruptKind::Auth), 0, 0, &p, &QuotaContext::default()),
            ContinueDecision::Stop {
                cause: StopCause::NeedsAuth
            }
        );
    }

    #[test]
    fn truncation_continues_immediately() {
        let p = ContinuePolicy::default();
        assert_eq!(
            decide(TurnOutcome::Completed(StopReason::MaxTokens), 0, 0, &p, &QuotaContext::default()),
            ContinueDecision::Continue {
                delay_seconds: 0,
                needs_fresh_process: false,
                switch_account: false
            }
        );
    }

    #[test]
    fn truncation_respects_opt_out() {
        let p = ContinuePolicy {
            continue_on_truncation: false,
            ..Default::default()
        };
        assert_eq!(
            decide(TurnOutcome::Completed(StopReason::MaxTokens), 0, 0, &p, &QuotaContext::default()),
            ContinueDecision::Stop {
                cause: StopCause::Truncated
            }
        );
    }

    #[test]
    fn network_failure_backs_off_exponentially() {
        let p = ContinuePolicy {
            base_delay_seconds: 10,
            ..Default::default()
        };
        let n = TurnOutcome::Interrupted(InterruptKind::Network);
        assert_eq!(
            decide(n, 0, 0, &p, &QuotaContext::default()),
            ContinueDecision::Continue {
                delay_seconds: 10,
                needs_fresh_process: false,
                switch_account: false
            }
        );
        assert_eq!(
            decide(n, 1, 0, &p, &QuotaContext::default()),
            ContinueDecision::Continue {
                delay_seconds: 20,
                needs_fresh_process: false,
                switch_account: false
            }
        );
        assert_eq!(
            decide(n, 2, 0, &p, &QuotaContext::default()),
            ContinueDecision::Continue {
                delay_seconds: 40,
                needs_fresh_process: false,
                switch_account: false
            }
        );
    }

    #[test]
    fn backoff_saturates_at_max() {
        let p = ContinuePolicy {
            base_delay_seconds: 10,
            max_delay_seconds: 60,
            ..Default::default()
        };
        assert_eq!(backoff_seconds(30, &p), 60);
        assert_eq!(backoff_seconds(64, &p), 60);
    }

    #[test]
    fn attempts_are_capped() {
        let p = ContinuePolicy {
            max_attempts: 2,
            max_long_retries: 0,
            ..Default::default()
        };
        let n = TurnOutcome::Interrupted(InterruptKind::Network);
        assert!(matches!(
            decide(n, 1, 0, &p, &QuotaContext::default()),
            ContinueDecision::Continue { .. }
        ));
        assert_eq!(
            decide(n, 2, 0, &p, &QuotaContext::default()),
            ContinueDecision::Stop {
                cause: StopCause::AttemptsExhausted
            }
        );
    }

    #[test]
    fn the_backoff_ladder_is_followed_by_slow_retries_not_by_giving_up() {
        // Five doublings from ten seconds is about five minutes, which a real
        // outage outlives. Stopping there would abandon a run over a network
        // problem that clears on its own.
        let p = ContinuePolicy {
            max_attempts: 2,
            base_delay_seconds: 10,
            max_long_retries: 2,
            long_retry_delay_seconds: 600,
            ..Default::default()
        };
        let n = TurnOutcome::Interrupted(InterruptKind::Network);

        // Tier one: the ladder.
        assert_eq!(decide(n, 0, 0, &p, &QuotaContext::default()).delay().as_secs(), 10);
        assert_eq!(decide(n, 1, 0, &p, &QuotaContext::default()).delay().as_secs(), 20);

        // Tier two: flat, slow, and still continuing.
        for attempt in [2, 3] {
            assert_eq!(
                decide(n, attempt, 0, &p, &QuotaContext::default()),
                ContinueDecision::Continue {
                    delay_seconds: 600,
                    needs_fresh_process: false,
                    switch_account: false,
                },
                "attempt {attempt} should be a long retry"
            );
        }

        // Both budgets spent.
        assert_eq!(
            decide(n, 4, 0, &p, &QuotaContext::default()),
            ContinueDecision::Stop {
                cause: StopCause::AttemptsExhausted
            }
        );
    }

    #[test]
    fn long_retries_reuse_the_attempt_counter() {
        // Deliberately one counter, so every existing caller keeps working
        // without new bookkeeping.
        let p = ContinuePolicy {
            max_attempts: 3,
            max_long_retries: 2,
            long_retry_delay_seconds: 600,
            ..Default::default()
        };
        assert_eq!(transient_delay(2, &p), Some(backoff_seconds(2, &p)));
        assert_eq!(transient_delay(3, &p), Some(600));
        assert_eq!(transient_delay(4, &p), Some(600));
        assert_eq!(transient_delay(5, &p), None);
    }

    #[test]
    fn a_dead_adapter_still_respawns_on_a_long_retry() {
        // The tier changes the delay, not what the failure needs doing about it.
        let p = ContinuePolicy {
            max_attempts: 0,
            max_long_retries: 1,
            long_retry_delay_seconds: 600,
            ..Default::default()
        };
        assert_eq!(
            decide(
                TurnOutcome::Interrupted(InterruptKind::AdapterCrash),
                0,
                0,
                &p,
                &QuotaContext::default()
            ),
            ContinueDecision::Continue {
                delay_seconds: 600,
                needs_fresh_process: true,
                switch_account: false,
            }
        );
    }

    #[test]
    fn long_retries_can_be_turned_off() {
        let p = ContinuePolicy {
            max_attempts: 1,
            max_long_retries: 0,
            ..Default::default()
        };
        assert_eq!(transient_delay(1, &p), None);
    }

    #[test]
    fn quota_default_is_to_wait_it_out() {
        // Waiting keeps the account, the conversation and the transcript where
        // they are; it only costs time.
        let p = ContinuePolicy::default();
        assert_eq!(p.on_rate_limit, RateLimitAction::Wait);
        match decide(
            TurnOutcome::Interrupted(InterruptKind::RateLimit),
            0,
            0,
            &p,
            &QuotaContext::default(),
        ) {
            ContinueDecision::Continue { switch_account, .. } => assert!(!switch_account),
            other @ ContinueDecision::Stop { .. } => panic!("expected a wait, got {other:?}"),
        }
    }

    #[test]
    fn quota_wait_uses_the_real_reset_not_a_fixed_delay() {
        // A blind backoff either wakes early into the same wall or sleeps past
        // the moment quota returns. The reported reset time does neither.
        let p = ContinuePolicy::default();
        let q = QuotaContext {
            seconds_until_reset: Some(1800),
            ..Default::default()
        };
        assert_eq!(
            decide(TurnOutcome::Interrupted(InterruptKind::RateLimit), 0, 0, &p, &q),
            ContinueDecision::Continue {
                delay_seconds: 1800 + RESET_MARGIN_SECONDS,
                needs_fresh_process: false,
                switch_account: false,
            }
        );
    }

    #[test]
    fn quota_wait_falls_back_when_the_reset_is_unknown() {
        let p = ContinuePolicy {
            rate_limit_delay_seconds: 900,
            ..Default::default()
        };
        match decide(
            TurnOutcome::Interrupted(InterruptKind::RateLimit),
            0,
            0,
            &p,
            &QuotaContext::default(),
        ) {
            ContinueDecision::Continue { delay_seconds, .. } => assert_eq!(delay_seconds, 900),
            other @ ContinueDecision::Stop { .. } => panic!("expected a wait, got {other:?}"),
        }
    }

    #[test]
    fn a_reset_beyond_the_cap_stops_instead_of_sleeping_all_day() {
        let p = ContinuePolicy {
            max_wait_hours: 2,
            ..Default::default()
        };
        let q = QuotaContext {
            seconds_until_reset: Some(5 * 3600),
            ..Default::default()
        };
        assert_eq!(
            decide(TurnOutcome::Interrupted(InterruptKind::RateLimit), 0, 0, &p, &q),
            ContinueDecision::Stop {
                cause: StopCause::WaitTooLong
            }
        );
    }

    #[test]
    fn stop_action_never_waits() {
        let p = ContinuePolicy {
            on_rate_limit: RateLimitAction::Stop,
            ..Default::default()
        };
        let q = QuotaContext {
            seconds_until_reset: Some(60),
            alternative_available: true,
            exhausted: true,
        };
        assert_eq!(
            decide(TurnOutcome::Interrupted(InterruptKind::RateLimit), 0, 0, &p, &q),
            ContinueDecision::Stop {
                cause: StopCause::RateLimited
            }
        );
    }

    #[test]
    fn switch_hands_over_and_respawns() {
        let p = ContinuePolicy {
            on_rate_limit: RateLimitAction::Switch,
            ..Default::default()
        };
        let q = QuotaContext {
            seconds_until_reset: Some(9000),
            alternative_available: true,
            exhausted: true,
        };
        assert_eq!(
            decide(TurnOutcome::Interrupted(InterruptKind::RateLimit), 0, 0, &p, &q),
            ContinueDecision::Continue {
                delay_seconds: 0,
                // The new account is a different login: the agent must be
                // respawned against its profile.
                needs_fresh_process: true,
                switch_account: true,
            }
        );
    }

    #[test]
    fn switch_with_nowhere_to_go_waits_rather_than_stopping() {
        let p = ContinuePolicy {
            on_rate_limit: RateLimitAction::Switch,
            ..Default::default()
        };
        let q = QuotaContext {
            seconds_until_reset: Some(600),
            alternative_available: false,
            exhausted: true,
        };
        assert_eq!(
            decide(TurnOutcome::Interrupted(InterruptKind::RateLimit), 0, 0, &p, &q),
            ContinueDecision::Continue {
                delay_seconds: 600 + RESET_MARGIN_SECONDS,
                needs_fresh_process: false,
                switch_account: false,
            }
        );
    }

    #[test]
    fn a_network_error_with_no_quota_left_is_treated_as_a_quota_wall() {
        // Retrying a network blip needs quota to spend. With the window empty,
        // a ten-second backoff would only reach the same wall.
        let p = ContinuePolicy::default();
        let q = QuotaContext {
            seconds_until_reset: Some(300),
            exhausted: true,
            ..Default::default()
        };
        assert_eq!(
            decide(TurnOutcome::Interrupted(InterruptKind::Network), 0, 0, &p, &q),
            ContinueDecision::Continue {
                delay_seconds: 300 + RESET_MARGIN_SECONDS,
                needs_fresh_process: false,
                switch_account: false,
            }
        );
    }

    #[test]
    fn a_network_error_with_quota_left_still_backs_off_normally() {
        let p = ContinuePolicy {
            base_delay_seconds: 10,
            ..Default::default()
        };
        let q = QuotaContext {
            seconds_until_reset: Some(300),
            exhausted: false,
            ..Default::default()
        };
        assert_eq!(
            decide(TurnOutcome::Interrupted(InterruptKind::Network), 0, 0, &p, &q),
            ContinueDecision::Continue {
                delay_seconds: 10,
                needs_fresh_process: false,
                switch_account: false,
            }
        );
    }

    #[test]
    fn rate_limit_uses_its_own_budget() {
        let p = ContinuePolicy {
            max_attempts: 0, // transient budget fully spent
            max_rate_limit_waits: 2,
            rate_limit_delay_seconds: 900,
            ..Default::default()
        };
        assert_eq!(
            decide(TurnOutcome::Interrupted(InterruptKind::RateLimit), 9, 0, &p, &QuotaContext::default()),
            ContinueDecision::Continue {
                delay_seconds: 900,
                needs_fresh_process: false,
                switch_account: false
            }
        );
        assert_eq!(
            decide(TurnOutcome::Interrupted(InterruptKind::RateLimit), 0, 2, &p, &QuotaContext::default()),
            ContinueDecision::Stop {
                cause: StopCause::RateLimitWaitsExhausted
            }
        );
    }

    #[test]
    fn adapter_crash_forces_a_fresh_process() {
        let p = ContinuePolicy::default();
        match decide(
            TurnOutcome::Interrupted(InterruptKind::AdapterCrash),
            0,
            0,
            &p,
            &QuotaContext::default(),
        ) {
            ContinueDecision::Continue {
                needs_fresh_process, ..
            } => assert!(needs_fresh_process, "a dead agent must be respawned"),
            other @ ContinueDecision::Stop { .. } => panic!("expected Continue, got {other:?}"),
        }
    }

    #[test]
    fn rate_limit_retry_reuses_the_process() {
        let p = ContinuePolicy {
            max_rate_limit_waits: 1,
            ..Default::default()
        };
        match decide(TurnOutcome::Interrupted(InterruptKind::RateLimit), 0, 0, &p, &QuotaContext::default()) {
            ContinueDecision::Continue {
                needs_fresh_process, ..
            } => assert!(!needs_fresh_process),
            other @ ContinueDecision::Stop { .. } => panic!("expected Continue, got {other:?}"),
        }
    }

    #[test]
    fn policy_clamp_rejects_nonsense() {
        let p = ContinuePolicy {
            max_attempts: 9999,
            base_delay_seconds: 0,
            max_delay_seconds: 1,
            rate_limit_delay_seconds: 1,
            max_rate_limit_waits: 9999,
            max_long_retries: 9999,
            long_retry_delay_seconds: 1,
            continue_message: "   ".into(),
            ..Default::default()
        }
        .clamp();
        assert_eq!(p.max_attempts, 100);
        assert_eq!(p.max_long_retries, 48);
        assert_eq!(p.long_retry_delay_seconds, 60);
        assert!((1..=24).contains(&p.max_wait_hours));
        assert_eq!(p.base_delay_seconds, 1);
        assert!(p.max_delay_seconds >= p.base_delay_seconds);
        assert_eq!(p.rate_limit_delay_seconds, 60);
        assert_eq!(p.max_rate_limit_waits, 48);
        assert_eq!(p.continue_message, DEFAULT_CONTINUE_MESSAGE);
    }

    #[test]
    fn decision_serialises_for_the_gui() {
        let d = ContinueDecision::Continue {
            delay_seconds: 20,
            needs_fresh_process: true,
            switch_account: true,
        };
        let json = serde_json::to_string(&d).unwrap();
        assert!(json.contains("\"action\":\"continue\""), "{json}");
        assert!(json.contains("\"delaySeconds\":20"), "{json}");
        assert!(json.contains("\"needsFreshProcess\":true"), "{json}");
        assert!(json.contains("\"switchAccount\":true"), "{json}");

        let s = serde_json::to_string(&ContinueDecision::Stop {
            cause: StopCause::NeedsAuth,
        })
        .unwrap();
        assert!(s.contains("\"action\":\"stop\""), "{s}");
        assert!(s.contains("\"cause\":\"needsAuth\""), "{s}");
    }
}
