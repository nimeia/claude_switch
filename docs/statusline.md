# 终端状态栏（statusline）

让 Claude Code 终端底部那一行，回答 claude-switch 独有的问题：
**这个终端现在跑在哪个账号上，它的额度还剩多少。**

目标是「三档预设，开箱即用」，不是再做一个可编辑的状态栏引擎。

## 1. 目标与非目标

**目标**

- 三档预设：`lean` / `standard` / `full`，一个复选框 + 一个三选一，没有第四个旋钮。
- 并行会话下每个终端显示**自己**的账号（这是 Claude Code 自带状态栏给不出的信息）。
- 零网络、零引擎、零锁：渲染进程只读文件，预算 < 50ms。
- 装与卸都可逆：不吃掉用户已有的 `statusLine` 配置。

**非目标**

- 不做 widget 编排、颜色编辑、powerline、Nerd Font。要这些的用户去用 ccstatusline，两者可以共存（见 §6.3）。
- 不做 per-project 配置。
- 状态栏文本不随界面语言切换（见 §11）。

## 2. 为什么不直接调 ccstatusline

评估过「预设即一份 ccstatusline 配置模板 + `statusLine.command = npx -y ccstatusline@latest`」这条路，放弃的原因：

| 问题 | 说明 |
|---|---|
| 依赖 | 需要 Node/Bun。本项目的卖点是「一个 exe，不装运行时」 |
| 成本 | 每次重绘一次进程：`npx` 实测约 1000ms，`bunx @latest` 约 630ms（ccstatusline README 自测数据）。我们的目标是 < 50ms |
| 它给不出账号 | ccstatusline 不知道 `CLAUDE_CONFIG_DIR` 背后是哪个 slot、别名是什么、其它账号还剩多少 |

ccstatusline 在这次设计里的价值是**经验来源**：它的 stdin schema（`src/types/StatusJSON.ts`）、
`settings.json` 合并策略、Windows 路径引号、以及「配置文件非法时绝不落盘」这几条，下面直接采纳。

## 3. 三档预设

一条语义阶梯：**我是谁 → 我还剩多少 → 接下来怎么办**。

### 3.1 `lean`（精简）

纯 ASCII，一行，约 30 列。给窄终端和不想被状态栏占视线的人。

```
#2 work · 5h 38% · Sonnet 5 high
```

| 段 | 来源 |
|---|---|
| `#2 work` | slot + 别名（无别名时用邮箱，受 PII 设置约束，§11） |
| `5h 38%` | 5 小时窗口已用 |
| `Sonnet 5 high` | 模型 + 思考强度（`effort.level`） |

### 3.2 `standard`（标准，默认）

一行，约 80 列。加上 7 天窗口、上下文占用、目录与分支。

```
#2 work · 5h ████░░░░░░ 38% · 7d 12% · ctx 24% · Sonnet 5 high · claude_switch (master)
```

- 进度条固定 10 格，`█`/`░`，不需要 Nerd Font。
- 百分比按阈值着色：< 50 绿、< 80 黄、≥ 80 红；其余段落 dim。
- 目录只显示叶子名；分支直接读 `.git/HEAD`（§4.4）。

### 3.3 `full`（完整）

在 `standard` 之上，再加一段只有 claude-switch 给得出的信息。

**装得下就一行，装不下才折行**（§3.4）。今天的终端通常有 120～200 列，
两段并排绰绰有余：

```
#2 work · 5h ████░░░░░░ 38% · 7d 12% · ctx 24% · Sonnet 5 high · claude_switch (master) · 5h resets 14:30 · 7d resets Fri 09:00 · spare #3 ops 8% · auto 90% · $1.24 42m
```

窄终端下退回两行，第一行仍与 `standard` 一致：

```
#2 work · 5h ████░░░░░░ 38% · 7d 12% · ctx 24% · Sonnet 5 high · claude_switch (master)
5h resets 14:30 · 7d resets Fri 09:00 · spare #3 ops 8% · auto 90% · $1.24 42m
```

