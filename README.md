# Claude Switch

在多个 Claude Code 账号之间切换的 Windows 托盘工具。**额度用满时自动换号，不用重启 Claude Code，正在进行的会话直接继续。**

<!-- 截图占位：主窗口（账号列表 + 活动条带）。见 docs/screenshots/ -->

## 安装

到 [Releases](../../releases) 下载，两种任选，内容一样：

| 下载 | 适合 |
|---|---|
| `ClaudeSwitch-<版本>-win-x64.zip` | **推荐**。解压即用，附使用说明与许可证；浏览器对 zip 的拦截比 exe 少 |
| `ClaudeSwitch-<版本>-win-x64.exe` | 只要程序本身 |

不需要装 .NET，不需要装 Rust。

**没有安装程序**——它是一个绿色可执行文件：不写注册表、不要管理员权限、放哪都行、删掉就干净。开机自启由应用内的复选框控制（写当前用户的启动项，取消勾选即移除）。

唯一的例外：.NET 单文件包首次运行时会把自带运行时解压到 `%TEMP%\.net\ClaudeSwitch\`。这是 .NET 的标准行为，不是安装。

**首次运行 Windows 会弹 SmartScreen 警告**——这个程序没有代码签名证书（一张证书每年几百美元，暂时没买）。点「更多信息」→「仍要运行」。

从 zip 解压出来的文件同样带「来自网络」标记，**压缩包不会绕过这个提示**。

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

### 并行会话

**在不同终端里同时用不同账号。** 卡片右键「用此账号打开终端」，选一个目录就开一个新终端，
里面的 Claude Code 以该账号登录——**默认登录、其它终端、VS Code 扩展全都不受影响**。

原理是给每个账号准备一份独立的配置目录（`<备份目录>/sessions/<槽位>-<邮箱>/`），
启动时用 `CLAUDE_CONFIG_DIR` 指过去。Claude Code 的配置和凭据查找都认这个变量，所以隔离是完整的。

- **目录可以绑定账号**——「目录」窗口里右键 →「绑定到账号」。绑定后从「继续会话」或「目录」窗口打开，
  自动用该账号，不用每次选。子目录继承最近的上级绑定
- **共享你自己的配置**：`settings.json` / `CLAUDE.md` / `skills/` / `commands/` / `agents/` 和
  用户级 MCP 服务器每次启动都从 `~/.claude` 同步过去。会话里改的会在下次启动被覆盖——改就改 `~/.claude`
- **对话历史不复制**（复制等于分叉）。「目录」窗口、用量总览、「继续会话」会把每个会话配置**一起扫描并合并**，
  所以哪个账号做的事都看得见，同一个目录只出现一次
- **自动切换会跳过有终端在跑的账号**——它的额度本来就在被消耗，再把它设成默认登录会让同一个
  refresh token 出现在两个配置目录里
- 要打开的账号如果**就是当前默认登录**，直接起裸 `claude`，不建第二份凭据副本
- 账号删除时，它的会话配置和目录绑定一起清掉；有终端在跑时拒绝删除

### 目录与会话

- 工具栏「继续会话」下拉和托盘菜单直接列出各目录最近的会话，**一步回到上次的对话**
- 「目录」窗口里可以浏览全部目录与每个目录的完整会话列表
- 恢复会话时**直接运行 `claude`，不经 cmd**——终端由你自己的「默认终端应用」设置决定，
  Claude 退出后窗口正常关闭，不会留一个 cmd 提示符
- 累计 token 统计按需执行（读全部会话记录，几百毫秒），结果带可视化
- **目录本身与账号无关**——Claude Code 不记录一段对话属于哪个账号。「账号」列显示的是你给这个目录
  设的绑定（决定从这里打开时用哪个账号），不是从对话记录里读出来的

### 界面语言

英文 / 简体中文，工具栏右侧的语言按钮随时切换，**不用重启**——切完主窗口、卡片、状态栏立刻换语言，
其它窗口下次打开时生效。首次启动按系统语言自动选择，选过之后记住你的选择。

需要临时指定一次，可以用环境变量：`CLAUDE_SWITCH_LANG=en`（优先级高于记住的选择）。

<details>
<summary>想加一门语言？</summary>

复制 `gui-win/ClaudeSwitch.App/Strings/en.json` 改名为你的语言代码（如 `ja.json`），翻译值，
然后在 `Loc.Available` 里加一行。文件是嵌入资源，不用改构建脚本。

`LocTests` 会强制每份词条与英文**键完全一致**、`{0}` 占位符完全对应——漏翻或写错占位符是测试失败，
不会变成用户界面上的半句英文。

一句实话：**英文比中文宽 1.5–2 倍**，这个项目为此改过工具栏、卡片量表、订阅字段列和热力图图例的宽度。
加语言时请把界面渲染出来看一眼，别只看 JSON。
</details>

## 网络

请求遵循 `HTTPS_PROXY` / `ALL_PROXY` / `NO_PROXY`，Windows 上还会读系统代理设置——和 Claude Code 走同一条路。

> 如果你的网络只能通过代理访问 Anthropic，直连会收到 `403 "Request not allowed"`。这个错误**看起来像鉴权失败，其实是网络不通**。代理配置在启动时读取，改了要重启。

## 数据存放

| 位置 | 内容 |
|---|---|
| `~/.claude-swap-backup/credentials/` | 各槽位凭据（加密存储） |
| `~/.claude-swap-backup/configs/` | 各槽位 `.claude.json` 快照 |
| `~/.claude-swap-backup/sequence.json` | 槽位顺序与当前账号 |
| `~/.claude-swap-backup/sessions/` | 各账号的会话配置（含它们自己的对话历史） |
| `~/.claude-swap-backup/mappings.json` | 目录 → 账号绑定（本机专有） |
| `~/.claude-swap-backup/cache/` | 用量总览缓存（可随时删除） |
| `%LOCALAPPDATA%\ClaudeSwitch\ui-prefs.ini` | 界面偏好（主题、语言、隐藏邮箱等） |
| `%TEMP%\.net\ClaudeSwitch\` | 单文件包自解压的运行时 |

格式与 [claude-swap](https://github.com/realiti4/claude-swap)（Python CLI）兼容，两者可以共用同一份备份。
会话配置的布局也和它的 `cswap run` 一致，只有内部标记文件名不同（`.cswitch-*` 对 `.cswap-*`）——
两边都能读同一批 profile，但各自管各自的标记。

## 从源码构建

需要 Rust 1.80+ 和 .NET 8 SDK。

```bash
cargo build -p claude-switch-ffi --release
dotnet publish gui-win/ClaudeSwitch.App/ClaudeSwitch.App.csproj \
  -c Release -p:PublishSingleFileBundle=true -o dist
