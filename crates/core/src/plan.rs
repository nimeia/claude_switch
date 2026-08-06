//! Subscription plan facts for a managed account.
//!
//! Everything here is read back from what Claude Code already wrote — the
//! account's own credential blob (`claudeAiOauth.subscriptionType` /
//! `rateLimitTier`) and its `.claude.json` snapshot (`oauthAccount`). Nothing is
//! fetched or inferred: Anthropic exposes no renewal/billing date to an OAuth
//! token (the scopes are `user:profile` / `user:inference` / …, none of them
//! billing), so this module reports *facts only* — `subscriptionCreatedAt` is
//! when the subscription started, never when it next renews.
//!
//! Freshness caveat: `oauthAccount` is written by Claude Code itself and is
//! captured into a slot on add/switch, so a slot that has not been live since
//! an upgrade still reports the older plan. [`PlanInfo::profile_fetched_at`]
//! carries Claude Code's own fetch timestamp so the UI can say how old it is.

use serde::{Deserialize, Serialize};
use serde_json::Value;

/// Subscription tier, normalized across the three fields that name it.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum PlanTier {
    Free,
    Pro,
    /// Max with an unknown multiplier (tier string named `max` but no 5x/20x).
    Max,
    Max5x,
    Max20x,
    Team,
    Enterprise,
    /// Managed `sk-ant-api…` key — no subscription quota at all.
    ApiKey,
    #[default]
    Unknown,
}

impl PlanTier {
    /// Display label, or `""` when unknown (callers render no badge then).
    ///
    /// Deliberately ASCII/product naming, not localized: the shells decide how
    /// to present an unknown tier.
    #[must_use]
    pub const fn label(self) -> &'static str {
        match self {
            Self::Free => "Free",
            Self::Pro => "Pro",
            Self::Max => "Max",
            Self::Max5x => "Max 5×",
            Self::Max20x => "Max 20×",
            Self::Team => "Team",
            Self::Enterprise => "Enterprise",
            Self::ApiKey => "API Key",
            Self::Unknown => "",
        }
    }

    /// Whether the tier implies a personal (self-owned) organization, where
    /// `organizationName` is the synthesized "<email>'s Organization" noise
    /// rather than a real team name worth showing.
    #[must_use]
    pub const fn is_personal(self) -> bool {
        matches!(
            self,
            Self::Free | Self::Pro | Self::Max | Self::Max5x | Self::Max20x
        )
    }
}

/// Plan facts for one account slot.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlanInfo {
    pub tier: PlanTier,
    /// [`PlanTier::label`], carried so shells don't re-implement the mapping.
    pub label: String,
    /// True when the tier names a personal plan (org name is synthesized).
    pub personal: bool,

    // Raw source strings — kept for diagnostics and for tiers we can't yet name.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub subscription_type: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rate_limit_tier: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub organization_type: Option<String>,

    /// `stripe_subscription` / `google_play_subscription` / …
    #[serde(skip_serializing_if = "Option::is_none")]
    pub billing_type: Option<String>,
    /// ISO-8601 start of the current subscription. **Not** a renewal date.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub subscription_created_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub account_created_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub trial_ends_at: Option<String>,
    /// Pay-as-you-go extra-usage credits enabled on the account.
    pub extra_usage_enabled: bool,

    #[serde(skip_serializing_if = "Option::is_none")]
    pub organization_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub organization_role: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub workspace_role: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub seat_tier: Option<String>,

    /// Epoch millis when Claude Code last refreshed this profile (staleness).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub profile_fetched_at: Option<i64>,
}

impl PlanInfo {
    /// Whether anything beyond a bare `Unknown` tier was resolved.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.tier == PlanTier::Unknown
            && self.billing_type.is_none()
            && self.subscription_created_at.is_none()
            && self.account_created_at.is_none()
            && self.organization_name.is_none()
            && self.seat_tier.is_none()
    }

    fn finish(mut self) -> Self {
        self.tier = classify(
            self.subscription_type.as_deref(),
            self.rate_limit_tier.as_deref(),
            self.organization_type.as_deref(),
        );
        self.label = self.tier.label().to_string();
        self.personal = self.tier.is_personal();
        self
    }
}

