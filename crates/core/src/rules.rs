//! Detection rules: the wording and markers that decide how a failure is
//! classified, and whether Claude Code has its own continue armed.
//!
//! These are the parts of the engine that track *someone else's* strings — the
//! API's error text, the classes Claude Code records, the notices it writes into
//! a transcript — and so the parts most likely to go stale with no change on our
//! side. The wording has already changed once: `Claude AI usage limit reached`
//! became `You've hit your session limit · resets 2:50pm`, and the classifier
//! kept compiling while silently no longer recognising the quota wall.
//!
//! # Built in, overridable, never load-bearing when broken
//!
//! [`builtin`] is what this build knows. A rules file beside the account backups
//! ([`RULES_FILENAME`]) overrides any part of it: a field left out keeps its
//! built-in value, and a list that is given replaces the built-in list. An
//! update therefore carries only what changed, and everything else keeps
//! following whatever a later build ships. A file that does not parse, declares
//! another schema, or fails validation is ignored in favour of the built-in
//! rules, and the reason is reported: a bad update must degrade detection to
//! what shipped, never break it.
//!
//! # Matching, deliberately simple
//!
//! Only case-insensitive substring groups, exact error kinds and case-sensitive
//! notice prefixes — no regular expressions. The rules are data meant to arrive
//! from elsewhere, and a pattern language would let one bad update make every
//! classification pathologically slow. A marker group matches when **all** of
//! its fragments occur in the text; a list of groups matches when **any** group
//! does. The *order* in which the classes are tried stays in code
//! ([`crate::autocontinue::classify_failure_with`]): it encodes why a quota hit
//! reported as `server_error` must not be retried on a network backoff.
//!
//! # Updates
//!
//! [`install`] validates a document and writes it atomically. `revision` is
//! monotonic: a document older than the installed one is refused unless the
//! caller explicitly allows a downgrade, so a delayed or replayed update cannot
//! roll the rules back. The engine reads the file on every use, so an installed
//! update applies to the next classification without a restart.

use std::path::Path;
use std::sync::LazyLock;

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::errors::{Error, Result};
use crate::fsutil::atomic_write;

/// File name of the rules override, beside the account backups.
pub const RULES_FILENAME: &str = "detection-rules.json";

/// The rules format this build reads. A document declaring another is ignored.
pub const SCHEMA_VERSION: u32 = 1;

static BUILTIN: LazyLock<DetectionRules> = LazyLock::new(|| DetectionRules {
    schema_version: SCHEMA_VERSION,
    revision: 0,
    classification: ClassificationRules::shipped(),
    auto_continue: AutoContinueRules::shipped(),
});

/// The rules this build shipped with.
#[must_use]
pub fn builtin() -> &'static DetectionRules {
    &BUILTIN
}

/// Every judgement that depends on wording we do not control.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct DetectionRules {
    pub schema_version: u32,
    /// Monotonic version of this document; 0 is the built-in rules.
    pub revision: u64,
    pub classification: ClassificationRules,
    pub auto_continue: AutoContinueRules,
}

impl Default for DetectionRules {
    fn default() -> Self {
        builtin().clone()
    }
}

/// What makes a failure a quota wall, a network blip, or a sign-in problem.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ClassificationRules {
    /// Error kinds that mean the quota wall, whatever the text says.
    pub rate_limit_kinds: Vec<String>,
    /// Text that means the quota wall. Tried before every other kind, because
    /// older adapters report a quota hit as `server_error`.
    pub rate_limit_text: Vec<Vec<String>>,
    /// Error kinds that need a human: re-login, fix the proxy.
    pub auth_kinds: Vec<String>,
    /// Error kinds that a retry could get past.
    pub network_kinds: Vec<String>,
    /// Text that marks a retryable transport or backend failure, for failures
    /// that arrive with no kind at all.
    pub network_text: Vec<Vec<String>>,
    /// Text that marks a sign-in problem, tried last.
    pub auth_text: Vec<Vec<String>>,
}

impl Default for ClassificationRules {
    fn default() -> Self {
        Self::shipped()
    }
}

impl ClassificationRules {
    fn shipped() -> Self {
        Self {
            rate_limit_kinds: owned(&["rate_limit"]),
            rate_limit_text: groups(&[
                &["usage limit reached"],
                &["rate limit"],
                &["429"],
                // `You've hit your session limit` / `You've hit your weekly limit`.
                &["hit your", "limit"],
            ]),
            auth_kinds: owned(&["authentication_failed"]),
            network_kinds: owned(&["server_error", "overloaded"]),
            network_text: groups(&[
                &["econnreset"],
                &["fetch failed"],
                &["connection closed"],
                &["connection error"],
                &["socket"],
                &["overloaded"],
                &["timed out"],
                &["529"],
                &["500"],
                &["502"],
                &["503"],
            ]),
            auth_text: groups(&[&["403"], &["authenticate"]]),
        }
    }
}

