//! Exact locations missed by older uninstallers. Shared application files are
//! edited selectively; their parent folders never become deletion candidates.

use super::{
    fs, glob_prefix, io, is_dir_link, paths_equal, push, Candidate, Operation, Opt, Path, PathBuf,
    PathEnv, PurgeFs, Value, EXT_PREFIX, GROUP_BROWSER, GROUP_DESKTOP, GROUP_DESKTOP_DATA,
    GROUP_IDE, GROUP_PROGRAM, GROUP_RUNTIME, GROUP_THIRD,
};

const BROWSER_EXTENSION: &str = "fcoeoabgfenejglbffodgkkbkcdhcgfn";
const REGISTRY_KEYS: &[&str] = &[
    r"Software\Classes\claude-cli",
    r"Software\Google\Chrome\NativeMessagingHosts\com.anthropic.claude_code_browser_extension",
    r"Software\Microsoft\Edge\NativeMessagingHosts\com.anthropic.claude_code_browser_extension",
    r"Software\Mozilla\NativeMessagingHosts\com.anthropic.claude_code_browser_extension",
    r"Software\WOW6432Node\Google\Chrome\NativeMessagingHosts\com.anthropic.claude_code_browser_extension",
    r"Software\WOW6432Node\Microsoft\Edge\NativeMessagingHosts\com.anthropic.claude_code_browser_extension",
];

pub(super) fn user_folder(env: &PathEnv, key: &str, fallback: &str, real: bool) -> PathBuf {
    if real {
        if let Some(path) = std::env::var_os(key)
            .map(PathBuf::from)
            .filter(|p| p.is_absolute())
        {
            return path;
        }
    }
    env.home.join(fallback)
}

pub(super) fn desktop_folder(env: &PathEnv, real: bool) -> PathBuf {
    #[cfg(windows)]
    if real {
        let root = winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER);
        if let Ok(key) = root
            .open_subkey(r"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders")
        {
            if let Ok(value) = key.get_value::<String, _>("Desktop") {
                let path =
                    PathBuf::from(value.replace("%USERPROFILE%", &env.home.to_string_lossy()));
                if path.is_absolute() {
                    return path;
                }
            }
        }
    }
    let _ = real;
    env.home.join("Desktop")
}

pub(super) fn temp_roots(env: &PathEnv, default: &Path, real: bool) -> Vec<PathBuf> {
    let mut roots = vec![default.to_path_buf()];
    if real {
        for key in ["TEMP", "TMP"] {
            if let Some(path) = std::env::var_os(key)
                .map(PathBuf::from)
                .filter(|p| p.is_absolute())
            {
                add_unique(&mut roots, path);
            }
        }
        // Earlier installs can leave the same user's Temp on another fixed drive.
        // Only this exact profile layout is considered; never walk entire drives.
        #[cfg(windows)]
        if env
            .home
            .parent()
            .and_then(Path::file_name)
            .is_some_and(|n| n.eq_ignore_ascii_case("Users"))
        {
            if let Some(user) = env.home.file_name() {
                extern "system" {
                    fn GetLogicalDrives() -> u32;
                    fn GetDriveTypeW(root: *const u16) -> u32;
                }
                let mask = unsafe { GetLogicalDrives() };
                for i in 0u8..26 {
                    if mask & (1 << i) == 0 {
                        continue;
                    }
                    let drive = format!("{}:\\", char::from(b'A' + i));
                    let wide: Vec<u16> = drive.encode_utf16().chain(Some(0)).collect();
                    if unsafe { GetDriveTypeW(wide.as_ptr()) } != 3 {
                        continue;
                    }
                    let path = PathBuf::from(drive)
                        .join("Users")
                        .join(user)
                        .join("AppData/Local/Temp");
                    if path.is_dir() {
                        add_unique(&mut roots, path);
                    }
                }
            }
        }
    }
    let _ = env;
    roots
}

