//! Putting the status line into Claude Code, and taking it back out.
//!
//! Two artefacts, both reversible:
//!
//! - the renderer binary at `<backup_root>/bin/cs-statusline`, stamped with the
//!   build that put it there;
//! - a `statusLine` block in the user's `~/.claude/settings.json`.
//!
//! The second one is somebody else's file. Three rules follow from that, and
//! they outrank convenience everywhere below:
//!
//! 1. **Unreadable means untouched.** A `settings.json` that is not valid JSON
//!    is a file someone is in the middle of editing, or one another tool wrote
//!    in a shape we do not know. We report it and change nothing.
//! 2. **Never take a status line silently.** If the configured command is not
//!    ours, the user is asked first, and whatever we displaced is kept so that
//!    turning the feature off puts it back.
//! 3. **The binary goes first.** Claude Code must never be pointed at a path
//!    that does not exist yet.

use std::path::{Path, PathBuf};

use chrono::Utc;
use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};

use crate::errors::{Error, Result};
use crate::fsutil::atomic_write;
use crate::paths::Paths;
use crate::settings::StatuslineSettings;
use crate::statusline::Preset;

/// The renderer binary, as `settings.json` names it.
pub const BINARY_STEM: &str = "cs-statusline";

/// Which build is sitting in `<backup_root>/bin`, so an upgrade can tell.
pub const VERSION_STAMP: &str = "cs-statusline.version";

/// One copy of the user's file as it was before this tool first touched it.
pub const SETTINGS_BACKUP: &str = "settings.json.cswitch-bak";

/// Claude Code's own key inside `settings.json`.
const STATUS_LINE_KEY: &str = "statusLine";

/// Claude Code accepts 1–60 seconds; anything else is dropped rather than
/// written and rejected at the other end.
const REFRESH_INTERVAL_RANGE: std::ops::RangeInclusive<u32> = 1..=60;

// --- the installed binary ---------------------------------------------------

/// Where the renderer lives once installed.
///
/// A fixed path under `backup_root`, not the app's own folder: this program is
/// portable, and the command line in `settings.json` is absolute. Someone who
/// moves `ClaudeSwitch.exe` to another drive would otherwise be left with a
/// status line pointing at nothing.
#[must_use]
pub fn binary_dir(backup_root: &Path) -> PathBuf {
    backup_root.join("bin")
}

#[must_use]
pub fn binary_path(backup_root: &Path) -> PathBuf {
    binary_dir(backup_root).join(format!("{BINARY_STEM}{}", std::env::consts::EXE_SUFFIX))
}

/// Which build is installed, if the stamp can be read.
#[must_use]
pub fn installed_version(backup_root: &Path) -> Option<String> {
    let text = std::fs::read_to_string(binary_dir(backup_root).join(VERSION_STAMP)).ok()?;
    let v = text.trim().to_string();
    (!v.is_empty()).then_some(v)
}

/// Put `source` at [`binary_path`] and stamp it, unless that build is already
/// there. Returns the installed path.
///
/// Called when the user turns the status line on and again after an upgrade,
/// never on every launch: this writes an executable to disk, and doing that
/// behind the user's back on a machine with an anxious antivirus is how a tray
/// app earns a reputation.
///
/// # Errors
///
/// [`Error::Validation`] when the source is not there (a build that forgot to
/// bundle the binary), [`Error::Io`] when the copy or the swap fails.
pub fn install_binary(backup_root: &Path, source: &Path, version: &str) -> Result<PathBuf> {
    let dir = binary_dir(backup_root);
    let dest = binary_path(backup_root);
    sweep_parked(&dir);

    if dest.is_file() && installed_version(backup_root).as_deref() == Some(version) {
        return Ok(dest);
    }
    if !source.is_file() {
        return Err(Error::Validation(format!(
            "status line binary missing from this build: {}",
            source.display()
        )));
    }

    std::fs::create_dir_all(&dir).map_err(Error::Io)?;
    let stamp = dir.join(VERSION_STAMP);
    // Drop the stamp first: if anything below fails, the next attempt must see
    // an unstamped directory and do the work again rather than trust it.
    let _ = std::fs::remove_file(&stamp);

    let staged = dir.join(format!("{BINARY_STEM}.new"));
    let _ = std::fs::remove_file(&staged);
    std::fs::copy(source, &staged).map_err(Error::Io)?;

    if dest.exists() {
        // Windows will not let a running image be deleted or overwritten, but it
        // will let it be renamed: the loader holds the file by handle, not by
        // path. So the old one is parked rather than removed, and swept away on
        // a later run once nothing is using it.
        let parked = dir.join(format!(
            "{BINARY_STEM}.old-{}",
            Utc::now().timestamp_millis()
        ));
        std::fs::rename(&dest, &parked).map_err(Error::Io)?;
        let _ = std::fs::remove_file(&parked);
    }
    std::fs::rename(&staged, &dest).map_err(Error::Io)?;

    // Stamped last, so a half-finished install never claims to be this version.
    atomic_write(&stamp, version.as_bytes()).map_err(Error::Io)?;
    Ok(dest)
}

