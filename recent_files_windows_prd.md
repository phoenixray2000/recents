# Windows Recent Files Launcher PRD

## 0. 文档定位

本文档是本项目的唯一产品真理源。后续设计、编码、测试、重构均以本文档为准。

项目目标：开发一个 Windows 桌面效率工具，通过全局快捷键呼出一个轻量窗口，展示**最近真实文件**与最近文件夹，支持搜索、筛选、拖拽、右键处理、收藏固定、排除规则和常驻托盘。

目标用户：Windows 桌面重度用户，需要频繁把最近文件拖入微信、飞书、浏览器、Office、PS、CAD、IDE 或其他应用中打开、上传、编辑或处理。

核心判断：本工具优先解决"快速找到最近真实文件并交给其他应用处理"的场景。所谓"最近文件"以**文件系统真实变更事件**为唯一权威来源（参考 macOS Trickster 通过 FSEvents 自建索引的做法），不依赖任何单一系统列表。

不做：全文搜索、内容预览、插件系统、复杂自动化、云同步、登录账号。

---

## 1. 产品名称

Recents。

---

## 2. 技术栈约束

首版技术栈固定为：

- 语言：C#
- 运行时：.NET 8 LTS
- UI：WPF
- 平台：Windows 10 1903+ / Windows 11
- 架构：单机本地应用，单实例
- 数据存储：本地 JSON 配置 + 本地 SQLite 索引 + 本地图标缓存
- 后台常驻：系统托盘
- 快捷键：Windows API RegisterHotKey
- 文件监听：FileSystemWatcher
- 部署形态：独立 EXE（非 MSIX，避免沙盒访问受限）

不使用 Electron、Tauri、WebView 作为主 UI。

---

## 3. 核心用户场景

### 3.1 快速上传最近文件

用户刚保存或下载了一个文件，需要发到微信、飞书、邮件、网页表单或客户系统。

操作流程：
1. 按全局快捷键呼出窗口。
2. 最近文件列表默认按最近时间倒序显示。
3. 用户拖拽目标文件到其他应用。
4. 其他应用收到真实文件路径。

验收标准：拖拽传递的是原始文件路径，不是 .lnk 快捷方式。**刚下载完成的文件应在 2 秒内出现在列表顶部。**

### 3.2 快速用指定应用打开文件

用户需要把最近文件交给特定应用处理，例如用 Photoshop 打开图片，用 Excel 打开表格，用 VS Code 打开脚本。

操作流程：
1. 呼出窗口。
2. 右键目标文件。
3. 选择"打开方式"。
4. 系统弹出 Windows 原生打开方式选择器。

验收标准：打开方式作用于原始文件，不作用于 .lnk。

### 3.3 快速定位文件所在目录

用户知道刚处理过某个文件，但忘记路径。

操作流程：
1. 呼出窗口。
2. 搜索文件名或扩展名。
3. 右键选择"打开所在位置"。
4. Explorer 打开原文件所在目录并选中文件。

### 3.4 通过键盘快速打开文件

用户偏键盘操作。

操作流程：
1. 呼出窗口后自动聚焦搜索框。
2. 输入关键词。
3. 上下方向键选择。
4. Enter 打开文件。
5. Esc 隐藏窗口。

### 3.5 处理网络共享盘上的文件

用户公司有一台 NAS 或文件服务器（如 `\\10.0.0.100\claw\`），希望最近写入的素材也出现在 Recents 列表中。

操作流程：
1. 用户在设置 → Sources 中添加 `\\10.0.0.100\claw\` 为映射监听目录。
2. 工具开始监听该 UNC 路径。
3. 该路径下的新文件、被修改的文件按时间进入主列表。

验收标准：UNC 路径在网络可达时，文件能被索引；网络断开时，已索引的文件标记为 Unknown 状态，不阻塞 UI。

---

## 4. 首版功能范围

### 4.1 必须实现

- 全局快捷键呼出 / 隐藏
- 系统托盘常驻 + 单实例
- 开机自启开关
- **多源最近文件采集**（见 §6.3）
- 解析 .lnk 快捷方式为真实文件路径
- 最近文件列表展示
- 最近文件夹列表展示
- 文件存在性校验（异步、超时控制）
- 最近优先排序
- 搜索过滤
- 文件类型筛选
- 拖拽原始文件到其他 App
- 双击打开原始文件
- 右键基础菜单
- 图标显示（异步、缓存）
- 快速预览信息
- 键盘操作
- 排除规则（扩展名、路径、关键词）
- 黑名单 / 白名单路径
- 自定义监听目录（含 UNC 路径，例 `\\10.0.0.100\claw\`）
- 最近文件数量限制
- 缓存机制（SQLite）
- 设置页
- 收藏文件 / 固定文件

### 4.2 首版不做

- 全文内容搜索
- OCR
- 文件内容预览
- 插件系统
- **云同步 / OneDrive / SharePoint 集成**
- 多设备同步
- 用户登录
- 文件版本管理
- 复杂自动化工作流
- 内置文件编辑器
- 浏览器历史 / 下载库 SQLite 解析
- UWP / MSIX 沙盒应用打开记录

明确说明：本工具**不做云盘**。OneDrive、Google Drive 等若已挂载为本地目录，可由用户**手动**添加为监听源；工具对其中的占位符（cloud-only）文件**不做特殊处理**，仅按本地文件对待，且不会触发联网下载（见 §6.19）。

---

## 5. 信息架构

### 5.1 主窗口

主窗口由以下区域组成：

1. 标题栏
2. 搜索区
3. 左侧导航栏
4. 文件列表区
5. 文件行快捷操作区
6. 底部状态栏

建议布局：

```text
┌────────────────────────────────────┐
│ Recents                       ⚙  × │
├────────────────────────────────────┤
│ Search by name / extension / path  │
├──────┬─────────────────────────────┤
│ ★    │  File item                  │
│ 🕘   │  File item                  │
│ 📁   │  File item                  │
│ 📄   │  File item                  │
│ 🖼   │  File item                  │
│ 🎬   │  File item                  │
│ ⚙    │  File item                  │
├──────┴─────────────────────────────┤
│ 200 items · sorted by recent time   │
└────────────────────────────────────┘
```

### 5.2 左侧导航栏

左侧导航项：

- All Recent
- Favorites
- Recent Folders
- Documents
- Images
- Videos
- Audio
- Archives
- Code
- Other
- Settings

导航项可以用图标展示，鼠标悬停时显示名称。

### 5.3 文件列表项

每个文件项展示：

- 文件图标
- 文件名
- 所在路径
- 最近时间
- 文件大小
- 文件类型
- 是否收藏
- 是否存在
- 数据来源标记（小图标，可选展示）
- 快捷操作按钮

单行建议结构：

```text
[icon] SalesContract.docx             [open] [pin]
       D:\Work\Contracts
       Modified: 2026-05-01 13:42 · 128 KB
