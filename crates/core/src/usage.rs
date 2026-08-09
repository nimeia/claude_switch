//! Usage windows + injectable HTTP usage fetch (ref: oauth / `poll_policy` / `usage_store`).

use std::collections::HashMap;
use std::sync::Arc;
use std::time::{Duration, Instant};

use parking_lot::Mutex;
use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::errors::{Error, Result};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UsageWindow {
    pub pct: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub resets_at: Option<String>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Usage {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub five_hour: Option<UsageWindow>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub seven_day: Option<UsageWindow>,
}

/// Why a slot has no usage numbers — a blank cell that cannot say why is a bug
/// report waiting to happen (ref: `json_output.py` usage sentinels).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum UsageStatus {
    /// Numbers are present and current.
    Ok,
    /// Never fetched yet this session.
    #[default]
    Unknown,
    /// Slot holds no stored credential at all.
    NoCredential,
    /// Credential is unusable and cannot be repaired: tokens wiped by a logout,
    /// or the refresh grant was rejected. Only a re-login fixes it.
    NeedsLogin,
    /// Managed `sk-ant-api…` key — no subscription quota exists to report.
    ApiKey,
    /// The fetch succeeded but the account has no subscription windows: the
    /// subscription lapsed, or there never was one. Not distinguishable from the
    /// response, so both share one status rather than guessing.
    NoSubscription,
    /// Fetch (or refresh) failed for a reason that may clear up: network down,
    /// per-token request budget hit, server error.
    Unavailable,
}

impl UsageStatus {
    /// Whether the shell should render numbers rather than a reason.
    #[must_use]
    pub const fn has_numbers(self) -> bool {
        matches!(self, Self::Ok)
    }

    /// Whether only the user can fix this — no amount of retrying will.
    ///
    /// Drives failover: an active account in one of these states is broken *now*,
    /// so waiting out `unhealthy_ticks` just prolongs a session that cannot work.
    #[must_use]
    pub const fn is_permanent(self) -> bool {
        matches!(
            self,
            Self::NeedsLogin | Self::NoCredential | Self::NoSubscription
        )
    }

    /// Account may join 5h-window warmup stagger (N) and receive fires.
    ///
    /// Requires a successful usage fetch that returned subscription windows —
    /// no sub, dead login, API key, or unknown/transient faults are excluded
    /// so N is not inflated by accounts that cannot actually open a bucket.
    #[must_use]
    pub const fn is_warmup_ready(self) -> bool {
        matches!(self, Self::Ok)
    }
}

impl Usage {
    /// Binding utilization = max of known windows (ref: autoswitch binding).
    #[must_use]
    pub fn binding_pct(&self) -> Option<f64> {
        let mut m = None;
        if let Some(w) = &self.five_hour {
            m = Some(m.map_or(w.pct, |x: f64| x.max(w.pct)));
        }
        if let Some(w) = &self.seven_day {
            m = Some(m.map_or(w.pct, |x: f64| x.max(w.pct)));
        }
        m
    }

    #[must_use]
    pub fn headroom_pct(&self) -> Option<f64> {
        self.binding_pct().map(|p| (100.0 - p).max(0.0))
    }
}

/// HTTP client abstraction (real or mock).
pub trait HttpClient: Send + Sync {
    fn get_json(&self, url: &str, headers: &[(&str, &str)]) -> Result<Value>;

    /// POST a JSON body (OAuth token grants).
    fn post_json(&self, url: &str, headers: &[(&str, &str)], body: &Value) -> Result<Value>;
}

/// Always-failing client (offline tests that assert network errors).
pub struct NoopHttp;

impl HttpClient for NoopHttp {
    fn get_json(&self, _url: &str, _headers: &[(&str, &str)]) -> Result<Value> {
        Err(Error::Network("no http client configured".into()))
    }

    fn post_json(&self, _url: &str, _headers: &[(&str, &str)], _body: &Value) -> Result<Value> {
        Err(Error::Network("no http client configured".into()))
    }
}