/// Delete the binaries parked by earlier upgrades; the ones still running stay.
fn sweep_parked(dir: &Path) {
    let prefix = format!("{BINARY_STEM}.old-");
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        if entry.file_name().to_string_lossy().starts_with(&prefix) {
            let _ = std::fs::remove_file(entry.path());
        }
    }
}

// --- the command line -------------------------------------------------------

/// What Claude Code should run, with the preset baked in.
///
/// The preset travels on the command line rather than in a config file so the
/// renderer needs to read nothing of ours to know what to draw: our own
/// `settings.json` could be missing or broken and the line still comes out.
#[must_use]
pub fn command_for(binary: &Path, preset: Preset) -> String {
    format!("{} --preset {}", quote(binary), preset.as_str())
}

/// Always quoted. On Windows the command goes through `cmd.exe`, and the
/// default install path (`C:\Users\First Last\…`) has a space in it often
/// enough that "quote only when needed" is a bug waiting for the wrong user.
fn quote(path: &Path) -> String {
    format!("\"{}\"", path.display())
}

/// The program a command line runs, with its quotes stripped.
fn first_token(command: &str) -> &str {
    let trimmed = command.trim();
    trimmed.strip_prefix('"').map_or_else(
        || trimmed.split_whitespace().next().unwrap_or(trimmed),
        |rest| rest.split('"').next().unwrap_or(rest),
    )
}

/// Whether a configured `statusLine` command is one of ours.
///
/// By the program it runs, not by the whole string: the path changes when the
/// backup root moves, and the preset changes whenever the user picks another
/// one. Both must still read as ours.
#[must_use]
pub fn is_ours(command: &str) -> bool {
    Path::new(first_token(command))
        .file_stem()
        .is_some_and(|stem| stem.eq_ignore_ascii_case(BINARY_STEM))
}

fn installed_command(block: Option<&Value>) -> Option<String> {
    block?
        .get("command")
        .and_then(Value::as_str)
        .map(ToOwned::to_owned)
}

// --- Claude Code's settings.json --------------------------------------------

/// What, if anything, stands between the user and the status line they asked
/// for — worst first, because only the worst one is actionable.
///
/// One value rather than a handful of flags: they are not independent. A
/// `settings.json` that will not parse says nothing about who owns the status
/// line, and a foreign one must be left alone whether or not it also differs
/// from what we would write.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Standing {
    /// Installed and current, or cleanly absent.
    #[default]
    Settled,
    /// Ours, but not what we would write now: an upgrade, a moved backup root,
    /// or an edit by hand. [`heal`] fixes exactly this.
    Drifted,
    /// Another tool's status line is configured. Never written over without
    /// the user saying so.
    Foreign,
    /// `settings.json` does not parse, so nothing may be written to it.
    Unreadable,
}

/// Where the status line stands, from both sides: what we intend and what
/// Claude Code is actually configured to run.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    /// What the user asked this tool for.
    pub enabled: bool,
    pub preset: Preset,
    /// The `statusLine` command Claude Code will run, whosever it is.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub command: Option<String>,
    pub standing: Standing,
}

