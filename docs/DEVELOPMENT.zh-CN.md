# Series Relay Launcher 再开发说明

[用户指南](GUIDE.zh-CN.md) · [引擎版本清单](../Emulators/catalog.json) · [发行历史](../Updates/history.json)

[English](DEVELOPMENT.md) · 简体中文 · [繁體中文](DEVELOPMENT.zh-TW.md) · [日本語](DEVELOPMENT.ja.md) · [한국어](DEVELOPMENT.ko.md)

本文对应启动器 **v1.0.4.2**。Windows 桌面端负责实例、输入配置和界面；Python 会话进程负责准备资源、启动引擎、转发日志。调试某个构建前，先用发行清单中的项目 ID 和提交号定位实际运行的引擎。

| 要追踪的问题 | 从这里进入 |
| --- | --- |
| 导入校验、实例与预设 | [GameImport.cs](../Source/GTA6Launcher/GameImport.cs)、[ImportCard.cs](../Source/GTA6Launcher/ImportCard.cs)、[GameLibrary.cs](../Source/GTA6Launcher/GameLibrary.cs) |
| 会话编排和预热 | [DesktopLauncher.cs](../Source/GTA6Launcher/DesktopLauncher.cs)、[session.py](../Source/XCL-VIctory-Lab/desktop/session.py)、[warmup_job.py](../Source/XCL-VIctory-Lab/desktop/warmup_job.py) |
| 成就解析、历史与显示 | [AchievementJournal.cs](../Source/GTA6Launcher/AchievementJournal.cs)、[AchievementToast.cs](../Source/GTA6Launcher/AchievementToast.cs)、[AchievementTile.cs](../Source/GTA6Launcher/AchievementTile.cs) |
| 键鼠绑定与 HUD 资源 | [BindingWizard.cs](../Source/GTA6Launcher/BindingWizard.cs)、[GameKeyPrompts.cs](../Source/GTA6Launcher/GameKeyPrompts.cs)、[key_prompts.py](../Source/XCL-VIctory-Lab/desktop/key_prompts.py) |

## 从导入来源到可启动实例

同一次导入有三个不同的完成条件。来源检查回答“输入指向什么”，文件校验回答“本机数据和主程序是否匹配”，会话预载回答“当前引擎是否已准备接收启动命令”。本地已解压文件可以在入库前完成文件校验。归档和磁力链接会先入库，再从大厅执行安装。

| 输入与阶段 | 判定条件与输出 | 发生故障时先看哪里 |
| --- | --- | --- |
| 本地目录，`Address` | 优先使用所选目录或它的直接子目录 `GameData` 中的 `MicrosoftGame.Config`。否则扫描同层 ZIP、7Z、RAR 或编号从 1 开始的分卷主文件。多个主包交由用户选择。 | 区分目录层级错误、清单不可读、首卷缺失和分卷编号断档。 |
| 磁力链接，`Address` | 传输子进程读取元数据。清单中出现 `MicrosoftGame.Config` 即走散装本体路径；只有支持的归档时记录归档标记，留待解压验证。 | 60 秒元数据超时与 90 秒传输停滞是不同阶段。 |
| EXE 来源，`Contents / External` | 自动查找沿清单里的相对路径解析。独立优化构建允许单独存放与声明文件同名的程序。文件校验检查 PE 签名和 AMD64 机器类型，并检验四个必要 RPF 的头部。 | 区分内容声明、缺少 EXE、EXE 名不匹配和文件格式不正确。主卷 EXE 属于归档输入。 |
| 归档，`Archive` | 普通解压和释放源卷都要目标目录，释放模式额外读取确认。选择变动使上次检查失效。 | 缺少可写输出目录、磁盘空间不足、源卷已删除、解压后没有唯一的游戏清单。 |
| 入库，安装，提交 | `InstallPlan` 分开保存本体与 EXE 的模式、来源和输出，并保存必要的种子元数据。大厅安装先处理单独的 EXE，再处理本体，每个成功阶段记录输出。最终校验通过后以相同实例 ID 替换待安装记录。 | 检查 `desktop/game-instances.xml` 的待安装信息、已生成的输出目录及当次日志。正式安装通知出现在成功提交后。 |

