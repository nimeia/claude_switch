<#
.SYNOPSIS
    Build the MSIX package for the Microsoft Store from a published folder.

.DESCRIPTION
    The Store build is not the portable download. It differs in two ways that
    matter, and both are deliberate:

      - Not single-file. The portable .exe extracts its runtime to %TEMP% on
        first launch; inside a package that is pointless work and an extra
        thing to explain. Here the runtime simply sits in the package.
      - Launch at logon goes through the manifest's startupTask, not the HKCU
        Run key, which a packaged process cannot write for real. See
        StartupHelper.cs.

    Run after:

        cargo build -p claude-switch-ffi -p claude-switch-statusline --release
        dotnet publish gui-win/ClaudeSwitch.App/ClaudeSwitch.App.csproj `
            -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=false -o <PublishDir>

    The result is an unsigned .msix. That is what Partner Center wants: the
    Store signs the package itself, with the certificate behind the publisher
    ID in the manifest. To sideload it for testing you must sign it yourself
    with a certificate whose subject is exactly the Publisher string in
    AppxManifest.xml, and trust that certificate — see docs/msix.md.

.PARAMETER PublishDir
    Directory holding the published app (ClaudeSwitch.exe and its runtime).

.PARAMETER OutDir
    Where to write the .msix.

.PARAMETER MakeAppx
    Path to makeappx.exe. Found in the installed Windows SDK when omitted.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDir,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $MakeAppx
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$version = (Get-Content (Join-Path $repo 'VERSION') -Raw).Trim()

# The Store reserves the fourth part of the version and rejects anything but 0.
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION is '$version'; expected three numeric parts like 0.4.0"
}
$packageVersion = "$version.0"

$exe = Join-Path $PublishDir 'ClaudeSwitch.exe'
if (-not (Test-Path $exe)) {
    throw "no published exe at $exe — see the publish command in this script's help"
}
# A single-file publish would leave the package with one huge exe that unpacks
# itself to %TEMP% at runtime: legal, but it defeats the point of packaging.
foreach ($needed in 'claude_switch.dll', 'cs-statusline.exe') {
    if (-not (Test-Path (Join-Path $PublishDir $needed))) {
        throw "$needed is missing from $PublishDir — run the cargo build first"
    }
}

function Resolve-MakeAppx {
    if ($MakeAppx) {
        if (-not (Test-Path $MakeAppx)) { throw "makeappx.exe not at $MakeAppx" }
        return $MakeAppx
    }
    $found = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory -ErrorAction SilentlyContinue |
        Where-Object Name -match '^10\.' |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if (-not $found) {
        throw 'makeappx.exe not found. Install the Windows 10/11 SDK, or pass -MakeAppx.'
    }
    return $found
}

# Tile images, scaled from the one logo the website already uses, so the Store
# and the Start menu cannot drift from it. System.Drawing rather than a
# committed set of PNGs: derived files that nothing regenerates go stale.
function Write-Logo {
    param([string] $Source, [string] $Destination, [int] $Size)

    Add-Type -AssemblyName System.Drawing
    $src = [System.Drawing.Image]::FromFile($Source)
    try {
        $bmp = New-Object System.Drawing.Bitmap $Size, $Size
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $g.Clear([System.Drawing.Color]::Transparent)
                $g.DrawImage($src, 0, 0, $Size, $Size)
            }
            finally { $g.Dispose() }
            $bmp.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $bmp.Dispose() }
    }
    finally { $src.Dispose() }
}

$makeappx = Resolve-MakeAppx

Remove-Item $OutDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$staging = Join-Path $OutDir '_staging'
New-Item -ItemType Directory -Path $staging -Force | Out-Null

# Symbols are for us, not for the 77 MB every customer downloads.
Copy-Item (Join-Path $PublishDir '*') $staging -Recurse -Exclude '*.pdb'

$assets = Join-Path $staging 'assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$logo = Join-Path $repo 'site\assets\img\logo.png'
Write-Logo -Source $logo -Destination (Join-Path $assets 'Square150x150Logo.png') -Size 150
Write-Logo -Source $logo -Destination (Join-Path $assets 'Square44x44Logo.png') -Size 44
Write-Logo -Source $logo -Destination (Join-Path $assets 'StoreLogo.png') -Size 50
# Unplated: what the taskbar and Alt-Tab show, with no coloured backplate.
Write-Logo -Source $logo -Destination (Join-Path $assets 'Square44x44Logo.targetsize-24_altform-unplated.png') -Size 24

$manifest = (Get-Content (Join-Path $PSScriptRoot 'msix\AppxManifest.xml') -Raw).Replace('@VERSION@', $packageVersion)
[System.IO.File]::WriteAllText(
    (Join-Path $staging 'AppxManifest.xml'), $manifest, [System.Text.UTF8Encoding]::new($false))

$msix = Join-Path $OutDir "ClaudeSwitch-$version-win-x64.msix"
& $makeappx pack /o /d $staging /p $msix
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed with exit code $LASTEXITCODE" }

Remove-Item $staging -Recurse -Force

$hash = (Get-FileHash $msix -Algorithm SHA256).Hash.ToLower()
Write-Host ""
Write-Host "$hash  $(Split-Path -Leaf $msix)"
Get-Item $msix | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