/// Read the current state. Never fails: the GUI shows this on every open, and a
/// broken `settings.json` is something to report in the window, not to throw.
#[must_use]
pub fn status(paths: &Paths, our: &StatuslineSettings) -> Status {
    let read = read_settings(&paths.claude_settings);
    let map = read.as_ref().ok().cloned().unwrap_or_default();
    let command = installed_command(map.get(STATUS_LINE_KEY));
    let expected = command_for(&binary_path(&paths.backup_root), our.preset);

    let standing = if read.is_err() {
        Standing::Unreadable
    } else if command.as_deref().is_some_and(|c| !is_ours(c)) {
        Standing::Foreign
    } else if our.enabled && command.as_deref() != Some(expected.as_str()) {
        Standing::Drifted
    } else {
        Standing::Settled
    };

    Status {
        enabled: our.enabled,
        preset: our.preset,
        command,
        standing,
    }
}

/// One request to install or re-install.
pub struct Request<'a> {
    pub preset: Preset,
    /// The binary to copy in. Only the .NET host knows where the single-file
    /// bundle unpacked it, so it is passed in rather than guessed at.
    pub source: &'a Path,
    /// Build stamped alongside the binary; an upgrade is a version change.
    pub version: &'a str,
    /// Replace a `statusLine` that belongs to something else. The user has been
    /// asked by the time this is true.
    pub takeover: bool,
    /// `statusLine.refreshInterval`, when the installed Claude Code is new
    /// enough to understand it (2.1.97+). Omitted otherwise.
    pub refresh_interval: Option<u32>,
}

/// Install the binary and point Claude Code at it.
///
/// # Errors
///
/// [`Error::Validation`] when `settings.json` cannot be parsed, when another
/// tool owns the status line and `takeover` is false, or when this build has no
/// binary to install; [`Error::Io`] on a failed write.
pub fn enable(paths: &Paths, our: &mut StatuslineSettings, req: &Request) -> Result<Status> {
    let mut map = read_settings(&paths.claude_settings)?;
    let existing = map.get(STATUS_LINE_KEY).cloned();
    let existing_command = installed_command(existing.as_ref());
    if let Some(command) = existing_command.as_deref() {
        if !is_ours(command) && !req.takeover {
            return Err(Error::Validation(format!(
                "another status line is already configured: {command}"
            )));
        }
    }

    // Before settings.json, so the command we write is never a broken path.
    let binary = install_binary(&paths.backup_root, req.source, req.version)?;
    backup_once(&paths.claude_settings)?;

    let command = command_for(&binary, req.preset);
    let mut block = Map::new();
    block.insert("type".into(), json!("command"));
    block.insert("command".into(), json!(command));
    block.insert("padding".into(), json!(0));
    if let Some(secs) = req
        .refresh_interval
        .filter(|s| REFRESH_INTERVAL_RANGE.contains(s))
    {
        block.insert("refreshInterval".into(), json!(secs));
    }
    map.insert(STATUS_LINE_KEY.into(), Value::Object(block));
    write_settings(&paths.claude_settings, &map)?;

    // Remember what we displaced, once. Re-installing over ourselves (a preset
    // change, an upgrade) must not overwrite the original with our own block.
    if our.replaced.is_none() && existing_command.is_some_and(|c| !is_ours(&c)) {
        our.replaced = existing;
    }
    our.enabled = true;
    our.preset = req.preset;
    our.installed_command = Some(command);
    Ok(status(paths, our))
}

/// Take the status line back out, restoring whatever was there before.
///
/// The binary stays: it may be running this second, and re-enabling should not
/// have to write an executable again. `<backup_root>/bin` is ours to keep.
///
/// # Errors
///
/// [`Error::Validation`] when `settings.json` cannot be parsed; [`Error::Io`]
/// on a failed write.
pub fn disable(paths: &Paths, our: &mut StatuslineSettings) -> Result<Status> {
    let mut map = read_settings(&paths.claude_settings)?;
    let command = installed_command(map.get(STATUS_LINE_KEY));

    match command {
        // Something else owns the status line now. Restoring our stored value
        // over it would undo whatever the user did after us.
        Some(c) if !is_ours(&c) => {
            our.replaced = None;
        }
        Some(_) => {
            match our.replaced.take() {
                Some(previous) => map.insert(STATUS_LINE_KEY.into(), previous),
                None => map.remove(STATUS_LINE_KEY),
            };
            write_settings(&paths.claude_settings, &map)?;
        }
        None => {
            // Already gone by hand; the value we were keeping is stale.
            our.replaced = None;
        }
    }

    our.enabled = false;
    our.installed_command = None;
    Ok(status(paths, our))
}

