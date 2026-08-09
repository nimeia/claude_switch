//! Minimal ACP client: NDJSON JSON-RPC 2.0 over the adapter's stdio.
//!
//! # Concurrency model
//!
//! One reader thread parses frames off the child's stdout and forwards them on
//! a channel; the caller's thread drives requests. This matters because ACP is
//! **bidirectional**: while a `session/prompt` is in flight the agent will send
//! us requests of its own (`session/request_permission`, `fs/read_text_file`),
//! and the prompt cannot complete until we answer them. So [`AcpClient::request`]
//! does not simply block on a response — it pumps inbound traffic and dispatches
//! it to a [`Handler`] until the response it is waiting for arrives.
//!
//! A second thread drains stderr, which would otherwise fill its pipe buffer and
//! wedge the adapter.
//!
//! # Frames observed in practice
//!
//! ```text
//! --> {"jsonrpc":"2.0","id":1,"method":"initialize","params":{...}}
//! <-- {"jsonrpc":"2.0","id":1,"result":{"protocolVersion":1,"agentCapabilities":{...}}}
//! <-- {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"…","update":{…}}}
//! <-- {"jsonrpc":"2.0","id":3,"error":{"code":-32603,"message":"Internal error: …",
//!                                      "data":{"errorKind":"authentication_failed"}}}
//! ```
//!
//! The `data.errorKind` field is the load-bearing one: it is the same taxonomy
//! Claude Code writes into its transcripts (`server_error`,
//! `authentication_failed`, …), so a turn's failure mode is typed rather than
//! guessed from prose.

use std::io::{BufRead, BufReader, Write};
use std::process::{Child, ChildStdin};
use std::sync::mpsc::{channel, Receiver, RecvTimeoutError, Sender};
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::adapter::{AdapterConfig, AdapterError};

/// ACP protocol version this client speaks.
pub const PROTOCOL_VERSION: u32 = 1;

#[derive(Debug)]
pub enum AcpError {
    Adapter(AdapterError),
    Io(std::io::Error),
    /// Agent answered with a JSON-RPC error object.
    Rpc(RpcError),
    /// Adapter exited or closed stdout before answering.
    Disconnected,
    Timeout(Duration),
    /// Well-formed JSON that did not match the shape we expect.
    Protocol(String),
}

impl std::fmt::Display for AcpError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Adapter(e) => write!(f, "{e}"),
            Self::Io(e) => write!(f, "io: {e}"),
            Self::Rpc(e) => write!(f, "agent error {}: {}", e.code, e.message),
            Self::Disconnected => write!(f, "adapter disconnected"),
            Self::Timeout(d) => write!(f, "timed out after {d:?}"),
            Self::Protocol(m) => write!(f, "protocol: {m}"),
        }
    }
}

impl std::error::Error for AcpError {}

impl From<AdapterError> for AcpError {
    fn from(e: AdapterError) -> Self {
        Self::Adapter(e)
    }
}
impl From<std::io::Error> for AcpError {
    fn from(e: std::io::Error) -> Self {
        Self::Io(e)
    }
}

/// JSON-RPC error object, with ACP's `data.errorKind` lifted out.
#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct RpcError {
    pub code: i64,
    pub message: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub data: Option<Value>,
}

impl RpcError {
    /// ACP's machine-readable failure class, e.g. `authentication_failed`.
    #[must_use]
    pub fn error_kind(&self) -> Option<&str> {
        self.data.as_ref()?.get("errorKind")?.as_str()
    }
}

/// What the client decides when the agent asks permission for a tool call.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum PermissionOutcome {
    /// Pick this option id from the offered list.
    Selected(String),
    /// Refuse the turn.
    Cancelled,
}

/// Callbacks the agent can invoke on us mid-turn.
///
/// The defaults are the safe ones: permission is **denied**, filesystem access
/// is refused. An unattended driver overrides `on_permission` deliberately.
pub trait Handler {
    /// Streaming progress: `session/update` notifications.
    fn on_update(&mut self, _session_id: &str, _update: &Value) {}

    /// Tool-call authorization. `options` is the agent-offered list, each with
    /// `optionId`, `name` and `kind` (`allow_once` / `allow_always` / …).
    fn on_permission(&mut self, _session_id: &str, _params: &Value) -> PermissionOutcome {
        PermissionOutcome::Cancelled
    }

    /// Honour `fs/read_text_file` only if you advertised the capability.
    fn on_read_text_file(&mut self, _path: &str) -> Result<String, String> {
        Err("fs/read_text_file not supported by this client".into())
    }

