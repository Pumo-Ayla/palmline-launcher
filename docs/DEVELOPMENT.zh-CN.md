# Series Relay Launcher 开发者指南

[用户指南](../README.md) · [引擎版本清单](../Emulators/catalog.json) · [发行历史](../Updates/history.json)

本文对应启动器 **v1.0.3**。Windows 桌面端负责实例、输入配置和界面；Python 会话进程负责准备资源、启动引擎、转发日志。调试某个构建前，先用发行清单中的项目 ID 和提交号定位实际运行的引擎。

| 要追踪的问题 | 从这里进入 |
| --- | --- |
| 导入校验、实例与预设 | [GameImport.cs](../Source/GTA6Launcher/GameImport.cs)、[ImportCard.cs](../Source/GTA6Launcher/ImportCard.cs)、[GameLibrary.cs](../Source/GTA6Launcher/GameLibrary.cs) |
| 会话编排和预热 | [DesktopLauncher.cs](../Source/GTA6Launcher/DesktopLauncher.cs)、[session.py](../Source/XCL-VIctory-Lab/desktop/session.py)、[warmup_job.py](../Source/XCL-VIctory-Lab/desktop/warmup_job.py) |
| 成就解析、历史与显示 | [AchievementJournal.cs](../Source/GTA6Launcher/AchievementJournal.cs)、[AchievementToast.cs](../Source/GTA6Launcher/AchievementToast.cs)、[AchievementTile.cs](../Source/GTA6Launcher/AchievementTile.cs) |
| 键鼠绑定与 HUD 资源 | [BindingWizard.cs](../Source/GTA6Launcher/BindingWizard.cs)、[GameKeyPrompts.cs](../Source/GTA6Launcher/GameKeyPrompts.cs)、[key_prompts.py](../Source/XCL-VIctory-Lab/desktop/key_prompts.py) |

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

v1.0.3 的启用条件包括 **CGBJ**、游戏清单中的 **TitleId `7952987A`**、键鼠模式、已配置的映射槽和已开启的「替换为实际键位」。准备发生在 `READY` 之前。

| 处理阶段 | 实现和约束 |
| --- | --- |
| 操作名解析 | `GameKeyPrompts.WithPrompts` 将上下文动作映射成键帽标签。例如步行的瞄准走 `INPUT_AIM`，驾驶的油门走独立车辆动作，两者可以复用手柄扳机。 |
| 帮助文字 | `replace_labels` 在支持的字符串池中替换已识别的操作标记，例如将瞄准提示替换为 `[RMB]`。仅在替换结果能容纳于原条目时写入，保持后续字符串的索引位置。 |
| HUD 图标 | `replace_button_glyphs` 从 Scaleform 图标库中把导出符号关联到动作。外轮廓、内底和键帽字样分别生成矢量层，复用相同轮廓。Enter 与 Space 各有独立图形。 |
| 会话资源 | `create_copy` 以只读方式打开源 `data.rpf`，确认能替换共享图标库后，将变更写入会话副本。归档内的压缩条目须适配原空间。缺少可用图标或超出资源容量时，准备过程会报错。 |

定位问题时，在本轮日志中找 `Keyboard prompts:`，分别核对 `HUD button glyphs` 和 `text entries` 的数量。文字条目数可以为零；图标数为零会使该次提示准备失败。想复现“输入正确但提示仍旧”的情况，可以分别测试关闭开关、切换引擎、准备完成后更换映射三条路径。

## 构建与回归测试

Windows x64 构建环境需要 .NET 9 SDK、.NET Framework 4.8 和 4.6.2 targeting pack，以及 Python 3.11 或更高版本。发行包版本应与源码匹配。将全量 ZIP 放入 `Updates` 后，在仓库根目录执行以下命令。第一步按仓库中的 SHA-256 校验包，再恢复离线组件和引擎输入。

```powershell
py -3 prepare_inputs.py Updates/Launcher-1.0.3.zip --portable
dotnet build Source/GTA6Launcher/GTA6Launcher.csproj -c Release
py -3 -m unittest discover -s Source/GTA6Launcher/Tools/tests -p test_cgbj_log.py
```

只构建桌面端时，`prepare_inputs.py` 可省略 `--portable`。完整的会话测试使用引擎运行目录。测试覆盖成就查询与解锁分类、历史进度合并、键帽字形、字符串池位置以及预载后配置失效等情况。浮层可用 Release 的 net48 构建和 Windows PowerShell 的 STA 模式单独检查。[会话测试](../Source/GTA6Launcher/Tools/tests/test_cgbj_log.py) · [浮层验证脚本](../Source/GTA6Launcher/Tools/tests/verify_achievement_overlay.ps1)

## 发布与版本检查

1. 递增启动器和日志控制台的版本。在 [history.json](../Updates/history.json) 追加版本号、`normal` 或 `critical` 及用户能读懂的变更说明。
2. 恢复便携版输入后，在空输出目录制作完整发行包。命令为 `py -3 Source/GTA6Launcher/Tools/package_portable.py --runtime-from Source/Runtime --output-dir Build`。文件名用 `Launcher-X.Y.Z.zip`，校验文件用 ASCII，内容为小写 SHA-256、两个空格和文件名。同步包内 `Sources/Launcher.zip` 中的 `GTA6Launcher/Assets/ComponentIndex.tsv`。
3. 将全量 ZIP、校验文件、同一构建的升级工具及配置放入 `Updates`。升级工具的发布名称为 `Upgrade-Launcher.exe` 和 `Upgrade-Launcher.exe.config`。运行 `py -3 prepare_release_assets.py` 生成模拟器独立包、清单及发行说明，再运行 `py -3 verify_release.py` 检查包完整性和用户数据隔离。
4. 在官方仓库创建对应标签的 GitHub Release，以生成的 [RELEASE.md](../Updates/RELEASE.md) 为正文，保留末尾的完整历史和最低支持版本标记。上传对应版本的启动器全量 ZIP、模拟器 ZIP、两者的 SHA-256 及离线升级工具和配置，并将发行版设为 Latest。Git 中保留源码、清单和校验文件，大型二进制随 Release 分发。

客户端按版本号筛选「本地版本之后、目标版本及之前」的记录，分别统计普通与关键更新。1.0.1 → 1.0.3 会跨过一次关键更新和一次普通更新，1.0.2 → 1.0.3 只有一次普通更新。两种路径使用同一份最新全量包。[更新器实现](../Source/GTA6Launcher/LauncherUpdate.cs) · [热更新清单](../Updates/latest.json)

### 旧版离线升级

对于 1.0.0 和 1.0.1 便携版，先关闭游戏与启动器。将全量 ZIP、对应的 `.sha256`、`Upgrade-Launcher.exe` 及其 `.config` 放在同一目录，运行升级工具并指定旧 `Launcher.exe` 所在目录。工具校验摘要后就地升级，保留本地游戏、存档和配置。升级完成后使用 `SRLauncher.exe` 启动。
