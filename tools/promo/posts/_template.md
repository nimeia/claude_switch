# <What this post is about> — <date>

Copy this file to tools/promo/posts/<date>-<topic>.md, fill it in, then from
the repository root:
  python tools/promo/promo.py check tools/promo/posts/<date>-<topic>.md
  python tools/promo/promo.py run   tools/promo/posts/<date>-<topic>.md

Say plainly that you built it, and word each post a little differently from
the last one — the same text and link everywhere is what spam filters catch.
Delete any platform section you don't want to post to.

## x
images: img/<file>.png

<At most 280 characters as X counts them: a link counts as 23, Chinese
characters count as 2. `check` tells you the number. `images:` is optional
everywhere, takes paths relative to tools/promo/ in the order they should be
dragged in, and X takes at most four.>

https://nimeia.github.io/claude_switch/

## facebook
targets: profile
images: img/<file>.png, img/<file>.png

<Longer, friendly text. Facebook builds the preview card from the link.
Group keys (see targets.json): claude-code-learning, claude-code-cowork-design,
claude-ai-hub. One day between posting the same link to more groups.>

https://nimeia.github.io/claude_switch/

## reddit
targets: ClaudeCode
title: <Up to 300 characters; say what it is and that you built it>
images: img/<file>.png, img/<file>.png

<Plain text, no ** or ` — Reddit's default editor shows them literally.
One subreddit per day.>
