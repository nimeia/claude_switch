//! Uninstall Claude Code: the program, its runtime files, and optionally
//! this app's imported-account backups.
//!
//! Distinct from [`crate::switcher::Switcher::remove_account`]: that drops one
//! managed slot. This removes the product from the machine. A caller picks
//! whether the backup tree stays so a later reinstall can switch back into
//! those accounts.
//!
//! Every deletion is a [`plan`] first. [`apply`] derives the plan again rather
//! than trusting paths a shell was shown. Roots are a whitelist; a name that
//! happens to contain "claude" is not enough.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::cleanup;
use crate::errors::{Error, Result};
use crate::paths::PathEnv;
use crate::sequence::SequenceData;
use crate::session;

#[cfg(windows)]
const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;

const GROUP_PROGRAM: &str = "program";
const GROUP_RUNTIME: &str = "runtime";
const GROUP_IDE: &str = "ide";
const GROUP_PROJECT: &str = "project-locals";
const GROUP_DESKTOP: &str = "desktop";
const GROUP_THIRD: &str = "third-party";
const GROUP_MANAGED: &str = "managed";
const GROUP_ACCOUNTS: &str = "accounts";

const RELOC_SUFFIX: &str = ".reloc-backup";
const EXT_PREFIX: &str = "anthropic.claude-code";

// --- options / snapshots ---------------------------------------------------------

/// What a caller can turn on or off. Program + CLI runtime are not optional.
///
/// Independent checkboxes, not a state machine — each flag is one optional
/// group in the dialog.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[allow(clippy::struct_excessive_bools)]
#[serde(rename_all = "camelCase", default)]
pub struct PurgeOptions {
    /// VS Code / Cursor / Windsurf `anthropic.claude-code-*` extensions.
    pub remove_ide_extension: bool,
    /// `CLAUDE.local.md` and `.claude/settings.local.json` in known projects.
    pub remove_project_locals: bool,
    /// Claude Desktop nested Code data and device IDs — not the Desktop app.
    pub remove_desktop_nested: bool,
    /// Third-party shells (`Claude-3p`, `Claude Nest-3p`).
    pub remove_third_party_shells: bool,
    /// Enterprise `ProgramData\ClaudeCode` managed settings, if present.
    pub remove_managed: bool,
    /// This app's backup tree (imported accounts).
    pub remove_imported_accounts: bool,
}

impl Default for PurgeOptions {
    fn default() -> Self {
        Self {
            remove_ide_extension: true,
            remove_project_locals: false,
            remove_desktop_nested: false,
            remove_third_party_shells: false,
            remove_managed: false,
            remove_imported_accounts: false,
        }
    }
}

/// One filesystem root the whitelist named.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PurgeItem {
    pub id: String,
    pub group: String,
    pub path: String,
    pub bytes: u64,
    pub exists: bool,
    /// File vs directory vs junction, for the preview.
    pub kind: String,
}

/// Items that share a toggle (or are always removed).
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PurgeGroup {
    pub id: String,
    pub required: bool,
    pub bytes: u64,
    pub items: Vec<PurgeItem>,
}

/// What is on the machine, before the user picks options.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PurgeScan {
    /// `native` / `npm` / `both` / `none`.
    pub install_kind: String,
    pub claude_running: bool,
    pub live_sessions: u32,
    pub imported_accounts: u32,
    pub groups: Vec<PurgeGroup>,
}

/// What [`apply`] would delete for these options.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PurgePlan {
    pub problems: Vec<String>,
    pub warnings: Vec<String>,
    pub items: Vec<PurgeItem>,
    pub total_bytes: u64,
    pub live_sessions: u32,
    pub claude_running: bool,
    pub imported_accounts: u32,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PurgeAction {
    pub id: String,
    pub path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

/// What [`apply`] actually did.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PurgeOutcome {
    pub deleted: Vec<PurgeAction>,
    pub skipped: Vec<PurgeAction>,
    pub failed: Vec<PurgeAction>,
    pub bytes: u64,
}

// --- public API ------------------------------------------------------------------

/// Inventory of whitelist roots under `env`.
#[must_use]
pub fn scan(env: &PathEnv) -> PurgeScan {
    let fs = PurgeFs::from_env(env);
    let candidates = catalog(&fs);
    let groups = group_existing(&candidates);
    let native = candidates
        .iter()
        .any(|c| c.id == "native-bin" && exists(&c.path));
    let npm = candidates
        .iter()
        .any(|c| c.id.starts_with("npm-") && exists(&c.path));
    let install_kind = match (native, npm) {
        (true, true) => "both",
        (true, false) => "native",
        (false, true) => "npm",
        (false, false) => "none",
    };
    PurgeScan {
        install_kind: install_kind.into(),
        claude_running: claude_bin_in_use(&candidates),
        live_sessions: live_session_count(env),
        imported_accounts: imported_account_count(&fs),
        groups,
    }
}