```

如果文件不存在：

```text
[icon] SalesContract.docx
       File missing · original path: D:\Work\Contracts\SalesContract.docx
```

不存在文件默认灰显，不默认隐藏；用户可在设置中选择隐藏不存在文件。

---

## 6. 核心功能详细要求

### 6.1 全局快捷键

默认快捷键：`Ctrl + Alt + R`。

要求：
- 支持用户在设置页修改快捷键。
- 注册失败时自动尝试候选序列：`Win + ;` → `Ctrl + Shift + Space` → `Ctrl + Alt + Space`，最终失败在托盘气泡和设置页提示。
- 按快捷键时，如果窗口未显示，则显示并聚焦搜索框。
- 按快捷键时，如果窗口已显示，则隐藏窗口。
- 窗口显示位置默认在当前鼠标所在屏幕中央。

实现建议：使用 Windows API `RegisterHotKey`。封装为 `HotkeyService`。

### 6.2 托盘常驻

要求：
- 应用启动后常驻系统托盘。
- 关闭主窗口时默认隐藏到托盘，不退出程序。
- 托盘菜单包含：显示窗口、设置、重新扫描、开机自启、退出。
- 退出必须真正结束进程并释放全局热键。

### 6.3 最近文件来源（多源融合，**核心改动**）

本工具不依赖单一系统列表。所有数据源统一抽象为 `IRecentSource`，由 `RecentIndexService` 融合去重，写入本地 SQLite 索引（`%LOCALAPPDATA%\Recents\index.db`）。

#### 6.3.1 数据源分层

| 层级 | 数据源 | 主要覆盖 | 实时性 | 必选 |
|---|---|---|---|---|
| L1 | **直接文件系统监听**（已知文件夹 + 用户自定义目录） | 下载、保存、生成、复制等所有写入 | 实时 | ✅ 必选 |
| L1 | `%APPDATA%\Microsoft\Windows\Recent` 中的 .lnk | Explorer / Office 显式"打开"信号 | 准实时 | ✅ 必选 |
| L2 | **Jump List** `AutomaticDestinations`（`*.automaticDestinations-ms`） | 各 App 自身的 MRU（IDE、浏览器、PS、CAD 等） | 准实时 | ✅ 必选 |
| L2 | **Office MRU 注册表** `HKCU\Software\Microsoft\Office\*\User MRU` | Office 文件打开历史 | 写入即变 | 可选（首版做） |
| L3 | **`OpenSavePidlMRU` 注册表** `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU` | 通用文件对话框历史 | 写入即变 | 可选（首版做） |
| L3 | **CustomDestinations** `*.customDestinations-ms`（用户固定项） | 各 App 用户固定项 | 写入即变 | 可选（首版做） |

L1 是数据底座，L2/L3 用来补充"用户操作意图"信号。每条最终 RecentItem 的 `RecentTime` 取所有命中来源里 `max(LastWriteTime, ExplicitOpenTime)`，并附带 `SourceKinds`（位掩码）用于调试与"从来源 X 隐藏"功能。

#### 6.3.2 L1：默认监听目录

启动时通过 `SHGetKnownFolderPath` 解析以下已知文件夹，递归监听：

| KNOWNFOLDERID | 默认含义 |
|---|---|
| `FOLDERID_Downloads` | 下载 |
| `FOLDERID_Desktop` | 桌面 |
| `FOLDERID_Documents` | 文档 |
| `FOLDERID_Pictures` | 图片 |
| `FOLDERID_Videos` | 视频 |
| `FOLDERID_Music` | 音乐 |

> 必须用 `SHGetKnownFolderPath`，不得硬编码 `C:\Users\<x>\Downloads`。组策略、用户重定向、OneDrive Known Folder Move 都会改写真实路径。

启动时增量扫描（只采近 `RecentLookbackDays` 天内 `LastWriteTime`，默认 30 天），运行期由 `FileSystemWatcher` 推送变更。

#### 6.3.3 L1：用户自定义监听目录（含 UNC）

用户可在 **Settings → Sources** 中追加自定义监听路径，包括：

- 本地路径，如 `D:\Projects\`
- 网络共享 / 映射路径，如 `\\10.0.0.100\claw\`
- 已挂载的网络盘符，如 `Z:\share\`

要求：
- 每个自定义来源独立可启用 / 禁用。
- 添加 UNC 路径时，先做一次可达性测试（带 3s 超时）；不可达只警告，不拒绝添加。
- UNC 路径在网络断开时，对应的 `FileSystemWatcher` 会失效，必须监听 `Error` 事件并自动重试（指数回退，最长间隔 5 分钟）。
- UNC 来源建议默认 `RecentLookbackDays = 7`，避免初次扫描拉取过多远端文件。
- 不对 UNC 路径上的文件读取大小、缩略图，全部走"懒加载"——只在文件进入可视区域时才查 `FileInfo`。

> **不做云盘集成**：OneDrive、Google Drive、Dropbox 客户端如果在本地把目录挂载为普通文件夹，用户可像普通本地路径一样手动添加监听；本工具不识别其登录态、不读取占位符元数据、不触发联网下载（见 §6.19 OneDrive 占位符处理）。

#### 6.3.4 L1：Recent 文件夹

继续读取 `%APPDATA%\Microsoft\Windows\Recent` 下的 .lnk，作为"显式打开"信号融合进索引。.lnk 解析见 §6.4。

#### 6.3.5 L2：Jump List

解析以下两个目录中的复合文档（OLECF）：

```text
%APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations\*.automaticDestinations-ms
%APPDATA%\Microsoft\Windows\Recent\CustomDestinations\*.customDestinations-ms
```

每个文件名前缀是应用 `AppUserModelID` 的 CRC64 hash，文件内是该 App 的最近 / 固定列表。

实现要点：
- 用纯托管 OLECF 解析库（如 `OpenMcdf`），不要依赖 COM `IShellLink` 链。
- 解析失败的单文件忽略，记日志。
- 启动时全量解析一次，运行期对该目录起 `FileSystemWatcher`，文件变更时增量重解析。

#### 6.3.6 L2/L3：注册表 MRU

读取以下键，每个 value 是一条历史记录：

```text
HKCU\Software\Microsoft\Office\<ver>\<App>\User MRU\<sid>\File MRU
HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU\*
HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs
```

- Office MRU 是带时间戳的字符串，可直接拿到打开时间。
- `OpenSavePidlMRU` 内是 PIDL，需要 `SHGetPathFromIDList` 解析。
- `RecentDocs` 仅作降级补充。

注册表读取**只读**，不做监听（注册表 `RegNotifyChangeKeyValue` 在 WPF 中代价不低），改为定时（5 分钟）轮询 + 启动时刷新。

#### 6.3.7 数据源融合规则

- 主键：`NormalizedPath`（统一为长路径、小写盘符、去除尾随分隔符；UNC 保留 `\\host\share\` 前缀）。
- 同 key 多来源：`RecentTime = max(各来源时间)`；`SourceKinds |= 各来源标志位`。
- 文件夹来源：L1 监听目录中的所有目标路径所在父目录会被聚合到 Recent Folders；.lnk 直指文件夹时也进入 Recent Folders。
- 删除：仅当某来源**显式**报告删除（FileSystemWatcher.Deleted）才从索引移除；其他来源消失只移除该 SourceKind 标志，不删条目。

#### 6.3.8 重新扫描

- 用户在托盘菜单或设置页可触发"Rebuild Index"，重置 SQLite 后并发跑所有数据源。
- 每条来源失败不影响其他来源。

### 6.4 .lnk 解析

要求：
- 展示原始文件，不展示 .lnk。
- 所有打开、拖拽、右键菜单均作用于原始文件。
- 缓存 .lnk 路径、目标路径、目标参数、工作目录、最近时间。
- 如果目标是文件夹，归入 Recent Folders。
- 如果目标不存在，标记 Missing。

实现：
- **首选**：纯托管解析 MS-SHLLINK 二进制格式（NuGet `Securify.ShellLink` 或同类库）。无 COM 依赖、可在线程池并发批量解析、不要求 STA。
- **备选**：P/Invoke `IShellLinkW + IPersistFile`。
- **不使用** `WSH IWshRuntimeLibrary`（性能差、STA 限制、对损坏的 .lnk 容错差）。

性能目标：1000 个 .lnk 在 ThreadPool 上并发解析 ≤ 1.5 秒。

### 6.5 最近优先排序

默认排序：`RecentTime` 倒序，`RecentTime = max(L1 LastWriteTime, L1 CreationTime, .lnk LastWriteTime, JumpList 时间, MRU 时间)`。

备选排序（可在设置 / 列头切换）：
- 原文件 LastWriteTime
- 文件名 A-Z
- 文件类型
- 文件大小

首版默认只需要 Recent Time 倒序，UI 保留排序切换的扩展位。

### 6.6 搜索过滤

搜索范围：
- 文件名
- 扩展名
- 原始路径

搜索语法（首版固定，不暴露给用户配置）：

| 输入示例 | 解释 |
|---|---|
| `report` | 文件名 / 路径子串模糊匹配（不区分大小写） |
| `.docx` 或 `pdf` 当首字符为 `.` | 扩展名精确匹配 |
| `D:\work` 或 含 `\` `/` | 路径片段匹配 |
| `claw report` | 多 token AND |

性能要求：
- 1 万条以内输入无明显卡顿。
- 搜索在内存索引上执行，不重新扫描磁盘。
- 排序与过滤后渲染走 UI 虚拟化（`VirtualizingStackPanel`）。

### 6.7 文件类型筛选

类型分类（可在设置页修改）：

- Documents：.doc, .docx, .xls, .xlsx, .ppt, .pptx, .pdf, .txt, .md, .rtf
- Images：.png, .jpg, .jpeg, .gif, .bmp, .webp, .svg, .heic
- Videos：.mp4, .mov, .avi, .mkv, .wmv, .webm
- Audio：.mp3, .wav, .flac, .aac, .m4a
- Archives：.zip, .rar, .7z, .tar, .gz
- Code：.py, .js, .ts, .tsx, .cs, .cpp, .c, .h, .java, .go, .rs, .json, .xml, .yaml, .yml, .html, .css, .sql
- Other：未归类

要求：
- 左侧导航支持按类型筛选。
- 搜索与类型筛选可叠加。

### 6.8 拖拽原文件

要求：
- 拖拽时 `DataObject` **同时**附 `DataFormats.FileDrop`（`string[]`）和 `Shell IDList Array`（CFSTR_SHELLIDLIST），保证微信、飞书、Outlook、浏览器、资源管理器全部兼容。
- 支持多选拖拽多个文件。
- Missing 文件禁止拖拽（拖拽前判定，禁拖时鼠标显示禁止图标）。
- 文件夹也支持拖拽。
- UNC 路径文件可拖拽，由目标 App 自行处理远程访问。

实现建议：封装 `DragDropService`，验收必须覆盖：微信、飞书、Outlook、Edge、Explorer 五个目标。

### 6.9 双击打开

要求：
- 双击文件项，用系统默认程序打开原始文件。
- Missing 文件双击时提示文件不存在。
- 打开失败时展示错误提示。

实现：`ProcessStartInfo { UseShellExecute = true }`。

### 6.10 右键基础菜单

文件右键菜单必须包含：

- 打开
- 打开方式
- 打开所在位置
- 复制完整路径
- 复制文件名
- 固定 / 取消固定
- 从列表隐藏
- 删除该 Recent 记录（仅清理 Recent .lnk；其他来源不会清，UI 上注明）

说明：
- "从列表隐藏"写入隐藏规则（`hidden_paths`），不删除原文件，**不影响其他用户**。
- "删除该 Recent 记录"只删 `%APPDATA%\Microsoft\Windows\Recent` 中对应的 .lnk；**不动**原文件，不动 Jump List，不动 Office MRU（这些由各应用自己管理，强删风险高）。
- "打开方式"必须作用于原始文件。

打开方式实现：优先 `SHOpenWithDialog`（Win10/11 现代选择器），失败回退到：

```text
rundll32.exe shell32.dll,OpenAs_RunDLL "原始文件路径"
```

打开所在位置实现：

```text
explorer.exe /select,"原始文件路径"
```

### 6.11 图标显示

要求：
- 占位图标：用 `SHGetFileInfo` + `SHGFI_USEFILEATTRIBUTES`（不访问磁盘）按扩展名取通用图标，立即填入 UI。
- 真实图标：异步用 `IShellItemImageFactory.GetImage` 拉取（支持高分辨率缩略图）。
- 图标按 `扩展名 + isFolder + DPI` 缓存到 `%LOCALAPPDATA%\Recents\icons\`。
- UI 永远不被图标加载阻塞。
- UNC 路径文件不预拉取真实图标，仅用占位图标，可视进入再异步拉取，超时 3s 放弃。
- Missing 文件使用灰色默认图标。

### 6.12 快速预览信息

首版只展示元信息，不做内容预览。

展示字段：
- 文件名
- 原始路径
- 文件大小
- 文件类型
- 最近时间
- 原文件修改时间
- 文件存在状态
- 数据来源（小图标，悬停展开多个来源）

可在鼠标悬停 Tooltip 或右侧详情面板展示。

### 6.13 键盘操作

必须支持：

- `Ctrl + Alt + R`：呼出 / 隐藏（默认值，可改）
- `Esc`：隐藏窗口
- `Enter`：打开当前选中文件
- `↑ / ↓`：移动选择
- `Ctrl + C`：复制原始文件完整路径
- `Ctrl + Shift + C`：复制文件名
- `Ctrl + O`：打开所在位置
- `Ctrl + F`：聚焦搜索框
- `Delete`：隐藏该条记录，需二次确认或撤销提示

### 6.14 排除规则

排除规则包括：

- 排除扩展名
- 排除路径
- 排除文件名关键词
- 隐藏不存在文件
- 隐藏系统路径
- 隐藏临时文件

默认排除扩展名建议：

```text
.tmp, .temp, .lnk, .url, .ini, .log, .crdownload, .partial, .part, .opdownload, .!ut
```

默认排除路径关键词建议：

```text
AppData\Local\Temp
Windows\Temp
$Recycle.Bin
node_modules
.git
__pycache__
```

### 6.15 黑名单路径

- 命中黑名单路径前缀的文件不展示。
- 支持精确路径和路径前缀。
- 支持用户在设置中添加、删除。

### 6.16 白名单路径

- 如果白名单为空，不限制来源。
- 如果白名单非空，只展示命中白名单前缀的文件。
- 白名单优先级：高于普通分类，低于"文件存在性 / Missing 显示开关"。

> 注意：白名单是**展示过滤器**，不是数据源。要让一个目录被采集，必须在 **Sources** 中添加（§6.3.3）。

### 6.17 最近文件数量限制

默认显示：200 条。

设置可选：100 / 200 / 500 / 1000 / Unlimited（不建议）。

### 6.18 缓存与索引

存储分两层：
- **索引**：SQLite，`%LOCALAPPDATA%\Recents\index.db`
- **配置**：JSON，`%APPDATA%\Recents\settings.json`
- **图标缓存**：`%LOCALAPPDATA%\Recents\icons\`
- **日志**：`%LOCALAPPDATA%\Recents\logs\recents.log`

启动流程：
1. 读取 `settings.json`。
2. 打开 `index.db`（如损坏自动备份并重建）。
3. UI 立即用 SQLite 中已有数据渲染。
4. 后台并发跑各 `IRecentSource`，增量更新索引并通知 UI。

SQLite schema（核心表）：

```sql
CREATE TABLE recent_items (
    normalized_path TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    extension TEXT,
    file_type TEXT,
    recent_time INTEGER NOT NULL,        -- unix epoch ms
    target_modified_time INTEGER,
    size_bytes INTEGER,
    exists_state INTEGER NOT NULL,       -- 0=Missing,1=Exists,2=Unknown
    is_folder INTEGER NOT NULL,
    is_favorite INTEGER NOT NULL,
    is_hidden INTEGER NOT NULL,
    source_kinds INTEGER NOT NULL,       -- bitmask
    icon_cache_key TEXT,
    last_seen_time INTEGER NOT NULL
);
CREATE INDEX idx_recent_time ON recent_items(recent_time DESC);
CREATE INDEX idx_extension   ON recent_items(extension);
```

### 6.19 文件系统监听

每个 L1 监听目录起一个 `FileSystemWatcher`。

监听事件：
- Created
- Changed
- Deleted
- Renamed

要求：
- `IncludeSubdirectories = true`。
- `InternalBufferSize = 64 * 1024`（默认 8KB 在大量并发写入下会溢出）。
- 必须订阅 `Error` 事件；溢出 / 失效后，标记该来源 `Stale`，**触发该来源全量增量重扫**（按 `LastWriteTime` 比对）。
- 默认 debounce **1200ms**（兼容浏览器 `.crdownload → .ext` 重命名 + 杀毒扫描的尾部抖动）。
- 增量解析：仅对变化路径更新索引。
- 解析失败不影响主程序运行。

**OneDrive / 占位符 / 重解析点保护**：

- 枚举或 `FileInfo` 调用前，先用 `GetFileAttributes` 检查：
  - `FILE_ATTRIBUTE_RECALL_ON_OPEN` (0x40000)
  - `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` (0x400000)
  - `FILE_ATTRIBUTE_OFFLINE` (0x1000)
- 命中任一标志位的文件：**只取 Win32 元数据**（路径、名称、`LastWriteTime`、`AttributesSize`），不读 `Length`、不读缩略图、不打开文件流，避免触发系统拉云端文件下载。
- 此类文件在 UI 上**不特别标注云图标**（首版不做云盘集成），按普通本地文件展示，但内部标记跳过缩略图。

**UNC 路径监听**：

- `FileSystemWatcher` 支持 UNC，但 SMB 通知需要服务端配合。当 watcher 触发 `Error`：
  - 标记该源 `Disconnected`。
  - 启动指数回退重连（1s, 2s, 4s, ..., 上限 5min）。
  - 重连成功后做一次增量比对（`LastWriteTime > lastSeenTime` 的文件）。
- 已索引的 UNC 文件在网络断开时 `exists_state` 设为 `Unknown`（不是 Missing），UI 灰显加"⚠ 离线"小标。

### 6.20 文件存在性校验

- `File.Exists` 在断开的网络盘上会卡 5–30 秒，**禁止**在 UI 线程或同步路径上调用。
- 实现：
  - 索引中保留 `exists_state` 三态（Missing / Exists / Unknown）。
  - 进入可视区域的文件项，触发后台 1.5s 超时的 `Exists` 探测。
  - 超时统一回 `Unknown`。
  - 网络盘整源不可达时，整源批量标 `Unknown`，不再逐文件探测。

### 6.21 开机自启

要求：
- 设置页提供开机自启开关。
- 当前用户级别自启，不需要管理员权限。
- 实现：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下 `Recents` 值。
- 启动参数 `--minimized` 让进程直接进入托盘。

### 6.22 设置页

设置页分组：

1. **General**
   - Launch at startup
   - Show on active monitor
   - Hide when focus lost
   - Always on top

2. **Hotkey**
   - Current hotkey
   - Record new hotkey
   - Reset to default

3. **Sources**（**新增**）
   - 默认已知文件夹（Downloads / Desktop / Documents / Pictures / Videos / Music），各自可启用 / 禁用
   - "Add Folder…" 添加自定义本地目录
   - "Add Network Path…" 添加 UNC 路径，例 `\\10.0.0.100\claw\`
   - 各源 `RecentLookbackDays` 可配
   - 显示每个源的状态（Active / Disconnected / Disabled / Stale）

4. **List**
   - Max recent items
   - Show missing files
   - Show folders
   - Default sort

5. **Filters**
   - File type groups
   - Excluded extensions
   - Excluded paths
   - Excluded keywords
   - Whitelisted paths

6. **Cache**
   - Rebuild index
   - Clear icon cache
   - Clear hidden items

7. **About**
   - Version
   - Data paths
   - Open log folder

### 6.23 收藏文件 / 固定文件

- 用户可将文件固定到 Favorites。
- Favorites 始终显示在收藏页。
- 收藏状态保存到 SQLite `is_favorite` 字段。
- 收藏文件不存在仍显示，灰显。
- 支持取消固定。

### 6.24 最近文件夹

来源：
- L1 监听目录中文件的父目录聚合（按最近文件时间）。
- .lnk / Jump List 中目标为文件夹的条目。
- Explorer Quick Access：`%APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations\f01b4d95cf55d32a.automaticDestinations-ms`（固定 fileId，是 Quick Access）。
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths`（地址栏输入历史，作为补充）。

