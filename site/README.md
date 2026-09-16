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

## Publishing

`site/` is plain static files — any host works. For GitHub Pages, either move this directory to `docs/` and point Pages at it, or add a workflow that uploads `site/` with `actions/upload-pages-artifact`.