fn add_unique(roots: &mut Vec<PathBuf>, path: PathBuf) {
    if !roots.iter().any(|p| paths_equal(p, &path)) {
        roots.push(path);
    }
}

pub(super) fn extend_catalog(stores: &PurgeFs, out: &mut Vec<Candidate>) {
    program_and_runtime(stores, out);
    ide(stores, out);
    desktop_data(stores, out);
    browsers(stores, out);
    third_party(stores, out);
    if stores.allow_machine_stores {
        for (i, key) in REGISTRY_KEYS.iter().enumerate() {
            out.push(Candidate {
                id: format!("registry-{i}"),
                group: GROUP_RUNTIME,
                option: None,
                path: PathBuf::from(format!("HKCU\\{key}")),
                follow_link: false,
                operation: Operation::Registry,
            });
        }
    }
}

fn program_and_runtime(stores: &PurgeFs, out: &mut Vec<Candidate>) {
    let bin = stores.env.home.join(".local/bin");
    for (i, path) in glob_prefix(&bin, "claude.exe.old.")
        .into_iter()
        .filter(|p| {
            p.file_name().and_then(|n| n.to_str()).is_some_and(|n| {
                n.get("claude.exe.old.".len()..).is_some_and(|s| {
                    !s.is_empty()
                        && s.split('.').all(|part| {
                            !part.is_empty() && part.bytes().all(|b| b.is_ascii_digit())
                        })
                })
            }) && !p.is_dir()
        })
        .enumerate()
    {
        push(
            out,
            &format!("native-bin-old-{i}"),
            GROUP_PROGRAM,
            None,
            path,
            false,
        );
    }
    push(
        out,
        "native-update-cache",
        GROUP_PROGRAM,
        None,
        stores.env.home.join(".cache/claude"),
        false,
    );
    push(
        out,
        "browser-native-host",
        GROUP_RUNTIME,
        None,
        stores.roaming.join("Claude Code"),
        false,
    );
    for (i, temp) in stores.temp_roots.iter().enumerate() {
        if !paths_equal(temp, &stores.temp) {
            push(
                out,
                &format!("temp-claude-extra-{i}"),
                GROUP_RUNTIME,
                None,
                temp.join("claude"),
                false,
            );
        }
    }
}

pub(super) fn extension_name(name: &str) -> bool {
    let name = name.to_ascii_lowercase();
    name == EXT_PREFIX
        || name
            .strip_prefix(EXT_PREFIX)
            .and_then(|s| s.strip_prefix('-'))
            .is_some_and(|s| s.as_bytes().first().is_some_and(u8::is_ascii_digit))
}

fn ide(stores: &PurgeFs, out: &mut Vec<Candidate>) {
    for (id, home, app) in [
        ("vscode", ".vscode", "Code"),
        ("vscode-insiders", ".vscode-insiders", "Code - Insiders"),
        ("cursor", ".cursor", "Cursor"),
        ("windsurf", ".windsurf", "Windsurf"),
    ] {
        let extensions = stores.env.home.join(home).join("extensions");
        for name in ["extensions.json", ".obsolete"] {
            add_index(out, &format!("{id}-index-{name}"), extensions.join(name));
        }
        let app = stores.roaming.join(app);
        for (i, profile) in child_dirs(&app.join("User/profiles"))
            .into_iter()
            .enumerate()
        {
            add_index(
                out,
                &format!("{id}-profile-index-{i}"),
                profile.join("extensions.json"),
            );
        }
        for (i, path) in glob_prefix(&app.join("CachedExtensionVSIXs"), EXT_PREFIX)
            .into_iter()
            .filter(|p| {
                p.file_name()
                    .and_then(|n| n.to_str())
                    .is_some_and(extension_name)
            })
            .enumerate()
        {
            push(
                out,
                &format!("{id}-vsix-{i}"),
                GROUP_IDE,
                Some(Opt::Ide),
                path,
                false,
            );
        }
        push(
            out,
            &format!("{id}-agent-sdk"),
            GROUP_IDE,
            Some(Opt::Ide),
            app.join("agent-host/sdk-cache/claude"),
            false,
        );
        push(
            out,
            &format!("{id}-extension-storage"),
            GROUP_IDE,
            Some(Opt::Ide),
            app.join("User/globalStorage").join(EXT_PREFIX),
            false,
        );
    }
}