/// How Claude Code's own automatic continue shows up in a transcript.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AutoContinueRules {
    /// Notices announcing that Claude Code has armed its own continue.
    pub armed_prefixes: Vec<String>,
    /// Notices ending an armed continue without firing it: turned off, stopped
    /// past its horizon, stopped after repeated hits.
    pub ended_prefixes: Vec<String>,
    /// What precedes the announced reset time in an arm notice (`… at 2:50pm`).
    pub reset_time_marker: String,
    /// How long past the announced reset an armed continue is still expected.
    pub grace_minutes: u32,
    /// How long an arm whose reset time cannot be read is trusted.
    pub fallback_hours: u32,
}

impl Default for AutoContinueRules {
    fn default() -> Self {
        Self::shipped()
    }
}

impl AutoContinueRules {
    fn shipped() -> Self {
        Self {
            armed_prefixes: owned(&["Usage limit reached · continuing"]),
            ended_prefixes: owned(&["Automatic continue"]),
            reset_time_marker: " at ".to_owned(),
            grace_minutes: 15,
            // A session limit resets within its five-hour window.
            fallback_hours: 5,
        }
    }

    #[must_use]
    pub fn is_armed_notice(&self, content: &str) -> bool {
        self.armed_prefixes
            .iter()
            .any(|p| content.starts_with(p.as_str()))
    }

    #[must_use]
    pub fn is_ended_notice(&self, content: &str) -> bool {
        self.ended_prefixes
            .iter()
            .any(|p| content.starts_with(p.as_str()))
    }

    #[must_use]
    pub const fn grace_ms(&self) -> i64 {
        self.grace_minutes as i64 * 60_000
    }

    #[must_use]
    pub const fn fallback_ms(&self) -> i64 {
        self.fallback_hours as i64 * 3_600_000
    }
}

/// Whether `kind` is one of `kinds`.
#[must_use]
pub fn has_kind(kinds: &[String], kind: Option<&str>) -> bool {
    kind.is_some_and(|k| kinds.iter().any(|candidate| candidate == k))
}

/// Whether `text` matches any group: every fragment of one group occurs in it,
/// ignoring case.
#[must_use]
pub fn matches_any(groups: &[Vec<String>], text: &str) -> bool {
    let text = text.to_lowercase();
    groups.iter().any(|group| {
        !group.is_empty()
            && group
                .iter()
                .all(|fragment| text.contains(&fragment.to_lowercase()))
    })
}

impl DetectionRules {
    /// Refuse rules that would misclassify everything rather than something.
    ///
    /// An empty fragment is contained in every string and an empty prefix
    /// starts every notice, so one of either would turn every failure into a
    /// quota wall, or every notice into an armed continue.
    pub fn validate(&self) -> Result<()> {
        if self.schema_version != SCHEMA_VERSION {
            return Err(invalid(&format!(
                "schemaVersion {} is not {SCHEMA_VERSION}, the version this build reads",
                self.schema_version
            )));
        }

        let c = &self.classification;
        for (name, kinds) in [
            ("rateLimitKinds", &c.rate_limit_kinds),
            ("authKinds", &c.auth_kinds),
            ("networkKinds", &c.network_kinds),
        ] {
            if kinds.iter().any(|k| k.trim().is_empty()) {
                return Err(invalid(&format!(
                    "classification.{name} contains an empty kind"
                )));
            }
        }
        for (name, groups) in [
            ("rateLimitText", &c.rate_limit_text),
            ("networkText", &c.network_text),
            ("authText", &c.auth_text),
        ] {
            if groups
                .iter()
                .any(|g| g.is_empty() || g.iter().any(|f| f.trim().is_empty()))
            {
                return Err(invalid(&format!(
                    "classification.{name} contains an empty group or fragment, which would match every message"
                )));
            }
        }
        if c.rate_limit_kinds.is_empty() && c.rate_limit_text.is_empty() {
            return Err(invalid(
                "classification has no way left to recognise the quota wall",
            ));
        }

        let a = &self.auto_continue;
        if a.armed_prefixes
            .iter()
            .chain(&a.ended_prefixes)
            .any(|p| p.trim().is_empty())
        {
            return Err(invalid(
                "autoContinue contains an empty notice prefix, which would match every notice",
            ));
        }
        if a.reset_time_marker.is_empty() {
            return Err(invalid("autoContinue.resetTimeMarker must not be empty"));
        }
        if a.grace_minutes > 24 * 60 {
            return Err(invalid("autoContinue.graceMinutes must be at most 1440"));
        }
        if !(1..=24).contains(&a.fallback_hours) {
            return Err(invalid(
                "autoContinue.fallbackHours must be between 1 and 24",
            ));
        }
        Ok(())
    }
}

