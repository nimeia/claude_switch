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
gui-win/        Windows tray GUI (WinForms) + FfiSmoke + P/Invoke
gui-win/ClaudeSwitch.App/Strings/    UI catalogs (one JSON per language, embedded)
```

- [docs/design-claude-switch.md](docs/design-claude-switch.md) — architecture, FFI, UI design
- Behavior is specified by the upstream Python CLI [claude-swap](https://github.com/realiti4/claude-swap) — credentials, three-lock switch transactions, autoswitch, and poll policy follow it.
  For development you can clone it to `reference/claude-swap/` (that path is not version-controlled).

Version is the root `VERSION` file and must match `Cargo.toml` `[workspace.package].version`.

## CI and release

Every push / PR runs a full Windows gate: Rust (fmt / clippy / test) → GUI tests → single-file publish → package (exe + zip + SHA256) → launch smoke. Artifacts are kept as workflow artifacts for 14 days.

To cut a release (maintainers):

```powershell
# 1. Bump VERSION and the matching Cargo.toml workspace.package.version
# 2. Commit, then tag and push (tag without the leading v must equal VERSION)
git tag v0.1.0
git push origin v0.1.0
```

Pushing a `v*` tag runs the [release](.github/workflows/release.yml) workflow: build → smoke → create a GitHub Release and upload `ClaudeSwitch-<version>-win-x64.zip` / `.exe` / `SHA256SUMS.txt`.

Dry run (no publish): Actions → **release** → Run workflow, leave `dry_run=true`.

## Platforms

Windows only for now. The core is cross-platform Rust; macOS / Linux native shells are in the design doc but not built yet.

## License

MIT — see [LICENSE](LICENSE). Backup format stays interoperable with claude-swap.
