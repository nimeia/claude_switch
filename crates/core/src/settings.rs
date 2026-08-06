//! Tool settings at `<backup_root>/settings.json` (ref: `settings.py`).

use std::path::Path;

use serde::{Deserialize, Serialize};

use crate::errors::{Error, Result};
use crate::fsutil::atomic_write;

pub const SETTINGS_SCHEMA_VERSION: u32 = 1;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutoSwitchSettings {
    #[serde(default = "default_threshold")]
    pub threshold: f64,
    #[serde(default = "default_interval")]
    pub interval_seconds: f64,
    #[serde(default = "default_cooldown")]
    pub cooldown_seconds: f64,
    #[serde(default = "default_hysteresis")]
    pub hysteresis_pct: f64,
    /// Wire kebab: `best` | `consume-first`
    #[serde(default = "default_strategy")]
    pub strategy: String,
    #[serde(default)]
    pub include_api_key_accounts: bool,
    #[serde(default = "default_unhealthy")]
    pub unhealthy_ticks: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub model: Option<String>,
    /// GUI/engine: user wants autoswitch running.
    #[serde(default)]
    pub enabled: bool,
}

fn default_threshold() -> f64 {
    90.0
}
fn default_interval() -> f64 {
    60.0
}
fn default_cooldown() -> f64 {
    300.0
}
fn default_hysteresis() -> f64 {
    10.0
}
fn default_strategy() -> String {
    "best".into()
}
fn default_unhealthy() -> u32 {
    3
}

impl Default for AutoSwitchSettings {
    fn default() -> Self {
        Self {
            threshold: default_threshold(),
            interval_seconds: default_interval(),
            cooldown_seconds: default_cooldown(),
            hysteresis_pct: default_hysteresis(),
            strategy: default_strategy(),
            include_api_key_accounts: false,
            unhealthy_ticks: default_unhealthy(),
            model: None,
            enabled: false,
        }
    }
}

impl AutoSwitchSettings {
    #[must_use]
    pub fn clamp(mut self) -> Self {
        self.threshold = self.threshold.clamp(50.0, 99.9);
        self.interval_seconds = self.interval_seconds.clamp(15.0, 3600.0);
        self.cooldown_seconds = self.cooldown_seconds.clamp(0.0, 86_400.0);
        self.hysteresis_pct = self.hysteresis_pct.clamp(0.0, 50.0);
        if self.strategy != "best" && self.strategy != "consume-first" {
            self.strategy = "best".into();
        }
        self.unhealthy_ticks = self.unhealthy_ticks.clamp(1, 100);
        self
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UiSettings {
    #[serde(default = "default_theme")]
    pub theme: String,
}

fn default_theme() -> String {
    "auto".into()
}

impl Default for UiSettings {
    fn default() -> Self {
        Self {
            theme: default_theme(),
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    #[serde(default = "default_schema")]
    pub schema_version: u32,
    #[serde(default)]
    pub autoswitch: AutoSwitchSettings,
    #[serde(default)]
    pub ui: UiSettings,
}

fn default_schema() -> u32 {
    SETTINGS_SCHEMA_VERSION
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            schema_version: SETTINGS_SCHEMA_VERSION,
            autoswitch: AutoSwitchSettings::default(),
            ui: UiSettings::default(),
        }
    }
}

impl Settings {
    pub fn load(path: &Path) -> Result<Self> {
        if !path.exists() {
            return Ok(Self::default());
        }
        let text = std::fs::read_to_string(path).map_err(Error::Io)?;
        let mut s: Settings = serde_json::from_str(&text).unwrap_or_default();
        s.autoswitch = s.autoswitch.clamp();
        s.schema_version = SETTINGS_SCHEMA_VERSION;
        Ok(s)
    }

    pub fn save(&self, path: &Path) -> Result<()> {
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(Error::Io)?;
        }
        let mut s = self.clone();
        s.autoswitch = s.autoswitch.clamp();
        s.schema_version = SETTINGS_SCHEMA_VERSION;
        let text = serde_json::to_string_pretty(&s)
            .map_err(|e| Error::Internal(format!("settings serialize: {e}")))?;
        atomic_write(path, text.as_bytes()).map_err(Error::Io)?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clamp_strategy() {
        let s = AutoSwitchSettings {
            strategy: "nope".into(),
            threshold: 200.0,
            ..Default::default()
        };
        let c = s.clamp();
        assert_eq!(c.strategy, "best");
        assert!((c.threshold - 99.9).abs() < f64::EPSILON);
    }

    #[test]
    fn roundtrip() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("settings.json");
        let mut s = Settings::default();
        s.autoswitch.enabled = true;
        s.autoswitch.threshold = 80.0;
        s.save(&path).unwrap();
        let loaded = Settings::load(&path).unwrap();
        assert!(loaded.autoswitch.enabled);
        assert!((loaded.autoswitch.threshold - 80.0).abs() < f64::EPSILON);
    }
}