本地分卷的释放依赖流的消费状态。读取下一卷时，前一卷只有完整消费后才可提前删除。任务末尾核对输出清单后再释放剩余源卷。磁力归档会先下载文件集再解压。归档检查成功只能证明当前阶段具备所需输入，后续仍可能发生解压、文件结构或 EXE 匹配错误。[来源状态机](../Source/GTA6Launcher/ImportSource.cs) · [延迟安装](../Source/GTA6Launcher/DeferredInstall.cs) · [本地归档](../Source/GTA6Launcher/Transfers/LocalImport.cs) · [磁力归档](../Source/GTA6Launcher/Transfers/MagnetArchives.cs)

## 会话协议与四类状态通知

| 事件 | 生产者的证据 | 界面动作 |
| --- | --- | --- |
| `PRELOADING`、`STAGE` | 会话进入准备阶段，后续阶段名称可能多次改变 | 处于预热状态时入队「开始预载」，用阶段消息更新按钮详情 |
| `COUNT`、`PROGRESS` | 前者是着色器数，后者是已处理与总管线数 | 只有后者可用于计算管线进度条 |
| `READY` | 引擎准备结束，可以接收 `PLAY` | 从预热切到就绪时提示「预载完成」 |
| `DEVICE_READY`、`PLAYING` | D3D12 设备就绪与检测到首帧分属两个阶段 | 首帧带来的 `PLAYING` 才触发「游戏启动成功」，并把窗口 PID 交给成就浮层 |
| `EXIT`、`SHADER_ERROR`、`ERROR` | 游戏退出、着色器准备失败或其他会话错误 | 结束会话或展示错误，结合本轮日志还原失败阶段 |

`LauncherNoticeKind` 的四个值为 `Installed`、`Preloading`、`Ready`、`Started`。队列逐个显示通知，单项生命周期约三秒。启动成功通知优先定位游戏所在显示器，其他通知以启动器工作区为基准。成就浮层位于同一显示器时预留通知所占高度。`ResetSession` 清理已经失效的会话通知，保留已入队的安装完成通知。状态通知不会写入成就进度。[通知队列和绘制](../Source/GTA6Launcher/LauncherNotice.cs)

## 成就事件如何变成本地记录

**SR Achievement Event Monitoring System** 有两条输出路径。实时路径将事件送到桌面端的通知队列；持久化路径把同一条事件追加到本轮会话文件，供大厅按实例重建历史。

```text
引擎运行日志
  └─ session.achievement_record → 归一化事件
      ├─ LOG → AchievementToast.Session.Observe → 进度或解锁通知
      └─ runs/<run>/achievements.log → AchievementJournal.ReadHistory → 大厅成就卡
```

### 采集与分类

`forward_runtime_logs` 将每行原始输出送进 `achievement_record(raw, engine)`。解析器保留带时区的原始时间戳，缺失时使用当前 UTC 时间，再附上引擎标记。对于 XUser 模块的 `local achievement update` 消息，只有 JSON 对象中的 `achievementId` 为 1–256 字符的字符串、`percentComplete` 为 0–100 的整数时才产生进度事件。布尔值、越界值和损坏的 JSON 会被丢弃。

| 归一化事件 | 生产端的证据 | 消费端行为 |
| --- | --- | --- |
| `local-progress` | XUser 本地更新携带明确的 ID 和百分比 | 保留该 ID 的最高进度，100 计为完成 |
| `unlocked` | 有明确的成功解锁语义，且可提取 ID | 以该 ID 的 100% 完成事件处理 |
| `query` | 成就服务的查询请求，可带 ID | 留作诊断 |
| `progress-update-request` | 请求访问成就更新端点 | 留作诊断，等到有实际进度或解锁证据才更改卡片 |