fn add_index(out: &mut Vec<Candidate>, id: &str, path: PathBuf) {
    out.push(Candidate {
        id: id.into(),
        group: GROUP_IDE,
        option: Some(Opt::Ide),
        path,
        follow_link: false,
        operation: Operation::IdeIndex,
    });
}

fn desktop_data(stores: &PurgeFs, out: &mut Vec<Candidate>) {
    for (id, path) in [
        ("desktop-all-roaming", stores.roaming.join("Claude")),
        ("desktop-local-data", stores.local.join("Claude-Data")),
    ] {
        push(
            out,
            id,
            GROUP_DESKTOP_DATA,
            Some(Opt::DesktopData),
            path,
            false,
        );
    }
    for (i, root) in stores.program_data.iter().enumerate() {
        push(
            out,
            &format!("desktop-all-programdata-{i}"),
            GROUP_DESKTOP_DATA,
            Some(Opt::DesktopData),
            root.join("Claude"),
            false,
        );
    }
    let shaders = stores.env.home.join("AppData/LocalLow/Intel/ShaderCache");
    for (i, path) in glob_prefix(&shaders, "DeviceId=")
        .into_iter()
        .filter(|p| {
            p.file_name()
                .and_then(|n| n.to_str())
                .is_some_and(|n| n.contains("_AppName=claude=_"))
        })
        .enumerate()
    {
        push(
            out,
            &format!("desktop-shader-{i}"),
            GROUP_DESKTOP_DATA,
            Some(Opt::DesktopData),
            path,
            false,
        );
    }
}

fn browsers(stores: &PurgeFs, out: &mut Vec<Candidate>) {
    for (browser, rel) in [
        ("chrome", "Google/Chrome/User Data"),
        ("edge", "Microsoft/Edge/User Data"),
        ("brave", "BraveSoftware/Brave-Browser/User Data"),
    ] {
        for (i, profile) in child_dirs(&stores.local.join(rel)).into_iter().enumerate() {
            for dir in [
                "Extensions",
                "Local Extension Settings",
                "Sync Extension Settings",
                "Managed Extension Settings",
            ] {
                push(
                    out,
                    &format!("browser-{browser}-{i}-{dir}"),
                    GROUP_BROWSER,
                    Some(Opt::Browser),
                    profile.join(dir).join(BROWSER_EXTENSION),
                    false,
                );
            }
            push(
                out,
                &format!("browser-{browser}-{i}-indexeddb"),
                GROUP_BROWSER,
                Some(Opt::Browser),
                profile.join("IndexedDB").join(format!(
                    "chrome-extension_{BROWSER_EXTENSION}_0.indexeddb.leveldb"
                )),
                false,
            );
        }
    }
}

fn third_party(stores: &PurgeFs, out: &mut Vec<Candidate>) {
    for (id, path) in [
        (
            "third-yunyi-backup",
            stores.env.home.join(".yunyi-cli/backups/claude"),
        ),
        (
            "third-kimi-cmd",
            stores.env.home.join("bin/kimi-claude.cmd"),
        ),
        (
            "third-kimi-ps1",
            stores.env.home.join("bin/kimi-claude.ps1"),
        ),
        (
            "third-kimi-shortcut",
            stores.desktop.join("Kimi Claude Code.lnk"),
        ),
        (
            "third-t3-status",
            stores.env.home.join(".t3/caches/claudeAgent.json"),
        ),
        (
            "third-usage-status",
            stores.local.join("AgentUsageLite/claude-code-status.json"),
        ),
    ] {
        push(out, id, GROUP_THIRD, Some(Opt::Third), path, false);
    }
}

