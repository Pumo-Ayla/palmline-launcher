# Series Relay Launcher

Windows 正式启动入口为 `SRLauncher.exe`。解压便携版后，从发行目录双击该文件。发行包同时提供 `Launcher.exe`，供已发布的旧更新器完成升级交接。

## 下载与更新

- [启动器全量包](https://github.com/Pumo-Ayla/series-relay-launcher/releases/download/v1.0.3/Launcher-1.0.3.zip) · [热更新目录](Updates/latest.json)
- [独立模拟器包](https://github.com/Pumo-Ayla/series-relay-launcher/releases/download/v1.0.3/SR-Emulators-1.0.3.zip) · [模拟器项目、提交与校验信息](Emulators/catalog.json)
- [逐版更新说明](Updates/RELEASE.md) · [机器可读的版本历史](Updates/history.json)

启动器从 Latest Release 的说明读取完整版本历史，按版本号统计本机版本之后的普通更新和关键更新。安装过最近的关键版本即可跳过后续普通更新。错过关键更新时，启动器提示安装最新全量包。成就事件组件使用英文名称 **SR Achievement Event Monitoring System**。

`Source/GTA6Launcher` 包含 Windows 启动器、更新器、下载引擎、日志控制台、原生桥接代码、界面资源和测试。`Source/XCL-VIctory-Lab` 包含会话、预热和输入脚本。`Source/Community` 保存项目清单及其对应提交的三份模拟器源码。

## 构建启动器

Windows x64 需要 .NET 9 SDK，以及 .NET Framework 4.8 和 4.6.2 targeting pack。构建所用的 NuGet 包可以从本机缓存还原。

本地预上传目录已备好离线组件库。通过 Git 获取源码后，将同版本 Release 的全量 ZIP 放入 `Updates`，使用 Python 3.11 或更高版本从仓库根目录执行：

```powershell
py -3 prepare_inputs.py Updates/Launcher-1.0.3.zip
dotnet build Source/GTA6Launcher/GTA6Launcher.csproj -c Release
```

首次启动使用 English (US)。游戏库、存档、游玩记录、键位配置、语言偏好和着色器缓存均由本机运行时生成或读取。

## 发布全量更新

当前发行标签为 `v1.0.3`。将全量 ZIP、校验文件、离线升级工具及其配置，以及独立模拟器 ZIP 和校验文件作为 `Pumo-Ayla/series-relay-launcher` 的正式 Release 附件上传，并将此发行设为 Latest。大 ZIP 和离线升级工具放在 Release 附件中，校验文件随源码提交。

仅向 `Pumo-Ayla/series-relay-launcher` 发布。使用旧仓库地址的客户端可通过离线工具迁移。

发布前可运行 `py -3 verify_release.py`，核对更新包摘要、压缩包完整性、内置组件清单，并检查源码和发布内容中是否混入用户数据。

原始 1.0.0 和 1.0.1 便携版可使用同一份 ZIP 升级。关闭启动器和游戏，将全量 ZIP、对应的 .sha256、Upgrade-Launcher.exe 和它的 .config 放入同一文件夹，双击 `Upgrade-Launcher.exe`，输入旧版 `Launcher.exe` 所在目录。工具校验 SHA-256 后就地升级，保留本地游戏、存档及配置。

## 重新打包

每次发行先向 `Updates/history.json` 追加版本号、`normal` 或 `critical` 类型和更新说明。全量 ZIP 到位后运行 `py -3 prepare_release_assets.py`，生成两个资源目录的清单、独立模拟器包和完整的 `Updates/RELEASE.md`。发布 GitHub Release 时，正文使用这个文件，保留末尾的历史记录及最低支持版本标记。

从同版本全量包恢复发布所需的二进制依赖，然后从源码重新生成便携版。输出目录应为空。

```powershell
py -3 prepare_inputs.py Updates/Launcher-1.0.3.zip --portable
py -3 Source/GTA6Launcher/Tools/package_portable.py --runtime-from Source/Runtime --output-dir Build
```

发布下一版时，先恢复构建输入，再递增启动器和日志控制台项目版本。从干净的构建输出制作完整 ZIP，使用 `Launcher-X.Y.Z.zip` 命名。将本次构建的 `Palmline.Update.exe` 及其 `.config` 分别以 `Upgrade-Launcher.exe`、`Upgrade-Launcher.exe.config` 命名，与 ZIP 一同放入 `Updates`。校验文件使用 ASCII，格式为小写 SHA-256、两个空格、ZIP 文件名。从该发行包的 `Sources/Launcher.zip` 同步 `GTA6Launcher/Assets/ComponentIndex.tsv` 到源码目录，确保源码与发行版使用相同的离线组件索引。每个 Release 只放一份与标签匹配的启动器 ZIP。

## 许可

项目原创且有权授权的代码和文档采用 [CC BY-NC 4.0](LICENSE)。模拟器、运行库、游戏素材和商标分别受各自权利人的许可约束，各模拟器的随附源码和许可文件位于对应项目目录。
