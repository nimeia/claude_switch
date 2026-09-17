//! System/environment HTTP proxy discovery, plus the env Claude Code actually
//! honours.
//!
//! Claude Code itself goes through the machine's proxy, so an account that works
//! in `claude` must also work here — a direct connection can be refused outright
//! (Anthropic answers blocked networks with `403 "Request not allowed"`, which
//! looks exactly like an auth failure and is nothing of the sort).
//!
//! Resolution order for a **system** choice (account or app):
//! 1. This app's global proxy, when set (`settings.json` `proxy`)
//! 2. `HTTPS_PROXY` / `https_proxy`, then `ALL_PROXY` / `all_proxy`
//! 3. Windows Internet Settings (`ProxyEnable` + `ProxyServer`)
//!
//! `NO_PROXY` (and the Windows `ProxyOverride` list) short-circuit the
//! machine sources. An explicit URL (app or account) is used as given.
//!
//! Claude Code's HTTP stack reads only those environment variables (and
//! `settings.json` `env`), never `WinINET`. A child we spawn has to have them
//! set, and a daemon worker that does not inherit our environment still picks
//! them up from the profile's `settings.json`.

use std::collections::BTreeMap;
use std::path::Path;

use serde_json::{json, Map, Value};

use crate::errors::{Error, Result};
use crate::fsutil::atomic_write;

/// Host Claude Code talks to; used when resolving an account's proxy.
pub const ANTHROPIC_API_HOST: &str = "api.anthropic.com";

/// Wire value meaning "do not proxy".
pub const DIRECT: &str = "direct";

/// Keys Claude Code's HTTP stack reads (both casings: Bun on Windows is not
/// reliably case-insensitive the way Win32 is).
const PROXY_ENV_KEYS: &[&str] = &["HTTPS_PROXY", "HTTP_PROXY", "https_proxy", "http_proxy"];

const RESOLVES_HOSTS: &str = "CLAUDE_CODE_PROXY_RESOLVES_HOSTS";

/// Extra names to clear when the account is set to direct, so a parent
/// `HTTPS_PROXY` cannot leak into the child.
const DIRECT_SCRUB: &[&str] = &[
    "HTTPS_PROXY",
    "HTTP_PROXY",
    "https_proxy",
    "http_proxy",
    "ALL_PROXY",
    "all_proxy",
    "CLAUDE_CODE_PROXY_RESOLVES_HOSTS",
];

/// Proxy URL the machine would use for `host`, ignoring this app's setting.
#[must_use]
pub fn resolve_os(host: &str) -> Option<String> {
    if let Some(list) = env_any(&["NO_PROXY", "no_proxy"]) {
        if bypasses(&list, host) {
            return None;
        }
    }
    if let Some(p) = env_any(&["HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy"]) {
        return normalize(&p);
    }
    system_proxy(host)
}

/// Proxy URL to use for `host`, or `None` to connect directly.
///
/// Same as [`resolve_os`]: callers that should honour the app setting use
/// [`resolve_stored_in`] / [`resolve_app`].
#[must_use]
pub fn resolve_for(host: &str) -> Option<String> {
    resolve_os(host)
}

/// App-wide stored proxy: `None` = machine, `"direct"` = none, else a URL.
#[must_use]
pub fn resolve_app(app: Option<&str>, host: &str) -> Option<String> {
    if is_direct(app) {
        return None;
    }
    match app
        .map(str::trim)
        .filter(|s| !s.is_empty() && !s.eq_ignore_ascii_case("system"))
    {
        None => resolve_os(host),
        Some(url) => normalize(url),
    }
}

/// Proxy env a child CLI needs to reach `host` the way this app does.
///
/// Claude Code reads `HTTPS_PROXY` / `HTTP_PROXY` from its environment and
/// never looks at Windows Internet Settings. So a child that inherits only
/// our environment connects directly whenever the proxy came from the
/// registry — and Anthropic answers a blocked network with
/// `403 "Request not allowed"`, which reads like an auth failure.
///
/// Empty when `HTTPS_PROXY` is already set (inherited as is), when the host
/// is bypassed, or when there is no proxy at all.
#[must_use]
pub fn child_env(host: &str) -> Vec<(&'static str, String)> {
    if env_any(&["HTTPS_PROXY", "https_proxy"]).is_some() {
        return Vec::new();
    }
    child_env_pairs(resolve_for(host).as_deref())
}

