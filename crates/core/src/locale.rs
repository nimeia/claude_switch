//! Per-account timezone and language for Claude Code launches.
//!
//! Claude Code does not read the Windows timezone or display language. It
//! shows interface times from `settings.json` `timeZone` (an IANA name) and
//! tells the model to reply in `language`. A child we spawn also gets `TZ` /
//! `LANG` / `LC_ALL` so Node/Bun and Unix-flavoured tools follow the same
//! region as the account's proxy exit — the local Clash URL cannot say which
//! country that is, so the values are stored next to the proxy and written at
//! launch.
//!
//! Absent / empty / `"system"` means "do not override": the keys are removed
//! from `settings.json` so a previous account's zone cannot stick, and the
//! names are scrubbed from the child so an inherited `TZ` cannot leak.

use serde_json::{json, Map, Value};

use crate::errors::{Error, Result};

const TZ: &str = "TZ";
const LANG: &str = "LANG";
const LC_ALL: &str = "LC_ALL";

/// Normalise a stored timezone: empty / `"system"` / `"local"` → inherit the
/// machine zone; anything else must look like an IANA name.
pub fn normalize_timezone(raw: Option<&str>) -> Result<Option<String>> {
    let s = raw.map(str::trim).filter(|s| !s.is_empty());
    match s {
        None => Ok(None),
        Some(s) if is_system_token(s) => Ok(None),
        Some(s) if is_iana(s) => Ok(Some(s.to_string())),
        Some(_) => Err(Error::Validation(
            "timezone must be an IANA name such as America/Los_Angeles, or empty for the system zone"
                .into(),
        )),
    }
}

/// Normalise a stored Claude Code `language` value: empty / `"system"` → unset.
/// The name is written verbatim into the system prompt, so it is not a closed
/// list; we only refuse controls and absurd length.
pub fn normalize_language(raw: Option<&str>) -> Result<Option<String>> {
    let s = raw.map(str::trim).filter(|s| !s.is_empty());
    match s {
        None => Ok(None),
        Some(s) if is_system_token(s) => Ok(None),
        Some(s) if s.len() > 64 || s.chars().any(char::is_control) => Err(Error::Validation(
            "language must be a short name such as chinese, english, or japanese".into(),
        )),
        Some(s) => Ok(Some(s.to_string())),
    }
}

/// Env to set and names to scrub for timezone / language.
///
/// When a value is unset we scrub, not skip: a parent `TZ` would otherwise
/// outlive the account that set it.
#[must_use]
pub fn launch_env(
    timezone: Option<&str>,
    language: Option<&str>,
) -> (std::collections::BTreeMap<String, String>, Vec<String>) {
    let mut env = std::collections::BTreeMap::new();
    let mut scrub = Vec::new();
    match timezone.map(str::trim).filter(|s| !s.is_empty()) {
        Some(tz) => {
            env.insert(TZ.to_string(), tz.to_string());
        }
        None => scrub.push(TZ.to_string()),
    }
    match language.map(str::trim).filter(|s| !s.is_empty()) {
        Some(lang) => match posix_locale(lang) {
            Some(posix) => {
                env.insert(LANG.to_string(), posix.clone());
                env.insert(LC_ALL.to_string(), posix);
            }
            None => {
                scrub.push(LANG.to_string());
                scrub.push(LC_ALL.to_string());
            }
        },
        None => {
            scrub.push(LANG.to_string());
            scrub.push(LC_ALL.to_string());
        }
    }
    (env, scrub)
}