展示逻辑：
- 双击文件夹用 Explorer 打开。
- 文件夹支持拖拽。

---

## 7. UI 风格要求

### 7.1 总体风格

参考用户提供的 Trickster 和 Recent Files 截图。

风格关键词：
- 轻量
- 快速
- 紧凑
- 桌面工具感
- 暗色优先
- 可长期常驻

默认主题：暗色。

### 7.2 窗口尺寸

默认尺寸：

```text
宽度：520px
高度：680px
```

最小尺寸：

```text
宽度：420px
高度：480px
```

窗口支持拖动和调整大小。

### 7.3 窗口行为

- 默认置顶。
- 默认不显示在任务栏，可在设置中切换。
- 默认失焦不自动隐藏，可在设置中开启。
- Esc 隐藏窗口。
- 点击托盘图标显示窗口。
- 高 DPI / 多显示器：app.manifest 声明 `PerMonitorV2`；窗口位置用鼠标当前所在显示器的 work area 居中。

### 7.4 颜色

暗色主题建议：

```text
背景：#2F2F2F
面板：#3A3A3A
列表项悬停：#454545
选中项：#505050
主文字：#F2F2F2
副文字：#B8B8B8
强调色：#00D7FF
边框：#4A4A4A
危险色：#D9534F
离线/未知：#7A7A7A
```

