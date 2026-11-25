# Palmline Launcher 棕榈线启动器

用于 Grand Theft Auto VI 兼容性实验的 Windows 桌面启动器。可选项目是本地 Z 集成版、ZETTROX 和 VIctory · Belobug AMD。打开窗口或切换项目会安排着色器预热，中文控制台显示进度。预热结束后可手动启动游戏。

## 构建

在安装了 .NET Framework 4.8 的 64 位 Windows 上，从仓库目录执行下列命令。

```powershell
.\build.ps1
```

构建产物是 `dist\Palmline Launcher.exe` 和配套的 `dist\XCL-VIctory-Lab` 目录。若要自定义封面，可在构建前将自己的图片命名为 `rooftop.jpg`，放在构建脚本旁边。

## 运行

启动器使用 Python 3 调度本机工具。下载源码即可构建界面。预热和启动游戏还需要用户自己的游戏安装和已经配置好的 XCL-VIctory-Lab 集成环境，包括对应版本的运行时、预热工具、着色器选择器和启动清单。部署时将构建产物按原有层级放在该环境的游戏根目录。各后端的目录约定见 `session.py` 和 `launcher.py`。

| 文件 | 用途 |
| --- | --- |
| `DesktopLauncher.cs`、`Main.xaml`、`Console.xaml` | WPF 主窗口、项目切换和中文控制台 |
| `session.py` | 对所选项目安排预热，处理手动启动命令 |
| `launcher.py`、`prepare_startup.py` | 匹配启动着色器，配置运行环境并调用预热工具 |

相关上游项目包括 [ZETTROX](https://github.com/Zettrox/ztrx)、[VIctory · Belobug AMD](https://github.com/Belobug/VIctory-Belobug-AMD) 和 [VIctory](https://github.com/Marvisak/VIctory)。本启动器为独立社区项目。