/// Non-empty trimmed string at `key` (JSON `null` and `""` → `None`).
fn str_field(v: &Value, key: &str) -> Option<String> {
    let s = v.get(key)?.as_str()?.trim();
    if s.is_empty() {
        None
    } else {
        Some(s.to_string())
    }
}

/// Resolve the tier from the three fields that can name it.
///
/// `rateLimitTier` is the only one that distinguishes Max 5× from Max 20×, so
/// it refines; `subscriptionType` names the base tier; `organizationType`
/// (`claude_pro` / `claude_max` / …) is the fallback when a slot's credential
/// predates those fields. Matching is by substring so an unrecognized prefix
/// still classifies instead of silently degrading to `Unknown`.
///
/// Verified against real Pro accounts (`pro` / `default_claude_ai` /
/// `claude_pro`). The Max/Team/Enterprise strings are best-effort until a real
/// account of each is observed — an unmatched value lands on `Unknown`, which
/// renders no badge rather than a wrong one.
#[must_use]
pub fn classify(
    subscription_type: Option<&str>,
    rate_limit_tier: Option<&str>,
    organization_type: Option<&str>,
) -> PlanTier {
    let sub = subscription_type.unwrap_or_default().to_ascii_lowercase();
    let rate = rate_limit_tier.unwrap_or_default().to_ascii_lowercase();
    let org = organization_type.unwrap_or_default().to_ascii_lowercase();

    // Multiplier only ever appears in the rate-limit tier.
    let max_variant = if rate.contains("20x") {
        Some(PlanTier::Max20x)
    } else if rate.contains("5x") {
        Some(PlanTier::Max5x)
    } else {
        None
    };

    let base = tier_from_token(&sub)
        .or_else(|| tier_from_token(&org))
        // `default_claude_ai` names no tier; only trust rate for team/ent/max.
        .or_else(|| tier_from_token(&rate));

    match (base, max_variant) {
        (Some(PlanTier::Max) | None, Some(v)) => v,
        (Some(t), _) => t,
        (None, None) => PlanTier::Unknown,
    }
}

/// Map a `subscriptionType` / `organizationType` value onto a tier.
fn tier_from_token(s: &str) -> Option<PlanTier> {
    if s.is_empty() {
        return None;
    }
    // Order matters: check the specific names before the broad ones.
    if s.contains("enterprise") {
        Some(PlanTier::Enterprise)
    } else if s.contains("team") {
        Some(PlanTier::Team)
    } else if s.contains("max") {
        Some(PlanTier::Max)
    } else if s.contains("pro") {
        Some(PlanTier::Pro)
    } else if s.contains("free") {
        Some(PlanTier::Free)
    } else {
        None
    }
}

/// Plan facts carried by a credential blob (`claudeAiOauth`).
#[must_use]
pub fn plan_from_credentials(credentials: &str) -> PlanInfo {
    if crate::credentials::looks_like_api_key(credentials) {
        return PlanInfo {
            tier: PlanTier::ApiKey,
            label: PlanTier::ApiKey.label().to_string(),
            personal: false,
            ..PlanInfo::default()
        };
    }
    let Ok(v) = serde_json::from_str::<Value>(credentials) else {
        return PlanInfo::default();
    };
    let Some(oauth) = v.get("claudeAiOauth") else {
        return PlanInfo::default();
    };
    PlanInfo {
        subscription_type: str_field(oauth, "subscriptionType"),
        rate_limit_tier: str_field(oauth, "rateLimitTier"),
        ..PlanInfo::default()
    }
    .finish()
}