### 7.5 字体

默认 Windows 系统字体 `Segoe UI`，中文环境自然回退到 `Microsoft YaHei UI`。

### 7.6 长路径与本地化

- app.manifest 声明 `<longPathAware>true</longPathAware>` + `dpiAwareness=PerMonitorV2`。
- 所有路径处理走 Unicode；UI 显示长路径中段省略 `D:\very\long\…\Contracts\file.docx`。

---

## 8. 数据模型

### 8.1 RecentItem

```csharp
public class RecentItem
{
    public string NormalizedPath { get; set; }   // 主键
    public string DisplayName { get; set; }
    public string Extension { get; set; }
    public string FileType { get; set; }
    public DateTime RecentTime { get; set; }
    public DateTime? TargetModifiedTime { get; set; }
    public long? SizeBytes { get; set; }
    public ExistsState Exists { get; set; }      // Missing / Exists / Unknown
    public bool IsFolder { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsHidden { get; set; }
    public SourceKinds Sources { get; set; }     // Flags 位掩码
    public string IconCacheKey { get; set; }
}

[Flags]
public enum SourceKinds
{
    None              = 0,
    KnownFolderWatch  = 1 << 0,   // Downloads/Desktop/...
    UserFolderWatch   = 1 << 1,   // 自定义本地目录
    UncFolderWatch    = 1 << 2,   // 自定义 UNC 路径
    RecentLnk         = 1 << 3,
    JumpListAuto      = 1 << 4,
    JumpListCustom    = 1 << 5,
    OfficeMru         = 1 << 6,
    OpenSavePidlMru   = 1 << 7,
    RecentDocsReg     = 1 << 8,
}

public enum ExistsState { Missing = 0, Exists = 1, Unknown = 2 }
```

