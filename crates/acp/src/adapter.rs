//! Locating and launching the `claude-agent-acp` adapter subprocess.
//!
//! The adapter is a Node program (`node >= 22`) that translates ACP into the
//! Claude Agent SDK. Two environment facts decide whether a launched session
//! works at all, and both were established empirically:
//!
//! - **Proxy must be injected explicitly.** Claude Code's own CLI picks up the
//!   Windows Internet Settings proxy; the Agent SDK under the adapter does not.
//!   On a proxied network the adapter fails its first API call with
//!   `403 "Request not allowed"`, which reads as an auth failure and is nothing
//!   of the sort (the same trap [`claude_switch_core::proxy`] documents). We
//!   resolve the proxy the same way core does and set `HTTPS_PROXY`/`HTTP_PROXY`
//!   on the child.
//! - **`CLAUDE_CONFIG_DIR` is honoured.** Pointing it at a session profile makes
//!   the adapter authenticate as that stored account and write its transcripts
//!   into the profile — verified by a probe run landing
//!   `projects/<encoded-cwd>/` inside the profile rather than `~/.claude`.
//!   Account isolation therefore survives the ACP hop.

use std::collections::BTreeMap;
use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

use claude_switch_core::{proxy, session};

/// Host reached for inference; what the proxy decision is made against.
const API_HOST: &str = "api.anthropic.com";

/// npm package providing the adapter binary.
pub const ADAPTER_PACKAGE: &str = "@agentclientprotocol/claude-agent-acp";

/// Relative path of the adapter entry point inside its package.
const ADAPTER_ENTRY: [&str; 2] = ["dist", "index.js"];

#[derive(Debug)]
pub enum AdapterError {
    NodeNotFound,
    AdapterNotFound { searched: Vec<PathBuf> },
    WorkDirMissing(PathBuf),
    Spawn(std::io::Error),
}

impl std::fmt::Display for AdapterError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::NodeNotFound => write!(f, "node executable not found on PATH (node >= 22 required)"),
            Self::AdapterNotFound { searched } => {
                write!(f, "{ADAPTER_PACKAGE} not found; searched:")?;
                for p in searched {
                    write!(f, "\n  {}", p.display())?;
                }
                Ok(())
            }
            Self::WorkDirMissing(p) => write!(f, "working directory does not exist: {}", p.display()),
            Self::Spawn(e) => write!(f, "spawning adapter: {e}"),
        }
    }
}

impl std::error::Error for AdapterError {}

/// How to launch one adapter process.
#[derive(Clone, Debug)]
pub struct AdapterConfig {
    /// Directory the agent treats as the project root.
    pub work_dir: PathBuf,
    /// Session profile for `CLAUDE_CONFIG_DIR`; `None` uses the default login.
    pub config_dir: Option<PathBuf>,
    /// Explicit adapter entry point. When `None`, [`find_adapter`] searches.
    pub adapter_js: Option<PathBuf>,
    /// Explicit `node` binary. When `None`, PATH is searched.
    pub node_exe: Option<PathBuf>,
    /// Extra environment for the child (applied last, so it can override).
    pub extra_env: BTreeMap<String, String>,
    /// Skip proxy injection. Only useful when the caller already set it.
    pub skip_proxy: bool,
}

impl AdapterConfig {
    #[must_use]
    pub fn new(work_dir: impl Into<PathBuf>) -> Self {
        Self {
            work_dir: work_dir.into(),
            config_dir: None,
            adapter_js: None,
            node_exe: None,
            extra_env: BTreeMap::new(),
            skip_proxy: false,
        }
    }

    #[must_use]
    pub fn config_dir(mut self, dir: impl Into<PathBuf>) -> Self {
        self.config_dir = Some(dir.into());
        self
    }

    /// Build the launch command without spawning it (also used by tests).
    pub fn to_command(&self) -> Result<Command, AdapterError> {
        if !self.work_dir.is_dir() {
            return Err(AdapterError::WorkDirMissing(self.work_dir.clone()));
        }
        let node = match &self.node_exe {
            Some(p) => p.clone(),
            None => find_on_path("node").ok_or(AdapterError::NodeNotFound)?,
        };
        let entry = match &self.adapter_js {
            Some(p) => p.clone(),
            None => find_adapter()?,
        };

        let mut cmd = Command::new(node);
        cmd.arg(entry)
            .current_dir(&self.work_dir)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());

        // An inherited API key would silently override the account this launch
        // names — the one thing an account-scoped launch must not allow.
        for key in session::AUTH_OVERRIDE_ENV_VARS {
            cmd.env_remove(key);
        }

        if let Some(dir) = &self.config_dir {
            cmd.env("CLAUDE_CONFIG_DIR", dir);
        }

