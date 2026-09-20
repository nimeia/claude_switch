# The Microsoft Store build

The Store ships an MSIX package. It is the same program as the portable
download, built and installed differently.

| | Portable (`Releases`) | Store (MSIX) |
|---|---|---|
| Shape | one self-extracting `.exe` | a package Windows installs |
| Code signing | none — SmartScreen warns on first run | the Store signs it; no warning |
| Uninstall | delete the file | Settings → Apps |
| Launch at logon | `HKCU\...\Run` | the manifest's `startupTask` |

That last row is the only behavioural difference, and it is not optional. A
packaged process writes `HKCU` into a private per-package overlay, so a `Run`
entry written there is never read at logon: it would look like it worked and
then silently do nothing. `StartupHelper` detects the package and calls
`Windows.ApplicationModel.StartupTask` instead. The user can still veto it in
**Settings → Apps → Startup**, and the app reports that rather than leaving the
checkbox lying.

## Build it

```powershell
cargo build -p claude-switch-ffi -p claude-switch-statusline --release

dotnet publish gui-win/ClaudeSwitch.App/ClaudeSwitch.App.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o dist-msix

./packaging/pack-msix.ps1 -PublishDir dist-msix -OutDir artifacts-msix
```

Not single-file, unlike the portable build: inside a package, extracting the
runtime to `%TEMP%` on first launch buys nothing.

Needs `makeappx.exe` from the Windows 10/11 SDK. The script finds the newest
installed SDK, or takes `-MakeAppx <path>`.

The output is an **unsigned** `.msix`. That is what Partner Center expects —
the Store signs the package with the certificate behind the publisher ID.

## Identity

`packaging/msix/AppxManifest.xml` hard-codes the values Partner Center
generated for this product (**Product management → Product identity**). They
must match exactly or the upload is rejected.

| Field | Value |
|---|---|
| `Package/Identity/Name` | `xiaoqian.ClaudeSwitch` |
| `Package/Identity/Publisher` | `CN=4BA29194-C2B3-4168-A1CB-6B285B39742D` |
| `Package/Properties/PublisherDisplayName` | `xiaoqian` |
| Package Family Name | `xiaoqian.ClaudeSwitch_3raqy9m2a4fk2` |
| Store ID | `9NZRW7JVLN8W` |

The version comes from `VERSION`, with `.0` appended — the Store reserves the
fourth part and rejects anything else.

## Testing it before submitting

An unsigned package cannot be installed. To try it locally, sign it with a
certificate whose subject is *exactly* the `Publisher` string above, and trust
that certificate on the test machine:

```powershell
$subject = 'CN=4BA29194-C2B3-4168-A1CB-6B285B39742D'
$cert = New-SelfSignedCertificate -Type Custom -Subject $subject `
    -KeyUsage DigitalSignature -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

$signtool = (Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\10.*\x64\signtool.exe' |
    Sort-Object FullName -Descending)[0].FullName
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint artifacts-msix\ClaudeSwitch-*.msix
```

Then export the certificate and import it into
`Cert:\LocalMachine\Root` (this trusts a signing key on that machine — do it on
a test machine, and remove it afterwards). `Add-AppxPackage` will then install
the signed package.

The signature is stripped and replaced when the Store publishes, so a test
certificate never reaches anyone else. **Do not commit one.**

## What to check in a packaged build

Things that work in the portable build and can break once packaged:

- **Launch at logon** — tick the box in Automation, sign out and back in.
- **Terminals** — Open terminal, Resume session and Directories launch other
  programs with `CLAUDE_CONFIG_DIR` set; confirm the child process sees it.
- **`~/.claude` access** — the package is full-trust, so the user profile is
  not virtualised, but this is the app's whole job. Add an account and switch.
- **The status line binary** — it is written to `~/.claude-swap-backup/bin/`,
  outside the package, and run by Claude Code rather than by this app.
- **Move Claude data** — it creates directory junctions.
