//! Which conversations can actually be resumed.
//!
//! A listed session is not necessarily one `claude --resume` can open. This
//! decides, from the transcripts themselves, what is offered:
//!
//! - it holds a real exchange — a session opened and left with `/exit` or
//!   `/login` has nothing to resume, and Claude Code says so;
//! - nothing is running it — resuming a session another terminal is still
//!   writing interleaves both into one transcript;
//! - Claude Code itself would offer it — sessions started by `claude -p` or the
//!   Agent SDK, and ones opened with `/loop`, are kept out of its own lists;
//! - it is resumed against the config home that holds it. Claude Code only
//!   looks in its own `CLAUDE_CONFIG_DIR`, so a conversation written in a
//!   session-mode profile is invisible to the default login, and the other way
//!   round; a profile no managed account owns can no longer be launched at all.
//!
//! Supervised runs make the same promise from the other side: a journal record
//! offers to continue a conversation, and [`run_blocker`] says when it cannot.

use std::collections::HashSet;
use std::path::{Path, PathBuf};

use crate::agentruns::{AgentRun, Unresumable};
use crate::cleanup::HistoryRoot;
use crate::projects::{self, path_key, ProjectSummary, ResumeTarget, SessionSummary};
use crate::session;

/// Whether Claude Code would offer this session for resuming, running or not.
#[must_use]
pub fn is_offerable(session: &SessionSummary) -> bool {
    session.has_conversation
        && !session.loop_session
        && !session
            .entrypoint
            .as_deref()
            .is_some_and(|e| e.starts_with("sdk"))
}

/// Session ids a Claude Code process is running right now, in any home.
fn live_ids(roots: &[HistoryRoot]) -> HashSet<String> {
    roots
        .iter()
        .flat_map(|r| session::live_sessions_for(&r.home()))
        .map(|s| s.session_id)
        .filter(|id| !id.is_empty())
        .collect()
}

fn root_for<'a>(roots: &'a [HistoryRoot], home: &Path) -> Option<&'a HistoryRoot> {
    let key = path_key(&home.to_string_lossy());
    roots
        .iter()
        .find(|r| path_key(&r.home().to_string_lossy()) == key)
}

/// `<home>` for a transcript folder `<home>/projects/<folder>`.
fn home_of_folder(folder: &Path) -> Option<&Path> {
    folder
        .parent()
        .filter(|p| {
            p.file_name()
                .is_some_and(|n| n.eq_ignore_ascii_case("projects"))
        })
        .and_then(Path::parent)
}

/// Fill in each directory's resume target, and whether the directory exists.
pub fn annotate_projects(projects: &mut [ProjectSummary], roots: &[HistoryRoot]) {
    let live = live_ids(roots);
    for project in projects.iter_mut() {
        project.directory_exists = Path::new(&project.path).is_dir();
        project.resume = project
            .transcript_dirs
            .iter()
            .filter_map(|dir| newest_resumable(Path::new(dir), roots, &live))
            .max_by_key(|t| t.modified_ms);
    }
}

/// The newest session in one transcript folder that can be resumed.
fn newest_resumable(
    folder: &Path,
    roots: &[HistoryRoot],
    live: &HashSet<String>,
) -> Option<ResumeTarget> {
    let home = home_of_folder(folder)?;
    let root = root_for(roots, home)?;
    if root.account_removed {
        return None;
    }

    let mut files: Vec<(i64, PathBuf)> = std::fs::read_dir(folder)
        .ok()?
        .flatten()
        .filter(|e| e.path().extension().is_some_and(|x| x == "jsonl"))
        .map(|e| (e.metadata().map_or(0, |m| projects::mtime_ms(&m)), e.path()))
        .collect();
    files.sort_by(|a, b| b.0.cmp(&a.0));

    files.into_iter().find_map(|(modified_ms, file)| {
        let head = projects::read_head(&file);
        (is_offerable(&head) && !live.contains(&head.id)).then(|| ResumeTarget {
            session_id: head.id,
            config_home: home.to_string_lossy().to_string(),
            profile_number: root.profile_number,
            prompt: head.first_prompt,
            title: head.title,
            modified_ms,
        })
    })
}

