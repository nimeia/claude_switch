# Auto-continue over ACP

Keeping a Claude Code run alive across interruptions that are not the model's
decision to stop.

Everything below was verified on Windows 11 against
`@agentclientprotocol/claude-agent-acp` **0.65.0** (bundling
`@agentclientprotocol/sdk` 1.3.0 and `@anthropic-ai/claude-agent-sdk` 0.3.220),
ACP protocol version **1**, Claude Code **2.1.223**, Node **22.20**.

## What ACP buys, and what it does not

ACP runs the agent as a **subprocess of its client**, speaking newline-delimited
JSON-RPC 2.0 over stdio. That gives a typed control channel: a turn ends with a
`stopReason`, or fails with a JSON-RPC error carrying `data.errorKind`. No
scraping transcripts, no guessing whether a quiet terminal is thinking or dead.

It also means the hard limit: **ACP cannot attach to a `claude` TUI the user
started themselves.** There is no such notion in the protocol. Watching
user-launched terminals remains the job of the transcript scanners and
`~/.claude/sessions/<pid>.json` in `claude-switch-core`. This crate only steers
sessions it owns.

## Layout

| Path | Role |
|---|---|
| `crates/core/src/autocontinue.rs` | **The retry policy.** Pure, transport-free, the only copy |
| `crates/core/src/agentruns.rs` | Run journal: what survives a restart, and how a dead run is spotted |
| `crates/acp/src/adapter.rs` | Locate + launch the adapter with the right environment |
| `crates/acp/src/client.rs` | Minimal ACP client: framing, correlation, agent→client callbacks |
| `crates/acp/src/autocontinue.rs` | `AcpError` → policy inputs, and the headless `Runner` |
| `crates/acp/src/bin/acp_run.rs` | `acp-run` CLI / end-to-end harness |
| `gui-win/…/AcpSession.cs` | ACP client for the GUI: async, streaming, interactive |
| `gui-win/…/AcpRunner.cs` | Run loop; asks the engine for every retry decision |
| `gui-win/…/AcpPermissionDialog.cs` | Turns `session/request_permission` into a modal |
| `gui-win/…/BackgroundRuns.cs` | Runs that outlive their window; journals their progress |
| `gui-win/…/AgentRunStore.cs` | Read/write model over the engine's journal |
| `gui-win/…/AgentWindow.cs` | A *view* onto a run — it does not own one |
| `gui-win/…/StalledWatch.cs` | Finds stalled user terminals; nudges first, takes over when that fails |
| `gui-win/…/TerminalNudge.cs` | Types a line into another console's input buffer |
| `tools/acp/probe.mjs` | Raw-protocol probe, for pinning down wire shapes |
| `tools/acp/kill-adapter-test.ps1` | Fault injection: kill the adapter mid-turn |

## Why the policy is in Rust and the transport is not

The GUI speaks ACP directly rather than through the Rust FFI, because the FFI is
a synchronous request/response surface and ACP is bidirectional and streaming.
While a prompt is in flight the agent sends *us* requests — most importantly
`session/request_permission`, which must become a modal dialog and cannot
resolve until a human answers. Pumping that through a blocking call would
deadlock the UI thread.

The *decision table*, though, stays in `claude-switch-core` and is reached over
FFI (`acp_decide`). A second copy in C# would drift, and the rows that would
drift first are exactly the subtle ones — a quota hit arriving tagged
`server_error`, an auth failure that must never be retried. The GUI also asks the
engine for the proxy (`proxy_resolve`) rather than re-deriving the
env-then-registry precedence itself.

The official `agent-client-protocol` Rust crate is async (tokio) and moves fast.
`claude-switch-core` deliberately has no async runtime, and the wire format is
small, so the client is hand-rolled on `serde_json` + std threads. `client.rs` is
the only module that would change if we swap in the official SDK.

## Two environment facts that decide whether this works at all

**1. The proxy must be injected explicitly.** Claude Code's own CLI picks up the
Windows Internet Settings proxy; the Agent SDK under the adapter does **not**. On
a proxied network the first API call fails with:

```
403 Request not allowed        errorKind: authentication_failed
```

which reads exactly like an auth failure and is nothing of the sort — the same
trap `crates/core/src/proxy.rs` documents. `AdapterConfig` resolves the proxy via
`proxy::resolve_for()` and sets `HTTPS_PROXY`/`HTTP_PROXY` on the child. Verified:
with no proxy env set, `acp-run --probe` reports
`proxy: http://192.168.1.54:7897` read straight from the registry, and the run
succeeds.

**2. `CLAUDE_CONFIG_DIR` is honoured.** Account isolation survives the ACP hop.
Verified structurally: a probe run with `--account-dir <profile>` created
`<profile>/projects/<encoded-cwd>/` inside the session profile rather than in
`~/.claude`, and authenticated with that profile's credentials (it failed with
*that account's* expired OAuth, not the default login's working one).