/// What would be deleted for `opts`. Problems block apply; warnings do not.
#[must_use]
pub fn plan(env: &PathEnv, opts: &PurgeOptions) -> PurgePlan {
    let fs = PurgeFs::from_env(env);
    let selected = selected_candidates(&fs, opts);
    let items: Vec<PurgeItem> = selected
        .iter()
        .filter(|c| exists(&c.path))
        .map(to_item)
        .collect();
    let total_bytes = items
        .iter()
        .map(|i| i.bytes)
        .fold(0u64, u64::saturating_add);
    let live_sessions = live_session_count(env);
    let claude_running = claude_bin_in_use(&catalog(&fs));
    let imported_accounts = imported_account_count(&fs);

    let mut problems = Vec::new();
    if live_sessions > 0 {
        problems.push("live-sessions".into());
    }
    if claude_running {
        problems.push("claude-running".into());
    }
    if items.is_empty() {
        problems.push("nothing".into());
    }
    problems.sort();
    problems.dedup();

    let mut warnings = Vec::new();
    if imported_accounts > 0 && !opts.remove_imported_accounts {
        warnings.push("accounts-kept-identity-remains".into());
    }
    let desktop_present = catalog(&fs)
        .iter()
        .any(|c| c.group == GROUP_DESKTOP && exists(&c.path));
    if desktop_present && !opts.remove_desktop_nested {
        warnings.push("desktop-left-installed".into());
    }
    if items
        .iter()
        .any(|i| i.id == "global-config" || i.id == "config-home")
    {
        warnings.push("machine-id-may-regenerate".into());
    }

    PurgePlan {
        problems,
        warnings,
        items,
        total_bytes,
        live_sessions,
        claude_running,
        imported_accounts,
    }
}

/// Delete what [`plan`] selects. Re-plans so a stale preview cannot name a path
/// the whitelist no longer owns.
///
/// # Errors
///
/// [`Error::SessionInUse`] when Claude Code is running against a config home
/// or the launcher we would delete is locked. [`Error::Validation`] when the
/// plan has nothing to do.
pub fn apply(env: &PathEnv, opts: &PurgeOptions) -> Result<PurgeOutcome> {
    let planned = plan(env, opts);
    if let Some(code) = planned.problems.first() {
        return Err(apply_problem(code, &planned));
    }
    let fs = PurgeFs::from_env(env);
    let selected = selected_candidates(&fs, opts);
    let mut out = PurgeOutcome {
        deleted: Vec::new(),
        skipped: Vec::new(),
        failed: Vec::new(),
        bytes: 0,
    };
    for cand in selected {
        if !exists(&cand.path) {
            out.skipped.push(action(&cand, Some("missing")));
            continue;
        }
        if is_protected(&fs, &cand.path) {
            out.failed.push(action(&cand, Some("protected")));
            continue;
        }
        let bytes = item_bytes(&cand);
        match remove_candidate(&cand) {
            Ok(()) => {
                out.bytes = out.bytes.saturating_add(bytes);
                out.deleted.push(action(&cand, None));
            }
            Err(e) => out.failed.push(action(&cand, Some(&e.to_string()))),
        }
    }
    Ok(out)
}

fn apply_problem(code: &str, plan: &PurgePlan) -> Error {
    match code {
        "live-sessions" => Error::SessionInUse(format!(
            "{} live Claude Code session(s)",
            plan.live_sessions
        )),
        "claude-running" => Error::SessionInUse(
            "Claude Code is running; close it before removing the install".into(),
        ),
        other => Error::Validation(format!("purge: {other}")),
    }
}

// --- catalogue -------------------------------------------------------------------

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Opt {
    Ide,
    Project,
    Desktop,
    Third,
    Managed,
    Accounts,
}

#[derive(Clone, Debug)]
struct Candidate {
    id: String,
    group: &'static str,
    option: Option<Opt>,
    path: PathBuf,
    follow_link: bool,
}

struct PurgeFs {
    env: PathEnv,
    roaming: PathBuf,
    local: PathBuf,
    temp: PathBuf,
    program_data: Vec<PathBuf>,
    allow_machine_stores: bool,
}

impl PurgeFs {
    fn from_env(env: &PathEnv) -> Self {
        let roaming = env.home.join("AppData").join("Roaming");
        let local = env.home.join("AppData").join("Local");
        let temp = local.join("Temp");
        let real_home = PathEnv::from_process().home;
        let allow_machine_stores = paths_equal(&env.home, &real_home);
        let mut program_data = vec![env.home.join("ProgramData")];
        if allow_machine_stores {
            if let Some(pd) = std::env::var_os("PROGRAMDATA") {
                let p = PathBuf::from(pd);
                if p.is_absolute() && !paths_equal(&p, &program_data[0]) {
                    program_data.push(p);
                }
            }
        }
        Self {
            env: env.clone(),
            roaming,
            local,
            temp,
            program_data,
            allow_machine_stores,
        }
    }

    fn backup_root(&self) -> PathBuf {
        self.env.backup_root()
    }
}

