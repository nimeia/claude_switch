//! ACP (Agent Client Protocol) client for driving Claude Code as a managed agent.
//!
//! # Why this exists
//!
//! claude-switch can already *watch* Claude Code sessions from the outside
//! (transcripts, `~/.claude/sessions/<pid>.json`, usage polls), but it cannot
//! *steer* them: an interactive TUI in Windows Terminal has no control channel.
//! Auto-continue — noticing that a run died on a network blip and resuming it —
//! needs one.
//!
//! ACP provides it. The agent runs as a subprocess speaking newline-delimited
//! JSON-RPC 2.0 over stdio, so a turn's outcome arrives as a typed
//! `stopReason` or a JSON-RPC error with an `errorKind`, rather than as text
//! scraped out of a transcript. Permission prompts arrive as
//! `session/request_permission` requests we answer programmatically, which is
//! what makes unattended continuation safe to attempt at all.
//!
//! # Scope
//!
//! This crate owns sessions it starts. It cannot attach to a `claude` TUI the
//! user launched themselves — ACP has no such notion, an agent is always a
//! subprocess of its client. Watching user-launched terminals stays the job of
//! the transcript/session-registry scanners in `claude-switch-core`.
//!
//! # Hand-rolled protocol
//!
//! The official `agent-client-protocol` crate is async (tokio) and moves fast.
//! `claude-switch-core` deliberately has no async runtime, and the wire format
//! here is small enough that hand-rolling keeps the dependency surface at
//! `serde_json`. [`client`] is the only module that would change if we later
//! swap in the official SDK.
//!
//! # Verified against
//!
//! `@agentclientprotocol/claude-agent-acp` 0.65.0 (bundling
//! `@agentclientprotocol/sdk` 1.3.0 and `@anthropic-ai/claude-agent-sdk`
//! 0.3.220), protocol version 1, Claude Code 2.1.223.

pub mod adapter;
pub mod autocontinue;
pub mod client;

pub use adapter::{AdapterConfig, AdapterError};
pub use autocontinue::{
    classify, ContinuePolicy, ContinueDecision, InterruptKind, StopReason, TurnOutcome,
};
pub use client::{AcpClient, AcpError, Handler, PermissionOutcome, SessionInfo};
