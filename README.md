# Recents

[![Windows](https://img.shields.io/badge/Windows-10%201903%2B%20%7C%2011-0078D4?style=for-the-badge&logo=windows&logoColor=white)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-8%2B-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](#development)
[![WPF](https://img.shields.io/badge/UI-WPF-5C2D91?style=for-the-badge)](#technology)
[![License](https://img.shields.io/badge/License-Apache--2.0-10B981?style=for-the-badge)](LICENSE)

**Recents is a local-first Windows command center for recent files, folders, and clipboard history.**

Use a global hotkey to bring up the files you just saved, downloaded, opened, copied, or edited. Search them, preview them, drag them into another app, paste clipboard items back to the active window, or pin your frequent handoff targets.

[中文](#recents-中文)

## Screenshots

<p align="center">
  <img src="docs/images/recents-feature-overview.png" alt="Recents feature overview" width="960">
</p>

<p align="center">
  <img src="docs/images/recents-clipboard-overview.png" alt="Recents clipboard history overview" width="960">
</p>

## Why Recents?

Windows keeps useful activity trails in many places: Recent Items, Office MRU, Open/Save dialogs, Downloads, Desktop, Documents, network shares, and the clipboard. At the same time, the built-in clipboard is too temporary for real work: copied text, images, files, HTML snippets, and rich text often disappear right when you need to reuse them.

Recents brings recent files and clipboard history into one fast, keyboard-friendly surface. It treats "the thing I just worked with" as a single workflow, whether that thing is a file on disk or a clipboard item waiting to be pasted.

It is designed for people who constantly hand files and snippets between tools: chat apps, browsers, forms, Office, IDEs, image editors, CAD tools, file servers, and internal systems.

## Core Features

| Capability | What it does |
| --- | --- |
| **Global file launcher** | Press `Alt+Shift+Z` to show or hide the main Recents window from anywhere. |
| **Pop Paste clipboard launcher** | Press `Alt+Shift+V` to search clipboard history and paste the selected item into the active app. |
| **Multi-source recent-file index** | Watches known folders, custom folders, UNC shares, Windows Recent Items, Office MRU, and Open/Save dialog MRU. |
| **Real file paths** | Resolves `.lnk` shortcuts and always acts on original files when opening, revealing, copying, previewing, or dragging. |
| **Drag to any app** | Drag one or more files into chat apps, browsers, mail clients, upload forms, Explorer, and other Windows programs. |
| **Fast search and filters** | Search by filename, extension, path, clipboard text, and metadata; filter by All Files, Docs, Images, Folders, and Clipboard. |
| **Quick preview** | Press `Space` to preview files or clipboard items with WebView2. Supports images, PDF, text, HTML, CSV, code, Markdown, audio, and video. |
| **Favorites drawer** | Pin files, folders, and clipboard snapshots into one persistent drawer and reorder them. |
| **Open With history** | Keep recently used apps per extension/folder type for faster handoff to the right tool. |
| **Tray resident** | Runs as a single-instance tray app with launch-at-startup, start-minimized, always-on-top, close-to-tray, and hide-on-focus-lost options. |

## File Workflow

1. Press `Alt+Shift+Z`.
2. Type part of a filename, extension, folder path, or source.
3. Use `Up` / `Down` to select.
4. Press `Enter` to open, `Space` to preview, or drag the file to another app.
5. Use the context menu to reveal in Explorer, open with a specific app, copy paths, pin, hide, or remove an item.

Recents stores a local SQLite index so the list is ready immediately at startup, then refreshes sources in the background.

## Clipboard Workflow

Clipboard history is optional and configurable. When enabled, Recents can capture:

- Text
- Files and folders
- Images
- HTML
- Rich text

Use `Alt+Shift+V` to open Pop Paste over the current app. Type to filter, use `Up` / `Down` to move, press `Enter` to paste, press `Space` to preview, and press `Esc` to close. `Ctrl+Click` pastes text-like items as plain text.

Clipboard settings include max items, retention days, max text length, max image size, source-app exclusions, sensitive text filters, temporary pause/resume, and optional restoration of the previous clipboard after paste.

## Data Sources

| Source | Purpose |
| --- | --- |
| Known folders | Downloads, Desktop, Documents, Pictures, Videos, and Music. |
| Custom local folders | Any folder you add in Settings. |
| UNC/network paths | Network shares such as `\\server\share\project`; unavailable sources are marked unknown without blocking the UI. |
| Windows Recent Items | Resolves `.lnk` entries under the Windows Recent folder back to original targets. |
| Office MRU | Reads Microsoft Office recent-file records from the current user registry hive. |
| Open/Save dialog MRU | Reads Explorer common-dialog MRU entries and decodes PIDL paths where possible. |
| Clipboard store | Stores captured clipboard metadata and payload snapshots locally when clipboard history is enabled. |

## Preview

Quick preview uses WebView2 and a local virtual host mapping for safe rendering of supported local content. It handles ordinary files and clipboard items, including clipboard images, HTML fragments, rich text, and file lists.

Unsupported, too-large, missing, or unavailable items fall back to a clear preview state instead of blocking the main window.

## Privacy

Recents is local-first:

- No account or login
- No cloud sync
- No telemetry pipeline
- No upload of files or clipboard contents
- No deletion of original files
- Settings, indexes, icon cache, logs, and clipboard data stay under your Windows user profile

Clipboard history is off by default in settings. Sensitive text patterns and excluded source apps are configurable, and password-manager style apps are excluded by default.

## What Recents Is Not

Recents is not a cloud drive, file manager, full-text search engine, sync service, plugin host, automation platform, or account-based product. Mounted cloud folders can be added as normal local folders, but Recents does not perform vendor-specific cloud integration or trigger cloud-only downloads.

## Requirements

- Windows 10 1903 or later, or Windows 11
- x64
- .NET 8 Desktop Runtime or a newer compatible .NET runtime for the framework-dependent build
- Microsoft Edge WebView2 Runtime for quick preview

The self-contained release package includes the .NET/Windows Desktop runtime. WebView2 may still be required by Windows if it is not already installed.

## Download

Download builds from [GitHub Releases](https://github.com/phoenixray2000/recents/releases).

- **Framework-dependent package**: smaller download, requires .NET Desktop Runtime.
- **Self-contained package**: larger download, includes the .NET runtime.

## Development

```powershell
git clone https://github.com/phoenixray2000/recents.git
cd recents
dotnet restore Recents.sln
dotnet build Recents.sln -c Release --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
```

Run the published app:

```powershell
.\publish\Recents.exe
```

For normal validation after packages are already restored:

```powershell
dotnet build Recents.sln --no-restore
```

## Technology

- C# / .NET 8+ with `RollForward=Major`
- WPF desktop UI
- Windows Forms `NotifyIcon` for tray residency
- Win32 `RegisterHotKey` for global shortcuts
- `FileSystemWatcher` for folder monitoring
- SQLite for local recent-file and clipboard indexes
- JSON settings under the user profile
- WebView2 for quick preview rendering
- Apache License 2.0

## License

Licensed under the [Apache License 2.0](LICENSE).

---

# Recents 中文

[![Windows](https://img.shields.io/badge/Windows-10%201903%2B%20%7C%2011-0078D4?style=for-the-badge&logo=windows&logoColor=white)](#系统要求)
[![.NET](https://img.shields.io/badge/.NET-8%2B-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](#开发)
[![WPF](https://img.shields.io/badge/UI-WPF-5C2D91?style=for-the-badge)](#技术栈)
[![许可证](https://img.shields.io/badge/License-Apache--2.0-10B981?style=for-the-badge)](LICENSE)

**Recents 是一个本地优先的 Windows 最近文件、文件夹与剪贴板历史工作台。**

用全局快捷键呼出刚保存、下载、打开、复制或编辑过的内容。你可以搜索、预览、拖到其他应用、把剪贴板条目粘贴回当前窗口，或者把高频使用的文件、文件夹和剪贴板快照固定起来。

[English](#recents)

## 截图

<p align="center">
  <img src="docs/images/recents-feature-overview.png" alt="Recents 功能概览截图" width="960">
</p>

<p align="center">
  <img src="docs/images/recents-clipboard-overview.png" alt="Recents 剪贴板历史功能概览截图" width="960">
</p>

## 为什么做 Recents？

Windows 会在很多地方留下最近活动记录：Recent Items、Office MRU、打开/保存对话框、下载目录、桌面、文档、网络共享盘和剪贴板。同时，系统自带剪贴板对真实工作流来说太临时：复制过的文本、图片、文件、HTML 片段和富文本，常常在需要复用时已经被下一次复制覆盖。

Recents 把最近文件和剪贴板历史收进同一个快速、键盘友好的界面。它把「刚才处理过的东西」视为同一种工作流，无论那是磁盘上的文件，还是等待粘贴回当前应用的剪贴板条目。

它面向每天在多个工具之间搬运文件和片段的人：聊天软件、浏览器、网页表单、Office、IDE、图像编辑器、CAD、文件服务器和内部系统。

## 核心功能

| 能力 | 说明 |
| --- | --- |
| **全局文件启动器** | 按 `Alt+Shift+Z` 在任何地方呼出或隐藏主窗口。 |
| **Pop Paste 剪贴板启动器** | 按 `Alt+Shift+V` 搜索剪贴板历史，并把选中条目粘贴到当前应用。 |
| **多来源最近文件索引** | 监听已知文件夹、自定义目录、UNC 共享，并读取 Windows Recent Items、Office MRU、打开/保存对话框 MRU。 |
| **真实文件路径** | 解析 `.lnk` 快捷方式，打开、定位、复制、预览、拖拽时都作用于原始文件。 |
| **拖到任意应用** | 可把一个或多个文件拖到聊天软件、浏览器、邮件客户端、上传表单、Explorer 和其他 Windows 程序。 |
| **快速搜索与筛选** | 按文件名、扩展名、路径、剪贴板文本和元数据搜索；按 All Files、Docs、Images、Folders、Clipboard 筛选。 |
| **快速预览** | 按 `Space` 使用 WebView2 预览文件或剪贴板条目，支持图片、PDF、文本、HTML、CSV、代码、Markdown、音频和视频。 |
| **收藏抽屉** | 把文件、文件夹和剪贴板快照固定到同一个持久收藏抽屉，并支持排序。 |
| **打开方式历史** | 按扩展名和文件夹类型保留最近使用的应用，便于快速交给正确工具处理。 |
| **常驻托盘** | 单实例托盘应用，支持开机自启、启动最小化、置顶、失焦隐藏和关闭到托盘。 |

## 文件工作流

1. 按 `Alt+Shift+Z`。
2. 输入文件名、扩展名、路径片段或来源。
3. 用 `Up` / `Down` 选择。
4. 按 `Enter` 打开，按 `Space` 预览，或直接拖到其他应用。
5. 通过右键菜单定位到 Explorer、选择打开方式、复制路径、固定、隐藏或移除条目。

Recents 使用本地 SQLite 索引，因此启动时可以立即显示缓存列表，并在后台刷新来源。

## 剪贴板工作流

剪贴板历史是可选功能，可在设置中开启和配置。开启后可以捕获：

- 文本
- 文件和文件夹
- 图片
- HTML
- 富文本

按 `Alt+Shift+V` 可在当前应用上方打开 Pop Paste。直接输入过滤内容，用 `Up` / `Down` 移动，按 `Enter` 粘贴，按 `Space` 预览，按 `Esc` 关闭。`Ctrl+单击` 可把文本类条目作为纯文本粘贴。

剪贴板设置包括最大条目数、保留天数、最大文本长度、最大图片大小、来源程序排除、敏感文本过滤、临时暂停/恢复，以及粘贴后是否恢复之前的剪贴板。

## 数据来源

| 来源 | 用途 |
| --- | --- |
| 已知文件夹 | Downloads、Desktop、Documents、Pictures、Videos、Music。 |
| 自定义本地目录 | 在设置里手动添加的任意目录。 |
| UNC / 网络路径 | 例如 `\\server\share\project`；网络不可用时标记为 unknown，不阻塞界面。 |
| Windows Recent Items | 读取 Windows Recent 文件夹里的 `.lnk`，并解析回原始目标路径。 |
| Office MRU | 从当前用户注册表读取 Microsoft Office 最近文件记录。 |
| 打开/保存对话框 MRU | 读取 Explorer 通用对话框 MRU，并尽量解析 PIDL 路径。 |
| 剪贴板存储 | 启用剪贴板历史后，在本地保存剪贴板元数据和内容快照。 |

## 预览

快速预览使用 WebView2，并通过本地虚拟主机映射安全渲染支持的本地内容。它既能处理普通文件，也能处理剪贴板图片、HTML 片段、富文本和文件列表。

不支持、过大、缺失或不可访问的条目会显示明确状态，不会阻塞主窗口。

## 隐私

Recents 是本地优先的桌面工具：

- 无账号登录
- 无云同步
- 无遥测管线
- 不上传文件或剪贴板内容
- 不删除原始文件
- 设置、索引、图标缓存、日志和剪贴板数据保存在你的 Windows 用户目录下

剪贴板历史默认关闭。敏感文本规则和排除来源程序可以配置，密码管理器类应用默认排除。

## Recents 不做什么

Recents 不是云盘、文件管理器、全文搜索引擎、同步服务、插件宿主、自动化平台，也不是需要账号登录的产品。已挂载到本地的云盘目录可以作为普通目录添加，但 Recents 不会做厂商特定的云盘集成，也不会触发 cloud-only 文件下载。

## 系统要求

- Windows 10 1903 或更新版本，或 Windows 11
- x64
- framework-dependent 包需要 .NET 8 Desktop Runtime 或更高兼容版本
- 快速预览需要 Microsoft Edge WebView2 Runtime

self-contained 发布包包含 .NET/Windows Desktop runtime。如果系统尚未安装 WebView2，预览功能仍可能需要安装 WebView2 Runtime。

## 下载

从 [GitHub Releases](https://github.com/phoenixray2000/recents/releases) 下载发布包。

- **framework-dependent 包**：体积更小，需要 .NET Desktop Runtime。
- **self-contained 包**：体积更大，内置 .NET runtime。

## 开发

```powershell
git clone https://github.com/phoenixray2000/recents.git
cd recents
dotnet restore Recents.sln
dotnet build Recents.sln -c Release --no-restore
dotnet publish src\Recents.App\Recents.App.csproj -c Release --no-restore -o publish
```

运行发布产物：

```powershell
.\publish\Recents.exe
```

依赖已经还原后，日常验证命令：

```powershell
dotnet build Recents.sln --no-restore
```

## 技术栈

- C# / .NET 8+，运行时允许 `RollForward=Major`
- WPF 桌面 UI
- Windows Forms `NotifyIcon` 实现托盘常驻
- Win32 `RegisterHotKey` 实现全局快捷键
- `FileSystemWatcher` 实现目录监听
- SQLite 保存本地最近文件和剪贴板索引
- JSON 保存用户配置
- WebView2 用于快速预览渲染
- Apache License 2.0

## 许可证

本项目使用 [Apache License 2.0](LICENSE) 许可协议。