fn child_dirs(path: &Path) -> Vec<PathBuf> {
    if is_dir_link(path) {
        return Vec::new();
    }
    let Ok(entries) = fs::read_dir(path) else {
        return Vec::new();
    };
    let mut children: Vec<_> = entries
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.is_dir() && !is_dir_link(p))
        .collect();
    children.sort();
    children
}

fn edit_ide_index(value: &mut Value) -> io::Result<bool> {
    if let Some(items) = value.as_array_mut() {
        let before = items.len();
        items.retain(|v| {
            !v.pointer("/identifier/id")
                .and_then(Value::as_str)
                .is_some_and(|id| id.eq_ignore_ascii_case(EXT_PREFIX))
        });
        Ok(before != items.len())
    } else if let Some(items) = value.as_object_mut() {
        let before = items.len();
        items.retain(|key, _| !extension_name(key));
        Ok(before != items.len())
    } else {
        Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Unsupported IDE extension index",
        ))
    }
}

pub(super) fn ide_index_contains_claude(path: &Path) -> bool {
    if is_dir_link(path) {
        return false;
    }
    let Ok(text) = fs::read_to_string(path) else {
        return false;
    };
    match serde_json::from_str::<Value>(&text) {
        Ok(mut value) => edit_ide_index(&mut value).unwrap_or(false),
        // Surface a corrupt index containing a Claude record as a failure on
        // apply, instead of claiming the extension was completely removed.
        Err(_) => text.to_ascii_lowercase().contains(EXT_PREFIX),
    }
}

pub(super) fn clean_ide_index(path: &Path) -> io::Result<()> {
    if is_dir_link(path) {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "Refusing to edit a linked IDE index",
        ));
    }
    let mut value: Value = serde_json::from_slice(&fs::read(path)?)?;
    if edit_ide_index(&mut value)? {
        crate::fsutil::atomic_write(path, &serde_json::to_vec(&value)?)?;
    }
    Ok(())
}

fn registry_key(path: &Path) -> Option<&str> {
    path.to_str()?
        .strip_prefix("HKCU\\")
        .filter(|key| REGISTRY_KEYS.contains(key))
}

pub(super) fn registry_exists(path: &Path) -> bool {
    let Some(key) = registry_key(path) else {
        return false;
    };
    #[cfg(windows)]
    {
        match winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER).open_subkey(key) {
            Ok(_) => true,
            Err(e) => e.kind() != io::ErrorKind::NotFound,
        }
    }
    #[cfg(not(windows))]
    {
        let _ = key;
        false
    }
}

pub(super) fn remove_registry(path: &Path) -> io::Result<()> {
    let key = registry_key(path).ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::PermissionDenied,
            "Registry key is not in the uninstall list",
        )
    })?;
    #[cfg(windows)]
    {
        remove_registry_from(
            &winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER),
            key,
        )
    }
    #[cfg(not(windows))]
    {
        let _ = key;
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Windows registry is unavailable",
        ))
    }
}

#[cfg(windows)]
fn remove_registry_from(root: &winreg::RegKey, key: &str) -> io::Result<()> {
    if !REGISTRY_KEYS.contains(&key) {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "Registry key is not in the uninstall list",
        ));
    }
    match root.delete_subkey_all(key) {
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
        result => result,
    }
}

pub(super) fn running_app_problems(stores: &PurgeFs, selected: &[Candidate]) -> Vec<String> {
    if !stores.allow_machine_stores {
        return Vec::new();
    }
    let groups: Vec<_> = selected
        .iter()
        .filter(|c| super::candidate_exists(c))
        .map(|c| c.group)
        .collect();
    if !groups
        .iter()
        .any(|g| [GROUP_IDE, GROUP_DESKTOP, GROUP_DESKTOP_DATA, GROUP_BROWSER].contains(g))
    {
        return Vec::new();
    }
    match running_process_names() {
        Ok(names) => problems_for_running_apps(&groups, &names),
        Err(_) => vec!["process-check-failed".into()],
    }
}