/// Production HTTP client (ureq, blocking).
pub struct UreqHttp {
    agent: ureq::Agent,
}

impl UreqHttp {
    #[must_use]
    pub fn new() -> Self {
        Self::for_host("api.anthropic.com")
    }

    /// Build an agent routed the way the machine routes `host`.
    ///
    /// ureq does not read proxy settings on its own. Skipping them is not a
    /// missing nicety: on a proxied network the direct request is answered with
    /// `403 "Request not allowed"`, which reads as a broken credential and is
    /// not one (Claude Code works there because it honors the same proxy).
    #[must_use]
    pub fn for_host(host: &str) -> Self {
        let mut builder = ureq::AgentBuilder::new()
            .timeout_connect(Duration::from_secs(5))
            .timeout_read(Duration::from_secs(10));
        if let Some(url) = crate::proxy::resolve_for(host) {
            // Unsupported scheme (e.g. socks without the feature): stay direct
            // rather than failing to construct a client at all.
            if let Ok(p) = ureq::Proxy::new(&url) {
                builder = builder.proxy(p);
            }
        }
        Self {
            agent: builder.build(),
        }
    }
}

impl Default for UreqHttp {
    fn default() -> Self {
        Self::new()
    }
}

impl HttpClient for UreqHttp {
    fn get_json(&self, url: &str, headers: &[(&str, &str)]) -> Result<Value> {
        let mut req = self.agent.get(url);
        for (k, v) in headers {
            req = req.set(k, v);
        }
        let resp = req
            .call()
            .map_err(|e| Error::Network(format!("usage GET failed: {e}")))?;
        let text = resp
            .into_string()
            .map_err(|e| Error::Network(format!("usage body: {e}")))?;
        serde_json::from_str(&text).map_err(|e| Error::Network(format!("usage JSON: {e}")))
    }

    fn post_json(&self, url: &str, headers: &[(&str, &str)], body: &Value) -> Result<Value> {
        let mut req = self.agent.post(url);
        for (k, v) in headers {
            req = req.set(k, v);
        }
        // The error body carries the grant rejection marker (`invalid_grant`),
        // which decides permanent-vs-transient — so it must survive into the
        // message rather than being flattened to a status code.
        // send_string, not send_json: ureq's json feature is off, and the
        // Content-Type header is already supplied by the caller.
        let payload = body.to_string();
        let resp = match req.send_string(&payload) {
            Ok(r) => r,
            Err(ureq::Error::Status(code, r)) => {
                let detail = r.into_string().unwrap_or_default();
                return Err(Error::Network(format!(
                    "token POST failed: {code} {}",
                    detail.chars().take(300).collect::<String>()
                )));
            }
            Err(e) => return Err(Error::Network(format!("token POST failed: {e}"))),
        };
        let text = resp
            .into_string()
            .map_err(|e| Error::Network(format!("token body: {e}")))?;
        serde_json::from_str(&text).map_err(|e| Error::Network(format!("token JSON: {e}")))
    }
}

/// Scripted mock: map access-token → usage JSON body.
#[derive(Default)]
pub struct MockHttp {
    pub by_token: Mutex<HashMap<String, Value>>,
    /// refresh-token → token-endpoint response.
    pub refresh_by_token: Mutex<HashMap<String, Value>>,
    /// refresh-token → error text (grant rejections).
    pub refresh_errors: Mutex<HashMap<String, String>>,
}

impl MockHttp {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Script a successful token grant for `refresh_token`.
    pub fn set_refresh(&self, refresh_token: &str, response: Value) {
        self.refresh_by_token
            .lock()
            .insert(refresh_token.to_string(), response);
    }

    /// Script a token-endpoint failure (e.g. `"400 invalid_grant"`).
    pub fn set_refresh_error(&self, refresh_token: &str, message: &str) {
        self.refresh_errors
            .lock()
            .insert(refresh_token.to_string(), message.to_string());
    }