### 8.2 SourceConfig

```csharp
public class SourceConfig
{
    public string Id { get; set; }                  // GUID
    public SourceKinds Kind { get; set; }
    public string Path { get; set; }                // 本地或 UNC
    public bool Enabled { get; set; } = true;
    public int RecentLookbackDays { get; set; } = 30;
}
```

### 8.3 AppSettings

```csharp
public class AppSettings
{
    public bool LaunchAtStartup { get; set; } = false;
    public bool AlwaysOnTop { get; set; } = true;
    public bool HideOnFocusLost { get; set; } = false;
    public bool ShowMissingFiles { get; set; } = true;
    public bool ShowFolders { get; set; } = true;
    public int MaxRecentItems { get; set; } = 200;
    public string Hotkey { get; set; } = "Ctrl+Alt+R";

    public List<SourceConfig> Sources { get; set; } = new();

    public List<string> ExcludedExtensions { get; set; } = new();
    public List<string> ExcludedPaths { get; set; } = new();
    public List<string> ExcludedKeywords { get; set; } = new();
    public List<string> WhitelistedPaths { get; set; } = new();
    public Dictionary<string, List<string>> FileTypeGroups { get; set; } = new();
}
```

---

## 9. 推荐代码结构

```text
Recents/
  Recents.sln
  src/
    Recents.App/
      App.xaml
      App.xaml.cs
      app.manifest                  // PerMonitorV2 + longPathAware
      MainWindow.xaml
      MainWindow.xaml.cs
      SettingsWindow.xaml
      SettingsWindow.xaml.cs
      Views/
      ViewModels/
        MainViewModel.cs
        SettingsViewModel.cs
        RecentItemViewModel.cs
        SourcesViewModel.cs
      Models/
        RecentItem.cs
        SourceConfig.cs
        AppSettings.cs
      Services/
        RecentIndexService.cs        // SQLite 索引 + 融合
        Sources/
          IRecentSource.cs
          KnownFolderWatchSource.cs  // L1 默认已知文件夹
          UserFolderWatchSource.cs   // L1 自定义本地目录
          UncFolderWatchSource.cs    // L1 UNC 路径
          RecentLnkSource.cs         // L1 Recent .lnk
          JumpListSource.cs          // L2
          OfficeMruSource.cs         // L2
          OpenSavePidlMruSource.cs   // L3
          CustomDestinationsSource.cs// L3
        ShellLinkResolver.cs
        FileIconService.cs
        HotkeyService.cs
        TrayService.cs
        DragDropService.cs
        SettingsService.cs
        StartupService.cs
        FileActionService.cs
        ExistsProbeService.cs        // 异步 + 超时
        SingleInstanceService.cs
      Utils/
        Debouncer.cs
        PathNormalizer.cs
        PathMatcher.cs
        FileTypeClassifier.cs
        CloudPlaceholderDetector.cs
      Resources/
        Icons/
```

