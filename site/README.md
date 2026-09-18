# Website

The product page for Claude Switch: one static `index.html` with its CSS and JS inline, plus screenshots in `assets/img/`. No build step, no dependencies — open `site/index.html` in a browser and it is the site.

English and 简体中文 both ship in the HTML; the toggle in the header only decides which copy is hidden, so the page reads correctly with JavaScript off (it falls back to English) and every string is translated in one place. Light and dark follow the viewer's system theme until they pick one; the hero screenshot follows the page theme.

## Screenshots

Every screenshot is the real app, captured by the layout probe against the built-in `--fixture` demo data — never a mockup. To retake them:

```powershell
$shots = "$env:TEMP\cs-shots"
$env:CLAUDE_SWITCH_LANG = "en"            # or zh-Hans for the Chinese set
$env:CLAUDE_SWITCH_LAYOUT_DIR = $shots
$env:CLAUDE_SWITCH_LAYOUT_EXIT = "1"
$env:CLAUDE_SWITCH_PROBE_SETTLE_MS = "4000"
$env:CLAUDE_SWITCH_PROBE_SIZE = "1800x1400"
$env:CLAUDE_SWITCH_PROBE_OVERVIEW = "1"
$env:CLAUDE_SWITCH_PROBE_SESSIONS = "1"
$env:CLAUDE_SWITCH_PROBE_PROJECTS = "1"
$env:CLAUDE_SWITCH_PROBE_DETAIL = "1"
dotnet build gui-win/ClaudeSwitch.App/ClaudeSwitch.App.csproj -c Release
gui-win/ClaudeSwitch.App/bin/Release/net8.0-windows/ClaudeSwitch.exe --skip-onboarding --fixture "$env:TEMP\cswitch-site-demo"
```

`CLAUDE_SWITCH_PROBE_FLIPTHEME=1` gives the dark set. Two notes learned the hard way:

- **Overlay captures (`PROBE_RESUME`, `PROBE_TOOLS`, `PROBE_DARK`) are screen copies** — they photograph whatever is behind the window, so they are not safe for a public page. `FLIPTHEME` renders dark through `PrintWindow` instead.
- The probe reads and writes the **real** `%LOCALAPPDATA%\ClaudeSwitch\ui-prefs.ini`, so a run can flip your own saved theme. Check it afterwards.

The captures carry a black margin where `PrintWindow` overshoots the window; crop it before use (`ImageChops.difference` against black, then `getbbox`).

The overview and directory windows are only worth photographing with history behind them. The fixture seeds three short transcripts; for the shots on this page the demo profiles were filled with a couple of months of synthetic sessions first.

## Preview

Open `index.html` directly, or serve the folder so it behaves like the published site:

```powershell
python -m http.server 8000 -d site    # then http://localhost:8000/
```

## Publishing

[`.github/workflows/pages.yml`](../.github/workflows/pages.yml) publishes this folder to GitHub Pages at **https://nimeia.github.io/claude_switch/**. It runs when a push to `master` changes `site/` (or the workflow), and from **Actions → pages → Run workflow**. There is no build: it copies `site/` without this README, fails if `index.html` references a local file that is not here, and deploys.

One-time setup, in the repository settings: **Pages → Build and deployment → Source: GitHub Actions**. Until that is set, the run stops at the configure step with "Get Pages site failed".

- **Paths stay relative.** The site lives under `/claude_switch/`, so a path starting with `/` would point outside it. Keep `assets/img/...` style references
- **The version does not need a redeploy.** The HTML names the release it was written for; on load the page asks the GitHub API for the latest release and replaces the version in the hero and the file names on the download cards (`data-release` / `data-release-file`). If that request fails, the written text stays. Bump it here now and then so a visitor without JavaScript is not far behind
- **Link previews** use absolute URLs (`canonical`, `og:url`, `og:image` in `<head>`). Update those three if the site moves to a custom domain; the domain itself is set under Settings → Pages, not with a `CNAME` file, when publishing from Actions