| 段 | 含义 |
|---|---|
| `5h resets 14:30` | 窗口重置时刻（复用 `feat(card): say when each window resets` 的同一套算法） |
| `spare #3 ops 8%` | **自动切换会挑中的下一个账号**及其已用量；没有可用备选时整段消失 |
| `auto 90%` | 自动切换开启及阈值；关闭时显示 `auto off` |
| `$1.24 42m` | 本次会话成本与时长（来自 stdin `cost`） |

### 3.4 `full` 什么时候折行

状态栏画在**每一屏的底部**：少占一行，就是给正文多留一行。所以换行是兜底，不是形态——
只要这一行放得下，就不折。

判定很直白：两段加中间的 ` · ` 的**可见宽度** ≤ 终端列数，就并成一行。「可见宽度」不算 ANSI
转义序列，中日韩字符按两格算（别名或目录名可能是中文）——进度条的 `█`/`░` 和 `·` 都是一格。

终端有多少列，只有跑在终端里的进程问得到，按权威性从高到低：

| 来源 | 说明 |
|---|---|
| `--width <cols>` / `CS_STATUSLINE_WIDTH` | 显式指定。探测不到时的出路；填 `0` 就是「永远两行」 |
| `COLUMNS` | 父进程导出了才有 |
| 控制台本身 | Windows 上打开 `CONOUT$` 调 `GetConsoleScreenBufferInfo`，取**窗口**宽而不是缓冲区宽 |

几条细节：

- **stdout 是管道**（Claude Code 读我们打印的东西），所以宽度不能从 stdout 问，只能问控制台设备。
  `CONOUT$` 在 ConPTY（Windows Terminal、VS Code）下同样有答案，需要 `GENERIC_READ | GENERIC_WRITE`
  才不会被拒；只读打开在部分宿主上返回 `ERROR_ACCESS_DENIED`。
- **问不出来就保持两行**。猜一个偏大的宽度会在别人终端里真的换行，而换行后的两行比我们自己画的
  断行更难看。
- 留 2 列余量：Claude Code 把这行画在它自己的框里，折到最后一列就会被它再折一次。
- 非 Windows 平台暂时只认 `--width` / `COLUMNS`，等对应 GUI 落地时再补 `TIOCGWINSZ`。
- GUI 预览走 `NO_WIDTH_LIMIT`：设置区不是终端，没有「列数」，它该展示的是终端里的样子。

## 4. 数据来自哪里

四个来源，全部是本地读取。

### 4.1 Claude Code 的 stdin JSON（权威、实时）

以 `reference/ccstatusline/src/types/StatusJSON.ts` 为准，我们只取：
`model.display_name`、**`effort.level`**、`workspace.current_dir`、`context_window.used_percentage`、
`cost.total_cost_usd`、`cost.total_duration_ms`，
以及 **`rate_limits.five_hour` / `rate_limits.seven_day`**（`used_percentage` + `resets_at`）。

思考强度（`low`/`medium`/`high`/`xhigh`/`max`）**只从这里取**。ccstatusline 还会回退去扫 transcript、再回退读 `settings.json` 的默认值；前者正是本渲染器要避开的每次重绘成本，后者是「配的默认」而不是「这个会话实际在用的」——**一个看着像真的错答案比没有更坏**。未认识的值原样透传（上游新增一档无需改这里），只有 `none`/`default`/空 被丢弃，并截断到 12 字符以免一个奇怪的 payload 把行擑宽。

`rate_limits` 是关键发现：**当前账号的额度不需要我们去查**，Claude Code 自己会给，而且比轮询快照更新。
它是 optional/nullable（旧版本、API Key 账号没有），所以有 §4.3 的回退。

### 4.2 「这个终端是哪个账号」