```

产物是 `dist/ClaudeSwitch.exe` 一个文件。两步顺序不能反——原生引擎必须先构建，否则 publish 会直接报错拒绝，而不是打出一个启动即崩的包。

要生成和 Release 一样的下载物（exe + zip + 校验和）：

```powershell
./packaging/pack.ps1 -PublishDir dist -OutDir artifacts
```

开发时：

```bash
cargo test --workspace
dotnet test gui-win/ClaudeSwitch.App.Tests/ClaudeSwitch.App.Tests.csproj
dotnet run --project gui-win/ClaudeSwitch.App -c Release -- --fixture %TEMP%\cswitch-demo
```

`--fixture` 用一份隔离的演示数据启动（六个账号，覆盖各种套餐），不碰你真实的 Claude 登录。

## 项目结构

```
crates/core     claude-switch-core   锁、凭据、切换、用量、自动切换、会话模式、Engine
crates/ffi      claude_switch.dll    C ABI（cs_engine_*）
gui-win/        Windows 托盘 GUI（WinForms）+ FfiSmoke + P/Invoke
gui-win/ClaudeSwitch.App/Strings/    界面词条（每种语言一份 JSON，嵌入资源）
```

- [docs/design-claude-switch.md](docs/design-claude-switch.md) — 架构、FFI、界面设计
- 行为规范来自上游 Python CLI [claude-swap](https://github.com/realiti4/claude-swap)——凭据处理、三锁切换事务、自动切换、轮询策略都以它为准。
  开发时可把它 clone 到 `reference/claude-swap/`（该目录不纳入版本控制）。

版本号以根目录 `VERSION` 为准，必须与 `Cargo.toml` 的 `[workspace.package].version` 一致。

## 平台

目前只有 Windows。核心是跨平台的 Rust，macOS / Linux 的原生外壳在设计文档里但还没做。

## License

MIT，见 [LICENSE](LICENSE)。备份格式与 claude-swap 保持互通。
