# 系统环境与依赖清单

本文档列出按 PRD 开发与运行 Recents 所需的全部系统环境、工具、运行时和 NuGet 依赖。开始编码前必须按本清单完成安装。

---

## 1. 操作系统

| 项 | 要求 | 说明 |
|---|---|---|
| 开发机 | Windows 10 1903（build 18362）及以上 / Windows 11 | PRD §2 平台约束 |
| 目标平台 | Windows 10 1903+ / Windows 11 | x64 架构 |
| 系统语言 | 任意（开发与运行均支持） | UI 默认 Segoe UI / Microsoft YaHei UI 自动回退 |

### 1.1 必须开启的系统设置

- **长路径支持**（PRD §7.6）。任选其一：
  - 注册表：`HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled = 1`（DWORD），重启生效。
  - 组策略：`计算机配置 → 管理模板 → 系统 → 文件系统 → 启用 Win32 长路径`。
- **允许执行非签名 / 本地编译的桌面应用**（消费版 Windows 默认允许，无需调整；企业版若开启 SmartScreen + Application Control 需配置例外）。

### 1.2 不需要的特权

- **不**要求管理员权限运行。
- **不**要求 UAC 提权。
- **不**需要安装驱动或服务。

---

## 2. 运行时

| 组件 | 版本 | 用途 | 安装地址 |
|---|---|---|---|
| .NET 8+ Desktop Runtime | 8.0+ | 终端用户运行 Recents | https://dotnet.microsoft.com/download/dotnet/8.0 |

> 开发机安装 .NET 8 SDK 即自动包含 Desktop Runtime，终端用户机只需 Desktop Runtime。

---

## 3. 开发环境

### 3.1 必装

| 组件 | 推荐版本 | 用途 |
|---|---|---|
| .NET 8+ SDK | 8.0.100 或更新 | 编译 / `dotnet build` |
| Git for Windows | 2.40+ | 源码版本控制 |

### 3.2 IDE（任选其一）

| IDE | 最低版本 | 必装 Workload / 插件 |
|---|---|---|
| Visual Studio 2022 | 17.8+ | "**.NET 桌面开发**" workload（含 WPF 设计器、Windows SDK） |
| JetBrains Rider | 2023.3+ | 内置 .NET / WPF 支持，无需额外插件 |
| Visual Studio Code | 1.85+ | 扩展：**C# Dev Kit**、**XAML Styler**（可选） |

### 3.3 推荐辅助工具

| 工具 | 版本 | 用途 |
|---|---|---|
| Windows Terminal | 任意 | 体验更好的终端 |
| PowerShell 7+ | 7.4+ | 构建脚本（如有） |
| dotnet-format | 与 SDK 同版本 | 代码格式化（CI 可调用） |
| Process Monitor (Sysinternals) | 任意 | 调试 FileSystemWatcher 行为、占位符触发问题 |

---

## 4. NuGet 依赖（按 PRD §6 模块映射）

> 实际版本号在第一阶段引入时确定，以下为推荐选型与最低主版本。所有依赖均可从 nuget.org 公开获取，**不引入需登录或私有源的包**。