```
CLAUDE_CONFIG_DIR 已设置 → <该目录>/.claude.json → oauthAccount.emailAddress
未设置                   → ~/.claude.json        → oauthAccount.emailAddress
```

邮箱再到 `<backup_root>/sequence.json` 换 slot 号与别名。这正是 `session.rs::bootstrap` 写进去的身份，
所以并行会话天然正确——每个终端看到的是它自己那个账号，不是「当前默认登录」。

### 4.3 快照 `<backup_root>/statusline.json`（引擎写，状态栏读）

引擎每次轮询 tick 后原子写入，内容是它本来就有的东西，**不含任何 token**：

```json
{
  "schemaVersion": 1,
  "updatedAt": "2026-09-16T10:20:31.000+08:00",
  "activeSlot": 2,
  "spareSlot": 3,
  "autoSwitch": { "enabled": true, "threshold": 90.0 },
  "hideEmail": false,
  "accounts": [
    { "slot": 2, "email": "a@b.com", "alias": "work", "state": "ok",
      "fiveHour": { "pct": 38.2, "resetsAt": "2026-09-16T14:00:00+08:00" },
      "sevenDay": { "pct": 12.0, "resetsAt": "2026-09-19T09:00:00+08:00" } }
  ]
}
```

窗口沿用 `crate::usage` 的形状（`pct` + RFC3339 `resetsAt`），`state` 是 `UsageStatus`
的 kebab 值，回答「为什么这个账号没有数字」。

`spareSlot` 由引擎算好写进来，**不让渲染端自己排序**：那是自动切换的策略，两处各算一遍迟早会分叉。
算法是拿真正的排序函数按「活跃账号刚好触到阈值」跑一次（`AutoSwitchEngine::spare_at_threshold`），
所以它给的答案就是下一次真 tick 会给的答案。

三个用途：给 `full` 的 `spare` / `auto` 段；当 stdin 没有 `rate_limits` 时按邮箱回退取本账号的百分比；
以及把 slot 号和别名交给身份段。

**新鲜度**：快照超过 10 分钟未更新（GUI 没开），回退值前加 `~` 标记，`spare` 段整段隐藏——
陈旧的「备用账号还剩很多」比不显示更有害。stdin 来的值永远不打标记。

### 4.4 Git

只读 `.git/HEAD`（以及 worktree 下 `.git` 文件的指向），**不 fork `git` 进程**。
因此没有「几个文件改动」「ahead/behind」这类段——那需要 `git status`，50~200ms，每次重绘都付，不划算。

## 5. 运行形态：一个独立的小二进制

**方案（已采用）**：`crates/statusline` 产出 `cs-statusline.exe`。
它作为内容打进 .NET 单文件包，在用户**首次启用状态栏时**释放到
`<backup_root>/bin/cs-statusline.exe`，`statusLine.command` 指向这个稳定路径。

实测（Windows 11，release 构建）：**751 KB**；连进程创建在内单次约 **28ms**（PowerShell 管道计时，
含 spawn 开销，二进制自身远低于此）。链接器把 core 里用不到的 HTTP/TLS 栈都丢掉了，所以体积没有被 `ureq` 拖大。

为什么不是 `ClaudeSwitch.exe --statusline`：

- `Program.Main` 一进来就 `ApplicationConfiguration.Initialize()` 并抢单实例 mutex，状态栏路径必须在这之前整段绕开，是条脆弱的旁路；
- .NET 单文件 WinExe 每次重绘启动约 100~250ms，比 Rust 二进制慢一个量级；
- 便携 exe 会被用户随手移动/改名，而 `settings.json` 里是绝对路径，一移就断；
- 渲染逻辑留在 Rust core，可与 gui-mac / gui-linux 复用（设计文档 §3.1 的分工）。

代价与处理：

