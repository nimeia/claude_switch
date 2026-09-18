# Contributing

Bug reports and pull requests are welcome. For anything larger than a fix, open an issue first so the approach can be agreed before the work.

## Build and test

Rust 1.80+ and the .NET 8 SDK. The native engine has to be built before the GUI:

```bash
cargo build -p claude-switch-ffi --release
cargo test --workspace
dotnet test gui-win/ClaudeSwitch.App.Tests/ClaudeSwitch.App.Tests.csproj
```

`dotnet run --project gui-win/ClaudeSwitch.App -c Release -- --fixture %TEMP%\cswitch-demo` starts the app on demo data without touching your real Claude login.

CI runs the same on Windows for every pull request: `cargo fmt --check`, `cargo clippy -- -D warnings`, the Rust and GUI tests, a single-file publish and a launch smoke test. A pull request needs that to pass.

## Things that trip people up

- **UI text** goes in both `gui-win/ClaudeSwitch.App/Strings/en.json` and `zh-Hans.json`. `LocTests` fails when the keys or the `{0}` placeholders differ between them.
- **Credential handling, locking and auto-switch** follow [claude-swap](https://github.com/realiti4/claude-swap); the backup layout stays compatible with it. The design notes are in [docs/design-claude-switch.md](docs/design-claude-switch.md).
- **Versions and releases** are the maintainer's: a release is a `VERSION` bump reaching `master`. Leave `VERSION` and `Cargo.toml` versions alone in a pull request.
- **Screenshots** on the website come from the app's layout probe on demo data, never from a real account — see [site/README.md](site/README.md).