/// Plan facts carried by a `.claude.json` snapshot (`oauthAccount`).
#[must_use]
pub fn plan_from_config(config_json: &str) -> PlanInfo {
    let Ok(v) = serde_json::from_str::<Value>(config_json) else {
        return PlanInfo::default();
    };
    let Some(acct) = v.get("oauthAccount") else {
        return PlanInfo::default();
    };
    PlanInfo {
        organization_type: str_field(acct, "organizationType"),
        rate_limit_tier: str_field(acct, "organizationRateLimitTier")
            .or_else(|| str_field(acct, "userRateLimitTier")),
        billing_type: str_field(acct, "billingType"),
        subscription_created_at: str_field(acct, "subscriptionCreatedAt"),
        account_created_at: str_field(acct, "accountCreatedAt"),
        trial_ends_at: str_field(acct, "claudeCodeTrialEndsAt"),
        extra_usage_enabled: acct
            .get("hasExtraUsageEnabled")
            .and_then(Value::as_bool)
            .unwrap_or(false),
        organization_name: str_field(acct, "organizationName"),
        organization_role: str_field(acct, "organizationRole"),
        workspace_role: str_field(acct, "workspaceRole"),
        seat_tier: str_field(acct, "seatTier"),
        profile_fetched_at: acct.get("profileFetchedAt").and_then(Value::as_i64),
        ..PlanInfo::default()
    }
    .finish()
}

/// Merge what the credential knows with what the config snapshot knows.
///
/// The credential wins on tier naming (it is rewritten on every token refresh,
/// so it tracks an upgrade sooner than the config snapshot); the config
/// contributes every field the credential does not carry. `None` when neither
/// side yielded anything worth showing.
#[must_use]
pub fn merge_plan(from_creds: PlanInfo, from_config: PlanInfo) -> Option<PlanInfo> {
    if from_creds.tier == PlanTier::ApiKey {
        return Some(from_creds);
    }
    let mut merged = from_config;
    merged.subscription_type = from_creds.subscription_type;
    if from_creds.rate_limit_tier.is_some() {
        merged.rate_limit_tier = from_creds.rate_limit_tier;
    }
    let merged = merged.finish();
    if merged.is_empty() {
        None
    } else {
        Some(merged)
    }
}