Also confirmed: `authMethods: []` — the OAuth subscription is sufficient, no API
key required. And `loadSession: true`, so conversations can be resumed.

## Classification

"Send `continue` when Claude stops" is wrong, and dangerously so — most stops are
the model handing control back to a human.

| Outcome | Signal | Action |
|---|---|---|
| `end_turn` | `stopReason` | **stop** — awaiting the user |
| `max_tokens`, `max_turn_requests` | `stopReason` | continue immediately (work was cut off) |
| `refusal`, `cancelled` | `stopReason` | stop — deliberate halt |
| 5xx / ECONNRESET / connection closed | `errorKind: server_error` | continue, exponential backoff |
| usage limit reached | message text | wait for the window, then continue |
| 403 / expired OAuth | `errorKind: authentication_failed` | stop — needs a human |
| adapter died / turn timeout | transport | respawn, `session/load`, continue |

Rate-limit detection reads the message text **before** `errorKind`, because the
adapter tags quota exhaustion as `server_error`; without that, a quota hit would
be hammered on a 10-second backoff.

Transient retries and quota waits use **separate budgets**, so a long quota wait
does not consume the allowance for network blips.

## Hitting the quota wall

A quota wall is not a fault to retry through — only time or a different account
moves it. `onRateLimit` picks which:

| Setting | Behaviour |
|---|---|
| `wait` (default) | Sleep until the window resets, then continue on the same account |
| `switch` | Hand the run to the account with the most headroom and continue now |
| `stop` | Stop and report |

Two details that matter:

- **The wait uses the real reset time**, read from the engine's usage cache
  (`acp_decide` takes an `accountNumber` and answers from `fiveHour.resetsAt`).
  A blind backoff either wakes early into the same wall or sleeps past the moment
  quota returns. `maxWaitHours` caps it.
- **A network error with no quota left is treated as a quota wall.** Retrying
  needs quota to spend; with the window empty a ten-second backoff would only
  reach the same wall.

`switch` degrades to `wait` when no account has headroom — "switch" with nowhere
to switch to must not become "give up".

### Switching accounts moves the transcript

A session is only visible to the profile holding its transcript: loading a
foreign session id answers `Resource not found` (verified). So a handover
**moves** the `.jsonl` into the target profile — `agent_run_handoff` does it,
after `session_prepare` has made sure that profile exists.

Moved, not copied: a copy would leave the same conversation in two profiles,
diverging from the moment the run continues. `projects::scan_envs` already reads
every profile as a root, so a moved file stays visible in the history without
being double-counted.

The encoded project folder name is **lossy** — both `_` and `-` become `-`, so
`D:\dev\lan_remote_app_control` and a hyphenated sibling collide. It therefore
cannot be recomputed from a path; the handover finds the file by session id and
reuses the folder it already sits in.

## Safety defaults

- Permission requests are **denied** by the default `Handler`. Unattended
  approval is an explicit opt-in (`--allow-tools`).
- Quota waiting is **off** by default (`max_rate_limit_waits: 0`) — a quota wait
  can idle for hours.
- Auth failures never retry, no matter the attempt budget.
- Every retry sends "continue from where you left off" rather than repeating the
  original instruction, so completed work is not redone.
- Retries resume the **same** session id via `session/load`; they never fork.

## Verified end to end

```
acp-run --probe                     → loadSession: true, 6 permission modes, PROBE OK
acp-run --prompt "create a file…"   → tool call ran, file written, Completed(EndTurn)
kill-adapter-test.ps1               → turn 0: Interrupted(AdapterCrash)
                                      turn 1: Completed(EndTurn), all 5 files produced
acp-run --account-dir <expired>     → turn 0: Interrupted(Auth), stop: NeedsAuth,
                                      0 continuations despite --max-attempts 5
```

The crash test was checked against the transcript: a single session file
containing both the original prompt and the continuation message. No fork.

## Setup

```sh
cd tools/acp && npm install @agentclientprotocol/claude-agent-acp
cargo build -p claude-switch-acp
./target/debug/acp-run --probe
```

`node_modules` is gitignored. The adapter is found via
`CLAUDE_SWITCH_ACP_ADAPTER`, then `tools/acp/node_modules` walking up from the
cwd, then the usual global npm roots.

## The GUI

An account card's context menu gains **Supervised run…**, and the toolbar gains
a **Runs** dropdown listing what is still going and what is worth resuming. It shares the directory
picker with the terminal path — the choice is the same one, only the thing
launched differs — and takes the account profile from `session_prepare`, so the
run authenticates as the named account exactly as a session-mode terminal would.

The window streams assistant text and tool calls, and its status strip names what
interrupted a run and what is being done about it. That strip is the point: a
silent retry is indistinguishable from a hang, which is the failure mode this
feature exists to remove.