#[allow(clippy::too_many_lines)]
fn catalog(fs: &PurgeFs) -> Vec<Candidate> {
    let mut out = Vec::new();
    let home = &fs.env.home;
    let config_home = fs.env.claude_config_home();
    let global = fs.env.global_config_path();
    let default_global = fs.env.default_global_config_path();

    // A. Program
    push(
        &mut out,
        "native-bin",
        GROUP_PROGRAM,
        None,
        home.join(".local").join("bin").join(claude_bin_name()),
        false,
    );
    push(
        &mut out,
        "native-versions",
        GROUP_PROGRAM,
        None,
        home.join(".local").join("share").join("claude"),
        false,
    );
    push(
        &mut out,
        "native-state",
        GROUP_PROGRAM,
        None,
        home.join(".local").join("state").join("claude"),
        false,
    );
    let npm = fs.roaming.join("npm");
    for (id, name) in [
        ("npm-cmd", "claude.cmd"),
        ("npm-ps1", "claude.ps1"),
        ("npm-bin", "claude"),
    ] {
        push(&mut out, id, GROUP_PROGRAM, None, npm.join(name), false);
    }
    push(
        &mut out,
        "npm-pkg",
        GROUP_PROGRAM,
        None,
        npm.join("node_modules")
            .join("@anthropic-ai")
            .join("claude-code"),
        false,
    );

    // B. Runtime
    push(
        &mut out,
        "config-home",
        GROUP_RUNTIME,
        None,
        config_home.clone(),
        true,
    );
    push(
        &mut out,
        "global-config",
        GROUP_RUNTIME,
        None,
        global.clone(),
        false,
    );
    push(
        &mut out,
        "global-config-bak",
        GROUP_RUNTIME,
        None,
        backup_sibling(&global, ".backup"),
        false,
    );
    if default_global != global {
        push(
            &mut out,
            "default-global-config",
            GROUP_RUNTIME,
            None,
            default_global.clone(),
            false,
        );
        push(
            &mut out,
            "default-global-config-bak",
            GROUP_RUNTIME,
            None,
            backup_sibling(&default_global, ".backup"),
            false,
        );
    }
    let home_json = home.join(".claude.json");
    if home_json != global && home_json != default_global {
        push(
            &mut out,
            "home-claude-json",
            GROUP_RUNTIME,
            None,
            home_json.clone(),
            false,
        );
        push(
            &mut out,
            "home-claude-json-bak",
            GROUP_RUNTIME,
            None,
            backup_sibling(&home_json, ".backup"),
            false,
        );
    }
    push(
        &mut out,
        "cli-nodejs",
        GROUP_RUNTIME,
        None,
        fs.local.join("claude-cli-nodejs"),
        false,
    );
    push(
        &mut out,
        "temp-claude",
        GROUP_RUNTIME,
        None,
        fs.temp.join("claude"),
        false,
    );
    let recent = fs.roaming.join("Microsoft").join("Windows").join("Recent");
    push(
        &mut out,
        "oauth-recent",
        GROUP_RUNTIME,
        None,
        recent.join("https--claude.ai-oauth-device.lnk"),
        false,
    );
    if let Some(extra) = fs.env.claude_config_dir.as_ref() {
        if extra != &config_home {
            push(
                &mut out,
                "config-dir-env",
                GROUP_RUNTIME,
                None,
                extra.clone(),
                true,
            );
        }
    }
    let reloc = reloc_backup(&config_home);
    if reloc != config_home {
        push(
            &mut out,
            "config-home-reloc",
            GROUP_RUNTIME,
            None,
            reloc,
            false,
        );
    }

    if fs.allow_machine_stores {
        for (i, target) in cmdkey_claude_code_targets().into_iter().enumerate() {
            out.push(Candidate {
                id: format!("win-cred-{i}"),
                group: GROUP_RUNTIME,
                option: None,
                path: PathBuf::from(format!("cmdkey:{target}")),
                follow_link: false,
            });
        }
    }

    // C. IDE extensions
    for (id, rel) in [
        ("vscode-ext", ".vscode"),
        ("vscode-insiders-ext", ".vscode-insiders"),
        ("cursor-ext", ".cursor"),
        ("windsurf-ext", ".windsurf"),
    ] {
        let dir = home.join(rel).join("extensions");
        for (n, child) in glob_prefix(&dir, EXT_PREFIX).into_iter().enumerate() {
            out.push(Candidate {
                id: format!("{id}-{n}"),
                group: GROUP_IDE,
                option: Some(Opt::Ide),
                path: child,
                follow_link: false,
            });
        }
    }

    // D. Project locals
    for (n, path) in project_local_files(fs).into_iter().enumerate() {
        out.push(Candidate {
            id: format!("project-local-{n}"),
            group: GROUP_PROJECT,
            option: Some(Opt::Project),
            path,
            follow_link: false,
        });
    }

    // E. Desktop nested
    let desktop = fs.roaming.join("Claude");
    for (id, rel) in [
        ("desktop-nested-code", "claude-code"),
        ("desktop-nested-sessions", "claude-code-sessions"),
        ("desktop-nested-vm", "claude-code-vm"),
        ("desktop-nested-agent", "local-agent-mode-sessions"),
        ("desktop-did", "ant-did"),
        ("desktop-device-registry", "ant-device-registry.json"),
        ("desktop-local-storage", "Local Storage"),
        ("desktop-indexeddb", "IndexedDB"),
    ] {
        push(
            &mut out,
            id,
            GROUP_DESKTOP,
            Some(Opt::Desktop),
            desktop.join(rel),
            false,
        );
    }
    push(
        &mut out,
        "desktop-local-logs",
        GROUP_DESKTOP,
        Some(Opt::Desktop),
        fs.local.join("claude"),
        false,
    );
    for (i, pd) in fs.program_data.iter().enumerate() {
        push(
            &mut out,
            &format!("desktop-programdata-logs-{i}"),
            GROUP_DESKTOP,
            Some(Opt::Desktop),
            pd.join("Claude").join("Logs"),
            false,
        );
    }

    // F. Third-party shells
    push(
        &mut out,
        "third-claude-3p",
        GROUP_THIRD,
        Some(Opt::Third),
        fs.local.join("Claude-3p"),
        false,
    );
    push(
        &mut out,
        "third-claude-nest-3p",
        GROUP_THIRD,
        Some(Opt::Third),
        fs.local.join("Claude Nest-3p"),
        false,
    );

    // G. Managed enterprise settings
    for (i, pd) in fs.program_data.iter().enumerate() {
        push(
            &mut out,
            &format!("managed-{i}"),
            GROUP_MANAGED,
            Some(Opt::Managed),
            pd.join("ClaudeCode"),
            false,
        );
        push(
            &mut out,
            &format!("managed-spaced-{i}"),
            GROUP_MANAGED,
            Some(Opt::Managed),
            pd.join("Claude Code"),
            false,
        );
    }

    // H. Imported accounts
    let backup = fs.backup_root();
    push(
        &mut out,
        "backup-root",
        GROUP_ACCOUNTS,
        Some(Opt::Accounts),
        backup.clone(),
        true,
    );
    let backup_reloc = reloc_backup(&backup);
    if backup_reloc != backup {
        push(
            &mut out,
            "backup-root-reloc",
            GROUP_ACCOUNTS,
            Some(Opt::Accounts),
            backup_reloc,
            false,
        );
    }
    // Never let a candidate name this app's own folder.
    out.retain(|c| {
        let name = c
            .path
            .file_name()
            .map(|n| n.to_string_lossy().to_ascii_lowercase())
            .unwrap_or_default();
        name != "claudeswitch"
    });
    out
}