/// Write or remove `timeZone` / `language` and the matching `env` keys.
pub fn merge_into_settings(
    map: &mut Map<String, Value>,
    timezone: Option<&str>,
    language: Option<&str>,
) {
    match timezone.map(str::trim).filter(|s| !s.is_empty()) {
        Some(tz) => {
            map.insert("timeZone".into(), json!(tz));
        }
        None => {
            map.remove("timeZone");
        }
    }
    match language.map(str::trim).filter(|s| !s.is_empty()) {
        Some(lang) => {
            map.insert("language".into(), json!(lang));
        }
        None => {
            map.remove("language");
        }
    }

    let mut env = match map.get("env") {
        Some(Value::Object(m)) => m.clone(),
        _ => Map::new(),
    };
    match timezone.map(str::trim).filter(|s| !s.is_empty()) {
        Some(tz) => {
            env.insert(TZ.into(), json!(tz));
        }
        None => {
            env.remove(TZ);
        }
    }
    match language.map(str::trim).filter(|s| !s.is_empty()) {
        Some(lang) => match posix_locale(lang) {
            Some(posix) => {
                env.insert(LANG.into(), json!(posix));
                env.insert(LC_ALL.into(), json!(posix));
            }
            None => {
                env.remove(LANG);
                env.remove(LC_ALL);
            }
        },
        None => {
            env.remove(LANG);
            env.remove(LC_ALL);
        }
    }
    if env.is_empty() {
        map.remove("env");
    } else {
        map.insert("env".into(), Value::Object(env));
    }
}

/// POSIX `LANG` for a Claude Code language name, when we recognise it.
#[must_use]
pub fn posix_locale(language: &str) -> Option<String> {
    let s = language.trim();
    if s.is_empty() {
        return None;
    }
    if let Some(posix) = as_posix_locale(s) {
        return Some(posix);
    }
    let key = s.to_ascii_lowercase().replace('_', "-");
    Some(
        match key.as_str() {
            "chinese" | "zh" | "zh-cn" | "zh-hans" | "simplified chinese" => "zh_CN.UTF-8",
            "zh-tw" | "zh-hk" | "zh-hant" | "traditional chinese" => "zh_TW.UTF-8",
            "japanese" | "ja" | "jp" => "ja_JP.UTF-8",
            "korean" | "ko" => "ko_KR.UTF-8",
            "english" | "en" | "en-us" | "american english" => "en_US.UTF-8",
            "en-gb" | "british english" => "en_GB.UTF-8",
            "spanish" | "es" => "es_ES.UTF-8",
            "french" | "fr" => "fr_FR.UTF-8",
            "german" | "de" => "de_DE.UTF-8",
            "portuguese" | "pt" | "pt-br" => "pt_BR.UTF-8",
            _ => return None,
        }
        .to_string(),
    )
}

/// What a geolocation lookup concluded about the proxy's exit.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ExitLocale {
    pub ip: String,
    pub country_code: String,
    pub country: String,
    pub timezone: String,
    pub language: String,
}

/// Probe the public IP as seen through `stored` (system / direct / URL) and
/// map it to a Claude Code timezone + language.
///
/// `app` is the app-wide proxy used when `stored` is system.
pub fn detect_exit(stored: Option<&str>, app: Option<&str>) -> Result<ExitLocale> {
    let proxy_url = crate::proxy::resolve_stored_in(stored, crate::proxy::ANTHROPIC_API_HOST, app);
    fetch_exit(proxy_url.as_deref())
}

fn fetch_exit(proxy_url: Option<&str>) -> Result<ExitLocale> {
    let mut builder = ureq::AgentBuilder::new()
        .timeout_connect(std::time::Duration::from_secs(4))
        .timeout_read(std::time::Duration::from_secs(8));
    if let Some(url) = proxy_url {
        match ureq::Proxy::new(url) {
            Ok(p) => builder = builder.proxy(p),
            Err(e) => {
                return Err(Error::Validation(format!("proxy URL is not usable: {e}")));
            }
        }
    }
    let agent = builder.build();
    let mut last = None;
    for url in [
        "https://ipwho.is/",
        "https://ipapi.co/json/",
        "http://ip-api.com/json/?fields=status,message,query,country,countryCode,timezone",
    ] {
        match get_json(&agent, url) {
            Ok(v) => match parse_geo_json(&v) {
                Ok(loc) => return Ok(loc),
                Err(e) => last = Some(e),
            },
            Err(e) => last = Some(e),
        }
    }
    Err(last.unwrap_or_else(|| {
        Error::Network("could not reach an IP geolocation service through this proxy".into())
    }))
}

