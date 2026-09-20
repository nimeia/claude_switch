# Parallel sessions — 2026-09-19

The second announcement, and the first one that is not the launch. The launch
led with auto-switch; this one is about the feature that turned out to be the
daily one: **several Claude Code terminals at once, each on a different
account.** Reddit has not seen this project at all yet, so r/ClaudeCode gets
the long version.

Images are built by `tools/promo/img/build.py`. 01 and 02 are diagrams drawn in
the site's colours; 03 is the real Directories window, cropped from the app
screenshot the site already ships.

**Spread Facebook over days.** The four targets below are not one sitting — the
launch link went to all of them on 2026-09-18. Post the profile first, then one
group a day: `run … --only facebook` skips whatever already went out, so the
same command is the right one to repeat.

## x
images: img/01-three-terminals.png, img/02-config-dirs.png

I built a Windows tray app for Claude Code. The part I use most isn't the headline: it opens one terminal per account. Three terminals, three logins, one machine. Each gets its own CLAUDE_CONFIG_DIR, so your default login never moves.

Free, MIT:
https://nimeia.github.io/claude_switch/

## facebook
targets: profile, claude-code-learning, claude-code-cowork-design, claude-ai-hub
images: img/01-three-terminals.png, img/02-config-dirs.png, img/03-directories.png

Last time I posted about Claude Switch — the little Windows tray app I built for Claude Code — I led with the auto-switch when the 5-hour limit hits. The feature I actually reach for every day is a different one, so here it is properly: parallel sessions.

You can have several Claude Code terminals open at the same time, each logged into a different account.

Right-click an account card, choose "Open terminal with this account", pick a directory, and a new terminal comes up running Claude Code as that account. Your default login, your other terminals and the VS Code extension carry on untouched.

The reason it works is one fact about Claude Code: it resolves both its config and its credentials from CLAUDE_CONFIG_DIR. So each account gets its own profile directory, and the terminal is launched pointing at it. Nothing is swapped, nothing is restored afterwards, and there is no window where two terminals disagree about who you are.

Three things I spent most of the time on:

Your own setup follows you. settings.json, CLAUDE.md, skills, commands, agents and your user-level MCP servers are re-synced from ~/.claude every time a session starts, so a per-account terminal behaves exactly like your normal one.

Your history does not fork. Transcripts are deliberately never copied. Every profile is read as an extra root instead, so the Directories window, the usage overview and Resume session merge across all of them — the work you did under any account is visible, and each directory appears once.

Auto-switch knows about the terminals. An account that already has a live terminal is skipped when it picks where to move next: its quota is already being spent there.

Free and open source (MIT). Windows only for now, and not affiliated with Anthropic.

https://nimeia.github.io/claude_switch/

If you juggle more than one Claude account, I'd like to know whether this matches how you actually work — or where it gets in the way.

## reddit
targets: ClaudeCode
title: I made my Claude Code tray app open one terminal per account, so several sessions run at once on different logins
images: img/01-three-terminals.png, img/02-config-dirs.png, img/03-directories.png

I shipped Claude Switch a little while ago: a small Windows tray app that switches Claude Code accounts when the 5-hour limit hits. That is the headline, but the feature I actually use most turned out to be a different one, so that is what this post is about.

You can run several Claude Code terminals at the same time, each logged into a different account.

How it works

Claude Code resolves both its config and its credentials from CLAUDE_CONFIG_DIR. So each account gets its own profile directory under the backup tree, and the terminal is launched pointing at it. That terminal is logged in as that account, and ~/.claude — your default login, your other terminals, the VS Code extension — is not touched. Nothing is swapped and nothing has to be restored afterwards, so there is no window where two terminals disagree about who you are.

Right-click an account card, choose Open terminal with this account, pick a directory. That is the whole flow. You can also bind a directory to an account, and then opening it from Resume session or the Directories window uses that account every time; subdirectories inherit the nearest bound parent.

The parts that took the longest to get right

Your own setup follows you. settings.json, CLAUDE.md, skills, commands, agents and your user-level MCP servers are re-synced from ~/.claude on every session start, so a per-account terminal behaves like your normal one. Edits made inside a session profile are overwritten on the next launch — change them in ~/.claude instead.

History is not copied. A copy would fork it. Every profile is read as an extra root instead, so the Directories window, the usage overview and Resume session merge across all of them: work done under any account is visible, and each directory shows up once. Claude Code does not record which account a conversation belonged to, so the Account column is the binding you set, not something read back from the transcript.

Auto-switch knows about the terminals. An account that already has a live terminal is skipped when picking where to switch next — its quota is already being spent, and promoting it to the default login would put the same refresh token in two config dirs.

The status line says which account you are on. A line at the bottom of each terminal: the slot and alias, how much of the 5-hour and 7-day window is left, context, model, cost. Claude Code's own status line cannot answer the account question, because it does not know which slot is behind CLAUDE_CONFIG_DIR. It is a roughly 750 KB native binary that reads three local files and exits — no Node, no network, no git subprocess.

Worth knowing before you try it

Windows only for now. A single portable exe, no installer. It is not code-signed, so SmartScreen warns on first run; the source is on GitHub and every release ships SHA256 checksums if you would rather verify it or build it yourself. It works with logins you already have — sign in with claude as usual, then click Add account. Apart from the usage checks it sends to Anthropic, nothing leaves your machine. Not affiliated with Anthropic.

Site: https://nimeia.github.io/claude_switch/
Code (MIT): https://github.com/nimeia/claude_switch

It is still just me maintaining it, so if you run more than one Claude account I would really like to hear whether this matches how you work, or where it falls over.