fn push(
    out: &mut Vec<Candidate>,
    id: &str,
    group: &'static str,
    option: Option<Opt>,
    path: PathBuf,
    follow_link: bool,
) {
    out.push(Candidate {
        id: id.to_string(),
        group,
        option,
        path,
        follow_link,
    });
}

fn selected_candidates(fs: &PurgeFs, opts: &PurgeOptions) -> Vec<Candidate> {
    let mut selected: Vec<Candidate> = catalog(fs)
        .into_iter()
        .filter(|c| option_enabled(c.option, opts))
        .collect();
    selected.sort_by_key(|c| (group_order(c.group), c.id.clone()));
    selected
}

fn option_enabled(option: Option<Opt>, opts: &PurgeOptions) -> bool {
    match option {
        None => true,
        Some(Opt::Ide) => opts.remove_ide_extension,
        Some(Opt::Project) => opts.remove_project_locals,
        Some(Opt::Desktop) => opts.remove_desktop_nested,
        Some(Opt::Third) => opts.remove_third_party_shells,
        Some(Opt::Managed) => opts.remove_managed,
        Some(Opt::Accounts) => opts.remove_imported_accounts,
    }
}

fn group_order(group: &str) -> u8 {
    match group {
        GROUP_RUNTIME => 0,
        GROUP_PROJECT => 1,
        GROUP_DESKTOP => 2,
        GROUP_THIRD => 3,
        GROUP_MANAGED => 4,
        GROUP_IDE => 5,
        GROUP_PROGRAM => 6,
        GROUP_ACCOUNTS => 7,
        _ => 9,
    }
}

fn group_existing(candidates: &[Candidate]) -> Vec<PurgeGroup> {
    let mut order: Vec<&str> = Vec::new();
    for c in candidates {
        if exists(&c.path) && !order.contains(&c.group) {
            order.push(c.group);
        }
    }
    order.sort_by_key(|g| group_order(g));
    order
        .into_iter()
        .map(|gid| {
            let items: Vec<PurgeItem> = candidates
                .iter()
                .filter(|c| c.group == gid && exists(&c.path))
                .map(to_item)
                .collect();
            let bytes = items
                .iter()
                .map(|i| i.bytes)
                .fold(0u64, u64::saturating_add);
            PurgeGroup {
                id: gid.into(),
                required: gid == GROUP_PROGRAM || gid == GROUP_RUNTIME,
                bytes,
                items,
            }
        })
        .collect()
}

fn to_item(c: &Candidate) -> PurgeItem {
    PurgeItem {
        id: c.id.clone(),
        group: c.group.into(),
        path: c.path.to_string_lossy().into_owned(),
        bytes: item_bytes(c),
        exists: exists(&c.path),
        kind: item_kind(&c.path),
    }
}

fn action(c: &Candidate, reason: Option<&str>) -> PurgeAction {
    PurgeAction {
        id: c.id.clone(),
        path: c.path.to_string_lossy().into_owned(),
        reason: reason.map(str::to_string),
    }
}

// --- project locals --------------------------------------------------------------