fn get_json(agent: &ureq::Agent, url: &str) -> Result<serde_json::Value> {
    let resp = agent
        .get(url)
        .set("Accept", "application/json")
        .set("User-Agent", "ClaudeSwitch/locale-detect")
        .call()
        .map_err(|e| Error::Network(format!("geolocation GET failed: {e}")))?;
    let text = resp
        .into_string()
        .map_err(|e| Error::Network(format!("geolocation body: {e}")))?;
    serde_json::from_str(&text).map_err(|e| Error::Network(format!("geolocation JSON: {e}")))
}

/// Read country / timezone out of ipwho.is, ipapi.co, or ip-api.com JSON.
pub fn parse_geo_json(v: &Value) -> Result<ExitLocale> {
    if v.get("success") == Some(&json!(false)) {
        let msg = v
            .get("message")
            .and_then(Value::as_str)
            .unwrap_or("geolocation failed");
        return Err(Error::Network(msg.into()));
    }
    if v.get("status").and_then(Value::as_str) == Some("fail") {
        let msg = v
            .get("message")
            .and_then(Value::as_str)
            .unwrap_or("geolocation failed");
        return Err(Error::Network(msg.into()));
    }

    let ip = first_str(v, &["ip", "query"]).unwrap_or_default();
    let country_code = first_str(v, &["country_code", "countryCode"])
        .unwrap_or_default()
        .to_ascii_uppercase();
    let country = first_str(v, &["country", "country_name"]).unwrap_or_default();
    let tz_raw = timezone_field(v);

    let timezone = match tz_raw.as_deref().filter(|s| is_iana(s)) {
        Some(tz) => tz.to_string(),
        None => timezone_for_country(&country_code)
            .map(str::to_string)
            .ok_or_else(|| Error::Network("geolocation response had no usable timezone".into()))?,
    };
    if country_code.is_empty() && tz_raw.is_none() {
        return Err(Error::Network(
            "geolocation response had no country or timezone".into(),
        ));
    }
    let language = language_for_country(&country_code).to_string();
    Ok(ExitLocale {
        ip,
        country_code,
        country,
        timezone,
        language,
    })
}

#[must_use]
pub fn language_for_country(cc: &str) -> &'static str {
    match cc.trim().to_ascii_uppercase().as_str() {
        "JP" => "japanese",
        "CN" | "TW" | "HK" | "MO" => "chinese",
        "KR" => "korean",
        "FR" => "french",
        "DE" | "AT" => "german",
        "ES" | "MX" | "AR" | "CL" | "CO" | "PE" | "VE" | "EC" | "UY" | "PY" | "BO" | "GT"
        | "CR" | "PA" | "CU" | "DO" | "HN" | "SV" | "NI" => "spanish",
        "BR" | "PT" => "portuguese",
        "IT" => "italian",
        "RU" | "BY" | "KZ" => "russian",
        "TH" => "thai",
        "VN" => "vietnamese",
        "ID" => "indonesian",
        "TR" => "turkish",
        "SA" | "AE" | "EG" | "QA" | "KW" | "BH" | "OM" | "JO" | "IQ" | "MA" | "DZ" | "TN"
        | "LB" => "arabic",
        _ => "english",
    }
}

fn timezone_for_country(cc: &str) -> Option<&'static str> {
    Some(match cc {
        "JP" => "Asia/Tokyo",
        "CN" => "Asia/Shanghai",
        "HK" => "Asia/Hong_Kong",
        "TW" => "Asia/Taipei",
        "MO" => "Asia/Macau",
        "KR" => "Asia/Seoul",
        "SG" => "Asia/Singapore",
        "GB" | "UK" => "Europe/London",
        "DE" => "Europe/Berlin",
        "FR" => "Europe/Paris",
        "AU" => "Australia/Sydney",
        "IN" => "Asia/Kolkata",
        "RU" => "Europe/Moscow",
        "BR" => "America/Sao_Paulo",
        "US" => "America/New_York",
        "CA" => "America/Toronto",
        _ => return None,
    })
}

fn first_str(v: &Value, keys: &[&str]) -> Option<String> {
    keys.iter().find_map(|k| {
        v.get(*k)
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(str::to_string)
    })
}

