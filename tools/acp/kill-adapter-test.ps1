# Fault injection: kill the ACP adapter mid-turn and prove auto-continue
# reconnects, resumes the SAME session, and finishes the work.
#
# Without this the retry path is only unit-tested; this exercises the real
# respawn + session/load round trip against a live agent.
param(
    [int]$KillAfterSeconds = 25,
    [string]$WorkDir = "$env:TEMP\acp-fault"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $repo "target\debug\acp-run.exe"

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
Remove-Item -Path (Join-Path $WorkDir "*.txt") -ErrorAction SilentlyContinue

# Must stay one line: Start-Process word-splits multiline argument strings.
$prompt = 'Do these steps one at a time, using a separate Write tool call for each file, and pause to think briefly between each one: create step1.txt containing STEP1, then step2.txt containing STEP2, then step3.txt containing STEP3, then step4.txt containing STEP4, then step5.txt containing STEP5. Finally reply DONE.'

Write-Host "=== launching acp-run (adapter will be killed after ${KillAfterSeconds}s) ==="
$out = Join-Path $WorkDir "run.log"
# Start-Process joins an -ArgumentList array with plain spaces and adds no
# quoting, so any value containing a space must carry its own quotes.
$argLine = "--cwd `"$WorkDir`" --mode acceptEdits --allow-tools " +
           "--max-attempts 3 --base-delay 3 --turn-timeout 600 --prompt `"$prompt`""

$proc = Start-Process -FilePath $exe -PassThru -NoNewWindow -RedirectStandardOutput $out `
    -RedirectStandardError (Join-Path $WorkDir "run.err") `
    -ArgumentList $argLine

Start-Sleep -Seconds $KillAfterSeconds

$adapters = Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" |
    Where-Object { $_.CommandLine -like "*claude-agent-acp*" }

if (-not $adapters) {
    Write-Host "!! no adapter process found to kill (turn may have finished early)"
} else {
    foreach ($a in $adapters) {
        Write-Host "=== killing adapter pid $($a.ProcessId) ==="
        Stop-Process -Id $a.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

$proc | Wait-Process -Timeout 900
Write-Host "`n=== run output ==="
Get-Content $out
Write-Host "`n=== files produced ==="
Get-ChildItem $WorkDir -Filter "step*.txt" | ForEach-Object {
    "{0}: {1}" -f $_.Name, (Get-Content $_.FullName -Raw).Trim()
}