- **释放时机**：只在启用时释放，不在每次启动时写盘。用 `bin/cs-statusline.version` 记版本戳，版本不符才重新释放——
  每次启动都重写一个 exe，正是让杀软对你感兴趣的做法。
- **替换正在运行的 exe**：Windows 不允许删除或覆盖运行中的映像，但允许改名（加载器按句柄而不是路径持有文件）。
  所以顺序是：新文件先写成 `cs-statusline.new` → 旧的 `rename` 成 `cs-statusline.old-<ms>` → `.new` 改名就位 →
  尽力删除 `.old-*`（删不掉说明还在跑，下次调用时扫掉）。版本戳**最后**写，半截安装绝不会自称是这个版本。
- **杀软**：新落盘的未签名 exe 可能被 Defender 拦。与现有 `claude_switch.dll` 自解压同性质，README 的 SmartScreen 段落一并说明。

（若后续决定不引入第二个二进制，`--statusline` 旁路是可退回的备选：把 §6 的命令换成
`"<ClaudeSwitch.exe>" --statusline standard`，其余设计不变。）

## 6. 写入 `~/.claude/settings.json` 的契约

### 6.1 写什么

```json
"statusLine": {
  "type": "command",
  "command": "\"C:\\Users\\me\\.claude-swap-backup\\bin\\cs-statusline.exe\" --preset standard",
  "padding": 0,
  "refreshInterval": 10
}
```

- **档位编码在命令行里**，渲染进程不读任何自有配置：即使我们的 `settings.json` 损坏，状态栏照样出。
- **路径一律加引号**，不做「含空格才加」的判断：Windows 上命令要过 `cmd.exe`，而默认用户目录
  （`C:\Users\First Last\…`）带空格的情况足够常见，条件分支只会留一个等着特定用户去踩的坑。
- `refreshInterval` 只在 Claude Code ≥ 2.1.97 时写，版本用 `ClaudeCli` 探一次即可；核心侧同时校验
  1–60 的取值范围，超出就当作不支持（宁可不写，也不写一个会被对面拒绝的值）。

### 6.2 怎么写

1. 读原文；**解析失败就整个放弃**，提示「`settings.json` 不是合法 JSON，未作修改」。绝不覆盖用户可能手写的文件。
2. 保留所有未知键，只增改 `statusLine`，原子写。
3. 首次写入前备份一份 `settings.json.cswitch-bak`。

### 6.3 已有别的状态栏时

若现存 `statusLine.command` 不是我们的（例如 ccstatusline），**不静默接管**：弹确认框
「当前状态栏由 `npx -y ccstatusline@latest` 提供，替换？」；用户同意才写，并把原值存进
我们自己的 `settings.json → statusline.replaced`。关闭功能时优先恢复该值，没有则删除 `statusLine` 键。

### 6.4 漂移自愈

应用启动时，若本地设置里 `statusline.enabled` 为真，但 `~/.claude/settings.json` 里的命令与当前应写入的不一致：
**认得出是我们写的**（路径指向 `<backup_root>/bin/cs-statusline*`）就直接改写（换目录、升级、改档都走这条）；
认不出则不动，交给 §6.3 的确认流程。

### 6.5 自有设置

`<backup_root>/settings.json` 新增（沿用现有 camelCase + schemaVersion 1）：

```json
"statusline": { "enabled": true, "preset": "standard", "installedCommand": "…", "replaced": null }
```

wire 取值遵守设计文档 §8.0 的 kebab 字母表：`lean` | `standard` | `full`。

## 7. 会话 profile 的传播

`session.rs` 的共享清单里已经有 `settings.json`——每次开会话终端都会把 `~/.claude/settings.json`
重新同步进 `<backup_root>/sessions/<slot>-<email>/`。因此：

- **启用一次，所有按账号打开的终端都有状态栏**，无需逐个 profile 写入；
- 关闭同理会随下次会话启动被抹掉；
- 已经在跑的会话保留旧副本直到重开——这是既有行为，文档里说明即可。

