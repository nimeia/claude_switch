# Announcing a release

`promo.py` walks you through posting one announcement to X, Facebook and Reddit. It prepares every step and leaves the final **Post** click to you:

| Platform | What the script does | What you do |
|---|---|---|
| X | Opens X's share-intent link with the text already in the composer | Check it, click Post |
| Facebook (profile, groups) | Puts the text on the clipboard, opens the page | Write something → Ctrl+V → Post |
| Reddit | Opens the subreddit's submit page with the title and body filled in, and puts the body on the clipboard in case Reddit drops it (`t` / `b` re-copy title / body) | Choose Text, pick the flair the sidebar asks for, Post |

The last click stays manual on purpose. All three sites forbid scripted posting through their websites, Facebook has no posting API for personal accounts, and the same link pushed to several places by a script is what their spam systems ban accounts for.

## Use

From the repository root:

```powershell
copy tools\promo\posts\_template.md tools\promo\posts\2026-10-01-v0.4.md   # then edit it
python tools/promo/promo.py check tools/promo/posts/2026-10-01-v0.4.md
python tools/promo/promo.py run   tools/promo/posts/2026-10-01-v0.4.md
python tools/promo/promo.py run   tools/promo/posts/2026-10-01-v0.4.md --only x,reddit
python tools/promo/promo.py history
python tools/promo/promo.py log   tools/promo/posts/2026-09-18-launch.md reddit ClaudeCode --url <post URL>
```

After each step `run` asks whether you posted (`y`, `n`, or paste the post's URL) and records it in `history.jsonl`. `log` records something you posted outside the walk-through.

## Files

- `posts/*.md`: one announcement per file, with `## x`, `## facebook` and `## reddit` sections. Right under a heading, `targets:` lists where it goes (keys from `targets.json`) and Reddit needs a `title:` line. See `posts/_template.md` and the first announcement in `posts/2026-09-18-launch.md`
- `targets.json`: the Facebook groups and subreddits with what their rules say. A target with `blocked` is refused unless you insist, for example the 207K "Claude Code" group, which needs the admin's approval first
- `history.jsonl`: what went where and when. It stays on this machine (git-ignored)

## Guard rails

- **`check`** fails on an X post over 280 as X counts it (a link is 23, Chinese characters are 2), a Reddit title over 300, a missing text, title or target. It warns when a text doesn't say you made it, when it has no link, and when a Reddit body uses `**` or backticks (shown literally in Reddit's default editor)
- **`run`** skips a place this post already went to (`--again` overrides), and asks before posting a link that went to the same platform in the last 24 hours
- Add a group or subreddit to `targets.json` only after reading its rules, and put what they say in `notes`