架构要求：
- MVVM。
- UI 层不直接扫描文件，不直接读注册表，不直接访问 SQLite。
- 所有数据源实现 `IRecentSource { Task InitialScanAsync(); IObservable<RecentChange> Watch(); }`。
- 耗时操作必须异步。
- 主窗口呼出时只读内存视图，不阻塞 UI 线程。

---

## 10. 性能要求

目标：

- 快捷键呼出窗口：< 100ms。
- 初次完整扫描（默认 6 个已知文件夹 + Recent + Jump List）：可接受 1–3 秒，必须后台执行；UI 立即用上次缓存渲染。
- 后续呼出：使用 SQLite + 内存视图，不重新全量扫描。
- 搜索过滤 1 万条：无明显输入卡顿。
- 图标加载：异步，不阻塞列表显示。
- 内存占用：常驻状态 < 150MB，不含 SQLite 页缓存。

性能原则：
- 呼出窗口只读内存列表。
- 不在 UI 线程解析 .lnk、读注册表、读图标、查 `File.Exists`。
- 不在每次搜索时访问磁盘。
- 不在每次呼出时重新扫描。
- 列表渲染走虚拟化。

---

## 11. 错误处理

必须处理：

- 已知文件夹路径解析失败（`SHGetKnownFolderPath` 报错）
- 自定义目录不存在 / 权限拒绝
- UNC 路径不可达
- `FileSystemWatcher` 缓冲区溢出（`Error` 事件）
- .lnk 解析失败
- Jump List OLECF 解析失败
- 注册表 MRU 读取失败
- 目标文件不存在
- 目标路径无权限访问
- 图标提取失败
- 快捷键注册失败
- 配置文件损坏
- SQLite 索引损坏
- 开机自启设置失败
- OneDrive 占位符触发拉取（应预防而非处理）

