# Codex TPS 中文悬浮窗

Windows、macOS 和 Linux 的本机日志监视工具，默认运行在系统托盘，不显示任务栏窗口按钮。白色圆角面板参考 [codex-tps-panel.png](https://github.com/gaofeng21cn/codex-tps/blob/main/docs/assets/codex-tps-panel.png)，代码为独立实现。

## 使用

从 [GitHub Releases](https://github.com/jinghero/CodexTpsFloat/releases/latest) 选择适合当前系统的附件：

| 系统 | 附件 | 启动文件 |
|---|---|---|
| Windows x64 | `CodexTpsFloat-v版本号-win-x64-portable.zip` | `CodexTpsFloat.exe` |
| macOS Apple 芯片 | `CodexTpsFloat-v版本号-osx-arm64-portable.tar.gz` | `CodexTpsFloat` |
| macOS Intel | `CodexTpsFloat-v版本号-osx-x64-portable.tar.gz` | `CodexTpsFloat` |
| Linux x64 | `CodexTpsFloat-v版本号-linux-x64-portable.tar.gz` | `CodexTpsFloat` |

压缩包包含运行时，不需要安装 Python 或 .NET，不需要管理员权限。请下载对应的便携包；GitHub 自动附带的 `Source code` 压缩包只包含源码。Release 附件同时提供压缩包的 `.sha256` 校验文件。隐私与提交范围见 [PRIVACY.md](PRIVACY.md)。

macOS 首次运行时如果被 Gatekeeper 阻止，请在 Finder 中右键 `CodexTpsFloat` 选择「打开」，或前往「系统设置」>「隐私与安全性」允许运行。便携包没有代码签名。

便携构建使用单文件程序，并关闭内部程序集压缩，以减少运行时解压所需的内存；下载时由外层压缩包压缩。解压后的程序较大，其中包含 .NET 运行时及各平台的界面依赖。

- 默认读取 `CODEX_HOME`；未设置时读取当前用户的 `.codex`。同时统计 `sessions` 与 `archived_sessions`。
- 托盘图标使用透明底的统计条形图；macOS 菜单栏图标随系统自动适配深浅色。面板打开时悬停显示当前生成速度估算，收起后显示「已暂停」。Windows 启动后只显示系统托盘图标，单击打开或收起统计面板，右键可打开面板、立即刷新或退出；macOS 点击图标弹出菜单，可选择「打开统计面板」。面板展开时也不出现任务栏窗口按钮或 macOS Dock 图标。macOS 上菜单栏图标可能受系统或第三方工具影响而不可见，因此启动会直接显示面板，之后可用减号收起到菜单栏。Linux 无法可靠检测托盘宿主，启动时直接显示面板并禁用收起，确保没有托盘的桌面也能操作和退出。
- 面板可拖动，蓝色菱形切换置顶，减号收起到托盘。电源按钮退出整个工具。
- 切换 1 分钟、5 分钟、30 分钟、1 小时窗口，或筛选模型。面板打开时自动刷新支持 5、15、30、60 秒；缩回托盘后暂停读取和刷新，销毁统计窗口及其渲染器，释放扫描缓存、统计快照与分时图表数据，并执行一次完整回收，托盘提示「已暂停」。重新打开时重建面板、重新扫描并恢复刷新，同时保留日期、模型筛选和展开状态；暂停期间日志仍保留在磁盘，统计不会丢失。托盘的「立即刷新」会打开面板后刷新。
- 展开「今日分时」，可以选择其他日期并导出中文 Markdown 或 JSON 报告。时区固定为北京时间 UTC+8。
- 分时折线图按时间从左到右显示加权 TPS 和中位数，悬停可查看样本数，无有效计时样本时断线。当天只显示已经开始的小时，历史日期显示完整一天，未来日期不显示时段；图表、导出报告和快照 JSON 中的小时均按时间正序排列。
- 「设置」可以选择数据目录。选择包含 `sessions` 的 `.codex` 目录，而不是 `sessions` 自身。
- 「登录启动」默认关闭，仅在手动勾选后设置当前用户的启动项。取消勾选即可移除。
- 第二次启动会打开已有统计面板，不重复运行监视进程。关闭面板前尚未打开过它，也可以从托盘菜单正常退出。Windows 的设置保存在程序旁的 `data/settings.json`；macOS 和 Linux 保存在用户配置目录，避免系统级安装目录不可写。

## 数字的含义

**大号读数是输出生成速度估算**：所选窗口内已完成响应的输出词元总数，除以各响应流式阶段耗时之和。包含推理词元；并发响应耗时分别相加，输入词元不计入。

流式阶段使用日志中的首个 `AgentMessage` / `Reasoning` 输出项开始时间至逐次 `token_usage_record` 时间。日志不是逐 token 计时；该数字不能当作服务端精确解码速度。缺少计时或只有零持续时间的输出项显示为无样本，不补成零。

**四色小读数是窗口吞吐量**：窗口内完成的对应词元数量 ÷ 完整窗口秒数。缓存是输入的子集，推理是输出的子集，均不重复相加。这个口径与参考项目的大号「总 token 吞吐量」有区别。

**轮次首字**是已经结束轮次的 `time_to_first_token_ms` 中位数，不代表每个内部请求的 TTFT。活跃会话以当前窗口内有已完成用量事件为准。

分时统计按首个输出项的北京时间小时分组。不同日期、时段的任务和上下文不同，不能单凭速度变化断定服务端或网络性能变化。现代日志支持逐次 `token_usage_record`；只有旧版 `token_count` 且包含实际输出用量的日志不会虚构逐次输出速度，状态会提示部分日志不可计量。未生成响应、仅有空用量事件的中断会话不触发旧格式告警。

## 数据与运行方式

工具只读日志，不修改 Codex 配置、日志或请求，不需要 API Key，不调用网络服务。读取在后台线程完成，按行解析 UTF-8 字节，避免整份日志的字符串副本；常规读取缓冲区归还缓存池前会清空，超长日志使用的临时大缓冲不驻留缓存池。面板打开期间后续刷新只读追加内容；完整 JSONL 尾行才计入。按 `response_id` 跨文件去重；归档、重复刷新不会重复计数。日志截断、替换或从托盘重新打开后重建统计。面板收起时取消扫描并释放统计数据，托盘保留打开、刷新和退出功能。

如果当前数据目录存在 `logs_*.sqlite`，以只读方式增量读取请求鉴权边界，用于排除失败尝试遗留的输出项。诊断库缺失、被锁定或格式不兼容时仍可使用 JSONL 输出项计时，状态提示中说明降级。边界日志可能已过期，因此仍是估算值。

输出不保存提示词或回答正文。导出报告的位置由保存对话框选择。Windows 配置写入程序旁目录；macOS 和 Linux 写入用户配置目录。

可用于脚本的单次快照：

```powershell
.\app\CodexTpsFloat.exe --snapshot-json .\snapshot.json
.\app\CodexTpsFloat.exe --codex-home 'D:\CodexData' --snapshot-json .\snapshot.json
```

macOS 或 Linux 使用相同参数：

```bash
./app/CodexTpsFloat --snapshot-json ./snapshot.json
./app/CodexTpsFloat --codex-home /path/to/.codex --snapshot-json ./snapshot.json
```

退出码：`0` 成功；`1` 读取或写入失败；`2` 参数错误；`3` 未找到会话日志。快照模式独立执行，JSON 包含四个滚动窗口和今日分时汇总，不包含正文。

GitHub 源码仓库不包含可执行文件和本机运行数据。从源码构建需要 .NET 10 SDK。Windows 使用 PowerShell：

```powershell
dotnet run --project .\tests\CoreTests.csproj -c Release
dotnet run --project .\tests\AppTests.csproj -c Release
dotnet run --project .\tests\TrayLifecycleTests.csproj -c Release -- --never-show
.\build.ps1 -Runtime win-x64 -Portable
```

macOS 和 Linux 使用 Bash，架构会从当前系统推断，也可以显式传入 RID：

```bash
dotnet run --project tests/CoreTests.csproj -c Release
dotnet run --project tests/AppTests.csproj -c Release
./build.sh osx-arm64
PORTABLE=1 ./build.sh linux-x64
```

源码中的 `--capture 文件.png` 用于界面验收，生成真实 Avalonia 控件渲染图后退出。Windows 便携 EXE 未进行 Authenticode 签名，macOS 便携程序未进行代码签名或公证。

## 发布新版本

先修改 `Directory.Build.props` 中的 `Version` 并提交、推送，再创建对应的 `v版本号` 标签。以下示例用于下一个补丁版本；标签说明会成为 Release 说明：

```powershell
git push origin main
git tag -a v1.2.3 -m "Release v1.2.3"
git push origin v1.2.3
```

推送标签后，GitHub Actions 会校验标签与项目版本一致，在 Linux、Windows 和两种 macOS 架构上运行核心测试和 Avalonia 无界面回归测试；Windows 额外运行旧版 WPF 隐藏托盘生命周期回归测试。拉取请求也运行相同的平台回归和四平台便携构建。这些测试不覆盖各平台原生托盘行为，后者仍需在各桌面环境进行手动验收。发布矩阵构建 Windows x64、macOS ARM64、macOS x64 和 Linux x64 便携包，解压后执行快照 smoke test，并生成 SHA-256 校验文件。全部平台成功后，工作流创建公开 Release 并上传所有压缩包与校验文件。打包只包含程序、README、隐私说明和第三方许可证，不包含本机设置、日志或调试符号。
