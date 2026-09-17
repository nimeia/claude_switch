//! `cs-statusline` — the line Claude Code prints at the bottom of a terminal.
//!
//! Claude Code runs this once per repaint, hands it a JSON payload on stdin and
//! prints the first lines of stdout. So the whole program is: read three local
//! files, render, exit. No network, no engine, no locks, no subprocess — see
//! `docs/statusline.md` for why each of those is ruled out.
//!
//! Two rules govern everything here:
//!
//! - **Never fail loudly.** A status line that prints an error, or exits
//!   non-zero, does it on every repaint in someone else's terminal. Every path
//!   below ends in "print what we have" and exit 0.
//! - **Never block.** Every read is of a small local file that the engine wrote
//!   atomically; nothing here waits on anything.
//!
//! What to show is decided in [`claude_switch_core::statusline`], which the GUI
//! preview calls too, so the preview cannot drift from the real line. The one
//! thing this program decides for itself is **how wide the terminal is**, since
//! only the process inside that terminal can ask (see [`terminal_width`]).

use std::io::Read;

use chrono::Local;
use claude_switch_core::paths::{PathEnv, Paths};
use claude_switch_core::statusline::{self, Frame, Live, Preset, State};

/// Claude Code's payload is a few kilobytes. The cap is not about them — it is
/// about never reading an unbounded pipe if something else is on the other end.
const MAX_STDIN: u64 = 1 << 20;

const HELP: &str = "\
cs-statusline — the Claude Switch status line for Claude Code

USAGE:
    cs-statusline [--preset lean|standard|full] [--no-color] [--width <cols>]

Claude Code runs this itself; it is configured from the Claude Switch app
(Automation → status line), which writes the command into ~/.claude/settings.json.
The payload arrives on stdin as JSON.

OPTIONS:
    -p, --preset <name>  How much to show (default: standard)
        --no-color       Plain text; NO_COLOR in the environment does the same
    -w, --width <cols>   Terminal columns, when the console cannot be asked.
                         `full` fits on one line when there is room for it;
                         0 pins it to two lines. CS_STATUSLINE_WIDTH does the
                         same from the environment.
    -V, --version        Print the version and exit
    -h, --help           Print this help and exit
";

fn main() {
    // A panic message on stderr would be printed by the terminal on every
    // repaint. Silence it; the fallback below is what the user should see.
    std::panic::set_hook(Box::new(|_| {}));

    let opts = Options::parse(std::env::args().skip(1));
    if opts.help {
        print!("{HELP}");
        return;
    }
    if opts.version {
        println!("cs-statusline {}", claude_switch_core::VERSION);
        return;
    }

    let mut stdin = String::new();
    let _ = std::io::stdin().take(MAX_STDIN).read_to_string(&mut stdin);

    // Two attempts: the whole line, then the same line with only what Claude
    // Code itself handed us. The second cannot touch the file system, so the
    // one thing that could still fail there is gone.
    let full = std::panic::catch_unwind(|| line(&opts, &stdin, true));
    let text = full.unwrap_or_else(|_| {
        std::panic::catch_unwind(|| line(&opts, &stdin, false)).unwrap_or_default()
    });
    if !text.is_empty() {
        println!("{text}");
    }
}

/// Render one line. With `local` false, nothing on disk is consulted — that is
/// the fallback after a panic, not an ordinary mode.
fn line(opts: &Options, stdin: &str, local: bool) -> String {
    let live = Live::parse(stdin);
    let mut frame = Frame::new(opts.preset, Local::now());
    frame.color = opts.color;
    frame.width = terminal_width(opts.width);

    if local {
        let paths = Paths::resolve(PathEnv::from_process());
        frame.state = State::load(&paths.statusline_file);
        // The config home is the one Claude Code is using: under a session
        // terminal that is `$CLAUDE_CONFIG_DIR`, so each terminal names its own
        // account rather than whoever is logged in by default.
        frame.identity = statusline::identity(&paths.global_config, frame.state.as_ref());
        frame.branch = live.current_dir.as_deref().and_then(statusline::git_branch);
    }

    frame.live = live;
    statusline::render(&frame)
}