处理原则：
- 单个文件失败不影响整个列表。
- 单个数据源失败不影响其他数据源。
- 配置损坏：自动备份坏文件并重建默认配置。
- SQLite 损坏：自动重命名并重建。
- 快捷键注册失败：托盘气泡 + 设置页双重提示。

---

## 12. 日志

日志路径：

```text
%LOCALAPPDATA%\Recents\logs\recents.log
```

记录内容：
- 应用启动 / 退出
- 单实例检测结果
- 快捷键注册状态（含候选回退）
- 各数据源初始化、扫描计数、错误
- .lnk / Jump List 解析失败统计（不记内容）
- SQLite 读写失败
- 设置读写失败
- 文件操作失败

隐私：
- 默认不记完整路径，对路径做"盘符 + 截断 + 哈希"处理（如 `D:\…\<sha1:8>\file.docx`）。
- 仅当用户在设置中开启 "Verbose logging" 才记完整路径。
- 不记录文件内容。

---

## 13. 隐私与安全

要求：
- 所有数据仅保存在本机。
- 不联网（除用户主动添加 UNC 路径，访问目标网络）。
- 不上传文件路径。
- 不读取文件内容。
- 不删除原始文件。
- "删除 Recent 记录"只删除 `%APPDATA%\Microsoft\Windows\Recent` 中的 .lnk，不动 Jump List、不动 Office MRU、不动注册表 MRU。
- 不识别 OneDrive / Google Drive 登录态，不读其元数据；用户挂载到本地的文件夹按普通文件夹处理（但跳过占位符的耗资源调用，见 §6.19）。
- 单实例（命名 Mutex `Global\Recents.SingleInstance`），第二次启动转发显示窗口请求并退出。

---

## 14. 首版验收清单

### 14.1 基础功能

- [ ] 应用启动后出现在系统托盘，且 Mutex 防双开。
- [ ] `Ctrl + Alt + R` 可以呼出 / 隐藏窗口。
- [ ] 默认热键被占用时，自动回退候选并提示。
- [ ] 窗口打开后搜索框自动聚焦。
- [ ] 启动后所有 L1 数据源均开始监听（Downloads/Desktop/Documents/Pictures/Videos/Music + Recent + Jump List + Office MRU）。
- [ ] 文件列表按最近时间倒序展示。
- [ ] 多源命中同一路径时，仅展示一条，且 Tooltip 列出所有来源。
- [ ] 显示文件图标、名称、路径、时间、大小。

### 14.2 数据源覆盖（关键）

