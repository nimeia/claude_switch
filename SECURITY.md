# Security policy

Claude Switch reads and writes Claude Code credentials: the live login in `~/.claude`, and a copy per account under `~/.claude-swap-backup`. A bug that exposes, leaks or corrupts them is a security problem.

## Reporting a vulnerability

Please do not open a public issue. Use **[Report a vulnerability](https://github.com/nimeia/claude_switch/security/advisories/new)** (GitHub's private reporting), with:

- the Claude Switch version and Windows version
- what someone could do with it, and what they need first (another local account, a crafted file, network position…)
- steps to reproduce

Fixes ship in the next release.

## Scope

In scope: this repository — the app, its native engine, the status-line binary, and the files they write (see [Where data lives](README.md#where-data-lives)).

Out of scope: Claude Code itself and Anthropic's services. Report those to Anthropic.
