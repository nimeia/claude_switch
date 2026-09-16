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
//! preview calls too, so the preview cannot drift from the real line.

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
    cs-statusline [--preset lean|standard|full] [--no-color]

Claude Code runs this itself; it is configured from the Claude Switch app
(Automation → status line), which writes the command into ~/.claude/settings.json.
The payload arrives on stdin as JSON.

OPTIONS:
    -p, --preset <name>  How much to show (default: standard)
        --no-color       Plain text; NO_COLOR in the environment does the same
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

#[derive(Debug, PartialEq, Eq)]
struct Options {
    preset: Preset,
    color: bool,
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
                "-V" | "--version" => opts.version = true,
                "-h" | "--help" => opts.help = true,
                // `--preset=full` as well as `--preset full`.
                other => {
                    if let Some(name) = other.strip_prefix("--preset=") {
                        if let Some(p) = Preset::parse(name) {
                            opts.preset = p;
                        }
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