/// Re-point a status line that drifted, at startup or after an upgrade.
///
/// Drift is ordinary here: the app is portable and the backup root can move, a
/// new build changes the stamped version, and users edit `settings.json`. What
/// is *not* ordinary is another tool having taken over in the meantime — that
/// is left exactly as it is, for the user to decide.
///
/// # Errors
///
/// As [`enable`], when a rewrite is needed and fails.
pub fn heal(
    paths: &Paths,
    our: &mut StatuslineSettings,
    source: &Path,
    version: &str,
    refresh_interval: Option<u32>,
) -> Result<Status> {
    let current = status(paths, our);
    if !our.enabled || current.standing != Standing::Drifted {
        return Ok(current);
    }
    enable(
        paths,
        our,
        &Request {
            preset: our.preset,
            source,
            version,
            takeover: false,
            refresh_interval,
        },
    )
}

/// Parse `settings.json`, or say why it must be left alone.
///
/// A missing file is an empty object — that is the ordinary first install. A
/// file that exists but does not parse is never a file to overwrite.
fn read_settings(path: &Path) -> Result<Map<String, Value>> {
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

fn write_settings(path: &Path, map: &Map<String, Value>) -> Result<()> {
    let text = serde_json::to_string_pretty(&Value::Object(map.clone()))
        .map_err(|e| Error::Internal(format!("claude settings serialize: {e}")))?;
    atomic_write(path, text.as_bytes()).map_err(Error::Io)
}

/// Keep one copy of the file as it was before this tool first wrote to it.
///
/// Once only: the point is the user's original, not the state before the most
/// recent preset change.
fn backup_once(path: &Path) -> Result<()> {
    let backup = path.with_file_name(SETTINGS_BACKUP);
    if backup.exists() || !path.exists() {
        return Ok(());
    }
    std::fs::copy(path, &backup).map_err(Error::Io)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::paths::PathEnv;

    /// Borrows only the source path, so callers can still hand `enable` a
    /// mutable borrow of the settings field beside it.
    fn req(source: &Path, preset: Preset) -> Request<'_> {
        Request {
            preset,
            source,
            version: "0.1.0",
            takeover: false,
            refresh_interval: Some(10),
        }
    }

    struct Fixture {
        _dir: tempfile::TempDir,
        paths: Paths,
        source: PathBuf,
        our: StatuslineSettings,
    }

    impl Fixture {
        fn new() -> Self {
            let dir = tempfile::tempdir().unwrap();
            let home = dir.path().join("home");
            std::fs::create_dir_all(home.join(".claude")).unwrap();
            let paths = Paths::resolve(PathEnv::isolated(&home));
            let source = dir
                .path()
                .join(format!("bundled{}", std::env::consts::EXE_SUFFIX));
            std::fs::write(&source, b"renderer").unwrap();
            Self {
                _dir: dir,
                paths,
                source,
                our: StatuslineSettings::default(),
            }
        }

        fn settings_text(&self) -> String {
            std::fs::read_to_string(&self.paths.claude_settings).unwrap()
        }

        fn settings(&self) -> Value {
            serde_json::from_str(&self.settings_text()).unwrap()
        }

        fn write_settings(&self, text: &str) {
            std::fs::write(&self.paths.claude_settings, text.as_bytes()).unwrap();
        }
    }

    // --- the binary ---

    /// A stand-in for the bundled binary: only its bytes matter here.
    fn fake_binary(dir: &Path, body: &str) -> PathBuf {
        let p = dir.join(format!("source{}", std::env::consts::EXE_SUFFIX));
        std::fs::write(&p, body.as_bytes()).unwrap();
        p
    }

    #[test]
    fn installing_the_binary_stamps_the_build() {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path().join("backup");
        let source = fake_binary(dir.path(), "v1 body");

        let dest = install_binary(&root, &source, "0.1.0").unwrap();
        assert_eq!(dest, binary_path(&root));
        assert_eq!(std::fs::read_to_string(&dest).unwrap(), "v1 body");
        assert_eq!(installed_version(&root).as_deref(), Some("0.1.0"));
    }

    #[test]
    fn the_same_build_is_not_written_again() {
        // Rewriting an executable on every launch is what makes antivirus
        // software take an interest; the stamp is what stops it.
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path().join("backup");
        let source = fake_binary(dir.path(), "v1 body");
        let dest = install_binary(&root, &source, "0.1.0").unwrap();

        std::fs::write(&dest, b"left alone").unwrap();
        install_binary(&root, &source, "0.1.0").unwrap();
        assert_eq!(std::fs::read_to_string(&dest).unwrap(), "left alone");

        // A new version does do the work.
        install_binary(&root, &source, "0.2.0").unwrap();
        assert_eq!(std::fs::read_to_string(&dest).unwrap(), "v1 body");
        assert_eq!(installed_version(&root).as_deref(), Some("0.2.0"));
    }

    #[test]
    fn a_missing_stamp_reinstalls_even_when_the_binary_is_there() {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path().join("backup");
        let source = fake_binary(dir.path(), "real");
        let dest = install_binary(&root, &source, "0.1.0").unwrap();

        std::fs::remove_file(binary_dir(&root).join(VERSION_STAMP)).unwrap();
        std::fs::write(&dest, b"half written").unwrap();
        install_binary(&root, &source, "0.1.0").unwrap();
        assert_eq!(std::fs::read_to_string(&dest).unwrap(), "real");
    }

    #[test]
    fn binaries_parked_by_an_upgrade_are_swept_later() {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path().join("backup");
        let source = fake_binary(dir.path(), "v1");
        install_binary(&root, &source, "0.1.0").unwrap();

        // What an upgrade leaves behind while the old one is still running.
        let parked = binary_dir(&root).join(format!("{BINARY_STEM}.old-123"));
        std::fs::write(&parked, b"was running").unwrap();

        install_binary(&root, &source, "0.2.0").unwrap();
        assert!(!parked.exists(), "the parked copy should be swept");
        assert!(binary_path(&root).is_file());
    }

    #[test]
    fn a_build_without_the_binary_says_so() {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path().join("backup");
        let err = install_binary(&root, &dir.path().join("nope.exe"), "0.1.0").unwrap_err();
        let message = err.to_string();
        assert!(message.contains("nope.exe"), "{message}");
        assert!(!binary_path(&root).exists());
    }

    // --- Claude Code's settings.json ---

    #[test]
    fn enabling_writes_the_block_and_installs_the_binary() {
        let mut f = Fixture::new();
        let status = enable(&f.paths, &mut f.our, &req(&f.source, Preset::Standard)).unwrap();

        let binary = binary_path(&f.paths.backup_root);
        assert!(binary.is_file(), "the binary lands before the command");
        let line = &f.settings()["statusLine"];
        assert_eq!(line["type"], "command");
        assert_eq!(line["padding"], 0);
        assert_eq!(line["refreshInterval"], 10);
        assert_eq!(
            line["command"].as_str().unwrap(),
            format!("\"{}\" --preset standard", binary.display())
        );

        assert!(status.enabled);
        assert_eq!(status.standing, Standing::Settled);
        assert!(f.our.enabled);
        assert_eq!(f.our.preset, Preset::Standard);
        assert!(f.our.replaced.is_none(), "nothing was displaced");
    }

    #[test]
    fn everything_else_in_the_file_survives() {
        let mut f = Fixture::new();
        f.write_settings(
            r#"{"model":"opus","env":{"FOO":"bar"},"permissions":{"allow":["Bash(ls:*)"]}}"#,
        );
        enable(&f.paths, &mut f.our, &req(&f.source, Preset::Lean)).unwrap();

        let v = f.settings();
        assert_eq!(v["model"], "opus");
        assert_eq!(v["env"]["FOO"], "bar");
        assert_eq!(v["permissions"]["allow"][0], "Bash(ls:*)");
        assert!(v["statusLine"].is_object());
    }

    #[test]
    fn a_file_that_does_not_parse_is_never_written_to() {
        let mut f = Fixture::new();
        f.write_settings("{ \"model\": \"opus\",,, ");
        let before = f.settings_text();

        let err = enable(&f.paths, &mut f.our, &req(&f.source, Preset::Standard)).unwrap_err();
        assert!(err.to_string().contains("left untouched"), "{err}");
        assert_eq!(f.settings_text(), before, "the file must be byte-identical");
        assert!(!f.our.enabled);
        assert!(
            !binary_path(&f.paths.backup_root).exists(),
            "nothing at all should have been installed"
        );

        // And the GUI can see why, rather than being told nothing.
        let status = status(&f.paths, &f.our);
        assert_eq!(status.standing, Standing::Unreadable);
    }

    #[test]
    fn another_tools_status_line_is_not_taken_without_being_asked() {
        let mut f = Fixture::new();
        f.write_settings(
            r#"{"statusLine":{"type":"command","command":"npx -y ccstatusline@latest"}}"#,
        );

        let err = enable(&f.paths, &mut f.our, &req(&f.source, Preset::Standard)).unwrap_err();
        assert!(err.to_string().contains("ccstatusline"), "{err}");
        assert_eq!(
            f.settings()["statusLine"]["command"],
            "npx -y ccstatusline@latest",
            "left exactly as it was"
        );

        // The GUI asks; the answer comes back as takeover.
        let status = status(&f.paths, &f.our);
        assert_eq!(status.standing, Standing::Foreign);
        assert_eq!(
            status.command.as_deref(),
            Some("npx -y ccstatusline@latest")
        );

        let mut request = req(&f.source, Preset::Standard);
        request.takeover = true;
        enable(&f.paths, &mut f.our, &request).unwrap();
        assert!(is_ours(
            f.settings()["statusLine"]["command"].as_str().unwrap()
        ));

        // Turning it off hands the status line back to its owner.
        disable(&f.paths, &mut f.our).unwrap();
        assert_eq!(
            f.settings()["statusLine"]["command"],
            "npx -y ccstatusline@latest"
        );
        assert!(!f.our.enabled);
    }

    #[test]
    fn re_installing_over_ourselves_keeps_the_original_to_restore() {
        let mut f = Fixture::new();
        f.write_settings(r#"{"statusLine":{"type":"command","command":"my-own-script.sh"}}"#);
        let mut request = req(&f.source, Preset::Standard);
        request.takeover = true;
        enable(&f.paths, &mut f.our, &request).unwrap();

        // Preset changes and upgrades re-enter enable(); the displaced value
        // must not be replaced by our own block.
        enable(&f.paths, &mut f.our, &req(&f.source, Preset::Full)).unwrap();
        enable(&f.paths, &mut f.our, &req(&f.source, Preset::Lean)).unwrap();
        assert!(f.settings()["statusLine"]["command"]
            .as_str()
            .unwrap()
            .ends_with("--preset lean"));

        disable(&f.paths, &mut f.our).unwrap();
        assert_eq!(f.settings()["statusLine"]["command"], "my-own-script.sh");
    }

    #[test]
    fn disabling_with_nothing_displaced_removes_the_key() {
        let mut f = Fixture::new();
        f.write_settings(r#"{"model":"opus"}"#);
        enable(&f.paths, &mut f.our, &req(&f.source, Preset::Standard)).unwrap();
        disable(&f.paths, &mut f.our).unwrap();

        let v = f.settings();
        assert!(v.get("statusLine").is_none(), "{v}");
        assert_eq!(v["model"], "opus", "the rest of the file is untouched");
        assert!(f.our.installed_command.is_none());
    }

    #[test]
    fn disabling_leaves_a_status_line_someone_else_installed_after_us() {
        let mut f = Fixture::new();
        enable(&f.paths, &mut f.our, &req(&f.source, Preset::Standard)).unwrap();
        f.write_settings(r#"{"statusLine":{"type":"command","command":"someone-else"}}"#);

        disable(&f.paths, &mut f.our).unwrap();
        assert_eq!(f.settings()["statusLine"]["command"], "someone-else");
        assert!(!f.our.enabled);
    }

    #[test]
    fn the_users_original_file_is_kept_once() {
        let mut f = Fixture::new();
        f.write_settings(r#"{"model":"the original"}"#);
        enable(&f.paths, &mut f.our, &req(&f.source, Preset::Standard)).unwrap();

        let backup = f.paths.claude_settings.with_file_name(SETTINGS_BACKUP);
        assert_eq!(
            serde_json::from_str::<Value>(&std::fs::read_to_string(&backup).unwrap()).unwrap()
                ["model"],
            "the original"
        );

        // A later preset change must not overwrite that copy with our own.
        enable(&f.paths, &mut f.our, &req(&f.source, Preset::Full)).unwrap();
        assert!(std::fs::read_to_string(&backup)
            .unwrap()
            .contains("the original"));
    }

    #[test]
    fn a_moved_backup_root_is_healed_on_the_next_start() {
        let mut f = Fixture::new();
        enable(&f.paths, &mut f.our, &req(&f.source, Preset::Full)).unwrap();

        // What an old path looks like after the user moved their profile.
        f.write_settings(
            r#"{"statusLine":{"type":"command","command":"\"D:\\old\\bin\\cs-statusline.exe\" --preset lean"}}"#,
        );
        assert_eq!(status(&f.paths, &f.our).standing, Standing::Drifted);

        let healed = heal(&f.paths, &mut f.our, &f.source, "0.1.0", Some(10)).unwrap();
        assert_eq!(healed.standing, Standing::Settled);
        assert_eq!(
            f.settings()["statusLine"]["command"].as_str().unwrap(),
            format!(
                "\"{}\" --preset full",
                binary_path(&f.paths.backup_root).display()
            ),
            "the stored preset wins, not what the stale command said"
        );
    }

    #[test]
    fn healing_does_not_fight_another_tool_for_the_status_line() {
        let mut f = Fixture::new();
        enable(&f.paths, &mut f.our, &req(&f.source, Preset::Standard)).unwrap();
        f.write_settings(r#"{"statusLine":{"type":"command","command":"bunx ccstatusline"}}"#);

        let status = heal(&f.paths, &mut f.our, &f.source, "0.1.0", Some(10)).unwrap();
        assert_eq!(status.standing, Standing::Foreign);
        assert_eq!(f.settings()["statusLine"]["command"], "bunx ccstatusline");
    }

    #[test]
    fn healing_puts_back_a_block_deleted_by_hand() {
        let mut f = Fixture::new();
        enable(&f.paths, &mut f.our, &req(&f.source, Preset::Standard)).unwrap();
        f.write_settings(r#"{"model":"opus"}"#);

        heal(&f.paths, &mut f.our, &f.source, "0.1.0", Some(10)).unwrap();
        assert!(is_ours(
            f.settings()["statusLine"]["command"].as_str().unwrap()
        ));
    }

    #[test]
    fn healing_is_quiet_when_the_feature_is_off() {
        let mut f = Fixture::new();
        f.write_settings(r#"{"statusLine":{"type":"command","command":"someone-else"}}"#);
        heal(&f.paths, &mut f.our, &f.source, "0.1.0", Some(10)).unwrap();
        assert_eq!(f.settings()["statusLine"]["command"], "someone-else");
    }

    #[test]
    fn an_unsupported_refresh_interval_is_left_out_rather_than_written() {
        let mut f = Fixture::new();
        let mut request = req(&f.source, Preset::Standard);
        request.refresh_interval = None;
        enable(&f.paths, &mut f.our, &request).unwrap();
        assert!(f.settings()["statusLine"].get("refreshInterval").is_none());

        // Out of Claude Code's accepted range is the same as unsupported.
        let mut request = req(&f.source, Preset::Standard);
        request.refresh_interval = Some(900);
        enable(&f.paths, &mut f.our, &request).unwrap();
        assert!(f.settings()["statusLine"].get("refreshInterval").is_none());
    }

    #[test]
    fn a_command_is_recognised_as_ours_by_the_program_it_runs() {
        assert!(is_ours(
            r#""C:\Users\A B\.claude-swap-backup\bin\cs-statusline.exe" --preset full"#
        ));
        assert!(is_ours(
            "/home/me/.local/share/claude-swap/bin/cs-statusline --preset lean"
        ));
        assert!(!is_ours("npx -y ccstatusline@latest"));
        assert!(!is_ours("bunx -y ccstatusline@2.2.27"));
        assert!(!is_ours(""));
        assert!(!is_ours("/usr/local/bin/my-cs-statusline-wrapper"));
    }

    #[test]
    fn the_path_is_quoted_even_when_it_looks_harmless() {
        let command = command_for(Path::new("/tmp/bin/cs-statusline"), Preset::Lean);
        assert_eq!(command, "\"/tmp/bin/cs-statusline\" --preset lean");
        assert_eq!(first_token(&command), "/tmp/bin/cs-statusline");

        let spaced = command_for(
            Path::new(r"C:\Program Files\cs-statusline.exe"),
            Preset::Full,
        );
        assert_eq!(first_token(&spaced), r"C:\Program Files\cs-statusline.exe");
    }
}