/// The pairs themselves, split out so the mapping is testable without
/// touching the process environment.
#[must_use]
pub fn child_env_pairs(url: Option<&str>) -> Vec<(&'static str, String)> {
    url.map(|u| {
        vec![
            ("HTTPS_PROXY", u.to_string()),
            ("HTTP_PROXY", u.to_string()),
            ("https_proxy", u.to_string()),
            ("http_proxy", u.to_string()),
            (RESOLVES_HOSTS, "1".to_string()),
        ]
    })
    .unwrap_or_default()
}

/// Normalise a stored proxy (account or app): `None`/`""`/`"system"` → inherit;
/// `"direct"` → no proxy; anything else must be an http(s) URL.
pub fn normalize_stored(raw: Option<&str>) -> Result<Option<String>> {
    let s = raw.map(str::trim).filter(|s| !s.is_empty());
    match s {
        None => Ok(None),
        Some(s) if s.eq_ignore_ascii_case("system") => Ok(None),
        Some(s) if s.eq_ignore_ascii_case(DIRECT) => Ok(Some(DIRECT.to_string())),
        Some(s) => {
            let n = normalize(s).ok_or_else(|| Error::Validation("proxy URL is empty".into()))?;
            let scheme_ok = n.starts_with("http://") || n.starts_with("https://");
            if !scheme_ok {
                return Err(Error::Validation(
                    "proxy must be an http(s) URL, 'direct', or empty for the system proxy".into(),
                ));
            }
            Ok(Some(n))
        }
    }
}

/// Whether the stored value means "do not proxy".
#[must_use]
pub fn is_direct(stored: Option<&str>) -> bool {
    stored.is_some_and(|s| s.trim().eq_ignore_ascii_case(DIRECT))
}

/// Resolve the URL a launch should use, if any.
///
/// Account `"system"` (absent) falls through to the machine. Prefer
/// [`resolve_stored_in`] when the app-wide proxy should sit in between.
#[must_use]
pub fn resolve_stored(stored: Option<&str>, host: &str) -> Option<String> {
    resolve_stored_in(stored, host, None)
}

/// Like [`resolve_stored`], with the app-wide proxy as the account's `"system"`.
#[must_use]
pub fn resolve_stored_in(stored: Option<&str>, host: &str, app: Option<&str>) -> Option<String> {
    if is_direct(stored) {
        return None;
    }
    match stored
        .map(str::trim)
        .filter(|s| !s.is_empty() && !s.eq_ignore_ascii_case("system"))
    {
        None => resolve_app(app, host),
        Some(url) => normalize(url),
    }
}

/// Env to set and names to scrub for a child Claude Code / adapter process.
#[must_use]
pub fn launch_env(
    stored: Option<&str>,
    host: &str,
) -> (BTreeMap<String, String>, Vec<String>, Option<String>) {
    launch_env_in(stored, host, None)
}

/// Like [`launch_env`], with the app-wide proxy as the account's `"system"`.
#[must_use]
pub fn launch_env_in(
    stored: Option<&str>,
    host: &str,
    app: Option<&str>,
) -> (BTreeMap<String, String>, Vec<String>, Option<String>) {
    if is_direct(stored) || (stored.is_none() && is_direct(app)) {
        return (
            BTreeMap::new(),
            DIRECT_SCRUB.iter().map(|s| (*s).to_string()).collect(),
            None,
        );
    }
    match resolve_stored_in(stored, host, app) {
        Some(url) => {
            let mut env = BTreeMap::new();
            for (k, v) in child_env_pairs(Some(&url)) {
                env.insert(k.to_string(), v);
            }
            (env, Vec::new(), Some(url))
        }
        None => (BTreeMap::new(), Vec::new(), None),
    }
}

/// Apply [`launch_env_in`] to a child process.
pub fn apply_to_command(
    cmd: &mut std::process::Command,
    stored: Option<&str>,
    host: &str,
    app: Option<&str>,
) {
    let (env, scrub, _) = launch_env_in(stored, host, app);
    for (k, v) in env {
        cmd.env(k, v);
    }
    for k in scrub {
        cmd.env_remove(k);
    }
}

/// Merge proxy keys into Claude Code's `settings.json` `env` object.
///
/// `url = Some` writes the four proxy vars plus `CLAUDE_CODE_PROXY_RESOLVES_HOSTS`.
/// `url = None` removes those keys so a previous account's proxy does not stick.
/// Other `env` entries and the rest of the file are left alone.
///
/// A missing file is created. A file that exists but is not JSON is left
/// untouched and reported, so we never smash a broken config.
pub fn apply_settings_env(settings_path: &Path, url: Option<&str>) -> Result<()> {
    let mut map = read_settings_object(settings_path)?;
    merge_proxy_env(&mut map, url);
    write_settings_object(settings_path, &map)
}

