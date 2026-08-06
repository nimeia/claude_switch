//! OAuth token expiry + refresh-token grant (ref: `oauth.py`).
//!
//! Claude Code refreshes only the credential it is *currently* logged in with.
//! Every other managed slot keeps whatever access token it had when it was last
//! live — those expire in hours, after which its usage can no longer be fetched.
//! Refreshing them here is what keeps the account list showing real numbers
//! instead of blanks.

use serde_json::{json, Value};

use crate::usage::HttpClient;

pub const OAUTH_TOKEN_URL: &str = "https://platform.claude.com/v1/oauth/token";
pub const OAUTH_CLIENT_ID: &str = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

/// Treat a token expiring within this window as already expired, so a refresh
/// happens before the fetch rather than after a 401 (ref: `oauth.py`).
pub const EXPIRY_BUFFER_MS: i64 = 5 * 60 * 1000;

/// Why a refresh could not produce a usable credential.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum RefreshError {
    /// No usable refresh token stored — only a re-login can fix this.
    NoRefreshToken,
    /// The server rejected the grant: this lineage is dead, stop retrying.
    InvalidGrant,
    /// Network/server blip — the stored token may still be fine, retry later.
    Transient(String),
}

impl RefreshError {
    /// Whether retrying later could plausibly succeed.
    #[must_use]
    pub const fn retryable(&self) -> bool {
        matches!(self, Self::Transient(_))
    }
}

fn oauth_obj(credentials: &str) -> Option<Value> {
    let v: Value = serde_json::from_str(credentials).ok()?;
    v.get("claudeAiOauth").cloned()
}

/// Non-empty string at `key` inside `claudeAiOauth`.
///
/// Emptiness matters: Claude Code blanks `accessToken`/`refreshToken` in place
/// on logout rather than deleting the file, so `""` is a real, common value.
fn oauth_str(credentials: &str, key: &str) -> Option<String> {
    let s = oauth_obj(credentials)?
        .get(key)?
        .as_str()
        .unwrap_or_default()
        .to_string();
    if s.is_empty() {
        None
    } else {
        Some(s)
    }
}

#[must_use]
pub fn access_token(credentials: &str) -> Option<String> {
    oauth_str(credentials, "accessToken")
}

#[must_use]
pub fn refresh_token(credentials: &str) -> Option<String> {
    oauth_str(credentials, "refreshToken")
}

#[must_use]
pub fn expires_at_ms(credentials: &str) -> Option<i64> {
    oauth_obj(credentials)?.get("expiresAt")?.as_i64()
}

#[must_use]
pub fn now_ms() -> i64 {
    chrono::Utc::now().timestamp_millis()
}

/// Whether the access token is expired (or about to be) at `now_ms`.
///
/// A credential with no `expiresAt` is *not* assumed expired — older backups
/// simply never recorded one, and a needless refresh rotates a live token.
#[must_use]
pub fn is_expired(expires_at: Option<i64>, now: i64) -> bool {
    expires_at.is_some_and(|e| now + EXPIRY_BUFFER_MS >= e)
}

/// Whether this credential needs a refresh before its usage can be fetched.
#[must_use]
pub fn needs_refresh(credentials: &str, now: i64) -> bool {
    access_token(credentials).is_none() || is_expired(expires_at_ms(credentials), now)
}

/// Exchange the stored refresh token for a fresh access token.
///
/// Returns the **full rotated credential JSON** — the server may hand back a new
/// refresh token, and losing it would strand the account. Callers must persist
/// the returned value before relying on it.
pub fn try_refresh(
    http: &dyn HttpClient,
    credentials: &str,
) -> std::result::Result<String, RefreshError> {
    let Some(refresh) = refresh_token(credentials) else {
        return Err(RefreshError::NoRefreshToken);
    };
    let body = json!({
        "grant_type": "refresh_token",
        "refresh_token": refresh,
        "client_id": OAUTH_CLIENT_ID,
    });

    let resp = http
        .post_json(
            OAUTH_TOKEN_URL,
            &[
                ("Content-Type", "application/json"),
                ("User-Agent", "claude-switch/0.1.0"),
            ],
            &body,
        )
        .map_err(|e| classify_refresh_error(&e))?;

    let access = resp
        .get("access_token")
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
        .ok_or_else(|| RefreshError::Transient("token response had no access_token".into()))?;

    let mut root: Value =
        serde_json::from_str(credentials).map_err(|_| RefreshError::NoRefreshToken)?;
    let obj = root
        .get_mut("claudeAiOauth")
        .and_then(Value::as_object_mut)
        .ok_or(RefreshError::NoRefreshToken)?;

    obj.insert("accessToken".into(), json!(access));
    if let Some(expires_in) = resp.get("expires_in").and_then(Value::as_i64) {
        obj.insert("expiresAt".into(), json!(now_ms() + expires_in * 1000));
    }
    // Rotated refresh tokens must replace the old one or the next refresh fails.
    if let Some(rt) = resp
        .get("refresh_token")
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
    {
        obj.insert("refreshToken".into(), json!(rt));
    }
    if let Some(scope) = resp.get("scope").and_then(Value::as_str) {
        let scopes: Vec<&str> = scope.split_whitespace().collect();
        if !scopes.is_empty() {
            obj.insert("scopes".into(), json!(scopes));
        }
    }

    Ok(root.to_string())
}