    fn on_write_text_file(&mut self, _path: &str, _content: &str) -> Result<(), String> {
        Err("fs/write_text_file not supported by this client".into())
    }

    /// Anything the adapter writes to stderr (adapter-level diagnostics).
    fn on_stderr(&mut self, _line: &str) {}
}

/// A no-op handler that denies everything; useful for capability probes.
#[derive(Default)]
pub struct DenyAll;
impl Handler for DenyAll {}

/// Result of `session/new`.
#[derive(Clone, Debug)]
pub struct SessionInfo {
    pub session_id: String,
    /// Permission mode ids the agent offers (`default`, `acceptEdits`,
    /// `bypassPermissions`, `plan`, `dontAsk`, `auto`).
    pub available_modes: Vec<String>,
    pub current_mode: Option<String>,
    /// Raw result, for fields this struct does not model.
    pub raw: Value,
}

/// Frames arriving from the agent.
enum Incoming {
    Response { id: u64, result: Result<Value, RpcError> },
    Request { id: Value, method: String, params: Value },
    Notification { method: String, params: Value },
    /// Reader thread ended: stdout closed.
    Eof,
}

/// A live ACP connection to one adapter process.
pub struct AcpClient {
    child: Child,
    stdin: ChildStdin,
    rx: Receiver<Incoming>,
    stderr_rx: Receiver<String>,
    next_id: u64,
    /// Responses that arrived while we were waiting for a different id.
    stash: Vec<(u64, Result<Value, RpcError>)>,
    pub agent_capabilities: Value,
    pub agent_info: Value,
}

impl AcpClient {
    /// Spawn the adapter and complete the `initialize` handshake.
    pub fn connect(config: &AdapterConfig, timeout: Duration) -> Result<Self, AcpError> {
        let mut child = config.to_command()?.spawn().map_err(AdapterError::Spawn)?;

        let stdin = child.stdin.take().ok_or(AcpError::Disconnected)?;
        let stdout = child.stdout.take().ok_or(AcpError::Disconnected)?;
        let stderr = child.stderr.take().ok_or(AcpError::Disconnected)?;

        let (tx, rx) = channel();
        std::thread::spawn(move || read_frames(stdout, &tx));

        let (etx, stderr_rx) = channel();
        std::thread::spawn(move || {
            for line in BufReader::new(stderr).lines().map_while(Result::ok) {
                if etx.send(line).is_err() {
                    break;
                }
            }
        });

        let mut client = Self {
            child,
            stdin,
            rx,
            stderr_rx,
            next_id: 1,
            stash: Vec::new(),
            agent_capabilities: Value::Null,
            agent_info: Value::Null,
        };

        let init = client.request(
            "initialize",
            &json!({
                "protocolVersion": PROTOCOL_VERSION,
                "clientCapabilities": {
                    "fs": { "readTextFile": true, "writeTextFile": true },
                    "terminal": false
                }
            }),
            &mut DenyAll,
            timeout,
        )?;
        client.agent_capabilities = init.get("agentCapabilities").cloned().unwrap_or(Value::Null);
        client.agent_info = init.get("agentInfo").cloned().unwrap_or(Value::Null);
        Ok(client)
    }

    /// Whether the agent advertised `loadSession` — required to resume.
    #[must_use]
    pub fn supports_load_session(&self) -> bool {
        self.agent_capabilities
            .get("loadSession")
            .and_then(Value::as_bool)
            .unwrap_or(false)
    }

    /// Auth methods the agent requires. Empty means ambient auth (the OAuth
    /// subscription in `CLAUDE_CONFIG_DIR`) is already sufficient.
    #[must_use]
    pub fn agent_name(&self) -> String {
        self.agent_info
            .get("name")
            .and_then(Value::as_str)
            .unwrap_or("unknown")
            .to_string()
    }

    pub fn new_session(
        &mut self,
        cwd: &str,
        handler: &mut dyn Handler,
        timeout: Duration,
    ) -> Result<SessionInfo, AcpError> {
        let res = self.request(
            "session/new",
            &json!({ "cwd": cwd, "mcpServers": [] }),
            handler,
            timeout,
        )?;
        let session_id = res
            .get("sessionId")
            .and_then(Value::as_str)
            .ok_or_else(|| AcpError::Protocol("session/new returned no sessionId".into()))?
            .to_string();
        let modes = res.get("modes");
        Ok(SessionInfo {
            session_id,
            available_modes: modes
                .and_then(|m| m.get("availableModes"))
                .and_then(Value::as_array)
                .map(|a| {
                    a.iter()
                        .filter_map(|m| m.get("id").and_then(Value::as_str).map(String::from))
                        .collect()
                })
                .unwrap_or_default(),
            current_mode: modes
                .and_then(|m| m.get("currentModeId"))
                .and_then(Value::as_str)
                .map(String::from),
            raw: res,
        })
    }