/// Proxy env plus `timeZone` / `language` (and `TZ` / `LANG` in `env`).
///
/// `timezone` / `language` of `None` removes those keys so a previous
/// account's region cannot stick on this profile.
pub fn apply_launch_settings(
    settings_path: &Path,
    proxy_url: Option<&str>,
    timezone: Option<&str>,
    language: Option<&str>,
) -> Result<()> {
    let mut map = read_settings_object(settings_path)?;
    merge_proxy_env(&mut map, proxy_url);
    crate::locale::merge_into_settings(&mut map, timezone, language);
    write_settings_object(settings_path, &map)
}

fn merge_proxy_env(map: &mut Map<String, Value>, url: Option<&str>) {
    let mut env = match map.get("env") {
        Some(Value::Object(m)) => m.clone(),
        _ => Map::new(),
    };
    if let Some(u) = url {
        for (k, v) in child_env_pairs(Some(u)) {
            env.insert(k.to_string(), json!(v));
        }
    } else {
        for k in PROXY_ENV_KEYS {
            env.remove(*k);
        }
        env.remove(RESOLVES_HOSTS);
    }
    if env.is_empty() {
        map.remove("env");
    } else {
        map.insert("env".into(), Value::Object(env));
    }
}

fn write_settings_object(path: &Path, map: &Map<String, Value>) -> Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(Error::Io)?;
    }
    let text = serde_json::to_string_pretty(&Value::Object(map.clone()))
        .map_err(|e| Error::Internal(format!("claude settings serialize: {e}")))?;
    atomic_write(path, text.as_bytes()).map_err(Error::Io)
}

fn read_settings_object(path: &Path) -> Result<Map<String, Value>> {
    let text = match std::fs::read_to_string(path) {
        Ok(t) => t,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(Map::new()),
        Err(e) => return Err(Error::Io(e)),
    };
    if text.trim().is_empty() {
        return Ok(Map::new());
    }
    match serde_json::from_str::<Value>(&text) {
        Ok(Value::Object(map)) => Ok(map),
        _ => Err(Error::Validation(format!(
            "{} is not valid JSON; it has been left untouched",
            path.display()
        ))),
    }
}

fn env_any(keys: &[&str]) -> Option<String> {
    keys.iter().find_map(|k| {
        std::env::var(k)
            .ok()
            .map(|v| v.trim().to_string())
            .filter(|v| !v.is_empty())
    })
}

/// Add a scheme when the value is a bare `host:port` (the Windows form).
#[must_use]
pub fn normalize(raw: &str) -> Option<String> {
    let s = raw.trim();
    if s.is_empty() {
        return None;
    }
    if s.contains("://") {
        Some(s.to_string())
    } else {
        Some(format!("http://{s}"))
    }
}

/// Whether `host` matches a `NO_PROXY`-style bypass list.
///
/// Entries are comma-separated and matched as suffixes (`.example.com` and
/// `example.com` both cover `api.example.com`), with `*` meaning "everything"
/// and a leading `*.` tolerated. Windows `ProxyOverride` also uses `<local>`
/// for dotless hosts and `192.168.*` style prefixes.
#[must_use]
pub fn bypasses(list: &str, host: &str) -> bool {
    let host = host.trim().trim_end_matches('.').to_ascii_lowercase();
    for raw in list.split([',', ';']) {
        let entry = raw.trim().to_ascii_lowercase();
        if entry.is_empty() {
            continue;
        }
        if entry == "*" {
            return true;
        }
        if entry == "<local>" {
            if !host.contains('.') {
                return true;
            }
            continue;
        }
        // Windows prefix wildcards: `192.168.*`, `127.*`
        if let Some(prefix) = entry.strip_suffix('*') {
            let prefix = prefix.trim_end_matches('.');
            if !prefix.is_empty() && (host == prefix || host.starts_with(&format!("{prefix}."))) {
                return true;
            }
            continue;
        }
        let entry = entry.trim_start_matches("*.").trim_start_matches('.');
        if host == entry || host.ends_with(&format!(".{entry}")) {
            return true;
        }
    }
    false
}