/// A rejected grant is permanent; everything else is worth retrying.
///
/// Only an explicit rejection marker counts as permanent: misreading a transient
/// failure as permanent would wrongly declare a working account dead.
fn classify_refresh_error(e: &crate::errors::Error) -> RefreshError {
    let msg = e.message();
    if msg.contains("invalid_grant") || msg.contains("invalid_client") {
        RefreshError::InvalidGrant
    } else {
        RefreshError::Transient(msg)
    }
}

/// Convenience for callers that only need to know whether a slot is hopeless.
#[must_use]
pub fn is_wiped(credentials: &str) -> bool {
    access_token(credentials).is_none() && refresh_token(credentials).is_none()
}

/// Test double: scripted refresh responses keyed by refresh token.
#[cfg(test)]
pub(crate) fn refresh_response(access: &str, expires_in: i64, new_refresh: Option<&str>) -> Value {
    let mut v = json!({ "access_token": access, "expires_in": expires_in });
    if let Some(r) = new_refresh {
        v["refresh_token"] = json!(r);
    }
    v
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::usage::MockHttp;

    fn cred(access: &str, refresh: &str, expires_at: Option<i64>) -> String {
        let mut o = json!({ "accessToken": access, "refreshToken": refresh });
        if let Some(e) = expires_at {
            o["expiresAt"] = json!(e);
        }
        json!({ "claudeAiOauth": o }).to_string()
    }

    #[test]
    fn wiped_credential_has_no_tokens() {
        // The shape Claude Code leaves behind on logout.
        let wiped = cred("", "", Some(0));
        assert!(is_wiped(&wiped));
        assert_eq!(access_token(&wiped), None);
        assert_eq!(refresh_token(&wiped), None);
        assert!(needs_refresh(&wiped, now_ms()));
        // And a refresh cannot rescue it.
        let http = MockHttp::new();
        assert_eq!(
            try_refresh(&http, &wiped).unwrap_err(),
            RefreshError::NoRefreshToken
        );
    }

    #[test]
    fn expiry_uses_a_buffer_and_tolerates_absence() {
        let now = 1_000_000_000_000;
        assert!(is_expired(Some(now), now));
        assert!(is_expired(Some(now + EXPIRY_BUFFER_MS - 1), now));
        assert!(!is_expired(Some(now + EXPIRY_BUFFER_MS + 1), now));
        // No recorded expiry is not evidence of expiry.
        assert!(!is_expired(None, now));
        assert!(!needs_refresh(&cred("at", "rt", None), now));
    }

    #[test]
    fn refresh_rotates_access_and_refresh_tokens() {
        let http = MockHttp::new();
        http.set_refresh("rt-old", refresh_response("at-new", 3600, Some("rt-new")));

        let before = cred("at-old", "rt-old", Some(0));
        let after = try_refresh(&http, &before).unwrap();

        assert_eq!(access_token(&after).as_deref(), Some("at-new"));
        // Dropping the rotated refresh token would strand the account.
        assert_eq!(refresh_token(&after).as_deref(), Some("rt-new"));
        assert!(!needs_refresh(&after, now_ms()));
        assert!(expires_at_ms(&after).unwrap() > now_ms());
    }

    #[test]
    fn refresh_keeps_the_old_refresh_token_when_none_is_returned() {
        let http = MockHttp::new();
        http.set_refresh("rt-keep", refresh_response("at-new", 3600, None));
        let after = try_refresh(&http, &cred("at-old", "rt-keep", Some(0))).unwrap();
        assert_eq!(refresh_token(&after).as_deref(), Some("rt-keep"));
    }

    #[test]
    fn refresh_preserves_unrelated_credential_fields() {
        let http = MockHttp::new();
        http.set_refresh("rt", refresh_response("at-new", 3600, None));
        let before = json!({
            "claudeAiOauth": {
                "accessToken": "old",
                "refreshToken": "rt",
                "expiresAt": 0,
                "subscriptionType": "pro",
                "rateLimitTier": "default_claude_ai",
            },
            "mcpOAuth": { "keep": true },
        })
        .to_string();
        let after = try_refresh(&http, &before).unwrap();
        assert!(after.contains("\"subscriptionType\":\"pro\""));
        assert!(after.contains("\"keep\":true"));
    }

    #[test]
    fn rejected_grant_is_permanent_other_failures_are_not() {
        let http = MockHttp::new();
        // No scripted response → mock reports a plain failure.
        let err = try_refresh(&http, &cred("at", "rt-unknown", Some(0))).unwrap_err();
        assert!(err.retryable(), "unexpected: {err:?}");

        http.set_refresh_error("rt-dead", "400 invalid_grant");
        let err = try_refresh(&http, &cred("at", "rt-dead", Some(0))).unwrap_err();
        assert_eq!(err, RefreshError::InvalidGrant);
        assert!(!err.retryable());
    }
}