fn invalid(why: &str) -> Error {
    Error::Validation(format!("invalid detection rules: {why}"))
}

/// The rules in force at one moment, and where they came from.
#[derive(Clone, Debug)]
pub struct Effective {
    pub rules: DetectionRules,
    /// A rules file supplied them.
    pub from_file: bool,
    /// Why a rules file that exists is not in force.
    pub rejected: Option<String>,
}

/// The rules in force: the file at `path` over the built-in rules, or the
/// built-in rules alone when there is no usable file.
#[must_use]
pub fn load(path: &Path) -> Effective {
    match std::fs::read_to_string(path) {
        Ok(text) => match parse(&text) {
            Ok(rules) => Effective {
                rules,
                from_file: true,
                rejected: None,
            },
            Err(e) => builtin_because(Some(e.to_string())),
        },
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => builtin_because(None),
        Err(e) => builtin_because(Some(format!("unreadable rules file: {e}"))),
    }
}

fn builtin_because(rejected: Option<String>) -> Effective {
    Effective {
        rules: builtin().clone(),
        from_file: false,
        rejected,
    }
}

/// Parse and validate a rules document.
pub fn parse(text: &str) -> Result<DetectionRules> {
    let doc: Value =
        serde_json::from_str(text).map_err(|e| invalid(&format!("not valid JSON: {e}")))?;
    from_document(&doc)
}

fn from_document(doc: &Value) -> Result<DetectionRules> {
    if !doc.is_object() {
        return Err(invalid("the document must be a JSON object"));
    }
    // Checked before merging: a document written for another format may mean
    // something different by the very fields this build knows.
    if let Some(v) = doc.get("schemaVersion") {
        if v.as_u64() != Some(u64::from(SCHEMA_VERSION)) {
            return Err(invalid(&format!(
                "schemaVersion {v} is not {SCHEMA_VERSION}, the version this build reads"
            )));
        }
    }
    let rules: DetectionRules =
        serde_json::from_value(doc.clone()).map_err(|e| invalid(&e.to_string()))?;
    rules.validate()?;
    Ok(rules)
}

/// What `detection_rules_get` reports: the rules in force, where they came
/// from, why a file was not used, and the built-in rules for comparison.
#[must_use]
pub fn describe(path: &Path) -> Value {
    let effective = load(path);
    json!({
        "source": if effective.from_file { "file" } else { "builtin" },
        "file": path.to_string_lossy(),
        "schemaVersion": SCHEMA_VERSION,
        "revision": effective.rules.revision,
        "rejected": effective.rejected,
        "rules": effective.rules,
        "builtin": builtin(),
    })
}

/// Validate a rules document and install it at `path`.
///
/// The document is stored as given, not merged: fields it leaves out keep
/// following the built-in rules, including whatever a later build improves.
///
/// A revision older than the installed one is refused unless `allow_downgrade`.
/// Resending the installed revision unchanged succeeds without writing;
/// resending it with different contents is refused, since two documents under
/// one revision would make "which rules are these" unanswerable.
pub fn install(path: &Path, doc: &Value, allow_downgrade: bool) -> Result<DetectionRules> {
    let rules = from_document(doc)?;
    let current = load(path);
    if current.from_file && !allow_downgrade {
        let installed = current.rules.revision;
        if rules.revision < installed {
            return Err(invalid(&format!(
                "revision {} is older than the installed revision {installed}",
                rules.revision
            )));
        }
        if rules.revision == installed {
            if rules == current.rules {
                return Ok(rules);
            }
            return Err(invalid(&format!(
                "revision {installed} is already installed with different contents"
            )));
        }
    }
    let bytes = serde_json::to_vec_pretty(doc).map_err(|e| Error::Internal(e.to_string()))?;
    atomic_write(path, &bytes).map_err(Error::Io)?;
    Ok(rules)
}

/// Remove an installed rules file, returning to the built-in rules. Reports
/// whether there was one.
pub fn reset(path: &Path) -> Result<bool> {
    match std::fs::remove_file(path) {
        Ok(()) => Ok(true),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(false),
        Err(e) => Err(Error::Io(e)),
    }
}

fn owned(items: &[&str]) -> Vec<String> {
    items.iter().map(|s| (*s).to_owned()).collect()
}