    /// Fixture tokens used by GUI `--fixture` / `FfiSmoke` (tok-a … tok-e).
    #[must_use]
    pub fn with_demo_tokens() -> Self {
        let m = Self::new();
        // Distinct windows so every demo card shows full "NN%" (not "暂无").
        m.set_usage("tok-a", 25.0, 10.0);
        m.set_usage("tok-b", 80.0, 20.0);
        m.set_usage("tok-c", 45.0, 30.0);
        m.set_usage("tok-d", 12.0, 8.0);
        m.set_usage("tok-e", 60.0, 40.0);
        m
    }

    pub fn set_usage(&self, token: &str, five_hour: f64, seven_day: f64) {
        self.by_token.lock().insert(
            token.to_string(),
            serde_json::json!({
                "five_hour": { "utilization": five_hour, "resets_at": null },
                "seven_day": { "utilization": seven_day, "resets_at": null },
            }),
        );
    }
}

impl HttpClient for MockHttp {
    fn get_json(&self, url: &str, headers: &[(&str, &str)]) -> Result<Value> {
        if !url.contains("/api/oauth/usage") {
            return Err(Error::Network(format!("unexpected url {url}")));
        }
        let token = headers
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case("authorization"))
            .and_then(|(_, v)| v.strip_prefix("Bearer "))
            .unwrap_or("");
        self.by_token
            .lock()
            .get(token)
            .cloned()
            .ok_or_else(|| Error::Network("no mock usage for token".into()))
    }

    fn post_json(&self, url: &str, _headers: &[(&str, &str)], body: &Value) -> Result<Value> {
        if !url.contains("/oauth/token") {
            return Err(Error::Network(format!("unexpected url {url}")));
        }
        let refresh = body
            .get("refresh_token")
            .and_then(Value::as_str)
            .unwrap_or_default();
        if let Some(msg) = self.refresh_errors.lock().get(refresh) {
            return Err(Error::Network(msg.clone()));
        }
        self.refresh_by_token
            .lock()
            .get(refresh)
            .cloned()
            .ok_or_else(|| Error::Network("no mock refresh for token".into()))
    }
}

/// Parse Anthropic usage payload into [`Usage`].
#[must_use]
pub fn parse_usage_payload(v: &Value) -> Usage {
    fn win(obj: Option<&Value>) -> Option<UsageWindow> {
        let o = obj?;
        let pct = o
            .get("utilization")
            .or_else(|| o.get("pct"))
            .and_then(serde_json::Value::as_f64)
            .unwrap_or(0.0);
        let resets_at = o
            .get("resets_at")
            .or_else(|| o.get("resetsAt"))
            .and_then(|x| x.as_str())
            .map(str::to_string);
        Some(UsageWindow { pct, resets_at })
    }
    Usage {
        five_hour: win(v.get("five_hour")).or_else(|| win(v.get("fiveHour"))),
        seven_day: win(v.get("seven_day")).or_else(|| win(v.get("sevenDay"))),
    }
}

pub fn access_token_from_cred(cred: &str) -> Option<String> {
    let v: Value = serde_json::from_str(cred).ok()?;
    v.pointer("/claudeAiOauth/accessToken")
        .and_then(|x| x.as_str())
        .map(str::to_string)
}

/// Fetch usage for an OAuth credential JSON blob.
pub fn fetch_usage(http: &dyn HttpClient, credentials: &str) -> Result<Usage> {
    if crate::credentials::looks_like_api_key(credentials) {
        return Ok(Usage::default());
    }
    let token = access_token_from_cred(credentials)
        .ok_or_else(|| Error::Credential("no accessToken in credential".into()))?;
    let body = http.get_json(
        "https://api.anthropic.com/api/oauth/usage",
        &[
            ("Authorization", &format!("Bearer {token}")),
            ("anthropic-beta", "oauth-2025-04-20"),
            ("User-Agent", "claude-switch/0.1.0"),
        ],
    )?;
    Ok(parse_usage_payload(&body))
}

