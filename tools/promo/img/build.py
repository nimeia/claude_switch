#!/usr/bin/env python3
"""Build the composed images for the parallel-sessions announcement.

    python tools/promo/img/build.py

Writes into this folder. The app screenshots next to these are the real app
(see site/README.md); the two images built here are diagrams, drawn in the
site's own colours so a post reads as one piece with the product page.

Pillow only. Fonts are the ones Windows ships: Segoe UI and Cascadia Mono.
"""

from __future__ import annotations

import pathlib

from PIL import Image, ImageDraw, ImageFont

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parents[2]
SHOTS = REPO / "site" / "assets" / "img"

# site/index.html :root
BG = "#F0EEE6"
CARD = "#FFFFFF"
INK = "#1C1B19"
INK2 = "#57534B"
INK3 = "#877F72"
LINE = "#E1DBCD"
ACCENT = "#C0603B"
TERM_BG = "#1B1A17"
TERM_DEEP = "#0E0D0C"
TERM_BAR = "#232220"
TERM_EDGE = "#33312E"
TERM_INK = "#DCD6CA"
TERM_HEAD = "#F5F2EA"
TERM_LEDE = "#BDB6A8"
MUTED = "#7E766B"
BAR_INK = "#9A9287"
GREEN = "#7FC79B"
AMBER = "#E3B55F"
CRIT = "#D98070"
ORANGE = "#E18A63"

F = "C:/Windows/Fonts/"
FACES = {"": "segoeui.ttf", "b": "segoeuib.ttf", "sb": "seguisb.ttf"}


def sans(size: int, weight: str = "") -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(F + FACES[weight], size)