fn timezone_field(v: &Value) -> Option<String> {
    if let Some(s) = first_str(v, &["timezone", "time_zone"]) {
        return Some(s);
    }
    for key in ["timezone", "time_zone"] {
        if let Some(id) = v
            .get(key)
            .and_then(Value::as_object)
            .and_then(|o| o.get("id"))
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|s| !s.is_empty())
        {
            return Some(id.to_string());
        }
    }
    None
}

fn is_system_token(s: &str) -> bool {
    s.eq_ignore_ascii_case("system")
        || s.eq_ignore_ascii_case("default")
        || s.eq_ignore_ascii_case("auto")
        || s.eq_ignore_ascii_case("local")
}

fn is_iana(s: &str) -> bool {
    if s.is_empty() || s.len() > 64 {
        return false;
    }
    if s.eq_ignore_ascii_case("UTC") || s.eq_ignore_ascii_case("GMT") {
        return true;
    }
    let mut segs = 0u32;
    for seg in s.split('/') {
        segs += 1;
        if seg.is_empty() {
            return false;
        }
        let mut chars = seg.chars();
        let Some(first) = chars.next() else {
            return false;
        };
        if !first.is_ascii_alphabetic() && first != '_' {
            return false;
        }
        if !chars.all(|c| c.is_ascii_alphanumeric() || matches!(c, '_' | '+' | '-')) {
            return false;
        }
    }
    segs >= 2
}