    /// Resume a previous conversation. Replays history via `session/update`
    /// before returning, so `handler` sees the prior turns.
    pub fn load_session(
        &mut self,
        session_id: &str,
        cwd: &str,
        handler: &mut dyn Handler,
        timeout: Duration,
    ) -> Result<(), AcpError> {
        self.request(
            "session/load",
            &json!({ "sessionId": session_id, "cwd": cwd, "mcpServers": [] }),
            handler,
            timeout,
        )?;
        Ok(())
    }

    /// Set the session's permission mode (`acceptEdits`, `bypassPermissions`, …).
    pub fn set_mode(
        &mut self,
        session_id: &str,
        mode_id: &str,
        handler: &mut dyn Handler,
        timeout: Duration,
    ) -> Result<(), AcpError> {
        self.request(
            "session/set_mode",
            &json!({ "sessionId": session_id, "modeId": mode_id }),
            handler,
            timeout,
        )?;
        Ok(())
    }

    /// Run one turn. The returned value carries `stopReason` and `usage`.
    pub fn prompt(
        &mut self,
        session_id: &str,
        text: &str,
        handler: &mut dyn Handler,
        timeout: Duration,
    ) -> Result<Value, AcpError> {
        self.request(
            "session/prompt",
            &json!({
                "sessionId": session_id,
                "prompt": [{ "type": "text", "text": text }]
            }),
            handler,
            timeout,
        )
    }

    /// Interrupt an in-flight turn (notification; no response expected).
    pub fn cancel(&mut self, session_id: &str) -> Result<(), AcpError> {
        self.notify("session/cancel", &json!({ "sessionId": session_id }))
    }

    /// Send a request and pump inbound traffic until its response arrives.
    pub fn request(
        &mut self,
        method: &str,
        params: &Value,
        handler: &mut dyn Handler,
        timeout: Duration,
    ) -> Result<Value, AcpError> {
        let id = self.next_id;
        self.next_id += 1;

        // A response for this id may already be stashed (cannot happen with
        // sequential use, but keeps the invariant honest).
        if let Some(pos) = self.stash.iter().position(|(sid, _)| *sid == id) {
            let (_, res) = self.stash.remove(pos);
            return res.map_err(AcpError::Rpc);
        }

        self.write_frame(&json!({
            "jsonrpc": "2.0", "id": id, "method": method, "params": params
        }))?;

        let deadline = Instant::now() + timeout;
        loop {
            self.drain_stderr(handler);

            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Err(AcpError::Timeout(timeout));
            }
            // Wake up regularly so stderr keeps draining during long turns.
            let slice = remaining.min(Duration::from_millis(250));

            match self.rx.recv_timeout(slice) {
                Ok(Incoming::Response { id: got, result }) => {
                    if got == id {
                        return result.map_err(AcpError::Rpc);
                    }
                    self.stash.push((got, result));
                }
                Ok(Incoming::Request { id: req_id, method, params }) => {
                    self.serve(&req_id, &method, &params, handler)?;
                }
                Ok(Incoming::Notification { method, params }) => {
                    if method == "session/update" {
                        let sid = params.get("sessionId").and_then(Value::as_str).unwrap_or("");
                        if let Some(update) = params.get("update") {
                            handler.on_update(sid, update);
                        }
                    }
                }
                Ok(Incoming::Eof) | Err(RecvTimeoutError::Disconnected) => {
                    return Err(AcpError::Disconnected)
                }
                Err(RecvTimeoutError::Timeout) => {}
            }
        }
    }

    fn notify(&mut self, method: &str, params: &Value) -> Result<(), AcpError> {
        self.write_frame(&json!({ "jsonrpc": "2.0", "method": method, "params": params }))
    }

    /// Answer an agent→client request.
    fn serve(
        &mut self,
        id: &Value,
        method: &str,
        params: &Value,
        handler: &mut dyn Handler,
    ) -> Result<(), AcpError> {
        let reply = match method {
            "session/request_permission" => {
                let sid = params.get("sessionId").and_then(Value::as_str).unwrap_or("");
                let outcome = match handler.on_permission(sid, params) {
                    PermissionOutcome::Selected(opt) => {
                        json!({ "outcome": { "outcome": "selected", "optionId": opt } })
                    }
                    PermissionOutcome::Cancelled => json!({ "outcome": { "outcome": "cancelled" } }),
                };
                Ok(outcome)
            }
            "fs/read_text_file" => {
                let path = params.get("path").and_then(Value::as_str).unwrap_or("");
                handler
                    .on_read_text_file(path)
                    .map(|content| json!({ "content": content }))
            }
            "fs/write_text_file" => {
                let path = params.get("path").and_then(Value::as_str).unwrap_or("");
                let content = params.get("content").and_then(Value::as_str).unwrap_or("");
                handler.on_write_text_file(path, content).map(|()| json!({}))
            }
            other => Err(format!("method not supported by this client: {other}")),
        };

        let frame = match reply {
            Ok(result) => json!({ "jsonrpc": "2.0", "id": id, "result": result }),
            Err(message) => json!({
                "jsonrpc": "2.0", "id": id,
                "error": { "code": -32601, "message": message }
            }),
        };
        self.write_frame(&frame)
    }

    fn write_frame(&mut self, frame: &Value) -> Result<(), AcpError> {
        let mut line = serde_json::to_vec(frame).map_err(|e| AcpError::Protocol(e.to_string()))?;
        line.push(b'\n');
        self.stdin.write_all(&line)?;
        self.stdin.flush()?;
        Ok(())
    }

    fn drain_stderr(&mut self, handler: &mut dyn Handler) {
        while let Ok(line) = self.stderr_rx.try_recv() {
            handler.on_stderr(&line);
        }
    }

    /// Whether the adapter process has exited.
    pub fn exited(&mut self) -> bool {
        matches!(self.child.try_wait(), Ok(Some(_)))
    }
}