fn problems_for_running_apps(groups: &[&str], names: &[String]) -> Vec<String> {
    let checks = [
        (
            groups.contains(&GROUP_IDE),
            "ide-running",
            &[
                "code.exe",
                "code - insiders.exe",
                "cursor.exe",
                "windsurf.exe",
            ][..],
        ),
        (
            groups.contains(&GROUP_DESKTOP) || groups.contains(&GROUP_DESKTOP_DATA),
            "desktop-running",
            &["claude.exe"][..],
        ),
        (
            groups.contains(&GROUP_BROWSER),
            "browser-running",
            &["chrome.exe", "msedge.exe", "brave.exe"][..],
        ),
    ];
    checks
        .iter()
        .filter(|(enabled, _, exes)| {
            *enabled && exes.iter().any(|exe| names.iter().any(|n| n == exe))
        })
        .map(|(_, code, _)| (*code).into())
        .collect()
}

pub(super) fn running_process_names() -> io::Result<Vec<String>> {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        let output = std::process::Command::new("tasklist.exe")
            .args(["/FO", "CSV", "/NH"])
            .creation_flags(0x0800_0000)
            .output()?;
        if !output.status.success() {
            return Err(io::Error::other("tasklist failed"));
        }
        Ok(String::from_utf8_lossy(&output.stdout)
            .lines()
            .filter_map(|line| line.strip_prefix('"').and_then(|s| s.split('"').next()))
            .map(str::to_ascii_lowercase)
            .collect())
    }
    #[cfg(not(windows))]
    {
        Ok(Vec::new())
    }
}

#[cfg(test)]
mod tests {
    use super::super::{apply, catalog, plan, scan, PurgeOptions};
    use super::*;
    use serde_json::json;

