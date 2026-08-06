Claude Switch — 在多个 Claude Code 账号之间切换
================================================

快速开始
--------

1. 双击 ClaudeSwitch.exe。没有安装步骤，也不需要装 .NET 或其它运行库。

2. 首次运行 Windows 会弹出 SmartScreen 警告：
   「Windows 已保护你的电脑」

   这是因为本程序没有购买代码签名证书，不是因为检出了问题。
   点「更多信息」→「仍要运行」。

   解压出来的文件同样带有"来自网络"标记，所以压缩包并不能绕过这个提示。
   介意的话，可以先核对校验和（见下），或者自行从源码构建。

3. 在 Claude Code 里登录一个账号，然后在本程序点「添加账号」把它纳入管理。
   对第二个账号重复一次，就可以开始切换了。


校验下载是否完整
----------------

在 PowerShell 里运行：

    Get-FileHash ClaudeSwitch.exe -Algorithm SHA256

把结果与随附的 SHA256SUMS.txt 比对。


它做什么
--------

* 一键在多个 Claude 账号之间切换。切换对**正在运行**的 Claude Code 立即生效，
  下一条消息就走新账号，不用重启。

* 额度用满时自动换号：任一账号 5 小时或 7 天用量达到阈值，自动切到余量最多的
  可用账号，并弹出通知告诉你切到了哪里、为什么。

* 显示每个账号的用量、套餐、订阅开始日期，以及你在 Claude Code 上的总体使用情况。


数据存放在哪
------------

    %USERPROFILE%\.claude-swap-backup\     各账号的凭据与配置备份
    %LOCALAPPDATA%\ClaudeSwitch\           界面偏好
    %TEMP%\.net\ClaudeSwitch\              单文件包自解压的运行时

备份格式与 claude-swap（Python 命令行工具）兼容，两者可共用同一份备份。


卸载
----

删掉 ClaudeSwitch.exe 即可。程序不写注册表，也不需要管理员权限。

如果开启过「开机自启」，先在程序里取消勾选，或手动删除：
    %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\ 下的快捷方式

想连备份一起清掉，删除 %USERPROFILE%\.claude-swap-backup\ 目录。
注意：删掉之后，未在 Claude Code 中登录的账号将无法恢复。


源码与问题反馈
--------------

https://github.com/nimeia/claude_switch

许可证：MIT（见 LICENSE）