/// Columns the terminal has, or [`None`] when nothing here can say.
///
/// Worth asking because `full` is two lines only when it has to be: a terminal
/// wide enough to hold both halves gets one line, and the row saved is a row of
/// transcript the reader keeps (`claude_switch_core::statusline::render`).
///
/// Three answers, in falling order of authority:
///
/// 1. **`--width`**, or `CS_STATUSLINE_WIDTH` in the environment. The way out
///    when the probe below cannot work — and `0` is how someone pins the two
///    lines they preferred.
/// 2. **`COLUMNS`**, when whoever spawned us exported it.
/// 3. **The console itself** ([`console_width`]).
///
/// The margin comes off whatever answers: Claude Code prints this line inside
/// its own frame, and a fold that lands on the last column wraps there — two
/// ragged lines, which is worse than the tidy break we would otherwise draw.
fn terminal_width(pinned: Option<usize>) -> Option<usize> {
    let cols = pinned
        .or_else(|| env_cols("CS_STATUSLINE_WIDTH"))
        .or_else(|| env_cols("COLUMNS"))
        .or_else(console_width)?;
    Some(cols.saturating_sub(WIDTH_MARGIN))
}

/// Columns kept clear of the fold; see [`terminal_width`].
const WIDTH_MARGIN: usize = 2;

fn env_cols(name: &str) -> Option<usize> {
    parse_cols(&std::env::var(name).ok()?)
}

/// A column count, or [`None`] for anything that is not one. Nothing here is
/// worth refusing to print a line over, so a bad value is simply not an answer.
fn parse_cols(s: &str) -> Option<usize> {
    s.trim().parse().ok()
}

/// The console this process shares with Claude Code.
///
/// Our stdout is a pipe — Claude Code reads what we print — so the size cannot
/// come from it; it has to come from the console device, `CONOUT$`, which is
/// answered by a pseudo-console (Windows Terminal, VS Code) as readily as by a
/// classic console window. Bound to this call only, so it stays within the program's
/// budget: no allocation, no subprocess, one open and one query.
#[cfg(windows)]
fn console_width() -> Option<usize> {
    use std::ffi::c_void;

    type Handle = *mut c_void;

    const GENERIC_READ: u32 = 0x8000_0000;
    const GENERIC_WRITE: u32 = 0x4000_0000;
    const FILE_SHARE_READ_WRITE: u32 = 0x0000_0003;
    const OPEN_EXISTING: u32 = 3;

    #[repr(C)]
    #[derive(Default)]
    struct Coord {
        x: i16,
        y: i16,
    }

    #[repr(C)]
    #[derive(Default)]
    struct SmallRect {
        left: i16,
        top: i16,
        right: i16,
        bottom: i16,
    }

    #[repr(C)]
    #[derive(Default)]
    struct ScreenBufferInfo {
        size: Coord,
        cursor: Coord,
        attributes: u16,
        window: SmallRect,
        max_window: Coord,
    }

    #[allow(non_snake_case)]
    extern "system" {
        fn CreateFileW(
            name: *const u16,
            access: u32,
            share: u32,
            security: *mut c_void,
            disposition: u32,
            flags: u32,
            template: Handle,
        ) -> Handle;
        fn GetConsoleScreenBufferInfo(console: Handle, info: *mut ScreenBufferInfo) -> i32;
        fn CloseHandle(object: Handle) -> i32;
    }

    // `CONOUT$`, UTF-16 and NUL-terminated. Read *and* write access: the query
    // is refused with ERROR_ACCESS_DENIED without both.
    let name: [u16; 8] = [0x43, 0x4F, 0x4E, 0x4F, 0x55, 0x54, 0x24, 0];
    let invalid = usize::MAX as Handle;

    // SAFETY: `name` is NUL-terminated and outlives the call; `info` is a live
    // local of the layout the API writes; the handle is closed exactly once,
    // and only when it is not INVALID_HANDLE_VALUE.
    unsafe {
        let console = CreateFileW(
            name.as_ptr(),
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ_WRITE,
            std::ptr::null_mut(),
            OPEN_EXISTING,
            0,
            std::ptr::null_mut(),
        );
        if console == invalid {
            return None;
        }
        let mut info = ScreenBufferInfo::default();
        let ok = GetConsoleScreenBufferInfo(console, &mut info) != 0;
        CloseHandle(console);
        if !ok {
            return None;
        }
        // The window, not the buffer: a classic console can scroll a buffer
        // wider than the window, and it is the window the text has to fit.
        usize::try_from(i32::from(info.window.right) - i32::from(info.window.left) + 1).ok()
    }
}

/// No probe off Windows yet; `--width` and `COLUMNS` still answer.
///
/// The product ships on Windows (`docs/design-claude-switch.md` §3.1); the
/// equivalent here is a `TIOCGWINSZ` on `/dev/tty`, and it belongs in the same
/// change as the platform it is for rather than as untested code ahead of it.
#[cfg(not(windows))]
const fn console_width() -> Option<usize> {
    None
}

#[derive(Debug, PartialEq, Eq)]
struct Options {
    preset: Preset,
    color: bool,
    /// Columns, as pinned on the command line. [`terminal_width`] decides what
    /// to do when it is absent.
    width: Option<usize>,
    help: bool,
    version: bool,
}