fn project_local_files(fs: &PurgeFs) -> Vec<PathBuf> {
    let mut dirs: Vec<PathBuf> = Vec::new();
    for config in [
        fs.env.global_config_path(),
        fs.env.default_global_config_path(),
        fs.env.home.join(".claude.json"),
    ] {
        for p in project_dirs_from_config(&config) {
            if !dirs.iter().any(|d| paths_equal(d, &p)) {
                dirs.push(p);
            }
        }
    }
    let mut files = Vec::new();
    for dir in dirs {
        let local_md = dir.join("CLAUDE.local.md");
        if local_md.is_file() {
            files.push(local_md);
        }
        let claude_dir = dir.join(".claude");
        let settings_local = claude_dir.join("settings.local.json");
        if settings_local.is_file() {
            files.push(settings_local);
        }
        if let Ok(rd) = fs::read_dir(&claude_dir) {
            for entry in rd.flatten() {
                let p = entry.path();
                let name = p
                    .file_name()
                    .map(|n| n.to_string_lossy().to_ascii_lowercase());
                let Some(name) = name else { continue };
                if (name.ends_with(".local.json") || name.ends_with(".local.md"))
                    && !files.iter().any(|f| paths_equal(f, &p))
                {
                    files.push(p);
                }
            }
        }
    }
    files
}

fn project_dirs_from_config(path: &Path) -> Vec<PathBuf> {
    let Ok(text) = fs::read_to_string(path) else {
        return Vec::new();
    };
    let Ok(v) = serde_json::from_str::<Value>(&text) else {
        return Vec::new();
    };
    let Some(obj) = v.get("projects").and_then(Value::as_object) else {
        return Vec::new();
    };
    obj.keys()
        .map(PathBuf::from)
        .filter(|p| p.is_absolute())
        .collect()
}

// --- delete ----------------------------------------------------------------------

fn remove_candidate(c: &Candidate) -> io::Result<()> {
    if let Some(target) = c.path.to_str().and_then(|s| s.strip_prefix("cmdkey:")) {
        return delete_cmdkey(target);
    }
    if is_dir_link(&c.path) && c.follow_link {
        if let Some(target) = link_target(&c.path) {
            if !is_protected_target(&target) {
                remove_tree(&target)?;
            }
        }
        remove_tree(&c.path)?;
        let reloc = reloc_backup(&c.path);
        if reloc.exists() && reloc != c.path {
            remove_tree(&reloc)?;
        }
        return Ok(());
    }
    remove_tree(&c.path)
}

fn remove_tree(path: &Path) -> io::Result<()> {
    let meta = match fs::symlink_metadata(path) {
        Ok(m) => m,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(e),
    };
    if is_reparse(&meta) {
        return remove_reparse(path, &meta);
    }
    if meta.is_dir() {
        fs::remove_dir_all(path)
    } else {
        fs::remove_file(path)
    }
}

fn remove_reparse(path: &Path, meta: &fs::Metadata) -> io::Result<()> {
    if meta.is_dir() || is_dir_link(path) {
        fs::remove_dir(path).or_else(|_| fs::remove_file(path))
    } else {
        fs::remove_file(path)
    }
}

fn is_protected(fs: &PurgeFs, path: &Path) -> bool {
    if path.to_string_lossy().starts_with("cmdkey:") {
        return false;
    }
    if path.components().count() < 2 {
        return true;
    }
    let protected = [
        fs.env.home.clone(),
        fs.roaming.clone(),
        fs.local.clone(),
        fs.temp.clone(),
        fs.env.home.join("AppData"),
        fs.env.home.join(".local"),
        fs.env.home.join(".local").join("bin"),
        fs.env.home.join(".local").join("share"),
        fs.env.home.join(".local").join("state"),
        fs.roaming.join("npm"),
        fs.roaming.join("npm").join("node_modules"),
        fs.local.join("ClaudeSwitch"),
    ];
    protected.iter().any(|p| paths_equal(p, path))
        || fs.program_data.iter().any(|p| paths_equal(p, path))
}

fn is_protected_target(path: &Path) -> bool {
    if path.components().count() < 2 {
        return true;
    }
    matches!(
        path.file_name()
            .and_then(|n| n.to_str())
            .map(str::to_ascii_lowercase)
            .as_deref(),
        Some("windows" | "system32" | "program files" | "program files (x86)" | "users")
    )
}

// --- sizes / existence -----------------------------------------------------------

fn exists(path: &Path) -> bool {
    if path.to_string_lossy().starts_with("cmdkey:") {
        return true;
    }
    path.exists() || is_dir_link(path)
}

fn item_kind(path: &Path) -> String {
    if path.to_string_lossy().starts_with("cmdkey:") {
        return "credential".into();
    }
    if is_dir_link(path) {
        return "link".into();
    }
    if path.is_dir() {
        return "dir".into();
    }
    "file".into()
}

fn item_bytes(c: &Candidate) -> u64 {
    if c.path.to_string_lossy().starts_with("cmdkey:") {
        return 0;
    }
    dir_size(&c.path)
}

fn dir_size(path: &Path) -> u64 {
    let Ok(meta) = fs::symlink_metadata(path) else {
        return 0;
    };
    if is_reparse(&meta) {
        if let Some(target) = link_target(path) {
            return dir_size_contents(&target);
        }
        return 0;
    }
    if meta.is_file() {
        return meta.len();
    }
    dir_size_contents(path)
}

fn dir_size_contents(path: &Path) -> u64 {
    let Ok(rd) = fs::read_dir(path) else {
        return 0;
    };
    let mut n = 0u64;
    for entry in rd.flatten() {
        let p = entry.path();
        let Ok(meta) = fs::symlink_metadata(&p) else {
            continue;
        };
        if is_reparse(&meta) {
            continue;
        }
        if meta.is_dir() {
            n = n.saturating_add(dir_size_contents(&p));
        } else {
            n = n.saturating_add(meta.len());
        }
    }
    n
}

