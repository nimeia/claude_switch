#!/usr/bin/env python3
"""Walk through posting one announcement to X, Facebook and Reddit.

    python tools/promo/promo.py check   posts/<post>.md
    python tools/promo/promo.py run     posts/<post>.md [--only x,facebook,reddit] [--again]
    python tools/promo/promo.py log     posts/<post>.md <platform> <target> [--url URL]
    python tools/promo/promo.py history [--days N]

It prepares every step and leaves the final click to you: X opens with the
text already in the composer (X's own share-intent link), Facebook and Reddit
open on the right page with the text on the clipboard, and a section's images
open in Explorer so you can drag them in. All three forbid
scripted posting through their websites, and the same link pushed to several
places by a script is what their spam systems look for — so the script keeps
a history and warns before repeating a link too soon.

Standard library only. Clipboard support is Windows-only; elsewhere the text
is printed for copying.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import re
import subprocess
import sys
import time
import webbrowser
from urllib.parse import quote

HERE = pathlib.Path(__file__).resolve().parent
TARGETS = HERE / "targets.json"
HISTORY = HERE / "history.jsonl"
PLATFORMS = ("x", "facebook", "reddit")
URL = re.compile(r"https?://\S+")
# Says who made it. Posting your own tool as a neutral "look what I found" is
# astroturfing, and Reddit and Facebook groups remove it.
MAKER = re.compile(r"\b(I|we) (built|made|wrote|created|shipped)\b|\bside project\b|\bmy (own )?(app|tool|project)\b", re.I)
X_LIMIT = 280
X_IMAGE_LIMIT = 4
REDDIT_TITLE_LIMIT = 300
SAME_LINK_HOURS = 24


# --- post file -----------------------------------------------------------------------


def load_post(path: pathlib.Path) -> dict:
    """A post file is Markdown: `## x`, `## facebook`, `## reddit` sections.

    Right under a heading, `targets:`, `images:` and (Reddit) `title:` lines
    configure the section; the text is everything after them. Image paths are
    relative to tools/promo/.
    """
    sections: dict[str, dict] = {}
    current = None
    for line in path.read_text(encoding="utf-8").splitlines():
        heading = re.match(r"^##\s+(\w+)\s*$", line)
        if heading:
            current = heading.group(1).lower()
            if current not in PLATFORMS:
                sys.exit(f"{path.name}: unknown section '## {current}' (use x, facebook, reddit)")
            sections[current] = {"lines": [], "meta": {}, "in_meta": True}
            continue
        if current is None:
            continue
        sec = sections[current]
        meta = re.match(r"^(targets|title|images):\s*(.*)$", line)
        if sec["in_meta"] and meta:
            sec["meta"][meta.group(1)] = meta.group(2).strip()
            continue
        if sec["in_meta"] and not line.strip():
            continue
        sec["in_meta"] = False
        sec["lines"].append(line)

    post = {"name": path.stem, "sections": {}}
    for platform, sec in sections.items():
        targets = [t.strip() for t in sec["meta"].get("targets", "").split(",") if t.strip()]
        images = [HERE / i.strip() for i in sec["meta"].get("images", "").split(",") if i.strip()]
        post["sections"][platform] = {
            "text": "\n".join(sec["lines"]).strip(),
            "title": sec["meta"].get("title", ""),
            "targets": targets or (["profile"] if platform == "x" else []),
            "images": images,
        }
    return post


def load_targets() -> dict:
    return json.loads(TARGETS.read_text(encoding="utf-8"))


# --- checks --------------------------------------------------------------------------


def x_length(text: str) -> int:
    """Length the way X counts it: every link is 23, most CJK and emoji are 2."""
    n = 0
    for part in URL.split(text):
        for ch in part:
            cp = ord(ch)
            light = cp <= 4351 or 8192 <= cp <= 8205 or 8208 <= cp <= 8223 or 8242 <= cp <= 8247
            n += 1 if light else 2
    return n + 23 * len(URL.findall(text))


def check(post: dict, targets: dict) -> tuple[list[str], list[str]]:
    errors: list[str] = []
    warnings: list[str] = []
    if not post["sections"]:
        errors.append("no ## x / ## facebook / ## reddit sections")
    for platform, sec in post["sections"].items():
        label = platform
        if not sec["text"]:
            errors.append(f"{label}: the text is empty")
            continue
        if not URL.search(sec["text"]):
            warnings.append(f"{label}: no link in the text")
        if not MAKER.search(sec["text"]):
            warnings.append(f"{label}: nothing says you made it (e.g. 'I built…', 'side project')")
        for t in sec["targets"]:
            if t not in targets.get(platform, {}):
                errors.append(f"{label}: target '{t}' is not in targets.json")
            elif targets[platform][t].get("blocked"):
                warnings.append(f"{label}/{t}: {targets[platform][t]['blocked']}")
        for image in sec["images"]:
            if not image.exists():
                errors.append(f"{label}: image '{image.name}' is not in tools/promo/{image.parent.name}/")
        if platform == "x":
            n = x_length(sec["text"])
            if n > X_LIMIT:
                errors.append(f"x: {n} characters as X counts them; the limit is {X_LIMIT}")
            if len(sec["images"]) > X_IMAGE_LIMIT:
                errors.append(f"x: {len(sec['images'])} images; X takes {X_IMAGE_LIMIT} per post")
        if platform == "reddit":
            if not sec["title"]:
                errors.append("reddit: needs a 'title:' line under the heading")
            elif len(sec["title"]) > REDDIT_TITLE_LIMIT:
                errors.append(f"reddit: title is {len(sec['title'])} characters; the limit is {REDDIT_TITLE_LIMIT}")
            if re.search(r"\*\*|`", sec["text"]):
                warnings.append("reddit: ** or ` show up literally in Reddit's default editor; use plain text")
            if len(sec["targets"]) > 1:
                warnings.append("reddit: the same text in several subreddits on one day looks like spam — post one, then the next a day later with a new title")
        if platform in ("facebook", "reddit") and not sec["targets"]:
            errors.append(f"{label}: needs a 'targets:' line under the heading")
    return errors, warnings


# --- history -------------------------------------------------------------------------


def read_history() -> list[dict]:
    if not HISTORY.exists():
        return []
    return [json.loads(l) for l in HISTORY.read_text(encoding="utf-8").splitlines() if l.strip()]


def append_history(entry: dict) -> None:
    with HISTORY.open("a", encoding="utf-8") as f:
        f.write(json.dumps(entry, ensure_ascii=False) + "\n")


def now() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc)


def parse_time(s: str) -> dt.datetime:
    return dt.datetime.fromisoformat(s.replace("Z", "+00:00"))


def spacing_warning(platform: str, link: str | None, history: list[dict]) -> str | None:
    if not link:
        return None
    cutoff = now() - dt.timedelta(hours=SAME_LINK_HOURS)
    recent = [
        h for h in history
        if h["platform"] == platform and h.get("status") == "posted"
        and h.get("link") == link and parse_time(h["time"]) > cutoff
    ]
    if not recent:
        return None
    where = ", ".join(sorted({h["target"] for h in recent}))
    return (f"this link already went to {platform} {len(recent)}x in the last "
            f"{SAME_LINK_HOURS}h ({where}) — repeating it now is what spam filters flag")


# --- clipboard -----------------------------------------------------------------------


def copy_to_clipboard(text: str) -> bool:
    if sys.platform != "win32":
        return False
    import ctypes
    from ctypes import wintypes

    CF_UNICODETEXT, GMEM_MOVEABLE = 13, 0x0002
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    user32.OpenClipboard.argtypes = [wintypes.HWND]
    user32.OpenClipboard.restype = wintypes.BOOL
    user32.EmptyClipboard.restype = wintypes.BOOL
    user32.SetClipboardData.argtypes = [wintypes.UINT, wintypes.HANDLE]
    user32.SetClipboardData.restype = wintypes.HANDLE
    user32.CloseClipboard.restype = wintypes.BOOL
    kernel32.GlobalAlloc.argtypes = [wintypes.UINT, ctypes.c_size_t]
    kernel32.GlobalAlloc.restype = wintypes.HGLOBAL
    kernel32.GlobalLock.argtypes = [wintypes.HGLOBAL]
    kernel32.GlobalLock.restype = wintypes.LPVOID
    kernel32.GlobalUnlock.argtypes = [wintypes.HGLOBAL]

    data = (text.replace("\r\n", "\n").replace("\n", "\r\n") + "\0").encode("utf-16-le")
    for _ in range(20):
        if user32.OpenClipboard(None):
            break
        time.sleep(0.05)
    else:
        return False
    try:
        user32.EmptyClipboard()
        handle = kernel32.GlobalAlloc(GMEM_MOVEABLE, len(data))
        if not handle:
            return False
        ptr = kernel32.GlobalLock(handle)
        ctypes.memmove(ptr, data, len(data))
        kernel32.GlobalUnlock(handle)
        return bool(user32.SetClipboardData(CF_UNICODETEXT, handle))
    finally:
        user32.CloseClipboard()


def put_on_clipboard(text: str, what: str) -> None:
    if copy_to_clipboard(text):
        print(f"    ✓ {what} is on the clipboard")
    else:
        print(f"    (no clipboard here — copy the {what} below)\n")
        print(indent(text))


# --- steps ---------------------------------------------------------------------------


def indent(text: str, prefix: str = "    │ ") -> str:
    return "\n".join(prefix + line for line in text.splitlines())


def show_images(images: list[pathlib.Path]) -> None:
    """Open the folder with the images selected, so they can be dragged in.

    None of the three sites accepts an image through a URL we can open, so this
    is as far as automation goes: put the files in front of you.
    """
    if not images:
        return
    for image in images:
        print(f"    image: {image}")
    if sys.platform == "win32":
        # /select, takes one file; the rest of the folder is visible next to it.
        subprocess.Popen(["explorer", "/select,", str(images[0])])
    print(f"    drag {'them' if len(images) > 1 else 'it'} into the composer, in this order")


def steps(post: dict, targets: dict, only: set[str]) -> list[dict]:
    out = []
    for platform in PLATFORMS:
        sec = post["sections"].get(platform)
        if not sec or platform not in only:
            continue
        for key in sec["targets"]:
            info = targets[platform][key]
            text, title = sec["text"], sec["title"]
            images = sec["images"]
            if platform == "x":
                url = "https://x.com/intent/post?text=" + quote(text)
                how = "The composer opens with the text filled in. Check it, then click Post."
                if images:
                    how += (" Drag the images in before you post — the share-intent link cannot "
                            "carry them, and an image replaces the link preview card.")
            elif platform == "facebook":
                url = info["url"]
                how = ("Click 'Write something…' (group) or 'What's on your mind' (profile), "
                       "press Ctrl+V, wait for the link preview, check the audience, click Post.")
                if images:
                    how += (" Add the images with Photo/Video, or drag them in; Facebook then drops "
                            "the link preview, so leave the URL in the text.")
            else:
                url = (f"https://www.reddit.com/r/{key}/submit?type=TEXT"
                       f"&title={quote(title)}&text={quote(text)}")
                how = ("Choose Text, check the title and body (paste with Ctrl+V if the body is empty), "
                       "read the sidebar rules, pick the flair they ask for, click Post.")
                if images:
                    how += (" Images go inside the body: switch the editor to Rich Text and drop each "
                            "one after the paragraph it illustrates. Some subreddits do not allow "
                            "images in a text post — if the drop is refused, post it without them.")
            out.append({"platform": platform, "target": key, "name": info.get("name", key),
                        "notes": info.get("notes", ""), "blocked": info.get("blocked"),
                        "url": url, "how": how, "text": text, "title": title, "images": images})
    return out


def ask(prompt: str, allowed: str) -> str:
    while True:
        answer = input(prompt).strip().lower()
        if answer == "" or answer[0] in allowed:
            return answer[:1]
        if URL.match(answer):
            return answer
        print(f"    type one of: {', '.join(allowed)}")


def run(post: dict, targets: dict, only: set[str], again: bool) -> None:
    history = read_history()
    todo = steps(post, targets, only)
    if not todo:
        print("Nothing to do.")
        return
    for i, step in enumerate(todo, 1):
        p, t = step["platform"], step["target"]
        link = (URL.findall(step["text"]) or [None])[0]
        print(f"\n[{i}/{len(todo)}] {p} → {step['name']}")
        if step["notes"]:
            print(f"    note: {step['notes']}")
        done = [h for h in history if h["post"] == post["name"] and h["platform"] == p
                and h["target"] == t and h.get("status") == "posted"]
        if done and not again:
            print(f"    already posted on {done[-1]['time'][:10]} — skipping (use --again to repeat)")
            continue
        if step["blocked"]:
            print(f"    ✗ {step['blocked']}")
            if ask("    open it anyway? [y/N] > ", "yn") != "y":
                continue
        warning = spacing_warning(p, link, history)
        if warning:
            print(f"    ⚠ {warning}")
            if ask("    go ahead anyway? [y/N] > ", "yn") != "y":
                continue
        print(indent(("Title: " + step["title"] + "\n\n" if step["title"] else "") + step["text"]))
        choice = ask("    Enter = open it, s = skip, q = quit > ", "sq")
        if choice == "q":
            return
        if choice == "s":
            continue
        if p == "reddit":
            put_on_clipboard(step["text"], "body")
        elif p == "facebook":
            put_on_clipboard(step["text"], "post text")
        show_images(step["images"])
        webbrowser.open(step["url"])
        print(f"    {step['how']}")
        while True:
            answer = ask("    posted? y / n / paste its URL"
                         + (" / t = copy title, b = copy body" if p == "reddit" else "")
                         + " > ", "yntb")
            if answer == "t":
                put_on_clipboard(step["title"], "title")
                continue
            if answer == "b":
                put_on_clipboard(step["text"], "body")
                continue
            break
        entry = {"time": now().isoformat(timespec="seconds"), "post": post["name"],
                 "platform": p, "target": t, "link": link,
                 "status": "posted" if answer not in ("n", "") else "skipped"}
        if URL.match(answer):
            entry["url"] = answer
        append_history(entry)
        history.append(entry)
        print(f"    recorded: {entry['status']}")


# --- cli -----------------------------------------------------------------------------


def main() -> None:
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("check", help="validate a post file")
    c.add_argument("post", type=pathlib.Path)
    r = sub.add_parser("run", help="walk through posting it")
    r.add_argument("post", type=pathlib.Path)
    r.add_argument("--only", default=",".join(PLATFORMS), help="e.g. x,facebook")
    r.add_argument("--again", action="store_true", help="repeat targets already posted")
    lg = sub.add_parser("log", help="record a post made outside the walk-through")
    lg.add_argument("post", type=pathlib.Path)
    lg.add_argument("platform", choices=PLATFORMS)
    lg.add_argument("target")
    lg.add_argument("--url")
    h = sub.add_parser("history", help="show what went where")
    h.add_argument("--days", type=int, default=30)
    args = ap.parse_args()

    if args.cmd == "history":
        cutoff = now() - dt.timedelta(days=args.days)
        rows = [e for e in read_history() if parse_time(e["time"]) > cutoff]
        for e in rows:
            print(f"{e['time'][:16].replace('T', ' ')}  {e['status']:7}  {e['platform']:8}  "
                  f"{e['target']:28}  {e['post']}  {e.get('url', '')}")
        if not rows:
            print(f"nothing in the last {args.days} days")
        return

    post = load_post(args.post)
    targets = load_targets()
    if args.cmd == "log":
        if args.target not in targets.get(args.platform, {}):
            sys.exit(f"'{args.target}' is not a {args.platform} target in targets.json")
        sec = post["sections"].get(args.platform, {"text": ""})
        entry = {"time": now().isoformat(timespec="seconds"), "post": post["name"],
                 "platform": args.platform, "target": args.target,
                 "link": (URL.findall(sec["text"]) or [None])[0], "status": "posted"}
        if args.url:
            entry["url"] = args.url
        append_history(entry)
        print("recorded")
        return

    errors, warnings = check(post, targets)
    for w in warnings:
        print(f"⚠ {w}")
    for e in errors:
        print(f"✗ {e}")
    if errors:
        sys.exit(1)
    if args.cmd == "check":
        for platform, sec in post["sections"].items():
            extra = f", {x_length(sec['text'])}/{X_LIMIT} as X counts" if platform == "x" else ""
            shots = f", {len(sec['images'])} images" if sec["images"] else ""
            print(f"✓ {platform}: {len(sec['text'])} characters{extra}{shots} → {', '.join(sec['targets'])}")
        return
    only = {p.strip() for p in args.only.split(",") if p.strip()}
    run(post, targets, only, args.again)


if __name__ == "__main__":
    main()