        if !self.skip_proxy {
            if let Some(url) = proxy::resolve_for(API_HOST) {
                // Node's fetch honours the lowercase forms too; set both so the
                // adapter and anything it shells out to agree.
                cmd.env("HTTPS_PROXY", &url);
                cmd.env("HTTP_PROXY", &url);
                cmd.env("https_proxy", &url);
                cmd.env("http_proxy", &url);
            }
        }

        for (k, v) in &self.extra_env {
            cmd.env(k, v);
        }

        Ok(cmd)
    }

    /// Proxy URL this config would inject, for diagnostics.
    #[must_use]
    pub fn resolved_proxy(&self) -> Option<String> {
        if self.skip_proxy {
            None
        } else {
            proxy::resolve_for(API_HOST)
        }
    }
}

/// Search the usual places for the adapter's `dist/index.js`.
///
/// Order: `CLAUDE_SWITCH_ACP_ADAPTER` override, the repo-local
/// `tools/acp/node_modules` used by the spike, then global npm roots.
pub fn find_adapter() -> Result<PathBuf, AdapterError> {
    let mut searched = Vec::new();

    if let Some(raw) = std::env::var_os("CLAUDE_SWITCH_ACP_ADAPTER") {
        let p = PathBuf::from(raw);
        if p.is_file() {
            return Ok(p);
        }
        searched.push(p);
    }

    for root in candidate_module_roots() {
        let p = package_entry(&root);
        if p.is_file() {
            return Ok(p);
        }
        searched.push(p);
    }

    Err(AdapterError::AdapterNotFound { searched })
}

fn package_entry(node_modules: &Path) -> PathBuf {
    let mut p = node_modules.to_path_buf();
    for seg in ADAPTER_PACKAGE.split('/') {
        p.push(seg);
    }
    for seg in ADAPTER_ENTRY {
        p.push(seg);
    }
    p
}

/// `node_modules` directories worth searching, most specific first.
fn candidate_module_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();

    // Walk up from the current directory: covers `tools/acp/node_modules` when
    // run from anywhere inside the repo.
    if let Ok(cwd) = std::env::current_dir() {
        let mut dir = Some(cwd.as_path());
        while let Some(d) = dir {
            roots.push(d.join("tools").join("acp").join("node_modules"));
            roots.push(d.join("node_modules"));
            dir = d.parent();
        }
    }

    // Global npm prefix.
    if let Some(home) = home_dir() {
        roots.push(home.join("AppData").join("Roaming").join("npm").join("node_modules"));
        roots.push(home.join(".npm-global").join("lib").join("node_modules"));
        roots.push(home.join(".local").join("lib").join("node_modules"));
    }
    if let Some(prefix) = std::env::var_os("NPM_CONFIG_PREFIX") {
        let p = PathBuf::from(prefix);
        roots.push(p.join("node_modules"));
        roots.push(p.join("lib").join("node_modules"));
    }

    roots
}

fn home_dir() -> Option<PathBuf> {
    std::env::var_os("USERPROFILE")
        .or_else(|| std::env::var_os("HOME"))
        .map(PathBuf::from)
}

/// PATH lookup honouring `PATHEXT` on Windows.
#[must_use]
pub fn find_on_path(command: &str) -> Option<PathBuf> {
    let path = std::env::var_os("PATH")?;
    let exts: Vec<OsString> = if cfg!(windows) {
        std::env::var("PATHEXT")
            .unwrap_or_else(|_| ".COM;.EXE;.BAT;.CMD".into())
            .split(';')
            .filter(|s| !s.is_empty())
            .map(OsString::from)
            .collect()
    } else {
        vec![OsString::new()]
    };
    for dir in std::env::split_paths(&path) {
        for ext in &exts {
            let mut name = OsString::from(command);
            name.push(ext);
            let candidate = dir.join(&name);
            if candidate.is_file() {
                return Some(candidate);
            }
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn package_entry_splits_scoped_name() {
        let p = package_entry(Path::new("/root/node_modules"));
        let s = p.to_string_lossy().replace('\\', "/");
        assert!(
            s.ends_with("node_modules/@agentclientprotocol/claude-agent-acp/dist/index.js"),
            "unexpected entry path: {s}"
        );
    }

    #[test]
    fn missing_work_dir_is_rejected_before_spawn() {
        let cfg = AdapterConfig::new("/definitely/not/a/real/dir/zzz");
        match cfg.to_command() {
            Err(AdapterError::WorkDirMissing(_)) => {}
            other => panic!("expected WorkDirMissing, got {other:?}"),
        }
    }

    #[test]
    fn candidate_roots_include_repo_local_tools_dir() {
        let roots = candidate_module_roots();
        assert!(
            roots.iter().any(|r| r.ends_with("tools/acp/node_modules")
                || r.to_string_lossy().replace('\\', "/").ends_with("tools/acp/node_modules")),
            "repo-local adapter root missing from search list"
        );
    }
}
