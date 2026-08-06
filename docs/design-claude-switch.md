# claude-switch — Claude Code 多账号切换的原生 GUI 重塑 · 设计文档

- 版本：v1.2（设计评审修订稿 · 一致性修补）
- 日期：2026-07-31
- 行为规格来源：`D:\dev\claude_switch\reference\claude-swap`（Python 3.12+，MIT，`cswap`）。本文中所有关于 claude-swap 现有行为的描述均经源码核实，引用格式为 `(ref: <文件>)`。
- 决策编号：文内 **KD-N** 均指 §15 Key Decisions 稳定编号；禁止再用“决策 N”裸数字交叉引用。
- **公开 wire 字母表**：枚举/策略字符串一律 **kebab-case**（见 §8.0）；与 cswap JSON、settings、serde 对齐。

---

## 1. 概述与目标

claude-switch 将 claude-swap（CLI 工具 `cswap`）的核心机制以 Rust 重写为一个厚核心库，并在其上构建三平台原生 GUI：

| 平台 | GUI 技术 | FFI 方式 |
|---|---|---|
| macOS | SwiftUI + `MenuBarExtra`（菜单栏常驻） | UniFFI 生成的 Swift 绑定 |
| Windows | WinUI 3 (C#) + 系统托盘，Fluent/Mica | C ABI + P/Invoke |
| Linux | GTK4 + libadwaita（relm4，Rust） | 同 crate 直接调用 |

核心目标：

1. **GUI 做薄、核心做厚**：三端 GUI 只渲染与转发；账号管理、切换事务、用量轮询、自动切换全部在 Rust 核心。
2. **自动切换引擎内嵌于托盘常驻 GUI 进程**，不做独立守护进程；用户退出 App 时提示自动切换将停止。
3. **与 claude-swap 数据兼容**：现有 cswap 用户可直接迁移（详见 §4.5 与 §10）。
4. **用户最佳体验**：首次启动向导、仪表盘即主界面、一键切换、系统通知、自动更新、跟随系统深浅色。

非目标（v1 不做）：

- `cswap run` 会话模式（session.py 的并行 profile）、TUI、目录映射（mappings.py）、CLI 全量命令面。核心库保留扩展点，v2 再加。
- **多机凭据同步**：明确拒绝。文档与向导**禁止**引导用户把 `backup_root` 复制到另一台机器作为支持路径（token 复活 / refresh lineage 风险与 cswap 立场一致）。跨机迁移仅支持官方 `.cswap` 导出信封（`transfer`），且目标机须重新完成 Keychain/ACL 授权。
- **账号 reorder 的 GUI 手势 / `move`/`swap` 可视化编排**：v1 CLI 级 move/swap 不暴露在 GUI；sequence 顺序由添加顺序与 `sequence.json` 字段维持。需要 slot 重编号时用户可改用 cswap CLI 或等 v1.1。
- **Job 取消 API**（`cancel_job`）：v1 不提供；见 §8.7。

---

## 2. 总体架构

### 2.1 架构图

```
┌──────────────────────────┐  ┌──────────────────────────┐  ┌──────────────────────────┐
│  macOS App (Swift)       │  │  Windows App (WinUI3/C#) │  │  Linux App (relm4/Rust)  │
│  SwiftUI + MenuBarExtra  │  │  系统托盘 + Fluent/Mica  │  │  GTK4 + libadwaita       │
│                          │  │                          │  │                          │
│  UniFFI 生成的绑定类      │  │  P/Invoke 声明 + 封送     │  │  直接 fn 调用             │
└────────────┬─────────────┘  └────────────┬─────────────┘  └────────────┬─────────────┘
             │ UniFFI (Swift)              │ C ABI (cdylib)              │ crate dep
             ▼                             ▼                             │
┌───────────────────────────────────────────────────────────────────────┴─────────────┐
│  claude-switch-ffi  (crate: cdylib + staticlib)                                      │
│  · UniFFI 接口（UDL 或 proc-macro）· C ABI 导出 · 命令分发 · 事件回调桥                 │
├──────────────────────────────────────────────────────────────────────────────────────┤
│  claude-switch-core  (纯 Rust lib，FFI 无关，可单测)                                   │
│                                                                                      │
│  ┌─────────────┐  ┌──────────────┐  ┌───────────────┐  ┌──────────────────────────┐  │
│  │ credentials │  │  switcher    │  │ usage + oauth │  │ autoswitch (引擎/状态机)  │  │
│  │ 存储路由层   │  │  切换事务    │  │  轮询与刷新   │  │ 阈值/冷却/迟滞/quarantine│  │
│  └──────┬──────┘  └──────┬───────┘  └──────┬────────┘  └───────────┬──────────────┘  │
│         │                │                 │                       │                  │
│  ┌──────┴──────────────┬─┴──────────┬──────┴──────────┬────────────┴───────────────┐  │
│  │ locks (FileLock +   │ paths      │ settings        │ usage_store + poll_policy  │  │
│  │  proper-lockfile)   │ (三平台)    │ (settings.json) │ (缓存/退避/自适应节奏)      │  │
│  └─────────────────────┴────────────┴─────────────────┴────────────────────────────┘  │
│                                                                                      │
│  tokio runtime（引擎宿主：定时 tick、HTTP 轮询、事件流、串行 mutation 队列）            │
└──────────────────────────────────────────────────────────────────────────────────────┘
             │ 文件系统                         │ macOS Keychain            │ HTTPS
             ▼                                 ▼                           ▼
   ~/.claude/.credentials.json        security-framework crate    api.anthropic.com
   ~/.claude.json                     · "Claude Code-credentials"  · /api/oauth/usage
   <backup_root>/ (sequence.json,     · "Claude Code"              · /api/oauth/profile
     settings.json, credentials/,     · "claude-swap" (备份)       platform.claude.com
     configs/, cache/, *.lock)                                    · /v1/oauth/token
```

### 2.2 进程/线程模型

**进程模型**：单进程。每个平台 GUI 是一个常驻进程（菜单栏/托盘），Rust 核心静态链接进该进程（macOS/Windows 经 cdylib，Linux 同 crate）。没有独立守护进程（**KD-4**）。

**单实例与双引擎策略**（**KD-12**，生产安全硬约束）：

1. **同产品单实例**：GUI 启动时获取**进程级命名互斥锁**；第二实例不启动引擎，而是尝试激活已运行实例的主窗口后退出（或打印明确错误后退出）。
   - Windows：`CreateMutexW(NULL, TRUE, L"Local\\ClaudeSwitch.SingleInstance.v1")`；已存在 → 广播自定义消息 `WM_CLAUDE_SWITCH_ACTIVATE` 后 exit 0。
   - macOS：`NSRunningApplication` 同 bundle id 检测 + `activate`；或文件锁 `~/Library/Application Support/claude-switch/instance.lock`（pid + start_time，stale 检测：pid 不存在或 start_time 不匹配则接管）。
   - Linux：`flock` on `$XDG_RUNTIME_DIR/claude-switch.instance.lock`（fallback `/tmp/claude-switch-$UID.instance.lock`），内含 pid；stale 规则同 macOS。
2. **跨产品双 autoswitch（本 App + `cswap auto` / `cswap --menubar`）**：文件锁 `.lock` **只串行切换事务，不串行 tick 循环**。采用 **autoswitch 领导租约**（leadership lease）写入 `autoswitch_state.json`：
   ```json
   "leadership": {
     "ownerApp": "claude-switch" | "cswap" | "unknown",
     "ownerPid": 12345,
     "heartbeatUntil": "2026-07-31T08:00:30Z"
   }
   ```
   - 租约 TTL = 30s；持有者每 tick / 每 ≤15s 续约（在 `.autoswitch_state.lock` 内读改写）。
   - 仅 `ownerApp+ownerPid` 匹配且 `now < heartbeatUntil` 的进程允许跑 AutoswitchEngine 决策与切换；其它本进程引擎进入 **observe-only**（仍可 poll usage 写入 cache、仍可响应用户手动 `switch_account`，但**不**自动切换、不写 cooldown）。
   - 抢领导（自然）：租约过期或 pid 已死 → 尝试认领；冲突时退避 1–3s 抖动后重读。
   - **与 cswap 互操作说明**：cswap 今日**不写** `leadership` 字段。claude-switch 读到无 `leadership` 时：
     - 若检测到其它进程持有 `<backup_root>/.lock` 且持续 >5s（启发式“可能有 cswap auto”），本引擎 observe-only，并向 GUI 推 `ConfigWarning` + 仪表盘文案：“检测到其它工具可能在自动切换；请关闭 `cswap auto` / menubar 后再启用本应用自动切换”。
   - **强制抢领导（API）**：设置页在确认对话框后调用 `claim_leadership(ClaimLeadershipOptions { force: true })`（§8.2）。`force=false`（默认）尊重未过期的外进程 lease 与上述 cswap 启发式；`force=true` 在 `.autoswitch_state.lock` 内**立即**写入本进程 lease（覆盖 live foreign/`unknown`），发出 `ConfigWarning`（文案含双写风险），并将 `is_leader=true`。`autoswitch_start` 默认 `force=false` 的 claim；若用户已在 observe-only 且点“强制抢领导”，只调 `claim_leadership`，不必 stop/start。
   - 向导与设置页固定文案（中英）：“请勿同时运行 cswap 的 auto/menubar 与本应用的自动切换。”
3. **observe-only 仍可**：手动切换、添加/删除账号、刷新用量、读 Snapshot。

**线程模型**（核心内部）：

```
GUI 主线程 (Swift/C#/GTK)                Rust 核心
        │                                 │
        │  FFI 调用（同步签名）            │
        ├────────────────────────────────►│ 命令分发层（claude-switch-ffi）
        │                                 │   ├─ 短查询（snapshot/list/status）：
        │                                 │   │  在当前调用线程执行；持
        │                                 │   │  EngineReadGuard（见下），
        │                                 │   │  同步返回（macOS：Swift 侧
        │                                 │   │  Task.detached 包装，勿堵 UI）
        │                                 │   └─ 长操作（switch/add/import…）：
        │                                 │      投入串行 mutation 队列，立即
        │                                 │      返回 jobId，结果经事件回推
        │                                 │
        │  事件回调（注册的 listener）     │ tokio runtime（worker 2–4；
        │◄────────────────────────────────┤   blocking 线程池 min 8）
        │  （runtime 线程发起；宿主必须   │   ├─ AutoswitchEngine task
        │    自行 marshal 到 UI 主线程）  │   ├─ PollScheduler / usage jobs
        │                                 │   ├─ MutationExecutor（串行）
        │                                 │   ├─ HTTP（reqwest + rustls）
        │                                 │   └─ 事件总线（§8.4）
        │                                 │
        │                                 │ FileLock touch：持锁期间在
        │                                 │ spawn_blocking 线程内每 3s touch
        │                                 │ （ref: claude_locks.py TOUCH_INTERVAL_S）
```

**内存锁 vs 文件锁层次**（**必须遵守，防死锁**）：

| 层级 | 锁 | 规则 |
|---|---|---|
| L0 | 无 | 纯计算、serde、路径解析 |
| L1 | `std::sync::RwLock` 配置/状态内存快照 | 不 await；持有时间微秒级 |
| L2 | `tokio::sync::Mutex<EngineInner>`（简称 `eng`） | 可跨 await；**禁止**在持有 `eng` 时调用 `spawn_blocking` 内再试图获取 `eng` |
| L3 | 文件锁：`FileLock(.lock)` → `claude_credentials_lock` → `claude_config_lock` | **仅**在 `spawn_blocking` 内获取；顺序固定（§5.1）；**非重入**（同进程二次 `FileLock` 死锁——与 Python `FileLock` 一致） |
| L4 | `.autoswitch_state.lock` / usage claim 文件协议 | 短临界区；**无锁抓取**（ref: usage_store claim） |

**Mutation 串行队列**：`switch_account` / `add_account` / `remove_account` / `set_alias` / `set_disabled` / `import_accounts` / `export_accounts` 全部进入单一 `MutationExecutor`（`tokio::sync::Mutex` 保护的队列）。同时只跑一个 mutation；第二个请求返回新 `jobId` 并排队（`JobState::Queued`），不拒绝。`refresh_usage` **不**走 mutation 队列，可与其它 usage collect 并行（靠 claim 租约）。

**Snapshot 隔离保证**：

- `snapshot()` 可在 mutation 进行中调用。
- 保证：**从不返回撕破的 JSON**（每个磁盘文件的读要么完整要么失败回落上次内存缓存）；可能**略旧**（例如 sequence 已提交、凭据写尚未完成的中间窗口——窗口仅存在于单次 spawn_blocking 事务内，通常 <1s）。
- 实现：`snapshot()` 取 `eng` 的读路径，聚合内存中的 `SequenceData` + usage store 快照 + `EngineStatus`；磁盘文件仅在内存未加载或 `SnapshotUpdated` 后惰性刷新。集成测试：慢 switch（注入 sleep）期间连续 `snapshot()` 不得 panic / 不得半写入字段。

**Blocking 线程池**：tokio 默认 blocking 池在“持锁 + touch 循环占线程”场景可能饿死。Engine 初始化时设置：

```rust
// worker 数：available_parallelism 钳制到 [2, 4]（Builder::worker_threads 只接受单个 usize）
let workers = std::thread::available_parallelism()
    .map(|n| n.get())
    .unwrap_or(4)
    .clamp(2, 4);

tokio::runtime::Builder::new_multi_thread()
    .worker_threads(workers)
    .max_blocking_threads(16)
    .thread_keep_alive(Duration::from_secs(60))
    // ...
    .build()
```

文档约定：每个持有 proper-lockfile 的临界区占用 **1** 个 blocking 线程直至释放（touch 循环与临界区同线程，避免再占一个）。禁止在持锁线程上做 HTTP。

**FFI 边界线程安全**：

- `Engine` 句柄 = `Arc<Engine>`，`Send + Sync`。
- UniFFI 对象满足 `Send + Sync`；C ABI 为不透明指针 + 内部同步。
- **回调不可重入（默认）**：`EventListener.on_event` / `CsEventCallback` 执行期间，**禁止**调用任何 mutating 的 Engine 方法（`switch_*`、`add_*`、`remove_*`、`import_*`、`export_*`、`autoswitch_start/stop`、`set_setting`、`shutdown`、`set_event_listener`）。允许的重入只读集合：`snapshot()`、`account_by_identifier`、`get_autoswitch_config`、`is_claude_code_running`、`schema_version`。违反时 Rust 侧检测同线程 re-entry flag → 返回 `SwitchError::Internal` / `CsError code=internal`，不死锁。
- **Panic 策略**：Rust panic 在 FFI 边界 `catch_unwind` → 映射为 `Error` 事件 + 调用结果错误；**永不** unwind 进 Swift/C#。二次 panic abort。

**退出语义（全平台唯一顺序，见 §8.3.3 / §8.8）**：

1. `set_event_listener(None)` / `cs_engine_set_event_callback(NULL, …)` — 退订，保证此后无回调  
2. `shutdown()` / `cs_engine_shutdown` — 取消 Autoswitch task；**等待 in-flight mutation 完成或标记 Failed**（`retryable=false`，`error_code=engine-shutdown`）；释放领导租约；停 runtime tasks  
3. `cs_engine_free` / drop `Engine`

macOS/Windows 退出确认对话框提示“自动切换将停止”。契约测试：步骤 1 之后不得再投递任何事件回调。

---

## 3. Rust 核心 crate 结构与模块划分

### 3.1 Workspace 布局

```
claude-switch/
├── Cargo.toml                    # workspace 根；version 单一事实源
├── VERSION                       # 纯文本 semver（CI / Sparkle / MSIX / UniFFI 元数据共读）
├── Cargo.toml members:
│     crates/core, crates/ffi, crates/gui-linux
├── crates/
│   ├── core/                     # claude-switch-core：全部业务逻辑，FFI 无关
│   │   ├── Cargo.toml
│   │   └── src/
│   │       ├── lib.rs            # Engine 门面（对外唯一入口类型）
│   │       ├── paths.rs          # 路径解析（对齐 ref: paths.py）
│   │       ├── models.rs         # Account/Usage/Platform 等数据类型
│   │       ├── errors.rs         # 结构化错误（见 §8.5）
│   │       ├── fsutil.rs         # 原子写入、replace_with_retry、0600/0700
│   │       ├── locks/
│   │       │   ├── mod.rs
│   │       │   ├── file_lock.rs      # 自有 FileLock（fcntl/Windows LockFileEx）
│   │       │   └── claude_locks.rs   # proper-lockfile 兼容目录锁
│   │       ├── keychain/
│   │       │   ├── mod.rs            # SecretStore trait + 路由
│   │       │   ├── macos.rs          # security-framework 实现（#[cfg(target_os)]）
│   │       │   └── file.rs           # .enc 文件后端（所有平台可用）
│   │       ├── credentials.rs    # 凭据存储层（对齐 ref: credentials.py）
│   │       ├── switcher.rs       # 账号编排 + 切换事务（对齐 ref: switcher.py）
│   │       ├── oauth.rs          # 用量 API / profile / token 刷新（ref: oauth.py）
│   │       ├── poll_policy.rs    # 轮询节奏常数与 plan_after_fetch（ref: poll_policy.py）
│   │       ├── usage_store.rs    # per-account 用量表 + claim/backoff（ref: usage_store.py）
│   │       ├── autoswitch/
│   │       │   ├── mod.rs            # AutoSwitchEngine + tick + leadership
│   │       │   ├── events.rs         # CoreEvent 定义
│   │       │   └── state.rs          # autoswitch_state.json 读写
│   │       ├── settings.rs       # settings.json（ref: settings.py）
│   │       ├── sequence.rs       # sequence.json 读写与校验
│   │       ├── transfer.rs       # 导入/导出（ref: transfer.py，FORMAT_VERSION 兼容）
│   │       ├── instance.rs       # 单实例锁（平台条件编译）
│   │       └── pace.rs           # 周窗口 pace 计算（ref: pace.py，UI 展示用）
│   ├── ffi/                      # claude-switch-ffi：cdylib + staticlib
│   │   ├── Cargo.toml            # crate-type = ["cdylib", "staticlib"]
│   │   ├── build.rs              # uniffi 脚手架生成
│   │   ├── claude_switch.udl     # UniFFI 接口定义（见 §8.2）
│   │   ├── include/claude_switch.h  # C ABI 头（cbindgen 生成，见 §8.3）
│   │   └── src/
│   │       ├── lib.rs            # uniffi setup + 命令分发
│   │       └── c_abi.rs          # #[no_mangle] extern "C" 导出
│   └── gui-linux/                # claude-switch-gui-linux（relm4）— 唯一在 Cargo workspace 的 GUI
│
├── gui-mac/                      # 非 Cargo member：Xcode 工程 + SwiftPM 本地 package
│   ├── ClaudeSwitch.xcodeproj
│   ├── ClaudeSwitch/             # SwiftUI App 源码
│   ├── ClaudeSwitchCore/         # Swift package：消费 UniFFI 生成绑定 + .xcframework
│   └── project.yml               # 可选 xcodegen
│
├── gui-win/                      # 非 Cargo member：.NET 解决方案
│   ├── ClaudeSwitch.sln
│   ├── ClaudeSwitch.App/         # WinUI 3 应用（csproj, TargetFramework net8.0-windows10.0.19041）
│   ├── ClaudeSwitch.Core/        # P/Invoke + JSON 封送 + xUnit 契约测试
│   └── ClaudeSwitch.Package/     # Windows Application Packaging Project (MSIX)
│
└── tests/                        # 跨 crate / 跨实现契约测试（见 §13）
```

**版本钉扎**：

| 产物 | 版本来源 |
|---|---|
| `crates/core` / `crates/ffi` Cargo package version | 根 `Cargo.toml` workspace.package.version ← `VERSION` |
| UniFFI 生成的 Swift module 元数据 | 同 semver；CI 把 `libclaude_switch.a`/xcframework 与 `VERSION` 一并拷入 `gui-mac` |
| `gui-win` 程序集 `AssemblyVersion` / MSIX `Identity Version` | CI 从 `VERSION` 写入 Directory.Build.props |
| `gui-linux` crate version | workspace 继承 |

Monorepo **同 tag 同版本**发布（**KD-10**）。Swift 不通过 CocoaPods 拉远程 dylib——始终使用本仓 CI 产物。

### 3.2 关键类型骨架

```rust
// crates/core/src/models.rs
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum Platform { MacOS, Linux, Wsl, Windows, Unknown }

impl Platform {
    /// 对齐 ref: models.py Platform.detect()
    /// 使用 std::env::consts::OS（等同 sys.platform 语义），避免 Windows 上 WMI hang。
    pub fn detect() -> Self {
        match std::env::consts::OS {
            "macos" => Self::MacOS,
            "windows" => Self::Windows,
            "linux" => {
                if std::env::var_os("WSL_DISTRO_NAME").is_some() {
                    Self::Wsl
                } else {
                    Self::Linux
                }
            }
            _ => Self::Unknown,
        }
    }
}
```

**Platform 检测表**（与 ref 一致）：

| 条件 | Platform | backup_root 规则 |
|---|---|---|
| `OS == macos` | `MacOS` | `~/.claude-swap-backup` |
| `OS == windows` | `Windows` | `~/.claude-swap-backup` |
| `OS == linux` 且 `WSL_DISTRO_NAME` 已设 | `Wsl` | XDG（`$XDG_DATA_HOME/claude-swap` 或 `~/.local/share/claude-swap`） |
| `OS == linux` 其它 | `Linux` | 同上 XDG |
| 其它 | `Unknown` | 回落 legacy `~/.claude-swap-backup` |

```rust
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountRecord {           // sequence.json 中 accounts 的一项
    pub email: String,
    #[serde(default)] pub uuid: String,
    #[serde(default, rename = "organizationUuid")] pub org_uuid: String,
    #[serde(default, rename = "organizationName")] pub org_name: String,
    #[serde(default)] pub added: String,
    #[serde(default, skip_serializing_if = "Option::is_none")] pub alias: Option<String>,
    #[serde(default)] pub disabled: bool,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SequenceData {            // sequence.json 全量（ref: switcher.py _get_sequence_data）
    pub schema_version: u32,
    pub accounts: BTreeMap<String, AccountRecord>,  // slot 号字符串 → 记录
    #[serde(default)] pub sequence: Vec<u32>,        // 保序的 slot 列表
    #[serde(default)] pub active_account_number: Option<u32>,
    #[serde(default)] pub last_updated: String,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AccountKind { OAuth, ApiKey }  // 由 looks_like_api_key 判定（见 §4.2）

/// 用量窗口（ref: oauth.py build_usage_result）
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct UsageWindow { pub pct: f64, pub resets_at: Option<String> }

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct Usage {
    pub five_hour: Option<UsageWindow>,
    pub seven_day: Option<UsageWindow>,
    pub spend: Option<SpendWindow>,              // extra_usage（pay-as-you-go）
    pub scoped: Vec<ScopedWindow>,               // 每模型周限制（display_name）
}

/// 哨兵状态：用量不可得时的原因分类（ref: json_output.py USAGE_*）
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum UsageSentinel {
    NoCredentials, TokenExpired, ApiKey, KeychainUnavailable,
    ReloginRequired, ForeignCredential,
}
```

```rust
// crates/core/src/keychain/mod.rs
/// 秘密存储后端抽象。macOS 上为 Keychain，其他平台/降级路径为 .enc 文件。
pub trait SecretStore: Send + Sync {
    fn get(&self, service: &str, account: &str) -> Result<Option<String>, SecretError>;
    fn set(&self, service: &str, account: &str, secret: &str) -> Result<(), SecretError>;
    fn delete(&self, service: &str, account: &str) -> Result<(), SecretError>;
}

// 服务名常数必须与 claude-swap / Claude Code 完全一致（ref: credentials.py L44-57）
pub const SECURITY_SERVICE: &str = "claude-swap";                  // 每账号备份
pub const CLAUDE_CODE_KEYCHAIN_SERVICE: &str = "Claude Code-credentials"; // 活动 OAuth
pub const CLAUDE_CODE_MANAGED_KEYCHAIN_SERVICE: &str = "Claude Code";     // 托管 API key
```

```rust
// crates/core/src/credentials.rs
/// 凭据存储层：Keychain-vs-文件路由 + 每进程能力缓存 + .enc-wins 对账。
/// 对齐 ref: credentials.py CredentialStore。
pub struct CredentialStore {
    backend: Box<dyn SecretStore>,           // 当前路由到的后端
    keychain: Option<Arc<dyn SecretStore>>,  // macOS 专用：能力探测用
    file_backend: FileSecretStore,           // .enc 文件后端（.enc-wins 读路径需要）
    keychain_usable: AtomicU8,               // Unknown/Usable/Failed（sticky）
    keychain_disabled_until: Mutex<Option<Instant>>, // 60s 冷却再探测
    credentials_dir: PathBuf,
    platform: Platform,
}

/// 机器共享字段白名单：激活时以 live 凭据为准（ref: credentials.py SHARED_CREDENTIAL_KEYS）
pub const SHARED_CREDENTIAL_KEYS: [&str; 5] =
    ["mcpOAuth", "mcpOAuthClientConfig", "mcpXaaIdp", "mcpXaaIdpConfig", "pluginSecrets"];
pub const ACCOUNT_CREDENTIAL_KEYS: [&str; 2] = ["claudeAiOauth", "trustedDeviceToken"];

pub fn looks_like_api_key(creds: &str) -> bool;          // "sk-ant-api" 前缀且非 JSON
pub fn approved_form(api_key: &str) -> &str;             // 末 20 字符
pub fn shared_credential_fields(creds: &str) -> Option<Map<String, Value>>;
pub fn merge_shared_credential_fields(target: &str, shared: &Map<String, Value>) -> String;
```

```rust
// crates/core/src/switcher.rs
pub struct Switcher {
    paths: Paths,
    store: CredentialStore,
    sequence: SequenceStore,
    // probe verdicts / provenance 缓存（ref: switcher.py _probe_verdicts）
}

/// 切换事务快照，失败时逆序回滚。
/// **Inspired by, not isomorphic to** ref: models.py SwitchTransaction。
///
/// 有意差异（等价测试断言磁盘结果与用户可见结果，不断言字段同构）：
/// | 本设计 | ref SwitchTransaction | 原因 |
/// |---|---|---|
/// | `original_credentials: Option<String>` | `original_credentials: str` | 区分“缺失”(None)与“读失败”(Err 中止)；ref 用空串+旁路标志 |
/// | `original_config_text: Option<String>` | `original_config: str` | 同上 |
/// | `original_active: Option<u32>` | `original_account_num: str` + `original_email` | slot 用数值；email 可从 sequence 重建，不重复存 |
/// | `completed: Vec<SwitchStep>` 类型枚举 | 字符串 step 名列表 | 编译期穷尽回滚分支 |
/// | 无 `config_path` 字段 | 有 `config_path` | 路径由 `Paths` 在回滚时解析，避免陈旧路径 |
pub struct SwitchTransaction {
    original_credentials: Option<String>,   // 读失败（而非缺失）时直接中止事务
    original_config_text: Option<String>,
    original_active: Option<u32>,
    completed: Vec<SwitchStep>,             // CredentialsWritten / ConfigWritten / SequenceUpdated
}

#[derive(Clone, Debug)]
pub enum SwitchStep {
    CredentialsWritten,
    ConfigWritten,
    SequenceUpdated,
}
```

```rust
// crates/core/src/autoswitch/events.rs — 事件流
// 事件负载策略（UniFFI + C ABI 统一）：
//   - 复杂/嵌套 map 一律展平为 sequence<Entry> 或使用共享 serde 类型后
//     在 C ABI 路径 JSON 序列化；UniFFI 路径使用同一字段布局的 dictionary。
//   - 禁止在 UDL 中使用 Map<String, Option<T>> / 嵌套 Map。

#[derive(Clone, Debug, Serialize)]
#[serde(tag = "event", rename_all = "kebab-case")]
pub enum CoreEvent {
    Poll {
        active: Option<AccountRef>,
        headroom: Vec<HeadroomEntry>,
        threshold: f64,
        fetch_errors: Vec<FetchErrorEntry>,
        windows: Vec<WindowPctEntry>,
    },
    Switch {
        trigger: SwitchTrigger,
        from: Option<AccountRef>,
        to: Option<AccountRef>,
        warnings: Vec<String>,
        dry_run: bool,
    },
    NoSwitch { reason: NoSwitchReason, detail: String },
    /// slot 在磁盘 sequence 中为字符串键；FFI/事件层统一用 u32（与 AccountRef.number 一致）
    AccountQuarantined { number: u32, email: String, reason: String },
    AccountUnquarantined { number: u32, email: String, reason: String },
    AllExhausted { earliest_reset_at: Option<String> },
    Sleep { seconds: f64, until: String },
    Error { message: String, transient: bool },
    ConfigWarning { message: String },
    // GUI 增量：
    SnapshotUpdated,
    JobFinished { job_id: u64, result: JobResult },
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountRef {
    pub number: u32,
    pub email: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub alias: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HeadroomEntry {
    pub number: u32,
    /// None = 未知 headroom（哨兵/抓取失败）
    pub pct: Option<f64>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FetchErrorEntry {
    pub number: u32,
    pub message: String, // 已脱敏
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WindowPctEntry {
    pub number: u32,
    pub window: String,  // "five_hour" | "seven_day" | scoped display_name
    pub pct: f64,
}

/// 公开 wire 字符串 = kebab-case（§8.0）。Rust 变体名 PascalCase；serde/JSON/C ABI/settings 写出 kebab。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum SwitchTrigger { Proactive, AtLimit, Failover, ConsumeFirst, Manual }
// wire: "proactive" | "at-limit" | "failover" | "consume-first" | "manual"

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum NoSwitchReason {
    BelowThreshold,
    Cooldown,
    ActiveApiKey,
    UnmanagedActive,
    NoActive,
    Unhealthy,
    NoCandidate,
    ObserveOnly,       // 非领导实例 / 双引擎降级
    EnginePaused,
    Other,
}
// wire: "below-threshold" | "cooldown" | "active-api-key" | "unmanaged-active"
//       | "no-active" | "unhealthy" | "no-candidate" | "observe-only"
//       | "engine-paused" | "other"

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum SwitchStrategy { Best, ConsumeFirst, NextAvailable }
// wire: "best" | "consume-first" | "next-available"

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct JobResult {
    pub ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error_code: Option<String>,    // kebab-case，§8.5
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,       // 已脱敏
    pub retryable: bool,
    /// 成功时可选的人类可读摘要（如 "switched 1 → 2"）
    #[serde(skip_serializing_if = "Option::is_none")]
    pub summary: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EngineStatus {
    /// autoswitch 任务是否在跑（用户 start 且未 stop）
    pub running: bool,
    /// 是否持有领导租约（false = observe-only）
    pub is_leader: bool,
    pub enabled: bool,                 // settings/gui 持久化的“希望启用”
    #[serde(skip_serializing_if = "Option::is_none")]
    pub next_tick_at: Option<String>,  // ISO-8601
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cooldown_remaining_seconds: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pause_reason: Option<String>,  // "observe-only" | "cooldown" | "all-exhausted" | …
    pub pending_mutations: u32,
}
```

---

## 4. 凭据存储设计

### 4.1 路径解析（三平台）

严格镜像 claude-swap 的路径规则（ref: paths.py）：

| 项 | 规则 | 默认示例 |
|---|---|---|
| Claude config home | `$CLAUDE_CONFIG_DIR` 若设且非空，否则 `~/.claude` | `~/.claude`；自定义时为 `/custom/claude` |
| 全局配置 | 若 `<config_home>/.config.json` **存在**则用它（legacy）；否则 `($CLAUDE_CONFIG_DIR \|\| $HOME)/.claude.json` | 默认 `~/.claude.json`；`CLAUDE_CONFIG_DIR=/c` → `/c/.claude.json`；legacy 存在时为 `<config_home>/.config.json` |
| 活动凭据文件 | `<config_home>/.credentials.json` | `~/.claude/.credentials.json` |
| 备份根 `backup_root` | Linux/WSL：`$XDG_DATA_HOME/claude-swap`（要求绝对路径，`~` 展开；未设则 `~/.local/share/claude-swap`）；macOS/Windows/Unknown：`~/.claude-swap-backup` | 见 Platform 表 |

**`init_engine` / `Paths` 构造时必须运行 legacy 备份迁移**（ref: `paths.migrate_legacy_backup_dir`）：

- 当解析后的 `backup_root` ≠ `get_legacy_backup_root()`（`~/.claude-swap-backup`）时（典型：Linux/WSL XDG 路径），若 legacy 目录存在则迁移到 target。
- 崩溃安全：先 touch `<target.parent>/.{target.name}.migrating` flag，再 `rename/move`，成功后删 flag。
  - flag 在且 legacy 仍在 → 丢弃可能残缺的 target，重试。
  - flag 在且 legacy 已无 → 仅清 flag。
  - 无 flag 且两侧都有有意义数据 → `MigrationError`（拒绝静默合并）。
  - target 仅有 throwaway（`cache/`、`claude-swap.log*`）→ 擦除后迁移。
- 单元/集成测试覆盖：默认 home、`CLAUDE_CONFIG_DIR`、legacy `.config.json`、Linux legacy→XDG 迁移、碰撞拒绝（PR 2 + PR 12）。

备份根内布局（与 claude-swap 完全一致，这是兼容决策的基础）：

```
<backup_root>/
├── sequence.json            # 账号表（§10.1）
├── settings.json            # 用户配置（§10.2）
├── autoswitch_state.json    # 冷却/quarantine/leadership 持久态（§10.3）
├── .lock                    # 自有 FileLock 的锁文件
├── .autoswitch_state.lock
├── credentials/             # .creds-{num}-{email}.enc（+ .prev 上一代）
├── configs/                 # .claude-config-{num}-{email}.json
├── cache/                   # usage.json（schemaVersion 2）等
└── stash/                   # 被置换的 live 凭据安全副本（ref: _stash_live_credential）
```

### 4.2 macOS Keychain 交互

**实现选型**：使用 `security-framework` crate（原生 Security framework 绑定），不再像 claude-swap 那样 shell 调 `security` CLI。

| 维度 | claude-swap（security CLI） | claude-switch（security-framework） |
|---|---|---|
| 秘密是否过 argv | 特意走 `security -i` stdin 避免（ref: macos_keychain.py L18-20） | 进程内调用，天然不过 argv |
| 延迟 | 每次 fork+exec，约 10-50ms | 亚毫秒 |
| 超时控制 | 需要自包超时 | 同步 API，无需 |
| Keychain 访问提示 | CLI 写入的 item 对签名 App 不可见/触发授权弹窗 | 见下方签名要求（§11.3） |

**Keychain item 约定**（必须与 Claude Code 及 claude-swap 一致，否则互相不可见）：

- 服务名：`Claude Code-credentials`（活动 OAuth）、`Claude Code`（托管 API key）、`claude-swap`（每账号备份）。
- 账号名：镜像 Claude Code 的 `getUsername()`——`$USER` → OS 用户名 → `"claude-code-user"`（ref: macos_keychain.py L76-94）。
- 备份 item 用户名：`account-{num}-{email}`（ref: credentials.py `_backup_username`）。

**路由与降级**（对齐 ref: credentials.py）：

- 每进程能力缓存：首次 Keychain 操作成功后置 usable；任何一次失败 → 切文件模式，并记 60s 冷却（`KEYCHAIN_RECHECK_COOLDOWN_S`），常驻进程冷却后重探测，单次调用内绝不重探测（防 split-brain）。
- **写降级即钉死**（`_pin_file_mode` 语义）：活动凭据写一旦降级到文件，本进程不再重探测——因为残留 Keychain item 可能删除失败，重探测会读到旧值。
- 活动 OAuth 读有 2 次有界重试（间隔 0.3s），以骑过锁定的 Keychain 的瞬时竞争。
- rc-44（errSecItemNotFound）映射为 `Ok(None)`，不算失败。

**读写规则的完整对齐**（ref: credentials.py `_write_oauth_credentials` / `_write_managed_credentials`）：

1. **OAuth 写入**：Keychain 可用 → 写 Keychain；**若 `.credentials.json` 已存在则用同内容原子重写它**（bump mtime 触发 Claude Code 热加载，#86），不存在则不创建。Keychain 不可用 → 写 `.credentials.json`（0600 原子写），best-effort 删除 Keychain 残留项（#30337），并钉死文件模式。
2. **API key 写入**：先把 `key[-20:]` 记入 `~/.claude.json` 的 `customApiKeyResponses.approved`（数组，不去重地 append-if-absent），Keychain 可用则写 Keychain 并从 config 删除 `primaryApiKey`，否则写 `primaryApiKey`。随后**清除 OAuth 轴**（互斥，镜像 Claude Code 的 saveApiKey/removeApiKey 语义）。
3. **OAuth 写入时也清除托管 key 轴**（Keychain "Claude Code" + `primaryApiKey`，但保留 `approved` 列表）。
4. 判定：`looks_like_api_key` —— trim 后以 `sk-ant-api` 开头且不以 `{` 开头。

**原子写入**：所有文件写走"同目录 mkstemp → write → fsync → rename → chmod 0600（非 Windows）"。`fsutil::replace_with_retry` 移植 Python 版在 Windows 上 rename 重试的语义（目标被占用时有限重试）。

**符号链接规则**：`settings.json`/`autoswitch_state.json` 等 JSON 写"穿透 symlink，绝不 rename 覆盖 symlink"（ref: settings.py `atomic_write_json` 的长 docstring），temp 文件创建在 resolve 后的目标旁。

### 4.3 每账号备份与 .enc-wins 对账

- `.enc` 文件 = **base64 编码的凭据明文字节**（不是加密，名字是历史遗留），0600。
- macOS 读路径 **.enc-wins**：`.enc` 存在且可解码非空 → 以它为准；缺失/损坏/空 → 回落 Keychain。
- Keychain 备份写成功后**必须**对账 `.enc`（删除；删不掉就用新值重写；再失败则报错）——这是正确性要求，不是 best-effort（ref: `_reconcile_enc_after_keychain_write`）。
- 覆盖前保留上一代 `.prev`（一个世代，best-effort），为误覆盖留恢复机会；swap/move 等 slot 重编号场景必须清除 `.prev`，防止旧世代凭据复活到新 owner。
- `delete_account_credentials_strict`：事务预提交清空——`exists()` 在不可访问目录上返回 false 属于 fail-open，因此改为无条件 `unlink(missing_ok)` + Keychain 删除错误传播 + 最终 read-back 校验，失败即中止提交。

### 4.4 机器共享字段合并

激活目标账号凭据时，`SHARED_CREDENTIAL_KEYS`（`mcpOAuth`、`mcpOAuthClientConfig`、`mcpXaaIdp`、`mcpXaaIdpConfig`、`pluginSecrets`）以 **live 凭据**为准（包括"live 缺失即删除 slot 快照里的陈旧副本"），其余字段（`claudeAiOauth`、`trustedDeviceToken`、以及一切未识别字段——fail-safe 视为账号私有）随 slot 走。live 不是 JSON 凭据对象时目标原样激活（ref: `_prepare_credentials_for_activation`）。未识别的 sibling key 打 debug 日志，便于发现新版 Claude Code 引入的共享字段。

### 4.5 兼容性决策：与 claude-swap 现有备份的关系

**决策：完全兼容——同路径、同格式、同 Keychain 服务名（KD-2）。** 分析：

1. **备份根**：三平台使用与 claude-swap 完全相同的 `backup_root` 解析规则（§4.1）。`sequence.json`、`settings.json`、`autoswitch_state.json`、`cache/usage.json`（schemaVersion 2）、`.enc` 文件命名与 base64 编码、Keychain 服务名 `claude-swap` 全部一致。Linux/WSL 上 `init` 执行 `migrate_legacy_backup_dir`（§4.1）。
2. **迁移体验**：已用 cswap 的用户安装 claude-switch 后**零迁移**——首次启动即看到全部账号、配置与 quarantine 状态。
3. **交替使用边界**：同一 `.lock` 保证**切换事务**互斥；**自动切换**靠领导租约 + 用户文案约束（§2.2 KD-12），**不是**“两个 auto 同时跑也安全”。
4. **代价**：我们被锁定在 claude-swap 的 schema 上。缓解：每个文件都有 `schemaVersion` 字段，破坏性演进时 bump 并在核心内做读侧迁移（读旧写新），与 claude-swap 的 `migrations.py` 模式相同。
5. **不兼容项的隔离**：claude-switch 专有的 GUI 配置（窗口位置、通知开关等）放独立文件 `<backup_root>/gui-settings.json`，不污染共享 schema。`leadership` 为 `autoswitch_state.json` 增量字段——cswap 读写保留未知键（ref: settings 同类行为），向前兼容。
6. **导入导出**：`transfer.rs` 支持读取/写出 claude-swap 的 `.cswap` 导出信封（`FORMAT_VERSION = 1`，ref: transfer.py）。v1 **交付** FFI + 账号管理页入口（§8.2、§9、PR 12b/14）。

---

## 5. 切换事务设计

### 5.1 锁的获取顺序

切换临界区持有**三把锁**，顺序固定（ref: switcher.py L5006；路径函数对齐 ref: claude_locks.py）：

```
1. FileLock(<backup_root>/.lock)
   — 自有锁（fcntl flock / Windows LockFileEx）
   — 与 cswap 及其它 claude-switch 实例的切换事务互斥
   — 非重入

2. claude_credentials_lock  — proper-lockfile 兼容目录锁，顺序：
   a. oauth_refresh_lock_dir()
        = get_claude_config_home() / ".oauth_refresh.lock"
        默认示例: ~/.claude/.oauth_refresh.lock
        CLAUDE_CONFIG_DIR=/custom/claude → /custom/claude/.oauth_refresh.lock
   b. credentials_lock_dir()   // legacy
        = get_claude_config_home().parent / (config_home.name + ".lock")
        默认示例: ~/.claude.lock
        CLAUDE_CONFIG_DIR=/custom/claude → /custom/claude.lock
        （注意：不是硬编码 ~/.claude.lock；是 config_home 的「父目录 + name.lock」）
   两者 staleness = 60s（绝不可偷取 live 持有者的锁）

3. claude_config_lock
   — config_lock_dir()
        = get_global_config_path().parent / (get_global_config_path().name + ".lock")
   默认示例:
        ~/.claude.json.lock
   CLAUDE_CONFIG_DIR=/custom/claude 且无 legacy:
        /custom/claude/.claude.json.lock   // 因 global config = /custom/claude/.claude.json
   legacy <config_home>/.config.json 存在时:
        <config_home>/.config.json.lock
   staleness = 10s
```

**单元测试矩阵（PR 3 / locks）必须覆盖**：

| 环境 | credentials legacy 锁 | oauth_refresh 锁 | config 锁 |
|---|---|---|---|
| 默认 home | `~/.claude.lock` | `~/.claude/.oauth_refresh.lock` | `~/.claude.json.lock` |
| `CLAUDE_CONFIG_DIR=/c/claude` | `/c/claude.lock` | `/c/claude/.oauth_refresh.lock` | `/c/claude/.claude.json.lock` |
| 同上 + `/c/claude/.config.json` 存在 | 同上 | 同上 | `/c/claude/.config.json.lock` |

proper-lockfile 协议实现（ref: claude_locks.py）：

- 锁载体是**目录**，`mkdir` 原子性即互斥原语。
- 获取：循环 `mkdir`；已存在则检查 mtime 是否超过 staleness（超时则 `rmdir` 抢锁重试）；每把锁独立 9s 获取预算，超出抛 `ClaudeCodeLockTimeout`（提示"Claude Code 正在刷新凭据，稍候重试"）。
- 持有期间：每 3s touch 目录 mtime（CC 是 5s，我们更快留裕量）——在 `spawn_blocking` **同一线程**内与临界区循环完成（不另开 daemon thread，与 Python 资源模型不同但语义等价；占用 1 个 blocking 槽，见 §2.2）。
- 释放：`rmdir`；消失则告警（可能被当作 stale 抢走）。

**锁内禁止网络 I/O**：与 claude-swap 相同。Rust 侧用模块分层强制——`switcher` 不依赖 `oauth`/`reqwest`；引擎在锁外完成 freshen 与 identity 探测后再进入事务。

死锁安全性：claude-switch 与 Claude Code 使用**相同的锁顺序**（oauth_refresh → legacy），互相等待只会超时不会死锁（CC 在 legacy 竞争时释放主锁重试）。

### 5.2 事务步骤与回滚

正常路径（`switch_to`，ref: switcher.py L4999-5170）：

```
锁外准备:
  1. 解析目标 slot，校验凭据与 config 备份存在且 config 含 oauthAccount
  2. 读取 live 凭据做 provenance 预取（profile oracle，可选）
进入三锁临界区:
  3. 快照回滚材料: live 凭据（读失败≠缺失，失败即中止）+ 全局 config 原文
  4. live 凭据与目标不同 → stash 安全副本（失败即中止；--force 除外）
  5. 备份当前账号的 live 凭据与 config 到其 slot（正常路径；直接激活路径跳过）
  6. 组合目标凭据 := merge_shared_credential_fields(目标 slot 凭据, live 共享字段)
  7. 写活动凭据（§4.2 的 Keychain/文件路由与互斥清除）
  8. 拼接 oauthAccount: 读现有全局 config → 仅替换 oauthAccount 键 → 原子写回
     （保留 projects/settings 等本机状态；无可用 config 时写完整导入 config）
  9. 更新 sequence.json 的 activeAccountNumber  ← **提交点**（_write_json 的 rename）
异常路径: 逆序回滚（sequence → config → credentials），回滚失败逐项记日志并继续，
         最终向用户报告"切换失败且回滚不完整"（ref: SwitchTransaction.rollback）
```

事务外收尾（best-effort，不失败切换）：session profile 失效（v2 才有）、新活动账号的轮询计划重排（`_replan_new_active`）、发送 `Switch` 事件。

### 5.3 与 Claude Code 热加载机制的交互

| 平台/形态 | Claude Code 的失效机制 | 我们的动作 |
|---|---|---|
| macOS，Keychain-only（无 `.credentials.json`） | Keychain 结果缓存 ~30s TTL | 只写 Keychain；运行中的 CC 最多 30s 后拾取 |
| macOS，文件存在 | 磁盘 mtime 缓存失效 | Keychain 写成功后**重写已存在的文件** bump mtime（#86）；绝不创建新文件 |
| Linux/Windows | 总是读 `.credentials.json`（mtime 失效） | 原子写文件即可自动拾取 |
| 凭据锁竞争 | CC 刷新在双锁内"读-刷新-保存"，并在锁内重读 | 我们持双锁交换 → CC 锁内重读到新（未过期）凭据 → 中止自己的刷新（ref: claude_locks.py L20-26） |

引擎侧 freshen 窗口：`FRESHEN_BUFFER_MS = 10min`——激活目标前若其 access token 10 分钟内过期，先用其备份 refresh token 刷新并**立即持久化**（refresh token 一次性，grant 消耗一代就必须落盘 successor），10 分钟是 CC 自身 5 分钟刷新缓冲的两倍，保证 CC 锁内重读看到的是新 token（ref: autoswitch.py L60-64）。

---

## 6. 用量轮询引擎

### 6.1 API 端点（ref: oauth.py）

| 用途 | 请求 |
|---|---|
| 用量 | `GET https://api.anthropic.com/api/oauth/usage`，`Authorization: Bearer <token>`，`anthropic-beta: oauth-2025-04-20`，5s 超时 |
| 身份 oracle | `GET https://api.anthropic.com/api/oauth/profile`，Bearer，5s 超时；仅当 `account.uuid` 为非空字符串才算"已解析"，否则 None（fail-open） |
| token 刷新 | `POST https://platform.claude.com/v1/oauth/token`，`client_id = 9d1c250a-e61b-44d9-88ed-5944d1962f5e`，`grant_type=refresh_token`，10s 超时 |

HTTP 栈：`reqwest` + `rustls`（避免 OpenSSL 跨平台问题）。

**User-Agent 稳定性契约**：

- 格式固定：`claude-switch/<semver>`，例如 `claude-switch/1.0.0`。
- `<semver>` 来自 `VERSION` / `CARGO_PKG_VERSION`，**禁止**附加 `-debug` / `-beta` / 渠道后缀到 UA 字符串。
- **stable 与 beta 通道共用同一 UA 前缀策略**（均为 `claude-switch/<version>`）。渠道差异只体现在更新 feed URL，不体现在 UA——避免按 token×UA 类把预算切碎。
- 单元测试锁定：`assert_eq!(user_agent(), format!("claude-switch/{}", env!("CARGO_PKG_VERSION")))` 且匹配 regex `^claude-switch/\d+\.\d+\.\d+$`。
- `client_id` 为 Anthropic 公开 OAuth 客户端 id，与 cswap 相同；不得“轮换”或按 build flavor 更换。

### 6.2 自适应轮询参数（逐一移植 ref: poll_policy.py）

端点预算实测形态：每 token 约 60 分钟滚动窗口 ~28-30 请求；容量只能随旧请求老化恢复。目标均值 ≤ 20 请求/小时/token。

| 常数 | 值 | 语义 |
|---|---|---|
| `SERVE_TTL_S` | 180 | 新鲜度地板：更年轻的条目直接服务不抓取 |
| `MIN_INTERVAL_S` | 180 | 正常节奏地板 |
| `URGENT_INTERVAL_S` | 60 | 紧急模式：活动账号在阈值 15pct 内且用量在动 |
| `ACTIVE_MAX_INTERVAL_S` / `CANDIDATE_DEFAULT_INTERVAL_S` / `CANDIDATE_MAX_INTERVAL_S` | 300 / 300 / 600 | 衰减天花板 |
| `EXHAUSTED_INTERVAL_S` | 600 | 耗尽账号的慢探测（可能提前恢复） |
| `MOVEMENT_DELTA_PCT` | 1.0 | binding pct 变化 ≥1 → 间隔减半（地板 180s） |
| `JITTER_FRAC` | 0.1 | ±10% 抖动，多进程不锁步 |
| `EDGE_BACKOFF_S` | 300 | 429+`Retry-After: 0` 的探测间隔 |
| `POST_429_MIN_INTERVAL_S` / `RECENT_429_WINDOW_S` | 360 / 3600 | 见过 429 后 1h 内的节奏地板 |
| `POST_429_BACKOFF_MULT` / `POST_429_MAX_INTERVAL_S` | 1.5 / 1800 | AIMD 乘性增长（多机共享 token 的类 TCP 拥塞控制） |
| `ESCALATION_MARGIN_PCT` | 15.0 | 活动账号距阈值 15pct 内 → 全量候选刷新 |
| `RESET_SLACK_S` | 60 | 下一次轮询不晚于已知 reset+60s |

`plan_after_fetch(prev_interval, prev_usage, new_usage, is_active, threshold, models, recent_429, now)` 按上述规则逐行移植，输出 `(next_poll_at, interval_s)` 并持久化到 usage store（`nextPollAt`/`pollIntervalS`），所有读取方共享同一节奏。

### 6.3 429 与失败退避（ref: usage_store.py）

- `Retry-After: 0` → 预算耗尽边缘，≥300s 后探测。
- `Retry-After: N>0` → burst 规则，照单全收，封顶 3600s（横跨一个滚动窗口；封顶防病态 header 无限停驻）。
- 无 Retry-After 的失败 → `30s · 2^(n-1)`，封顶 600s，指数 shift 封顶 32。
- **stale-on-error**：失败只更新错误/退避字段，绝不动 last-good 测量。信任上限：一般失败 3600s（`TRUST_MAX_AGE_S`）；429 陈化数据信任到窗口 reset（上限 7200s 兜底）——429 是轮询节流而非配额变化，last-good 是真实用量的下界。
- **claim 租约**：并发收集器（引擎 tick 与 GUI 手动刷新）以 `claimUntil`（90s TTL）认领抓取集合，崩溃的认领者自然过期。协议：lock→读/认领→unlock→**无锁抓取**→lock→合并→写→unlock。
- 同一 collect pass 内逐账号请求错开 0.25s（`_FETCH_STAGGER_S`，请求礼仪）。
- `invalid_grant`（refresh lineage 死亡）是确定性永久失败：达阈值即不再抓取，账号进 quarantine 态（哨兵 `relogin_required`）。

### 6.4 token 刷新时机

- **活动账号永不主动刷新**——凭据归 Claude Code 所有（ref: `try_fetch_usage_for_account`）。活动 token 过期 → 哨兵 `token_expired`，引擎进入 idle-hold（见 §7）。
- **非活动账号**：备份凭据过期 → 刷新 → 持久化（失败响亮告警：refresh token 一次性，落盘失败 = 下一次 `invalid_grant`）→ 用新 token 请求；401 时对有 refresh token 者刷新重试一次。
- 刷新响应可能带 `account`/`organization` 身份（`tokenAccount`），作为零请求身份源用于 quarantine 判定与 uuid 回填（严格边界：畸形即 None，绝不允许身份解析搞坏刷新）。

---

## 7. 自动切换引擎

### 7.1 状态机与 tick 流程

引擎为 tokio 常驻 task：`tick → 计算睡眠 → tokio::time::sleep（可被 wake 事件打断）→ tick`。仅 **leader** 执行步骤 5–12 的自动切换决策；observe-only 执行 1–4（用量收集）后发 `NoSwitch { reason: ObserveOnly }` 并睡 `intervalSeconds`。

单次 leader `tick()` 的决策流程（对齐 ref: autoswitch.py `_tick_inner`）：

```
1. 读 autoswitch_state.json → 释放已恢复的 quarantine；续约 leadership
2. 无活动账号 → PollEvent + NoSwitchEvent（区分 unmanaged-active / no-active），NO_ACTION
3. 按计划收集用量（活动账号 + 一个到期候选；升级带内全量刷新）
4. 活动账号是 API key 且未 include → NoSwitch("active-api-key")，NO_ACTION
5. 活动 headroom 已知:
   - utilization < threshold → 非 consume-first: NoSwitch("below-threshold")；
     consume-first: trigger=consume-first（两阶段提交：先按快照决策，点火前新鲜化重决策）
   - ≥ threshold → trigger = at-limit（headroom≤0）| proactive
6. 活动 headroom 未知:
   - 哨兵 token_expired → idle-hold：Claude Code 持有 token 且闲置，慢速等待自愈；
     持续 > 30min（IDLE_HOLD_MAX_S）恢复不健康计数（可能是死 refresh token + 活跃用户）
   - 其余 → unhealthy_ticks++；未到 3 次 NO_ACTION；达到 → trigger=failover
7. proactive/consume-first 且在冷却内 → NoSwitch("cooldown")
8. 候选选择（排除当前/隔离/disabled；API-key 候选仅在 include 时且必须移动时兜底）
9. 排序:
   - best: headroom 最大；候选须低于阈值且优于活动账号 hysteresis_pct（10pct）
   - consume-first: 7d reset 最早者（严格更早 + 有余量）；已到期的 reset 视为未知
   - 全部账号 ≥ 阈值 → 恢复逃逸：选 binding 窗口最早恢复者，须早 ≥ RECOVERY_HYSTERESIS_S(300s)
10. 逐候选 freshen（§5.3）→ ok 即激活；invalid_grant/identity-conflict → quarantine 该候选
    并尝试下一个；transient → 本 tick 放弃
11. 全部不可用 → AllExhaustedEvent（带最早 reset），BLOCKED，睡到 reset+slack（封顶 600s）
12. 成功 → 执行切换事务（§5，锁内）→ 写冷却 → SwitchEvent；dry-run 只发事件不动作
```

### 7.2 语义与配置项

**quarantine**（ref: autoswitch.py `_quarantine`）：refresh lineage 死亡（`invalid_grant`）或身份冲突（refresh 出来的 token 属于别的账号——org 优先比较，再比 uuid）时，把 slot 写入 `autoswitch_state.json` 的 `quarantine` 表，记录 `{email, reason, at, refreshTokenFingerprint}`。释放条件：slot 的 email 变化（账号被替换）或凭据指纹变化（用户重新登录并 re-add）——引擎每 tick 自动检查并释放。指纹 = refresh token 的 sha256（`sha256:` 前缀），无 refresh token 时全文哈希（`sha256-full:`）。

**冷却**：默认 300s，仅约束 proactive/consume-first；活动账号硬到限（at-limit）与 failover 绕过。

**迟滞**：候选必须比活动账号好 ≥10pct 才切换，防两个账号在阈值线附近乒乓。

**配置项**（`settings.json` 的 `autoswitch` 节，camelCase，范围钳制与 ref: settings.py `SETTING_SPECS` 一致）：

| 键 | 默认 | 范围/取值 |
|---|---|---|
| `threshold` | 90.0 | 50.0–99.9 |
| `intervalSeconds` | 60.0 | 15–3600 |
| `cooldownSeconds` | 300.0 | 0–86400 |
| `hysteresisPct` | 10.0 | 0–50 |
| `strategy` | `"best"` | `"best"` \| `"consume-first"`（手动切换另有 `"next-available"`，同样支持） |
| `includeApiKeyAccounts` | false | bool |
| `unhealthyTicks` | 3 | 1–100 |
| `model` | null | 逗号分隔 display_name 或 `"all"`（大小写不敏感、去重） |

**`enabled` 单一事实源**（解决 start/stop 与配置双控）：

| 存储 | 键 | 含义 |
|---|---|---|
| `settings.json` → `autoswitch.enabled`（claude-switch 增量键；cswap 忽略未知键） | bool，默认 `false` | **用户意图**：是否希望自动切换在 App 运行时启用 |
| 内存 `EngineStatus.running` | bool | 本进程 task 是否在跑 |
| 内存 `EngineStatus.is_leader` | bool | 是否持有领导租约 |

行为：

1. `init_engine` 成功后：若 `autoswitch.enabled == true`，自动 `autoswitch_start(None)`（尝试成为 leader）。
2. `autoswitch_start(overrides)`：合并 overrides 到内存配置；**写** `settings.json` `autoswitch.enabled = true`；启动 task；尝试 leadership。
3. `autoswitch_stop()`：**写** `enabled = false`；停止 task；释放 leadership。
4. 崩溃后重启：读 `enabled`；true → 再 start（与 1 相同）。
5. `AutoswitchConfig.enabled` 字段 = 上述持久化值的镜像，供 GUI 开关绑定；GUI 开关 on → `start`，off → `stop`，不要只改内存。

加载宽容（坏文件/坏值 → 默认值 + 告警）、写入严格（GUI 设置时越界即报错）、未知键 round-trip 保留、写穿透 symlink（§4.2）。

---

## 8. FFI API 设计

> **契约语言**：本节所有方法名、JSON 键、错误码、枚举字符串均为 **English-only**，可直接 copy-paste 实现。叙述性中文不影响键名。

### 8.0 公开 wire 字母表（kebab-case 唯一规范）

**规则**：所有跨边界的策略/触发器/原因/事件 tag **字符串**使用 **kebab-case**。单一事实源 = Rust `serde(rename_all = "kebab-case")` 与下表。禁止在 UniFFI UDL、C ABI JSON、settings.json、事件 JSON 中混用 snake_case 作为这些 token 的 wire 值。

| 类型 | 合法 wire 值（完整枚举） |
|---|---|
| `SwitchStrategy` | `best` · `consume-first` · `next-available` |
| `SwitchTrigger` | `proactive` · `at-limit` · `failover` · `consume-first` · `manual` |
| `NoSwitchReason` | `below-threshold` · `cooldown` · `active-api-key` · `unmanaged-active` · `no-active` · `unhealthy` · `no-candidate` · `observe-only` · `engine-paused` · `other` |
| `CoreEvent` tag（`event` 字段） | `poll` · `switch` · `no-switch` · `account-quarantined` · `account-unquarantined` · `all-exhausted` · `sleep` · `error` · `config-warning` · `snapshot-updated` · `job-finished` |
| `AccountKind` | `oauth` · `api-key` |
| `UsageStatus` | `ok` · `token-expired` · `api-key` · `keychain-unavailable` · `relogin-required` · `foreign-credential` · `no-credentials` · `unavailable` |
| 错误 `code` | 一律 kebab（§8.5），如 `claude-code-lock-timeout` |

**分层约定**：

| 层 | 形式 | 说明 |
|---|---|---|
| 磁盘 `settings.json` `autoswitch.strategy` | kebab 字符串 | 与 cswap 一致：`"consume-first"` |
| 事件 JSON / Snapshot JSON / C ABI | kebab（serde） | 与 cswap `--json` 习惯对齐 |
| UniFFI UDL enum 字面量 | **必须写 kebab**（`"consume-first"` 而非 `"consume_first"`） | 生成绑定的 *标识符* 可能是语言惯用风格（Swift `consumeFirst` 等）；**序列化/比较 wire 值时仍用 kebab** |
| Rust 源码枚举变体 | PascalCase | `SwitchTrigger::ConsumeFirst` |
| `AutoswitchConfig.strategy` | 类型 = `SwitchStrategy`（非自由 string） | 与 `SwitchOptions.strategy` 同一类型 |

**边界转换**：FFI 入口若收到未知策略串 → `validation-failed`。读 settings 时非法值 → 回落默认 `best` + `ConfigWarning`（与 §7.2 宽容读一致）。

**契约测试**（PR 10 / fixtures）：`tests/fixtures/enum_wire_v1.json` 列出上表每一值；对每个 `SwitchTrigger` / `NoSwitchReason` / `SwitchStrategy` 变体 `serde_json::to_string` 断言精确等于表中 kebab 字符串（含连字符，无下划线）。

### 8.1 总体形态

- **请求-响应**（查询/命令）：UniFFI 导出的 `Engine` 接口对象方法；短查询同步返回，长操作返回 `jobId` 并异步回执。
- **事件流**：UniFFI callback interface（Swift）/ C 函数指针（C#）订阅 `CoreEvent` 流。
- **错误模型**：结构化错误 enum（UniFFI `[Error]`）+ 稳定错误码字符串。
- **schema 演进**：事件与 Snapshot 的 JSON 投影带 `schemaVersion`（事件 1 / Snapshot 2）；`init_engine` 带 `ffi_schema_version` 握手（§8.6）。

### 8.2 UniFFI UDL（`crates/ffi/claude_switch.udl`）

```idl
namespace claude_switch {
    [Throws=SwitchError]
    Engine init_engine(InitOptions opts);

    string schema_version();          // 核心支持的 FFI schema，如 "1"
    string core_version();            // semver 字符串
};

// 当前 FFI_SCHEMA_VERSION = 1
dictionary InitOptions {
    string? backup_root_override;     // 测试用；null = 按平台规则解析
    boolean debug_logging = false;
    u32 ffi_schema_version;           // GUI 声明自己编译期支持的 schema；必填
};

dictionary AccountRow {
    u32 number;
    string email;
    string organization_name;
    string organization_uuid;
    boolean is_organization;
    boolean active;
    AccountKind kind;
    string? alias;
    boolean disabled;
    boolean quarantined;
    UsageStatus usage_status;
    UsagePayload? usage;
    string? usage_fetched_at;         // ISO-8601 UTC
    double? usage_age_seconds;
};

// wire/JSON 使用 kebab（§8.0）。UniFFI 3.x enum 字符串字面量如下——与 JSON 相同。
enum AccountKind { "oauth", "api-key" };
enum UsageStatus { "ok", "token-expired", "api-key", "keychain-unavailable",
                   "relogin-required", "foreign-credential", "no-credentials",
                   "unavailable" };

dictionary UsagePayload {
    WindowPayload? five_hour;
    WindowPayload? seven_day;
    SpendPayload? spend;
    sequence<ScopedWindowPayload> scoped;
};

dictionary WindowPayload {
    double pct;
    string? resets_at;
    string? countdown;
    string? clock;
    double? expected_pct;
    boolean? ahead_of_pace;
    string? projected_exhaustion_at;
    boolean? will_last_to_reset;
};

dictionary SpendPayload {
    double? used;
    double? limit;
    string? currency;
};

dictionary ScopedWindowPayload {
    string display_name;
    double pct;
    string? resets_at;
};

// —— 完整导出类型目录（凡 UDL/事件引用必须在此定义）——

dictionary AccountRef {
    u32 number;
    string email;
    string? alias;
};

dictionary HeadroomEntry {
    u32 number;
    double? pct;
};

dictionary FetchErrorEntry {
    u32 number;
    string message;
};

dictionary WindowPctEntry {
    u32 number;
    string window;
    double pct;
};

dictionary JobResult {
    boolean ok;
    string? error_code;
    string? message;
    boolean retryable;
    string? summary;
};

dictionary EngineStatus {
    boolean running;
    boolean is_leader;
    boolean enabled;
    string? next_tick_at;
    double? cooldown_remaining_seconds;
    string? pause_reason;
    u32 pending_mutations;
};

// 全部 kebab-case，与 §8.0 / serde / cswap / settings 一致（禁止 snake_case wire）
enum SwitchStrategy {
    "best", "consume-first", "next-available"
};

enum NoSwitchReason {
    "below-threshold", "cooldown", "active-api-key", "unmanaged-active",
    "no-active", "unhealthy", "no-candidate", "observe-only",
    "engine-paused", "other"
};

enum SwitchTrigger {
    "proactive", "at-limit", "failover", "consume-first", "manual"
};

// CoreEvent：UniFFI 用扁平 enum + 关联 dictionary，避免嵌套 Map
[Enum]
interface CoreEvent {
    Poll(AccountRef? active, sequence<HeadroomEntry> headroom, double threshold,
         sequence<FetchErrorEntry> fetch_errors, sequence<WindowPctEntry> windows);
    Switch(SwitchTrigger trigger, AccountRef? from, AccountRef? to,
           sequence<string> warnings, boolean dry_run);
    NoSwitch(NoSwitchReason reason, string detail);
    AccountQuarantined(u32 number, string email, string reason);
    AccountUnquarantined(u32 number, string email, string reason);
    AllExhausted(string? earliest_reset_at);
    Sleep(double seconds, string until);
    Error(string message, boolean transient);
    ConfigWarning(string message);
    SnapshotUpdated();
    JobFinished(u64 job_id, JobResult result);
};

dictionary Snapshot {
    u32 schema_version;
    string taken_at;
    u32? active_number;
    sequence<AccountRow> accounts;
    EngineStatus engine;
};

interface Engine {
    // —— 查询（同步；GUI 侧离开 UI 线程）——
    [Throws=SwitchError]
    Snapshot snapshot();
    [Throws=SwitchError]
    AccountRow? account_by_identifier(string identifier);
    [Throws=SwitchError]
    AutoswitchConfig get_autoswitch_config();
    boolean is_claude_code_running();

    // —— 命令（长操作 → jobId + JobFinished）——
    u64 switch_account(string identifier, SwitchOptions opts);
    u64 add_account(AddAccountOptions opts);
    u64 remove_account(u32 number);
    u64 refresh_usage(sequence<u32>? numbers);
    u64 set_alias(u32 number, string? alias);
    u64 set_disabled(u32 number, boolean disabled);
    u64 export_accounts(ExportOptions opts);
    u64 import_accounts(ImportOptions opts);

    // —— 引擎控制 ——
    [Throws=SwitchError]
    void autoswitch_start(AutoswitchConfig? overrides);  // claim 使用 force=false
    void autoswitch_stop();
    // 领导租约：设置页“强制抢领导”确认后调用 claim_leadership({ force: true })
    [Throws=SwitchError]
    void claim_leadership(ClaimLeadershipOptions opts);
    void wake();
    [Throws=SwitchError]
    void set_setting(string dotted_key, string raw_value);
    [Throws=SwitchError]
    void unset_setting(string dotted_key);

    void set_event_listener(EventListener? listener);
    void shutdown();
};

callback interface EventListener {
    void on_event(CoreEvent event);
};

dictionary SwitchOptions {
    boolean force = false;
    boolean dry_run = false;
    SwitchStrategy? strategy = null;
};

dictionary ClaimLeadershipOptions {
    boolean force = false;
    // force=false: 仅当 lease 过期/pid 已死时认领；尊重 cswap .lock 启发式 → 可能仍 observe-only
    // force=true:  立即写本进程 lease（覆盖 live foreign/unknown）；发 ConfigWarning；要求引擎 running
};

dictionary AddAccountOptions {
    u32? slot = null;
    string? setup_token = null;
};

dictionary ExportOptions {
    string path;                      // 目标 .cswap 路径；核心做路径穿越校验
    sequence<u32>? numbers = null;    // null = 全部
    boolean include_settings = true;
};

dictionary ImportOptions {
    string path;                      // 源 .cswap
    boolean merge = true;             // true=按 email 合并；false=拒绝非空序列
    boolean dry_run = false;
};

dictionary AutoswitchConfig {
    double threshold;
    double interval_seconds;
    double cooldown_seconds;
    double hysteresis_pct;
    SwitchStrategy strategy;          // 与 SwitchOptions.strategy 同类型；wire kebab（§8.0）
    boolean include_api_key_accounts;
    u32 unhealthy_ticks;
    string? model;
    boolean enabled;                  // 持久化意图，见 §7.2
};

[Error]
enum SwitchError {
    "AccountNotFound", "ValidationFailed", "ConfigError", "CredentialError",
    "CredentialWriteFailed", "CredentialReadFailed", "LockTimeout",
    "ClaudeCodeLockTimeout", "SessionInUse", "KeychainUnavailable",
    "Network", "TransferError", "MigrationError", "SchemaUnsupported",
    "AlreadyRunning", "Internal"
};
```

### 8.3 C ABI 完整契约（Windows P/Invoke）

UniFFI 不生成 C#，因此在 `ffi` crate 暴露一组薄 C ABI（`cbindgen` 生成头文件）。设计原则：**JSON over the wire**——请求/响应均为 UTF-8 JSON 字符串（与 UniFFI 层共享同一 serde 类型），C# 侧用 `System.Text.Json` 反序列化。

#### 8.3.1 头文件

```c
/* claude_switch.h — 全部字符串为 UTF-8，无 locale code page */
#pragma once
#include <stdint.h>

#ifdef _WIN32
  #ifdef CS_BUILD
    #define CS_API __declspec(dllexport)
  #else
    #define CS_API __declspec(dllimport)
  #endif
#else
  #define CS_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct CsEngine CsEngine;

typedef struct CsError {
    int32_t code;   /* CsErrorCode；CS_OK=0 时 message 必为 NULL */
    char* message;  /* 堆分配 UTF-8；非 NULL 时由调用方 cs_string_free */
} CsError;

/* 与 §8.5 kebab-case 稳定码一一对应的数值枚举；只增不改 */
typedef enum CsErrorCode {
    CS_OK = 0,
    CS_ACCOUNT_NOT_FOUND = 1,
    CS_VALIDATION_FAILED = 2,
    CS_CONFIG_ERROR = 3,
    CS_CREDENTIAL_ERROR = 4,
    CS_CREDENTIAL_WRITE_FAILED = 5,
    CS_CREDENTIAL_READ_FAILED = 6,
    CS_LOCK_TIMEOUT = 7,
    CS_CLAUDE_CODE_LOCK_TIMEOUT = 8,
    CS_SESSION_IN_USE = 9,
    CS_KEYCHAIN_UNAVAILABLE = 10,
    CS_NETWORK = 11,
    CS_TRANSFER_ERROR = 12,
    CS_MIGRATION_ERROR = 13,
    CS_SCHEMA_UNSUPPORTED = 14,
    CS_ALREADY_RUNNING = 15,
    CS_INTERNAL = 16,
    CS_NULL_POINTER = 17,
    CS_INVALID_METHOD = 18,
    CS_JSON_PARSE = 19
} CsErrorCode;

CS_API CsEngine* cs_engine_new(const char* init_options_json, CsError* out_err);
CS_API void      cs_engine_free(CsEngine* e);

/* 成功：返回堆分配 JSON，调用方 cs_string_free；*out_err = {CS_OK,NULL}
 * 失败：返回 NULL；*out_err 填 code+message（message 堆分配） */
CS_API char* cs_engine_call(CsEngine* e,
                            const char* method,
                            const char* args_json, /* 可 NULL ≡ {} */
                            CsError* out_err);

typedef void (*CsEventCallback)(const char* event_json, void* user_data);
/* cb==NULL 退订。user_data 所有权在调用方；引擎只存指针不 free */
CS_API void cs_engine_set_event_callback(CsEngine* e,
                                         CsEventCallback cb,
                                         void* user_data);

CS_API void cs_engine_shutdown(CsEngine* e);
CS_API void cs_string_free(char* s);

/* 将 CsErrorCode 映射为 kebab-case 稳定字符串（静态存储期，勿 free） */
CS_API const char* cs_error_code_string(int32_t code);

#ifdef __cplusplus
}
#endif
```

#### 8.3.2 字符串与生命周期

| 指针 | 分配方 | 释放方 | 有效期 |
|---|---|---|---|
| `init_options_json` / `method` / `args_json` | 调用方 | 调用方 | 仅在对应 `cs_*` 调用返回前需保持有效；引擎同步拷贝 |
| `cs_engine_call` 返回的 `char*` | 引擎（Rust `CString`） | **调用方** `cs_string_free` | 直到 free |
| `CsError.message` | 引擎 | **调用方** `cs_string_free` | 直到 free |
| `event_json`（回调参数） | 引擎 | 引擎 | **仅回调同步执行期间**。被调方必须在返回前拷贝（C#：`Marshal.PtrToStringUTF8` → managed string）。返回后指针悬空 |
| `user_data` | 调用方 | 调用方 | 必须覆盖整个 callback 注册期（见 8.3.5） |

编码：**仅 UTF-8**。Windows 上不使用 `ANSI`/`WCHAR` API 传业务字符串。

#### 8.3.3 线程、重入、panic

1. `cs_engine_call` / `cs_engine_new` / `set_event_callback` / `shutdown` 可从**任意** OS 线程调用；内部同步。
2. `CsEventCallback` 在 **tokio runtime worker 线程**上同步调用（非 UI 线程）。
3. **回调内禁止**调用任意 `cs_engine_*`（包括 `cs_engine_call` 与 `set_event_callback`），无白名单例外——比 UniFFI 更严，因 C# 封送更易死锁。需要 snapshot 时：回调里只把 JSON 拷到队列，UI 线程再 `cs_engine_call("snapshot", …)`。
4. 若回调抛出 SEH/托管异常：C# 侧必须在 native 回调入口 `try/catch` 吞掉并记日志；Rust 侧假设回调不 panic。Rust 自身 panic → `catch_unwind` → 不进入回调，转 `Error` 事件或 `CS_INTERNAL`。
5. `cs_engine_free` 仅在 `shutdown` 之后且无进行中 call；重复 free = UB。
   **唯一合法拆除顺序**（与 §2.2 / §8.3.5 / §8.8 完全一致，禁止颠倒）：
   1. `cs_engine_set_event_callback(e, NULL, NULL)` — 退订；此后不得再回调  
   2. `cs_engine_shutdown(e)` — 排空 mutation、释 leadership、停 tasks  
   3. `cs_engine_free(e)`

#### 8.3.4 `cs_engine_call` method 目录

| method | args_json schema | 成功 response JSON | 同步/异步 |
|---|---|---|---|
| `snapshot` | `{}` | `Snapshot` 对象 | 同步（返回体即结果） |
| `account_by_identifier` | `{"identifier":"2"}` | `AccountRow` 或 `null` | 同步 |
| `get_autoswitch_config` | `{}` | `AutoswitchConfig` | 同步 |
| `is_claude_code_running` | `{}` | `{"running":true}` | 同步 |
| `switch_account` | `{"identifier":"2","opts":{"force":false,"dryRun":false,"strategy":"next-available"}}` | `{"jobId":42}` | 异步；strategy 为 kebab 或 null |
| `add_account` | `{"opts":{"slot":null,"setupToken":null}}` | `{"jobId":…}` | 异步 |
| `remove_account` | `{"number":3}` | `{"jobId":…}` | 异步 |
| `refresh_usage` | `{"numbers":null}` 或 `{"numbers":[1,2]}` | `{"jobId":…}` | 异步 |
| `set_alias` | `{"number":1,"alias":"dev"}` | `{"jobId":…}` | 异步 |
| `set_disabled` | `{"number":1,"disabled":true}` | `{"jobId":…}` | 异步 |
| `export_accounts` | `{"path":"C:\\\\a.cswap","numbers":null,"includeSettings":true}` | `{"jobId":…}` | 异步 |
| `import_accounts` | `{"path":"C:\\\\a.cswap","merge":true,"dryRun":false}` | `{"jobId":…}` | 异步 |
| `autoswitch_start` | `{"overrides":null}` 或 config 对象 | `{}` | 同步；内部 claim force=false |
| `autoswitch_stop` | `{}` | `{}` | 同步 |
| `claim_leadership` | `{"force":true}` | `{"isLeader":true}` 或 `{"isLeader":false}` | 同步；见 §2.2 |
| `wake` | `{}` | `{}` | 同步 |
| `set_setting` | `{"key":"autoswitch.threshold","value":"92"}` | `{}` | 同步 |
| `unset_setting` | `{"key":"autoswitch.model"}` | `{}` | 同步 |
| `shutdown` | `{}` | `{}` | 同步（亦可用 `cs_engine_shutdown`） |

未知 method → `CS_INVALID_METHOD`。JSON 解析失败 → `CS_JSON_PARSE`。`args_json == NULL` 视为 `{}`。

异步方法成功时 HTTP 语义的“业务失败”通过后续 `JobFinished` 事件传递；`cs_engine_call` 仅在入队前校验失败时返回非 OK。

#### 8.3.5 C# 封送要点（强制）

```csharp
// 伪代码 — gui-win/ClaudeSwitch.Core/Native.cs
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void CsEventCallback(IntPtr eventJson, IntPtr userData);

static CsEventCallback? _pinnedCb; // 必须是字段，防 GC
static GCHandle _userDataHandle;

void Subscribe(CsEngineSafeHandle eng) {
    _pinnedCb = OnEvent;
    _userDataHandle = GCHandle.Alloc(this);
    cs_engine_set_event_callback(eng, _pinnedCb, GCHandle.ToIntPtr(_userDataHandle));
}

void OnEvent(IntPtr eventJson, IntPtr userData) {
    // 1) 立刻拷贝：指针只在回调内有效
    var json = Marshal.PtrToStringUTF8(eventJson) ?? "";
    // 2) 绝不在此调用 cs_engine_call
    // 3) marshal 到 UI 线程
    _dispatcherQueue.TryEnqueue(() => HandleEventJson(json));
}

void Dispose() {
    // 唯一合法顺序：退订 → shutdown → free（§2.2 / §8.3.3）
    cs_engine_set_event_callback(eng, null, IntPtr.Zero);
    cs_engine_shutdown(eng);
    eng.Dispose(); // cs_engine_free
    if (_userDataHandle.IsAllocated) _userDataHandle.Free();
    _pinnedCb = null;
}
// 契约测试：set_event_callback(null) 之后即使 runtime 仍短暂存活，也不得调用托管 OnEvent
```

- 回调委托必须存为 **GC 根**（静态/实例字段），不能是局部变量。
- `user_data` 若指向托管对象，使用 `GCHandle.Alloc`，引擎生命周期内不可 `Free`。
- DLL 搜索：打包后与 exe 同目录；开发 unpackaged 见 §9.2。

### 8.4 事件流契约

- 事件类型 = §3.2 / §8.2 的 `CoreEvent`，序列化投影兼容 claude-swap `--json` 事件流习惯（`schemaVersion`、`event`、`ts` 三键 + camelCase 字段名；**枚举/tag 值为 kebab-case**，§8.0；ref: autoswitch.py `AutoSwitchEvent.to_json`）。C ABI 回调的 `event_json` 即该投影。
- 语义保证：
  - **有序**（单 producer）。
  - **至少一次**（GUI 断线重连后先拉 `snapshot()` 对齐）。
  - **背压**：通道容量 256。
- **丢弃策略（硬保证）**：
  - **仅** `Poll` 可被丢弃。溢出时丢弃中间 `Poll`，并确保最终补一条 `SnapshotUpdated`（合并降级）。
  - **`Switch` / `AccountQuarantined` / `AccountUnquarantined` / `JobFinished` / `Error` / `ConfigWarning` / `AllExhausted` / `NoSwitch` / `Sleep` / `SnapshotUpdated` 永不静默丢弃**。实现：这些变体 `try_send` 失败时写入有界 side buffer（容量 64）；side buffer 亦满则 **block 生产方**（mutation/autoswitch task）直到有空位，并 `tracing::error` 记一条。禁止为吞吐丢弃 `JobFinished`。
- GUI 生命周期内 `set_event_listener(None)` / `cs_engine_set_event_callback(NULL)` 即退订。

### 8.5 错误模型

- Rust 侧：`thiserror` 结构化错误树，`SwitchError` 为 FFI 边界聚合类型。
- 跨 FFI 携带：`code`（稳定 kebab-case 字符串）、`message`（人类可读、已脱敏）、`retryable`（bool）。
- C ABI 数值码与 kebab-case 映射（`cs_error_code_string`）：

| CsErrorCode | code 字符串 |
|---|---|
| 1 | `account-not-found` |
| 2 | `validation-failed` |
| 3 | `config-error` |
| 4 | `credential-error` |
| 5 | `credential-write-failed` |
| 6 | `credential-read-failed` |
| 7 | `lock-timeout` |
| 8 | `claude-code-lock-timeout` |
| 9 | `session-in-use` |
| 10 | `keychain-unavailable` |
| 11 | `network` |
| 12 | `transfer-error` |
| 13 | `migration-error` |
| 14 | `schema-unsupported` |
| 15 | `already-running` |
| 16 | `internal` |
| 17 | `null-pointer` |
| 18 | `invalid-method` |
| 19 | `json-parse` |

与 claude-swap 异常类对应（ref: exceptions.py）保持稳定；只增不改。

### 8.6 schemaVersion 与握手

**三层版本**：

| 层 | 字段 | 当前值 |
|---|---|---|
| FFI Snapshot/事件 | `schemaVersion` | Snapshot=2（v2 增补 `accounts[].plan` / `activeVerified` / `unmanagedLoginEmail`，纯增量，旧读者忽略即可）；事件=1 |

**Snapshot 的 `activeAccountNumber` 语义（v2 起）**：由**实时登录**解析，不再直接回放 `sequence.json`。
`sequence.json` 的 `activeAccountNumber` 只记录本工具最后一次 switch；`claude /login`、cswap CLI
切换、登出都会绕过它。因此 Engine 用实时 `.credentials.json` + `.claude.json` 的 `oauthAccount`
身份（`accountUuid` 优先，否则 email + org uuid 成对比较）去匹配各 slot：

- 匹配到 → 该 slot 为当前，`activeVerified=true`
- 已登录但不属于任何托管 slot → `activeAccountNumber=null` + `unmanagedLoginEmail`（**不**回退到过期值）
- 未登录 → `activeAccountNumber=null`
- 有凭据但身份读不出来 → 回退到 `sequence.json` 的值，`activeVerified=false`

`snapshot()` 保持只读；`reconcile_active` 负责把校正后的值写回 `sequence.json`（并顺带补齐旧
slot 空缺的 `uuid`/`organizationUuid`），供 cswap CLI 共读。
| 磁盘文件 | 各文件自有 | sequence/settings/state=1；usage cache=2 |
| 导出信封 | `FORMAT_VERSION` | 1 |

**`init_engine` 握手**（非 phantom）：

```json
// InitOptions (UniFFI / C ABI JSON)
{
  "backupRootOverride": null,
  "debugLogging": false,
  "ffiSchemaVersion": 1
}
```

| GUI 声明 `ffi_schema_version` | 核心支持集合（v1 = {1}） | 结果 |
|---|---|---|
| ∈ 支持集合 | — | 成功；引擎以该版本投影事件/Snapshot |
| 0 或缺失（C ABI JSON 缺键） | — | `schema-unsupported` / `CS_SCHEMA_UNSUPPORTED` |
| > 核心最大 | GUI 更新、核心旧 | 同上拒绝；message 提示升级 App/核心 |
| < 核心最小（未来丢弃旧版时） | — | 同上拒绝 |

Monorepo 同版本发布下，正常安装路径 GUI 与核心始终同数字；握手的价值在于：（1）开发期错配 DLL；（2）未来第三方绑定；（3）热更新部分组件失败时的快速失败。拒绝时**不**创建 backup 副作用（迁移亦不分发）。

规则：增量字段不加版本（消费方必须忽略未知键）；破坏性变更 bump；读侧保留旧版本解析至少两个 minor。

### 8.7 Job 状态机

```
Queued → Running → Succeeded
                 ↘ Failed
                 ↘ Cancelled   // v1 不从 API 进入；仅预留枚举给 v1.1 cancel_job
```

- 每个 `jobId` **恰好一个**终端 `JobFinished`（Succeeded 或 Failed）。shutdown 时 in-flight → Failed，`error_code=engine-shutdown`，`retryable=false`。
- Mutation 串行：多 job 按入队序 `Queued`；usage refresh 可并行。
- **v1 无 `cancel_job`**；UI 在 Running 时显示 spinner，不可取消。若需取消，列入 v1.1。
- `JobFinished` 受 §8.4 保护，不丢弃。通道溢出时 UI 仍可能延迟看到，但最终必达；建议 GUI 在 job 超时（默认 120s）后主动 `snapshot()` 对账。

### 8.8 Swift 并发契约（macOS UniFFI）

1. **出站**：所有 `Engine` 方法从非 MainActor 调用（`Task.detached` 或专用 `DispatchQueue`）。禁止在 `body`/`onAppear` 直接同步 FFI。
2. **入站回调**：`EventListener.on_event` 在 tokio worker 调用；listener 实现必须 **非 `@MainActor`**，且类型 `Sendable`（或内部仅用锁保护的不可变转发）。
3. 回调内：
   ```swift
   func onEvent(event: CoreEvent) {
     let copy = event // 值类型拷贝
     Task { @MainActor in
       viewModel.apply(copy)
     }
     // 协议（§2.2）：回调栈上禁止 mutating；允许只读集合
     //   snapshot / account_by_identifier / get_autoswitch_config /
     //   is_claude_code_running / schema_version
     // 推荐：仍把所有 Engine 调用投递到回调返回之后 / MainActor，
     // 以免 UniFFI 重入踩坑——推荐不等于协议禁止只读。
   }
   ```
4. **重入**：与 §2.2 一致——回调栈上只允许只读集合；mutating 必须投递到回调返回之后。推荐实现可以更严（回调内零 FFI），但不收紧协议。
5. **关闭顺序**（与 §2.2 / §8.3.3 相同，防 use-after-free）：
   ```
   set_event_listener(nil)  →  shutdown()  →  release Engine 引用（deinit）
   ```
   禁止在 `shutdown` 后使用 Engine。Listener 若被 Swift 侧强引用，须在 `set_event_listener(nil)` 后释放。
   契约测试：`set_event_listener(nil)` 之后不得再调用 `on_event`。
6. Engine 与 listener 不得越过 `shutdown` 存活后仍收事件；Rust 在 shutdown 后 drop broadcast sender，迟到 poll 直接结束。

---

## 9. 各平台 GUI 信息架构

### 9.1 共同信息架构（核心驱动，三端一致）

```
菜单栏/托盘图标（常态：当前账号缩写 + 用量最高窗口的环形指示）
├── 左键/单击 → 主面板（仪表盘即主界面，popover/窗口）
│   ├── 账号列表（每行：#slot · 别名/email [Org] · 套餐徽标[Pro/Max 20×/…] ·
│   │             订阅开始日 · 5h/7d/模型 进度条 ·
│   │             reset 倒计时 · 状态徽标[active/disabled/quarantined]）
│   │     · 单击行 → 展开详情（订阅facts、spend、last-seen、token 状态、错误原因）
│   │     · "切换到此账号" 按钮 → 确认弹层 → jobId → 事件驱动刷新
│   │     · 上下文菜单：设别名、禁用/启用、删除（确认）
│   ├── 页脚：自动切换开关 + 状态行（下次评估/冷却中/observe-only/已暂停原因）
│   └── 导航：设置页、账号管理页、关于/诊断页
├── 右键菜单：切换子菜单、立即刷新用量、自动切换开关、退出
└── 首次启动：向导（见 9.3）
```

**账号管理页**（v1 范围）：添加、删除、别名、禁用/启用、**导入 `.cswap`、导出 `.cswap`**。不做 move/swap 拖拽排序（§1 非目标）。

**共享 IA 夹具**：`tests/fixtures/golden_snapshot_v1.json`（及若干事件样例）作为三端 UI 预览与快照测试的单一事实源；PR 10 产出，gui-mac / gui-win / gui-linux 均依赖，**不**依赖另一平台 GUI 代码。

### 9.2 平台差异

| | macOS | Windows | Linux |
|---|---|---|---|
| 形态 | `MenuBarExtra`（`.window` 风格 popover） | WinUI 3 主窗口 + **托盘**（见下） | GTK4 状态图标 + AdwWindow |
| 深浅色 | 跟随系统（SwiftUI 默认） | 跟随系统（`RequestedTheme`） | 跟随系统（libadwaita 默认） |
| 通知 | `UNUserNotificationCenter` | `AppNotification`（Toast） | `org.freedesktop.Notifications` |
| 登录项自启 | `SMAppService.loginItem` | **仅** MSIX `StartupTask`（禁止写注册表 `Run`） | XDG autostart `.desktop` |

#### Windows 托盘与打包（可实施规格）

**托盘方案（选定）**：WinUI 3 本身无一等托盘 API。v1 使用 **Windows App SDK 1.5+ 的 `Microsoft.Windows.AppNotifications` + 经典 `Shell_NotifyIcon` COM 互操作** 封装在 `gui-win/ClaudeSwitch.App/Tray/TrayIconService.cs`：

- 通过 `P/Invoke` 调用 `Shell_NotifyIconW`（`NOTIFYICONDATAW`），图标来自打包的 `.ico`。
- 左键：`ShowWindow` 主窗口 + Mica。
- 右键：WinUI `MenuFlyout` 或原生 `TrackPopupMenu`（实现可选其一；推荐原生弹出避免 WinUI 焦点问题）。
- 不引入第三方托盘库，避免额外签名面；实现量约 200–400 行 C#，集中在一个服务类。

**MSIX 打包**：

| 项 | 选择 |
|---|---|
| 形态 | **packaged only** 发布（R-8）；商店/侧载同一 MSIX |
| Runtime | **framework-dependent**：依赖 Windows App SDK runtime 重分发（`<WindowsAppSDKSelfContained>false</WindowsAppSDKSelfContained>`）；`claude_switch.dll` **自带**（与 App 同包 `ClaudeSwitch.App\claude_switch.dll`） |
| RID | CI 打 `win-x64` 与 `win-arm64` 两个 MSIX（或 single-project multi-RID） |
| `Package.appxmanifest` capabilities | `internetClient`；`runFullTrust`（desktop bridge / full trust，因需读写 `%USERPROFILE%\.claude` 与 `%USERPROFILE%\.claude-swap-backup` 包外路径） |
| 自启 | `uap5:Extension Category="windows.startupTask"`，TaskId=`ClaudeSwitchStartup`；**禁止**注册表 Run 与任务计划程序旁路 |
| 更新 | `.appinstaller` → 新 MSIX（§12.3） |

**开发循环（unpackaged F5）**：

- Debug 配置：`WindowsPackageType=None`，输出目录 post-build 复制 `target/debug/claude_switch.dll`（及 `claude_switch.dll.lib` 若需要）到 `bin/Debug/...`。
- 单实例 mutex / 路径逻辑与 packaged 相同；StartupTask 在 unpackaged 下跳过并 log。
- CI 发布路径仍只产生签名 MSIX；PR 门禁用 unpackaged 头less 契约测试 + 可选 packaged smoke。

### 9.3 关键交互流

**首次启动向导**（检测到 `sequence.json` 不存在且存在 live 登录时）：
1. 欢迎页：说明工作原理（凭据安全存储在 Keychain/文件，不会上传）。
2. 检测页：自动探测 Claude Code 当前登录 → 显示识别到的 email/org；若是 cswap 老用户（检测到 `backup_root` 已有数据）→ 直接进入"发现既有账号"页一键沿用（§4.5 零迁移）。若检测到可能在跑的 `cswap auto`，警告双引擎（§2.2）。
3. 捕获页：一键"将此账号添加为 Account-1"（调 `add_account`）。
4. 引导页：提示"在 Claude Code 中 `/login` 另一个账号后回来点'添加账号'"。
5. 完成页：自动切换介绍（默认关，用户显式开启）→ 进入仪表盘。

**添加账号向导**（仪表盘"+ 添加账号"）：检测当前 live 登录身份 → 与已有账号比对（同 email+org 则提示"将覆盖 Account-N 的凭据"）→ 确认 → 完成。提供"粘贴 setup-token"高级入口（等价 `cswap --add-token`）。

**切换确认**：显示"Account-1 (a@x.com) → Account-2 (b@y.com)"，附目标账号 5h/7d 用量摘要与 token 状态；Claude Code 运行中时追加一行"新账号将在当前会话自动生效（macOS 最长约 30 秒）"。切换进行中按钮变 spinner（事件 `JobFinished` 收敛），失败弹结构化错误（含 retryable 提示）。

**通知 / UI / 日志 的 PII 分层**（统一）：

| 层 | email 策略 | token/API key |
|---|---|---|
| (1) 应用内 UI（仪表盘、确认框、设置） | **可显示完整 email** | 永不显示原文 |
| (2) OS 通知（Toast / UNNotification / desktop notification） | **掩码本地部分**，保留域名：`b***@y.com`；账号标签优先用 alias 或 `Account-N` | 永不 |
| (3) 日志、诊断导出、事件 `message` 字段 | **掩码**（同 2）；用量失败 context 仅 slot（ref: oauth.py paste-safe） | 仅 `sha256:` 指纹前 12 位 |

Quarantine 通知示例（层 2）：“Account-3 的登录已失效，请用该账号登录 Claude Code 后在 App 中重新添加。”——不放完整 email；详情在应用内 UI（层 1）展示。

**退出确认**：引擎运行中退出 App → "自动切换将在应用退出后停止。用量监控与自动轮换将不可用。"（选项：退出 / 取消）。

### 9.4 仪表盘渲染规则（与核心共享）

进度条文案/颜色完全由核心事件与 Snapshot 数据驱动，三端不写自己的格式化逻辑——`countdown`/`clock`/pace 字段由核心算好（ref: `fresh_reset_strings` 的"渲染期重算"语义在核心内完成）。哨兵状态文案用核心提供的固定字符串（对齐 ref: `SENTINEL_NOTES`）。页脚绑定 `EngineStatus`（下次 tick、冷却、`pause_reason`、`is_leader`）。

---

## 10. 配置与数据存储

### 10.1 `sequence.json`（schemaVersion 跟随 claude-swap）

```json
{
  "schemaVersion": 1,
  "accounts": { "1": { "email": "...", "uuid": "...", "organizationUuid": "...",
                       "organizationName": "...", "added": "...", "alias": "dev" } },
  "sequence": [1, 2],
  "activeAccountNumber": 1,
  "lastUpdated": "2026-07-31T08:00:00Z"
}
```

写入是切换事务的提交点（§5.2）。读侧宽容（坏 JSON → None + 告警）、写侧原子 + 校验。

### 10.2 `settings.json`（schemaVersion 1，与 claude-swap 同构）

`autoswitch` 节（§7.2 全部键，含增量 `enabled`）+ `ui` 节（`theme`: dark/light/auto——claude-swap 已有此键，GUI 直接沿用）。claude-switch 专有键放**新节**（如 `gui`）或独立文件 `gui-settings.json`，增量字段对 cswap 无害（其读写保留未知键，ref: settings.py "Unknown keys survive a round trip"）。

### 10.3 `autoswitch_state.json`（schemaVersion 1）

```json
{
  "schemaVersion": 1,
  "cooldown": { "lastSwitchAt": "..." },
  "quarantine": {
    "3": {
      "email": "...",
      "reason": "invalid_grant",
      "at": "...",
      "refreshTokenFingerprint": "sha256:..."
    }
  },
  "leadership": {
    "ownerApp": "claude-switch",
    "ownerPid": 12345,
    "heartbeatUntil": "2026-07-31T08:00:30Z"
  }
}
```

读-改-写走专用文件锁 `.autoswitch_state.lock`（多进程安全，ref: `_mutate_state`）。`leadership` 为增量字段，缺省=无领导。

### 10.4 其它

- `cache/usage.json`（schemaVersion 2）：last-good 测量 + 抓取/退避/节奏状态；哨兵状态**不落盘**（每次 collect 现算现叠，ref: usage_store.py docstring）。
- `stash/`：被置换 live 凭据的安全副本，0600。
- 日志：`<backup_root>/logs/claude-switch.log`，滚动（5 个 × 2MB），脱敏规则见 §11.2。
- GUI 专有：`gui-settings.json`（窗口位置、通知开关、自启状态、主题覆盖）。

---

## 11. 安全设计

### 11.1 凭据不落明文（超出既有契约的部分）

- macOS：备份凭据优先 Keychain；`.enc` 仅是 base64（与 claude-swap 相同，属降级兼容路径，非加密）——文档中对用户明示这一点。
- Windows/Linux：`.enc` base64 文件 + 0600/用户 ACL。**决策：v1 保持与 claude-swap 一致（base64，不加密）**，理由：与 cswap 双向兼容是更高优先级目标，且威胁模型（本机同用户读取）下 DPAPI/keyring 加密增益有限。作为 Open Question 记录 v2 引入 OS 加密存储的选项（§16）。

**曾考虑的替代**：Windows DPAPI / Linux libsecret 加密 `.enc`——提供静态磁盘窃取防护，但破坏与 cswap 的字节级互通，且同用户恶意进程仍可读。**拒绝于 v1，可逆于 v2**（读侧可同时认 base64 与加密信封）。

### 11.2 日志脱敏

- 任何日志/错误消息/事件 `message` 中**绝不出现** access token、refresh token、API key 原文；必要时只出现 `sha256:` 指纹前 12 位。
- 用量失败日志 context 只带 slot 号不带 email（ref: oauth.py `_log_usage_failure` "no email: paste-safe for public issues"）；GUI 诊断导出（"复制诊断信息"按钮）做同样的 email 脱敏（保留域名，掩码本地部分）。
- OS 通知与应用内 UI 分层见 §9.3。
- `tracing` 宏层加编译期 lint：禁止对 `SecretString` 类型实现 `Display`（`secrecy` crate 包装凭据字符串，内存中清零）。

### 11.3 macOS Keychain 访问的签名要求

- App 必须**稳定签名身份**（Developer ID，同 team ID + bundle ID）：Security framework 对 generic password 的默认 ACL 按"创建者签名身份"放行免提示访问；重签名/换身份会导致每次访问弹授权框。
- 与 `security` CLI（cswap）创建的 item 互操作：cswap 创建的备份 item 的 ACL 不含我们的 App——首次读取会触发一次性授权弹窗（"始终允许"）。缓解：检测到既有 `claude-swap` item 时引导页文案预告弹窗；迁移时采用"读→用我们的身份重写"使后续免提示。
- 不启用 keychain-access-groups entitlement（无需跨 App 共享，避免不必要的权限面）。
- Claude Code 自己的 `Claude Code-credentials` item 归 Claude Code 签名身份所有：我们读写它必然需要用户授权一次。这与 cswap（security CLI）现状一致，属平台约束，在首次启动向导中显式告知。

### 11.4 其它

- HTTPS：rustls + 证书固定不做（端点证书轮换风险大于收益），但强制 TLS1.2+ 与系统/内置根证书。
- 锁文件与临时文件 0600/0700；临时文件与目标同目录（防跨文件系统 rename）。
- FFI 边界校验：所有来自 GUI 的字符串（email、alias、slot、导入路径）在核心内重新校验（email 正则、alias normalize、slot 为正整数、路径穿越拒绝）——不信任 FFI 调用方（ref: transfer.py）。

---

## 12. CI/CD

### 12.1 构建矩阵（GitHub Actions）

| Job | Runner | 产物 |
|---|---|---|
| `core-test` | ubuntu / macos / windows | `cargo test`（三平台）、`cargo clippy -D warnings`、`cargo fmt --check` |
| `ffi-build` | macos-14（universal2） | `libclaude_switch.dylib`（arm64+x86_64 lipo）+ Swift 绑定 + `.xcframework` |
| `ffi-build` | windows-latest | `claude_switch.dll` + `.lib` + C 头 + C# 封送层编译 |
| `core-build` | ubuntu | `libclaude_switch.so` / `.a`（供 GTK App） |
| `gui-mac` | macos-14 | `.app` → 公证 → `.dmg` |
| `gui-win` | windows | MSIX 包（签名） |
| `gui-linux` | ubuntu | `.deb` / `.rpm` / Flatpak manifest；`cargo build --release` |
| `contract-test` | 三平台 | FFI 契约测试（§13.2）与 Python 行为对拍（§13.1） |
| `interop-test` | ubuntu + macos | M1 跨实现对拍（PR 12） |

Windows 先行验证（**KD-11**）：PR 阶段必过门禁为 `core-test`（三平台）+ `interop-test`（M1 后）+ `gui-win`；macOS/Linux GUI job 先以 `continue-on-error` 接入，M2/M3 里程碑转为必过。

### 12.2 签名与公证

- **macOS**：Developer ID Application 签名（codesign + hardened runtime + 指定 entitlements），`notarytool` 公证后 stapler 装订。证书存 GitHub Secrets（base64 p12）。
- **Windows**：MSIX + EV 代码签名证书（或 Azure Trusted Signing，推荐——无需本地 HSM，REST 集成进 CI）。
- **Linux**：deb/rpm 仓库 GPG 签名；Flatpak 走 Flathub 流程。

### 12.3 自动更新

| 平台 | 机制 | 说明 |
|---|---|---|
| macOS | **Sparkle 2** | 事实标准；appcast.xml 托管在 GitHub Releases；EdDSA 签名更新包 |
| Windows | **MSIX 自带 App Installer + appinstaller 文件** | 免自研；发布时更新 `.appinstaller` 指到新 MSIX |
| Linux | 包管理器原生（apt/dnf/Flathub）+ 应用内"新版本提示"（读 GitHub Releases API，只做提示不自动装） | Flatpak 由商店更新 |

**曾考虑**：自研更新器 / 通用 electron-updater 风格——拒绝（攻击面与签名复杂度，**KD-9**）。全部用平台原生/事实标准方案。更新通道：stable + beta（appcast/appinstaller 各一套 URL；UA 不区分，§6.1）。

### 12.4 发布流程

`tag vX.Y.Z` → CI 构建全部产物 → 校验（契约测试 + 签名验证）→ 打 GitHub Release（草稿）→ 人工确认 → 发布 → appcast/appinstaller 更新 PR 自动创建 → 合并即推送给用户。版本号 semver；核心与三端 GUI 同仓同版本（**KD-10**）。

---

## 13. 测试策略

### 13.1 核心等价测试（以 claude-swap `tests/` 为蓝本）

ref: `tests/` 下 33 个测试文件即行为规格。移植策略：

1. **黄金路径对拍**：将 `test_switcher.py`、`test_autoswitch.py`、`test_credentials.py`（隐含于 swap/switcher 测试）、`test_poll_policy.py`、`test_usage_store.py`、`test_settings.py`、`test_locking.py`、`test_claude_locks.py`、`test_paths.py`、`test_json_output.py` 逐文件对应为 Rust 集成测试（`crates/core/tests/`）。关键用例一比一映射并注释溯源（如 `// ref: test_autoswitch.py::test_quarantine_released_on_credential_replace`）。
2. **可注入的测试替身**：时钟（`clock: Box<dyn Fn() -> f64>`，对齐 Python 版 `clock` 参数）、HTTP（`oauth` 依赖 `HttpClient` trait，mock 响应含 429/Retry-After/invalid_grant 剧本）、文件系统沙箱（`tempfile` + `backup_root_override`）、Keychain（`SecretStore` 内存实现）。
3. **跨实现对拍（CI job，M1）**：同一临时目录上先跑 `cswap` 命令再跑 claude-switch 操作，断言磁盘状态（`sequence.json`、`.enc` 内容、state 文件）字节级一致——兼容性决策（§4.5）的可执行证明。**断言用户可见结果与磁盘状态，不断言 `SwitchTransaction` 结构体字段同构**（§3.2）。
4. **并发集成测试**：慢 switch（`spawn_blocking` 内 sleep）期间连续 `snapshot()`；双 `switch_account` job 串行完成；领导租约抢占。

### 13.2 FFI 契约测试

- UniFFI 层：`uniffi::generate_scaffolding` 编译期保证 + Swift 侧 XCTest 冒烟（init → snapshot → 事件订阅 → shutdown）。
- C ABI 层：C# xUnit 测试跑完整 happy path（init/snapshot/switch dry-run/事件回调收到 `poll` 事件）+ JSON schema 断言（事件三键 `schemaVersion/event/ts` 存在、未知字段容忍）+ 回调内字符串拷贝生命周期测试。
- 事件流压力测试：快速 1000 事件不丢序；强制溢出时仅 Poll 丢失且出现 `SnapshotUpdated`；`JobFinished` 计数 = 提交 job 数。

### 13.3 GUI 测试边界

GUI 层只测**纯视图逻辑**（进度条映射、导航状态机）与**黄金路径 UI 测试**（macOS XCUITest / Windows WinAppDriver 可选，范围限于向导与切换确认两个流程）。业务断言一律下沉核心。GUI 测试使用核心的内存 `SecretStore` + mock HTTP，不碰真实 Keychain/网络。共享 `golden_snapshot_v1.json`。

### 13.4 平台特有测试

- macOS Keychain 契约测试：对齐 `test_macos_keychain_contract.py`——在 CI runner 上验证 Security framework 的 rc-44/锁定行为映射正确。
- proper-lockfile 互操作测试：用 `proper-lockfile`（npm）在测试里持锁，验证我们的目录锁正确等待/超时（staleness 60s/10s 语义）。
- 锁路径矩阵：§5.1 三行环境。
- Legacy backup 迁移：§4.1 flag 语义。

**PR 5 / PR 6 测试所有权**：

| PR | 必须绿灯的测试 | 说明 |
|---|---|---|
| 5 | `SecretStore` 内存 mock 全契约；`file` 后端（.enc-wins、.prev、strict delete、共享字段合并）；credentials 路由状态机（能力缓存/60s 冷却/pin file mode）在 **file 后端** 下的切换路径 | macOS Keychain 仅 stub（`unimplemented` 或 always-fail → 文件降级路径必须测到） |
| 6 | Keychain 真实/契约测试（rc-44、账号名解析、三服务名）；**作为 switcher macOS CI 变绿的门禁**——PR 8 的 macOS job 依赖 PR 6 契约 | 避免 PR 5 凭据测试在 Keychain 落地后整表重写 |

---

## 14. 风险登记册

| # | 风险 | 等级 | 缓解 |
|---|---|---|---|
| R-1 | Anthropic 调整 `/api/oauth/usage` 预算/形状（poll_policy 常数基于 2026-07-11 实测） | 中 | 常数集中于 `poll_policy.rs` 单文件；429 反应逻辑对未知形状保守（AIMD 天然退让）；遥测日志健康不变量（稳态零 429）写入诊断导出 |
| R-2 | Claude Code 改变锁协议/凭据存储位置（协议对 2.1.218 bundle 验证） | 高 | 锁参数（staleness/路径）常量化；版本探测 + 灰度：检测到协议不符（锁形态异常）时降级为"仅自有锁 + 警告"；跟踪 CC changelog 纳入发布检查单 |
| R-3 | Keychain ACL 弹窗破坏体验（cswap 遗留 item、重签名） | 中 | §11.3 的稳定签名 + 向导预告 + 一次性重写迁移 |
| R-4 | refresh token 一次性语义下持久化失败 → lineage 死亡 | 中 | 严格移植"persist-first"顺序；失败响亮告警 + 事件通知 GUI 弹系统通知 |
| R-5 | tokio 持锁跨 await 死锁/锁内网络 I/O / blocking 池饿死 | 中 | 编译期分层（switcher 无 HTTP）；事务整体 `spawn_blocking`；L0–L4 锁序（§2.2）；`max_blocking_threads(16)`；集成测试 snapshot-during-switch |
| R-6 | FFI 事件风暴阻塞 UI（每账号每 tick 多事件） | 低 | 事件通道容量 + 仅 Poll 合并降级 + 关键事件 side buffer（§8.4）；GUI 侧 diff 渲染 |
| R-7 | 三平台 GUI 并行开发拖慢核心迭代 | 中 | Windows 先行（**KD-11**）；macOS/Linux GUI 在 FFI schema v1 冻结后开工；共享 golden Snapshot，macOS **不**依赖 Windows UI PR |
| R-8 | WinUI 3 托盘/MSIX/自启部署复杂度 | 中 | packaged-only + StartupTask-only；`Shell_NotifyIcon` 自研薄封装（§9.2）；unpackaged F5 开发环 |
| R-9 | 自研更新器安全面 | — | 已决策：不自研（**KD-9**，§12.3） |
| R-10 | 与 cswap 共用数据目录时的版本交错写（老 cswap 不认识新字段） | 低 | 共享文件只做增量演进；两工具写路径均保留未知键（已验证 cswap 行为，ref: settings.py） |
| R-11 | relm4/GTK4 托盘支持在部分 Linux 桌面（GNOME 无托盘）退化 | 中 | 主入口即窗口（仪表盘），托盘用 StatusNotifierItem 可选增强；无托盘环境退化为普通应用窗口 + 关闭到后台提示 |
| R-12 | Windows Defender 等对未积累信誉的签名二进制误报 | 低 | Azure Trusted Signing + 分发前提交 Microsoft 误报申报；MSIX 商店渠道备选 |
| R-13 | 双引擎 thrash（本 App + `cswap auto`/menubar 或残留第二实例）导致双倍 poll 与抢切换 | 高 | 单实例 mutex + leadership lease（**KD-12**，§2.2）；向导/设置文案；observe-only 降级；PR 9b |

---

## 15. Key Decisions

稳定编号格式 **KD-N**。文内交叉引用必须使用此前缀。

| ID | 决策 | 选项与取舍 |
|---|---|---|
| **KD-1** | 单 crate workspace + 薄 FFI crate：`claude-switch-core` + `claude-switch-ffi`（cdylib+staticlib） | 核心一处实现三端复用；FFI 隔离使核心测试不需 UniFFI 脚手架。gui-mac/gui-win 为仓内非 Cargo 工程（§3.1）。 |
| **KD-2** | 与 claude-swap 数据完全兼容（同路径/同格式/同 Keychain 服务名）；`.enc` 保持 base64 | 零迁移 + 交替使用切换路径。代价：schema 演进需读侧迁移；加密存储推 v2。 |
| **KD-3** | macOS Keychain 用 `security-framework` crate 而非 `security` CLI | 性能与 argv 安全。代价：需稳定签名身份（§11.3）。 |
| **KD-4** | 自动切换引擎内嵌 GUI 进程（tokio task），**无守护进程** | **选项**： (a) 内嵌托盘进程（选中）；(b) 用户级 Windows Service / launchd agent / systemd --user；(c) 独立 daemon + 薄 GUI。**(b)(c) 拒绝理由**：服务安装权限/登录会话隔离使 Keychain 与 `~/.claude` 访问复杂；调试与卸载成本高；产品已定“退出即停自动切换”。**可逆性**：若未来需要 headless auto，可抽 Engine 为独立 bin，文件 schema 不变。 |
| **KD-5** | 事件流推送 + Snapshot 拉取混合 UI 驱动 | 首屏/重连 Snapshot；稳态事件；仅 Poll 可合并。 |
| **KD-6** | Windows 用 **JSON-over-the-wire C ABI**；macOS 用 **UniFFI→Swift** | **FFI 栈选项**：(a) UniFFI 全平台——无官方 C# 后端，拒绝；(b) cxx ——偏 C++ 互操作，C#/Swift 不友好；(c) flutter-rust-bridge ——绑定 Flutter 产品形态，与原生 SwiftUI/WinUI 目标冲突；(d) 分平台：UniFFI(Swift)+JSON C ABI(C#)+直接链接(Linux)（**选中**）。JSON C ABI vs protobuf：protobuf 多一套 schema 与生成物，跨语言收益在此规模不划算；serde 类型已是事实源。 |
| **KD-7** | 三锁顺序与 proper-lockfile 参数逐值移植（60s/10s、touch 3s、超时 9s、oauth_refresh→legacy） | 与 CC 互操作安全依赖实测值，不做“优化”。路径按函数规则解析，不硬编码 home（§5.1）。 |
| **KD-8** | 活动账号凭据永不主动刷新；过期走 idle-hold（30min 封顶） | 与 cswap 一致；活动 token 归 Claude Code。 |
| **KD-9** | 自动更新全平台用原生/事实标准（Sparkle 2 / MSIX App Installer / 包管理器） | 不自研更新器。 |
| **KD-10** | Monorepo 同版本发布 | FFI schema 同步演进零协调成本；`VERSION` 文件为单一事实源。 |
| **KD-11** | **Windows GUI 先行**作为产品与 CI 门禁策略；macOS/Linux GUI 在 FFI v1 冻结后并行 | 降低三端同时烧核心的风险。M1 互操作证明排在完整 Windows 仪表盘之前（§17）。 |
| **KD-12** | **单实例 mutex + autoswitch 领导租约**；禁止与 `cswap auto`/menubar 同时自动切换 | 见 §2.2。文件锁不够；必须进程级互斥 + 跨产品 lease/文案。 |
| **KD-13** | v1 裁剪 session 模式/TUI/mappings/多机同步/move-swap GUI/job cancel | 聚焦仪表盘主路径；transfer 导入导出**保留在 v1**。 |
| **KD-14** | Linux GUI = **relm4 + GTK4 + libadwaita** | **选项**：(a) relm4（选中，Elm 架构贴近 SwiftUI 状态流，纯 Rust 与 core 同语言）；(b) egui——即时模式，系统托盘/通知/a11y 弱；(c) Tauri——再引入 Web 栈与双运行时，与“原生三端”冲突。可逆：core 不依赖 GUI。 |

---

## 16. Open Questions

### 已决策（产品确认 · 2026-07-31）

| # | 问题 | 决策 |
|---|---|---|
| OQ-2 | macOS 分发渠道 | **仅 Developer ID 直发** + Sparkle；不上 Mac App Store（沙盒限制 `~/.claude` / Keychain） |
| OQ-3 | Linux 托盘 | **`ksni`（StatusNotifierItem，纯 Rust）** |
| OQ-4 | 遥测/崩溃上报 | **零遥测**；崩溃日志靠用户手动导出，无 Sentry/行为埋点 |

### 仍开放（不阻塞 v1 开工）

1. **v2 是否引入 OS 加密备份存储**（Windows DPAPI / Linux secret-service）以替代 base64 `.enc`？与 cswap 的双向兼容将变成单向迁移，需要用户沟通策略。
2. **`cswap run` 会话模式的 GUI 形态**（v2）：是否做"以指定账号启动 Claude Code"的入口，还是先保持 CLI 独占？

（原 OQ-6 多机同步已决策为 v1 非目标，见 §1 / **KD-13**。若 v2 研究安全的导出迁移 UX，另开研究项，不作“同步”。）

---

## 17. PR Plan

> 顺序即依赖序；每个 PR 可独立评审合并。M 标记为里程碑门禁。

| # | 标题 | 涉及文件/组件 | 依赖 | 变更简述 |
|---|---|---|---|---|
| 1 | chore: workspace 骨架与 CI 三平台 core-test | `Cargo.toml`、`VERSION`、`crates/core`（空 lib）、`.github/workflows/ci.yml` | — | workspace、toolchain、fmt/clippy/test 三平台门禁 |
| 2 | feat(core): paths + fsutil + errors + legacy backup 迁移 | `paths.rs`、`fsutil.rs`、`errors.rs` | #1 | 三平台路径、`Platform::detect`、CLAUDE_CONFIG_DIR、legacy config、XDG、`migrate_legacy_backup_dir` flag 语义、原子写/symlink 穿透 |
| 3 | feat(core): locks（FileLock + proper-lockfile） | `locks/`、`tests/locks.rs` | #2 | 自有文件锁 + CC 目录锁；**路径矩阵测试**（默认 / CLAUDE_CONFIG_DIR / legacy config）；npm 互操作 |
| 4 | feat(core): models + sequence + settings | `models.rs`、`sequence.rs`、`settings.rs` | #2 | sequence/settings 读写、SETTING_SPECS、`autoswitch.enabled` 键、宽容读/严格写 |
| 5 | feat(core): SecretStore 抽象 + file 后端 + credentials 路由 | `keychain/`、`credentials.rs` | #3 #4 | 内存 mock + file 后端**完整测试**；路由状态机；macOS Keychain **stub only**（测试所有权见 §13.4） |
| 6 | feat(core): macOS Keychain（security-framework） | `keychain/macos.rs`、契约测试 | #5 | 三服务名、rc-44、账号名解析；**契约测试作为后续 macOS switcher CI 门禁** |
| 7 | feat(core): oauth + poll_policy + usage_store | `oauth.rs`、`poll_policy.rs`、`usage_store.rs`、`pace.rs` | #5 | HttpClient trait、usage/profile/refresh、节奏常数、claim/退避；**UA 单测锁定** |
| 8 | feat(core): switcher 切换事务 | `switcher.rs` | #3 #5 | 三锁事务、回滚、共享字段、stash；等价测试（磁盘结果，非结构体同构） |
| 9 | feat(core): autoswitch 引擎 + 事件 + leadership | `autoswitch/`、`lib.rs` Engine 门面 | #7 #8 | tick 状态机、quarantine/冷却/迟滞/idle-hold、CoreEvent、**领导租约**、observe-only |
| 9b | feat(core): 单实例锁 + 双引擎策略接线 | `instance.rs`、引擎 init 钩子 | #9 | 命名 mutex/flock；lease 读写；`claim_leadership(force)`；ConfigWarning 文案键；集成测试双进程 |
| 10 | feat(ffi): UniFFI 接口 + 事件回调 + 完整类型目录 | `crates/ffi`（UDL、build.rs）、`tests/fixtures/golden_snapshot_v1.json`、`enum_wire_v1.json`、Swift 冒烟 | #9 #9b | §8.0/§8.2 全部接口（含 import/export、`claim_leadership`）；kebab wire 契约测试；schema 握手；事件降级 |
| 11 | feat(ffi): C ABI + C# 封送层 + 契约测试 | `ffi/src/c_abi.rs`、`include/claude_switch.h`、`gui-win/ClaudeSwitch.Core/` | #10 | §8.3 全契约、method 表、GCHandle 样例测试、JSON schema |
| 12 | **M1** test: 跨实现对拍（cswap ↔ claude-switch） | `tests/interop/`、CI `interop-test` | #9 | 同一 backup_root 交替操作，磁盘状态一致；含 Linux legacy 迁移场景；**在完整 GUI 之前合并** |
| 12b | feat(core+ffi): transfer 导入导出端到端 | `transfer.rs`、FFI 已暴露方法的集成测 | #10 #12 | FORMAT_VERSION=1 读写、路径穿越拒绝、与 cswap `.cswap` 互操作 |
| 13 | feat(gui-win): 薄壳 + 托盘 + 单实例 + Snapshot 绑定 | `gui-win/`（App、TrayIconService、P/Invoke、单实例） | #11 #12 | **薄壳**：托盘、`Shell_NotifyIcon`、主窗口显示 Snapshot、深浅色；**不做**完整账号管理抛光。证明 FFI 在真 UI 线程可用 |
| 14 | feat(gui-win): 仪表盘 + 切换流 + 账号管理 + 导入导出 | `gui-win/` Dashboard、AccountMgmt、ViewModels | #13 #12b | 列表/切换确认/别名/禁用/删除/import/export、事件驱动刷新、PII 分层通知 |
| 15 | feat(gui-win): 首次启动向导 + 通知 + 退出确认 | `gui-win/Onboarding/`、`Notifications` | #14 | 向导 5 步、双引擎警告、toast（掩码 email） |
| 16 | feat(gui-win): 设置页 + MSIX 打包签名 + 自动更新 | `gui-win/Settings/`、`Package.appxmanifest`、CI `gui-win` | #15 | autoswitch UI；observe-only 时“强制抢领导”→`claim_leadership({force:true})`+确认框；StartupTask；签名；appinstaller；unpackaged Debug 文档 |
| 17 | **M2** feat(gui-mac): SwiftUI MenuBarExtra App | `gui-mac/`（App、MenuBarExtra、Dashboard） | **#10**（+ golden fixtures）；**不**依赖 #13/#16 | UniFFI 绑定、菜单栏 popover、仪表盘对齐 **IA 夹具**（非对齐 Windows 代码） |
| 18 | feat(gui-mac): 向导/账号管理/通知/Sparkle/公证 | `gui-mac/Onboarding/`、Sparkle、CI 公证 | #17 | SMAppService、UNUserNotification、import/export UI、appcast |
| 19 | feat(gui-linux): relm4 GTK4/libadwaita App | `crates/gui-linux/` | #9 #10 | 仪表盘 + 窗口主入口 + SNI 托盘可选、XDG autostart、import/export |
| 20 | **M3** release: v1.0 发布管线 | appcast/appinstaller 自动化、发布 runbook | #16 #18 #19 | tag → 产物 → 签名 → Release；macOS/Linux CI 转必过 |

**里程碑**：

- **M1（#12）**：跨实现兼容可执行证明 —— **阻塞**完整 Windows 仪表盘（#14+）与发布叙事。
- **M2（#17）**：macOS GUI 主路径。
- **M3（#20）**：v1.0 发布。

---

*本文档基于对 `reference/claude-swap` 源码（credentials.py、switcher.py、autoswitch.py、oauth.py、claude_locks.py、locking.py、json_output.py、poll_policy.py、usage_store.py、settings.py、paths.py、models.py、transfer.py、macos_keychain.py）的直接阅读撰写；所有标注 `(ref: …)` 的行为描述均可溯源。v1.1 回应首轮评审 Issues 1–24；v1.2 回应再评审 Issues 25–30（wire kebab、拆除顺序、claim_leadership、tokio API、Swift 重入文案、quarantine u32）。*