/// Plan facts for a slot from its stored credential + config backup.
#[must_use]
pub fn plan_for_slot(credentials: Option<&str>, config_json: Option<&str>) -> Option<PlanInfo> {
    merge_plan(
        credentials.map(plan_from_credentials).unwrap_or_default(),
        config_json.map(plan_from_config).unwrap_or_default(),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn pro_cred() -> String {
        json!({
            "claudeAiOauth": {
                "accessToken": "at",
                "refreshToken": "rt",
                "subscriptionType": "pro",
                "rateLimitTier": "default_claude_ai",
            }
        })
        .to_string()
    }

    /// Shape observed on a real Pro account (2026-08-05).
    fn pro_config() -> String {
        json!({
            "oauthAccount": {
                "emailAddress": "a@x.com",
                "accountUuid": "u",
                "organizationUuid": "o",
                "hasExtraUsageEnabled": false,
                "billingType": "google_play_subscription",
                "accountCreatedAt": "2026-07-14T02:17:33.007131Z",
                "subscriptionCreatedAt": "2026-07-25T09:57:13.092763Z",
                "claudeCodeTrialEndsAt": null,
                "seatTier": null,
                "displayName": "someone",
                "profileFetchedAt": 1_785_899_866_810i64,
                "organizationRole": "admin",
                "workspaceRole": null,
                "organizationName": "a@x.com's Organization",
                "organizationType": "claude_pro",
                "organizationRateLimitTier": "default_claude_ai",
                "userRateLimitTier": null,
            }
        })
        .to_string()
    }

    #[test]
    fn real_pro_account_resolves() {
        let p = plan_for_slot(Some(&pro_cred()), Some(&pro_config())).unwrap();
        assert_eq!(p.tier, PlanTier::Pro);
        assert_eq!(p.label, "Pro");
        assert!(p.personal);
        assert_eq!(
            p.subscription_created_at.as_deref(),
            Some("2026-07-25T09:57:13.092763Z")
        );
        assert_eq!(p.billing_type.as_deref(), Some("google_play_subscription"));
        assert_eq!(p.organization_role.as_deref(), Some("admin"));
        assert_eq!(p.profile_fetched_at, Some(1_785_899_866_810));
        assert!(!p.extra_usage_enabled);
        // Nulls must not become empty strings.
        assert_eq!(p.seat_tier, None);
        assert_eq!(p.trial_ends_at, None);
        assert_eq!(p.workspace_role, None);
    }

    #[test]
    fn max_multiplier_comes_from_rate_limit_tier() {
        assert_eq!(
            classify(
                Some("max"),
                Some("default_claude_max_20x"),
                Some("claude_max")
            ),
            PlanTier::Max20x
        );
        assert_eq!(
            classify(
                Some("max"),
                Some("default_claude_max_5x"),
                Some("claude_max")
            ),
            PlanTier::Max5x
        );
        // Max with no multiplier reported stays plain Max, never a guessed 5×.
        assert_eq!(
            classify(Some("max"), Some("default_claude_ai"), None),
            PlanTier::Max
        );
        assert_eq!(PlanTier::Max20x.label(), "Max 20×");
    }

    #[test]
    fn team_and_enterprise_classify_and_are_not_personal() {
        assert_eq!(
            classify(Some("team"), None, Some("claude_team")),
            PlanTier::Team
        );
        assert_eq!(
            classify(None, None, Some("claude_enterprise")),
            PlanTier::Enterprise
        );
        assert!(!PlanTier::Team.is_personal());
        assert!(!PlanTier::Enterprise.is_personal());
        assert!(PlanTier::Pro.is_personal());
    }

    #[test]
    fn config_only_slot_still_names_the_tier() {
        // Credential predating subscriptionType: config carries the tier.
        let cred = json!({"claudeAiOauth": {"accessToken": "at"}}).to_string();
        let p = plan_for_slot(Some(&cred), Some(&pro_config())).unwrap();
        assert_eq!(p.tier, PlanTier::Pro);
        assert_eq!(p.rate_limit_tier.as_deref(), Some("default_claude_ai"));
    }

    #[test]
    fn api_key_slot_is_its_own_tier() {
        let p = plan_for_slot(Some("sk-ant-api03-abcdefghijklmnop"), None).unwrap();
        assert_eq!(p.tier, PlanTier::ApiKey);
        assert_eq!(p.label, "API Key");
        assert!(p.subscription_created_at.is_none());
    }

    #[test]
    fn unknown_tier_reports_no_label() {
        assert_eq!(
            classify(None, Some("default_claude_ai"), None),
            PlanTier::Unknown
        );
        assert_eq!(PlanTier::Unknown.label(), "");
        // Nothing parseable at all → no plan rather than an empty badge.
        assert!(plan_for_slot(Some("not json"), Some("{}")).is_none());
        assert!(plan_for_slot(None, None).is_none());
    }

    #[test]
    fn config_facts_survive_an_unnamed_tier() {
        // Tier unresolvable, but the dates are still facts worth showing.
        let cfg = json!({
            "oauthAccount": {
                "billingType": "stripe_subscription",
                "subscriptionCreatedAt": "2026-05-24T05:45:13.496010Z",
            }
        })
        .to_string();
        let p = plan_for_slot(None, Some(&cfg)).unwrap();
        assert_eq!(p.tier, PlanTier::Unknown);
        assert_eq!(p.label, "");
        assert_eq!(
            p.subscription_created_at.as_deref(),
            Some("2026-05-24T05:45:13.496010Z")
        );
    }

    #[test]
    fn credential_tier_overrides_a_stale_config_snapshot() {
        // Upgraded to Max but the slot's config snapshot still says Pro.
        let cred = json!({
            "claudeAiOauth": {
                "subscriptionType": "max",
                "rateLimitTier": "default_claude_max_5x",
            }
        })
        .to_string();
        let p = plan_for_slot(Some(&cred), Some(&pro_config())).unwrap();
        assert_eq!(p.tier, PlanTier::Max5x);
        // Config-only facts still come through.
        assert_eq!(p.billing_type.as_deref(), Some("google_play_subscription"));
    }

    #[test]
    fn json_projection_is_camel_case() {
        let p = plan_for_slot(Some(&pro_cred()), Some(&pro_config())).unwrap();
        let v = serde_json::to_value(&p).unwrap();
        assert_eq!(v["tier"], "pro");
        assert_eq!(v["label"], "Pro");
        assert_eq!(v["personal"], true);
        assert!(v["subscriptionCreatedAt"].is_string());
        assert!(v["extraUsageEnabled"].is_boolean());
        // Absent optionals are omitted, not null.
        assert!(v.get("seatTier").is_none());
    }
}