## 8. 失败与降级

状态栏进程的第一条纪律：**永远不要在别人的终端里刷异常**。

| 情况 | 行为 |
|---|---|
| stdin 空 / 非法 JSON | 用能拿到的环境信息渲染；至少给出账号段 |
| 某段数据缺失（无 `rate_limits`、无 git、无快照） | **整段消失**，不输出 `?` 占位 |
| 账号无订阅额度 / API Key 账号 | 只显示账号与模型 |
| 任意 panic | catch-all，打印降级行（模型 + 目录），退出码 0 |
| `NO_COLOR` 有值 | 不输出 ANSI |

预算：无网络、无引擎初始化、不取任何锁（快照与 `sequence.json` 都是只读打开），目标 < 50ms。

## 9. GUI

主窗口「自动化」设置区新增第三行（落地后的样子）：

```
☐ 终端状态栏   显示  ┌────┬────┬────┐   #1 alice · 5h ██░░░░░░░░ 25% · 7d 10% · ctx 24% · Sonnet 5 high · my-project (main)
                     │精简│标准│完整│   （选中格填浅绿，图中为「标准」）
                     └────┴────┴────┘
```

- 三选一是一个单独的 `SegmentedChoice` 控件：**一条外框内分三格**。
  最初用三个 `PillButton` 排在一起，结果读起来就是三个按钮——三条描边彼此打架，还跟旁边的复选框和上两行的字段打架；
  分段控件用一条外框说出「三选一」，而这正是这个设置的本质。不用单选按钮，是因为每个选项就两三个字，
  三个带圆圈的标签要多占好几倍宽度说同一件事。格宽取最宽选项对齐（否则切语言时格子会在鼠标下面移位），
  选中格填色且不画分隔线，←/→/Home/End 可键盘切换，`AccessibleRole` 用 `ComboBox`（读屏会报出控件名 + 当前选项）。
- **预览是真的**：调 `statusline_preview`，渲染代码与安装的二进制同一份，账号和百分比取自真实快照，
  只有模型名/目录/分支是样例。等宽字体（`Theme.FontMono`，Cascadia Mono → Consolas → 通用等宽），
  否则进度条的宽度会骗人；有一条测试断言这个字体确实等宽。
- `full` 在终端里通常是一行（§3.4），预览也就按那个样子渲染；`StatuslineHelper.OneLine` 留作兜底，
  因为设置区每行只有一行的高度。
- 勾选/切档立即生效，走既有 `FlashSettingsSaved`；失败弹 MessageBox，且显示的是**引擎自己的话**
  （从错误 JSON 里取 `message`，而不是「调用失败(2)」加一串 JSON）。
- **启动时后台跑一次** `statusline_heal` 和 `claude --version` 探测：前者要读写文件，后者要起 Node 进程，
  都不该占着画窗口的线程。版本探测结果缓存一次，用户点复选框时就已经在手上了。
- 「隐藏邮箱」勾选时顺手 `set_ui {hideEmail}` 写进引擎设置——渲染器在另一个进程里，看不到 GUI 的 `UiPrefs`。
- 不进 onboarding 向导，保持首次启动的步骤数不变。

## 10. FFI

沿用 `cs_engine_call(method, params)` 的 JSON 分发，方法名跟随既有的 snake_case 惯例：

| method | 作用 |
|---|---|
| `statusline_status` | `{enabled, preset, command?, standing}` |
| `statusline_set` | `{enabled, preset, binaryPath, takeover, refreshInterval}` → 释放二进制 + 写/还原 `~/.claude/settings.json` |
| `statusline_heal` | `{binaryPath, refreshInterval}` → 只在 `standing=drifted` 时改写；启动时调一次 |
| `statusline_preview` | `{preset}` → `{line}`，渲染好的纯文本（按 `NO_WIDTH_LIMIT` 渲染，见 §3.4） |