impl Options {
    /// Parse the installed command line.
    ///
    /// Unknown arguments are ignored rather than rejected: this command line
    /// lives in the user's `settings.json` and may outlive the binary that
    /// wrote it, and an old binary meeting a new flag should still print a line.
    fn parse<I: IntoIterator<Item = String>>(args: I) -> Self {
        let mut opts = Self {
            preset: Preset::default(),
            color: std::env::var_os("NO_COLOR").is_none(),
            width: None,
            help: false,
            version: false,
        };
        let mut args = args.into_iter().peekable();
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "-p" | "--preset" => {
                    if let Some(p) = args.next().as_deref().and_then(Preset::parse) {
                        opts.preset = p;
                    }
                }
                "--no-color" => opts.color = false,
                "-w" | "--width" => {
                    if let Some(cols) = args.next().as_deref().and_then(parse_cols) {
                        opts.width = Some(cols);
                    }
                }
                "-V" | "--version" => opts.version = true,
                "-h" | "--help" => opts.help = true,
                // `--preset=full` as well as `--preset full`.
                other => {
                    if let Some(name) = other.strip_prefix("--preset=") {
                        if let Some(p) = Preset::parse(name) {
                            opts.preset = p;
                        }
                    } else if let Some(cols) = other.strip_prefix("--width=").and_then(parse_cols) {
                        opts.width = Some(cols);
                    }
                }
            }
        }
        opts
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse(args: &[&str]) -> Options {
        Options::parse(args.iter().map(ToString::to_string))
    }

    #[test]
    fn the_preset_can_be_given_either_way() {
        assert_eq!(parse(&["--preset", "full"]).preset, Preset::Full);
        assert_eq!(parse(&["--preset=lean"]).preset, Preset::Lean);
        assert_eq!(parse(&["-p", "lean"]).preset, Preset::Lean);
    }

    #[test]
    fn an_unusable_preset_falls_back_rather_than_failing() {
        // The command line comes out of settings.json, where anything can be
        // typed; a bad value must still leave a line on the screen.
        assert_eq!(parse(&["--preset", "powerline"]).preset, Preset::Standard);
        assert_eq!(parse(&["--preset"]).preset, Preset::Standard);
        assert_eq!(parse(&[]).preset, Preset::Standard);
    }

    #[test]
    fn unknown_flags_are_ignored_not_rejected() {
        let o = parse(&["--future-flag", "--preset", "full", "extra"]);
        assert_eq!(o.preset, Preset::Full);
        assert!(!o.help && !o.version);
    }

    #[test]
    fn help_and_version_are_recognised() {
        assert!(parse(&["--help"]).help);
        assert!(parse(&["-h"]).help);
        assert!(parse(&["--version"]).version);
        assert!(parse(&["-V"]).version);
    }

    #[test]
    fn no_color_can_be_asked_for_on_the_command_line() {
        assert!(!parse(&["--no-color"]).color);
    }

    #[test]
    fn the_width_can_be_pinned_either_way() {
        assert_eq!(parse(&["--width", "200"]).width, Some(200));
        assert_eq!(parse(&["--width=200"]).width, Some(200));
        assert_eq!(parse(&["-w", "200"]).width, Some(200));
        // Zero is a real answer: nothing fits in it, so `full` keeps its break.
        assert_eq!(parse(&["--width", "0"]).width, Some(0));
    }

    #[test]
    fn an_unusable_width_is_ignored_rather_than_obeyed() {
        // Same reasoning as the preset: this command line lives in the user's
        // settings.json, and a typo there must not cost them the status line.
        assert_eq!(parse(&["--width", "wide"]).width, None);
        assert_eq!(parse(&["--width", "-8"]).width, None);
        assert_eq!(parse(&["--width"]).width, None);
    }

    #[test]
    fn a_pinned_width_beats_the_console_and_keeps_the_margin() {
        assert_eq!(terminal_width(Some(200)), Some(200 - WIDTH_MARGIN));
        // And a pin of zero cannot underflow into a very wide terminal.
        assert_eq!(terminal_width(Some(0)), Some(0));
    }

    #[test]
    fn the_fallback_line_needs_nothing_from_disk() {
        let opts = parse(&["--preset", "standard", "--no-color"]);
        let stdin = r#"{"model":{"display_name":"Sonnet 5"},
                        "workspace":{"current_dir":"/tmp/proj"},
                        "context_window":{"used_percentage":12}}"#;
        assert_eq!(line(&opts, stdin, false), "ctx 12% · Sonnet 5 · proj");
        // And an empty payload leaves nothing to print, rather than a husk.
        assert_eq!(line(&opts, "", false), "");
    }
}