**Auto-continue** is a checkbox. Unticking it sets `maxAttempts: 0` and
`continueOnTruncation: false`, so the run does exactly one turn and reports
whatever happened.

Permission requests open a modal built from the agent's own option list — the
`optionId` values are echoed back, never invented. Closing the dialog with Esc
denies, so the escape hatch never grants anything.

A screenshot of the window is captured by the layout probe
(`CLAUDE_SWITCH_PROBE_AGENT=1` with `CLAUDE_SWITCH_LAYOUT_DIR`), which is the
only visual test it has.

### GUI verification

`gui-win/ClaudeSwitch.App.Tests/AcpEndToEndTests.cs` drives the GUI's own ACP
client against a real agent. They are opt-in — they spawn the adapter, reach
Anthropic, and spend quota:

```sh
CLAUDE_SWITCH_ACP_E2E=1 dotnet test gui-win/ClaudeSwitch.App.Tests   --filter "FullyQualifiedName~AcpEndToEnd"
```

All three pass:

- a plain run completes and writes its file;
- **killing the agent mid-turn** is recovered — the run reconnects, resumes the
  *same* session id, and finishes the work (the test kills the exact pid the
  runner reports, so it cannot pass by accident);
- denying permission does not spin on retries.

## Stalled terminals: wake first, take over second

A Claude Code the **user** launched cannot be attached to (see the top of this
doc), but it can still be *prodded*. Before any takeover, `StalledWatch` tries
to wake the terminal the session is already in — the conversation stays in the
window the user opened, nothing goes stale, and no second process writes the
same transcript.

**How the pid is trusted.** The session→pid mapping is Claude Code's own
`~/.claude/sessions/<pid>.json`. A live pid proves nothing by itself — Windows
recycles pids quickly and the file survives a crash — so the file's own timing
evidence is checked against the real process creation time: `procStart` (the
creation FILETIME Claude Code records) must match within 2s, else `startedAt`
must not predate the process by more than a minute. A mismatch means the pid
now belongs to somebody else's console, and the mapping is dropped — without
this the nudge would type a sentence into an innocent terminal.

**How the line is delivered.** `AttachConsole(pid)` + `CONIN$` +
`WriteConsoleInputW` — addressed to a process, not aimed at a window, so no
focus is stolen and the wrong tab cannot receive it. Letters carry a real
virtual key (`VkKeyScanW`, Shift only — Ctrl/Alt would fire shortcuts).
Non-ASCII is refused (the console's input code page is the user's and would
mangle it). The write goes out in 64-event chunks with a bounded retry, so a
briefly busy reader drains its queue instead of failing the whole line.
`AttachConsole` is process-wide and is locked; if the recorded pid has no
console, children then shell parents (`cmd` / `powershell` / `node` / …)
are tried, never a GUI host (`WindowsTerminal`, `warp`, `explorer`).

A process whose stdin is a **pipe** holds the read end — there is nothing to
write. Those hosts fall through to a takeover. Warp's **GUI shell prompt**
is a related miss: stdin is a console nobody is reading (Warp types into the
PTY from the GUI). A Claude TUI in that window is passthrough and follows
the OpenConsole path below.