- [ ] **从浏览器下载文件到 Downloads，2 秒内出现在列表顶部**（这是 Recent 文件夹方案漏掉的核心场景）。
- [ ] 在 VS Code 中打开 / 保存文件，相应文件出现在列表（来源标 KnownFolderWatch 或 RecentLnk 之一即可）。
- [ ] 从微信 / 飞书接收文件到默认目录，相应文件出现在列表。
- [ ] 在 Office 中打开文件，相应文件出现在列表（来源含 OfficeMru）。
- [ ] 添加 `\\10.0.0.100\claw\` 为自定义 UNC 来源后，远端写入的文件能进列表。
- [ ] UNC 网络断开时，已索引的远端文件标 Unknown 灰显，UI 不卡顿；网络恢复后自动重新可达。

### 14.3 文件操作

- [ ] 双击文件可用默认程序打开。
- [ ] 右键可打开文件 / 打开方式 / 打开所在位置 / 复制完整路径。
- [ ] 拖拽到微信、飞书、Outlook、Edge、Explorer 五处目标 App 时均传递原始文件路径。
- [ ] Missing 文件灰显，禁止拖拽。

### 14.4 搜索与筛选

- [ ] 输入文件名子串可筛选。
- [ ] 输入 `.docx` 或 `pdf`（首字符为 `.`）按扩展名精确筛选。
- [ ] 输入路径片段可筛选。
- [ ] 左侧文件类型筛选可用。
- [ ] 搜索和类型筛选可叠加。

### 14.5 设置

- [ ] 可设置开机自启。
- [ ] 可设置最大显示数量。
- [ ] 可设置是否显示 Missing 文件。
- [ ] 可在 **Sources** 启用 / 禁用每个默认已知文件夹。
- [ ] 可在 **Sources** 添加 / 移除自定义本地目录。
- [ ] 可在 **Sources** 添加 / 移除 UNC 路径。
- [ ] 可设置排除扩展名 / 路径 / 关键词。
- [ ] 可设置白名单路径。
- [ ] 可重建索引（SQLite）。

### 14.6 性能

- [ ] 快捷键呼出 < 100ms。
- [ ] 搜索输入无明显卡顿（1 万条规模）。
- [ ] 图标异步加载不阻塞 UI。
- [ ] FileSystemWatcher 缓冲区溢出后能自动恢复（人为触发 64KB 内大量并发写入测试）。
- [ ] OneDrive 占位符不被工具触发联网下载（关键回归点）。

---

## 15. 开发优先级

### P0（第一阶段必做）

- WPF .NET 8 项目骨架 + app.manifest（PerMonitorV2 + longPathAware）
- 单实例 Mutex
- 托盘常驻
- 全局快捷键（含候选回退）
- 主窗口暗色 UI
- **`IRecentSource` 抽象 + `RecentIndexService` SQLite 索引融合**
- **L1 数据源：KnownFolderWatchSource（Downloads / Desktop / Documents / Pictures / Videos / Music）**
- L1 数据源：RecentLnkSource（含纯托管 .lnk 解析）
- 主窗口列表（虚拟化）
- 双击打开
- 搜索过滤
- 拖拽原始文件（FileDrop + Shell IDList Array）

### P1

- 右键菜单
- 文件图标（占位 + 异步真实图标 + 缓存）
- 文件类型筛选
- L1 数据源：UserFolderWatchSource（自定义本地目录）
- L1 数据源：UncFolderWatchSource（UNC 路径，含断网重连）
- L2 数据源：JumpListSource、OfficeMruSource
- 设置页（含 Sources Tab）
- 开机自启

### P2

- 收藏固定
- 最近文件夹（Quick Access、TypedPaths）
- L3 数据源：OpenSavePidlMruSource、CustomDestinationsSource、RecentDocs
- 黑白名单路径
- 排除规则 UI
- 快速预览信息
- 键盘快捷操作完善
- Verbose logging 开关

---

## 16. 编码与质量要求

编码时必须遵守：

1. 不一次性生成过度复杂的完整系统；分阶段保证可编译可运行。
2. 每完成一个数据源，必须能在主列表中看到其贡献的条目。
3. 所有路径、快捷键、显示数量、监听目录都必须可配置。
4. 所有文件操作必须作用于原始文件，不作用于 .lnk。
5. 所有耗时任务必须异步，使用 `async/await` + `IProgress<T>` 通知 UI。
6. UI 不得因扫描、解析、图标加载、`File.Exists` 而卡顿。
7. 不联网（用户主动添加的 UNC 路径除外）。
8. 不删除原始文件。
9. 不集成任何云盘 SDK、不读取浏览器/聊天工具的私有数据库。
10. 代码结构按 Services / ViewModels / Models / Views 分层，数据源都放 `Services/Sources/`。
11. 路径处理统一走 `PathNormalizer`，禁止散落 `ToLower()`、`TrimEnd('\\')` 等就地处理。
12. 所有外部 IO 必须可单元测试（数据源接口注入、文件系统抽象）。

---

## 17. 第一阶段实现范围

第一阶段（P0）必须实现：

- 创建 WPF .NET 8 项目，app.manifest 配置 DPI 与长路径
- 单实例 Mutex
- 主窗口暗色 UI（含搜索框、列表虚拟化）
- 托盘常驻（显示 / 退出 / 重建索引）
- `Ctrl + Alt + R` 呼出 / 隐藏 + 候选回退
- `IRecentSource` 抽象 + SQLite 索引融合
- **`KnownFolderWatchSource` 监听 Downloads / Desktop / Documents / Pictures / Videos / Music**
- `RecentLnkSource` 解析 `%APPDATA%\Microsoft\Windows\Recent` 的 .lnk
- 显示最近 200 个文件，时间倒序
- 搜索文件名 / 扩展名 / 路径
- 双击打开原文件
- 拖拽原文件（FileDrop + Shell IDList Array）
- 右键打开 / 打开所在位置 / 复制路径

第一阶段**不做**：设置页 UI、收藏、白名单 / 黑名单 UI、开机自启、Jump List、Office MRU、UNC 自定义来源、图标真实缩略图（仅占位图标）。

> 第一阶段就必须解决"下载文件不出现"问题：`KnownFolderWatchSource` 是核心，**不允许把 Downloads 监听挪到后续阶段**。

---

## 18. 已知不支持场景（首版）

明确列出，避免反复争议：

- UWP / MSIX 沙盒应用打开记录（如 Microsoft Photos、Mail App）。
- OneDrive / SharePoint / Google Drive 的云端文件元数据，未本地缓存的占位符不读取。
- 浏览器内"下载历史"列表中**未实际写入磁盘**的条目。
- 加密容器（VeraCrypt / BitLocker To Go 未挂载）内的文件。
- 虚拟驱动器（Dokan / WinFsp）若不支持 `FileSystemWatcher` 通知，依赖定时增量扫描。
- 完全离线编辑且通过第三方同步工具回写、不修改 LastWriteTime 的文件。
