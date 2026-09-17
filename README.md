# Claude Switch

A Windows tray app for switching between Claude Code accounts. **When a rate limit is hit, it switches for you — no Claude Code restart, and the session in progress keeps going.**

<!-- Screenshot placeholder: main window (account list + activity strip). See docs/screenshots/ -->

## Install

Download from [Releases](../../releases). Either option is the same build:

| Download | Best for |
|---|---|
| `ClaudeSwitch-<version>-win-x64.zip` | **Recommended.** Unzip and run; includes quick-start notes and license. Browsers warn less on zip than on bare exe |
| `ClaudeSwitch-<version>-win-x64.exe` | Just the program |

No .NET install. No Rust install.

**There is no installer** — it is a portable executable: no registry writes, no admin rights, put it anywhere, delete it to uninstall. Launch-at-login is controlled by a checkbox in the app (current-user Startup shortcut; uncheck to remove).

The one exception: the .NET single-file bundle extracts its runtime on first launch to `%TEMP%\.net\ClaudeSwitch\`. That is normal .NET behavior, not an install.

**Windows SmartScreen will warn on first run** — this program is not code-signed (a cert costs hundreds of dollars a year; we have not bought one yet). Click **More info** → **Run anyway**.

Files extracted from the zip also carry the Mark of the Web, so **the zip does not bypass that prompt**.

If you want to double-check the download, Releases include SHA256 checksums:

```powershell
Get-FileHash ClaudeSwitch.exe -Algorithm SHA256
```

Or [build from source](#build-from-source) — the steps are the same ones CI uses.

## What problem it solves

You have several Claude accounts (personal + work, or multiple subscriptions). Mid-session the 5-hour limit hits and Claude Code stops. You export credentials, swap files, restart.

This tool turns that into: **it switches; you keep writing.**

- **Switches take effect on the running session** — on Windows credentials are files; Claude Code re-reads them on change, so the next message uses the new account. No restart, no reopening VS Code tabs.
- **Auto-switch** — when any account’s 5-hour or 7-day usage reaches the threshold, it moves to the healthiest available account. It also fails over when the current account is **broken** (login dead, subscription gone, empty slot) — cases a threshold alone would never catch.
- **Usage at a glance** — per-account 5-hour / 7-day headroom, plan (Pro / Max 20× / Team), subscription start date.
- **Usage overview** — what you actually did in Claude Code: total tokens, activity calendar, project breakdown.

## Features

### Accounts

- Capture the machine’s current login (**Add account**); multi-slot, aliases, drag reorder, disable
- **The active account is resolved from the live login**, not replayed from the last switch this app made — `claude /login` or other tools are recognized correctly
- Plan info is read from each slot’s own credentials + `.claude.json` backup — **no network request**. Renewal dates are not shown: OAuth tokens have no billing scope, so that date is unavailable

### Usage

- Fetched per slot; expired OAuth tokens are refreshed first (**the currently logged-in account’s token is never rotated** — that would kick your live session)
- When usage cannot be loaded, the reason is explicit (`Needs login` / `No credentials` / `No subscription quota` / `API Key` / `Fetch failed`) instead of a blank

### Auto-switch

- **Only switches to accounts with measured usage.** Unmeasured slots are skipped and never ranked as “0% used / 100% free” — otherwise the one account that cannot work would rank first
- Failover wait policy depends on whether waiting can help: wiped login fails over immediately; transient network faults wait for `unhealthyTicks`
- Credentials without an access token **refuse activation**, including manual switch — that is not a switch, it is a logout

### Parallel sessions

**Use different accounts in different terminals at the same time.** Right-click a card → **Open terminal with this account**, pick a directory, and a new terminal runs Claude Code logged into that account — **the default login, other terminals, and the VS Code extension are unaffected**.

Each account gets its own config directory (`<backup root>/sessions/<slot>-<email>/`), pointed at with `CLAUDE_CONFIG_DIR` on launch. Claude Code resolves config and credentials through that variable, so isolation is complete.

- **Directories can be bound to an account** — in the **Directories** window, right-click → **Bind to account**. Opens from **Resume session** or **Directories** then use that account automatically. Subdirectories inherit the nearest bound ancestor
- **Your own config is shared**: `settings.json` / `CLAUDE.md` / `skills/` / `commands/` / `agents/` and user-level MCP servers are re-synced from `~/.claude` on every session start. Edits inside the session profile are overwritten next launch — change them in `~/.claude`
- **Transcript history is not copied** (copying would fork). **Directories**, usage overview, and **Resume session** scan every session profile and **merge** results, so work done under any account is visible and each directory appears once
- **Auto-switch skips accounts that already have a live terminal** — their quota is already being spent, and promoting them to the default login would put the same refresh token in two config dirs
- If the target account **is already the default login**, a bare `claude` is started with no second credentials copy
- Deleting an account removes its session profile and directory bindings; deletion is refused while a terminal is still running

### Directories and sessions

- The toolbar **Resume session** dropdown and the tray menu list recent sessions per directory — **one step back into the last conversation**
- The **Directories** window browses every directory and its full session list
- Resume runs **`claude` directly, not via cmd** — the terminal is whatever your **default terminal app** is set to; when Claude exits the window closes cleanly (no leftover cmd prompt)
- Aggregate token stats run on demand (reads all transcripts, typically a few hundred ms) with visualization
- **Directories themselves are account-agnostic** — Claude Code does not record which account owned a conversation. The **Account** column is the binding you set for that directory (which account opens from there), not something read from the transcript

### Terminal status line

A line at the bottom of every Claude Code terminal, answering what Claude Code's own status line cannot: **which account this terminal is on, and how much of its quota is left.**

```
#2 work · 5h ████░░░░░░ 38% · 7d 12% · ctx 24% · Sonnet 5 high · my-project (main)
5h resets 14:30 · 7d resets Fri 09:00 · spare #3 ops 8% · auto 90% · $1.24 42m
```

- **Three presets, no widget editor** — Lean / Standard / Full, from a checkbox in **Automation**. The settings row previews the real line, rendered by the same code that draws it in the terminal
- **It uses the width you have** — Full is one line on a terminal wide enough to hold it, and only breaks in two when it is not. Pass `--width <cols>` (or `--width 0` to keep the break) if your terminal cannot be asked
- **Model, thinking effort, context, cost** come from Claude Code's own payload; the account, the windows and the spare come from this app
- **Per terminal, not per machine** — a terminal opened for one account names *that* account, not the default login
- **No Node, no runtime** — a ~750 KB native binary, written to `~/.claude-swap-backup/bin/` when you turn the feature on. No network, no `git` subprocess: it reads three local files and exits
- **Your own `settings.json` is respected** — the `statusLine` key is merged in, everything else is kept, and a file that does not parse is left untouched. If another status line (e.g. ccstatusline) is configured, it is never replaced without asking, and turning this off puts it back
- Enabling it once covers every per-account terminal: session profiles re-sync `~/.claude/settings.json` on launch

Details and the data sources are in [docs/statusline.md](docs/statusline.md).

### UI language

English / Simplified Chinese via the language button on the right of the toolbar — **no restart**. Main window, cards, and status bar update immediately; other windows pick it up the next time they open. First launch follows the system language; after you pick one, that choice is remembered.

For a one-off override: `CLAUDE_SWITCH_LANG=en` (takes priority over the remembered choice).

<details>
<summary>Want to add a language?</summary>

Copy `gui-win/ClaudeSwitch.App/Strings/en.json` to your language code (e.g. `ja.json`), translate the values, and add a row in `Loc.Available`. Catalogs are embedded resources — no build-script changes.

`LocTests` requires every catalog to match English **keys exactly** and `{0}` placeholders exactly — a missing string or wrong placeholder fails the test instead of shipping half-English UI.

Practical note: **English is often 1.5–2× wider than Chinese**. This project already widened the toolbar, card meters, subscription columns, and heatmap legend for that. Render the UI when you add a language; do not judge from the JSON alone.
</details>

## Networking

Requests honor `HTTPS_PROXY` / `ALL_PROXY` / `NO_PROXY`, and on Windows also read the system proxy settings — the same path Claude Code uses.

> If your network can only reach Anthropic through a proxy, a direct connection returns `403 "Request not allowed"`. That error **looks like auth failure but is network reachability**. Proxy settings are read at startup; restart after changing them.

## Where data lives

| Location | Contents |
|---|---|
| `~/.claude-swap-backup/credentials/` | Per-slot credentials (encrypted) |
| `~/.claude-swap-backup/configs/` | Per-slot `.claude.json` snapshots |
| `~/.claude-swap-backup/sequence.json` | Slot order and current account |
| `~/.claude-swap-backup/sessions/` | Per-account session profiles (including their own transcript history) |
| `~/.claude-swap-backup/mappings.json` | Directory → account bindings (machine-local) |
| `~/.claude-swap-backup/cache/` | Usage-overview cache (safe to delete) |
| `~/.claude-swap-backup/bin/` | The status-line renderer, written when that feature is enabled |
| `~/.claude-swap-backup/statusline.json` | What the status line reads (no credentials) |
| `~/.claude/settings.json` | Claude Code's own file; only its `statusLine` key is ever touched, and a copy is kept as `settings.json.cswitch-bak` |
| `%LOCALAPPDATA%\ClaudeSwitch\ui-prefs.ini` | UI prefs (theme, language, hide email, etc.) |
| `%TEMP%\.net\ClaudeSwitch\` | Self-extracted single-file runtime |

Layout is compatible with [claude-swap](https://github.com/realiti4/claude-swap) (Python CLI); both can share the same backup tree. Session profile layout matches its `cswap run` layout; only internal marker filenames differ (`.cswitch-*` vs `.cswap-*`) — both can read the same profiles, each managing its own markers.

## Build from source

Requires Rust 1.80+ and the .NET 8 SDK.

```bash
cargo build -p claude-switch-ffi --release
dotnet publish gui-win/ClaudeSwitch.App/ClaudeSwitch.App.csproj \
  -c Release -p:PublishSingleFileBundle=true -o dist