/// One recently updated conversation, as the home page and the conversations
/// window list it.
#[derive(Clone, Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RecentSession {
    pub session_id: String,
    /// Working directory the conversation belongs to.
    pub path: String,
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub prompt: Option<String>,
    /// Claude Code's title for it, when the transcript has one.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub title: Option<String>,
    /// Last write to the transcript, epoch ms.
    pub modified_ms: i64,
    /// Config home holding the transcript; resuming has to happen there.
    pub config_home: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub profile_number: Option<u32>,
    /// Running right now. Listed, because it is the newest work and leaving it
    /// out made the list look stale; not resumable, because resuming it would
    /// put two terminals into one transcript.
    pub live: bool,
    /// The transcript itself — how a deletion names the conversation.
    pub file: String,
    /// The transcript plus what belongs to it (subagent output, spilled tool
    /// results, file snapshots): what deleting the conversation frees.
    pub total_bytes: u64,
}

/// The most recently updated conversations on this machine, newest first.
///
/// Per conversation, not per directory: a directory worked in all day
/// contributes every conversation touched, rather than one busy directory
/// hiding the rest of the day's work behind its single newest entry while
/// last week's conversations elsewhere filled the list. Skips what Claude Code
/// itself would not offer (see [`is_offerable`]), conversations whose directory
/// is gone, and ones in a profile no account owns. Transcripts are read newest
/// first, and only until `limit` qualify.
#[must_use]
pub fn recent_sessions(
    directories: &[ProjectSummary],
    roots: &[HistoryRoot],
    limit: usize,
) -> Vec<RecentSession> {
    let live = live_ids(roots);
    let mut candidates: Vec<(i64, PathBuf, &ProjectSummary, &HistoryRoot)> = Vec::new();
    for directory in directories.iter().filter(|d| Path::new(&d.path).is_dir()) {
        for dir in &directory.transcript_dirs {
            let folder = Path::new(dir);
            let Some(root) = home_of_folder(folder).and_then(|home| root_for(roots, home)) else {
                continue;
            };
            if root.account_removed {
                continue;
            }
            for entry in std::fs::read_dir(folder).into_iter().flatten().flatten() {
                let file = entry.path();
                if file.extension().is_some_and(|x| x == "jsonl") {
                    let modified = entry.metadata().map_or(0, |m| projects::mtime_ms(&m));
                    candidates.push((modified, file, directory, root));
                }
            }
        }
    }
    candidates.sort_by(|a, b| b.0.cmp(&a.0));

    let mut out = Vec::new();
    for (modified_ms, file, directory, root) in candidates {
        if out.len() >= limit {
            break;
        }
        let head = projects::read_head(&file);
        if !is_offerable(&head) {
            continue;
        }
        out.push(RecentSession {
            live: live.contains(&head.id),
            total_bytes: crate::cleanup::session_bytes(&file),
            file: file.to_string_lossy().to_string(),
            session_id: head.id,
            path: directory.path.clone(),
            name: directory.name.clone(),
            prompt: head.first_prompt,
            title: head.title,
            modified_ms,
            config_home: root.home().to_string_lossy().to_string(),
            profile_number: root.profile_number,
        });
    }
    out
}

/// Mark each session with its home's profile, whether it is running, and
/// whether it can be resumed from the directory window.
///
/// Automated and `/loop` sessions stay resumable here: that window is where a
/// user goes looking for a specific conversation, and `--resume <id>` opens
/// them. Only the quick menu follows Claude Code's own choice of what to offer.
pub fn annotate_sessions(sessions: &mut [SessionSummary], roots: &[HistoryRoot]) {
    let live = live_ids(roots);
    for s in sessions.iter_mut() {
        s.live = live.contains(&s.id);
        let root = Path::new(&s.file)
            .parent()
            .and_then(home_of_folder)
            .and_then(|home| root_for(roots, home));
        if let Some(root) = root {
            s.config_home = Some(root.home().to_string_lossy().to_string());
            s.profile_number = root.profile_number;
        }
        s.resumable = s.has_conversation && !s.live && root.is_some_and(|r| !r.account_removed);
    }
}

