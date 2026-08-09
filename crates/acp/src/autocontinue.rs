//! Auto-continue: keep a run alive across interruptions that are not the
//! model's decision to stop.
//!
//! # Why classification comes first
//!
//! "Send `continue` when Claude stops" is wrong, and dangerously so — most
//! stops are the model handing control back to a human. Only a subset of
//! outcomes deserve an automatic retry, and the right *response* differs per
//! class:
//!
//! | outcome | meaning | action |
//! |---|---|---|
//! | `end_turn` | model finished, awaiting the user | **stop** |
//! | `max_tokens` / `max_turn_requests` | work was cut off mid-flight | continue immediately |
//! | `refusal` / `cancelled` | deliberate halt | stop |
//! | `server_error`, transport death | network/backend blip | continue with backoff |
//! | usage limit reached | quota exhausted | wait for the window, then continue |
//! | `authentication_failed` | needs a human to re-login or fix a proxy | stop |
//!
//! The last row is not hypothetical: on a proxied network a missing
//! `HTTPS_PROXY` produces `403 "Request not allowed"` tagged
//! `authentication_failed`, and retrying it forever accomplishes nothing. See
//! [`crate::adapter`].
//!
//! # Where the signal comes from
//!
//! ACP makes all of this typed. A completed turn returns `stopReason`; a failed
//! one returns a JSON-RPC error whose `data.errorKind` uses the same taxonomy
//! Claude Code writes into its transcripts. No prose scraping, and no guessing
//! whether a silent terminal is thinking or dead.
//!
//! [`decide`] is pure and unit-tested; [`Runner`] is the part that actually
//! spawns processes.

use std::time::Duration;

use serde_json::Value;

use crate::adapter::AdapterConfig;
use crate::client::{AcpClient, AcpError, Handler};

pub use claude_switch_core::autocontinue::{
    backoff_seconds, classify_failure, classify_transport, decide, ContinueDecision,
    ContinuePolicy, InterruptKind, QuotaContext, RateLimitAction, StopCause, StopReason,
    TransportFailure, TurnOutcome, DEFAULT_CONTINUE_MESSAGE,
};

/// Classify the result of [`AcpClient::prompt`].
///
/// This is the only ACP-shaped part of the policy: it turns an [`AcpError`] into
/// the transport-agnostic inputs [`classify_failure`] and [`classify_transport`]
/// expect. Everything downstream lives in `claude-switch-core` so the GUI reaches
/// the same table over FFI.
#[must_use]
pub fn classify(result: &Result<Value, AcpError>) -> TurnOutcome {
    match result {
        Ok(value) => {
            let reason = value.get("stopReason").and_then(Value::as_str).unwrap_or("");
            TurnOutcome::Completed(StopReason::parse(reason))
        }
        Err(AcpError::Rpc(err)) => {
            TurnOutcome::Interrupted(classify_failure(err.error_kind(), &err.message))
        }
        Err(AcpError::Disconnected) => {
            TurnOutcome::Interrupted(classify_transport(TransportFailure::Disconnected))
        }
        Err(AcpError::Timeout(_)) => {
            TurnOutcome::Interrupted(classify_transport(TransportFailure::Timeout))
        }
        Err(_) => TurnOutcome::Interrupted(InterruptKind::Unknown),
    }
}

/// One turn as recorded by [`Runner`].
#[derive(Clone, Debug)]
pub struct TurnRecord {
    pub outcome: TurnOutcome,
    /// Agent-reported error text, when the turn failed.
    pub detail: Option<String>,
    pub waited_before: Duration,
}

/// What a whole auto-continued run did.
#[derive(Clone, Debug)]
pub struct RunReport {
    pub session_id: Option<String>,
    pub turns: Vec<TurnRecord>,
    pub stop_cause: StopCause,
    pub total_waited: Duration,
}

impl RunReport {
    /// Continuations that were issued (turns beyond the first).
    #[must_use]
    pub fn continuations(&self) -> usize {
        self.turns.len().saturating_sub(1)
    }
}

