# Claude Switch

在多个 Claude Code 账号之间切换的 Windows 托盘工具。**额度用满时自动换号，不用重启 Claude Code，正在进行的会话直接继续。**

<!-- 截图占位：主窗口（账号列表 + 活动条带）。见 docs/screenshots/ -->

## 安装

到 [Releases](../../releases) 下载 `ClaudeSwitch.exe`，双击运行。不需要装 .NET、不需要装 Rust、没有安装程序、没有其它文件。

**首次运行 Windows 会弹 SmartScreen 警告**——这个程序没有代码签名证书（一张证书每年几百美元，暂时没买）。点「更多信息」→「仍要运行」。

不放心的话，Release 里附了 SHA256，可以先核对：

```powershell
Get-FileHash ClaudeSwitch.exe -Algorithm SHA256
```

或者直接[从源码构建](#从源码构建)——构建步骤和 CI 用的完全一样。

## 它解决什么问题

你有多个 Claude 账号（个人 + 工作，或者几个订阅）。写着代码，5 小时额度满了，Claude Code 停在那里。你得手动导出凭据、换文件、重启。

这个工具把这件事变成：**它自己换，你继续写。**

- **切换对运行中的会话立即生效** — Windows 上凭据是文件，Claude Code 在文件变化时会重新读取，下一条消息就走新账号。不用重启，不用重开 VS Code 标签页。
- **自动切换** — 任一账号 5 小时或 7 天额度达到阈值，自动切到余量最多的可用账号。也会在当前账号**坏掉**时切走（登录失效、订阅到期、槽位为空），这是单纯看阈值永远不会触发的情况。
- **用量一目了然** — 每个账号的 5 小时 / 7 天余量、套餐（Pro / Max 20× / Team）、订阅开始日期。
- **用量总览** — 你在 Claude Code 上到底干了多少：token 总量、活动日历、各项目分布。

## 功能

### 账号

- 托管本机当前登录（「添加账号」），支持多槽位、别名、拖拽排序、停用
- **当前账号由实时登录反查**，不是回放上次切换的记录——`claude /login` 或其它工具切换都能被正确识别
- 套餐信息从各槽位自己的凭据 + `.claude.json` 备份读回，**不发任何请求**。不显示续费日期：OAuth token 没有账单权限，这个日期拿不到

### 用量

- 每个槽位单独获取，过期的 OAuth token 会先自动刷新（**当前登录账号的 token 不动**——轮换它会把你正在用的会话踢下线）
- 取不到时明确说明原因（`需重新登录` / `无凭据` / `无订阅额度` / `API Key` / `获取失败`），而不是显示空白

### 自动切换

- **只会切到有实测用量的账号**。没有数据的槽位被跳过，绝不当成"0% 已用、100% 空闲"——否则唯一那个不能干活的账号会排名第一
- 当前账号坏掉时的等待策略按"等下去有没有意义"区分：登录被清除立即切走，网络抖动则等满 `unhealthyTicks` 次
- 没有 access token 的凭据**拒绝激活**，手动切换也一样——那不是切换，是把你登出

### 目录与会话

- Claude Code 用过的所有目录、每个目录的会话列表，一键 `claude --resume` 继续任意历史会话
- 累计 token 统计按需执行（读全部会话记录，几百毫秒），结果带可视化
- **这份数据与账号无关**——Claude Code 不记录会话属于哪个账号

## 网络

请求遵循 `HTTPS_PROXY` / `ALL_PROXY` / `NO_PROXY`，Windows 上还会读系统代理设置——和 Claude Code 走同一条路。

> 如果你的网络只能通过代理访问 Anthropic，直连会收到 `403 "Request not allowed"`。这个错误**看起来像鉴权失败，其实是网络不通**。代理配置在启动时读取，改了要重启。

## 数据存放

| 位置 | 内容 |
|---|---|
| `~/.claude-swap-backup/credentials/` | 各槽位凭据（加密存储） |
| `~/.claude-swap-backup/configs/` | 各槽位 `.claude.json` 快照 |
| `~/.claude-swap-backup/sequence.json` | 槽位顺序与当前账号 |

格式与 [claude-swap](https://github.com/realiti4/claude-swap)（Python CLI）兼容，两者可以共用同一份备份。

## 从源码构建

需要 Rust 1.80+ 和 .NET 8 SDK。

```bash
cargo build -p claude-switch-ffi --release
dotnet publish gui-win/ClaudeSwitch.App/ClaudeSwitch.App.csproj \
  -c Release -p:PublishSingleFileBundle=true -o dist
```

产物是 `dist/ClaudeSwitch.exe` 一个文件。

开发时：

```bash
cargo test --workspace
dotnet test gui-win/ClaudeSwitch.App.Tests/ClaudeSwitch.App.Tests.csproj
dotnet run --project gui-win/ClaudeSwitch.App -c Release -- --fixture %TEMP%\cswitch-demo
```

`--fixture` 用一份隔离的演示数据启动（六个账号，覆盖各种套餐），不碰你真实的 Claude 登录。

## 项目结构

```
crates/core     claude-switch-core   锁、凭据、切换、用量、自动切换、Engine
crates/ffi      claude_switch.dll    C ABI（cs_engine_*）
gui-win/        Windows 托盘 GUI（WinForms）+ FfiSmoke + P/Invoke
```

- [docs/design-claude-switch.md](docs/design-claude-switch.md) — 架构、FFI、界面设计
- 行为规范来自上游 Python CLI [claude-swap](https://github.com/realiti4/claude-swap)——凭据处理、三锁切换事务、自动切换、轮询策略都以它为准。
  开发时可把它 clone 到 `reference/claude-swap/`（该目录不纳入版本控制）。

版本号以根目录 `VERSION` 为准，必须与 `Cargo.toml` 的 `[workspace.package].version` 一致。

## 平台

目前只有 Windows。核心是跨平台的 Rust，macOS / Linux 的原生外壳在设计文档里但还没做。

## License

MIT，见 [LICENSE](LICENSE)。备份格式与 claude-swap 保持互通。