/// Whether a session's transcript is anywhere in a config home.
#[must_use]
pub fn transcript_exists(home: &Path, session_id: &str) -> bool {
    let safe = !session_id.is_empty()
        && session_id.len() <= 128
        && session_id
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_');
    safe && std::fs::read_dir(home.join("projects"))
        .into_iter()
        .flatten()
        .flatten()
        .any(|e| e.path().join(format!("{session_id}.jsonl")).is_file())
}

/// Why a supervised run can no longer be resumed, if it cannot.
///
/// A directory on a volume that is not there right now is not treated as gone —
/// see [`crate::cleanup::directory_is_gone`] — so unplugging a drive never costs
/// a record that would work again once it is back.
#[must_use]
pub fn run_blocker(run: &AgentRun, default_home: &Path) -> Option<Unresumable> {
    let home = run
        .config_dir
        .as_deref()
        .map_or_else(|| default_home.to_path_buf(), PathBuf::from);
    if run.config_dir.is_some() && !home.is_dir() {
        return Some(Unresumable::ProfileGone);
    }
    if crate::cleanup::directory_is_gone(&run.cwd) {
        return Some(Unresumable::DirectoryGone);
    }
    let id = run.session_id.as_deref()?;
    (!transcript_exists(&home, id)).then_some(Unresumable::TranscriptGone)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::Platform;
    use crate::paths::PathEnv;
    use serde_json::json;
    use std::time::{Duration, SystemTime};

    fn env_at(home: &Path) -> PathEnv {
        PathEnv {
            home: home.to_path_buf(),
            claude_config_dir: None,
            xdg_data_home: None,
            platform: Platform::Windows,
        }
    }

    /// A transcript of `lines`, last written `age_s` seconds ago.
    fn transcript(folder: &Path, id: &str, lines: &[serde_json::Value], age_s: u64) -> PathBuf {
        std::fs::create_dir_all(folder).unwrap();
        let file = folder.join(format!("{id}.jsonl"));
        let mut body = String::new();
        for line in lines {
            body.push_str(&line.to_string());
            body.push('\n');
        }
        std::fs::write(&file, body).unwrap();
        std::fs::File::options()
            .write(true)
            .open(&file)
            .unwrap()
            .set_modified(SystemTime::now() - Duration::from_secs(age_s))
            .unwrap();
        file
    }

    fn talk(cwd: &str, entrypoint: &str, prompt: &str) -> Vec<serde_json::Value> {
        vec![
            json!({"type": "user", "cwd": cwd, "entrypoint": entrypoint,
                   "message": {"content": prompt}}),
            json!({"type": "assistant", "message": {"model": "claude-opus-5",
                   "content": [{"type": "text", "text": "ok"}]}}),
        ]
    }

    #[test]
    fn only_a_session_that_can_really_be_resumed_is_offered() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let work = home.join("work");
        std::fs::create_dir_all(&work).unwrap();
        let cwd = work.to_string_lossy().to_string();
        let folder = home.join(".claude").join("projects").join("work");

        // Newest to oldest: running, empty, automated, /loop, and the real one.
        transcript(&folder, "running", &talk(&cwd, "cli", "still going"), 10);
        transcript(
            &folder,
            "empty",
            &[
                json!({"type": "user", "cwd": cwd, "message": {"content": "<command-name>/login</command-name>"}}),
                json!({"type": "assistant", "message": {"model": "<synthetic>", "content": []}}),
                json!({"type": "user", "cwd": cwd, "message": {"content": "<command-name>/exit</command-name>"}}),
            ],
            20,
        );
        transcript(
            &folder,
            "automated",
            &talk(&cwd, "sdk-cli", "run the job"),
            30,
        );
        transcript(
            &folder,
            "looping",
            &[
                json!({"type": "user", "cwd": cwd, "message": {"content": "<command-name>/loop</command-name>"}}),
                json!({"type": "assistant", "message": {"model": "claude-opus-5", "content": []}}),
            ],
            40,
        );
        transcript(&folder, "real", &talk(&cwd, "cli", "修一下构建"), 50);

        let me = std::process::id();
        let pid_file = home
            .join(".claude")
            .join("sessions")
            .join(format!("{me}.json"));
        std::fs::create_dir_all(pid_file.parent().unwrap()).unwrap();
        std::fs::write(
            &pid_file,
            json!({"pid": me, "sessionId": "running", "cwd": cwd,
                   "startedAt": crate::agentruns::now_ms()})
            .to_string(),
        )
        .unwrap();

        let env = env_at(home);
        let roots = crate::cleanup::history_roots(&env, &home.join("backup"), None);
        let mut listed = projects::list_projects(&env).unwrap();
        annotate_projects(&mut listed, &roots);

        assert_eq!(listed.len(), 1);
        assert!(listed[0].directory_exists);
        let resume = listed[0].resume.as_ref().expect("the real conversation");
        assert_eq!(resume.session_id, "real");
        assert_eq!(resume.prompt.as_deref(), Some("修一下构建"));
        assert_eq!(resume.profile_number, None);
    }

    #[test]
    fn a_directory_with_nothing_resumable_offers_nothing() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let folder = home.join(".claude").join("projects").join("gone");
        let cwd = home.join("gone").to_string_lossy().to_string();
        transcript(
            &folder,
            "empty",
            &[
                json!({"type": "user", "cwd": cwd, "message": {"content": "<command-name>/exit</command-name>"}}),
            ],
            5,
        );

        let env = env_at(home);
        let roots = crate::cleanup::history_roots(&env, &home.join("backup"), None);
        let mut listed = projects::list_projects(&env).unwrap();
        annotate_projects(&mut listed, &roots);

        assert!(listed[0].resume.is_none());
        assert!(!listed[0].directory_exists);
    }

    #[test]
    fn a_profile_conversation_is_resumed_in_its_profile() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let backup = home.join("backup");
        let profile = session::session_dir_for(&backup, 2, "b@x.com");
        let cwd = home.to_string_lossy().to_string();
        let file = transcript(
            &profile.join("projects").join("here"),
            "in-profile",
            &talk(&cwd, "cli", "hi"),
            5,
        );

        let env = env_at(home);
        let managed = [profile.clone()];
        let roots = crate::cleanup::history_roots(&env, &backup, Some(&managed));
        let mut listed =
            projects::list_projects_in(&crate::projects::scan_envs(&env, &backup)).unwrap();
        annotate_projects(&mut listed, &roots);

        let resume = listed[0].resume.as_ref().unwrap();
        assert_eq!(resume.profile_number, Some(2));
        assert_eq!(
            path_key(&resume.config_home),
            path_key(&profile.to_string_lossy())
        );

        let mut sessions = projects::list_sessions(file.parent().unwrap()).unwrap();
        annotate_sessions(&mut sessions, &roots);
        assert_eq!(sessions[0].profile_number, Some(2));
        assert!(sessions[0].resumable);

        // Once no account owns the profile, nothing in it can be launched.
        let orphaned = crate::cleanup::history_roots(&env, &backup, Some(&[]));
        let mut listed =
            projects::list_projects_in(&crate::projects::scan_envs(&env, &backup)).unwrap();
        annotate_projects(&mut listed, &orphaned);
        assert!(listed[0].resume.is_none());
        annotate_sessions(&mut sessions, &orphaned);
        assert!(!sessions[0].resumable);
    }

    #[test]
    fn a_run_is_unresumable_once_its_directory_transcript_or_profile_is_gone() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path().join(".claude");
        let work = tmp.path().join("work");
        std::fs::create_dir_all(&work).unwrap();
        transcript(
            &home.join("projects").join("work"),
            "kept",
            &talk("x", "sdk-cli", "p"),
            5,
        );

        let run = |cwd: &Path, session: &str, config_dir: Option<&Path>| -> AgentRun {
            let mut v = json!({
                "id": format!("run-{session}"), "sessionId": session,
                "cwd": cwd.to_string_lossy(), "prompt": "p", "status": "interrupted",
                "ownerPid": 1, "createdMs": 1, "updatedMs": 1
            });
            if let Some(dir) = config_dir {
                v["configDir"] = json!(dir.to_string_lossy());
            }
            serde_json::from_value(v).unwrap()
        };

        assert_eq!(run_blocker(&run(&work, "kept", None), &home), None);
        assert_eq!(
            run_blocker(&run(&tmp.path().join("deleted"), "kept", None), &home),
            Some(Unresumable::DirectoryGone)
        );
        assert_eq!(
            run_blocker(&run(&work, "swept", None), &home),
            Some(Unresumable::TranscriptGone)
        );
        assert_eq!(
            run_blocker(
                &run(&work, "kept", Some(&tmp.path().join("no-profile"))),
                &home
            ),
            Some(Unresumable::ProfileGone)
        );
    }

    #[test]
    fn sessions_are_listed_most_recently_updated_first() {
        // Began a fortnight ago but written to minutes ago, against begun
        // yesterday and left: the one still in use belongs on top.
        let tmp = tempfile::tempdir().unwrap();
        let folder = tmp.path().join(".claude").join("projects").join("work");
        let opened = |at: &str, text: &str| {
            vec![json!({"type": "user", "cwd": "x", "timestamp": at,
                        "message": {"content": text}})]
        };
        transcript(
            &folder,
            "still-in-use",
            &opened("2026-09-01T00:00:00Z", "long-running work"),
            60,
        );
        transcript(
            &folder,
            "left-yesterday",
            &opened("2026-09-13T00:00:00Z", "a quick question"),
            3600,
        );

        let sessions = projects::list_sessions(&folder).unwrap();
        let ids: Vec<&str> = sessions.iter().map(|s| s.id.as_str()).collect();
        assert_eq!(ids, vec!["still-in-use", "left-yesterday"]);
    }

    #[test]
    fn recent_sessions_lists_every_recent_conversation_newest_first() {
        // A busy directory contributes each conversation touched, not one; an
        // open one is listed and flagged; empty ones and ones whose directory
        // is gone are not.
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path();
        let projects_dir = home.join(".claude").join("projects");
        let busy = home.join("busy");
        let quiet = home.join("quiet");
        std::fs::create_dir_all(&busy).unwrap();
        std::fs::create_dir_all(&quiet).unwrap();
        let busy_cwd = busy.to_string_lossy().to_string();
        let quiet_cwd = quiet.to_string_lossy().to_string();
        let gone_cwd = home.join("gone").to_string_lossy().to_string();

        let busy_folder = projects_dir.join("busy");
        transcript(
            &busy_folder,
            "open-now",
            &talk(&busy_cwd, "cli", "still typing"),
            5,
        );
        transcript(
            &busy_folder,
            "afternoon",
            &talk(&busy_cwd, "cli", "afternoon work"),
            60,
        );
        transcript(
            &busy_folder,
            "exited",
            &[json!({"type": "user", "cwd": busy_cwd,
                     "message": {"content": "<command-name>/exit</command-name>"}})],
            90,
        );
        transcript(
            &busy_folder,
            "morning",
            &talk(&busy_cwd, "cli", "morning work"),
            120,
        );
        transcript(
            &projects_dir.join("gone"),
            "moved",
            &talk(&gone_cwd, "cli", "moved away"),
            30,
        );
        transcript(
            &projects_dir.join("quiet"),
            "last-week",
            &talk(&quiet_cwd, "cli", "old"),
            600,
        );

        let me = std::process::id();
        let pid_file = home
            .join(".claude")
            .join("sessions")
            .join(format!("{me}.json"));
        std::fs::create_dir_all(pid_file.parent().unwrap()).unwrap();
        std::fs::write(
            &pid_file,
            json!({"pid": me, "sessionId": "open-now", "cwd": busy_cwd,
                   "startedAt": crate::agentruns::now_ms()})
            .to_string(),
        )
        .unwrap();

        let env = env_at(home);
        let roots = crate::cleanup::history_roots(&env, &home.join("backup"), None);
        let directories = projects::list_projects(&env).unwrap();

        let recent = recent_sessions(&directories, &roots, 8);
        let ids: Vec<&str> = recent.iter().map(|r| r.session_id.as_str()).collect();
        assert_eq!(ids, vec!["open-now", "afternoon", "morning", "last-week"]);
        assert!(recent[0].live, "the open conversation is listed, flagged");
        // Enough to delete a conversation by, and to say what that frees.
        assert!(
            recent[1].file.ends_with("afternoon.jsonl"),
            "{}",
            recent[1].file
        );
        assert!(recent[1].total_bytes > 0);
        assert!(!recent[1].live);
        assert_eq!(recent[1].prompt.as_deref(), Some("afternoon work"));
        assert_eq!(recent[1].name, "busy");

        assert_eq!(recent_sessions(&directories, &roots, 2).len(), 2);
    }
}