/// Drives one session, reconnecting and resuming when the adapter dies.
pub struct Runner {
    config: AdapterConfig,
    cwd: String,
    policy: ContinuePolicy,
    /// Permission mode applied to every session this runner opens.
    mode: Option<String>,
    turn_timeout: Duration,
    connect_timeout: Duration,
    client: Option<AcpClient>,
    session_id: Option<String>,
}

impl Runner {
    #[must_use]
    pub fn new(config: AdapterConfig, policy: ContinuePolicy) -> Self {
        let cwd = config.work_dir.to_string_lossy().into_owned();
        Self {
            config,
            cwd,
            policy,
            mode: None,
            turn_timeout: Duration::from_secs(30 * 60),
            connect_timeout: Duration::from_secs(120),
            client: None,
            session_id: None,
        }
    }

    /// Permission mode for launched sessions (`acceptEdits`, `bypassPermissions`, …).
    #[must_use]
    pub fn mode(mut self, mode: impl Into<String>) -> Self {
        self.mode = Some(mode.into());
        self
    }

    /// Resume an existing conversation instead of starting one.
    #[must_use]
    pub fn resume(mut self, session_id: impl Into<String>) -> Self {
        self.session_id = Some(session_id.into());
        self
    }

    #[must_use]
    pub fn turn_timeout(mut self, d: Duration) -> Self {
        self.turn_timeout = d;
        self
    }

    #[must_use]
    pub fn session_id(&self) -> Option<&str> {
        self.session_id.as_deref()
    }

    /// Connect (if needed) and make sure a session exists on this connection.
    ///
    /// After a crash the same `sessionId` is re-attached with `session/load`, so
    /// the continuation lands in the original conversation rather than forking
    /// a new one.
    fn ensure_ready(&mut self, handler: &mut dyn Handler) -> Result<String, AcpError> {
        if self.client.as_mut().is_some_and(AcpClient::exited) {
            self.client = None;
        }
        if self.client.is_none() {
            let client = AcpClient::connect(&self.config, self.connect_timeout)?;
            self.client = Some(client);

            let existing = self.session_id.clone();
            let client = self.client.as_mut().expect("just connected");
            if let Some(sid) = existing {
                if !client.supports_load_session() {
                    return Err(AcpError::Protocol(
                        "agent cannot resume: loadSession capability not advertised".into(),
                    ));
                }
                client.load_session(&sid, &self.cwd, handler, self.connect_timeout)?;
            } else {
                let info = client.new_session(&self.cwd, handler, self.connect_timeout)?;
                self.session_id = Some(info.session_id);
            }

            if let Some(mode) = self.mode.clone() {
                let sid = self.session_id.clone().expect("session established above");
                let client = self.client.as_mut().expect("connected");
                client.set_mode(&sid, &mode, handler, self.connect_timeout)?;
            }
        }
        self.session_id
            .clone()
            .ok_or_else(|| AcpError::Protocol("no session id after connect".into()))
    }