| 模块（PRD 节） | 包 | 说明 |
|---|---|---|
| §6.18 SQLite 索引 | `Microsoft.Data.Sqlite` ≥ 8.0 | 索引库 `index.db` |
| §6.18 SQLite 索引（可选） | `Dapper` ≥ 2.1 | 轻量数据访问，可不引入直接用 ADO.NET |
| §6.4 .lnk 解析 | `Securify.ShellLink` ≥ 1.x | 纯托管 MS-SHLLINK 二进制解析 |
| §6.3.5 Jump List 解析 | `OpenMcdf` ≥ 2.x | OLECF 复合文档解析 |
| §6.2 系统托盘 | `H.NotifyIcon.Wpf` ≥ 2.x | WPF 托盘控件（首版可选；也可用纯 P/Invoke） |
| MVVM 基础 | `CommunityToolkit.Mvvm` ≥ 8.2 | `ObservableObject`、`RelayCommand` |
| §12 日志 | `Serilog` ≥ 4.x | 结构化日志 |
| §12 日志 | `Serilog.Sinks.File` ≥ 6.x | 写入 `%LOCALAPPDATA%\Recents\logs\` |
| §6.1 全局快捷键 | （无需引入第三方） | 直接 P/Invoke `RegisterHotKey` |
| §6.11 系统图标 | （无需引入第三方） | P/Invoke `SHGetFileInfo` / `IShellItemImageFactory` |
| §8 JSON 配置 | `System.Text.Json`（运行时内置） | 读写 `settings.json` |

### 4.1 不引入的依赖

明确不允许引入的依赖类型：

- **任何云盘 SDK**（OneDrive、Google Drive、Dropbox、SharePoint）—— PRD §4.2 禁止云集成。
- **任何 HTTP / 网络上报库**（除用户主动添加的 UNC 路径外不联网）。
- **任何浏览器历史 / 聊天工具数据库读取库**。
- **Electron / Chromium / WebView2 作为主 UI** —— PRD §2 限定 WPF。
- **WSH `IWshRuntimeLibrary`** —— PRD §6.4 明确弃用，理由是 STA 限制与性能差。

---

## 5. 运行期文件路径

应用运行时会读写以下路径（无需管理员权限即可访问）：

| 路径 | 类型 | 说明 |
|---|---|---|
| `%APPDATA%\Recents\settings.json` | 写 | 用户配置 |
| `%LOCALAPPDATA%\Recents\index.db` | 读写 | SQLite 索引 |
| `%LOCALAPPDATA%\Recents\icons\` | 读写 | 图标缓存 |
| `%LOCALAPPDATA%\Recents\logs\recents.log` | 写 | 滚动日志 |
| `%APPDATA%\Microsoft\Windows\Recent` | 读 / 监听 | Recent .lnk |
| `%APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations` | 读 / 监听 | Jump List |
| `%APPDATA%\Microsoft\Windows\Recent\CustomDestinations` | 读 / 监听 | 用户固定项 |
| 已知文件夹（Downloads/Desktop/Documents/Pictures/Videos/Music） | 读 / 监听 | L1 数据源 |
| `HKCU\Software\Microsoft\Office\*\User MRU` | 读 | Office MRU |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU` | 读 | 文件对话框历史 |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths` | 读 | 地址栏历史 |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Recents` | 写（仅启用自启时） | 开机自启 |
| 用户在 Sources 中添加的本地路径 | 读 / 监听 | 自定义来源 |
| 用户在 Sources 中添加的 UNC 路径 | 读 / 监听 | 跨网络访问 |

---

## 6. 网络与防火墙

| 项 | 要求 |
|---|---|
| 互联网访问 | **不需要**（应用本身不联网） |
| 内网 SMB 访问 | 仅当用户在 Sources 中添加 UNC 路径时需要（如 `\\10.0.0.100\claw\`） |
| 防火墙规则 | 应用作为 SMB **客户端**访问用户配置的服务器，通常无需新增入站规则 |

---

## 7. 构建与发布

| 项 | 要求 |
|---|---|
| 构建命令 | `dotnet build -c Release` |
| 发布命令（自包含 EXE） | `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true` |
| 发布命令（依赖运行时） | `dotnet publish -c Release -r win-x64 --self-contained false` |
| 平台 | `win-x64`（首版仅 x64） |
| 部署形态 | 独立 EXE，**不**打包为 MSIX（PRD §2，避免 AppContainer 沙盒访问限制） |

---

## 8. 安装顺序建议（新机）

1. 安装最新 Windows 累积更新。
2. 启用长路径支持（§1.1）。
3. 安装 Git for Windows。
4. 安装 .NET 8 SDK（自动带上 Desktop Runtime）。
5. 安装 IDE（VS 2022 / Rider / VS Code 任选其一）。
6. clone 仓库后执行 `dotnet restore`，IDE 自动恢复 NuGet 包。
7. `dotnet build` 验证可编译。

---

## 9. 校验命令

完成安装后，PowerShell 执行以下命令均应成功输出版本号：

```powershell
dotnet --version              # 期望 8.0.x
dotnet --list-sdks            # 期望含 8.0.x
dotnet --list-runtimes        # 期望含 Microsoft.WindowsDesktop.App 8.0.x
git --version                 # 期望 2.40+
```

注册表长路径检查：

```powershell
Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name 'LongPathsEnabled'
# 期望 LongPathsEnabled : 1
```