def mono(size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(F + "CascadiaMono.ttf", size)


def spans(d: ImageDraw.ImageDraw, x: int, y: int, font, parts) -> int:
    """Draw (text, colour) runs on one line; returns the x after the last one."""
    for text, colour in parts:
        d.text((x, y), text, font=font, fill=colour)
        x += int(round(font.getlength(text)))
    return x


def wrap(text: str, font, width: int) -> list[str]:
    lines, line = [], ""
    for word in text.split():
        trial = (line + " " + word).strip()
        if font.getlength(trial) <= width:
            line = trial
        else:
            lines.append(line)
            line = word
    if line:
        lines.append(line)
    return lines


def meter(used: int) -> list[tuple[str, str]]:
    """The status line's 10-cell bar, coloured the way the renderer colours it."""
    filled = round(used / 10)
    colour = GREEN if used < 50 else AMBER if used < 80 else CRIT
    return [("\u2588" * filled, colour), ("\u2591" * (10 - filled), MUTED)]


def sign(im: Image.Image, d: ImageDraw.ImageDraw, colour: str) -> None:
    """Logo and site, bottom right — so a screenshot that travels keeps its source."""
    logo = Image.open(SHOTS / "logo.png").convert("RGBA").resize((34, 34), Image.LANCZOS)
    text, font = "Claude Switch · nimeia.github.io/claude_switch", sans(18)
    w = int(round(font.getlength(text)))
    x, y = im.width - 80 - w - 44, im.height - 62
    im.paste(logo, (x, y - 8), logo)
    d.text((x + 44, y), text, font=font, fill=colour)


def arrow(d: ImageDraw.ImageDraw, points, colour: str, width: int = 2) -> None:
    d.line(points, fill=colour, width=width, joint="curve")
    (x1, y1), (x2, y2) = points[-2], points[-1]
    h = 7
    if y1 == y2:
        s = 1 if x2 > x1 else -1
        d.polygon([(x2, y2), (x2 - s * h * 1.6, y2 - h), (x2 - s * h * 1.6, y2 + h)], fill=colour)
    else:
        s = 1 if y2 > y1 else -1
        d.polygon([(x2, y2), (x2 - h, y2 - s * h * 1.6), (x2 + h, y2 - s * h * 1.6)], fill=colour)


# --- 1. three terminals ---------------------------------------------------------------

TERMINALS = [
    ("acme-api \u2014 claude", "add pagination to the orders endpoint",
     "#2 work", 38, "7d 12%", "ctx 24%", "Sonnet 5 high", "acme-api (main)"),
    ("acme-web \u2014 claude", "why does the checkout total drift by a cent",
     "#3 ops", 8, "7d 4%", "ctx 11%", "Opus 5 high", "acme-web (feat/checkout)"),
    ("claude-switch \u2014 claude", "write the release notes for 0.4.0",
     "#1 personal", 71, "7d 33%", "ctx 52%", "Sonnet 5", "claude-switch (master)"),
]


def three_terminals(path: pathlib.Path) -> None:
    im = Image.new("RGB", (1600, 900), TERM_BG)
    d = ImageDraw.Draw(im)

    d.text((80, 62), "Three terminals. Three accounts. One machine.", font=sans(44, "b"), fill=TERM_HEAD)
    d.text((80, 128), "Every Claude Code terminal runs on the account you opened it for \u2014",
           font=sans(22), fill=TERM_LEDE)
    d.text((80, 160), "and the line at the bottom says which one, and how much it has left.",
           font=sans(22), fill=TERM_LEDE)

    x0, x1 = 80, 1520
    top, card_h, gap = 216, 176, 30
    bar_f, body_f = mono(13), mono(17)

    for i, (tab, prompt, slot, used, d7, ctx, model, where) in enumerate(TERMINALS):
        y = top + i * (card_h + gap)
        d.rounded_rectangle([x0, y, x1, y + card_h], 12, fill=TERM_DEEP, outline=TERM_EDGE, width=1)
        d.rounded_rectangle([x0, y, x1, y + 40], 12, fill=TERM_BAR)
        d.rectangle([x0 + 1, y + 26, x1 - 1, y + 34], fill=TERM_BAR)
        d.line([x0, y + 34, x1, y + 34], fill=TERM_EDGE, width=1)
        for k in range(3):
            cx = x0 + 18 + k * 15
            d.ellipse([cx, y + 12, cx + 9, y + 21], fill="#3A3733")
        d.text((x0 + 70, y + 9), tab, font=bar_f, fill=BAR_INK)

        bx, by = x0 + 22, y + 54
        spans(d, bx, by, body_f, [("> ", MUTED), (prompt, TERM_INK)])
        # U+25CF, not Claude Code's U+23FA: Cascadia Mono has no glyph for that one.
        d.text((bx, by + 28), "\u25cf working\u2026", font=body_f, fill=MUTED)
        spans(d, bx, by + 74, body_f, [
            (slot, ORANGE), (" \u00b7 5h ", TERM_INK), *meter(used),
            (" %d%% \u00b7 %s \u00b7 %s \u00b7 %s \u00b7 %s" % (used, d7, ctx, model, where), TERM_INK),
        ])

    d.text((80, 838), "The default login, your other terminals and the VS Code extension carry on untouched.",
           font=sans(20), fill=BAR_INK)
    sign(im, d, BAR_INK)
    im.save(path)
    print("wrote", path.name, im.size)


# --- 2. one config dir per account ----------------------------------------------------


def config_dirs(path: pathlib.Path) -> None:
    im = Image.new("RGB", (1600, 900), BG)
    d = ImageDraw.Draw(im)

    d.text((80, 58), "One config directory per account", font=sans(42, "b"), fill=INK)
    lede = ("Claude Code resolves its config and its credentials from CLAUDE_CONFIG_DIR. Point it at a "
            "per-account profile and that terminal is logged in as that account.")
    for i, line in enumerate(wrap(lede, sans(21), 1340)):
        d.text((80, 120 + i * 30), line, font=sans(21), fill=INK2)

    mono_s, small = mono(15), sans(15)

    # your own ~/.claude
    lx0, ly0, lx1, ly1 = 80, 236, 500, 566
    d.rounded_rectangle([lx0, ly0, lx1, ly1], 14, fill=CARD, outline=LINE, width=2)
    d.text((lx0 + 24, ly0 + 22), "~/.claude", font=mono(19), fill=ACCENT)
    d.text((lx0 + 24, ly0 + 54), "your default login", font=small, fill=INK3)
    for i, item in enumerate(["settings.json", "CLAUDE.md", "skills/", "commands/", "agents/",
                              "user MCP servers"]):
        d.text((lx0 + 24, ly0 + 94 + i * 30), item, font=mono_s, fill=INK2)

    # per-account session profiles
    for name, y in [("sessions/2-work/", 255), ("sessions/3-ops/", 385)]:
        d.rounded_rectangle([620, y, 1020, y + 96], 12, fill=CARD, outline=LINE, width=2)
        d.text((644, y + 20), "~/.claude-swap-backup/", font=mono(14), fill=INK3)
        d.text((644, y + 46), name, font=mono(17), fill=INK)
        arrow(d, [(500, y + 48), (610, y + 48)], ACCENT)
        arrow(d, [(1020, y + 48), (1130, y + 48)], ACCENT)

    d.text((508, 212), "copied in at every launch", font=small, fill=INK3)
    d.text((1028, 212), "CLAUDE_CONFIG_DIR", font=mono(14), fill=INK3)

    # the terminals they produce
    for slot, cwd, y in [("#2 work", "D:\\work\\acme-api", 255),
                         ("#3 ops", "D:\\work\\acme-web", 385),
                         ("#1 personal", "D:\\personal\\claude-switch", 515)]:
        d.rounded_rectangle([1140, y, 1520, y + 96], 12, fill=TERM_DEEP, outline=TERM_EDGE, width=1)
        d.text((1164, y + 18), slot, font=mono(17), fill=ORANGE)
        d.text((1164, y + 46), cwd, font=mono(15), fill=TERM_LEDE)
        d.text((1164, y + 68), "> claude", font=mono(14), fill=MUTED)

    # a plain `claude` is still the default login
    arrow(d, [(290, 566), (290, 634), (1090, 634), (1090, 563), (1130, 563)], INK3)
    d.text((596, 600), "a plain claude, in any other terminal, is still your default login",
           font=small, fill=INK3)

    note = ("Transcripts are never copied \u2014 a copy would fork your history. Every profile is read as an "
            "extra root instead, so Directories, usage and Resume session show the work done under any "
            "account, each directory once.")
    for i, line in enumerate(wrap(note, sans(19), 1440)):
        d.text((80, 664 + i * 28), line, font=sans(19), fill=INK2)

    d.rounded_rectangle([80, 740, 1520, 814], 12, fill="#F6E5DB", outline="#EBD3C4", width=1)
    tip = ("Right-click a card \u2192 Open terminal with this account. Or bind a directory to an account, "
           "and it opens that way every time.")
    d.text((104, 766), tip, font=sans(20), fill="#A34E2E")

    sign(im, d, INK3)
    im.save(path)
    print("wrote", path.name, im.size)


# --- 3. the real Directories window ---------------------------------------------------


def directories(path: pathlib.Path) -> None:
    """The real Directories window, with the empty middle taken out.

    The two bands that matter are the table (the Account column is the point of
    the picture) and the footnote under it. Everything between them is blank
    list, and on a phone-sized post it is what pushes the note off the screen.
    """
    src = Image.open(SHOTS / "app-directories.png").convert("RGB")
    table, note = src.crop((0, 0, 1680, 400)), src.crop((0, 930, 1680, 979))
    im = Image.new("RGB", (1680, table.height + note.height + 1), "#FFFFFF")
    im.paste(table, (0, 0))
    ImageDraw.Draw(im).line([0, table.height, 1680, table.height], fill=LINE)
    im.paste(note, (0, table.height + 1))
    im.save(path)
    print("wrote", path.name, im.size)


if __name__ == "__main__":
    three_terminals(HERE / "01-three-terminals.png")
    config_dirs(HERE / "02-config-dirs.png")
    directories(HERE / "03-directories.png")