```

Output is a single `dist/ClaudeSwitch.exe`. Order matters — the native engine must be built first, or publish fails hard instead of shipping a binary that dies on launch.

To produce the same artifacts as a Release (exe + zip + checksums):

```powershell
./packaging/pack.ps1 -PublishDir dist -OutDir artifacts
```

For development:

```bash
cargo test --workspace
dotnet test gui-win/ClaudeSwitch.App.Tests/ClaudeSwitch.App.Tests.csproj
dotnet run --project gui-win/ClaudeSwitch.App -c Release -- --fixture %TEMP%\cswitch-demo
```

`--fixture` starts with isolated demo data (six accounts across plan types) and does not touch your real Claude login.

## Project layout

```
crates/core     claude-switch-core   locks, credentials, switch, usage, autoswitch, session mode, Engine
crates/ffi      claude_switch.dll    C ABI (cs_engine_*)
crates/statusline cs-statusline.exe  the status line Claude Code runs on every repaint
gui-win/        Windows tray GUI (WinForms) + FfiSmoke + P/Invoke
gui-win/ClaudeSwitch.App/Strings/    UI catalogs (one JSON per language, embedded)
site/           the product page: one static HTML file + app screenshots
```

- [docs/design-claude-switch.md](docs/design-claude-switch.md) — architecture, FFI, UI design
- Behavior is specified by the upstream Python CLI [claude-swap](https://github.com/realiti4/claude-swap) — credentials, three-lock switch transactions, autoswitch, and poll policy follow it.
  For development you can clone it to `reference/claude-swap/` (that path is not version-controlled).

Version is the root `VERSION` file and must match `Cargo.toml` `[workspace.package].version`.

## CI and release

Every push / PR runs a full Windows gate: Rust (fmt / clippy / test) → GUI tests → single-file publish → package (exe + zip + SHA256) → launch smoke. Artifacts are kept as workflow artifacts for 14 days.

**A release is a version bump that reached master.** Bump `VERSION` and the matching `Cargo.toml` `[workspace.package].version`, commit, merge. Once the gate passes, CI asks whether that version already has a tag; if it does not, it calls the [release](.github/workflows/release.yml) workflow, which rebuilds, smokes, creates `v<VERSION>` and publishes a GitHub Release with `ClaudeSwitch-<version>-win-x64.zip` / `.exe` / `SHA256SUMS.txt`.

The gate is **the absence of the tag**, not the diff of the push — a rerun, a squash, a revert or a force push all converge on one release per version, which a diff-based check does not.

Pushing a `v*` tag by hand still does the same thing, for a release cut off-schedule:

```powershell
git tag v0.2.0    # tag without the leading v must equal VERSION
git push origin v0.2.0
```

CI calls the release workflow instead of pushing a tag itself, because a tag pushed with `GITHUB_TOKEN` does not start another workflow — the release would build nothing.

Dry run (no publish): Actions → **release** → Run workflow, leave `dry_run=true`. That builds and packages the artifacts without creating a Release.

## Platforms

Windows only for now. The core is cross-platform Rust; macOS / Linux native shells are in the design doc but not built yet.

## License

MIT — see [LICENSE](LICENSE). Backup format stays interoperable with claude-swap.