| Host | Topology | Nudge |
|---|---|---|
| conhost | `CREATE_NEW_CONSOLE` → reader | works (cooked + raw) |
| `cmd.exe` | `cmd /d /s /c` → reader | works (the app's own launch wraps Claude in cmd) |
| Windows Terminal | `wt -w new --` → reader | works |
| Warp OpenConsole | Warp's own `OpenConsole.exe --headless` | works (Claude TUI / passthrough) |
| Warp GUI prompt | `warp.exe` → `pwsh` (no OpenConsole) | delivered, not consumed → takeover |

**How success is judged.** Delivered keystrokes are not success — the console
buffer accepts anything. Claude Code appends a submitted line to its
transcript the moment it is accepted, so the transcript is the witness
(`session_tail`, polled every 2s for up to 30s), and its **mtime** is the
arbiter:

- tail moved to a turn in progress or a reply → **woken**;
- tail moved and reads `failed` again → the line was taken and the wall is
  still up: straight to a takeover, no waiting out the window;
- tail unmoved after 10s → nobody read the line (a transient dialog ate it):
  **sent again, once**, then the takeover if that too achieves nothing.

**If it stalls again.** A successful nudge only proves the terminal listened,
not that the wall lifted. The session is marked, and if a later sweep finds it
stalled again the wall is known to be real: straight to a takeover (whose
`onRateLimit` policy exists for exactly this) instead of burning another turn
in the terminal.

### Verified

`gui-win/ClaudeSwitch.App.Tests/TerminalNudgeLiveTests.cs` — opt-in, spawns
real short-lived consoles:

```sh
CLAUDE_SWITCH_NUDGE_E2E=1 dotnet test gui-win/ClaudeSwitch.App.Tests --filter "FullyQualifiedName~TerminalNudgeLive"
# E2E also covers wt and Warp OpenConsole; override individually with:
#   CLAUDE_SWITCH_NUDGE_WT=1 / CLAUDE_SWITCH_NUDGE_WARP=1
```

All seven pass on Windows 11 (Claude Code 2.1.225, Warp 0.2026.07.29, WT 1.24):

- delivery to a cooked-mode reader and to a **raw-mode node reader** — the
  exact input path Claude Code uses — with the full sentence arriving intact;
- delivery under **cmd**, **Windows Terminal**, and **Warp's OpenConsole**;
- the whole `TryResume` loop against a real console and a real engine,
  including a session hosted in cmd: nudge sent, transcript gains the line,
  `NudgeSucceeded` fires, no takeover is started;
- a console that accepts input and never reads it is taken over, with the
  sends bounded at two.

`StalledNudgeTests.cs` pins the judgement calls without a console: the resend
is exactly one, an unreadable tail is neither success nor failure, a retry
into the same wall skips the resend, and a session stalled again after a
successful nudge goes straight to a takeover.

## Unattended runs

### What can and cannot survive

An ACP agent is a **child process of the app**. Exiting kills it; there is no way
around that short of a detached supervisor, and an agent editing files with no UI
attached is not something to build by accident. So "unattended" means two
specific things:

- A run keeps going when its **window** closes, for as long as the app lives.
  `BackgroundRuns` owns it; `AgentWindow` is a view that attaches and detaches.
- Every run is journaled with its `sessionId`, so a run cut short by the app
  exiting can be **resumed** on the next launch — same conversation, via
  `session/load`, with the work already done still in it.

That second point is recovery, not continuity, and the distinction is worth
keeping honest: after a restart the agent has to be *told* to continue.

### Spotting a run that died with its app

A record left in `running` proves nothing — the process that wrote it may be
gone. Each carries the `ownerPid` that last touched it, and `agent_run_list`
demotes running records whose owner is no longer alive to `interrupted`, on read.
Reaping on read rather than on write is the point: the process that would have
marked the run interrupted is precisely the one that died. It is the same trick
Claude Code uses for `~/.claude/sessions/<pid>.json`.

A clean shutdown does mark its runs first (`BackgroundRuns.ShutdownAll`); the
reaper is what covers a crash or a power cut.

### The journal

`<backup root>/agent-runs.json`, owned by the engine
(`agent_run_list` / `agent_run_upsert` / `agent_run_remove`). Live and
interrupted records are never pruned — those are the ones a user might still act
on; finished ones are capped at 50. A corrupt journal reads as empty rather than
blocking startup: it holds recovery hints, and refusing to launch over them is
the worse failure.

A run is journaled before it has an ACP session id, under a `pending-…`
placeholder, and re-keyed to the real id at the first sign of life. Without that
re-key the record could never be matched to its conversation, and the placeholder
would linger beside the real entry — a bug the end-to-end test caught.

### In the app

A **Runs** toolbar dropdown, labelled `Runs (n)` while agents are working — the
only place a background run is visible once its window is closed. It lists live
runs first, then resumable ones below a separator. A dropdown rather than a
window: background runs need somewhere to be reachable, but a handful of rows
does not earn a form of its own. On launch, a tray balloon points out anything
left unfinished; a modal demanding a decision about yesterday's work is the
wrong way to open an app.

**Shutdown order matters.** `FormClosing` marks live runs resumable, stops them,
and only then disposes the engine. The tray's Exit therefore does nothing but
call `Application.Exit()` — an earlier version disposed the engine first, and the
last journal write then landed on a disposed engine and threw on the way out.
`AgentRunStore` also swallows a disposed engine, so journalling can never take
the exit path down again.

### Verified

Two more opt-in end-to-end tests, both passing:

- **A run orphaned by an app crash resumes with its context.** Run one writes a
  file and is told a word; the record is then left `running` under a dead pid.
  The next "launch" finds it demoted to `interrupted`, resumes it, and the agent
  writes the remembered word to a second file **without reading any files** — it
  could only know it from the conversation it resumed.
- **A finished run is journaled as completed**, under its real session id rather
  than the placeholder, and can be forgotten.

The runs window was also captured through the layout probe
(`CLAUDE_SWITCH_PROBE_RUNS=1`) against a seeded journal, which showed the reaper
working through the real UI: a record written as `running` with a dead owner
renders as **Interrupted**.

## Not done here

Scheduled starts — kicking off a supervised run at a time, or on a trigger,
without someone pressing Run. The journal now holds everything such a scheduler
would need (prompt, account, directory, policy, session), so it is a smaller
piece of work than it was, but it is still a separate one.