impl Drop for AcpClient {
    fn drop(&mut self) {
        // Closing stdin asks the adapter to exit; kill if it does not.
        let _ = self.stdin.flush();
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn read_frames(stdout: std::process::ChildStdout, tx: &Sender<Incoming>) {
    for line in BufReader::new(stdout).lines().map_while(Result::ok) {
        if line.trim().is_empty() {
            continue;
        }
        let Ok(msg) = serde_json::from_str::<Value>(&line) else {
            continue; // adapter occasionally prints non-JSON noise
        };

        let has_method = msg.get("method").and_then(Value::as_str).is_some();
        let id = msg.get("id");

        let frame = match (id, has_method) {
            // Response to one of our requests.
            (Some(id), false) => {
                let Some(id) = id.as_u64() else { continue };
                let result = if let Some(err) = msg.get("error") {
                    Err(serde_json::from_value::<RpcError>(err.clone()).unwrap_or(RpcError {
                        code: -1,
                        message: err.to_string(),
                        data: None,
                    }))
                } else {
                    Ok(msg.get("result").cloned().unwrap_or(Value::Null))
                };
                Incoming::Response { id, result }
            }
            // Request from the agent.
            (Some(id), true) => Incoming::Request {
                id: id.clone(),
                method: msg["method"].as_str().unwrap_or_default().to_string(),
                params: msg.get("params").cloned().unwrap_or(Value::Null),
            },
            // Notification.
            (None, true) => Incoming::Notification {
                method: msg["method"].as_str().unwrap_or_default().to_string(),
                params: msg.get("params").cloned().unwrap_or(Value::Null),
            },
            (None, false) => continue,
        };

        if tx.send(frame).is_err() {
            return;
        }
    }
    let _ = tx.send(Incoming::Eof);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rpc_error_lifts_error_kind() {
        let e = RpcError {
            code: -32603,
            message: "Internal error: Failed to authenticate. API Error: 403 Request not allowed"
                .into(),
            data: Some(json!({ "errorKind": "authentication_failed" })),
        };
        assert_eq!(e.error_kind(), Some("authentication_failed"));
    }

    #[test]
    fn rpc_error_without_data_has_no_kind() {
        let e = RpcError { code: -1, message: "boom".into(), data: None };
        assert_eq!(e.error_kind(), None);
    }

    #[test]
    fn default_handler_denies_permission_and_fs() {
        let mut h = DenyAll;
        assert_eq!(
            h.on_permission("s", &json!({})),
            PermissionOutcome::Cancelled
        );
        assert!(h.on_read_text_file("/etc/passwd").is_err());
        assert!(h.on_write_text_file("/tmp/x", "data").is_err());
    }
}