    fn write(root: &Path, rel: &str, data: &[u8]) -> PathBuf {
        let path = root.join(rel);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, data).unwrap();
        path
    }

    #[test]
    fn leftovers_can_be_removed_after_the_launcher_is_already_gone() {
        let tmp = tempfile::tempdir().unwrap();
        let env = PathEnv::isolated(tmp.path());
        let old = write(
            &env.home,
            ".local/bin/claude.exe.old.1790406799517.10440",
            b"old",
        );
        let cache = write(
            &env.home,
            ".cache/claude/staging/2.1.287/claude.exe",
            b"partial",
        );
        let host = write(&env.home, "AppData/Roaming/Claude Code/ChromeNativeHost/com.anthropic.claude_code_browser_extension.json", b"{}");
        let uv = write(&env.home, ".local/bin/uv.exe", b"keep");
        let note = write(&env.home, ".local/bin/claude.exe.old.notes", b"keep");
        let backup = write(&env.home, ".claude-swap-backup/statusline.json", b"keep");
        let other_cache = write(&env.home, ".cache/other/file", b"keep");
        assert_eq!(scan(&env).install_kind, "none");
        let planned = plan(&env, &PurgeOptions::default());
        assert!(planned.problems.is_empty(), "{planned:?}");
        assert_eq!(planned.total_bytes, 12);
        let result = apply(&env, &PurgeOptions::default()).unwrap();
        assert!(result.failed.is_empty(), "{result:?}");
        for path in [old, cache, host] {
            assert!(!path.exists(), "{path:?}");
        }
        for path in [uv, note, backup, other_cache] {
            assert!(path.exists(), "{path:?}");
        }
        assert!(plan(&env, &PurgeOptions::default())
            .problems
            .contains(&"nothing".into()));
    }

    #[test]
    fn ide_cleanup_removes_only_claude_records_and_caches() {
        let tmp = tempfile::tempdir().unwrap();
        let env = PathEnv::isolated(tmp.path());
        let other = json!({"identifier":{"id":"other.extension"}, "metadata":{"keep":true}});
        let index = write(&env.home, ".vscode/extensions/extensions.json", &serde_json::to_vec(&json!([
            {"identifier":{"id":"anthropic.claude-code"},"relativeLocation":"already-deleted"}, other
        ])).unwrap());
        let obsolete = write(
            &env.home,
            ".vscode/extensions/.obsolete",
            br#"{"anthropic.claude-code-2.1.270-win32-x64":true,"other.extension-1.0":true}"#,
        );
        let vsix = write(
            &env.home,
            "AppData/Roaming/Code/CachedExtensionVSIXs/anthropic.claude-code-2.1.270-win32-x64",
            b"vsix",
        );
        let sdk = write(
            &env.home,
            "AppData/Roaming/Code/agent-host/sdk-cache/claude/0.3.220/claude.exe",
            b"sdk",
        );
        let profile = write(
            &env.home,
            "AppData/Roaming/Code/User/profiles/p1/extensions.json",
            br#"[{"identifier":{"id":"anthropic.claude-code"}}]"#,
        );
        let unrelated = [
            write(
                &env.home,
                "AppData/Roaming/Code/agent-host/sdk-cache/copilot/other.exe",
                b"keep",
            ),
            write(
                &env.home,
                ".vscode/extensions/anthropic.claude-code-helper/file",
                b"keep",
            ),
            write(
                &env.home,
                "AppData/Roaming/Code/CachedExtensionVSIXs/anthropic.claude-code-helper",
                b"keep",
            ),
        ];
        let off = PurgeOptions {
            remove_ide_extension: false,
            ..PurgeOptions::default()
        };
        assert!(plan(&env, &off).items.is_empty());
        let planned = plan(&env, &PurgeOptions::default());
        assert_eq!(
            planned.total_bytes, 7,
            "shared indexes are edited, not freed"
        );
        assert!(planned.items.iter().any(|i| i.kind == "json-entries"));
        let result = apply(&env, &PurgeOptions::default()).unwrap();
        assert!(result.failed.is_empty(), "{result:?}");
        assert_eq!(
            serde_json::from_slice::<Value>(&fs::read(index).unwrap()).unwrap(),
            json!([other])
        );
        assert_eq!(
            serde_json::from_slice::<Value>(&fs::read(obsolete).unwrap()).unwrap(),
            json!({"other.extension-1.0":true})
        );
        assert_eq!(
            serde_json::from_slice::<Value>(&fs::read(profile).unwrap()).unwrap(),
            json!([])
        );
        assert!(!vsix.exists() && !sdk.exists());
        for path in unrelated {
            assert!(path.exists(), "{path:?}");
        }
    }

    #[test]
    fn corrupt_extension_index_is_reported_without_overwriting_it() {
        let tmp = tempfile::tempdir().unwrap();
        let env = PathEnv::isolated(tmp.path());
        let body = b"[{\"identifier\":{\"id\":\"anthropic.claude-code\"";
        let path = write(&env.home, ".vscode/extensions/extensions.json", body);
        let result = apply(&env, &PurgeOptions::default()).unwrap();
        assert_eq!(result.failed.len(), 1);
        assert_eq!(fs::read(path).unwrap(), body);
    }

    #[test]
    fn desktop_cleanup_is_opt_in_and_does_not_double_count_nested_data() {
        let tmp = tempfile::tempdir().unwrap();
        let env = PathEnv::isolated(tmp.path());
        let data = [
            write(&env.home, "AppData/Roaming/Claude/Cache/file", b"123"),
            write(
                &env.home,
                "AppData/Roaming/Claude/claude-code/session",
                b"1234",
            ),
            write(&env.home, "AppData/Roaming/Claude/config.json", b"{}"),
            write(
                &env.home,
                "ProgramData/Claude/Logs/cowork-service.log",
                b"12345",
            ),
        ];
        fs::create_dir_all(env.home.join("AppData/Local/Claude-Data")).unwrap();
        let keep = write(
            &env.home,
            "AppData/Local/ClaudeSwitch/settings.json",
            b"keep",
        );
        assert!(plan(&env, &PurgeOptions::default()).items.is_empty());
        let opts = PurgeOptions {
            remove_desktop_data: true,
            remove_desktop_nested: true,
            ..PurgeOptions::default()
        };
        let planned = plan(&env, &opts);
        assert_eq!(planned.total_bytes, 14);
        assert!(!planned.items.iter().any(|i| i.id == "desktop-nested-code"));
        let result = apply(&env, &opts).unwrap();
        assert!(result.failed.is_empty(), "{result:?}");
        assert_eq!(result.bytes, 14);
        for path in data {
            assert!(!path.exists(), "{path:?}");
        }
        assert!(!env.home.join("AppData/Local/Claude-Data").exists());
        assert!(!env.home.join("ProgramData/Claude").exists());
        assert!(keep.exists());
    }

    #[test]
    fn browser_cleanup_only_removes_the_claude_extension_in_each_profile() {
        let tmp = tempfile::tempdir().unwrap();
        let env = PathEnv::isolated(tmp.path());
        let mut remove = Vec::new();
        let mut keep = Vec::new();
        for browser in ["Google/Chrome", "Microsoft/Edge"] {
            for profile in ["Default", "Profile 1"] {
                let root = env
                    .home
                    .join("AppData/Local")
                    .join(browser)
                    .join("User Data")
                    .join(profile);
                for store in [
                    "Extensions",
                    "Local Extension Settings",
                    "Sync Extension Settings",
                ] {
                    remove.push(write(
                        &root,
                        &format!("{store}/{BROWSER_EXTENSION}/file"),
                        b"claude",
                    ));
                    keep.push(write(
                        &root,
                        &format!("{store}/other-extension/file"),
                        b"keep",
                    ));
                }
                keep.push(write(&root, "Preferences", b"keep"));
                keep.push(write(&root, "Network/Cookies", b"keep"));
            }
        }
        assert!(plan(&env, &PurgeOptions::default()).items.is_empty());
        let opts = PurgeOptions {
            remove_browser_extension: true,
            ..PurgeOptions::default()
        };
        let result = apply(&env, &opts).unwrap();
        assert!(result.failed.is_empty(), "{result:?}");
        for path in remove {
            assert!(!path.exists(), "{path:?}");
        }
        for path in keep {
            assert!(path.exists(), "{path:?}");
        }
    }

    #[test]
    fn third_party_cleanup_keeps_other_providers_and_the_switcher() {
        let tmp = tempfile::tempdir().unwrap();
        let env = PathEnv::isolated(tmp.path());
        let remove = [
            write(
                &env.home,
                ".yunyi-cli/backups/claude/config.json",
                b"secret",
            ),
            write(&env.home, "bin/kimi-claude.cmd", b"launcher"),
            write(&env.home, "bin/kimi-claude.ps1", b"launcher"),
            write(&env.home, "Desktop/Kimi Claude Code.lnk", b"shortcut"),
            write(&env.home, ".t3/caches/claudeAgent.json", b"cache"),
            write(
                &env.home,
                "AppData/Local/AgentUsageLite/claude-code-status.json",
                b"cache",
            ),
        ];
        let keep = [
            write(&env.home, ".yunyi-cli/backups/other/config.json", b"keep"),
            write(&env.home, "bin/other.cmd", b"keep"),
            write(&env.home, ".claude-swap-backup/statusline.json", b"keep"),
            write(
                &env.home,
                "AppData/Local/ClaudeSwitch/settings.json",
                b"keep",
            ),
        ];
        assert!(plan(&env, &PurgeOptions::default()).items.is_empty());
        let opts = PurgeOptions {
            remove_third_party_shells: true,
            ..PurgeOptions::default()
        };
        let result = apply(&env, &opts).unwrap();
        assert!(result.failed.is_empty(), "{result:?}");
        for path in remove {
            assert!(!path.exists(), "{path:?}");
        }
        for path in keep {
            assert!(path.exists(), "{path:?}");
        }
    }

    #[test]
    fn extra_temp_roots_are_exact_and_fixture_mode_never_reads_machine_stores() {
        let tmp = tempfile::tempdir().unwrap();
        let env = PathEnv::isolated(tmp.path().join("fixture"));
        let mut stores = PurgeFs::from_env(&env);
        assert!(!stores.allow_machine_stores);
        assert_eq!(stores.temp_roots, vec![env.home.join("AppData/Local/Temp")]);
        assert!(catalog(&stores)
            .iter()
            .all(|c| c.operation != Operation::Registry));
        let old_temp = tmp.path().join("old-drive/Users/test/AppData/Local/Temp");
        let remove = write(&old_temp, "claude/run/file", b"scratch");
        let keep = write(&old_temp, "other/file", b"keep");
        stores.temp_roots.push(old_temp);
        let candidates = catalog(&stores);
        let candidate = candidates
            .iter()
            .find(|c| c.id.starts_with("temp-claude-extra-"))
            .unwrap();
        super::super::remove_candidate(candidate).unwrap();
        assert!(!remove.exists());
        assert!(keep.exists());
    }

    #[test]
    fn running_apps_block_only_the_selected_optional_groups() {
        let names = vec!["code.exe".into(), "chrome.exe".into(), "claude.exe".into()];
        assert!(problems_for_running_apps(&[GROUP_PROGRAM, GROUP_RUNTIME], &names).is_empty());
        assert_eq!(
            problems_for_running_apps(&[GROUP_IDE], &names),
            ["ide-running"]
        );
        assert_eq!(
            problems_for_running_apps(&[GROUP_BROWSER], &names),
            ["browser-running"]
        );
        assert_eq!(
            problems_for_running_apps(&[GROUP_DESKTOP_DATA], &names),
            ["desktop-running"]
        );
    }

    #[cfg(windows)]
    #[test]
    fn optional_cache_junction_removes_only_the_link_and_reports_no_freed_bytes() {
        let tmp = tempfile::tempdir().unwrap();
        let env = PathEnv::isolated(tmp.path().join("fixture"));
        let keep = write(tmp.path(), "elsewhere/important", b"keep");
        let link = env
            .home
            .join("AppData/Roaming/Code/agent-host/sdk-cache/claude");
        fs::create_dir_all(link.parent().unwrap()).unwrap();
        junction::create(keep.parent().unwrap(), &link).unwrap();
        assert_eq!(plan(&env, &PurgeOptions::default()).total_bytes, 0);
        assert!(apply(&env, &PurgeOptions::default())
            .unwrap()
            .failed
            .is_empty());
        assert!(!is_dir_link(&link));
        assert!(keep.exists());
    }

    #[cfg(windows)]
    #[test]
    fn registry_cleanup_is_limited_to_exact_keys_under_an_isolated_test_root() {
        use winreg::{enums::HKEY_CURRENT_USER, RegKey};
        struct Cleanup(String);
        impl Drop for Cleanup {
            fn drop(&mut self) {
                let _ = RegKey::predef(HKEY_CURRENT_USER).delete_subkey_all(&self.0);
            }
        }
        let tmp = tempfile::tempdir().unwrap();
        let relative = format!(
            "Software\\ClaudeSwitchPurgeTests\\{}",
            tmp.path().file_name().unwrap().to_string_lossy()
        );
        let hkcu = RegKey::predef(HKEY_CURRENT_USER);
        let (root, _) = hkcu.create_subkey(&relative).unwrap();
        let _cleanup = Cleanup(relative);
        for key in REGISTRY_KEYS {
            root.create_subkey(key).unwrap();
        }
        let unrelated = r"Software\Google\Chrome\NativeMessagingHosts\other.extension";
        root.create_subkey(unrelated).unwrap();
        for key in REGISTRY_KEYS {
            remove_registry_from(&root, key).unwrap();
            assert!(root.open_subkey(key).is_err());
        }
        assert!(root.open_subkey(unrelated).is_ok());
        assert!(remove_registry_from(&root, r"Software\Google\Chrome").is_err());
        assert!(registry_key(Path::new(r"HKCU\Software\Classes")).is_none());
    }
}