fn is_dir_link(path: &Path) -> bool {
    fs::symlink_metadata(path).is_ok_and(|m| is_reparse(&m))
}

fn is_reparse(meta: &fs::Metadata) -> bool {
    if meta.file_type().is_symlink() {
        return true;
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        meta.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
    }
    #[cfg(not(windows))]
    {
        false
    }
}

fn link_target(path: &Path) -> Option<PathBuf> {
    #[cfg(windows)]
    {
        if let Ok(t) = junction::get_target(path) {
            return Some(t);
        }
    }
    fs::read_link(path).ok().map(|t| {
        if t.is_absolute() {
            t
        } else {
            path.parent().unwrap_or_else(|| Path::new(".")).join(t)
        }
    })
}

fn paths_equal(a: &Path, b: &Path) -> bool {
    match (fs::canonicalize(a), fs::canonicalize(b)) {
        (Ok(ca), Ok(cb)) => ca == cb,
        _ => {
            #[cfg(windows)]
            {
                a.to_string_lossy()
                    .eq_ignore_ascii_case(&b.to_string_lossy())
            }
            #[cfg(not(windows))]
            {
                a == b
            }
        }
    }
}

fn reloc_backup(source: &Path) -> PathBuf {
    let name = match source.file_name() {
        Some(n) => {
            let mut s = n.to_os_string();
            s.push(RELOC_SUFFIX);
            s
        }
        None => RELOC_SUFFIX.into(),
    };
    match source.parent() {
        Some(parent) if !parent.as_os_str().is_empty() => parent.join(name),
        _ => PathBuf::from(name),
    }
}

fn backup_sibling(file: &Path, suffix: &str) -> PathBuf {
    let mut name = file.file_name().unwrap_or_default().to_os_string();
    name.push(suffix);
    match file.parent() {
        Some(parent) => parent.join(name),
        None => PathBuf::from(name),
    }
}

fn glob_prefix(dir: &Path, prefix: &str) -> Vec<PathBuf> {
    let Ok(rd) = fs::read_dir(dir) else {
        return Vec::new();
    };
    let needle = prefix.to_ascii_lowercase();
    let mut out: Vec<PathBuf> = rd
        .flatten()
        .map(|e| e.path())
        .filter(|p| {
            p.file_name().is_some_and(|n| {
                n.to_string_lossy()
                    .to_ascii_lowercase()
                    .starts_with(&needle)
            })
        })
        .collect();
    out.sort();
    out
}

fn claude_bin_name() -> &'static str {
    if cfg!(windows) {
        "claude.exe"
    } else {
        "claude"
    }
}

fn live_session_count(env: &PathEnv) -> u32 {
    cleanup::history_roots(env, &env.backup_root(), None)
        .iter()
        .map(|r| u32::try_from(session::live_sessions_for(&r.home()).len()).unwrap_or(u32::MAX))
        .fold(0, u32::saturating_add)
}

fn imported_account_count(fs: &PurgeFs) -> u32 {
    let seq = SequenceData::load(&fs.backup_root().join("sequence.json")).unwrap_or_default();
    u32::try_from(seq.accounts.len()).unwrap_or(u32::MAX)
}

fn claude_bin_in_use(candidates: &[Candidate]) -> bool {
    candidates.iter().any(|c| {
        c.group == GROUP_PROGRAM
            && (c.id.starts_with("native-bin") || c.id.starts_with("npm-"))
            && file_in_use(&c.path)
    })
}

fn file_in_use(path: &Path) -> bool {
    if !path.is_file() {
        return false;
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        std::fs::OpenOptions::new()
            .read(true)
            .share_mode(0)
            .open(path)
            .is_err()
    }
    #[cfg(not(windows))]
    {
        let _ = path;
        false
    }
}

// --- Windows Credential Manager --------------------------------------------------

fn cmdkey_claude_code_targets() -> Vec<String> {
    #[cfg(windows)]
    {
        let out = std::process::Command::new("cmdkey")
            .arg("/list")
            .output()
            .ok();
        let Some(out) = out else {
            return Vec::new();
        };
        let text = String::from_utf8_lossy(&out.stdout);
        parse_cmdkey_targets(&text)
    }
    #[cfg(not(windows))]
    {
        Vec::new()
    }
}

fn parse_cmdkey_targets(text: &str) -> Vec<String> {
    let mut out = Vec::new();
    for line in text.lines() {
        let trimmed = line.trim();
        let Some(rest) = trimmed
            .strip_prefix("Target:")
            .or_else(|| trimmed.strip_prefix("target:"))
        else {
            continue;
        };
        let target = rest.trim();
        if target.is_empty() {
            continue;
        }
        let lower = target.to_ascii_lowercase();
        if lower.contains("claudeswitch") || lower.contains("zed:") {
            continue;
        }
        if lower.contains("claude code") {
            out.push(target.to_string());
        }
    }
    out.sort();
    out.dedup();
    out
}