#[cfg(windows)]
fn system_proxy(host: &str) -> Option<String> {
    use winreg::enums::HKEY_CURRENT_USER;
    use winreg::RegKey;

    let key = RegKey::predef(HKEY_CURRENT_USER)
        .open_subkey(r"Software\Microsoft\Windows\CurrentVersion\Internet Settings")
        .ok()?;
    let enabled: u32 = key.get_value("ProxyEnable").unwrap_or(0);
    if enabled == 0 {
        return None;
    }
    if let Ok(over) = key.get_value::<String, _>("ProxyOverride") {
        if bypasses(&over, host) {
            return None;
        }
    }
    let server: String = key.get_value("ProxyServer").ok()?;
    normalize(&pick_protocol(&server, "https")?)
}

#[cfg(not(windows))]
fn system_proxy(_host: &str) -> Option<String> {
    None
}

/// Pick one endpoint out of a `ProxyServer` value.
///
/// The value is either a bare `host:port` used for every protocol, or a
/// `scheme=host:port;…` list. An https-specific entry wins; otherwise the http
/// one is used to tunnel (which is what browsers do).
#[must_use]
pub fn pick_protocol(server: &str, want: &str) -> Option<String> {
    let s = server.trim();
    if s.is_empty() {
        return None;
    }
    if !s.contains('=') {
        return Some(s.to_string());
    }
    let mut fallback = None;
    for part in s.split(';') {
        let (scheme, addr) = part.split_once('=')?;
        let (scheme, addr) = (scheme.trim(), addr.trim());
        if addr.is_empty() {
            continue;
        }
        if scheme.eq_ignore_ascii_case(want) {
            return Some(addr.to_string());
        }
        if scheme.eq_ignore_ascii_case("http") && fallback.is_none() {
            fallback = Some(addr.to_string());
        }
    }
    fallback
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bare_host_port_gets_a_scheme() {
        assert_eq!(
            normalize("192.168.1.54:7897").as_deref(),
            Some("http://192.168.1.54:7897")
        );
        assert_eq!(normalize("http://p:8080").as_deref(), Some("http://p:8080"));
        assert_eq!(normalize("  ").as_deref(), None);
    }

    #[test]
    fn protocol_list_prefers_https_then_http() {
        assert_eq!(
            pick_protocol("http=a:1;https=b:2", "https").as_deref(),
            Some("b:2")
        );
        // No https entry → tunnel through the http proxy.
        assert_eq!(
            pick_protocol("http=a:1;ftp=c:3", "https").as_deref(),
            Some("a:1")
        );
        // Bare form applies to everything.
        assert_eq!(pick_protocol("p:7897", "https").as_deref(), Some("p:7897"));
        assert_eq!(pick_protocol("", "https"), None);
    }

    #[test]
    fn child_env_carries_the_proxy_or_nothing() {
        let pairs = child_env_pairs(Some("http://192.168.1.54:7897"));
        assert!(pairs
            .iter()
            .any(|(k, v)| *k == "HTTPS_PROXY" && v == "http://192.168.1.54:7897"));
        assert!(pairs
            .iter()
            .any(|(k, v)| *k == "https_proxy" && v == "http://192.168.1.54:7897"));
        assert!(pairs.iter().any(|(k, v)| *k == RESOLVES_HOSTS && v == "1"));
        assert!(child_env_pairs(None).is_empty());
    }

    #[test]
    fn stored_direct_and_system_and_url() {
        assert_eq!(normalize_stored(None).unwrap(), None);
        assert_eq!(normalize_stored(Some("")).unwrap(), None);
        assert_eq!(normalize_stored(Some("system")).unwrap(), None);
        assert_eq!(
            normalize_stored(Some("direct")).unwrap().as_deref(),
            Some(DIRECT)
        );
        assert_eq!(
            normalize_stored(Some("127.0.0.1:7897")).unwrap().as_deref(),
            Some("http://127.0.0.1:7897")
        );
        assert!(normalize_stored(Some("socks5://127.0.0.1:1080")).is_err());
        assert!(is_direct(Some("DIRECT")));
        assert!(!is_direct(None));
        assert_eq!(resolve_stored(Some("direct"), "api.anthropic.com"), None);
    }

    #[test]
    fn account_system_follows_the_app_proxy() {
        let host = "api.anthropic.com";
        let app = Some("http://127.0.0.1:7897");
        assert_eq!(
            resolve_stored_in(None, host, app).as_deref(),
            Some("http://127.0.0.1:7897")
        );
        assert_eq!(resolve_stored_in(Some("direct"), host, app), None);
        assert_eq!(
            resolve_stored_in(Some("http://10.0.0.1:1"), host, app).as_deref(),
            Some("http://10.0.0.1:1")
        );
        assert_eq!(resolve_app(Some("direct"), host), None);
        let (env, scrub, url) = launch_env_in(None, host, Some("direct"));
        assert!(url.is_none());
        assert!(env.is_empty());
        assert!(scrub.iter().any(|k| k == "HTTPS_PROXY"));
        let (env, _, url) = launch_env_in(None, host, app);
        assert_eq!(url.as_deref(), Some("http://127.0.0.1:7897"));
        assert_eq!(
            env.get("HTTPS_PROXY").map(String::as_str),
            Some("http://127.0.0.1:7897")
        );
    }

    #[test]
    fn launch_env_scrubs_on_direct_and_sets_on_url() {
        let (env, scrub, url) = launch_env(Some("direct"), "api.anthropic.com");
        assert!(env.is_empty());
        assert!(url.is_none());
        assert!(scrub.iter().any(|k| k == "HTTPS_PROXY"));

        let (env, scrub, url) = launch_env(Some("http://127.0.0.1:7897"), "api.anthropic.com");
        assert_eq!(
            env.get("HTTPS_PROXY").map(String::as_str),
            Some("http://127.0.0.1:7897")
        );
        assert_eq!(
            env.get("https_proxy").map(String::as_str),
            Some("http://127.0.0.1:7897")
        );
        assert!(scrub.is_empty());
        assert_eq!(url.as_deref(), Some("http://127.0.0.1:7897"));
    }

    #[test]
    fn settings_json_env_is_merged_not_replaced() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("settings.json");
        std::fs::write(&path, r#"{"model":"opus","env":{"FOO":"bar"}}"#).unwrap();
        apply_settings_env(&path, Some("http://127.0.0.1:7897")).unwrap();
        let v: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        assert_eq!(v["model"], "opus");
        assert_eq!(v["env"]["FOO"], "bar");
        assert_eq!(v["env"]["HTTPS_PROXY"], "http://127.0.0.1:7897");
        apply_settings_env(&path, None).unwrap();
        let v: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        assert_eq!(v["env"]["FOO"], "bar");
        assert!(v["env"].get("HTTPS_PROXY").is_none());
    }

    #[test]
    fn launch_settings_write_proxy_and_locale_together() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("settings.json");
        std::fs::write(&path, r#"{"model":"opus"}"#).unwrap();
        apply_launch_settings(
            &path,
            Some("http://127.0.0.1:7897"),
            Some("Asia/Tokyo"),
            Some("japanese"),
        )
        .unwrap();
        let v: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        assert_eq!(v["model"], "opus");
        assert_eq!(v["timeZone"], "Asia/Tokyo");
        assert_eq!(v["language"], "japanese");
        assert_eq!(v["env"]["HTTPS_PROXY"], "http://127.0.0.1:7897");
        assert_eq!(v["env"]["TZ"], "Asia/Tokyo");
        apply_launch_settings(&path, None, None, None).unwrap();
        let v: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        assert!(v.get("timeZone").is_none());
        assert!(v.get("language").is_none());
        assert!(v.get("env").and_then(|e| e.get("HTTPS_PROXY")).is_none());
        assert!(v.get("env").and_then(|e| e.get("TZ")).is_none());
    }

    #[test]
    fn no_proxy_matches_suffixes_and_wildcards() {
        assert!(bypasses("example.com", "api.example.com"));
        assert!(bypasses(".example.com", "api.example.com"));
        assert!(bypasses("*.example.com", "api.example.com"));
        assert!(bypasses("a.com,example.com", "example.com"));
        assert!(bypasses("*", "anything.com"));
        assert!(!bypasses("example.com", "notexample.com"));
        assert!(!bypasses("", "api.anthropic.com"));
    }

    #[test]
    fn windows_override_forms() {
        // The real ProxyOverride list from an ordinary Windows box.
        let over = "localhost;127.*;192.168.*;10.*;<local>";
        assert!(bypasses(over, "localhost"));
        assert!(bypasses(over, "127.0.0.1"));
        assert!(bypasses(over, "192.168.1.54"));
        // Dotless host = "local" by Windows' definition.
        assert!(bypasses(over, "intranet"));
        // The endpoint we actually care about must NOT be bypassed.
        assert!(!bypasses(over, "api.anthropic.com"));
    }
}