/// Adaptive poll cadence helpers (subset of `poll_policy.py`).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PollPlan {
    pub next_after: Duration,
}

/// Plan the next usage refresh interval from binding utilization and threshold.
///
/// Near/at threshold → 30s; within 20pp of threshold → 60s; otherwise → 180s.
#[must_use]
pub fn plan_after_fetch(binding_pct: Option<f64>, near_threshold: f64) -> PollPlan {
    let pct = binding_pct.unwrap_or(0.0);
    let secs = if pct >= near_threshold {
        30.0
    } else if pct >= near_threshold - 20.0 {
        60.0
    } else {
        180.0
    };
    PollPlan {
        next_after: Duration::from_secs_f64(secs),
    }
}

/// In-memory usage cache per account slot.
#[derive(Default)]
pub struct UsageCache {
    inner: Mutex<HashMap<u32, (Usage, UsageStatus, Instant)>>,
}

impl UsageCache {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    pub fn put(&self, num: u32, usage: Usage, status: UsageStatus) {
        self.inner
            .lock()
            .insert(num, (usage, status, Instant::now()));
    }

    /// Numbers for a slot — `None` unless the last fetch actually produced them,
    /// so a stale or failed read can never be mistaken for real utilization.
    pub fn get(&self, num: u32) -> Option<Usage> {
        self.inner
            .lock()
            .get(&num)
            .filter(|(_, s, _)| s.has_numbers())
            .map(|(u, _, _)| u.clone())
    }

    pub fn status(&self, num: u32) -> UsageStatus {
        self.inner
            .lock()
            .get(&num)
            .map_or(UsageStatus::Unknown, |(_, s, _)| *s)
    }

    pub fn age(&self, num: u32) -> Option<Duration> {
        self.inner.lock().get(&num).map(|(_, _, t)| t.elapsed())
    }
}

pub type SharedHttp = Arc<dyn HttpClient>;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mock_fetch_usage() {
        let http = MockHttp::new();
        http.set_usage("tok1", 25.0, 10.0);
        let cred = r#"{"claudeAiOauth":{"accessToken":"tok1","refreshToken":"r"}}"#;
        let u = fetch_usage(&http, cred).unwrap();
        assert!((u.five_hour.as_ref().unwrap().pct - 25.0).abs() < f64::EPSILON);
        assert!((u.headroom_pct().unwrap() - 75.0).abs() < f64::EPSILON);
    }

    #[test]
    fn plan_after_fetch_adaptive_bands() {
        // Threshold 90: high → 30s, mid (70+) → 60s, low → 180s.
        assert_eq!(
            plan_after_fetch(Some(95.0), 90.0).next_after,
            Duration::from_secs(30)
        );
        assert_eq!(
            plan_after_fetch(Some(75.0), 90.0).next_after,
            Duration::from_secs(60)
        );
        assert_eq!(
            plan_after_fetch(Some(10.0), 90.0).next_after,
            Duration::from_secs(180)
        );
        assert_eq!(
            plan_after_fetch(None, 90.0).next_after,
            Duration::from_secs(180)
        );
    }

    #[test]
    fn demo_tokens_resolve() {
        let http = MockHttp::with_demo_tokens();
        let a = fetch_usage(
            &http,
            r#"{"claudeAiOauth":{"accessToken":"tok-a","refreshToken":"r"}}"#,
        )
        .unwrap();
        assert!((a.five_hour.as_ref().unwrap().pct - 25.0).abs() < f64::EPSILON);
        let b = fetch_usage(
            &http,
            r#"{"claudeAiOauth":{"accessToken":"tok-b","refreshToken":"r"}}"#,
        )
        .unwrap();
        assert!((b.five_hour.as_ref().unwrap().pct - 80.0).abs() < f64::EPSILON);
    }
}