fn groups(items: &[&[&str]]) -> Vec<Vec<String>> {
    items.iter().map(|g| owned(g)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_builtin_rules_are_valid() {
        builtin().validate().unwrap();
        assert_eq!(builtin().revision, 0);
    }

    #[test]
    fn a_partial_document_keeps_everything_else_builtin() {
        let rules = parse(r#"{ "revision": 3, "autoContinue": { "graceMinutes": 30 } }"#).unwrap();
        assert_eq!(rules.revision, 3);
        assert_eq!(rules.auto_continue.grace_minutes, 30);
        assert_eq!(
            rules.auto_continue.armed_prefixes,
            builtin().auto_continue.armed_prefixes
        );
        assert_eq!(rules.classification, builtin().classification);
    }

    #[test]
    fn a_list_that_is_given_replaces_the_builtin_list() {
        let rules =
            parse(r#"{ "classification": { "rateLimitText": [["quota wall"]] } }"#).unwrap();
        assert_eq!(
            rules.classification.rate_limit_text,
            vec![vec!["quota wall".to_owned()]]
        );
        assert_eq!(
            rules.classification.network_text,
            builtin().classification.network_text
        );
    }

    #[test]
    fn fields_a_newer_build_added_are_ignored() {
        let rules = parse(r#"{ "revision": 1, "somethingNew": { "x": 1 } }"#).unwrap();
        assert_eq!(rules.revision, 1);
    }

    #[test]
    fn rules_that_would_match_everything_are_refused() {
        for doc in [
            r#"{ "classification": { "rateLimitText": [[""]] } }"#,
            r#"{ "classification": { "networkText": [[]] } }"#,
            r#"{ "autoContinue": { "armedPrefixes": [" "] } }"#,
            r#"{ "autoContinue": { "resetTimeMarker": "" } }"#,
            r#"{ "classification": { "rateLimitKinds": [], "rateLimitText": [] } }"#,
            r#"{ "autoContinue": { "fallbackHours": 0 } }"#,
        ] {
            assert!(parse(doc).is_err(), "should refuse {doc}");
        }
    }

    #[test]
    fn another_schema_is_refused_before_merging() {
        assert!(parse(r#"{ "schemaVersion": 2 }"#).is_err());
        assert!(parse(r#"["not", "an", "object"]"#).is_err());
    }

    #[test]
    fn groups_need_every_fragment_and_ignore_case() {
        let g = groups(&[&["hit your", "limit"]]);
        assert!(matches_any(&g, "You've HIT YOUR weekly LIMIT"));
        assert!(!matches_any(&g, "You've hit your stride"));
        assert!(has_kind(&owned(&["rate_limit"]), Some("rate_limit")));
        assert!(!has_kind(&owned(&["rate_limit"]), None));
    }

    #[test]
    fn no_file_is_the_builtin_rules_without_complaint() {
        let dir = tempfile::tempdir().unwrap();
        let effective = load(&dir.path().join(RULES_FILENAME));
        assert!(!effective.from_file);
        assert!(effective.rejected.is_none());
        assert_eq!(effective.rules, *builtin());
    }

    #[test]
    fn a_broken_file_falls_back_to_the_builtin_rules_and_says_why() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(RULES_FILENAME);
        std::fs::write(
            &path,
            r#"{ "classification": { "rateLimitText": [[""]] } }"#,
        )
        .unwrap();
        let effective = load(&path);
        assert!(!effective.from_file);
        assert!(effective.rejected.unwrap().contains("rateLimitText"));
        assert_eq!(effective.rules, *builtin());
    }

    #[test]
    fn install_refuses_going_back_and_tolerates_a_resend() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(RULES_FILENAME);
        let v2 = serde_json::json!({ "revision": 2, "autoContinue": { "graceMinutes": 20 } });
        install(&path, &v2, false).unwrap();

        let v1 = serde_json::json!({ "revision": 1 });
        assert!(
            install(&path, &v1, false).is_err(),
            "an older revision must not roll back"
        );
        install(&path, &v2, false).expect("resending the installed revision is harmless");

        let v2_changed =
            serde_json::json!({ "revision": 2, "autoContinue": { "graceMinutes": 25 } });
        assert!(install(&path, &v2_changed, false).is_err());

        install(&path, &v1, true).expect("an explicit downgrade is allowed");
        assert_eq!(load(&path).rules.revision, 1);
    }

    #[test]
    fn install_stores_the_document_as_given_not_merged() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(RULES_FILENAME);
        install(
            &path,
            &serde_json::json!({ "revision": 1, "autoContinue": { "graceMinutes": 30 } }),
            false,
        )
        .unwrap();
        let stored: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        // Left out, so it keeps following the built-in rules of later builds.
        assert!(stored.get("classification").is_none());
    }

    #[test]
    fn an_invalid_document_is_not_installed() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(RULES_FILENAME);
        assert!(install(
            &path,
            &serde_json::json!({ "autoContinue": { "armedPrefixes": [""] } }),
            false
        )
        .is_err());
        assert!(!path.exists());
    }

    #[test]
    fn reset_returns_to_the_builtin_rules() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(RULES_FILENAME);
        install(&path, &serde_json::json!({ "revision": 1 }), false).unwrap();
        assert!(reset(&path).unwrap());
        assert!(!reset(&path).unwrap());
        assert!(!load(&path).from_file);
    }
}