fn delete_cmdkey(target: &str) -> io::Result<()> {
    #[cfg(windows)]
    {
        let status = std::process::Command::new("cmdkey")
            .arg(format!("/delete:{target}"))
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status()?;
        if status.success() {
            Ok(())
        } else {
            Err(io::Error::other(format!(
                "cmdkey /delete failed for {target}"
            )))
        }
    }
    #[cfg(not(windows))]
    {
        let _ = target;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn env_at(root: &Path) -> PathEnv {
        PathEnv {
            home: root.to_path_buf(),
            claude_config_dir: None,
            xdg_data_home: None,
            platform: crate::models::Platform::Windows,
        }
    }

    fn write_tree(dir: &Path, rel: &str, body: &[u8]) {
        let path = dir.join(rel);
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).unwrap();
        }
        fs::write(path, body).unwrap();
    }

    fn keep_accounts() -> PurgeOptions {
        PurgeOptions {
            remove_ide_extension: false,
            ..PurgeOptions::default()
        }
    }

    #[test]
    fn scan_lists_native_runtime_and_leaves_uv() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        write_tree(&env.home.join(".local").join("bin"), "claude.exe", b"cli");
        write_tree(&env.home.join(".local").join("bin"), "uv.exe", b"uv");
        write_tree(
            &env.home.join(".local").join("share").join("claude"),
            "versions/1.bin",
            b"v",
        );
        write_tree(&env.home.join(".claude"), "settings.json", b"{}");
        fs::write(env.home.join(".claude.json"), b"{\"userID\":\"abc\"}").unwrap();
        write_tree(&env.backup_root(), "sequence.json", b"{\"accounts\":{}}");

        let s = scan(&env);
        assert_eq!(s.install_kind, "native");
        let ids: Vec<_> = s
            .groups
            .iter()
            .flat_map(|g| g.items.iter().map(|i| i.id.as_str()))
            .collect();
        assert!(ids.contains(&"native-bin"), "{ids:?}");
        assert!(ids.contains(&"native-versions"), "{ids:?}");
        assert!(ids.contains(&"config-home"), "{ids:?}");
        assert!(ids.contains(&"global-config"), "{ids:?}");
        assert!(!ids.iter().any(|id| id.contains("uv")), "{ids:?}");
        assert_eq!(s.live_sessions, 0);
    }

    #[test]
    fn default_apply_removes_program_and_runtime_keeps_backup_and_uv() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let bin = env.home.join(".local").join("bin");
        write_tree(&bin, "claude.exe", b"cli");
        write_tree(&bin, "uv.exe", b"uv");
        write_tree(
            &env.home.join(".local").join("share").join("claude"),
            "versions/1.bin",
            &[0u8; 32],
        );
        write_tree(&env.home.join(".claude"), "telemetry/e.json", b"evt");
        fs::write(
            env.home.join(".claude.json"),
            b"{\"userID\":\"u\",\"oauthAccount\":{\"emailAddress\":\"a@x.com\"}}",
        )
        .unwrap();
        fs::write(env.home.join(".claude.json.backup"), b"{\"userID\":\"u\"}").unwrap();
        write_tree(
            &env.home
                .join("AppData")
                .join("Local")
                .join("claude-cli-nodejs"),
            "Cache/x.jsonl",
            b"{}",
        );
        write_tree(&env.backup_root(), "credentials/.creds-a.enc", b"tok");
        write_tree(
            &env.backup_root(),
            "sequence.json",
            b"{\"accounts\":{\"1\":{\"email\":\"a@x.com\"}},\"sequence\":[1]}",
        );

        let opts = keep_accounts();
        let planned = plan(&env, &opts);
        assert!(planned.problems.is_empty(), "{:?}", planned.problems);
        assert!(planned
            .warnings
            .iter()
            .any(|w| w == "accounts-kept-identity-remains"));

        let out = apply(&env, &opts).unwrap();
        assert!(out.failed.is_empty(), "{:?}", out.failed);
        assert!(!bin.join("claude.exe").exists());
        assert!(
            bin.join("uv.exe").exists(),
            "other tools in .local/bin stay"
        );
        assert!(!env
            .home
            .join(".local")
            .join("share")
            .join("claude")
            .exists());
        assert!(!env.home.join(".claude").exists());
        assert!(!env.home.join(".claude.json").exists());
        assert!(!env.home.join(".claude.json.backup").exists());
        assert!(!env
            .home
            .join("AppData")
            .join("Local")
            .join("claude-cli-nodejs")
            .exists());
        assert!(env.backup_root().join("credentials/.creds-a.enc").exists());
        assert!(env.backup_root().join("sequence.json").exists());
    }

    #[test]
    fn removing_imported_accounts_drops_the_backup_tree() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        write_tree(&env.home.join(".claude"), "x.txt", b"x");
        write_tree(&env.backup_root(), "configs/a.json", b"{}");
        let mut opts = keep_accounts();
        opts.remove_imported_accounts = true;
        apply(&env, &opts).unwrap();
        assert!(!env.backup_root().exists());
    }

    #[test]
    fn project_locals_and_desktop_stay_unless_asked() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let proj = tmp.path().join("work");
        write_tree(&proj, "CLAUDE.md", b"# team");
        write_tree(&proj, "CLAUDE.local.md", b"me");
        write_tree(&proj, ".claude/settings.local.json", b"{}");
        let cfg = json!({
            "userID": "u",
            "projects": { proj.to_string_lossy().to_string(): { "allowedTools": [] } }
        });
        fs::write(env.home.join(".claude.json"), cfg.to_string()).unwrap();
        write_tree(&env.home.join(".claude"), "x.txt", b"x");
        write_tree(
            &env.home.join("AppData").join("Roaming").join("Claude"),
            "ant-did",
            b"device-id-bytes______________",
        );

        apply(&env, &keep_accounts()).unwrap();
        assert!(proj.join("CLAUDE.md").exists());
        assert!(proj.join("CLAUDE.local.md").exists());
        assert!(proj.join(".claude/settings.local.json").exists());
        assert!(env
            .home
            .join("AppData")
            .join("Roaming")
            .join("Claude")
            .join("ant-did")
            .exists());

        let mut opts = keep_accounts();
        opts.remove_project_locals = true;
        opts.remove_desktop_nested = true;
        // Runtime already gone; these optionals still apply.
        fs::write(env.home.join(".claude.json"), cfg.to_string()).unwrap();
        write_tree(&env.home.join(".claude"), "x.txt", b"x");
        apply(&env, &opts).unwrap();
        assert!(proj.join("CLAUDE.md").exists(), "committed CLAUDE.md stays");
        assert!(!proj.join("CLAUDE.local.md").exists());
        assert!(!proj.join(".claude/settings.local.json").exists());
        assert!(!env
            .home
            .join("AppData")
            .join("Roaming")
            .join("Claude")
            .join("ant-did")
            .exists());
    }

    #[test]
    fn live_sessions_block_apply() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let home = env.claude_config_home();
        let sessions = home.join("sessions");
        fs::create_dir_all(&sessions).unwrap();
        let me = std::process::id();
        fs::write(
            sessions.join(format!("{me}.json")),
            json!({
                "pid": me,
                "sessionId": "s",
                "cwd": "D:\\a",
                "startedAt": chrono::Utc::now().timestamp_millis(),
            })
            .to_string(),
        )
        .unwrap();
        write_tree(&home, "x.txt", b"x");
        let planned = plan(&env, &keep_accounts());
        assert!(
            planned.problems.iter().any(|p| p == "live-sessions"),
            "{:?}",
            planned.problems
        );
        let err = apply(&env, &keep_accounts()).unwrap_err();
        assert!(err.to_string().contains("live"), "{err}");
        assert!(home.join("x.txt").exists(), "nothing deleted on refusal");
    }

    #[test]
    fn missing_optional_paths_are_not_errors() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        write_tree(&env.home.join(".claude"), "x.txt", b"x");
        let mut opts = keep_accounts();
        opts.remove_ide_extension = true;
        opts.remove_desktop_nested = true;
        opts.remove_third_party_shells = true;
        opts.remove_managed = true;
        let out = apply(&env, &opts).unwrap();
        assert!(out.failed.is_empty(), "{:?}", out.failed);
        assert!(!env.home.join(".claude").exists());
    }

    #[test]
    fn npm_tree_is_removed_with_the_program_group() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let npm = env.home.join("AppData").join("Roaming").join("npm");
        write_tree(&npm, "claude.cmd", b"@echo off");
        write_tree(
            &npm.join("node_modules")
                .join("@anthropic-ai")
                .join("claude-code"),
            "package.json",
            b"{}",
        );
        write_tree(&npm.join("node_modules").join("other"), "x", b"keep");
        write_tree(&env.home.join(".claude"), "x.txt", b"x");
        apply(&env, &keep_accounts()).unwrap();
        assert!(!npm.join("claude.cmd").exists());
        assert!(!npm
            .join("node_modules")
            .join("@anthropic-ai")
            .join("claude-code")
            .exists());
        assert!(npm.join("node_modules").join("other").join("x").exists());
    }

    #[test]
    fn parse_cmdkey_keeps_claude_code_and_drops_zed() {
        let text = "\n\
            Target: LegacyGeneric:target=Claude Code-credentials\n\
            Target: LegacyGeneric:target=zed:url=https://api.xiaomimimo.com/anthropic\n\
            Target: ClaudeSwitch.Something\n\
            Target: Claude Code\n";
        let got = parse_cmdkey_targets(text);
        assert_eq!(
            got,
            vec![
                "Claude Code".to_string(),
                "LegacyGeneric:target=Claude Code-credentials".to_string(),
            ]
        );
    }

    #[test]
    fn empty_machine_is_nothing() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let planned = plan(&env, &PurgeOptions::default());
        assert!(
            planned.problems.iter().any(|p| p == "nothing"),
            "{planned:?}"
        );
    }

    #[cfg(windows)]
    #[test]
    fn apply_follows_a_config_home_junction_and_its_reloc_backup() {
        let tmp = tempfile::tempdir().unwrap();
        let env = env_at(tmp.path());
        let real = tmp.path().join("offload").join("claude");
        write_tree(&real, "settings.json", b"{\"a\":1}");
        let link = env.home.join(".claude");
        fs::create_dir_all(link.parent().unwrap()).unwrap();
        junction::create(&real, &link).unwrap();
        let reloc = reloc_backup(&link);
        write_tree(&reloc, "old.txt", b"old");
        fs::write(env.home.join(".claude.json"), b"{}").unwrap();

        apply(&env, &keep_accounts()).unwrap();
        assert!(!link.exists() && !is_dir_link(&link));
        assert!(!real.exists(), "junction target must go too");
        assert!(!reloc.exists());
        assert!(!env.home.join(".claude.json").exists());
    }
}