例如，下面是**合成的归一化输出**。它展示同一个 ID 的一次进度和一次完成，可用于核对解析器输入。目录中字符串 ID `"1"` 对应「仿若天堂」。

```text
2026-01-01T12:00:00Z [CGBJ achievement] local-progress {"achievementId":"1","percentComplete":42}
2026-01-01T12:01:00Z [CGBJ achievement] unlocked achievementId=1
```

### 归属、去重与显示

1. **按实例归集。** `ReadHistory(lab, instance)` 枚举 `desktop/play-history/<instance>/Desktop-*.time`，用标记文件名定位 `runs/<run>/achievements.log`。实例 ID 决定遍历哪些会话，即使两个实例的展示名相同也可以分别回放。
2. **按 ID 合并。** `TryProgress` 识别已知引擎标记里的两类有效事件。历史取每个 ID 的最大百分比，完成时间取带有效时区的最早记录。同一时刻同时有进度 100 和明确解锁时，保留明确解锁的证据。
3. **把数据接到界面。** `Session.Observe` 用本地目录筛选 ID，并将新百分比与历史最高值比较。只有严格增长才入队，同一条日志重放不会重复弹窗。收到 `PLAYING` 后，`Attach(pid)` 用游戏窗口定位显示器工作区。完成事件的展示优先级高于普通进度。

目录通过 [GtaViAchievements.tsv](../Source/GTA6Launcher/Assets/GtaViAchievements.tsv) 加载。每行 12 列，依次为 ID、点数、五种语言的名称、同顺序的五种描述。名称和描述按简中、英文、繁中、日文、韩文排列。新增条目时需同步图标资源。排查“日志有 ID，卡片没变”时，先看事件种类，再核对实例标记、目录 ID 和历史最高值。

### 复现一次进度回退与重复解锁

在测试输入中对已知 ID 依次传入查询、42%、12%、明确解锁、同一解锁的重放。预期依次为无进度、42%、仍为 42%、100%、仍为 100%。最后一次不应再弹出解锁通知。若原始日志里已经有进度而大厅不变，沿原始 `runtime.log`、归一化 `achievements.log`、实例的会话标记、TSV 中的 ID 和历史最高值依次核查。旧引擎标记能被历史解析器识别；当前引擎卡的“支持成就”由在售版本实际接入的后端决定。[引擎卡实现](../Source/GTA6Launcher/ProjectHoverCard.cs)

## 键鼠映射与 HUD 重绘

**输入和显示是两条链路。** 键位能触发动作时，输入链路已生效。HUD 仍显示手柄图标时，应沿提示资源链路排查。

### 输入配置与会话协议

[MappingSteps.cs](../Source/GTA6Launcher/MappingSteps.cs) 定义向导的 18 个手柄输入项。[BindingSlots.cs](../Source/GTA6Launcher/BindingSlots.cs) 管理用户配置槽及独立的内置预设，[GtaViPreset.cs](../Source/GTA6Launcher/GtaViPreset.cs) 处理预设的步行、瞄准和驾驶上下文。预设中的 F6 切换步行与驾驶，同一物理键可以随上下文改变输出。

`CurrentPlayCommand` 在启动时确定输入模式。手柄使用 `PLAY`；CGBJ 自定义映射使用 `PLAY<TAB>KEYBOARD` 加 `PAD=`，其中按固定顺序传送前 16 项虚拟键码，Share 和 Guide 在这组字段之外。内置方案使用 `PLAY<TAB>GTA6_PRESET`。这里的 `<TAB>` 表示单个制表符。可选字段 `PROMPTS=` 携带 Base64 编码的 ASCII 动作表，解码后的两行示例如下。

```text
INPUT_AIM=RMB
INPUT_JUMP=Space
```

