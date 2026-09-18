#!/usr/bin/env python3
"""Assemble the published website from site/ into an output folder.

    python tools/site/build.py _site

- copies site/ without its README
- index.html: the English page (x-default)
- zh/index.html: the same page showing Chinese by default, with a Chinese
  <title>, description and canonical URL, so search engines index a Chinese
  URL. The page carries both languages either way; only the default and the
  metadata differ. The Chinese metadata lives in data-zh attributes in
  site/index.html, which are dropped from both outputs.
- sitemap.xml: both URLs, each listing the other as its language alternate
- fails if either page references a local file that is not there

Standard library only: CI runs it on a bare ubuntu runner.
"""

from __future__ import annotations

import datetime
import pathlib
import re
import shutil
import sys

BASE = "https://nimeia.github.io/claude_switch/"
ZH = BASE + "zh/"
REPO = pathlib.Path(__file__).resolve().parents[2]

DATA_ZH = re.compile(r'\s+data-zh="[^"]*"')
TITLE_ZH = re.compile(r'<title data-zh="([^"]*)">[^<]*</title>')
META_ZH = re.compile(r'content="[^"]*"\s+data-zh="([^"]*)"')
LOCAL_REF = re.compile(r'(?:src|href)="([^"#:?]+)"')


def replace_once(text: str, old: str, new: str) -> str:
    if text.count(old) != 1:
        sys.exit(f"build.py: expected exactly one {old!r} in site/index.html")
    return text.replace(old, new)


def english(page: str) -> str:
    return DATA_ZH.sub("", page)


def chinese(page: str) -> str:
    page = replace_once(page, '<html lang="en">', '<html lang="zh-Hans" data-lang="zh">')
    page, titles = TITLE_ZH.subn(r"<title>\1</title>", page)
    page, metas = META_ZH.subn(r'content="\1"', page)
    if titles != 1 or metas < 1:
        sys.exit("build.py: site/index.html lost its data-zh title or description")
    page = replace_once(
        page,
        f'<link rel="canonical" href="{BASE}">',
        f'<link rel="canonical" href="{ZH}">',
    )
    page = replace_once(
        page,
        f'<meta property="og:url" content="{BASE}">',
        f'<meta property="og:url" content="{ZH}">',
    )
    page = replace_once(
        page,
        '<meta property="og:locale" content="en_US">\n<meta property="og:locale:alternate" content="zh_CN">',
        '<meta property="og:locale" content="zh_CN">\n<meta property="og:locale:alternate" content="en_US">',
    )
    # One level down: local files are one directory up.
    page = re.sub(r'(src|href)="assets/', r'\1="../assets/', page)
    return DATA_ZH.sub("", page)


def sitemap(today: str) -> str:
    alternates = "".join(
        f'\n    <xhtml:link rel="alternate" hreflang="{lang}" href="{url}"/>'
        for lang, url in (("en", BASE), ("zh-Hans", ZH), ("x-default", BASE))
    )
    urls = "".join(
        f"\n  <url>\n    <loc>{loc}</loc>\n    <lastmod>{today}</lastmod>{alternates}\n  </url>"
        for loc in (BASE, ZH)
    )
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"'
        ' xmlns:xhtml="http://www.w3.org/1999/xhtml">'
        f"{urls}\n</urlset>\n"
    )


def missing_refs(out: pathlib.Path, page: pathlib.Path) -> list[str]:
    missing = []
    for ref in sorted(set(LOCAL_REF.findall(page.read_text(encoding="utf-8")))):
        target = (page.parent / ref).resolve()
        if not target.exists() or out.resolve() not in target.parents:
            missing.append(f"{page.relative_to(out).as_posix()} references {ref}")
    return missing


def main() -> None:
    if len(sys.argv) != 2:
        sys.exit("usage: build.py <output dir>")
    out = pathlib.Path(sys.argv[1])
    if out.exists():
        shutil.rmtree(out)
    shutil.copytree(REPO / "site", out, ignore=shutil.ignore_patterns("README.md"))

    source = (out / "index.html").read_text(encoding="utf-8")
    (out / "index.html").write_text(english(source), encoding="utf-8")
    (out / "zh").mkdir()
    (out / "zh" / "index.html").write_text(chinese(source), encoding="utf-8")
    today = datetime.datetime.now(datetime.timezone.utc).date().isoformat()
    (out / "sitemap.xml").write_text(sitemap(today), encoding="utf-8")

    missing = missing_refs(out, out / "index.html") + missing_refs(out, out / "zh" / "index.html")
    for line in missing:
        print(f"::error file=site/index.html::{line}, which is not in site/")
    if missing:
        sys.exit(1)
    print(f"built {out}: index.html, zh/index.html, sitemap.xml")


if __name__ == "__main__":
    main()
