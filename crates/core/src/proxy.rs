//! System/environment HTTP proxy discovery.
//!
//! Claude Code itself goes through the machine's proxy, so an account that works
//! in `claude` must also work here — a direct connection can be refused outright
//! (Anthropic answers blocked networks with `403 "Request not allowed"`, which
//! looks exactly like an auth failure and is nothing of the sort).
//!
//! Resolution order matches what CLI tools and browsers do:
//! 1. `HTTPS_PROXY` / `https_proxy`, then `ALL_PROXY` / `all_proxy`
//! 2. Windows Internet Settings (`ProxyEnable` + `ProxyServer`)
//!
//! `NO_PROXY` (and the Windows `ProxyOverride` list) short-circuit both.

/// Proxy URL to use for `host`, or `None` to connect directly.
#[must_use]
pub fn resolve_for(host: &str) -> Option<String> {
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