`standing` 是一个**按优先级排序的单值**，而不是几个 bool：

| 值 | 含义 | GUI 该做什么 |
|---|---|---|
| `settled` | 装好了且是最新的，或干净地没装 | 正常显示 |
| `drifted` | 是我们的，但不是现在该写的（升级、备份根搬家、手改） | `statusline_heal` 自己修 |
| `foreign` | 状态栏归别的工具 | 启用前先弹确认，同意后 `takeover: true` |
| `unreadable` | `settings.json` 解析不了 | 提示用户，什么都不写 |

这几个状态不是互相独立的——解析不了的文件根本说不出状态栏归谁，而 foreign 无论是否同时 drifted 都必须原样不动。
`statusline_set` 在 foreign 且 `takeover=false` 时返回 `Validation` 作为兜底（防竞态），
正常流程是 GUI 先读到 `foreign` 再去问用户。

`binaryPath` 由宿主传入：单文件包把渲染器解压到哪里，只有 .NET 宿主知道（`AppContext.BaseDirectory`）；
DLL 里的 `current_exe()` 拿到的是用户双击的那个便携 exe，不是解压目录。

## 11. 语言与隐私

- **状态栏文本不本地化**：段标签只用 `5h` / `7d` / `ctx` / `auto` 这类符号化短词。理由是终端宽度与
  CJK 全角对齐，中英混排会让进度条抖动；GUI 里的说明文字照常走 `Loc.T`。
- **PII**：状态栏里的账号标识优先用别名，其次 slot 号；需要落邮箱时套用 `Pii` 的掩码规则，并遵守
  设置里的「隐藏邮箱」。快照文件写在 `backup_root`，与 `sequence.json` 同级同敏感度，不含 token。

## 12. 测试

- `crates/core/src/statusline.rs`：渲染是纯函数 `render(preset, stdin, snapshot, now) -> String`，
  三档各配 golden 断言；缺字段、陈旧快照、无订阅、非法 stdin 各一例。
- 折行（§3.4）：够宽时并成一行且文字与两行版逐字相同；刚好够/差一列各一例（断在哪一列是有定义的）；
  可见宽度不数 ANSI、中日韩按两格，因此上色与否折在同一列。
- `settings.json` 合并：保留未知键、非法 JSON 不落盘、接管与还原往返、路径引号。
- 释放逻辑：版本戳命中则不写盘；`.old` 改名路径。
- C# 侧：预览文本非空且不含 ANSI；勾选 → 设置文件内容的往返。

## 13. 落地顺序

1. **core**：`statusline/mod.rs`（预设 + 渲染 + 快照类型）、引擎轮询后写快照。纯逻辑，可单独测。✅ 已完成
2. **bin**：`crates/statusline` + 打包进单文件包 + 释放/版本戳。✅ 已完成
3. **安装契约**：`~/.claude/settings.json` 读写与接管/还原、漂移自愈、四个 FFI 方法。✅ 已完成
4. **GUI**：设置行、预览、中英文案、README 一节。✅ 已完成

## 14. 风险

| 风险 | 处置 |
|---|---|
| Claude Code 改 stdin schema | 所有字段按 optional 读，缺了就隐藏该段（§8） |
| `rate_limits` 在部分版本/账号缺席 | 快照回退 + `~` 标记（§4.3） |
| 释放的 exe 被杀软拦 | 与既有 dll 自解压同性质，文档说明；失败时状态栏保持关闭并给出原因 |
| 用户同时装了 ccstatusline | 不静默接管，替换前确认并保存原值（§6.3） |
| 每次重绘都起进程 | Rust 二进制 + 只读文件 + 不 fork git；并写 `refreshInterval: 10` 降低重绘频率 |
| 宽度探测在某些宿主下失效 | 探不到就保持两行（旧行为），`--width` / `CS_STATUSLINE_WIDTH` 是人工出路（§3.4） |