/// `en_US` or `en_US.UTF-8` already in POSIX form.
fn as_posix_locale(s: &str) -> Option<String> {
    let (base, suffix) = match s.split_once('.') {
        Some((b, rest)) => (b, Some(rest)),
        None => (s, None),
    };
    let mut parts = base.split('_');
    let lang = parts.next()?;
    let region = parts.next()?;
    if parts.next().is_some() {
        return None;
    }
    if lang.len() != 2 || !lang.chars().all(|c| c.is_ascii_alphabetic()) {
        return None;
    }
    if region.len() != 2 || !region.chars().all(|c| c.is_ascii_alphabetic()) {
        return None;
    }
    match suffix {
        None => Some(format!("{base}.UTF-8")),
        Some(_) => Some(s.to_string()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn timezone_system_and_iana() {
        assert_eq!(normalize_timezone(None).unwrap(), None);
        assert_eq!(normalize_timezone(Some("")).unwrap(), None);
        assert_eq!(normalize_timezone(Some("system")).unwrap(), None);
        assert_eq!(
            normalize_timezone(Some("America/Los_Angeles"))
                .unwrap()
                .as_deref(),
            Some("America/Los_Angeles")
        );
        assert_eq!(
            normalize_timezone(Some("UTC")).unwrap().as_deref(),
            Some("UTC")
        );
        assert_eq!(
            normalize_timezone(Some("America/Argentina/Buenos_Aires"))
                .unwrap()
                .as_deref(),
            Some("America/Argentina/Buenos_Aires")
        );
        assert!(normalize_timezone(Some("PST")).is_err());
        assert!(normalize_timezone(Some("America/Los Angeles")).is_err());
        assert!(normalize_timezone(Some("../etc")).is_err());
    }

    #[test]
    fn language_is_a_short_name() {
        assert_eq!(normalize_language(None).unwrap(), None);
        assert_eq!(normalize_language(Some("system")).unwrap(), None);
        assert_eq!(
            normalize_language(Some("chinese")).unwrap().as_deref(),
            Some("chinese")
        );
        let long = "x".repeat(80);
        assert!(normalize_language(Some(&long)).is_err());
        assert!(normalize_language(Some("en\nl")).is_err());
    }

    #[test]
    fn posix_locale_maps_common_names() {
        assert_eq!(posix_locale("chinese").as_deref(), Some("zh_CN.UTF-8"));
        assert_eq!(posix_locale("Japanese").as_deref(), Some("ja_JP.UTF-8"));
        assert_eq!(posix_locale("english").as_deref(), Some("en_US.UTF-8"));
        assert_eq!(posix_locale("zh_CN").as_deref(), Some("zh_CN.UTF-8"));
        assert_eq!(posix_locale("zh_CN.UTF-8").as_deref(), Some("zh_CN.UTF-8"));
        assert_eq!(posix_locale("klingon"), None);
    }

    #[test]
    fn launch_env_sets_or_scrubs() {
        let (env, scrub) = launch_env(Some("Asia/Tokyo"), Some("japanese"));
        assert_eq!(env.get("TZ").map(String::as_str), Some("Asia/Tokyo"));
        assert_eq!(env.get("LANG").map(String::as_str), Some("ja_JP.UTF-8"));
        assert!(scrub.is_empty());

        let (env, scrub) = launch_env(None, None);
        assert!(env.is_empty());
        assert!(scrub.iter().any(|k| k == "TZ"));
        assert!(scrub.iter().any(|k| k == "LANG"));
    }

    #[test]
    fn settings_json_locale_is_merged() {
        let mut map = Map::new();
        map.insert("model".into(), json!("opus"));
        map.insert("env".into(), json!({"FOO": "bar"}));
        merge_into_settings(&mut map, Some("Asia/Tokyo"), Some("japanese"));
        assert_eq!(map["model"], "opus");
        assert_eq!(map["timeZone"], "Asia/Tokyo");
        assert_eq!(map["language"], "japanese");
        assert_eq!(map["env"]["FOO"], "bar");
        assert_eq!(map["env"]["TZ"], "Asia/Tokyo");
        assert_eq!(map["env"]["LANG"], "ja_JP.UTF-8");

        merge_into_settings(&mut map, None, None);
        assert!(map.get("timeZone").is_none());
        assert!(map.get("language").is_none());
        assert_eq!(map["env"]["FOO"], "bar");
        assert!(map["env"].get("TZ").is_none());
    }

    #[test]
    fn geo_json_from_the_common_providers() {
        let ipwho = json!({
            "ip": "203.0.113.10",
            "success": true,
            "country": "Japan",
            "country_code": "JP",
            "timezone": { "id": "Asia/Tokyo" }
        });
        let loc = parse_geo_json(&ipwho).unwrap();
        assert_eq!(loc.ip, "203.0.113.10");
        assert_eq!(loc.country_code, "JP");
        assert_eq!(loc.timezone, "Asia/Tokyo");
        assert_eq!(loc.language, "japanese");

        let ipapi = json!({
            "ip": "198.51.100.2",
            "country_name": "United States",
            "country_code": "us",
            "timezone": "America/Los_Angeles"
        });
        let loc = parse_geo_json(&ipapi).unwrap();
        assert_eq!(loc.timezone, "America/Los_Angeles");
        assert_eq!(loc.language, "english");

        let ipapi_com = json!({
            "status": "success",
            "query": "192.0.2.1",
            "country": "China",
            "countryCode": "CN",
            "timezone": "Asia/Shanghai"
        });
        let loc = parse_geo_json(&ipapi_com).unwrap();
        assert_eq!(loc.language, "chinese");
        assert_eq!(loc.timezone, "Asia/Shanghai");
    }

    #[test]
    fn geo_json_falls_back_to_country_timezone() {
        let v = json!({ "country_code": "JP", "ip": "1.1.1.1" });
        let loc = parse_geo_json(&v).unwrap();
        assert_eq!(loc.timezone, "Asia/Tokyo");
        assert_eq!(loc.language, "japanese");
    }

    #[test]
    fn geo_json_rejects_failures() {
        assert!(parse_geo_json(&json!({"success": false, "message": "nope"})).is_err());
        assert!(parse_geo_json(&json!({"status": "fail", "message": "nope"})).is_err());
        assert!(parse_geo_json(&json!({"ip": "1.2.3.4"})).is_err());
    }

    #[test]
    fn language_follows_the_country() {
        assert_eq!(language_for_country("jp"), "japanese");
        assert_eq!(language_for_country("CN"), "chinese");
        assert_eq!(language_for_country("GB"), "english");
        assert_eq!(language_for_country("XX"), "english");
    }
}
