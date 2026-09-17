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
}