`GameKeyPrompts.Command` 从选中配置生成动作表。`decode_prompts` 校验 Base64、字段名和键帽标签，再生成供资源处理使用的绑定表。准备阶段和启动阶段都核对同一份配置；更换配置后，桌面端会重新安排准备任务。

### 文字和矢量图标怎样重绘

v1.0.4 的启用条件包括 **CGBJ**、游戏清单中的 **TitleId `7952987A`**、键鼠模式、已配置的映射槽和已开启的「替换为实际键位」。准备发生在 `READY` 之前。

| 处理阶段 | 实现和约束 |
| --- | --- |
| 操作名解析 | `GameKeyPrompts.WithPrompts` 将上下文动作映射成键帽标签。例如步行的瞄准走 `INPUT_AIM`，驾驶的油门走独立车辆动作，两者可以复用手柄扳机。 |
| 帮助文字 | `replace_labels` 在支持的字符串池中替换已识别的操作标记，例如将瞄准提示替换为 `[RMB]`。仅在替换结果能容纳于原条目时写入，保持后续字符串的索引位置。 |
| HUD 图标 | `replace_button_glyphs` 从 Scaleform 图标库中把导出符号关联到动作。外轮廓、内底和键帽字样分别生成矢量层，复用相同轮廓。Enter 与 Space 各有独立图形。 |
| 会话资源 | `create_copy` 以只读方式打开源 `data.rpf`，确认能替换共享图标库后，将变更写入会话副本。归档内的压缩条目须适配原空间。缺少可用图标或超出资源容量时，准备过程会报错。 |

定位问题时，在本轮日志中找 `Keyboard prompts:`，分别核对 `HUD button glyphs` 和 `text entries` 的数量。文字条目数可以为零；图标数为零会使该次提示准备失败。想复现“输入正确但提示仍旧”的情况，可以分别测试关闭开关、切换引擎、准备完成后更换映射三条路径。

## 渲染、缓存和组件修复

`prepare_nvidia` 先加载随引擎发布的 `preload.env`，再合并 F1 菜单中支持的设置，调用原生 `preload`。部分设置会改变着色器缓存键，例如 early depth。`prewarm.log` 可区分着色器缓存条目、无法翻译条目、已知管线和写入原生管线库的进度。缓存已有数万条着色器时，下一轮仍可能有管线工作。比较两次耗时要使用同一实例数据、相同引擎提交和渲染选项，分别比较各阶段，以免把扫描时间当成重新编译。

渲染页按探测结果、控制台档位、超分状态与帧生成方式计算可选倍率；改变设置后会将已选倍率限制在有效范围。VIctory-NVIDIA 的适配层更新 F1 菜单实际读取的配置项，并保留无关设置。切换引擎后先检查当前项目和实际写入文件，再诊断画面差异。[渲染页](../Source/GTA6Launcher/RenderSettings.cs) · [菜单适配层](../Source/GTA6Launcher/VictoryNvidiaMenu.cs)

组件索引按组记录目标相对路径、长度和 SHA-256。组件检测可从 `Runtime/Components/Library.zip` 的同摘要条目或嵌入的恢复资源取得内容。Python 运行时会实际导入所需模块，传输工具运行自检。已安装游戏还会走游戏文件校验；用户缺失的游戏资源要从其原来源补齐。索引与离线库要来自同一发行构建。[修复逻辑](../Source/GTA6Launcher/ComponentLibrary.cs) · [组件自检](../Source/GTA6Launcher/ComponentSettings.cs)

## 实例、存档和日志的数据边界

| 位置 | 生命周期 | 常见排查点 |
| --- | --- | --- |
| `desktop/game-instances.xml`（相对实验室目录） | 实例 ID、文件引用、配置、媒体和待安装计划。写临时文件后替换 | 重命名不应产生新 ID。延迟安装完成后继续使用原 ID。 |
| `GameSaves/<instance>` | 当前机器的存档槽，XCL 另有自己的布局。导入 ZIP 先进入暂存目录 | 切换存档要在游戏退出后进行。成就回放依据实例的会话标记。 |
| `Logs/Launcher-*.log` | 启动器级日志，逐行带时间和来源。导出时复制当下的有效长度 | 调试多个引擎时，保留出错时刻、实例和引擎 ID。 |
| `runs/<run>/` | 一轮会话的原始日志、预热日志和成就日志 | 图形准备问题先看 `prewarm.log`，成就问题要对照原始和归一化日志。 |