    /// Send `prompt`, then keep the run alive per [`ContinuePolicy`].
    pub fn run(&mut self, prompt: &str, handler: &mut dyn Handler) -> RunReport {
        let mut turns = Vec::new();
        let mut attempt = 0_u32;
        let mut rate_waits = 0_u32;
        let mut total_waited = Duration::ZERO;
        let mut waited_before = Duration::ZERO;
        let mut text = prompt.to_string();

        let stop_cause = loop {
            let session_id = match self.ensure_ready(handler) {
                Ok(id) => id,
                Err(e) => {
                    // Failing to connect is itself classifiable: a dead adapter
                    // is worth a retry, a bad credential is not.
                    let outcome = classify(&Err(e));
                    let detail = Some(format!("{outcome:?} while connecting"));
                    turns.push(TurnRecord { outcome, detail, waited_before });
                    match decide(outcome, attempt, rate_waits, &self.policy, &QuotaContext::default()) {
                        ContinueDecision::Continue { delay_seconds, .. } => {
                            let delay = Duration::from_secs(delay_seconds);
                            self.client = None;
                            attempt += 1;
                            total_waited += delay;
                            waited_before = delay;
                            std::thread::sleep(delay);
                            continue;
                        }
                        ContinueDecision::Stop { .. } => break StopCause::Failed,
                    }
                }
            };

            let client = self.client.as_mut().expect("ensure_ready leaves a client");
            let result = client.prompt(&session_id, &text, handler, self.turn_timeout);
            let outcome = classify(&result);
            let detail = match &result {
                Err(AcpError::Rpc(e)) => Some(e.message.clone()),
                Err(e) => Some(e.to_string()),
                Ok(_) => None,
            };
            turns.push(TurnRecord { outcome, detail, waited_before });

            match decide(outcome, attempt, rate_waits, &self.policy, &QuotaContext::default()) {
                ContinueDecision::Stop { cause } => break cause,
                ContinueDecision::Continue { delay_seconds, needs_fresh_process, .. } => {
                    if matches!(outcome, TurnOutcome::Interrupted(InterruptKind::RateLimit)) {
                        rate_waits += 1;
                    } else {
                        attempt += 1;
                    }
                    if needs_fresh_process {
                        self.client = None;
                    }
                    // Every retry resumes rather than repeating the original
                    // instruction, so partially-done work is not redone.
                    text = self.policy.continue_message.clone();
                    let delay = Duration::from_secs(delay_seconds);
                    total_waited += delay;
                    waited_before = delay;
                    if !delay.is_zero() {
                        std::thread::sleep(delay);
                    }
                }
            }
        };

        RunReport {
            session_id: self.session_id.clone(),
            turns,
            stop_cause,
            total_waited,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::client::RpcError;
    use serde_json::json;

    // The retry table itself is tested in `claude_switch_core::autocontinue`.
    // What belongs here is the ACP-specific half: turning an `AcpError` into the
    // transport-agnostic inputs that table expects.

    fn rpc(message: &str, kind: Option<&str>) -> Result<Value, AcpError> {
        Err(AcpError::Rpc(RpcError {
            code: -32603,
            message: message.into(),
            data: kind.map(|k| json!({ "errorKind": k })),
        }))
    }

    #[test]
    fn maps_stop_reason_from_the_prompt_result() {
        assert_eq!(
            classify(&Ok(json!({ "stopReason": "end_turn" }))),
            TurnOutcome::Completed(StopReason::EndTurn)
        );
        assert_eq!(
            classify(&Ok(json!({ "stopReason": "max_tokens" }))),
            TurnOutcome::Completed(StopReason::MaxTokens)
        );
    }

    #[test]
    fn a_result_without_a_stop_reason_is_not_a_finish() {
        // Defensive: an empty reason must not be read as `end_turn`.
        assert_eq!(
            classify(&Ok(json!({}))),
            TurnOutcome::Completed(StopReason::Other)
        );
    }

    #[test]
    fn lifts_error_kind_and_message_out_of_an_rpc_error() {
        // Verbatim from a probe run with no proxy configured.
        assert_eq!(
            classify(&rpc(
                "Internal error: Failed to authenticate. API Error: 403 Request not allowed",
                Some("authentication_failed"),
            )),
            TurnOutcome::Interrupted(InterruptKind::Auth)
        );
        assert_eq!(
            classify(&rpc("API Error: 529 Overloaded", Some("server_error"))),
            TurnOutcome::Interrupted(InterruptKind::Network)
        );
        assert_eq!(
            classify(&rpc("Claude AI usage limit reached", Some("server_error"))),
            TurnOutcome::Interrupted(InterruptKind::RateLimit)
        );
    }

    #[test]
    fn maps_transport_failures() {
        assert_eq!(
            classify(&Err(AcpError::Disconnected)),
            TurnOutcome::Interrupted(InterruptKind::AdapterCrash)
        );
        assert_eq!(
            classify(&Err(AcpError::Timeout(Duration::from_secs(1)))),
            TurnOutcome::Interrupted(InterruptKind::Timeout)
        );
    }

    #[test]
    fn other_client_errors_are_unknown_not_silent_success() {
        assert_eq!(
            classify(&Err(AcpError::Protocol("bad frame".into()))),
            TurnOutcome::Interrupted(InterruptKind::Unknown)
        );
    }
}
