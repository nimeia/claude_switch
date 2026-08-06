<#
.SYNOPSIS
    Assemble the downloadable artifacts from a published single-file exe.

.DESCRIPTION
    Produces both shapes users ask for, from one build:

      ClaudeSwitch-<ver>-win-x64.exe   the bare executable
      ClaudeSwitch-<ver>-win-x64.zip   the same exe plus the notes and licence
      SHA256SUMS.txt                   checksums for both

    The zip is not about dependencies — the exe already has none. It exists
    because browsers block or scare-warn on .exe downloads far more readily than
    on .zip, and because a first-time user should get the quick-start and the
    licence without going back to the repository.

    Run after: dotnet publish -p:PublishSingleFileBundle=true -o <PublishDir>

.PARAMETER PublishDir
    Directory holding the freshly published ClaudeSwitch.exe.

.PARAMETER OutDir
    Where to write the release artifacts.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDir,
    [Parameter(Mandatory)] [string] $OutDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$version = (Get-Content (Join-Path $repo 'VERSION') -Raw).Trim()
$stem = "ClaudeSwitch-$version-win-x64"

$exe = Join-Path $PublishDir 'ClaudeSwitch.exe'
if (-not (Test-Path $exe)) {
    throw "no published exe at $exe — run dotnet publish -p:PublishSingleFileBundle=true first"
}

# A publish directory with anything else in it means the bundle did not absorb
# a dependency, and the zip would ship a file the bare .exe download lacks.
$stray = Get-ChildItem $PublishDir -File | Where-Object Name -ne 'ClaudeSwitch.exe'
if ($stray) {
    throw "publish dir has unbundled files: $($stray.Name -join ', ')"
}

Remove-Item $OutDir -Recurse -Force -ErrorAction SilentlyContinue
$staging = Join-Path $OutDir '_staging'
New-Item -ItemType Directory -Path $staging -Force | Out-Null

Copy-Item $exe (Join-Path $staging 'ClaudeSwitch.exe')
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $staging 'LICENSE.txt')
# Notepad on older Windows guesses the encoding wrong without a BOM, and this
# file is mostly Chinese — a mojibake quick-start is worse than none.
$notes = Get-Content (Join-Path $PSScriptRoot 'README-dist.txt') -Raw
[System.IO.File]::WriteAllText(
    (Join-Path $staging '使用说明.txt'), $notes, [System.Text.UTF8Encoding]::new($true))

$zip = Join-Path $OutDir "$stem.zip"
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
Copy-Item $exe (Join-Path $OutDir "$stem.exe")
Remove-Item $staging -Recurse -Force

# One checksum file covering both downloads, in the format sha256sum -c reads.
Push-Location $OutDir
try {
    $lines = Get-ChildItem -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name |
        ForEach-Object { "$((Get-FileHash $_.Name -Algorithm SHA256).Hash.ToLower())  $($_.Name)" }
    [System.IO.File]::WriteAllLines(
        (Join-Path $OutDir 'SHA256SUMS.txt'), $lines, [System.Text.UTF8Encoding]::new($false))
    $lines | ForEach-Object { Write-Host $_ }
}
finally { Pop-Location }

Get-ChildItem $OutDir -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