[存档库](../Source/GTA6Launcher/LocalSaveLibrary.cs) · [XCL 存档库](../Source/GTA6Launcher/XclSaveLibrary.cs) · [日志导出](../Source/GTA6Launcher/SessionLog.cs)

## 构建与回归测试

Windows x64 构建环境需要 .NET 9 SDK、.NET Framework 4.8 和 4.6.2 targeting pack，以及 Python 3.11 或更高版本。发行包版本应与源码匹配。将全量 ZIP 放入 `Updates` 后，在仓库根目录执行以下命令。第一步按仓库中的 SHA-256 校验包，再恢复离线组件和引擎输入。

```powershell
py -3 prepare_inputs.py Updates/SRLauncher-1.0.4.2.zip --portable
dotnet build Source/GTA6Launcher/GTA6Launcher.csproj -c Release
py -3 -m unittest discover -s Source/GTA6Launcher/Tools/tests -p test_cgbj_log.py
py -3 Source/GTA6Launcher/Tools/tests/test_import_archive.py
powershell -NoProfile -File Source/GTA6Launcher/Tools/tests/test_launcher_update.ps1 -Helper Source/GTA6Launcher/Updater/bin/Release/net462/Palmline.Update.exe
```

只构建桌面端时，`prepare_inputs.py` 可省略 `--portable`。完整的会话测试使用引擎运行目录。测试覆盖成就查询与解锁分类、历史进度合并、键帽字形、字符串池位置以及预载后配置失效等情况。浮层可用 Release 的 net48 构建和 Windows PowerShell 的 STA 模式单独检查。[会话测试](../Source/GTA6Launcher/Tools/tests/test_cgbj_log.py) · [浮层验证脚本](../Source/GTA6Launcher/Tools/tests/verify_achievement_overlay.ps1)

## 发布与版本检查

1. 递增启动器和日志控制台的版本。在 [history.json](../Updates/history.json) 追加版本号、`normal` 或 `critical` 及用户能读懂的变更说明。
2. 恢复便携版输入后，在空输出目录制作完整发行包。命令为 `py -3 Source/GTA6Launcher/Tools/package_portable.py --runtime-from Source/Runtime --output-dir Build`。文件名用 `SRLauncher-<version>.zip`，校验文件用 ASCII，内容为小写 SHA-256、两个空格和文件名。同步包内 `Sources/Launcher.zip` 中的 `GTA6Launcher/Assets/ComponentIndex.tsv`。
3. 将全量 ZIP、校验文件、匹配的 `Upgrade-Launcher.exe` 和 `Upgrade-Launcher.exe.config` 放入 `Updates/`。执行 `py -3 prepare_release_assets.py` 和 `py -3 verify_release.py`。向对应标签仅上传启动器全量包和离线升级包，源码由 GitHub 归档。使用 `Updates/RELEASE.md` 作为发布正文，保留可解析的逐版更新历史。

### 旧版离线升级

**从 1.0.4 或更早版本原地升级**　旧版只识别三段版本号。下载[离线升级包](https://github.com/Pumo-Ayla/series-relay-launcher/releases/download/v1.0.4.2/SRLauncher-1.0.4.2-offline.zip)，解压到单独的临时文件夹，保留里面的启动器 ZIP 原样。关闭启动器和游戏，双击 `Upgrade-Launcher.exe`，输入原来放 `SRLauncher.exe` 或 `Launcher.exe` 的文件夹路径并回车。显示升级完成后，从原目录打开启动器。游戏实例、设置和存档会保留。
